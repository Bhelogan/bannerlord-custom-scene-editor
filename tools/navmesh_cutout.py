#!/usr/bin/env python3
"""Plan an NMG8/NMG9 rectangular cutout without modifying a navmesh.

The planner resolves a CSC .navcut.json footprint (or explicit XY corners) against
the decoded polygon mesh, finds the complete intersecting face patch, and validates
that its outer boundary forms one repairable loop. Applying topology is intentionally
separate; this command is safe and read-only.
"""

from __future__ import annotations

import argparse
import json
import math
from dataclasses import asdict, dataclass
from pathlib import Path

import navmesh_inspect


Point2 = tuple[float, float]


@dataclass(frozen=True)
class CutoutPlan:
    navmesh: str
    corners_xy: tuple[Point2, ...]
    projected_corners_xyz: tuple[tuple[float, float, float], ...]
    affected_faces: tuple[int, ...]
    affected_groups: tuple[int, ...]
    outer_boundary_vertices: tuple[int, ...]
    internal_vertices: tuple[int, ...]
    fully_contained: bool
    repairable_single_loop: bool
    planned_triangles: tuple[tuple[int, int, int], ...]
    triangulation_area_error: float


def _cross(a: Point2, b: Point2, c: Point2) -> float:
    return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])


def _on_segment(a: Point2, b: Point2, p: Point2, epsilon: float = 1e-5) -> bool:
    return (
        abs(_cross(a, b, p)) <= epsilon
        and min(a[0], b[0]) - epsilon <= p[0] <= max(a[0], b[0]) + epsilon
        and min(a[1], b[1]) - epsilon <= p[1] <= max(a[1], b[1]) + epsilon
    )


def _segments_intersect(a: Point2, b: Point2, c: Point2, d: Point2) -> bool:
    values = (_cross(a, b, c), _cross(a, b, d), _cross(c, d, a), _cross(c, d, b))
    if any(abs(value) <= 1e-5 for value in values):
        return any(
            _on_segment(x, y, p)
            for x, y, p in ((a, b, c), (a, b, d), (c, d, a), (c, d, b))
        )
    return (values[0] > 0) != (values[1] > 0) and (values[2] > 0) != (values[3] > 0)


def _point_in_polygon(point: Point2, polygon: tuple[Point2, ...]) -> bool:
    inside = False
    previous = polygon[-1]
    for current in polygon:
        if _on_segment(previous, current, point):
            return True
        if (current[1] > point[1]) != (previous[1] > point[1]):
            crossing_x = (previous[0] - current[0]) * (point[1] - current[1]) / (
                previous[1] - current[1]
            ) + current[0]
            if point[0] < crossing_x:
                inside = not inside
        previous = current
    return inside


def _polygons_intersect(left: tuple[Point2, ...], right: tuple[Point2, ...]) -> bool:
    if any(_point_in_polygon(point, right) for point in left):
        return True
    if any(_point_in_polygon(point, left) for point in right):
        return True
    return any(
        _segments_intersect(left[i], left[(i + 1) % len(left)], right[j], right[(j + 1) % len(right)])
        for i in range(len(left))
        for j in range(len(right))
    )


def _boundary_loop(face_indices, faces, edges) -> tuple[tuple[int, ...], bool]:
    counts: dict[int, int] = {}
    for face_index in face_indices:
        for edge_index in faces[face_index].edges:
            counts[edge_index] = counts.get(edge_index, 0) + 1
    boundary_edges = [edge_index for edge_index, count in counts.items() if count == 1]
    adjacency: dict[int, list[int]] = {}
    for edge_index in boundary_edges:
        edge = edges[edge_index]
        adjacency.setdefault(edge[1], []).append(edge[2])
        adjacency.setdefault(edge[2], []).append(edge[1])
    if not adjacency or any(len(neighbors) != 2 for neighbors in adjacency.values()):
        return tuple(sorted(adjacency)), False
    start = min(adjacency)
    loop = [start]
    previous = -1
    current = start
    while True:
        choices = adjacency[current]
        following = choices[0] if choices[0] != previous else choices[1]
        if following == start:
            break
        if following in loop:
            return tuple(loop), False
        loop.append(following)
        previous, current = current, following
    return tuple(loop), len(loop) == len(adjacency)


def _signed_area(indices: list[int], points: dict[int, Point2]) -> float:
    return sum(
        points[indices[index]][0] * points[indices[(index + 1) % len(indices)]][1]
        - points[indices[(index + 1) % len(indices)]][0] * points[indices[index]][1]
        for index in range(len(indices))
    ) / 2.0


def _strictly_inside_triangle(point: Point2, a: Point2, b: Point2, c: Point2) -> bool:
    values = (_cross(a, b, point), _cross(b, c, point), _cross(c, a, point))
    return (all(value > 1e-6 for value in values) or all(value < -1e-6 for value in values))


