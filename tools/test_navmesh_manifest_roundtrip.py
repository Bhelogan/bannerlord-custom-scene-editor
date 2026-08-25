"""Save/reload regression for CSC project JSON and navmesh-cutout manifests.

This covers the JSON contract that the in-game editor (Newtonsoft.Json) and the
offline Python tools both depend on:

1. A project with 2 entities and 2 non-overlapping cutouts round-trips through
   serialize -> deserialize with all fields intact.
2. The navmesh-cutout manifest derived from that project preserves both cutouts.
3. The multi-cutout workflow produces a valid single-component candidate mesh.
4. A ProjectNavMeshRequirement with a Boundary array survives round-trip, and
   one without it deserializes cleanly (the backfill path).

These tests do not exercise the C# runtime; they validate the JSON shape that
the C# ProjectSerializer and NavMeshCutoutManifestExporter produce and consume.
"""

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


def _ring(cx, cy, radius, count=16, z=0.0):
    import math
    values = []
    for index in range(count):
        angle = index * math.pi * 2 / count
        values.extend([cx + math.cos(angle) * radius, cy + math.sin(angle) * radius, z])
    return values


def two_entity_two_cutout_project():
    """Mirror the C# SceneProject JSON shape with 2 entities and 2 cutouts."""
    return {
        "Name": "regression_two_cutouts",
        "Version": 1,
        "Created": "2026-08-17T12:00:00Z",
        "Modified": "2026-08-17T12:00:00Z",
        "TargetScene": "regression_scene",
        "SceneLevels": "base",
        "Entities": [
            {
                "Id": "{AAAA0000-0000-0000-0000-000000000001}",
                "Prefab": "test_barn_a",
                "Pos": [3.0, 3.0, 0.0],
                "RotF": [0, 1, 0],
                "RotU": [0, 0, 1],
                "RotS": [1, 0, 0],
                "Index": 0,
                "Scripts": [],
            },
            {
                "Id": "{BBBB1111-1111-1111-1111-111111111112}",
                "Prefab": "test_barn_b",
                "Pos": [7.0, 7.0, 0.0],
                "RotF": [0, 1, 0],
                "RotU": [0, 0, 1],
                "RotS": [1, 0, 0],
                "Index": 0,
                "Scripts": [],
            },
        ],
        "NavMeshCutouts": [
            {
                "EntityId": "{AAAA0000-0000-0000-0000-000000000001}",
                "Prefab": "test_barn_a",
                "Clearance": 0.75,
                "MinZ": 0.0,
                "MaxZ": 10.0,
                "Corners": [1, 3, 2, 3, 3, 2, 3, 7, 2, 1, 7, 2],
                "FaceIndices": [0],
                "FaceGroups": [0],
                "FaceIslands": [0],
                "Sampled": "2026-08-17T12:00:00Z",
            },
            {
                "EntityId": "{BBBB1111-1111-1111-1111-111111111112}",
                "Prefab": "test_barn_b",
                "Clearance": 0.75,
                "MinZ": 0.0,
                "MaxZ": 10.0,
                "Corners": [7, 3, 2, 9, 3, 2, 9, 7, 2, 7, 7, 2],
                "FaceIndices": [0],
                "FaceGroups": [0],
                "FaceIslands": [0],
                "Sampled": "2026-08-17T12:00:00Z",
            },
        ],
        "NavMeshRequirements": [],
    }


def manifest_from_project(project):
    """Mirror the C# NavMeshCutoutManifestExporter JSON shape."""
    return {
        "Format": "CustomSceneCreator.NavMeshPlan/2",
        "Project": project["Name"],
        "Scene": project["TargetScene"],
        "SceneLevels": project["SceneLevels"],
        "ExportedUtc": datetime.now(timezone.utc).isoformat(),
        "Cutouts": project["NavMeshCutouts"],
        "RequiredAreas": project.get("NavMeshRequirements", []),
        "Note": "Authoring handoff only. Applying this file must validate the current navmesh before modifying it.",
    }


