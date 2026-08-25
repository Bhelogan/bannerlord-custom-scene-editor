using System;
using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>
    /// The structural health of a navmesh, counted rather than judged.
    ///
    /// Every field is a count so a result can be compared against the same measurement taken before
    /// an edit. That comparison is the real test: an absolute number like "3 unused vertices" says
    /// nothing, because shipped navmeshes contain such things, but "3 before and 47 after" says the
    /// edit broke something.
    /// </summary>
    public class NavMeshSummary {
        public string Format = "";
        public int Vertices;
        public int Edges;
        public int Faces;
        public bool FiniteVertices;
        public int InvalidEdgeEndpoints;
        public int SelfEdges;
        public int DuplicateEdges;
        public int InvalidFaceReferences;
        public int InvalidFaceBoundaries;
        public int UnusedVertices;
        public int UnusedEdges;
        public int NonManifoldEdges;
        public int ConnectedFaceComponents;
        public int GlobalTailBytes;

        /// <summary>True when nothing structurally wrong was found. Component count is not judged here - it is compared.</summary>
        public bool IsStructurallySound =>
            FiniteVertices
            && InvalidEdgeEndpoints == 0
            && SelfEdges == 0
            && DuplicateEdges == 0
            && InvalidFaceReferences == 0
            && InvalidFaceBoundaries == 0;

        public string Describe() =>
            $"{Format} v={Vertices} e={Edges} f={Faces} components={ConnectedFaceComponents} "
            + $"unusedV={UnusedVertices} unusedE={UnusedEdges} nonManifold={NonManifoldEdges} "
            + $"badEndpoints={InvalidEdgeEndpoints} selfEdges={SelfEdges} dupEdges={DuplicateEdges} "
            + $"badFaceRefs={InvalidFaceReferences} badFaceBounds={InvalidFaceBoundaries}";
    }

    /// <summary>
    /// Structural checks over decoded navmesh data.
    ///
    /// Nothing here changes anything. Editing passes run it before and after, and refuse to keep a
    /// result whose health got worse - which is how a subtly broken mesh is caught here rather than
    /// as agents walking through walls twenty minutes into a game.
    /// </summary>
    public static class NavMeshValidation {

        public static NavMeshSummary Summarize(NavMeshData data) {
            int vertexCount = data.Vertices.Count;
            int edgeCount = data.Edges.Count;

            var summary = new NavMeshSummary {
                Format = data.Signature,
                Vertices = vertexCount,
                Edges = edgeCount,
                Faces = data.Faces.Count,
                GlobalTailBytes = data.GlobalTail.Length,
                FiniteVertices = true,
            };

            foreach (NavVertex vertex in data.Vertices) {
                if (IsFinite(vertex.X) && IsFinite(vertex.Y) && IsFinite(vertex.Z)) continue;
                summary.FiniteVertices = false;
                break;
            }
            var normalizedEdges = NavMeshEdgeKey.NewSet(edgeCount);
            foreach (NavEdge edge in data.Edges) {
                if (edge.B < 0 || edge.B >= vertexCount || edge.C < 0 || edge.C >= vertexCount) {
                    summary.InvalidEdgeEndpoints++;
                }
                if (edge.B == edge.C) summary.SelfEdges++;
                if (!normalizedEdges.Add(EdgeKey(edge.B, edge.C))) summary.DuplicateEdges++;
            }
            var usedVertices = new HashSet<uint>();
            var edgeFaceCounts = new int[edgeCount];

            // Union-find rather than adjacency lists. Counting regions does not need to remember who
            // neighbours whom, and allocating a list per face to answer "how many pieces" dominated
            // the cost of this whole check on a large mesh.
            var regions = new int[data.Faces.Count];
            for (int i = 0; i < regions.Length; i++) regions[i] = i;

            var firstFaceForEdge = new Dictionary<uint, int>();

            for (int faceIndex = 0; faceIndex < data.Faces.Count; faceIndex++) {
                NavFace face = data.Faces[faceIndex];
                int degree = face.Degree;

                if (AnyAtLeast(face.Vertices, vertexCount) || AnyAtLeast(face.Edges, edgeCount)) {
                    summary.InvalidFaceReferences++;
                    continue;
                }

                foreach (uint vertex in face.Vertices) usedVertices.Add(vertex);

                for (int index = 0; index < degree; index++) {
                    uint edgeIndex = face.Edges[index];
                    edgeFaceCounts[edgeIndex]++;

                    if (!firstFaceForEdge.TryGetValue(edgeIndex, out int other)) {
                        firstFaceForEdge[edgeIndex] = faceIndex;
                    } else if (other != faceIndex) {
                        Union(regions, faceIndex, other);
                    }

                    // The face's vertex loop and its edge loop must describe the same boundary. A
                    // mismatch is the signature of an edit that added faces without wiring edges.
                    NavEdge edge = data.Edges[(int)edgeIndex];
                    uint expectedA = face.Vertices[index];
                    uint expectedB = face.Vertices[(index + 1) % degree];
                    bool matches = (edge.B == expectedA && edge.C == expectedB)
                                   || (edge.B == expectedB && edge.C == expectedA);
                    if (!matches) {
                        summary.InvalidFaceBoundaries++;
                        break;
                    }
                }
            }
            summary.UnusedVertices = vertexCount - usedVertices.Count;
            foreach (int count in edgeFaceCounts) {
                if (count == 0) summary.UnusedEdges++;
                if (count > 2) summary.NonManifoldEdges++;
            }
            int components = 0;
            for (int i = 0; i < regions.Length; i++) {
                if (Find(regions, i) == i) components++;
            }
            summary.ConnectedFaceComponents = components;
            return summary;
        }

        /// <summary>Union-find root, with path compression so repeated lookups stay flat.</summary>
        private static int Find(int[] regions, int index) {
            while (regions[index] != index) {
                regions[index] = regions[regions[index]];
                index = regions[index];
            }
            return index;
        }

        private static void Union(int[] regions, int left, int right) {
            int a = Find(regions, left), b = Find(regions, right);
            if (a == b) return;

            // Lower index wins, so a root is always the smallest face in its region and the final
            // "is this its own root" sweep counts each region exactly once.
            if (a < b) regions[b] = a;
            else regions[a] = b;
        }

        private static bool AnyAtLeast(uint[] values, int limit) {
            foreach (uint value in values) {
                if (value >= limit) return true;
            }
            return false;
        }

        private static long EdgeKey(int a, int b) => NavMeshEdgeKey.Of(a, b);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
