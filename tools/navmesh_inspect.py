#!/usr/bin/env python3
"""Read-only inspection and comparison of Bannerlord NMG navmesh files.

It validates the container, decompresses the raw NMG stream, and parses the proven sections:

    NMG8 | vertex_count | Vec3[] | edge_count | 5 * int32[] | faces | tail
    NMG9 | vertex_count | Vec3[] | edge_count | 6 * int32[] | faces | tail

Scene navmeshes normally use an RNM1/LZ4 wrapper. Navmesh prefabs use a four-byte
length prefix followed directly by NMG. Raw streams are accepted too. The structural
parser supports NMG8 and NMG9; older revisions are rejected instead of misinterpreted.

No input file is ever modified. Use --raw-out only to write the decoded NMG bytes
to a new path. --roundtrip-out writes a separately named, locally verified copy.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Optional


class NavMeshFormatError(ValueError):
    pass


@dataclass(frozen=True)
class NavMeshSummary:
    path: str
    container: str
    format: str
    compressed_bytes: int
    raw_bytes: int
    compressed_sha256: str
    raw_sha256: str
    vertices: int
    edges: int
    faces: int
    vertex_offset: int
    edge_offset: int
    face_offset: int
    trailing_face_bytes: int
    finite_vertices: bool
    invalid_edge_endpoints: int
    self_edges: int
    duplicate_edges: int
    face_degrees: dict[int, int]
    invalid_face_references: int
    invalid_face_boundaries: int
    global_tail_bytes: int
    global_tail_sha256: str
    unused_vertices: int
    unused_edges: int
    nonmanifold_edges: int
    connected_face_components: int


@dataclass(frozen=True)
class NavMeshComparison:
    left: NavMeshSummary
    right: NavMeshSummary
    added_vertices: list[tuple[float, float, float]]
    removed_vertices: list[tuple[float, float, float]]
    added_edges: list[tuple[int, int, int, int, int, int]]
    removed_edges: list[tuple[int, int, int, int, int, int]]
    added_faces: list[tuple[tuple[float, float, float], ...]]
    removed_faces: list[tuple[tuple[float, float, float], ...]]


@dataclass(frozen=True)
class NmgFace:
    vertices: tuple[int, ...]
    edges: tuple[int, ...]
    metadata: tuple[int, int, int, int, int]
    direction: int


def _u32(data: bytes, offset: int) -> int:
    if offset < 0 or offset + 4 > len(data):
        raise NavMeshFormatError(f"u32 outside file at 0x{offset:X}")
    return struct.unpack_from("<I", data, offset)[0]


def _u64(data: bytes, offset: int) -> int:
    if offset < 0 or offset + 8 > len(data):
        raise NavMeshFormatError(f"u64 outside file at 0x{offset:X}")
    return struct.unpack_from("<Q", data, offset)[0]


def decompress_lz4_block(source: bytes, expected_size: int) -> bytes:
    """Decode the raw LZ4 block stored after Bannerlord's 48-byte RNM1 header."""
    output = bytearray()
    cursor = 0
    while cursor < len(source):
        token = source[cursor]
        cursor += 1

        literal_length = token >> 4
        if literal_length == 15:
            while True:
                if cursor >= len(source):
                    raise NavMeshFormatError("truncated LZ4 literal length")
                extension = source[cursor]
                cursor += 1
                literal_length += extension
                if extension != 255:
                    break
        literal_end = cursor + literal_length
        if literal_end > len(source):
            raise NavMeshFormatError("truncated LZ4 literal")
        output.extend(source[cursor:literal_end])
        cursor = literal_end

        # A final literal-only sequence has no match offset.
        if cursor == len(source):
            break
        if cursor + 2 > len(source):
            raise NavMeshFormatError("truncated LZ4 match offset")
        match_offset = source[cursor] | (source[cursor + 1] << 8)
        cursor += 2
        if match_offset == 0 or match_offset > len(output):
            raise NavMeshFormatError(f"invalid LZ4 match offset {match_offset}")

        match_length = token & 0x0F
        if match_length == 15:
            while True:
                if cursor >= len(source):
                    raise NavMeshFormatError("truncated LZ4 match length")
                extension = source[cursor]
                cursor += 1
                match_length += extension
                if extension != 255:
                    break
        match_length += 4
        match_start = len(output) - match_offset
        for index in range(match_length):
            output.append(output[match_start + index])

    if len(output) != expected_size:
        raise NavMeshFormatError(
            f"decompressed size {len(output)} != RNM1 declaration {expected_size}"
        )
    return bytes(output)


