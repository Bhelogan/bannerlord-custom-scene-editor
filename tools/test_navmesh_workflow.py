import importlib.util
import json
import struct
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path


TOOLS = Path(__file__).parent
sys.path.insert(0, str(TOOLS))
SPEC = importlib.util.spec_from_file_location("navmesh_workflow", TOOLS / "navmesh_workflow.py")
assert SPEC and SPEC.loader
navmesh_workflow = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = navmesh_workflow
SPEC.loader.exec_module(navmesh_workflow)
import navmesh_inspect


def one_quad_raw() -> bytes:
    vertices = ((0.0, 0.0, 0.0), (10.0, 0.0, 0.0), (10.0, 10.0, 0.0), (0.0, 10.0, 0.0))
    edges = ((-1, 0, 1, -1, -1, 0), (-1, 1, 2, -1, -1, 0), (-1, 2, 3, -1, -1, 0), (-1, 3, 0, -1, -1, 0))
    face = navmesh_inspect.NmgFace((0, 1, 2, 3), (0, 1, 2, 3), (2, 0, 0, 0, 15), 0)
    return navmesh_inspect.serialize_raw(b"NMG9", list(vertices), list(edges), [face], bytes(260))


def manifest(scene="workflow_scene", cutout_count=1):
    cutouts = [
        {"Prefab": "test_barn_a", "Corners": [1, 3, 2, 3, 3, 2, 3, 7, 2, 1, 7, 2]},
        {"Prefab": "test_barn_b", "Corners": [7, 3, 2, 9, 3, 2, 9, 7, 2, 7, 7, 2]},
    ]
    return {
        "Format": "CustomSceneCreator.NavMeshPlan/2",
        "Project": "Workflow Test",
        "Scene": scene,
        "Cutouts": cutouts[:cutout_count],
        "RequiredAreas": [],
    }


class WorkflowTests(unittest.TestCase):
    def test_locates_unique_scene_and_creates_candidate_report(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "Modules" / "TestModule" / "SceneObj" / "workflow_scene" / "navmesh.bin"
            source.parent.mkdir(parents=True)
            raw = one_quad_raw()
            source.write_bytes(struct.pack("<I", len(raw)) + raw)
            plan = root / "test.navcut.json"
            plan.write_text(json.dumps(manifest()), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            result = navmesh_workflow.create_candidate(plan, output, modules_roots=[root / "Modules"])
            self.assertTrue(output.is_file())
            self.assertTrue(output.with_name("navmesh_candidate_report.json").is_file())
            self.assertEqual(result["scene"], "workflow_scene")
            self.assertEqual(result["max_abs_z_delta"], 2.0)
            self.assertEqual(result["cutouts_applied"], 1)

    def test_refuses_ambiguous_scene(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for module in ("One", "Two"):
                path = root / module / "SceneObj" / "same_scene" / "navmesh.bin"
                path.parent.mkdir(parents=True)
                path.write_bytes(b"x")
            with self.assertRaises(navmesh_inspect.NavMeshFormatError):
                navmesh_workflow.locate_scene_navmesh("same_scene", [root])

    def test_requires_at_least_one_cutout(self):
        with tempfile.TemporaryDirectory() as directory:
            plan = Path(directory) / "test.navcut.json"
            plan.write_text(json.dumps(manifest(cutout_count=0)), encoding="utf-8")
            with self.assertRaises(navmesh_inspect.NavMeshFormatError):
                navmesh_workflow.load_manifest(plan)

    def test_applies_two_cutouts_to_one_candidate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.bin"
            raw = one_quad_raw()
            source.write_bytes(struct.pack("<I", len(raw)) + raw)
            plan = root / "two.navcut.json"
            plan.write_text(json.dumps(manifest(cutout_count=2)), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            result = navmesh_workflow.create_candidate(plan, output, source=source)
            self.assertEqual(result["cutouts_applied"], 2)
            self.assertEqual(len(result["operations"]), 2)
            self.assertEqual([op["prefab"] for op in result["operations"]], ["test_barn_a", "test_barn_b"])
            self.assertEqual(navmesh_inspect.summarize(output).connected_face_components, 1)

    def test_default_output_is_documents_candidate_folder(self):
        value = navmesh_workflow.default_output(
            manifest(), datetime(2026, 8, 16, 12, 34, 56, tzinfo=timezone.utc)
        )
        self.assertEqual(value.name, "navmesh.bin")
        self.assertEqual(value.parent.name, "Workflow_Test_20260816T123456Z")
        self.assertEqual(value.parent.parent.name, "navmesh-candidates")

    def test_default_output_follows_manifest_export_root(self):
        plan = Path("D:/RedirectedDocs/CustomSceneCreator/exports/navmesh-cutouts/test.navcut.json")
        value = navmesh_workflow.default_output(
            manifest(),
            datetime(2026, 8, 16, 12, 34, 56, tzinfo=timezone.utc),
            manifest_path=plan,
        )
        self.assertEqual(value.parent.parent, plan.parent.parent / "navmesh-candidates")


if __name__ == "__main__":
    unittest.main()
