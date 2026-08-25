using System;
using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>
    /// A navmesh vertex.
    ///
    /// Its own type rather than TaleWorlds' Vec3 so this layer stays usable outside a running game -
    /// by a desktop test harness, and by any mod that would rather not take a dependency.
    /// </summary>
    public struct NavVertex {
        public float X;
        public float Y;
        public float Z;

        public NavVertex(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    /// <summary>
    /// One edge record.
    ///
    /// NMG8 stores five integers and NMG9 six. Both are held here as six; the sixth is zero for
    /// NMG8, which is exactly what the Modding Kit writes when it upgrades a file. That means an
    /// NMG8 input can be re-serialized as NMG8 unchanged, or written out as NMG9 after editing.
    /// </summary>
    public struct NavEdge {
        public int A, B, C, D, E, F;

        public NavEdge(int a, int b, int c, int d, int e, int f) {
            A = a; B = b; C = c; D = d; E = e; F = f;
        }
    }

    /// <summary>
    /// One face: a vertex loop, the matching edge loop, five metadata integers and a direction byte.
    ///
    /// Metadata[2] is the face group. Metadata[4] is normally 15; faces rebuilt by the Modding Kit
    /// carry -15, and this code follows that convention for faces it generates, so a rebuilt patch is
    /// distinguishable from original geometry.
    /// </summary>
    public class NavFace {
        public uint[] Vertices;
        public uint[] Edges;
        public int[] Metadata;         // always length 5
        public byte Direction;

        public NavFace(uint[] vertices, uint[] edges, int[] metadata, byte direction) {
            Vertices = vertices;
            Edges = edges;
            Metadata = metadata;
            Direction = direction;
        }

        public int Degree => Vertices.Length;

        /// <summary>The face group, which cutout and addition passes must not mix.</summary>
        public int Group => Metadata.Length > 2 ? Metadata[2] : 0;

        public NavFace Clone() => new NavFace(
            (uint[])Vertices.Clone(), (uint[])Edges.Clone(), (int[])Metadata.Clone(), Direction);
    }

    /// <summary>
    /// A decoded navmesh: the three arrays, plus the trailing bytes kept verbatim.
    ///
    /// The 260-byte global tail is preserved rather than interpreted. Nothing here knows what it
    /// means, and a file that round-trips byte for byte is the only proof that the parts which ARE
    /// understood have been read correctly.
    /// </summary>
    public class NavMeshData {
        public string Signature = "NMG9";              // "NMG8" or "NMG9"
        public List<NavVertex> Vertices = new List<NavVertex>();
        public List<NavEdge> Edges = new List<NavEdge>();
        public List<NavFace> Faces = new List<NavFace>();
        public byte[] GlobalTail = new byte[0];

        /// <summary>How the file this came from was wrapped, so it can be written back the same way.</summary>
        public NavMeshWrapper Wrapper = NavMeshWrapper.Rnm1;

        /// <summary>The original file bytes, needed to preserve the RNM1 header on write.</summary>
        public byte[] OriginalContainer = new byte[0];

        public bool IsNmg8 => Signature == "NMG8";

        /// <summary>
        /// A deep copy, apart from the tail and the original container which are never written to.
        ///
        /// Editing passes mutate in place, so a caller that wants to try an edit and keep the result
        /// only if it succeeds takes a copy first. That is how one bad footprint is skipped without
        /// losing the other edits in the same bake.
        /// </summary>
        public NavMeshData Clone() {
            var copy = new NavMeshData {
                Signature = Signature,
                Vertices = new List<NavVertex>(Vertices),
                Edges = new List<NavEdge>(Edges),
                // Shared, not deep-copied. The editing passes never modify a face in place: they
                // build a new face list and append newly constructed faces. Cloning three arrays per
                // face made copying a town-sized mesh cost more than the edit being attempted.
                Faces = new List<NavFace>(Faces),
                GlobalTail = GlobalTail,
                Wrapper = Wrapper,
                OriginalContainer = OriginalContainer,
            };
            return copy;
        }

        // -- reading -------------------------------------------------------------------------------

        public static NavMeshData Parse(byte[] container) {
            var data = new NavMeshData { OriginalContainer = container };
            byte[] raw = NavMeshContainer.Unwrap(container, out NavMeshWrapper wrapper);
            data.Wrapper = wrapper;
            data.ParseRaw(raw);
            return data;
        }

        /// <summary>Parses an already-unwrapped NMG stream.</summary>
        public void ParseRaw(byte[] raw) {
            Signature = "" + (char)raw[0] + (char)raw[1] + (char)raw[2] + (char)raw[3];
            if (Signature != "NMG8" && Signature != "NMG9") {
                throw new NavMeshFormatException("unsupported inner signature " + Signature);
            }

            int edgeWidth = IsNmg8 ? 20 : 24;
            int cursor = 4;

            int vertexCount = (int)ReadU32(raw, cursor);
            cursor += 4;
            Vertices = new List<NavVertex>(vertexCount);
            for (int i = 0; i < vertexCount; i++) {
                Vertices.Add(new NavVertex(
                    BitConverter.ToSingle(raw, cursor),
                    BitConverter.ToSingle(raw, cursor + 4),
                    BitConverter.ToSingle(raw, cursor + 8)));
                cursor += 12;
            }

            int edgeCount = (int)ReadU32(raw, cursor);
            cursor += 4;
            Edges = new List<NavEdge>(edgeCount);
            for (int i = 0; i < edgeCount; i++) {
                Edges.Add(new NavEdge(
                    BitConverter.ToInt32(raw, cursor),
                    BitConverter.ToInt32(raw, cursor + 4),
                    BitConverter.ToInt32(raw, cursor + 8),
                    BitConverter.ToInt32(raw, cursor + 12),
                    BitConverter.ToInt32(raw, cursor + 16),
                    IsNmg8 ? 0 : BitConverter.ToInt32(raw, cursor + 20)));
                cursor += edgeWidth;
            }

            int faceCount = (int)ReadU32(raw, cursor);
            cursor += 4;
            Faces = new List<NavFace>(faceCount);
            for (int i = 0; i < faceCount; i++) {
                int degree = (int)ReadU32(raw, cursor);
                if (degree < 3 || degree > 64) {
                    throw new NavMeshFormatException(
                        "invalid face degree " + degree + " for face " + i + " at 0x" + cursor.ToString("X"));
                }
                cursor += 4;

                if (cursor + degree * 8 + 21 > raw.Length) {
                    throw new NavMeshFormatException("truncated face " + i);
                }

                var vertices = new uint[degree];
                for (int v = 0; v < degree; v++) { vertices[v] = ReadU32(raw, cursor); cursor += 4; }

                var edges = new uint[degree];
                for (int e = 0; e < degree; e++) { edges[e] = ReadU32(raw, cursor); cursor += 4; }

                var metadata = new int[5];
                for (int m = 0; m < 5; m++) { metadata[m] = BitConverter.ToInt32(raw, cursor); cursor += 4; }

                byte direction = raw[cursor++];
                Faces.Add(new NavFace(vertices, edges, metadata, direction));
            }

            GlobalTail = new byte[raw.Length - cursor];
            Array.Copy(raw, cursor, GlobalTail, 0, GlobalTail.Length);
        }

        // -- writing -------------------------------------------------------------------------------

        /// <summary>
        /// Serializes to an NMG stream.
        /// </summary>
        /// <param name="signature">
        /// "NMG8" or "NMG9". Editing passes write NMG9 even from an NMG8 source, matching what the
        /// Modding Kit does on upgrade; a plain round-trip keeps the original.
        /// </param>
        public byte[] SerializeRaw(string signature) {
            if (signature != "NMG8" && signature != "NMG9") {
                throw new NavMeshFormatException("unsupported inner signature " + signature);
            }

            bool nmg8 = signature == "NMG8";
            var output = new List<byte>(
                16 + Vertices.Count * 12 + Edges.Count * (nmg8 ? 20 : 24) + Faces.Count * 40 + GlobalTail.Length);

            foreach (char c in signature) output.Add((byte)c);

            AddU32(output, (uint)Vertices.Count);
            foreach (NavVertex vertex in Vertices) {
                AddBytes(output, BitConverter.GetBytes(vertex.X));
                AddBytes(output, BitConverter.GetBytes(vertex.Y));
                AddBytes(output, BitConverter.GetBytes(vertex.Z));
            }

            AddU32(output, (uint)Edges.Count);
            foreach (NavEdge edge in Edges) {
                AddI32(output, edge.A);
                AddI32(output, edge.B);
                AddI32(output, edge.C);
                AddI32(output, edge.D);
                AddI32(output, edge.E);
                if (!nmg8) AddI32(output, edge.F);
            }

            AddU32(output, (uint)Faces.Count);
            foreach (NavFace face in Faces) {
                if (face.Vertices.Length != face.Edges.Length) {
                    throw new NavMeshFormatException("face vertex and edge loops have different lengths");
                }
                AddU32(output, (uint)face.Degree);
                foreach (uint v in face.Vertices) AddU32(output, v);
                foreach (uint e in face.Edges) AddU32(output, e);
                foreach (int m in face.Metadata) AddI32(output, m);
                output.Add(face.Direction);
            }

            AddBytes(output, GlobalTail);
            return output.ToArray();
        }

        /// <summary>Serializes and re-wraps, ready to write to disk.</summary>
        public byte[] Serialize(string signature) =>
            NavMeshContainer.Wrap(SerializeRaw(signature), OriginalContainer, Wrapper);

        private static uint ReadU32(byte[] data, int offset) {
            if (offset < 0 || offset + 4 > data.Length) {
                throw new NavMeshFormatException("u32 outside data at 0x" + offset.ToString("X"));
            }
            return BitConverter.ToUInt32(data, offset);
        }

        private static void AddBytes(List<byte> output, byte[] value) {
            for (int i = 0; i < value.Length; i++) output.Add(value[i]);
        }

        private static void AddU32(List<byte> output, uint value) => AddBytes(output, BitConverter.GetBytes(value));
        private static void AddI32(List<byte> output, int value) => AddBytes(output, BitConverter.GetBytes(value));
    }
}