def compress_lz4_literal_block(raw: bytes) -> bytes:
    """Encode one valid final literal-only LZ4 sequence.

    This favors a tiny, auditable implementation over compression ratio. It is used
    only for copied-file round-trip tests until mutation is proven safe.
    """
    length = len(raw)
    token = min(length, 15) << 4
    output = bytearray((token,))
    if length >= 15:
        remaining = length - 15
        while remaining >= 255:
            output.append(255)
            remaining -= 255
        output.append(remaining)
    output.extend(raw)
    return bytes(output)


def _append_lz4_length(output: bytearray, extra: int) -> None:
    while extra >= 255:
        output.append(255)
        extra -= 255
    output.append(extra)


def compress_lz4_block(raw: bytes) -> bytes:
    """Encode a deterministic raw LZ4 block with a small greedy hash matcher."""
    if len(raw) < 13:
        return compress_lz4_literal_block(raw)
    output = bytearray()
    latest: dict[int, int] = {}
    anchor = 0
    cursor = 0
    match_limit = len(raw) - 12  # Preserve the conventional five-literal final sequence.
    while cursor <= match_limit:
        key = int.from_bytes(raw[cursor : cursor + 4], "little")
        previous = latest.get(key)
        latest[key] = cursor
        if (
            previous is None
            or cursor - previous > 65535
            or raw[previous : previous + 4] != raw[cursor : cursor + 4]
        ):
            cursor += 1
            continue

        match_length = 4
        while (
            cursor + match_length < len(raw) - 5
            and raw[previous + match_length] == raw[cursor + match_length]
        ):
            match_length += 1
        literal_length = cursor - anchor
        encoded_match_length = match_length - 4
        output.append((min(literal_length, 15) << 4) | min(encoded_match_length, 15))
        if literal_length >= 15:
            _append_lz4_length(output, literal_length - 15)
        output.extend(raw[anchor:cursor])
        output.extend(struct.pack("<H", cursor - previous))
        if encoded_match_length >= 15:
            _append_lz4_length(output, encoded_match_length - 15)

        match_end = cursor + match_length
        cursor += 1
        while cursor < match_end:
            if cursor + 4 <= len(raw):
                latest[int.from_bytes(raw[cursor : cursor + 4], "little")] = cursor
            cursor += 1
        anchor = match_end

    final_literals = raw[anchor:]
    literal_length = len(final_literals)
    output.append(min(literal_length, 15) << 4)
    if literal_length >= 15:
        _append_lz4_length(output, literal_length - 15)
    output.extend(final_literals)
    return bytes(output)


def encode_container(raw: bytes, original: bytes, wrapper: str) -> bytes:
    if wrapper == "RNM1":
        if len(original) < 48 or original[:4] != b"RNM1":
            raise NavMeshFormatError("cannot preserve invalid RNM1 header")
        compressed = compress_lz4_block(raw)
        header = bytearray(original[:48])
        total = 48 + len(compressed)
        struct.pack_into("<I", header, 4, total - 8)
        struct.pack_into("<Q", header, 16, len(raw))
        struct.pack_into("<Q", header, 24, total - 8)
        return bytes(header) + compressed
    if wrapper == "length-prefixed":
        return struct.pack("<I", len(raw)) + raw
    if wrapper == "raw":
        return raw
    raise NavMeshFormatError(f"unsupported container wrapper {wrapper!r}")


def write_roundtrip_copy(path: Path, output: Path) -> Path:
    if output.resolve() == path.resolve():
        raise NavMeshFormatError("round-trip output must not overwrite the input")
    if output.exists():
        raise NavMeshFormatError("round-trip output already exists")
    original, raw, wrapper = read_raw(path)
    vertices, edges, face_count, face_offset = _leading_arrays(raw)
    faces, tail = _faces(raw, face_count, face_offset)
    rebuilt_raw = serialize_raw(raw[:4], vertices, edges, faces, tail)
    if rebuilt_raw != raw:
        raise NavMeshFormatError("parse/serialize verification changed decoded bytes")
    encoded = encode_container(rebuilt_raw, original, wrapper)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(encoded)
    _, verification_raw, _ = read_raw(output)
    if verification_raw != raw:
        output.unlink(missing_ok=True)
        raise NavMeshFormatError("written copy failed read-back verification")
    return output


