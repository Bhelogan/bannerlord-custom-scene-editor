using System;
using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>Anything wrong with a navmesh file's structure.</summary>
    public class NavMeshFormatException : Exception {
        public NavMeshFormatException(string message) : base(message) { }
    }

    /// <summary>How the NMG stream was wrapped, so a rewrite can put it back the same way.</summary>
    public enum NavMeshWrapper {
        /// <summary>Scene navmeshes: a 48-byte RNM1 header followed by one raw LZ4 block.</summary>
        Rnm1,
        /// <summary>Navmesh prefabs: a u32 length followed directly by the NMG stream.</summary>
        LengthPrefixed,
        /// <summary>The bare NMG stream.</summary>
        Raw,
    }

    /// <summary>
    /// The container around an NMG stream: RNM1 + LZ4, a length prefix, or nothing.
    ///
    /// Deliberately free of any TaleWorlds type. This layer is bytes in, bytes out, so another mod
    /// can use it - and so a desktop harness with no game running can verify it against the Python
    /// implementation, which is how this port is checked.
    ///
    /// The LZ4 codec is hand-written rather than taken from a package. It is about a hundred lines,
    /// it keeps a redistributable dependency out of a mod that otherwise has none, and the decoder
    /// has to exist regardless: Bannerlord stores a bare block with no frame header, so a stock LZ4
    /// frame decoder cannot read it.
    /// </summary>
    public static class NavMeshContainer {

        // -- LZ4 -----------------------------------------------------------------------------------

        /// <summary>Decodes the raw LZ4 block that follows the RNM1 header.</summary>
        public static byte[] DecompressLz4(byte[] source, long expectedSize) {
            var output = new List<byte>(expectedSize > 0 && expectedSize < int.MaxValue
                ? (int)expectedSize
                : 1024);
            int cursor = 0;

            while (cursor < source.Length) {
                byte token = source[cursor++];

                int literalLength = token >> 4;
                if (literalLength == 15) {
                    while (true) {
                        if (cursor >= source.Length) {
                            throw new NavMeshFormatException("truncated LZ4 literal length");
                        }
                        byte extension = source[cursor++];
                        literalLength += extension;
                        if (extension != 255) break;
                    }
                }

                int literalEnd = cursor + literalLength;
                if (literalEnd > source.Length) throw new NavMeshFormatException("truncated LZ4 literal");
                for (int i = cursor; i < literalEnd; i++) output.Add(source[i]);
                cursor = literalEnd;

                // A final literal-only sequence carries no match offset.
                if (cursor == source.Length) break;
                if (cursor + 2 > source.Length) throw new NavMeshFormatException("truncated LZ4 match offset");

                int matchOffset = source[cursor] | (source[cursor + 1] << 8);
                cursor += 2;
                if (matchOffset == 0 || matchOffset > output.Count) {
                    throw new NavMeshFormatException("invalid LZ4 match offset " + matchOffset);
                }

                int matchLength = token & 0x0F;
                if (matchLength == 15) {
                    while (true) {
                        if (cursor >= source.Length) {
                            throw new NavMeshFormatException("truncated LZ4 match length");
                        }
                        byte extension = source[cursor++];
                        matchLength += extension;
                        if (extension != 255) break;
                    }
                }
                matchLength += 4;

                // Copied one byte at a time on purpose: an LZ4 match may overlap the output still
                // being written, which is how the format encodes runs. A block copy reads stale bytes.
                int matchStart = output.Count - matchOffset;
                for (int i = 0; i < matchLength; i++) output.Add(output[matchStart + i]);
            }

            if (output.Count != expectedSize) {
                throw new NavMeshFormatException(
                    "decompressed size " + output.Count + " != RNM1 declaration " + expectedSize);
            }
            return output.ToArray();
        }

        private static void AppendLength(List<byte> output, int extra) {
            while (extra >= 255) {
                output.Add(255);
                extra -= 255;
            }
            output.Add((byte)extra);
        }

        /// <summary>One valid literal-only sequence. Correct for any input, and compresses nothing.</summary>
        public static byte[] CompressLz4Literal(byte[] raw) {
            var output = new List<byte> { (byte)(Math.Min(raw.Length, 15) << 4) };
            if (raw.Length >= 15) AppendLength(output, raw.Length - 15);
            output.AddRange(raw);
            return output.ToArray();
        }

        /// <summary>
        /// A deterministic block with a small greedy matcher.
        ///
        /// Ratio is secondary. Being byte-for-byte reproducible is what matters, because that is what
        /// allows a candidate written here to be compared against the Python writer by hash.
        /// </summary>
        public static byte[] CompressLz4(byte[] raw) {
            if (raw.Length < 13) return CompressLz4Literal(raw);

            var output = new List<byte>(raw.Length / 2);
            var latest = new Dictionary<uint, int>();
            int anchor = 0;
            int cursor = 0;
            int matchLimit = raw.Length - 12;    // preserve the conventional five-literal final sequence

            while (cursor <= matchLimit) {
                uint key = BitConverter.ToUInt32(raw, cursor);
                bool seen = latest.TryGetValue(key, out int previous);
                latest[key] = cursor;

                if (!seen || cursor - previous > 65535 || !FourBytesEqual(raw, previous, cursor)) {
                    cursor++;
                    continue;
                }

                int matchLength = 4;
                while (cursor + matchLength < raw.Length - 5 &&
                       raw[previous + matchLength] == raw[cursor + matchLength]) {
                    matchLength++;
                }

                int literalLength = cursor - anchor;
                int encodedMatch = matchLength - 4;
                output.Add((byte)((Math.Min(literalLength, 15) << 4) | Math.Min(encodedMatch, 15)));
                if (literalLength >= 15) AppendLength(output, literalLength - 15);
                for (int i = anchor; i < cursor; i++) output.Add(raw[i]);

                int offset = cursor - previous;
                output.Add((byte)(offset & 0xFF));
                output.Add((byte)((offset >> 8) & 0xFF));
                if (encodedMatch >= 15) AppendLength(output, encodedMatch - 15);

                int matchEnd = cursor + matchLength;
                cursor++;
                while (cursor < matchEnd) {
                    if (cursor + 4 <= raw.Length) latest[BitConverter.ToUInt32(raw, cursor)] = cursor;
                    cursor++;
                }
                anchor = matchEnd;
            }

            int finalLiterals = raw.Length - anchor;
            output.Add((byte)(Math.Min(finalLiterals, 15) << 4));
            if (finalLiterals >= 15) AppendLength(output, finalLiterals - 15);
            for (int i = anchor; i < raw.Length; i++) output.Add(raw[i]);
            return output.ToArray();
        }

        private static bool FourBytesEqual(byte[] data, int a, int b) =>
            data[a] == data[b] && data[a + 1] == data[b + 1] &&
            data[a + 2] == data[b + 2] && data[a + 3] == data[b + 3];

        // -- container -----------------------------------------------------------------------------

        private static uint ReadU32(byte[] data, int offset) {
            if (offset < 0 || offset + 4 > data.Length) {
                throw new NavMeshFormatException("u32 outside file at 0x" + offset.ToString("X"));
            }
            return BitConverter.ToUInt32(data, offset);
        }

        private static ulong ReadU64(byte[] data, int offset) {
            if (offset < 0 || offset + 8 > data.Length) {
                throw new NavMeshFormatException("u64 outside file at 0x" + offset.ToString("X"));
            }
            return BitConverter.ToUInt64(data, offset);
        }

        /// <summary>The first four bytes as text, for an error message.</summary>
        private static string Describe(byte[] raw) {
            if (raw == null || raw.Length < 4) return "(too short)";
            var text = new char[4];
            for (int i = 0; i < 4; i++) {
                text[i] = raw[i] >= 32 && raw[i] < 127 ? (char)raw[i] : '?';
            }
            return new string(text);
        }

        private static bool Matches(byte[] data, int offset, string signature) {
            if (offset + signature.Length > data.Length) return false;
            for (int i = 0; i < signature.Length; i++) {
                if (data[offset + i] != (byte)signature[i]) return false;
            }
            return true;
        }

        /// <summary>Unwraps a file to its NMG stream, reporting how it was wrapped.</summary>
        public static byte[] Unwrap(byte[] container, out NavMeshWrapper wrapper) {
            byte[] raw;

            if (container.Length >= 49 && Matches(container, 0, "RNM1")) {
                if (ReadU32(container, 4) != (uint)(container.Length - 8)) {
                    throw new NavMeshFormatException("RNM1 compressed-length field does not match file");
                }
                if (ReadU64(container, 24) != (ulong)(container.Length - 8)) {
                    throw new NavMeshFormatException("RNM1 mirrored compressed-length field does not match file");
                }

                var block = new byte[container.Length - 48];
                Array.Copy(container, 48, block, 0, block.Length);
                raw = DecompressLz4(block, (long)ReadU64(container, 16));
                wrapper = NavMeshWrapper.Rnm1;
            } else if (container.Length >= 8 &&
                       (Matches(container, 4, "NMG8") || Matches(container, 4, "NMG9"))) {
                if (ReadU32(container, 0) != (uint)(container.Length - 4)) {
                    throw new NavMeshFormatException("prefab length prefix does not match file");
                }
                raw = new byte[container.Length - 4];
                Array.Copy(container, 4, raw, 0, raw.Length);
                wrapper = NavMeshWrapper.LengthPrefixed;
            } else if (container.Length >= 4 &&
                       (Matches(container, 0, "NMG8") || Matches(container, 0, "NMG9"))) {
                raw = container;
                wrapper = NavMeshWrapper.Raw;
            } else {
                throw new NavMeshFormatException("not an RNM1, length-prefixed, or raw NMG8/NMG9 file");
            }

            // NMG7 is accepted for READING only - stock 1.x scenes still ship it, and refusing it
            // forced every such scene through the official Modding Kit just to be upgraded. It is
            // written back out as NMG9; see NavMeshData.OutputSignature.
            if (!Matches(raw, 0, "NMG7") && !Matches(raw, 0, "NMG8") && !Matches(raw, 0, "NMG9")) {
                // Name it. "unsupported inner signature" on its own leaves no way to tell a scene
                // this editor simply cannot read from one it has corrupted, and the two need
                // completely different responses.
                throw new NavMeshFormatException(
                    "unsupported inner signature '" + Describe(raw) + "'; this editor reads NMG7, NMG8 and NMG9");
            }
            return raw;
        }

        /// <summary>
        /// Re-wraps an NMG stream the way the original was wrapped.
        ///
        /// The RNM1 header is COPIED from the original rather than rebuilt. It carries fields this
        /// code does not claim to understand; only the three length fields are known to depend on the
        /// payload, so only those are rewritten.
        /// </summary>
        public static byte[] Wrap(byte[] raw, byte[] original, NavMeshWrapper wrapper) {
            switch (wrapper) {
                case NavMeshWrapper.Rnm1: {
                    if (original.Length < 48 || !Matches(original, 0, "RNM1")) {
                        throw new NavMeshFormatException("cannot preserve invalid RNM1 header");
                    }

                    byte[] compressed = CompressLz4(raw);
                    var output = new byte[48 + compressed.Length];
                    Array.Copy(original, 0, output, 0, 48);

                    int total = output.Length;
                    WriteU32(output, 4, (uint)(total - 8));
                    WriteU64(output, 16, (ulong)raw.Length);
                    WriteU64(output, 24, (ulong)(total - 8));
                    Array.Copy(compressed, 0, output, 48, compressed.Length);
                    return output;
                }

                case NavMeshWrapper.LengthPrefixed: {
                    var output = new byte[4 + raw.Length];
                    WriteU32(output, 0, (uint)raw.Length);
                    Array.Copy(raw, 0, output, 4, raw.Length);
                    return output;
                }

                case NavMeshWrapper.Raw:
                    return raw;

                default:
                    throw new NavMeshFormatException("unsupported container wrapper " + wrapper);
            }
        }

        private static void WriteU32(byte[] data, int offset, uint value) =>
            Array.Copy(BitConverter.GetBytes(value), 0, data, offset, 4);

        private static void WriteU64(byte[] data, int offset, ulong value) =>
            Array.Copy(BitConverter.GetBytes(value), 0, data, offset, 8);
    }
}
