#!/usr/bin/env python3
"""Create an isolated NMG9 candidate directly from a CSC navmesh-plan manifest."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import tempfile

import navmesh_apply_cutout
import navmesh_cutout
import navmesh_inspect


SUPPORTED_FORMAT = "CustomSceneCreator.NavMeshPlan/2"


def _safe_name(value: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "_", value.strip()).strip("._")
    return cleaned or "navmesh_candidate"


def load_manifest(path: Path) -> dict:
    document = json.loads(path.read_text(encoding="utf-8-sig"))
    if document.get("Format") != SUPPORTED_FORMAT:
        raise navmesh_inspect.NavMeshFormatError(
            f"unsupported manifest format {document.get('Format')!r}"
        )
    if not str(document.get("Scene", "")).strip():
        raise navmesh_inspect.NavMeshFormatError("manifest has no Scene")
    cutouts = document.get("Cutouts")
    if not isinstance(cutouts, list) or len(cutouts) < 1:
        count = len(cutouts) if isinstance(cutouts, list) else 0
        raise navmesh_inspect.NavMeshFormatError(
            f"the workflow requires at least one cutout; manifest has {count}"
        )
    return document


def default_modules_roots() -> list[Path]:
    roots: list[Path] = []
    configured = os.environ.get("BANNERLORD_MODULES_ROOT")
    if configured:
        roots.append(Path(configured))
    roots.extend([
        Path(r"F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\Modules"),
        Path(r"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules"),
        Path(r"C:\Program Files\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules"),
    ])
    unique: list[Path] = []
    seen: set[str] = set()
    for root in roots:
        key = str(root).casefold()
        if key not in seen and root.is_dir():
            unique.append(root)
            seen.add(key)
    return unique


def locate_scene_navmesh(scene: str, modules_roots: list[Path]) -> Path:
    matches: list[Path] = []
    for root in modules_roots:
        matches.extend(root.glob(f"*/SceneObj/{scene}/navmesh.bin"))
    unique = sorted({match.resolve() for match in matches}, key=lambda path: str(path).casefold())
    if len(unique) != 1:
        detail = "\n".join(f"  {path}" for path in unique) or "  (none)"
        raise navmesh_inspect.NavMeshFormatError(
            f"expected exactly one navmesh for scene {scene!r}, found {len(unique)}:\n{detail}\n"
            "Pass --source to select the intended file explicitly."
        )
    return unique[0]


def default_output(
    manifest: dict,
    now: datetime | None = None,
    manifest_path: Path | None = None,
) -> Path:
    stamp = (now or datetime.now(timezone.utc)).strftime("%Y%m%dT%H%M%SZ")
    project = _safe_name(str(manifest.get("Project") or manifest["Scene"]))
    if manifest_path is not None and manifest_path.parent.name.casefold() == "navmesh-cutouts":
        exports = manifest_path.parent.parent
    else:
        documents = Path.home() / "Documents" / "Mount and Blade II Bannerlord" / "CustomSceneCreator"
        exports = documents / "exports"
    return exports / "navmesh-candidates" / f"{project}_{stamp}" / "navmesh.bin"


def create_candidate(
    manifest_path: Path,
    output: Path,
    source: Path | None = None,
    modules_roots: list[Path] | None = None,
) -> dict:
    manifest = load_manifest(manifest_path)
    scene = str(manifest["Scene"]).strip()
    if source is None:
        source = locate_scene_navmesh(scene, modules_roots or default_modules_roots())
    if not source.is_file():
        raise navmesh_inspect.NavMeshFormatError(f"source navmesh does not exist: {source}")
    report_path = output.with_name("navmesh_candidate_report.json")
    if report_path.exists():
        raise navmesh_inspect.NavMeshFormatError("candidate report already exists")

    original_source = source.resolve()
    source_summary = navmesh_inspect.summarize(source)
    operations: list[dict] = []
    with tempfile.TemporaryDirectory(prefix="csc_navmesh_") as temporary:
        current = source
        cutouts = manifest["Cutouts"]
        for cutout_index, cutout in enumerate(cutouts):
            authored = navmesh_cutout.manifest_corner_xyz(manifest_path, cutout_index)
            xy = tuple((corner[0], corner[1]) for corner in authored)
            height_band = navmesh_cutout.manifest_height_band(manifest_path, cutout_index)
            projected = navmesh_cutout.build_plan(current, xy, *height_band).projected_corners_xyz
            z_deltas = tuple(projected[index][2] - authored[index][2] for index in range(len(authored)))
            is_last = cutout_index == len(cutouts) - 1
            step_output = output if is_last else Path(temporary) / f"step_{cutout_index + 1}.bin"
            step = navmesh_apply_cutout.apply_cutout(current, step_output, projected, *height_band)
            operations.append({
                "cutout_index": cutout_index,
                "entity_id": str(cutout.get("EntityId", "")),
                "prefab": str(cutout.get("Prefab", "")),
                "affected_faces_removed": step["affected_faces_removed"],
                "repair_faces_added": step["repair_faces_added"],
                "projected_corners_xyz": projected,
                "authored_to_projected_z_delta": z_deltas,
                "max_abs_z_delta": max(abs(value) for value in z_deltas),
                "triangulation_area_error": step["triangulation_area_error"],
            })
            current = step_output

    final_summary = navmesh_inspect.summarize(output)
    result = {
        "source": str(original_source),
        "output": str(output.resolve()),
        "manifest": str(manifest_path.resolve()),
        "manifest_format": manifest["Format"],
        "project": str(manifest.get("Project", "")),
        "scene": scene,
        "cutouts_applied": len(operations),
        "required_areas_recorded_not_applied": len(manifest.get("RequiredAreas") or []),
        "affected_faces_removed_total": sum(op["affected_faces_removed"] for op in operations),
        "repair_faces_added_total": sum(op["repair_faces_added"] for op in operations),
        "vertices_before": source_summary.vertices,
        "vertices_after": final_summary.vertices,
        "edges_before": source_summary.edges,
        "edges_after": final_summary.edges,
        "faces_before": source_summary.faces,
        "faces_after": final_summary.faces,
        "connected_face_components": final_summary.connected_face_components,
        "retained_unused_vertices": final_summary.unused_vertices,
        "retained_unused_edges": final_summary.unused_edges,
        "nonmanifold_edges": final_summary.nonmanifold_edges,
        "max_abs_z_delta": max(op["max_abs_z_delta"] for op in operations),
        "output_sha256": final_summary.compressed_sha256,
        "raw_sha256": final_summary.raw_sha256,
        "operations": operations,
    }
    try:
        report_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    except Exception:
        output.unlink(missing_ok=True)
        raise
    result["report"] = str(report_path.resolve())
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--source", type=Path, help="explicit source navmesh.bin")
    parser.add_argument("--modules-root", action="append", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        manifest = load_manifest(args.manifest)
        output = args.output or default_output(manifest, manifest_path=args.manifest)
        result = create_candidate(
            args.manifest, output, source=args.source, modules_roots=args.modules_root
        )
        print(json.dumps(result, indent=2))
    except (OSError, ValueError, json.JSONDecodeError, navmesh_inspect.NavMeshFormatError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
