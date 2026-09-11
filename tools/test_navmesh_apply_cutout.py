import importlib.util
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path


TOOLS = Path(__file__).parent
sys.path.insert(0, str(TOOLS))
SPEC = importlib.util.spec_from_file_location("navmesh_apply_cutout", TOOLS / "navmesh_apply_cutout.py")
assert SPEC and SPEC.loader
navmesh_apply_cutout = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = navmesh_apply_cutout
SPEC.loader.exec_module(navmesh_apply_cutout)
import navmesh_inspect


def one_quad_raw() -> bytes:
    vertices = ((0.0, 0.0, 0.0), (10.0, 0.0, 0.0), (10.0, 10.0, 0.0), (0.0, 10.0, 0.0))
    edges = ((-1, 0, 1, -1, -1, 0), (-1, 1, 2, -1, -1, 0), (-1, 2, 3, -1, -1, 0), (-1, 3, 0, -1, -1, 0))
    face = navmesh_inspect.NmgFace((0, 1, 2, 3), (0, 1, 2, 3), (2, 0, 0, 0, 15), 0)
    return navmesh_inspect.serialize_raw(b"NMG9", list(vertices), list(edges), [face], bytes(260))


def stacked_quads_raw() -> bytes:
    base = ((0.0, 0.0, 0.0), (10.0, 0.0, 0.0), (10.0, 10.0, 0.0), (0.0, 10.0, 0.0))
    top = tuple((x, y, 10.0) for x, y, _ in base)
    edges = [
        (-1, start, end, -1, -1, 0)
        for offset in (0, 4)
        for start, end in ((offset, offset + 1), (offset + 1, offset + 2),
                           (offset + 2, offset + 3), (offset + 3, offset))
    ]
    faces = [
        navmesh_inspect.NmgFace((0, 1, 2, 3), (0, 1, 2, 3), (2, 0, 0, 0, 15), 0),
        navmesh_inspect.NmgFace((4, 5, 6, 7), (4, 5, 6, 7), (2, 0, 1, 0, 15), 0),
    ]
    return navmesh_inspect.serialize_raw(b"NMG9", list(base + top), edges, faces, bytes(260))


class ApplyTests(unittest.TestCase):
    def test_height_band_selects_only_matching_storey(self):
        raw = stacked_quads_raw()
        wrapped = struct.pack("<I", len(raw)) + raw
        corners = ((3.0, 3.0), (7.0, 3.0), (7.0, 7.0), (3.0, 7.0))
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.bin"
            source.write_bytes(wrapped)
            plan = navmesh_apply_cutout.navmesh_cutout.build_plan(source, corners, 8.75, 11.25)
        self.assertEqual(plan.affected_faces, (1,))
        self.assertTrue(all(abs(point[2] - 10.0) < 1e-6 for point in plan.projected_corners_xyz))

    def test_applies_to_new_copy_and_leaves_hole(self):
        raw = one_quad_raw()
        wrapped = struct.pack("<I", len(raw)) + raw
        corners = ((3.0, 3.0, 0.0), (7.0, 3.0, 0.0), (7.0, 7.0, 0.0), (3.0, 7.0, 0.0))
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.bin"
            output = Path(directory) / "output.bin"
            source.write_bytes(wrapped)
            report = navmesh_apply_cutout.apply_cutout(source, output, corners)
            summary = navmesh_inspect.summarize(output)
            _, rebuilt, _ = navmesh_inspect.read_raw(output)
            vertices, _, count, offset = navmesh_inspect._leading_arrays(rebuilt)
            faces, _ = navmesh_inspect._faces(rebuilt, count, offset)
        self.assertEqual(report["affected_faces_removed"], 1)
        self.assertEqual(report["repair_faces_added"], 8)
        self.assertEqual(summary.invalid_face_boundaries, 0)
        center = (5.0, 5.0)
        polygons = [tuple((vertices[index][0], vertices[index][1]) for index in face.vertices) for face in faces]
        self.assertFalse(any(navmesh_apply_cutout.navmesh_cutout._point_in_polygon(center, polygon) for polygon in polygons))

    def test_refuses_overwrite(self):
        raw = one_quad_raw()
        wrapped = struct.pack("<I", len(raw)) + raw
        corners = ((3.0, 3.0, 0.0), (7.0, 3.0, 0.0), (7.0, 7.0, 0.0), (3.0, 7.0, 0.0))
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.bin"
            output = Path(directory) / "output.bin"
            source.write_bytes(wrapped)
            output.write_bytes(b"keep")
            with self.assertRaises(navmesh_inspect.NavMeshFormatError):
                navmesh_apply_cutout.apply_cutout(source, output, corners)
            self.assertEqual(output.read_bytes(), b"keep")

    def test_sequential_cutouts_accept_mixed_metadata_sign(self):
        """A second cutout whose affected faces include repair faces from the first
        (which carry -15 instead of +15) must not fail the metadata compatibility check."""
        raw = one_quad_raw()
        wrapped = struct.pack("<I", len(raw)) + raw
        corners_a = ((1.0, 1.0, 0.0), (4.0, 1.0, 0.0), (4.0, 4.0, 0.0), (1.0, 4.0, 0.0))
        corners_b = ((6.0, 6.0, 0.0), (9.0, 6.0, 0.0), (9.0, 9.0, 0.0), (6.0, 9.0, 0.0))
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.bin"
            step0 = Path(directory) / "step0.bin"
            step1 = Path(directory) / "step1.bin"
            source.write_bytes(wrapped)
            navmesh_apply_cutout.apply_cutout(source, step0, corners_a)
            navmesh_apply_cutout.apply_cutout(step0, step1, corners_b)
            summary = navmesh_inspect.summarize(step1)
        self.assertEqual(summary.connected_face_components, 1)
        self.assertEqual(summary.nonmanifold_edges, 0)

    def test_reads_csc_manifest_corners(self):
        document = {
            "Format": "CustomSceneCreator.NavMeshPlan/2",
            "Cutouts": [
                {
                    "Corners": [
                        3.0, 3.0, 0.25,
                        7.0, 3.0, 0.5,
                        7.0, 7.0, 0.75,
                        3.0, 7.0, 1.0,
                    ]
                }
            ],
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "plan.navcut.json"
            path.write_text(json.dumps(document), encoding="utf-8")
            corners = navmesh_apply_cutout.navmesh_cutout.manifest_corner_xyz(path, 0)
        self.assertEqual(
            corners,
            ((3.0, 3.0, 0.25), (7.0, 3.0, 0.5), (7.0, 7.0, 0.75), (3.0, 7.0, 1.0)),
        )


if __name__ == "__main__":
    unittest.main()