class ProjectRoundTripTests(unittest.TestCase):
    """Verify the JSON contract survives serialize -> parse without field loss."""

    def test_two_entities_two_cutouts_roundtrip(self):
        original = two_entity_two_cutout_project()
        serialized = json.dumps(original, indent=2)
        reloaded = json.loads(serialized)

        self.assertEqual(reloaded["Name"], original["Name"])
        self.assertEqual(len(reloaded["Entities"]), 2)
        self.assertEqual(reloaded["Entities"][0]["Id"], original["Entities"][0]["Id"])
        self.assertEqual(reloaded["Entities"][1]["Prefab"], "test_barn_b")
        self.assertEqual(len(reloaded["NavMeshCutouts"]), 2)
        self.assertEqual(reloaded["NavMeshCutouts"][0]["Prefab"], "test_barn_a")
        self.assertEqual(reloaded["NavMeshCutouts"][1]["Prefab"], "test_barn_b")
        self.assertEqual(reloaded["NavMeshCutouts"][0]["Corners"], original["NavMeshCutouts"][0]["Corners"])
        self.assertEqual(reloaded["NavMeshCutouts"][1]["Corners"], original["NavMeshCutouts"][1]["Corners"])
        self.assertEqual(len(reloaded["NavMeshCutouts"][0]["FaceIndices"]), 1)

    def test_manifest_preserves_both_cutouts(self):
        project = two_entity_two_cutout_project()
        manifest = manifest_from_project(project)
        serialized = json.dumps(manifest, indent=2)
        reloaded = json.loads(serialized)

        self.assertEqual(reloaded["Format"], "CustomSceneCreator.NavMeshPlan/2")
        self.assertEqual(len(reloaded["Cutouts"]), 2)
        self.assertEqual(reloaded["Cutouts"][0]["EntityId"], project["NavMeshCutouts"][0]["EntityId"])
        self.assertEqual(reloaded["Cutouts"][1]["EntityId"], project["NavMeshCutouts"][1]["EntityId"])

    def test_multi_cutout_workflow_produces_valid_candidate(self):
        project = two_entity_two_cutout_project()
        manifest = manifest_from_project(project)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.bin"
            raw = one_quad_raw()
            source.write_bytes(struct.pack("<I", len(raw)) + raw)
            plan = root / "two.navcut.json"
            plan.write_text(json.dumps(manifest), encoding="utf-8")
            output = root / "candidate" / "navmesh.bin"
            result = navmesh_workflow.create_candidate(plan, output, source=source)

            self.assertEqual(result["cutouts_applied"], 2)
            self.assertEqual(len(result["operations"]), 2)
            self.assertEqual([op["prefab"] for op in result["operations"]], ["test_barn_a", "test_barn_b"])
            summary = navmesh_inspect.summarize(output)
            self.assertEqual(summary.connected_face_components, 1)
            self.assertEqual(summary.nonmanifold_edges, 0)


class RequirementBoundaryRoundTripTests(unittest.TestCase):
    """Verify ProjectNavMeshRequirement Boundary persistence in the JSON contract."""

    def test_requirement_with_boundary_survives_roundtrip(self):
        boundary = _ring(225.0, 678.0, 4.0, z=70.0)
        requirement = {
            "Id": "{CB3D76AB-4AFD-46D9-983A-1E8B7E017893}",
            "Label": "Navmesh needed",
            "Pos": [225.385483, 678.2148, 70.11629],
            "Radius": 4.0,
            "Boundary": boundary,
            "NearestFaceIndex": 22805,
            "NearestFaceDistance": 24.693264,
            "Created": "2026-08-16T20:30:01.8370593Z",
        }
        serialized = json.dumps(requirement, indent=2)
        reloaded = json.loads(serialized)

        self.assertIn("Boundary", reloaded)
        self.assertEqual(len(reloaded["Boundary"]), 48)  # 16 samples * 3 floats
        self.assertAlmostEqual(reloaded["Boundary"][0], boundary[0], places=5)
        self.assertAlmostEqual(reloaded["Boundary"][2], boundary[2], places=5)
        self.assertEqual(reloaded["Radius"], 4.0)
        self.assertEqual(reloaded["NearestFaceIndex"], 22805)

    def test_legacy_requirement_without_boundary_deserializes(self):
        """Older projects saved before the Boundary field was added."""
        legacy = {
            "Id": "{CB3D76AB-4AFD-46D9-983A-1E8B7E017893}",
            "Label": "Navmesh needed",
            "Pos": [225.385483, 678.2148, 70.11629],
            "Radius": 4.0,
            "NearestFaceIndex": 22805,
            "NearestFaceDistance": 24.693264,
            "Created": "2026-08-16T20:30:01.8370593Z",
        }
        serialized = json.dumps(legacy, indent=2)
        reloaded = json.loads(serialized)

        # The Boundary key is absent, which mirrors Newtonsoft.Json leaving the
        # C# default Array.Empty<float>() unchanged on deserialization.
        self.assertNotIn("Boundary", reloaded)
        self.assertEqual(reloaded["Radius"], 4.0)
        self.assertEqual(reloaded["Pos"], legacy["Pos"])

    def test_addition_plan_accepts_boundary_requirement(self):
        """The addition planner should see geometry_ready when Boundary is present."""
        import importlib.util
        spec = importlib.util.spec_from_file_location(
            "navmesh_addition_plan", TOOLS / "navmesh_addition_plan.py")
        assert spec and spec.loader
        planner = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = planner
        spec.loader.exec_module(planner)

        boundary = _ring(12, 5, 2, z=0.0)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            navmesh = root / "navmesh.bin"
            raw = one_quad_raw()
            navmesh.write_bytes(struct.pack("<I", len(raw)) + raw)
            manifest = root / "plan.json"
            manifest.write_text(json.dumps({
                "Format": "CustomSceneCreator.NavMeshPlan/2",
                "Scene": "test",
                "RequiredAreas": [{
                    "Id": "one",
                    "Pos": [12, 5, 0],
                    "Radius": 2,
                    "Boundary": boundary,
                }],
            }), encoding="utf-8")
            result = planner.build_addition_plan(navmesh, manifest)

        self.assertTrue(result["all_geometry_ready"])
        self.assertEqual(result["requirements"][0]["boundary_samples"], 16)

    def test_addition_plan_reports_legacy_requirement_not_ready(self):
        """The addition planner should flag missing Boundary as not geometry-ready."""
        import importlib.util
        spec = importlib.util.spec_from_file_location(
            "navmesh_addition_plan", TOOLS / "navmesh_addition_plan.py")
        assert spec and spec.loader
        planner = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = planner
        spec.loader.exec_module(planner)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            navmesh = root / "navmesh.bin"
            raw = one_quad_raw()
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


