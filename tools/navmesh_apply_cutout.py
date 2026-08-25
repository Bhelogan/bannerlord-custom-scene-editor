#!/usr/bin/env python3
"""Apply one validated CSC cutout to a separately named NMG9 copy.

This experimental writer never overwrites its input or an existing destination. It
retains all original vertices/edges, removes only the intersecting face patch, adds
four hole vertices plus a triangulated repair ring, serializes a new RNM1/NMG9 file,
then decodes and structurally validates the result before leaving it on disk.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import navmesh_cutout
import navmesh_inspect


def apply_cutout(
    source: Path,
    output: Path,
    corners_xyz: tuple[tuple[float, float, float], ...],
) -> dict:
    if output.resolve() == source.resolve():
        raise navmesh_inspect.NavMeshFormatError("output must not overwrite the input")
    if output.exists():
        raise navmesh_inspect.NavMeshFormatError("output already exists")
    if len(corners_xyz) != 4 or any(
        not math.isfinite(value) for corner in corners_xyz for value in corner
    ):
        raise navmesh_inspect.NavMeshFormatError("four finite XYZ corners are required")

    corners_xy = tuple((corner[0], corner[1]) for corner in corners_xyz)
    plan = navmesh_cutout.build_plan(source, corners_xy)
    if not plan.fully_contained or not plan.repairable_single_loop:
        raise navmesh_inspect.NavMeshFormatError("cutout patch is not a contained single-loop repair")
    if len(plan.affected_groups) != 1:
        raise navmesh_inspect.NavMeshFormatError("cutout crosses multiple face groups")

    original, raw, wrapper = navmesh_inspect.read_raw(source)
    source_summary = navmesh_inspect.summarize(source)
    vertices, edges, face_count, face_offset = navmesh_inspect._leading_arrays(raw)
    faces, tail = navmesh_inspect._faces(raw, face_count, face_offset)
    if any(edge[0] != -1 or edge[3] != -1 or edge[4] != -1 or edge[5] != 0 for edge in edges):
        raise navmesh_inspect.NavMeshFormatError("unsupported nonstandard NMG9 edge metadata")

    affected_set = set(plan.affected_faces)

    # Faces whose only metadata difference is the sign of the fifth integer (the Modding Kit
    # uses -15 for rebuilt faces while original faces use +15) are compatible. Collapse them
    # into one canonical tuple so a second cutout can be applied to a mesh that already has
    # repair faces from a prior cutout.
    def _canonical_metadata(md: tuple[int, int, int, int, int]) -> tuple[int, int, int, int, int]:
        return (md[0], md[1], md[2], md[3], abs(md[4]))

    affected_canonical = {_canonical_metadata(faces[index].metadata) for index in affected_set}
    affected_directions = {faces[index].direction for index in affected_set}
    if len(affected_canonical) != 1 or len(affected_directions) != 1:
        raise navmesh_inspect.NavMeshFormatError("affected faces do not share compatible metadata")
    metadata = list(next(iter(affected_canonical)))
    metadata[4] = -abs(metadata[4]) if metadata[4] else metadata[4]
    direction = next(iter(affected_directions))

    new_vertices = list(vertices) + list(corners_xyz)
    new_edges = list(edges)
    edge_lookup = {
        (min(edge[1], edge[2]), max(edge[1], edge[2])): index
        for index, edge in enumerate(new_edges)
    }
    new_faces = [face for index, face in enumerate(faces) if index not in affected_set]
    for triangle in plan.planned_triangles:
        points = [(new_vertices[index][0], new_vertices[index][1]) for index in triangle]
        area = navmesh_cutout._cross(points[0], points[1], points[2]) / 2.0
        if area <= 1e-4:
            raise navmesh_inspect.NavMeshFormatError(f"degenerate or inverted repair triangle {triangle}")
        triangle_edges = []
        for index, start in enumerate(triangle):
            end = triangle[(index + 1) % 3]
            key = (min(start, end), max(start, end))
            edge_index = edge_lookup.get(key)
            if edge_index is None:
                edge_index = len(new_edges)
                edge_lookup[key] = edge_index
                new_edges.append((-1, start, end, -1, -1, 0))
            triangle_edges.append(edge_index)
        new_faces.append(
            navmesh_inspect.NmgFace(
                vertices=triangle,
                edges=tuple(triangle_edges),
                metadata=tuple(metadata),
                direction=direction,
            )
        )

    # A valid ring must leave the footprint interior genuinely unmeshed, not merely
    # produce a topologically consistent but overlapping set of faces.
    new_polygons = [
        tuple((new_vertices[index][0], new_vertices[index][1]) for index in face.vertices)
        for face in new_faces
    ]
    for row in range(1, 5):
        v = row / 5.0
        for column in range(1, 5):
            u = column / 5.0
            left = (
                corners_xy[0][0] + (corners_xy[3][0] - corners_xy[0][0]) * v,
                corners_xy[0][1] + (corners_xy[3][1] - corners_xy[0][1]) * v,
            )
            right = (
                corners_xy[1][0] + (corners_xy[2][0] - corners_xy[1][0]) * v,
                corners_xy[1][1] + (corners_xy[2][1] - corners_xy[1][1]) * v,
            )
            sample = (left[0] + (right[0] - left[0]) * u, left[1] + (right[1] - left[1]) * u)
            if any(navmesh_cutout._point_in_polygon(sample, polygon) for polygon in new_polygons):
                raise navmesh_inspect.NavMeshFormatError(
                    f"repair face still covers cutout interior sample {sample}"
                )

    # Always emit the current format. NMG8 differs only by its missing sixth edge integer; the
    # current Modding Kit performs the same zero-filled NMG8 -> NMG9 upgrade when navigation is saved.
    rebuilt_raw = navmesh_inspect.serialize_raw(b"NMG9", new_vertices, new_edges, new_faces, tail)
    # Reparse before writing, then independently decode and summarize after writing.
    check_vertices, check_edges, check_face_count, check_face_offset = navmesh_inspect._leading_arrays(rebuilt_raw)
    check_faces, check_tail = navmesh_inspect._faces(rebuilt_raw, check_face_count, check_face_offset)
    if navmesh_inspect.serialize_raw(b"NMG9", check_vertices, check_edges, check_faces, check_tail) != rebuilt_raw:
        raise navmesh_inspect.NavMeshFormatError("in-memory repair failed lossless validation")

    encoded = navmesh_inspect.encode_container(rebuilt_raw, original, wrapper)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(encoded)
    try:
        summary = navmesh_inspect.summarize(output)
        if (
            summary.invalid_edge_endpoints
            or summary.self_edges
            or summary.duplicate_edges
            or summary.invalid_face_references
            or summary.invalid_face_boundaries
            or not summary.finite_vertices
            or summary.connected_face_components != source_summary.connected_face_components
        ):
            raise navmesh_inspect.NavMeshFormatError("written repair failed structural validation")
    except Exception:
        output.unlink(missing_ok=True)
        raise

    return {
        "source": str(source.resolve()),
        "output": str(output.resolve()),
        "affected_faces_removed": len(affected_set),
        "repair_faces_added": len(plan.planned_triangles),
        "vertices_before": len(vertices),
        "vertices_after": len(new_vertices),
        "edges_before": len(edges),
        "edges_after": len(new_edges),
        "faces_before": len(faces),
        "faces_after": len(new_faces),
        "connected_face_components": summary.connected_face_components,
        "retained_unused_vertices": summary.unused_vertices,
        "retained_unused_edges": summary.unused_edges,
        "hole_corners_xyz": corners_xyz,
        "triangulation_area_error": plan.triangulation_area_error,
        "output_sha256": summary.compressed_sha256,
        "raw_sha256": summary.raw_sha256,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("navmesh", type=Path)
    parser.add_argument("output", type=Path)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--manifest", type=Path)
    source.add_argument("--corners-xyz", nargs=12, type=float)
    parser.add_argument("--cutout-index", type=int, default=0)
    parser.add_argument(
        "--use-manifest-z",
        action="store_true",
        help="use authored object-base Z instead of projecting XY onto the existing navmesh",
    )
    args = parser.parse_args()
    try:
        if args.manifest:
            authored = navmesh_cutout.manifest_corner_xyz(args.manifest, args.cutout_index)
            if args.use_manifest_z:
                corners_xyz = authored
            else:
                xy = tuple((corner[0], corner[1]) for corner in authored)
                corners_xyz = navmesh_cutout.build_plan(args.navmesh, xy).projected_corners_xyz
        else:
            if args.use_manifest_z:
                raise navmesh_inspect.NavMeshFormatError("--use-manifest-z requires --manifest")
            corners_xyz = tuple(
                (args.corners_xyz[index], args.corners_xyz[index + 1], args.corners_xyz[index + 2])
                for index in range(0, 12, 3)
            )
        print(json.dumps(apply_cutout(args.navmesh, args.output, corners_xyz), indent=2))
    except (OSError, ValueError, json.JSONDecodeError, navmesh_inspect.NavMeshFormatError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
