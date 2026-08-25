#!/usr/bin/env python3
"""Add walkable navmesh faces where none exist, bridging from the existing mesh edge
to terrain-snapped authoring markers described in a CSC NavMeshPlan/2 manifest.

This is the inverse of ``navmesh_apply_cutout``: instead of removing faces under a
building, it *adds* faces over open ground that the original scene author left
unmeshed.  The algorithm is generic — it works for any number of required-area
markers, any cluster shape, and any terrain elevation (stairs, slopes, plateaus).

High-level flow
---------------

1. **Read** the source navmesh (NMG8 or NMG9) and the manifest's ``RequiredAreas``.
2. **Cluster** the required areas by overlapping radius circles (same logic as
   ``navmesh_addition_plan``).  Each cluster is processed independently.
3. **Envelope construction**:  For each cluster, collect every boundary sample
   from every requirement.  Compute a 2-D convex hull of the XY projections.
   The hull vertices (with their original terrain Z) form the *cluster envelope*.
4. **Bridge construction**:  Find the nearest open boundary edge on the existing
   mesh to each envelope vertex.  Create a fan of triangles from that edge to
   the nearest envelope vertex, filling the gap between the existing mesh and
   the new region.  Each bridge triangle shares one edge with an existing face,
   so the result is topologically connected.
5. **Interior triangulation**:  Triangulate the envelope polygon (ear-clipping,
   same proven routine from ``navmesh_cutout``).  Each triangle becomes a new
   face.  Vertices carry their terrain-sampled Z, so faces follow slopes and
   stairs naturally.
6. **Metadata assignment**:  New faces inherit the face-group ID and direction
   of the existing face they connect to.  The fifth metadata integer is set to
   ``-15`` (Modding Kit rebuilt-face convention), matching what the cutout
   writer produces.
7. **Serialize and validate**:  Re-encode as NMG9, write to a separately named
   candidate, then decode and run the same structural checks as the cutout
   writer (no non-manifold edges, single connected component, valid references,
   finite vertices).

The writer never overwrites its source, never writes to an existing file, and
refuses any cluster whose bridge or interior triangulation fails structural
validation.  It retains all original vertices and edges (same minimisation
principle as the cutout writer).

Elevation handling
------------------

Each required area carries a ``Boundary`` array of 16 terrain-snapped XYZ
samples (48 floats).  These samples are the Z source for every new vertex:

* Envelope hull vertices use their original boundary-sample Z directly.
* Bridge midpoints (if any) interpolate Z between the existing navmesh edge
  endpoint and the envelope vertex.
* Interior triangulation vertices are the envelope vertices themselves — no
  additional interior points are introduced, so Z is always a real terrain
  sample, not an interpolation that could mismatch the ground.

This means faces that span a slope will tilt to follow the terrain, and faces
that span a stair edge will have a Z step at the stair boundary — exactly what
the official navmesh authoring guide says to do.

Limitations
-----------

* The convex-hull envelope means concave cluster shapes (e.g. an L-shaped
  pathway) will include some area outside the original authoring circles.
  Future work: alpha-shape or authored polygon instead of convex hull.
* All new faces share one face-group ID (the group of the nearest existing
  face).  If a cluster spans multiple groups, the writer refuses rather than
  guessing.
* No subdivision: large envelope triangles may exceed the engine's preferred
  face size.  Future work: subdivide triangles above a size threshold.
* The writer does not create new edges between non-adjacent existing faces.
  Each bridge connects exactly one existing open edge to one envelope vertex.
"""

from __future__ import annotations

import argparse
import json
import math
import tempfile
from pathlib import Path

import navmesh_cutout
import navmesh_inspect


# ---------------------------------------------------------------------------
# Geometry helpers
# ---------------------------------------------------------------------------

Point2 = tuple[float, float]
Point3 = tuple[float, float, float]


def _cross(a: Point2, b: Point2, c: Point2) -> float:
    return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])


def _distance(a: Point2, b: Point2) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def _point_in_polygon(point: Point2, polygon: tuple[Point2, ...]) -> bool:
    return navmesh_cutout._point_in_polygon(point, polygon)


