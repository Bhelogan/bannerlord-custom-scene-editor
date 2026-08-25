import importlib.util
import struct
import sys
import tempfile
import unittest
import random
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("navmesh_inspect.py")
SPEC = importlib.util.spec_from_file_location("navmesh_inspect", MODULE_PATH)
assert SPEC and SPEC.loader
navmesh_inspect = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = navmesh_inspect
SPEC.loader.exec_module(navmesh_inspect)


class Lz4BlockTests(unittest.TestCase):
    def test_final_literal_sequence(self):
        source = bytes([0x50]) + b"NMG9!"
        self.assertEqual(navmesh_inspect.decompress_lz4_block(source, 5), b"NMG9!")

    def test_overlapping_match(self):
        # Four literals, then an eight-byte overlapping copy from four bytes back.
        source = bytes([0x44]) + b"ABCD" + bytes([4, 0])
        self.assertEqual(
            navmesh_inspect.decompress_lz4_block(source, 12), b"ABCDABCDABCD"
        )

    def test_extended_literal_length(self):
        payload = b"X" * 300
        source = bytes([0xF0, 255, 30]) + payload
        self.assertEqual(navmesh_inspect.decompress_lz4_block(source, 300), payload)

    def test_literal_encoder_round_trip(self):
        for payload in (b"", b"NMG9", b"X" * 15, b"ABCD" * 1000):
            encoded = navmesh_inspect.compress_lz4_literal_block(payload)
            self.assertEqual(
                navmesh_inspect.decompress_lz4_block(encoded, len(payload)), payload
            )

    def test_greedy_encoder_round_trip(self):
        randomizer = random.Random(104)
        payloads = (
            b"",
            b"NMG9",
            b"ABCD" * 1000,
            bytes(randomizer.randrange(256) for _ in range(4096)),
            (b"vertex-edge-face-" * 10000) + bytes(range(256)),
        )
        for payload in payloads:
            encoded = navmesh_inspect.compress_lz4_block(payload)
            self.assertEqual(navmesh_inspect.decompress_lz4_block(encoded, len(payload)), payload)

    def test_rejects_zero_match_offset(self):
        with self.assertRaises(navmesh_inspect.NavMeshFormatError):
            navmesh_inspect.decompress_lz4_block(bytes([0x10]) + b"A\0\0", 5)


class ContainerTests(unittest.TestCase):
    def test_nmg8_parse_serialize_is_lossless(self):
        vertices = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)]
        edges = [(-1, 0, 1, -1, -1, 0), (-1, 1, 2, -1, -1, 0), (-1, 2, 0, -1, -1, 0)]
        face = navmesh_inspect.NmgFace((0, 1, 2), (0, 1, 2), (2, 0, 0, 0, 15), 0)
        raw = navmesh_inspect.serialize_raw(b"NMG8", vertices, edges, [face], bytes(260))
        parsed_vertices, parsed_edges, count, offset = navmesh_inspect._leading_arrays(raw)
        parsed_faces, tail = navmesh_inspect._faces(raw, count, offset)
        self.assertEqual(navmesh_inspect.serialize_raw(b"NMG8", parsed_vertices, parsed_edges, parsed_faces, tail), raw)

    @staticmethod
    def _one_face_raw() -> bytes:
        vertices = struct.pack("<12f", *(float(value) for value in range(12)))
        edges = b"".join(
            struct.pack("<6i", -1, a, b, -1, -1, 0)
            for a, b in ((0, 1), (1, 2), (2, 3), (3, 0))
        )
        face = (
            struct.pack("<I", 4)
            + struct.pack("<4I", 0, 1, 2, 3)
            + struct.pack("<4I", 0, 1, 2, 3)
            + struct.pack("<5iB", 2, 0, 4, 0, -1, 0)
        )
        return b"NMG9" + struct.pack("<I", 4) + vertices + struct.pack("<I", 4) + edges + struct.pack("<I", 1) + face + bytes(260)

    def test_length_prefixed_prefab(self):
        raw = self._one_face_raw()
        wrapped = struct.pack("<I", len(raw)) + raw
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixture.bin"
            path.write_bytes(wrapped)
            summary = navmesh_inspect.summarize(path)
        self.assertEqual(summary.container, "length-prefixed")
        self.assertEqual((summary.vertices, summary.edges, summary.faces), (4, 4, 1))
        self.assertEqual(summary.trailing_face_bytes, 317)
        self.assertEqual(summary.invalid_edge_endpoints, 0)
        self.assertEqual(summary.self_edges, 0)
        self.assertEqual(summary.duplicate_edges, 0)
        self.assertEqual(summary.face_degrees, {4: 1})
        self.assertEqual(summary.invalid_face_references, 0)
        self.assertEqual(summary.invalid_face_boundaries, 0)
        self.assertEqual(summary.global_tail_bytes, 260)
        self.assertEqual(summary.unused_vertices, 0)
        self.assertEqual(summary.unused_edges, 0)
        self.assertEqual(summary.nonmanifold_edges, 0)
        self.assertEqual(summary.connected_face_components, 1)

    def test_parse_serialize_raw_is_lossless(self):
        raw = self._one_face_raw()
        vertices, edges, face_count, face_offset = navmesh_inspect._leading_arrays(raw)
        faces, tail = navmesh_inspect._faces(raw, face_count, face_offset)
        rebuilt = navmesh_inspect.serialize_raw(raw[:4], vertices, edges, faces, tail)
        self.assertEqual(rebuilt, raw)

    def test_raw_nmg(self):
        raw = self._one_face_raw()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixture.nmg"
            path.write_bytes(raw)
            summary = navmesh_inspect.summarize(path)
        self.assertEqual(summary.container, "raw")

    def test_rejects_bad_prefab_length(self):
        raw = self._one_face_raw()
        wrapped = struct.pack("<I", len(raw) + 1) + raw
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixture.bin"
            path.write_bytes(wrapped)
            with self.assertRaises(navmesh_inspect.NavMeshFormatError):
                navmesh_inspect.summarize(path)

    def test_rnm1_copied_round_trip(self):
        raw = self._one_face_raw()
        header = bytearray(48)
        header[:4] = b"RNM1"
        original = navmesh_inspect.encode_container(raw, bytes(header), "RNM1")
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.bin"
            target = Path(directory) / "target.bin"
            source.write_bytes(original)
            navmesh_inspect.write_roundtrip_copy(source, target)
            _, decoded, wrapper = navmesh_inspect.read_raw(target)
        self.assertEqual(wrapper, "RNM1")
        self.assertEqual(decoded, raw)

    def test_round_trip_refuses_existing_output(self):
        raw = self._one_face_raw()
        wrapped = struct.pack("<I", len(raw)) + raw
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.bin"
            target = Path(directory) / "target.bin"
            source.write_bytes(wrapped)
            target.write_bytes(b"keep")
            with self.assertRaises(navmesh_inspect.NavMeshFormatError):
                navmesh_inspect.write_roundtrip_copy(source, target)
            self.assertEqual(target.read_bytes(), b"keep")


if __name__ == "__main__":
    unittest.main()