def _visible_bridge(outer: list[int], hole: list[int], points: dict[int, Point2]) -> tuple[int, int]:
    rings = (outer, hole)
    candidates = []
    for outer_position, outer_vertex in enumerate(outer):
        for hole_position, hole_vertex in enumerate(hole):
            a, b = points[outer_vertex], points[hole_vertex]
            blocked = False
            for ring in rings:
                for index in range(len(ring)):
                    c_vertex, d_vertex = ring[index], ring[(index + 1) % len(ring)]
                    if outer_vertex in (c_vertex, d_vertex) or hole_vertex in (c_vertex, d_vertex):
                        continue
                    if _segments_intersect(a, b, points[c_vertex], points[d_vertex]):
                        blocked = True
                        break
                if blocked:
                    break
            midpoint = ((a[0] + b[0]) / 2.0, (a[1] + b[1]) / 2.0)
            outer_polygon = tuple(points[index] for index in outer)
            hole_polygon = tuple(points[index] for index in hole)
            if not blocked and _point_in_polygon(midpoint, outer_polygon) and not _point_in_polygon(midpoint, hole_polygon):
                distance = (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2
                candidates.append((distance, outer_position, hole_position))
    if not candidates:
        raise navmesh_inspect.NavMeshFormatError("no visible bridge between cutout and repair boundary")
    _, outer_position, hole_position = min(candidates)
    return outer_position, hole_position


def _triangulate_with_hole(
    outer: tuple[int, ...],
    hole: tuple[int, ...],
    points: dict[int, Point2],
) -> tuple[tuple[tuple[int, int, int], ...], float]:
    outer_list = list(outer)
    hole_list = list(hole)
    if _signed_area(outer_list, points) < 0:
        outer_list.reverse()
    if _signed_area(hole_list, points) > 0:
        hole_list.reverse()
    outer_position, hole_position = _visible_bridge(outer_list, hole_list, points)
    bridged = (
        outer_list[: outer_position + 1]
        + hole_list[hole_position:]
        + hole_list[: hole_position + 1]
        + [outer_list[outer_position]]
        + outer_list[outer_position + 1 :]
    )

    remaining = list(range(len(bridged)))
    triangles: list[tuple[int, int, int]] = []
    guard = 0
    while len(remaining) > 3:
        clipped = False
        for position in range(len(remaining)):
            previous_slot = remaining[(position - 1) % len(remaining)]
            current_slot = remaining[position]
            next_slot = remaining[(position + 1) % len(remaining)]
            triangle = (bridged[previous_slot], bridged[current_slot], bridged[next_slot])
            a, b, c = (points[index] for index in triangle)
            if len(set(triangle)) < 3 or _cross(a, b, c) <= 1e-6:
                continue
            if any(
                slot not in (previous_slot, current_slot, next_slot)
                and bridged[slot] not in triangle
                and _strictly_inside_triangle(points[bridged[slot]], a, b, c)
                for slot in remaining
            ):
                continue
            triangles.append(triangle)
            remaining.pop(position)
            clipped = True
            break
        guard += 1
        if not clipped or guard > len(bridged) * len(bridged):
            raise navmesh_inspect.NavMeshFormatError("cutout repair polygon could not be triangulated safely")
    final = tuple(bridged[slot] for slot in remaining)
    if len(set(final)) != 3:
        raise navmesh_inspect.NavMeshFormatError("cutout triangulation ended with a degenerate face")
    triangles.append(final)

    triangle_area = sum(abs(_cross(points[a], points[b], points[c])) / 2.0 for a, b, c in triangles)
    expected_area = abs(_signed_area(outer_list, points)) - abs(_signed_area(hole_list, points))
    error = abs(triangle_area - expected_area)
    if error > max(1e-3, expected_area * 1e-5):
        raise navmesh_inspect.NavMeshFormatError(
            f"triangulation area mismatch: {triangle_area:.6f} vs {expected_area:.6f}"
        )
    return tuple(triangles), error


def _interpolate_z(point: Point2, polygon, vertices) -> float:
    p0 = vertices[polygon[0]]
    for index in range(1, len(polygon) - 1):
        a, b, c = p0, vertices[polygon[index]], vertices[polygon[index + 1]]
        denominator = _cross((a[0], a[1]), (b[0], b[1]), (c[0], c[1]))
        if abs(denominator) < 1e-8:
            continue
        wa = _cross(point, (b[0], b[1]), (c[0], c[1])) / denominator
        wb = _cross(point, (c[0], c[1]), (a[0], a[1])) / denominator
        wc = 1.0 - wa - wb
        if min(wa, wb, wc) >= -1e-5:
            return wa * a[2] + wb * b[2] + wc * c[2]
    raise navmesh_inspect.NavMeshFormatError(f"cannot project cutout corner {point} onto affected faces")


def build_plan(path: Path, corners: tuple[Point2, ...]) -> CutoutPlan:
    if len(corners) != 4 or any(not math.isfinite(value) for corner in corners for value in corner):
        raise navmesh_inspect.NavMeshFormatError("cutout requires four finite XY corners")
    _, raw, _ = navmesh_inspect.read_raw(path)
    if raw[:4] not in (b"NMG8", b"NMG9"):
        raise navmesh_inspect.NavMeshFormatError("cutout planning requires NMG8 or NMG9")
    vertices, edges, face_count, face_offset = navmesh_inspect._leading_arrays(raw)
    faces, _ = navmesh_inspect._faces(raw, face_count, face_offset)

    face_polygons = [tuple((vertices[index][0], vertices[index][1]) for index in face.vertices) for face in faces]
    affected = tuple(index for index, polygon in enumerate(face_polygons) if _polygons_intersect(polygon, corners))
    if not affected:
        raise navmesh_inspect.NavMeshFormatError("cutout does not intersect any face")
    boundary, repairable = _boundary_loop(affected, faces, edges)
    used = {vertex for face_index in affected for vertex in faces[face_index].vertices}
    internal = tuple(sorted(used - set(boundary)))

    projected = []
    fully_contained = True
    for corner in corners:
        containing = [index for index in affected if _point_in_polygon(corner, face_polygons[index])]
        if not containing:
            fully_contained = False
            projected.append((corner[0], corner[1], math.nan))
            continue
        z = _interpolate_z(corner, faces[containing[0]].vertices, vertices)
        projected.append((corner[0], corner[1], z))

    groups = tuple(sorted({faces[index].metadata[2] for index in affected}))
    planned_triangles: tuple[tuple[int, int, int], ...] = ()
    triangulation_area_error = math.nan
    if repairable and fully_contained:
        first_new_vertex = len(vertices)
        hole_indices = tuple(first_new_vertex + index for index in range(4))
        point_lookup = {index: (vertex[0], vertex[1]) for index, vertex in enumerate(vertices)}
        point_lookup.update({index: corner for index, corner in zip(hole_indices, corners)})
        planned_triangles, triangulation_area_error = _triangulate_with_hole(
            boundary, hole_indices, point_lookup
        )
    return CutoutPlan(
        navmesh=str(path.resolve()),
        corners_xy=corners,
        projected_corners_xyz=tuple(projected),
        affected_faces=affected,
        affected_groups=groups,
        outer_boundary_vertices=boundary,
        internal_vertices=internal,
        fully_contained=fully_contained,
        repairable_single_loop=repairable,
        planned_triangles=planned_triangles,
        triangulation_area_error=triangulation_area_error,
    )


def _manifest_corners(path: Path, cutout_index: int) -> tuple[Point2, ...]:
    return tuple((x, y) for x, y, _ in manifest_corner_xyz(path, cutout_index))


def manifest_corner_xyz(path: Path, cutout_index: int) -> tuple[tuple[float, float, float], ...]:
    document = json.loads(path.read_text(encoding="utf-8-sig"))
    if document.get("Format") not in ("CustomSceneCreator.NavMeshPlan/1", "CustomSceneCreator.NavMeshPlan/2"):
        raise navmesh_inspect.NavMeshFormatError("unsupported CSC navmesh-plan format")
    cutouts = document.get("Cutouts") or []
    try:
        values = cutouts[cutout_index]["Corners"]
    except (IndexError, KeyError, TypeError) as error:
        raise navmesh_inspect.NavMeshFormatError(f"manifest cutout {cutout_index} is unavailable") from error
    if len(values) != 12:
        raise navmesh_inspect.NavMeshFormatError("manifest cutout must contain twelve XYZ values")
    return tuple(
        (float(values[index]), float(values[index + 1]), float(values[index + 2]))
        for index in range(0, 12, 3)
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("navmesh", type=Path)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--manifest", type=Path)
    source.add_argument("--corners", nargs=8, type=float, metavar=("X0", "Y0", "X1", "Y1", "X2", "Y2", "X3", "Y3"))
    parser.add_argument("--cutout-index", type=int, default=0)
    args = parser.parse_args()
    try:
        corners = (
            _manifest_corners(args.manifest, args.cutout_index)
            if args.manifest
            else tuple((args.corners[index], args.corners[index + 1]) for index in range(0, 8, 2))
        )
        print(json.dumps(asdict(build_plan(args.navmesh, corners)), indent=2))
    except (OSError, ValueError, json.JSONDecodeError, navmesh_inspect.NavMeshFormatError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
