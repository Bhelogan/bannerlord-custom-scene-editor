#!/usr/bin/env python3
"""Plan saved navmesh-required areas without changing a mesh."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import navmesh_cutout
import navmesh_inspect


def _xyz(values: object) -> tuple[tuple[float, float, float], ...]:
    if not isinstance(values, list) or len(values) < 24 or len(values) % 3:
        return ()
    points = tuple(
        (float(values[index]), float(values[index + 1]), float(values[index + 2]))
        for index in range(0, len(values), 3)
    )
    return points if all(math.isfinite(value) for point in points for value in point) else ()


def _closest_on_segment(
    point: tuple[float, float],
    start: tuple[float, float, float],
    end: tuple[float, float, float],
) -> tuple[float, tuple[float, float, float]]:
    dx, dy = end[0] - start[0], end[1] - start[1]
    length2 = dx * dx + dy * dy
    t = 0.0 if length2 <= 1e-12 else max(
        0.0, min(1.0, ((point[0] - start[0]) * dx + (point[1] - start[1]) * dy) / length2)
    )
    closest = (
        start[0] + dx * t,
        start[1] + dy * t,
        start[2] + (end[2] - start[2]) * t,
    )
    return math.hypot(point[0] - closest[0], point[1] - closest[1]), closest


def _clusters(requirements: list[dict]) -> list[list[int]]:
    adjacency = [[] for _ in requirements]
    for left in range(len(requirements)):
        a = requirements[left]
        for right in range(left + 1, len(requirements)):
            b = requirements[right]
            distance = math.hypot(a["center"][0] - b["center"][0], a["center"][1] - b["center"][1])
            if distance <= a["radius"] + b["radius"]:
                adjacency[left].append(right)
                adjacency[right].append(left)
    result: list[list[int]] = []
    seen: set[int] = set()
    for start in range(len(requirements)):
        if start in seen:
            continue
        stack, cluster = [start], []
        seen.add(start)
        while stack:
            current = stack.pop()
            cluster.append(current)
            for neighbor in adjacency[current]:
                if neighbor not in seen:
                    seen.add(neighbor)
                    stack.append(neighbor)
        result.append(sorted(cluster))
    return result


def build_addition_plan(navmesh: Path, manifest_path: Path) -> dict:
    document = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if document.get("Format") != "CustomSceneCreator.NavMeshPlan/2":
        raise navmesh_inspect.NavMeshFormatError("addition planning requires a version-2 CSC manifest")
    saved = document.get("RequiredAreas")
    if not isinstance(saved, list) or not saved:
        raise navmesh_inspect.NavMeshFormatError("manifest contains no navmesh-required areas")

    _, raw, _ = navmesh_inspect.read_raw(navmesh)
    vertices, edges, face_count, face_offset = navmesh_inspect._leading_arrays(raw)
    faces, _ = navmesh_inspect._faces(raw, face_count, face_offset)
    edge_faces: list[list[int]] = [[] for _ in edges]
    for face_index, face in enumerate(faces):
        for edge_index in face.edges:
            edge_faces[edge_index].append(face_index)
    boundary_edges = [index for index, owners in enumerate(edge_faces) if len(owners) == 1]
    if not boundary_edges:
        raise navmesh_inspect.NavMeshFormatError("navmesh has no open boundary edge to extend")

    requirements: list[dict] = []
    for index, requirement in enumerate(saved):
        pos = requirement.get("Pos")
        if not isinstance(pos, list) or len(pos) < 3:
            raise navmesh_inspect.NavMeshFormatError(f"required area {index} has no valid position")
        center = (float(pos[0]), float(pos[1]), float(pos[2]))
        radius = max(0.5, float(requirement.get("Radius", 4.0)))
        boundary = _xyz(requirement.get("Boundary"))
        probes = boundary or (center,)
        nearest: tuple[float, int, tuple[float, float, float], tuple[float, float, float]] | None = None
        for probe in probes:
            for edge_index in boundary_edges:
                edge = edges[edge_index]
                start, end = vertices[edge[1]], vertices[edge[2]]
                distance, closest = _closest_on_segment((probe[0], probe[1]), start, end)
                candidate = (distance, edge_index, probe, closest)
                if nearest is None or candidate[0] < nearest[0]:
                    nearest = candidate
        assert nearest is not None
        distance, edge_index, probe, closest = nearest
        owner = edge_faces[edge_index][0]
        polygon = tuple((vertices[v][0], vertices[v][1]) for v in faces[owner].vertices)
        center_is_meshed = navmesh_cutout._point_in_polygon((center[0], center[1]), polygon) or any(
            navmesh_cutout._point_in_polygon(
                (center[0], center[1]),
                tuple((vertices[v][0], vertices[v][1]) for v in face.vertices),
            )
            for face in faces
        )
        grades = []
        if boundary:
            for at, point in enumerate(boundary):
                other = boundary[(at + 1) % len(boundary)]
                horizontal = math.hypot(other[0] - point[0], other[1] - point[1])
                if horizontal > 1e-6:
                    grades.append(abs(other[2] - point[2]) / horizontal)
        requirements.append({
            "index": index,
            "id": str(requirement.get("Id", "")),
            "label": str(requirement.get("Label", "Navmesh needed")),
            "center": center,
            "radius": radius,
            "boundary_samples": len(boundary),
            "geometry_ready": len(boundary) >= 8 and not center_is_meshed,
            "center_already_meshed": center_is_meshed,
            "nearest_open_edge": edge_index,
            "nearest_open_edge_face": owner,
            "nearest_open_edge_group": faces[owner].metadata[2],
            "horizontal_gap_from_sample": distance,
            "sample_xyz": probe,
            "connection_xyz": closest,
            "connection_z_delta": closest[2] - probe[2],
            "max_boundary_grade": max(grades, default=0.0),
            "note": "ready for topology design" if boundary else "re-mark with a terrain-sampling editor build",
        })

    clusters = _clusters(requirements)
    return {
        "navmesh": str(navmesh.resolve()),
        "manifest": str(manifest_path.resolve()),
        "scene": str(document.get("Scene", "")),
        "required_areas": len(requirements),
        "geometry_ready_areas": sum(item["geometry_ready"] for item in requirements),
        "clusters": clusters,
        "all_geometry_ready": all(item["geometry_ready"] for item in requirements),
        "requirements": requirements,
        "plan_only": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("navmesh", type=Path)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--json-out", type=Path)
    args = parser.parse_args()
    try:
        result = build_addition_plan(args.navmesh, args.manifest)
        rendered = json.dumps(result, indent=2)
        if args.json_out:
            if args.json_out.exists():
                raise navmesh_inspect.NavMeshFormatError("--json-out already exists")
            args.json_out.parent.mkdir(parents=True, exist_ok=True)
            args.json_out.write_text(rendered, encoding="utf-8")
        print(rendered)
    except (OSError, ValueError, json.JSONDecodeError, navmesh_inspect.NavMeshFormatError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