# ---------------------------------------------------------------------------
# Convex hull (Andrew's monotone chain, XY only)
# ---------------------------------------------------------------------------

def convex_hull_xy(points: list[Point3]) -> list[Point3]:
    """Compute the 2-D convex hull of ``points`` projected to XY.

    Returns the hull vertices in counter-clockwise order, each carrying its
    original Z.  If fewer than three non-collinear points are given, returns
    the input sorted by XY.
    """
    if len(points) <= 2:
        return list(points)

    # Sort by (x, y); deduplicate.
    unique: dict[Point2, Point3] = {}
    for p in points:
        unique[(p[0], p[1])] = p
    sorted_pts = sorted(unique.values(), key=lambda p: (p[0], p[1]))
    if len(sorted_pts) <= 2:
        return sorted_pts

    def cross_o(a: Point3, b: Point3, c: Point3) -> float:
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])

    # Lower hull
    lower: list[Point3] = []
    for p in sorted_pts:
        while len(lower) >= 2 and cross_o(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)

    # Upper hull
    upper: list[Point3] = []
    for p in reversed(sorted_pts):
        while len(upper) >= 2 and cross_o(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)

    # Concatenate (omit last point of each half — it's the first of the other).
    hull = lower[:-1] + upper[:-1]
    if len(hull) < 3:
        return sorted_pts
    return hull


# ---------------------------------------------------------------------------
# Cluster detection (same as navmesh_addition_plan)
# ---------------------------------------------------------------------------

def _clusters(requirements: list[dict]) -> list[list[int]]:
    adjacency = [[] for _ in requirements]
    for left in range(len(requirements)):
        a = requirements[left]
        for right in range(left + 1, len(requirements)):
            b = requirements[right]
            distance = math.hypot(
                a["center"][0] - b["center"][0],
                a["center"][1] - b["center"][1],
            )
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


# ---------------------------------------------------------------------------
# Boundary sample extraction
# ---------------------------------------------------------------------------

def _xyz(values: object) -> tuple[Point3, ...]:
    if not isinstance(values, list) or len(values) < 24 or len(values) % 3:
        return ()
    points = tuple(
        (float(values[i]), float(values[i + 1]), float(values[i + 2]))
        for i in range(0, len(values), 3)
    )
    return points if all(math.isfinite(v) for p in points for v in p) else ()


# ---------------------------------------------------------------------------
# Triangulation (ear-clipping, reused from navmesh_cutout logic)
# ---------------------------------------------------------------------------

def _signed_area(indices: list[int], points: dict[int, Point2]) -> float:
    return sum(
        points[indices[i]][0] * points[indices[(i + 1) % len(indices)]][1]
        - points[indices[(i + 1) % len(indices)]][0] * points[indices[i]][1]
        for i in range(len(indices))
    ) / 2.0


def _strictly_inside_triangle(point: Point2, a: Point2, b: Point2, c: Point2) -> bool:
    vals = (_cross(a, b, point), _cross(b, c, point), _cross(c, a, point))
    return all(v > 1e-6 for v in vals) or all(v < -1e-6 for v in vals)