def read_raw(path: Path) -> tuple[bytes, bytes, str]:
    container = path.read_bytes()
    wrapper: str
    if len(container) >= 49 and container[:4] == b"RNM1":
        if _u32(container, 4) != len(container) - 8:
            raise NavMeshFormatError("RNM1 compressed-length field does not match file")
        if _u64(container, 24) != len(container) - 8:
            raise NavMeshFormatError("RNM1 mirrored compressed-length field does not match file")
        raw = decompress_lz4_block(container[48:], _u64(container, 16))
        wrapper = "RNM1"
    elif len(container) >= 8 and container[4:8] in (b"NMG8", b"NMG9"):
        if _u32(container, 0) != len(container) - 4:
            raise NavMeshFormatError("prefab length prefix does not match file")
        raw = container[4:]
        wrapper = "length-prefixed"
    elif len(container) >= 4 and container[:4] in (b"NMG8", b"NMG9"):
        raw = container
        wrapper = "raw"
    else:
        raise NavMeshFormatError("not an RNM1, length-prefixed, or raw NMG8/NMG9 file")
    if raw[:4] not in (b"NMG8", b"NMG9"):
        raise NavMeshFormatError(f"unsupported inner signature {raw[:4]!r}")
    return container, raw, wrapper


def _leading_arrays(raw: bytes) -> tuple[
    list[tuple[float, float, float]],
    list[tuple[int, int, int, int, int, int]],
    int,
    int,
]:
    vertex_count = _u32(raw, 4)
    vertex_offset = 8
    signature = raw[:4]
    if signature not in (b"NMG8", b"NMG9"):
        raise NavMeshFormatError(f"unsupported inner signature {signature!r}")
    edge_width = 20 if signature == b"NMG8" else 24
    edge_count_offset = vertex_offset + vertex_count * 12
    edge_count = _u32(raw, edge_count_offset)
    edge_offset = edge_count_offset + 4
    face_count_offset = edge_offset + edge_count * edge_width
    face_count = _u32(raw, face_count_offset)
    face_offset = face_count_offset + 4
    if face_offset > len(raw):
        raise NavMeshFormatError("leading NMG arrays extend past decoded data")

    vertices = list(struct.iter_unpack("<3f", raw[vertex_offset:edge_count_offset]))
    if signature == b"NMG8":
        edges = [(*edge, 0) for edge in struct.iter_unpack("<5i", raw[edge_offset:face_count_offset])]
    else:
        edges = list(struct.iter_unpack("<6i", raw[edge_offset:face_count_offset]))
    return vertices, edges, face_count, face_offset


def _faces(
    raw: bytes,
    face_count: int,
    face_offset: int,
) -> tuple[list[NmgFace], bytes]:
    """Decode the self-sized NMG9 face records and preserve the global tail."""
    faces: list[NmgFace] = []
    cursor = face_offset
    for face_index in range(face_count):
        degree = _u32(raw, cursor)
        if degree < 3 or degree > 64:
            raise NavMeshFormatError(
                f"invalid face degree {degree} for face {face_index} at 0x{cursor:X}"
            )
        cursor += 4
        record_end = cursor + degree * 8 + 21
        if record_end > len(raw):
            raise NavMeshFormatError(f"truncated face {face_index} at 0x{cursor - 4:X}")
        vertices = struct.unpack_from(f"<{degree}I", raw, cursor)
        cursor += degree * 4
        edges = struct.unpack_from(f"<{degree}I", raw, cursor)
        cursor += degree * 4
        metadata = struct.unpack_from("<5i", raw, cursor)
        cursor += 20
        direction = raw[cursor]
        cursor += 1
        faces.append(NmgFace(vertices, edges, metadata, direction))
    return faces, raw[cursor:]


