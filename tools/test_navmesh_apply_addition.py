"""Tests for the navmesh addition writer (navmesh_apply_addition.py).

Covers:
- Basic addition: one required area near an open mesh edge gets faces added.
- Cluster merge: two overlapping required areas produce one connected region.
- Validation: the result has no non-manifold edges, valid references, finite vertices.
- Refusal cases: no boundary samples, no open edges, output exists.
- Area coverage: new faces actually cover the required area centers.
"""

import importlib.util
import json
import math
import struct
import sys
import tempfile
import unittest
from pathlib import Path


TOOLS = Path(__file__).parent
sys.path.insert(0, str(TOOLS))

SPEC = importlib.util.spec_from_file_location("navmesh_apply_addition", TOOLS / "navmesh_apply_addition.py")
assert SPEC and SPEC.loader
navmesh_apply_addition = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = navmesh_apply_addition
SPEC.loader.exec_module(navmesh_apply_addition)
import navmesh_inspect
import navmesh_cutout


def _ring(cx, cy, radius, count=16, z=0.0):
    """Terrain-sampled boundary ring: count XYZ triples around a center."""
    values = []
    for index in range(count):
        angle = index * math.pi * 2 / count
        values.extend([cx + math.cos(angle) * radius, cy + math.sin(angle) * radius, z])
    return values


def _mesh_with_open_edge() -> bytes:
    """A simple mesh: one quad face from (0,0) to (10,10) with open boundary edges.

    All four edges are open (owned by exactly one face), so the addition writer
    can bridge from any of them.
    """
    vertices = [(0.0, 0.0, 0.0), (10.0, 0.0, 0.0), (10.0, 10.0, 0.0), (0.0, 10.0, 0.0)]
    edges = [
        (-1, 0, 1, -1, -1, 0),
        (-1, 1, 2, -1, -1, 0),
        (-1, 2, 3, -1, -1, 0),
        (-1, 3, 0, -1, -1, 0),
    ]
    face = navmesh_inspect.NmgFace((0, 1, 2, 3), (0, 1, 2, 3), (2, 0, 0, 0, 15), 0)
    return navmesh_inspect.serialize_raw(b"NMG9", vertices, edges, [face], bytes(260))


def _manifest_one_area(cx, cy, radius=2.0, z=0.0, scene="test_scene"):
    """A version-2 manifest with one required area that has boundary samples."""
    return {
        "Format": "CustomSceneCreator.NavMeshPlan/2",
        "Project": "test",
        "Scene": scene,
        "Cutouts": [],
        "RequiredAreas": [
            {
                "Id": "{TEST-0001}",
                "Label": "Navmesh needed",
                "Pos": [cx, cy, z],
                "Radius": radius,
                "Boundary": _ring(cx, cy, radius, z=z),
                "NearestFaceIndex": -1,
                "NearestFaceDistance": -1.0,
                "Created": "2026-08-17T00:00:00Z",
            }
        ],
        "Note": "test",
    }


def _manifest_two_areas(cx1, cy1, cx2, cy2, radius=2.0, z=0.0, scene="test_scene"):
    """A version-2 manifest with two overlapping required areas."""
    return {
        "Format": "CustomSceneCreator.NavMeshPlan/2",
        "Project": "test",
        "Scene": scene,
        "Cutouts": [],
        "RequiredAreas": [
            {
                "Id": "{TEST-0001}",
                "Label": "Navmesh needed",
                "Pos": [cx1, cy1, z],
                "Radius": radius,
                "Boundary": _ring(cx1, cy1, radius, z=z),
                "NearestFaceIndex": -1,
                "NearestFaceDistance": -1.0,
                "Created": "2026-08-17T00:00:00Z",
            },
            {
                "Id": "{TEST-0002}",
                "Label": "Navmesh needed",
                "Pos": [cx2, cy2, z],
                "Radius": radius,
                "Boundary": _ring(cx2, cy2, radius, z=z),
                "NearestFaceIndex": -1,
                "NearestFaceDistance": -1.0,
                "Created": "2026-08-17T00:00:01Z",
            },
        ],
        "Note": "test",
    }


