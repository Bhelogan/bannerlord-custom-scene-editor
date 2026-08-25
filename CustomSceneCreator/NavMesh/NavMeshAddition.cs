using System;
using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>
    /// One place the author marked as needing walkable ground, with the terrain samples taken there.
    ///
    /// The boundary samples are the whole point. They are real heights read off the terrain at mark
    /// time, so faces built from them follow slopes and step at stairs. Anything interpolated instead
    /// would float above or sink below the ground the player actually walks on.
    /// </summary>
    public class NavMeshRequiredArea {
        public string Id = "";
        public string Label = "Navmesh needed";
        public NavVertex Center;
        public double Radius = 4.0;
        public NavVertex[] Boundary = new NavVertex[0];

        /// <summary>True when there are enough samples to build a shape from.</summary>
        public bool HasGeometry => Boundary != null && Boundary.Length >= 8;
    }

    /// <summary>What one cluster of required areas turned into.</summary>
    public class AdditionOperation {
        public int ClusterId;
        public string[] Areas = new string[0];
        public int EnvelopeVertices;
        public int BridgeEdge;
        public int FacesAdded;
        public double Area;
    }

    /// <summary>What the addition pass changed overall.</summary>
    public class AdditionResult {
        public List<AdditionOperation> Operations = new List<AdditionOperation>();

        /// <summary>Marked areas that became walkable ground.</summary>
        public int AreasFilled;

        /// <summary>Marked areas that could not be built, counted individually.</summary>
        public int AreasSkipped;

        /// <summary>One line per skipped cluster, naming the areas and the reason.</summary>
        public List<string> Skipped = new List<string>();

        public int VerticesBefore, VerticesAfter;
        public int EdgesBefore, EdgesAfter;
        public int FacesBefore, FacesAfter;
        public NavMeshSummary Before = new NavMeshSummary();
        public NavMeshSummary After = new NavMeshSummary();

        public string Describe() {
            string text =
                $"{AreasFilled} area(s) filled in {Operations.Count} patch(es), "
                + $"added {FacesAfter - FacesBefore} faces; "
                + $"v {VerticesBefore}->{VerticesAfter}, e {EdgesBefore}->{EdgesAfter}, "
                + $"f {FacesBefore}->{FacesAfter}; "
                + $"components {Before.ConnectedFaceComponents}->{After.ConnectedFaceComponents}, "
                + $"nonManifold={After.NonManifoldEdges}";
            return AreasSkipped > 0 ? text + $"; {AreasSkipped} area(s) skipped" : text;
        }
    }

    /// <summary>
    /// Builds walkable navmesh over ground the scene author left unmeshed.
    ///
    /// The inverse of <see cref="NavMeshCutout"/>: instead of removing faces under a building, it
    /// adds faces over open ground. Marked areas that overlap are merged into one cluster and filled
    /// as a single shape, so two overlapping marks produce one continuous surface rather than two
    /// patches meeting at a seam.
    ///
    /// Each cluster is joined to the existing mesh by exactly one bridge, from the nearest open
    /// boundary edge. Sharing that edge is what makes the new ground reachable - a patch that merely
    /// sits next to the mesh, touching but not joined, is invisible to pathfinding.
    /// </summary>
    public static class NavMeshAddition {

        /// <summary>
        /// Fills every marked area, in place.
        ///
        /// Existing vertices, edges, and faces are untouched; this pass only ever adds. That is what
        /// makes it safe to run alongside cutouts, and safe to run twice.
        /// </summary>
        public static AdditionResult Apply(NavMeshData data, IList<NavMeshRequiredArea> areas) {
            if (areas == null || areas.Count == 0) {
                throw new NavMeshFormatException("there are no marked areas to add navmesh for");
            }

            foreach (NavMeshRequiredArea area in areas) {
                if (!area.HasGeometry) {
                    throw new NavMeshFormatException(
                        $"marked area '{area.Label}' has no terrain samples; re-mark it in the editor");
                }
            }

            foreach (NavEdge edge in data.Edges) {
                if (edge.A != -1 || edge.D != -1 || edge.E != -1 || edge.F != 0) {
                    throw new NavMeshFormatException(
                        "this navmesh uses edge fields this editor does not understand; refusing to edit it");
                }
            }

            NavMeshSummary before = NavMeshValidation.Summarize(data);
            if (!before.IsStructurallySound) {
                throw new NavMeshFormatException(
                    "refusing to edit a navmesh that is already structurally unsound: " + before.Describe());
            }

            var result = new AdditionResult {
                Before = before,
                VerticesBefore = data.Vertices.Count,
                EdgesBefore = data.Edges.Count,
                FacesBefore = data.Faces.Count,
            };

            // An open edge - one belonging to a single face - is the only place new ground can be
            // attached. Attaching anywhere else would produce a non-manifold edge shared by three
            // faces, which the engine treats as broken.
            var edgeOwner = new int[data.Edges.Count];
            var edgeFaceCount = new int[data.Edges.Count];
            for (int faceIndex = 0; faceIndex < data.Faces.Count; faceIndex++) {
                foreach (uint edgeIndex in data.Faces[faceIndex].Edges) {
                    if (edgeFaceCount[edgeIndex]++ == 0) edgeOwner[edgeIndex] = faceIndex;
                }
            }

            var openEdges = new List<int>();
            for (int i = 0; i < edgeFaceCount.Length; i++) {
                if (edgeFaceCount[i] == 1) openEdges.Add(i);
            }
            if (openEdges.Count == 0) {
                throw new NavMeshFormatException("this navmesh has no open edge to extend from");
            }

            Dictionary<long, int> edgeLookup = NavMeshEdgeKey.NewMap(data.Edges.Count);
            for (int i = 0; i < data.Edges.Count; i++) {
                long key = EdgeKey(data.Edges[i].B, data.Edges[i].C);
                if (!edgeLookup.ContainsKey(key)) edgeLookup[key] = i;
            }

            var consumedEdges = new HashSet<int>();
            List<List<int>> clusters = Cluster(areas);

            for (int clusterId = 0; clusterId < clusters.Count; clusterId++) {
                List<int> members = clusters[clusterId];

                var names = new List<string>();
                foreach (int member in members) {
                    names.Add(string.IsNullOrEmpty(areas[member].Label)
                        ? areas[member].Id
                        : areas[member].Label);
                }

                // Everything a cluster adds is appended, so undoing one is a matter of cutting the
                // three lists back. Clusters are independent pieces of ground: one that cannot be
                // built is no reason to abandon the others, which is what a single failure used to
                // cost.
                int vertexMark = data.Vertices.Count;
                int edgeMark = data.Edges.Count;
                int faceMark = data.Faces.Count;

                try {
                    int bridgeEdge = BuildCluster(data, areas, members, names, clusterId,
                                                  openEdges, consumedEdges, edgeOwner, edgeLookup,
                                                  result.Operations);
                    consumedEdges.Add(bridgeEdge);
                    result.AreasFilled += members.Count;
                } catch (NavMeshFormatException ex) {
                    Rollback(data, edgeLookup, vertexMark, edgeMark, faceMark);
                    result.AreasSkipped += members.Count;
                    result.Skipped.Add(Join(names) + ": " + ex.Message);
                }
            }

            if (result.AreasFilled == 0) {
                throw new NavMeshFormatException(result.Skipped.Count > 0
                    ? result.Skipped[0]
                    : "no marked area could be turned into walkable ground");
            }

            data.Signature = "NMG9";

            NavMeshSummary after = NavMeshValidation.Summarize(data);
            if (!after.IsStructurallySound) {
                throw new NavMeshFormatException("the added ground produced an unsound navmesh: " + after.Describe());
            }
            if (after.NonManifoldEdges > before.NonManifoldEdges) {
                throw new NavMeshFormatException(
                    $"the added ground left {after.NonManifoldEdges} edges shared by more than two faces");
            }
            if (after.ConnectedFaceComponents > before.ConnectedFaceComponents) {
                throw new NavMeshFormatException(
                    $"the added ground did not connect to the existing navmesh "
                    + $"({after.ConnectedFaceComponents} separate pieces, was {before.ConnectedFaceComponents})");
            }

            result.After = after;
            result.VerticesAfter = data.Vertices.Count;
            result.EdgesAfter = data.Edges.Count;
            result.FacesAfter = data.Faces.Count;
            return result;
        }

        /// <summary>
        /// Builds one cluster's ground and attaches it to the mesh. Returns the bridged edge.
        ///
        /// Throws rather than half-building: the caller undoes everything this added, so a partial
        /// result never survives.
        /// </summary>
        private static int BuildCluster(NavMeshData data, IList<NavMeshRequiredArea> areas,
                                        List<int> members, List<string> names, int clusterId,
                                        List<int> openEdges, HashSet<int> consumedEdges,
                                        int[] edgeOwner, Dictionary<long, int> edgeLookup,
                                        List<AdditionOperation> operations) {
            var samples = new List<NavVertex>();
            foreach (int member in members) samples.AddRange(areas[member].Boundary);

            List<NavVertex> envelope = ConvexHullXy(samples);
            if (envelope.Count < 3) {
                throw new NavMeshFormatException(
                    "the marked area is too small or too narrow to build ground from");
            }

            var envelopeIndices = new List<int>(envelope.Count);
            foreach (NavVertex vertex in envelope) {
                envelopeIndices.Add(data.Vertices.Count);
                data.Vertices.Add(vertex);
            }

            FindBridge(data, envelope, envelopeIndices, openEdges, consumedEdges,
                       out int bridgeEdge, out int bridgeA, out int bridgeB, out int bridgeEnvelope);

            NavFace ownerFace = data.Faces[edgeOwner[bridgeEdge]];
            var metadata = (int[])ownerFace.Metadata.Clone();
            if (metadata.Length > 4) {
                metadata[4] = metadata[4] != 0 ? -Math.Abs(metadata[4]) : 0;
            }
            byte direction = ownerFace.Direction;

            // The bridge endpoints are folded into the same polygon as the envelope rather than
            // triangulated separately, so the bridge triangle and the interior triangles share real
            // edges instead of merely meeting in space.
            var points = new Dictionary<int, Point2>(envelopeIndices.Count + 2);
            foreach (int index in envelopeIndices) {
                points[index] = new Point2(data.Vertices[index].X, data.Vertices[index].Y);
            }
            points[bridgeA] = new Point2(data.Vertices[bridgeA].X, data.Vertices[bridgeA].Y);
            points[bridgeB] = new Point2(data.Vertices[bridgeB].X, data.Vertices[bridgeB].Y);

            var envelopeCcw = new List<int>(envelopeIndices);
            if (NavMeshGeometry.SignedArea(envelopeCcw, points) < 0) envelopeCcw.Reverse();

            // Which way round the two bridge endpoints go decides whether the ring closes cleanly or
            // folds back through itself. It depends on which side of the mesh edge the new ground
            // lies on, so both orders are tried and the one that gives a simple ring is used.
            List<int> combined = BuildRing(bridgeA, bridgeB, bridgeEnvelope, envelopeCcw);
            if (!NavMeshGeometry.IsSimplePolygon(combined, points)) {
                combined = BuildRing(bridgeB, bridgeA, bridgeEnvelope, envelopeCcw);
            }
            if (!NavMeshGeometry.IsSimplePolygon(combined, points)) {
                throw new NavMeshFormatException(
                    "this ground wraps around the navmesh edge it would join onto; "
                    + "mark it as smaller separate areas");
            }

            List<int[]> triangles = NavMeshGeometry.TriangulateSimplePolygon(
                combined, points, out double area);

            int added = 0;
            foreach (int[] triangle in triangles) {
                var a = new Point2(data.Vertices[triangle[0]].X, data.Vertices[triangle[0]].Y);
                var b = new Point2(data.Vertices[triangle[1]].X, data.Vertices[triangle[1]].Y);
                var c = new Point2(data.Vertices[triangle[2]].X, data.Vertices[triangle[2]].Y);

                if (Math.Abs(NavMeshGeometry.Cross(a, b, c)) / 2.0 <= 1e-4) {
                    throw new NavMeshFormatException("the added ground produced a degenerate face");
                }

                var edges = new uint[3];
                for (int index = 0; index < 3; index++) {
                    edges[index] = (uint)EnsureEdge(
                        data, edgeLookup, triangle[index], triangle[(index + 1) % 3]);
                }

                data.Faces.Add(new NavFace(
                    new uint[] { (uint)triangle[0], (uint)triangle[1], (uint)triangle[2] },
                    edges, (int[])metadata.Clone(), direction));
                added++;
            }

            operations.Add(new AdditionOperation {
                ClusterId = clusterId,
                Areas = names.ToArray(),
                EnvelopeVertices = envelope.Count,
                BridgeEdge = bridgeEdge,
                FacesAdded = added,
                Area = area,
            });
            return bridgeEdge;
        }

        /// <summary>Walks the bridge edge, then the hull starting at the bridged corner.</summary>
        private static List<int> BuildRing(int first, int second, int bridgeEnvelope,
                                           List<int> envelopeCcw) {
            int start = envelopeCcw.IndexOf(bridgeEnvelope);
            var ring = new List<int>(envelopeCcw.Count + 2) { first, second };
            for (int i = 0; i < envelopeCcw.Count; i++) {
                ring.Add(envelopeCcw[(start + i) % envelopeCcw.Count]);
            }
            return ring;
        }

        /// <summary>Cuts back everything a failed cluster appended, including its edge lookup entries.</summary>
        private static void Rollback(NavMeshData data, Dictionary<long, int> edgeLookup,
                                     int vertexMark, int edgeMark, int faceMark) {
            if (data.Faces.Count > faceMark) {
                data.Faces.RemoveRange(faceMark, data.Faces.Count - faceMark);
            }

            if (data.Edges.Count > edgeMark) {
                var stale = new List<long>();
                foreach (KeyValuePair<long, int> entry in edgeLookup) {
                    if (entry.Value >= edgeMark) stale.Add(entry.Key);
                }
                foreach (long key in stale) edgeLookup.Remove(key);
                data.Edges.RemoveRange(edgeMark, data.Edges.Count - edgeMark);
            }

            if (data.Vertices.Count > vertexMark) {
                data.Vertices.RemoveRange(vertexMark, data.Vertices.Count - vertexMark);
            }
        }

        private static string Join(List<string> names) =>
            names.Count == 1 ? "'" + names[0] + "'" : names.Count + " overlapping area(s)";

        /// <summary>
        /// Groups marked areas whose circles overlap.
        ///
        /// Overlapping marks describe one piece of ground the author wanted walkable, so they are
        /// filled as one shape. Filling them separately would leave a seam along which the two
        /// patches share vertices but not edges, and agents stop at seams.
        /// </summary>
        private static List<List<int>> Cluster(IList<NavMeshRequiredArea> areas) {
            var adjacency = new List<int>[areas.Count];
            for (int i = 0; i < areas.Count; i++) adjacency[i] = new List<int>();

            for (int left = 0; left < areas.Count; left++) {
                for (int right = left + 1; right < areas.Count; right++) {
                    double dx = areas[left].Center.X - areas[right].Center.X;
                    double dy = areas[left].Center.Y - areas[right].Center.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) > areas[left].Radius + areas[right].Radius) continue;
                    adjacency[left].Add(right);
                    adjacency[right].Add(left);
                }
            }

            var clusters = new List<List<int>>();
            var seen = new bool[areas.Count];

            for (int start = 0; start < areas.Count; start++) {
                if (seen[start]) continue;
                var cluster = new List<int>();
                var stack = new Stack<int>();
                stack.Push(start);
                seen[start] = true;

                while (stack.Count > 0) {
                    int current = stack.Pop();
                    cluster.Add(current);
                    foreach (int neighbor in adjacency[current]) {
                        if (seen[neighbor]) continue;
                        seen[neighbor] = true;
                        stack.Push(neighbor);
                    }
                }
                cluster.Sort();
                clusters.Add(cluster);
            }
            return clusters;
        }

        /// <summary>
        /// Picks the open edge and envelope corner that are closest together.
        ///
        /// One bridge per cluster, and never the same edge twice: once an open edge carries a bridge
        /// it belongs to two faces, and bridging it again would make it non-manifold.
        /// </summary>
        private static void FindBridge(NavMeshData data, List<NavVertex> envelope, List<int> envelopeIndices,
                                       List<int> openEdges, HashSet<int> consumed,
                                       out int bridgeEdge, out int bridgeA, out int bridgeB,
                                       out int bridgeEnvelope) {
            double best = double.MaxValue;
            bridgeEdge = -1;
            bridgeA = bridgeB = bridgeEnvelope = -1;

            foreach (int edgeIndex in openEdges) {
                if (consumed.Contains(edgeIndex)) continue;
                NavEdge edge = data.Edges[edgeIndex];
                NavVertex va = data.Vertices[edge.B];
                NavVertex vb = data.Vertices[edge.C];

                for (int i = 0; i < envelope.Count; i++) {
                    NavVertex corner = envelope[i];
                    double distance = Math.Min(
                        Distance(corner.X, corner.Y, va.X, va.Y),
                        Distance(corner.X, corner.Y, vb.X, vb.Y));
                    if (distance >= best) continue;

                    best = distance;
                    bridgeEdge = edgeIndex;
                    bridgeA = edge.B;
                    bridgeB = edge.C;
                    bridgeEnvelope = envelopeIndices[i];
                }
            }

            if (bridgeEdge < 0) {
                throw new NavMeshFormatException(
                    "no open navmesh edge was available to join the new ground to");
            }
        }

        private static int EnsureEdge(NavMeshData data, Dictionary<long, int> lookup, int a, int b) {
            long key = EdgeKey(a, b);
            if (lookup.TryGetValue(key, out int edgeIndex)) return edgeIndex;

            edgeIndex = data.Edges.Count;
            lookup[key] = edgeIndex;
            data.Edges.Add(new NavEdge(-1, a, b, -1, -1, 0));
            return edgeIndex;
        }

        /// <summary>
        /// The convex hull of the samples in XY, each corner keeping its own sampled Z.
        ///
        /// Andrew's monotone chain. A hull rather than the marks' outlines because a hull is always a
        /// simple polygon, and a simple polygon is the only thing ear clipping can be trusted with.
        /// The cost is that a concave group of marks fills slightly more ground than was marked.
        /// </summary>
        public static List<NavVertex> ConvexHullXy(List<NavVertex> points) {
            var unique = new Dictionary<long, NavVertex>(NavMeshEdgeKey.Comparer);
            var ordered = new List<NavVertex>();
            foreach (NavVertex point in points) {
                long key = ((long)BitConverter.ToInt32(BitConverter.GetBytes(point.X), 0) << 32)
                           ^ (uint)BitConverter.ToInt32(BitConverter.GetBytes(point.Y), 0);
                if (unique.ContainsKey(key)) continue;
                unique[key] = point;
                ordered.Add(point);
            }
            if (ordered.Count <= 2) return ordered;

            ordered.Sort((left, right) =>
                left.X != right.X ? left.X.CompareTo(right.X) : left.Y.CompareTo(right.Y));

            var lower = new List<NavVertex>();
            foreach (NavVertex point in ordered) {
                while (lower.Count >= 2 && Turn(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0) {
                    lower.RemoveAt(lower.Count - 1);
                }
                lower.Add(point);
            }

            var upper = new List<NavVertex>();
            for (int i = ordered.Count - 1; i >= 0; i--) {
                NavVertex point = ordered[i];
                while (upper.Count >= 2 && Turn(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0) {
                    upper.RemoveAt(upper.Count - 1);
                }
                upper.Add(point);
            }

            var hull = new List<NavVertex>(lower.Count + upper.Count);
            for (int i = 0; i < lower.Count - 1; i++) hull.Add(lower[i]);
            for (int i = 0; i < upper.Count - 1; i++) hull.Add(upper[i]);
            return hull.Count < 3 ? ordered : hull;
        }

        private static double Turn(NavVertex a, NavVertex b, NavVertex c) =>
            ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);

        private static double Distance(double ax, double ay, double bx, double by) {
            double dx = ax - bx, dy = ay - by;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static long EdgeKey(int a, int b) => NavMeshEdgeKey.Of(a, b);
    }
}