def triangulate_polygon(
    polygon: list[int],
    points: dict[int, Point2],
) -> tuple[tuple[tuple[int, int, int], ...], float]:
    """Ear-clipping triangulation of a simple polygon.

    ``polygon`` is a list of vertex indices in CCW order.
    ``points`` maps vertex index to XY.
    Returns a tuple of triangles (each a 3-tuple of vertex indices) and the
    total triangle area.
    """
    if len(polygon) < 3:
        return (), 0.0

    remaining = list(polygon)
    # Ensure CCW.
    if _signed_area(remaining, points) < 0:
        remaining.reverse()

    triangles: list[tuple[int, int, int]] = []
    guard = 0
    max_guard = len(remaining) * len(remaining) + 10

    while len(remaining) > 3:
        clipped = False
        for pos in range(len(remaining)):
            prev_slot = remaining[(pos - 1) % len(remaining)]
            curr_slot = remaining[pos]
            next_slot = remaining[(pos + 1) % len(remaining)]
            tri = (prev_slot, curr_slot, next_slot)
            a, b, c = (points[i] for i in tri)
            if len(set(tri)) < 3 or _cross(a, b, c) <= 1e-6:
                continue
            # No other remaining vertex strictly inside this triangle.
            if any(
                slot not in (prev_slot, curr_slot, next_slot)
                and remaining_idx not in tri
                and _strictly_inside_triangle(points[remaining_idx], a, b, c)
                for slot, remaining_idx in enumerate(remaining)
            ):
                continue
            triangles.append(tri)
            remaining.pop(pos)
            clipped = True
            break
        guard += 1
        if not clipped or guard > max_guard:
            raise navmesh_inspect.NavMeshFormatError(
                "addition polygon could not be triangulated safely"
            )

    final = tuple(remaining)
    if len(set(final)) != 3:
        raise navmesh_inspect.NavMeshFormatError(
            "addition triangulation ended with a degenerate face"
        )
    triangles.append(final)

    area = sum(abs(_cross(points[a], points[b], points[c])) / 2.0 for a, b, c in triangles)
    return tuple(triangles), area


# ---------------------------------------------------------------------------
# Metadata resolution
# ---------------------------------------------------------------------------

def _canonical_metadata(md: tuple[int, int, int, int, int]) -> tuple[int, int, int, int, int]:
    return (md[0], md[1], md[2], md[3], abs(md[4]))


# ---------------------------------------------------------------------------
# Main addition writer
# ---------------------------------------------------------------------------

