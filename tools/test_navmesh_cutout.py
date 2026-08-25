import importlib.util
import sys
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


if __name__ == "__main__":
    unittest.main()