def serialize_raw(
    signature: bytes,
    vertices: list[tuple[float, float, float]],
    edges: list[tuple[int, int, int, int, int, int]],
    faces: list[NmgFace],
    global_tail: bytes,
) -> bytes:
    """Serialize the fully decoded NMG leading arrays and face records."""
    if signature not in (b"NMG8", b"NMG9"):
        raise NavMeshFormatError(f"unsupported inner signature {signature!r}")
    output = bytearray(signature)
    output.extend(struct.pack("<I", len(vertices)))
    for vertex in vertices:
        output.extend(struct.pack("<3f", *vertex))
    output.extend(struct.pack("<I", len(edges)))
    for edge in edges:
        output.extend(struct.pack("<5i" if signature == b"NMG8" else "<6i", *edge[:5] if signature == b"NMG8" else edge))
    output.extend(struct.pack("<I", len(faces)))
    for face in faces:
        degree = len(face.vertices)
        if degree != len(face.edges):
            raise NavMeshFormatError("face vertex and edge loops have different lengths")
        output.extend(struct.pack("<I", degree))
        output.extend(struct.pack(f"<{degree}I", *face.vertices))
        output.extend(struct.pack(f"<{degree}I", *face.edges))
        output.extend(struct.pack("<5i", *face.metadata))
        output.append(face.direction)
    output.extend(global_tail)
    return bytes(output)


def _canonical_polygon(
    face: NmgFace,
    vertices: list[tuple[float, float, float]],
) -> tuple[tuple[float, float, float], ...]:
    points = tuple(vertices[index] for index in face.vertices)
    rotations = []
    for sequence in (points, tuple(reversed(points))):
        rotations.extend(sequence[index:] + sequence[:index] for index in range(len(sequence)))
    return min(rotations)


def summarize(path: Path, raw_out: Optional[Path] = None) -> NavMeshSummary:
    container, raw, wrapper = read_raw(path)
    vertices, edges, face_count, face_offset = _leading_arrays(raw)
    faces, global_tail = _faces(raw, face_count, face_offset)
    vertex_count = len(vertices)
    vertex_offset = 8
    edge_count_offset = vertex_offset + vertex_count * 12
    edge_count = len(edges)
    edge_offset = edge_count_offset + 4

    invalid_edge_endpoints = sum(
        1
        for edge in edges
        if edge[1] < 0 or edge[1] >= vertex_count or edge[2] < 0 or edge[2] >= vertex_count
    )
    self_edges = sum(1 for edge in edges if edge[1] == edge[2])
    normalized_edges = [(min(edge[1], edge[2]), max(edge[1], edge[2])) for edge in edges]
    duplicate_edges = len(normalized_edges) - len(set(normalized_edges))
    face_degrees: dict[int, int] = {}
    invalid_face_references = 0
    invalid_face_boundaries = 0
    used_vertices: set[int] = set()
    edge_face_counts = [0] * edge_count
    face_adjacency: list[list[int]] = [[] for _ in faces]
    first_face_for_edge: dict[int, int] = {}
    for face_index, face in enumerate(faces):
        degree = len(face.vertices)
        face_degrees[degree] = face_degrees.get(degree, 0) + 1
        if any(vertex >= vertex_count for vertex in face.vertices) or any(
            edge >= edge_count for edge in face.edges
        ):
            invalid_face_references += 1
            continue
        used_vertices.update(face.vertices)
        for index, edge_index in enumerate(face.edges):
            edge_face_counts[edge_index] += 1
            other = first_face_for_edge.setdefault(edge_index, face_index)
            if other != face_index:
                face_adjacency[face_index].append(other)
                face_adjacency[other].append(face_index)
            expected = {face.vertices[index], face.vertices[(index + 1) % degree]}
            actual = {edges[edge_index][1], edges[edge_index][2]}
            if expected != actual:
                invalid_face_boundaries += 1
                break
    visited: set[int] = set()
    connected_face_components = 0
    for start in range(len(faces)):
        if start in visited:
            continue
        connected_face_components += 1
        stack = [start]
        visited.add(start)
        while stack:
            for neighbor in face_adjacency[stack.pop()]:
                if neighbor not in visited:
                    visited.add(neighbor)
                    stack.append(neighbor)

    if raw_out is not None:
        if raw_out.resolve() == path.resolve():
            raise NavMeshFormatError("--raw-out must not overwrite the input")
        raw_out.parent.mkdir(parents=True, exist_ok=True)
        raw_out.write_bytes(raw)

    return NavMeshSummary(
        path=str(path.resolve()),
        container=wrapper,
        format=raw[:4].decode("ascii"),
        compressed_bytes=len(container),
        raw_bytes=len(raw),
        compressed_sha256=hashlib.sha256(container).hexdigest().upper(),
        raw_sha256=hashlib.sha256(raw).hexdigest().upper(),
        vertices=vertex_count,
        edges=edge_count,
        faces=face_count,
        vertex_offset=vertex_offset,
        edge_offset=edge_offset,
        face_offset=face_offset,
        trailing_face_bytes=len(raw) - face_offset,
        finite_vertices=all(math.isfinite(value) for vertex in vertices for value in vertex),
        invalid_edge_endpoints=invalid_edge_endpoints,
        self_edges=self_edges,
        duplicate_edges=duplicate_edges,
        face_degrees=face_degrees,
        invalid_face_references=invalid_face_references,
        invalid_face_boundaries=invalid_face_boundaries,
        global_tail_bytes=len(global_tail),
        global_tail_sha256=hashlib.sha256(global_tail).hexdigest().upper(),
        unused_vertices=vertex_count - len(used_vertices),
        unused_edges=sum(count == 0 for count in edge_face_counts),
        nonmanifold_edges=sum(count > 2 for count in edge_face_counts),
        connected_face_components=connected_face_components,
    )


