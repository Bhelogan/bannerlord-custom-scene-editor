import importlib.util
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path


TOOLS = Path(__file__).parent
sys.path.insert(0, str(TOOLS))
SPEC = importlib.util.spec_from_file_location("navmesh_addition_plan", TOOLS / "navmesh_addition_plan.py")
assert SPEC and SPEC.loader
planner = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = planner
SPEC.loader.exec_module(planner)
import navmesh_inspect


def mesh() -> bytes:
    vertices = [(0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0)]
    edges = [(-1, 0, 1, -1, -1, 0), (-1, 1, 2, -1, -1, 0), (-1, 2, 3, -1, -1, 0), (-1, 3, 0, -1, -1, 0)]
    face = navmesh_inspect.NmgFace((0, 1, 2, 3), (0, 1, 2, 3), (2, 0, 0, 0, 15), 0)
    return navmesh_inspect.serialize_raw(b"NMG9", vertices, edges, [face], bytes(260))


def ring(cx, cy, radius, count=16):
    import math
    values = []
    for index in range(count):
        angle = index * math.pi * 2 / count
        values.extend([cx + math.cos(angle) * radius, cy + math.sin(angle) * radius, 0])
    return values


class AdditionPlanTests(unittest.TestCase):
    def test_plans_sampled_area_at_boundary(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            navmesh = root / "navmesh.bin"
            raw = mesh()
            navmesh.write_bytes(struct.pack("<I", len(raw)) + raw)
            manifest = root / "plan.json"
            manifest.write_text(json.dumps({
                "Format": "CustomSceneCreator.NavMeshPlan/2",
                "Scene": "test",
                "RequiredAreas": [{"Id": "one", "Pos": [12, 5, 0], "Radius": 2, "Boundary": ring(12, 5, 2)}],
            }), encoding="utf-8")
            result = planner.build_addition_plan(navmesh, manifest)
        self.assertTrue(result["all_geometry_ready"])
        self.assertEqual(result["requirements"][0]["boundary_samples"], 16)
        self.assertAlmostEqual(result["requirements"][0]["horizontal_gap_from_sample"], 0.0, places=5)

    def test_old_note_is_reported_not_ready(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            navmesh = root / "navmesh.bin"
            raw = mesh()
            navmesh.write_bytes(struct.pack("<I", len(raw)) + raw)
            manifest = root / "plan.json"
            manifest.write_text(json.dumps({
                "Format": "CustomSceneCreator.NavMeshPlan/2",
                "Scene": "test",
                "RequiredAreas": [{"Pos": [14, 5, 0], "Radius": 2}],
            }), encoding="utf-8")
            result = planner.build_addition_plan(navmesh, manifest)
        self.assertFalse(result["all_geometry_ready"])
        self.assertIn("re-mark", result["requirements"][0]["note"])


if __name__ == "__main__":
    unittest.main()
