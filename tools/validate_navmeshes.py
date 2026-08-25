#!/usr/bin/env python3
"""Recursively validate NMG8/NMG9 navmeshes without changing them.

The validator checks container decoding, references, edge/face topology, and an
exact parse/serialize round trip. Older NMG revisions and unrelated .bin files are
reported as skipped rather than treated as corrupt.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import navmesh_inspect


def validate(path: Path) -> tuple[str, str]:
    try:
        _, raw, _ = navmesh_inspect.read_raw(path)
        if raw[:4] not in (b"NMG8", b"NMG9"):
            return "skipped", raw[:4].decode("ascii", "replace")
        vertices, edges, face_count, face_offset = navmesh_inspect._leading_arrays(raw)
        faces, tail = navmesh_inspect._faces(raw, face_count, face_offset)
        rebuilt = navmesh_inspect.serialize_raw(raw[:4], vertices, edges, faces, tail)
        if rebuilt != raw:
            return "failed", "parse/serialize round trip changed bytes"
        summary = navmesh_inspect.summarize(path)
        problems = {
            "non_finite_vertices": not summary.finite_vertices,
            "invalid_edge_endpoints": summary.invalid_edge_endpoints,
            "self_edges": summary.self_edges,
            "duplicate_edges": summary.duplicate_edges,
            "invalid_face_references": summary.invalid_face_references,
            "invalid_face_boundaries": summary.invalid_face_boundaries,
            "nonmanifold_edges": summary.nonmanifold_edges,
        }
        problems = {key: value for key, value in problems.items() if value}
        if problems:
            return "failed", json.dumps(problems, sort_keys=True)
        return "passed", f"{summary.vertices}v/{summary.edges}e/{summary.faces}f"
    except (OSError, navmesh_inspect.NavMeshFormatError) as error:
        message = str(error)
        if "not an RNM1" in message or "unsupported inner signature" in message:
            return "skipped", message
        return "failed", message


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("roots", nargs="+", type=Path)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--limit", type=int, help="stop after this many candidate files")
    args = parser.parse_args()

    results = []
    seen: set[Path] = set()
    for root in args.roots:
        candidates = (
            [root]
            if root.is_file()
            else (
                path
                for path in root.rglob("*.bin")
                if path.name.lower() == "navmesh.bin"
                or "navmeshprefabs" in {part.lower() for part in path.parts}
            )
        )
        for path in candidates:
            if args.limit is not None and len(results) >= args.limit:
                break
            resolved = path.resolve()
            if resolved in seen:
                continue
            seen.add(resolved)
            status, detail = validate(path)
            results.append({"path": str(resolved), "status": status, "detail": detail})
        if args.limit is not None and len(results) >= args.limit:
            break

    counts = {status: sum(item["status"] == status for item in results) for status in ("passed", "failed", "skipped")}
    if args.json:
        print(json.dumps({"counts": counts, "results": results}, indent=2))
    else:
        for item in results:
            if item["status"] == "failed":
                print(f"FAILED\t{item['path']}\t{item['detail']}")
        print(" ".join(f"{key}={value}" for key, value in counts.items()))
    return 1 if counts["failed"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