def compare(left_path: Path, right_path: Path) -> NavMeshComparison:
    left_container, left_raw, _ = read_raw(left_path)
    right_container, right_raw, _ = read_raw(right_path)
    del left_container, right_container
    if left_raw[:4] != b"NMG9" or right_raw[:4] != b"NMG9":
        raise NavMeshFormatError("structural comparison requires two NMG9 files")
    left_vertices, left_edges, left_face_count, left_face_offset = _leading_arrays(left_raw)
    right_vertices, right_edges, right_face_count, right_face_offset = _leading_arrays(right_raw)
    left_faces, _ = _faces(left_raw, left_face_count, left_face_offset)
    right_faces, _ = _faces(right_raw, right_face_count, right_face_offset)

    left_vertex_set = set(left_vertices)
    right_vertex_set = set(right_vertices)
    left_edge_set = set(left_edges)
    right_edge_set = set(right_edges)
    left_face_set = {_canonical_polygon(face, left_vertices) for face in left_faces}
    right_face_set = {_canonical_polygon(face, right_vertices) for face in right_faces}
    return NavMeshComparison(
        left=summarize(left_path),
        right=summarize(right_path),
        added_vertices=sorted(right_vertex_set - left_vertex_set),
        removed_vertices=sorted(left_vertex_set - right_vertex_set),
        added_edges=sorted(right_edge_set - left_edge_set),
        removed_edges=sorted(left_edge_set - right_edge_set),
        added_faces=sorted(right_face_set - left_face_set),
        removed_faces=sorted(left_face_set - right_face_set),
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("navmesh", type=Path)
    parser.add_argument("--compare", type=Path, help="compare proven vertex/edge sections")
    parser.add_argument("--json", action="store_true", help="emit machine-readable JSON")
    parser.add_argument("--raw-out", type=Path, help="write decompressed NMG bytes to a new file")
    parser.add_argument(
        "--roundtrip-out",
        type=Path,
        help="write a separately named, read-back-verified NMG9 container copy",
    )
    args = parser.parse_args()
    try:
        if args.compare and (args.raw_out or args.roundtrip_out):
            raise NavMeshFormatError("--compare cannot be combined with output options")
        result = compare(args.navmesh, args.compare) if args.compare else summarize(args.navmesh, args.raw_out)
        if args.roundtrip_out:
            write_roundtrip_copy(args.navmesh, args.roundtrip_out)
    except (OSError, NavMeshFormatError) as error:
        parser.error(str(error))
    if args.json:
        print(json.dumps(asdict(result), indent=2))
    else:
        for key, value in asdict(result).items():
            print(f"{key}: {value}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