class RealFixtureRegressionTests(unittest.TestCase):
    """Validate the real saved fixtures from the handoff maintain their contracts."""

    def test_battle_terrain_008_project_has_two_entities_two_cutouts(self):
        path = Path(r"D:\Users\cball\Documents\Mount and Blade II Bannerlord"
                    r"\CustomSceneCreator\projects\battle_terrain_008.json")
        if not path.is_file():
            self.skipTest("real fixture not available on this machine")
        project = json.loads(path.read_text(encoding="utf-8-sig"))
        self.assertEqual(len(project["Entities"]), 2)
        self.assertEqual(len(project["NavMeshCutouts"]), 2)
        cutout_prefabs = {c["Prefab"] for c in project["NavMeshCutouts"]}
        self.assertEqual(cutout_prefabs, {"arabian_house_new_a", "arabian_house_new_c"})
        self.assertEqual(len(project["NavMeshRequirements"]), 0)

    def test_battle_terrain_005_project_navmesh_authoring_is_well_formed(self):
        path = Path(r"D:\Users\cball\Documents\Mount and Blade II Bannerlord"
                    r"\CustomSceneCreator\projects\battle_terrain_005.json")
        if not path.is_file():
            self.skipTest("real fixture not available on this machine")
        project = json.loads(path.read_text(encoding="utf-8-sig"))
        self.assertGreaterEqual(len(project["Entities"]), 1)
        for cutout in project.get("NavMeshCutouts", []):
            self.assertEqual(len(cutout["Corners"]), 12)
        for req in project.get("NavMeshRequirements", []):
            self.assertIn("Boundary", req, "Boundary should be present after re-save with updated build")
            self.assertEqual(len(req["Boundary"]), 48)  # 16 samples * 3 floats
        for ramp in project.get("NavMeshRamps", []):
            self.assertFalse(ramp.get("IsDraft", False))
            self.assertGreaterEqual(len(ramp.get("Outline", [])), 12)
            self.assertEqual(len(ramp["Outline"]) % 3, 0)

    def test_battle_terrain_005_manifest_matches_current_project(self):
        path = Path(r"D:\Users\cball\Documents\Mount and Blade II Bannerlord"
                    r"\CustomSceneCreator\exports\navmesh-cutouts\battle_terrain_005.navcut.json")
        if not path.is_file():
            self.skipTest("real fixture not available on this machine")
        manifest = json.loads(path.read_text(encoding="utf-8-sig"))
        project_path = Path(r"D:\Users\cball\Documents\Mount and Blade II Bannerlord"
                            r"\CustomSceneCreator\projects\battle_terrain_005.json")
        if not project_path.is_file():
            self.skipTest("matching project fixture not available on this machine")
        project = json.loads(project_path.read_text(encoding="utf-8-sig"))
        self.assertEqual(manifest["Format"], "CustomSceneCreator.NavMeshPlan/2")
        self.assertEqual(len(manifest["Cutouts"]), len(project.get("NavMeshCutouts", [])))
        self.assertEqual(len(manifest["RequiredAreas"]), len(project.get("NavMeshRequirements", [])))
        self.assertEqual(len(manifest.get("Ramps", [])),
                         len([r for r in project.get("NavMeshRamps", [])
                              if not r.get("IsDraft", False)]))
        for req in manifest["RequiredAreas"]:
            self.assertIn("Boundary", req)


if __name__ == "__main__":
    unittest.main()
