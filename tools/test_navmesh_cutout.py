import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


TOOLS = Path(__file__).parent
sys.path.insert(0, str(TOOLS))
SPEC = importlib.util.spec_from_file_location("navmesh_cutout", TOOLS / "navmesh_cutout.py")
assert SPEC and SPEC.loader
navmesh_cutout = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = navmesh_cutout
SPEC.loader.exec_module(navmesh_cutout)


class GeometryTests(unittest.TestCase):
    def test_point_in_polygon_includes_boundary(self):
        square = ((0.0, 0.0), (2.0, 0.0), (2.0, 2.0), (0.0, 2.0))
        self.assertTrue(navmesh_cutout._point_in_polygon((1.0, 1.0), square))
        self.assertTrue(navmesh_cutout._point_in_polygon((2.0, 1.0), square))
        self.assertFalse(navmesh_cutout._point_in_polygon((3.0, 1.0), square))

    def test_polygon_crossing_without_contained_vertices_intersects(self):
        horizontal = ((-2.0, -0.5), (2.0, -0.5), (2.0, 0.5), (-2.0, 0.5))
        vertical = ((-0.5, -2.0), (0.5, -2.0), (0.5, 2.0), (-0.5, 2.0))
        self.assertTrue(navmesh_cutout._polygons_intersect(horizontal, vertical))

    def test_freeform_manifest_accepts_polygon_and_height_band(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "cutout.json"
            manifest.write_text(json.dumps({
                "Format": "CustomSceneCreator.NavMeshPlan/2",
                "Cutouts": [{
                    "IsFreeform": True,
                    "MinZ": 7.5,
                    "MaxZ": 10.0,
                    "Corners": [0, 0, 8, 3, 0, 8, 4, 2, 8, 2, 4, 8, 0, 2, 8],
                }],
            }), encoding="utf-8")
            self.assertEqual(len(navmesh_cutout.manifest_corner_xyz(manifest, 0)), 5)
            self.assertEqual(navmesh_cutout.manifest_height_band(manifest, 0), (7.5, 10.0))

    def test_legacy_manifest_cutout_remains_unbounded(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "cutout.json"
            manifest.write_text(json.dumps({
                "Format": "CustomSceneCreator.NavMeshPlan/2",
                "Cutouts": [{"Corners": [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0]}],
            }), encoding="utf-8")
            self.assertEqual(
                navmesh_cutout.manifest_height_band(manifest, 0),
                (-float("inf"), float("inf")),
            )


if __name__ == "__main__":
    unittest.main()