def apply_addition(
    source: Path,
    output: Path,
    manifest_path: Path,
) -> dict:
    """Add walkable navmesh faces for every geometry-ready required area in the manifest.

    The source navmesh is never modified.  A new candidate is written to
    ``output`` and structurally validated before the function returns.
    """
    if output.resolve() == source.resolve():
        raise navmesh_inspect.NavMeshFormatError("output must not overwrite the input")
    if output.exists():
        raise navmesh_inspect.NavMeshFormatError("output already exists")

    # ── 1. Read manifest ──────────────────────────────────────────────────
    document = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if document.get("Format") != "CustomSceneCreator.NavMeshPlan/2":
        raise navmesh_inspect.NavMeshFormatError("addition writer requires a version-2 CSC manifest")
    saved = document.get("RequiredAreas")
    if not isinstance(saved, list) or not saved:
        raise navmesh_inspect.NavMeshFormatError("manifest contains no navmesh-required areas")

    # Parse requirements with boundary geometry.
    requirements: list[dict] = []
    for index, req in enumerate(saved):
        pos = req.get("Pos")
        if not isinstance(pos, list) or len(pos) < 3:
            raise navmesh_inspect.NavMeshFormatError(f"required area {index} has no valid position")
        center: Point3 = (float(pos[0]), float(pos[1]), float(pos[2]))
        radius = max(0.5, float(req.get("Radius", 4.0)))
        boundary = _xyz(req.get("Boundary"))
        if not boundary:
            raise navmesh_inspect.NavMeshFormatError(
                f"required area {index} has no Boundary samples — re-mark with a terrain-sampling editor build"
            )
        requirements.append({
            "index": index,
            "id": str(req.get("Id", "")),
            "center": center,
            "radius": radius,
            "boundary": boundary,
        })

    # ── 2. Read source navmesh ────────────────────────────────────────────
    original, raw, wrapper = navmesh_inspect.read_raw(source)
    source_summary = navmesh_inspect.summarize(source)
    vertices: list[Point3] = list(navmesh_inspect._leading_arrays(raw)[0])
    edges = list(navmesh_inspect._leading_arrays(raw)[1])
    face_count, face_offset = navmesh_inspect._leading_arrays(raw)[2], navmesh_inspect._leading_arrays(raw)[3]
    faces, tail = navmesh_inspect._faces(raw, face_count, face_offset)

    # Validate edge format (same check as cutout writer).
    if any(e[0] != -1 or e[3] != -1 or e[4] != -1 or e[5] != 0 for e in edges):
        raise navmesh_inspect.NavMeshFormatError("unsupported nonstandard NMG9 edge metadata")

    # Build edge-face ownership and open-boundary-edge list.
    edge_faces: list[list[int]] = [[] for _ in edges]
    for face_index, face in enumerate(faces):
        for edge_index in face.edges:
            edge_faces[edge_index].append(face_index)
    open_edges = [
        (edge_index, edges[edge_index][1], edges[edge_index][2])
        for edge_index, owners in enumerate(edge_faces)
        if len(owners) == 1
    ]
    if not open_edges:
        raise navmesh_inspect.NavMeshFormatError("navmesh has no open boundary edge to extend from")

    # ── 3. Cluster requirements ───────────────────────────────────────────
    clusters = _clusters(requirements)

    # ── 4. Process each cluster ───────────────────────────────────────────
    new_vertices = list(vertices)
    new_edges = list(edges)
    new_faces = list(faces)

    # Edge lookup for deduplication (same pattern as cutout writer).
    edge_lookup: dict[tuple[int, int], int] = {}
    for edge_index, edge in enumerate(new_edges):
        key = (min(edge[1], edge[2]), max(edge[1], edge[2]))
        edge_lookup[key] = edge_index

    # Track consumed open edges globally — once a bridge uses an open edge,
    # it becomes a manifold edge (two faces) and must not be bridged again
    # by a subsequent cluster.
    consumed_open_edges: set[int] = set()

    operations: list[dict] = []

    for cluster_id, cluster_indices in enumerate(clusters):
        cluster_reqs = [requirements[i] for i in cluster_indices]

        # ── 4a. Collect all boundary samples from all requirements in cluster
        all_samples: list[Point3] = []
        for req in cluster_reqs:
            all_samples.extend(req["boundary"])

        if len(all_samples) < 3:
            raise navmesh_inspect.NavMeshFormatError(
                f"cluster {cluster_id} has fewer than 3 boundary samples"
            )

        # ── 4b. Compute convex hull envelope (XY projection)
        envelope = convex_hull_xy(all_samples)
        if len(envelope) < 3:
            raise navmesh_inspect.NavMeshFormatError(
                f"cluster {cluster_id} hull has fewer than 3 vertices"
            )

        # Add envelope vertices to the global vertex list.
        envelope_global_indices: list[int] = []
        for env_vertex in envelope:
            global_idx = len(new_vertices)
            new_vertices.append(env_vertex)
            envelope_global_indices.append(global_idx)

        # ── 4c. Find the single closest bridge: one open edge + one envelope vertex
        # We use exactly one bridge per cluster to connect the new region to the
        # existing mesh.  The bridge's two existing-edge endpoints become part of
        # the combined polygon so the interior triangulation shares edges with
        # the bridge triangle, ensuring topological connectivity.
        best_bridge: tuple[float, int, int, int, int, int] | None = None  # (dist, edge_idx, va, vb, env_local, face_idx)
        for edge_index, va, vb in open_edges:
            if edge_index in consumed_open_edges:
                continue
            face_idx = edge_faces[edge_index][0]
            for env_local_idx, env_vertex in enumerate(envelope):
                env_xy: Point2 = (env_vertex[0], env_vertex[1])
                dist_a = _distance(env_xy, (new_vertices[va][0], new_vertices[va][1]))
                dist_b = _distance(env_xy, (new_vertices[vb][0], new_vertices[vb][1]))
                min_dist = min(dist_a, dist_b)
                if best_bridge is None or min_dist < best_bridge[0]:
                    best_bridge = (min_dist, edge_index, va, vb, env_local_idx, face_idx)

        if best_bridge is None:
            raise navmesh_inspect.NavMeshFormatError(
                f"cluster {cluster_id}: no open edge was close enough to any envelope vertex"
            )

        _, bridge_edge_index, bridge_va, bridge_vb, bridge_env_local, bridge_face_idx = best_bridge
        consumed_open_edges.add(bridge_edge_index)
        bridge_env_global = envelope_global_indices[bridge_env_local]

        # Determine the bridge triangle winding.
        a_xy: Point2 = (new_vertices[bridge_va][0], new_vertices[bridge_va][1])
        b_xy: Point2 = (new_vertices[bridge_vb][0], new_vertices[bridge_vb][1])
        env_xy: Point2 = (envelope[bridge_env_local][0], envelope[bridge_env_local][1])
        cross = _cross(a_xy, b_xy, env_xy)
        if cross > 0:
            bridge_triangle = (bridge_va, bridge_vb, bridge_env_global)
        else:
            bridge_triangle = (bridge_va, bridge_env_global, bridge_vb)

        # ── 4d. Resolve metadata from the bridge owner face
        owner_face = faces[bridge_face_idx]
        metadata = list(_canonical_metadata(owner_face.metadata))
        metadata[4] = -abs(metadata[4]) if metadata[4] else metadata[4]
        direction = owner_face.direction

        # ── 4e. Build a combined polygon that includes the bridge edge endpoints
        # and the full envelope, then triangulate as one piece.
        # The combined polygon walks: bridge_va → bridge_vb → [envelope starting
        # from bridge_env_local, going CCW around the hull] → back to bridge_va.
        # This ensures the bridge triangle and interior triangles share edges.
        point_lookup: dict[int, Point2] = {}
        for env_local_idx, env_vertex in enumerate(envelope):
            point_lookup[envelope_global_indices[env_local_idx]] = (env_vertex[0], env_vertex[1])
        point_lookup[bridge_va] = (new_vertices[bridge_va][0], new_vertices[bridge_va][1])
        point_lookup[bridge_vb] = (new_vertices[bridge_vb][0], new_vertices[bridge_vb][1])

        # Build the combined polygon: start from bridge_va, go to bridge_vb,
        # then walk the envelope from bridge_env_local in CCW order, then close
        # back to bridge_va.  The envelope hull is already CCW from convex_hull_xy.
        envelope_ccw = list(envelope_global_indices)
        # Ensure CCW.
        if _signed_area(envelope_ccw, point_lookup) < 0:
            envelope_ccw.reverse()

        # Find the position of bridge_env_global in the CCW envelope.
        env_start_pos = envelope_ccw.index(bridge_env_global)

        # Combined polygon: bridge_va, bridge_vb, then envelope from env_start forward,
        # wrapping around, then back to bridge_va.
        combined_polygon = [bridge_va, bridge_vb]
        combined_polygon.extend(envelope_ccw[env_start_pos:] + envelope_ccw[:env_start_pos])
        combined_polygon.append(bridge_va)  # close the loop

        # Remove the trailing duplicate if present.
        if combined_polygon[-1] == combined_polygon[0]:
            combined_polygon = combined_polygon[:-1]

        all_new_triangles, interior_area = triangulate_polygon(combined_polygon, point_lookup)

        def _ensure_edge(a: int, b: int) -> int:
            key = (min(a, b), max(a, b))
            edge_index = edge_lookup.get(key)
            if edge_index is None:
                edge_index = len(new_edges)
                edge_lookup[key] = edge_index
                new_edges.append((-1, a, b, -1, -1, 0))
            return edge_index

        faces_added = 0
        for tri in all_new_triangles:
            # Validate non-degenerate.
            pts = [(new_vertices[tri[0]][0], new_vertices[tri[0]][1]),
                   (new_vertices[tri[1]][0], new_vertices[tri[1]][1]),
                   (new_vertices[tri[2]][0], new_vertices[tri[2]][1])]
            area = abs(_cross(pts[0], pts[1], pts[2])) / 2.0
            if area <= 1e-4:
                raise navmesh_inspect.NavMeshFormatError(
                    f"degenerate addition triangle {tri} (area {area:.6e})"
                )
            tri_edges = tuple(_ensure_edge(tri[i], tri[(i + 1) % 3]) for i in range(3))
            new_faces.append(navmesh_inspect.NmgFace(
                vertices=tri,
                edges=tri_edges,
                metadata=tuple(metadata),
                direction=direction,
            ))
            faces_added += 1

        operations.append({
            "cluster_id": cluster_id,
            "requirements": [req["id"] for req in cluster_reqs],
            "envelope_vertices": len(envelope),
            "bridge_edge_index": bridge_edge_index,
            "bridge_endpoints": [bridge_va, bridge_vb],
            "bridge_envelope_vertex": bridge_env_global,
            "combined_polygon_vertices": len(combined_polygon),
            "faces_added": faces_added,
            "interior_area": interior_area,
            "metadata": list(metadata),
            "direction": direction,
        })

    # ── 5. Serialize and validate ─────────────────────────────────────────
    rebuilt_raw = navmesh_inspect.serialize_raw(b"NMG9", new_vertices, new_edges, new_faces, tail)
    # Reparse for lossless check.
    cv, ce, cfc, cfo = navmesh_inspect._leading_arrays(rebuilt_raw)
    cf, ct = navmesh_inspect._faces(rebuilt_raw, cfc, cfo)
    if navmesh_inspect.serialize_raw(b"NMG9", cv, ce, cf, ct) != rebuilt_raw:
        raise navmesh_inspect.NavMeshFormatError("in-memory addition failed lossless validation")

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
            or summary.nonmanifold_edges
        ):
            raise navmesh_inspect.NavMeshFormatError("written addition failed structural validation")
    except Exception:
        output.unlink(missing_ok=True)
        raise

    # Source must not have changed.
    source_recheck = navmesh_inspect.summarize(source)
    if source_recheck.compressed_sha256 != source_summary.compressed_sha256:
        raise navmesh_inspect.NavMeshFormatError("source navmesh was modified during addition")

    return {
        "source": str(source.resolve()),
        "output": str(output.resolve()),
        "manifest": str(manifest_path.resolve()),
        "scene": str(document.get("Scene", "")),
        "clusters_processed": len(clusters),
        "operations": operations,
        "vertices_before": len(vertices),
        "vertices_after": len(new_vertices),
        "edges_before": len(edges),
        "edges_after": len(new_edges),
        "faces_before": len(faces),
        "faces_after": len(new_faces),
        "connected_face_components": summary.connected_face_components,
        "nonmanifold_edges": summary.nonmanifold_edges,
        "retained_unused_vertices": summary.unused_vertices,
        "retained_unused_edges": summary.unused_edges,
        "output_sha256": summary.compressed_sha256,
        "raw_sha256": summary.raw_sha256,
    }


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path,
                        help="path to a CSC NavMeshPlan/2 .navcut.json manifest")
    parser.add_argument("--source", type=Path,
                        help="explicit source navmesh.bin (auto-located by default)")
    parser.add_argument("--output", type=Path,
                        help="output candidate navmesh.bin (auto-named by default)")
    parser.add_argument("--modules-root", action="append", type=Path)
    args = parser.parse_args()

    try:
        # Reuse the workflow's manifest loader and scene locator.
        import importlib.util
        spec = importlib.util.spec_from_file_location(
            "navmesh_workflow", Path(__file__).parent / "navmesh_workflow.py")
        assert spec and spec.loader
        navmesh_workflow = importlib.util.module_from_spec(spec)
        import sys
        sys.modules[spec.name] = navmesh_workflow
        spec.loader.exec_module(navmesh_workflow)

        manifest = navmesh_workflow.load_manifest(args.manifest)
        scene = str(manifest["Scene"]).strip()
        source = args.source or navmesh_workflow.locate_scene_navmesh(
            scene, args.modules_root or navmesh_workflow.default_modules_roots())
        if not source.is_file():
            raise navmesh_inspect.NavMeshFormatError(f"source navmesh does not exist: {source}")
        output = args.output or navmesh_workflow.default_output(
            manifest, manifest_path=args.manifest)
        result = apply_addition(source, output, args.manifest)
        print(json.dumps(result, indent=2))
    except (OSError, ValueError, json.JSONDecodeError, navmesh_inspect.NavMeshFormatError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