class AdditionTests(unittest.TestCase):
    def test_adds_faces_for_one_required_area(self):
        raw = _mesh_with_open_edge()
        wrapped = struct.pack("<I", len(raw)) + raw
        manifest = _manifest_one_area(15.0, 5.0, radius=3.0)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / "source.bin"
            source.write_bytes(wrapped)
            plan = root / "plan.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            result = navmesh_apply_addition.apply_addition(source, output, plan)

            self.assertTrue(output.is_file())
            self.assertGreater(result["faces_after"], result["faces_before"])
            self.assertGreater(result["vertices_after"], result["vertices_before"])
            self.assertEqual(result["clusters_processed"], 1)
            self.assertEqual(result["nonmanifold_edges"], 0)

    def test_result_has_no_nonmanifold_or_invalid_references(self):
        raw = _mesh_with_open_edge()
        wrapped = struct.pack("<I", len(raw)) + raw
        manifest = _manifest_one_area(15.0, 5.0, radius=3.0)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / "source.bin"
            source.write_bytes(wrapped)
            plan = root / "plan.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            navmesh_apply_addition.apply_addition(source, output, plan)
            summary = navmesh_inspect.summarize(output)

            self.assertEqual(summary.nonmanifold_edges, 0)
            self.assertEqual(summary.invalid_face_references, 0)
            self.assertEqual(summary.invalid_face_boundaries, 0)
            self.assertEqual(summary.self_edges, 0)
            self.assertTrue(summary.finite_vertices)

    def test_new_faces_cover_required_area_center(self):
        """The center of the required area should now be inside a navmesh face."""
        raw = _mesh_with_open_edge()
        wrapped = struct.pack("<I", len(raw)) + raw
        cx, cy = 15.0, 5.0
        manifest = _manifest_one_area(cx, cy, radius=3.0)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / "source.bin"
            source.write_bytes(wrapped)
            plan = root / "plan.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            navmesh_apply_addition.apply_addition(source, output, plan)

            # Read the result and check that (cx, cy) is inside some face.
            _, raw_out, _ = navmesh_inspect.read_raw(output)
            vertices_out, edges_out, fc, fo = navmesh_inspect._leading_arrays(raw_out)
            faces_out, _ = navmesh_inspect._faces(raw_out, fc, fo)
            polygons = [
                tuple((vertices_out[v][0], vertices_out[v][1]) for v in face.vertices)
                for face in faces_out
            ]
            self.assertTrue(
                any(navmesh_cutout._point_in_polygon((cx, cy), p) for p in polygons),
                "No new face covers the required area center"
            )

    def test_two_overlapping_areas_merge_into_one_cluster(self):
        raw = _mesh_with_open_edge()
        wrapped = struct.pack("<I", len(raw)) + raw
        manifest = _manifest_two_areas(14.0, 5.0, 16.0, 5.0, radius=2.0)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / "source.bin"
            source.write_bytes(wrapped)
            plan = root / "plan.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            result = navmesh_apply_addition.apply_addition(source, output, plan)

            self.assertEqual(result["clusters_processed"], 1)
            self.assertGreater(result["faces_after"], result["faces_before"])
            self.assertEqual(result["nonmanifold_edges"], 0)

    def test_two_non_overlapping_areas_form_two_clusters(self):
        # Two separate quad faces so each cluster has its own open edges.
        vertices = [
            (0.0, 0.0, 0.0), (10.0, 0.0, 0.0), (10.0, 10.0, 0.0), (0.0, 10.0, 0.0),  # face 0
            (0.0, 20.0, 0.0), (10.0, 20.0, 0.0), (10.0, 30.0, 0.0), (0.0, 30.0, 0.0),  # face 1
        ]
        edges = [
            (-1, 0, 1, -1, -1, 0), (-1, 1, 2, -1, -1, 0), (-1, 2, 3, -1, -1, 0), (-1, 3, 0, -1, -1, 0),
            (-1, 4, 5, -1, -1, 0), (-1, 5, 6, -1, -1, 0), (-1, 6, 7, -1, -1, 0), (-1, 7, 4, -1, -1, 0),
        ]
        face0 = navmesh_inspect.NmgFace((0, 1, 2, 3), (0, 1, 2, 3), (2, 0, 0, 0, 15), 0)
        face1 = navmesh_inspect.NmgFace((4, 5, 6, 7), (4, 5, 6, 7), (2, 0, 0, 0, 15), 0)
        raw = navmesh_inspect.serialize_raw(b"NMG9", vertices, edges, [face0, face1], bytes(260))
        wrapped = struct.pack("<I", len(raw)) + raw

        manifest = _manifest_two_areas(15.0, 5.0, 15.0, 25.0, radius=2.0)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / "source.bin"
            source.write_bytes(wrapped)
            plan = root / "plan.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            result = navmesh_apply_addition.apply_addition(source, output, plan)

            self.assertEqual(result["clusters_processed"], 2)
            self.assertEqual(result["nonmanifold_edges"], 0)

    def test_refuses_existing_output(self):
        raw = _mesh_with_open_edge()
        wrapped = struct.pack("<I", len(raw)) + raw
        manifest = _manifest_one_area(15.0, 5.0, radius=3.0)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / "source.bin"
            source.write_bytes(wrapped)
            plan = root / "plan.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            output.parent.mkdir(parents=True)
            output.write_bytes(b"keep")
            with self.assertRaises(navmesh_inspect.NavMeshFormatError):
                navmesh_apply_addition.apply_addition(source, output, plan)
            self.assertEqual(output.read_bytes(), b"keep")

    def test_refuses_manifest_without_boundary(self):
        raw = _mesh_with_open_edge()
        wrapped = struct.pack("<I", len(raw)) + raw
        manifest = {
            "Format": "CustomSceneCreator.NavMeshPlan/2",
            "Scene": "test",
            "Cutouts": [],
            "RequiredAreas": [{"Pos": [15.0, 5.0, 0.0], "Radius": 2.0}],
        }
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / "source.bin"
            source.write_bytes(wrapped)
            plan = root / "plan.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            with self.assertRaises(navmesh_inspect.NavMeshFormatError):
                navmesh_apply_addition.apply_addition(source, output, plan)

    def test_preserves_source_unchanged(self):
        raw = _mesh_with_open_edge()
        wrapped = struct.pack("<I", len(raw)) + raw
        manifest = _manifest_one_area(15.0, 5.0, radius=3.0)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / "source.bin"
            source.write_bytes(wrapped)
            original_hash = navmesh_inspect.summarize(source).compressed_sha256
            plan = root / "plan.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            navmesh_apply_addition.apply_addition(source, output, plan)
            after_hash = navmesh_inspect.summarize(source).compressed_sha256
            self.assertEqual(original_hash, after_hash)

    def test_elevation_aware_z_in_new_vertices(self):
        """New vertices should carry terrain-sampled Z, not zero."""
        raw = _mesh_with_open_edge()
        wrapped = struct.pack("<I", len(raw)) + raw
        # Area at z=5.0 — above the flat mesh at z=0.
        manifest = _manifest_one_area(15.0, 5.0, radius=3.0, z=5.0)
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            source = root / "source.bin"
            source.write_bytes(wrapped)
            plan = root / "plan.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            navmesh_apply_addition.apply_addition(source, output, plan)

            _, raw_out, _ = navmesh_inspect.read_raw(output)
            vertices_out = navmesh_inspect._leading_arrays(raw_out)[0]
            # The first 4 vertices are the original mesh (z=0). New vertices
            # should have z close to 5.0 (from the boundary samples).
            new_zs = [v[2] for v in vertices_out[4:]]
            self.assertTrue(all(abs(z - 5.0) < 0.01 for z in new_zs),
                f"New vertex Z values should be ~5.0, got {new_zs}")


class ConvexHullTests(unittest.TestCase):
    def test_square_hull(self):
        pts = [(0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0), (5, 5, 0)]
        hull = navmesh_apply_addition.convex_hull_xy(pts)
        self.assertEqual(len(hull), 4)
        hull_xy = sorted((p[0], p[1]) for p in hull)
        self.assertEqual(hull_xy, [(0, 0), (0, 10), (10, 0), (10, 10)])

    def test_collinear_returns_sorted(self):
        pts = [(0, 0, 0), (5, 0, 0), (10, 0, 0)]
        hull = navmesh_apply_addition.convex_hull_xy(pts)
        self.assertEqual(len(hull), 3)

    def test_deduplicates_xy(self):
        pts = [(5, 5, 0), (5, 5, 3), (10, 10, 0)]
        hull = navmesh_apply_addition.convex_hull_xy(pts)
        # Two unique XY points — not enough for a hull, returns sorted.
        self.assertEqual(len(hull), 2)


if __name__ == "__main__":
    unittest.main()
