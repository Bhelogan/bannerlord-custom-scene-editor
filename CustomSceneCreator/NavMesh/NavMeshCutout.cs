using System;
using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>
    /// What a cutout would do, worked out without touching the mesh.
    ///
    /// Planning is separate from applying so the editor can tell the user a footprint is unusable
    /// while it is still a placement decision, rather than after a save has rewritten a navmesh.
    /// </summary>
    public class CutoutPlan {
        public Point2[] Corners = new Point2[0];
        public NavVertex[] ProjectedCorners = new NavVertex[0];
        public int[] AffectedFaces = new int[0];
        public int[] AffectedGroups = new int[0];
        public int[] OuterBoundaryVertices = new int[0];
        public int[] InternalVertices = new int[0];
        public bool FullyContained;
        public bool RepairableSingleLoop;
        public List<int[]> PlannedTriangles = new List<int[]>();
        public double TriangulationAreaError = double.NaN;

        /// <summary>True when the plan can actually be applied.</summary>
        public bool CanApply => FullyContained && RepairableSingleLoop && AffectedGroups.Length == 1
                                && PlannedTriangles.Count > 0;

        /// <summary>Why the plan cannot be applied; empty when it can. Test <see cref="CanApply"/> first.</summary>
        public string BlockingReason {
            get {
                if (!FullyContained) return "the footprint reaches past the edge of the navmesh";
                if (!RepairableSingleLoop) return "the affected faces do not form one repairable ring";
                if (AffectedGroups.Length != 1) return "the footprint crosses " + AffectedGroups.Length + " face groups";
                if (PlannedTriangles.Count == 0) return "no repair ring could be built";
                return "";
            }
        }
    }

    /// <summary>What actually changed, for logging and for the harness to compare against Python.</summary>
    public class CutoutResult {
        public int FacesRemoved;
        public int RepairFacesAdded;
        public int VerticesBefore, VerticesAfter;
        public int EdgesBefore, EdgesAfter;
        public int FacesBefore, FacesAfter;
        public double TriangulationAreaError;
        public NavMeshSummary Before = new NavMeshSummary();
        public NavMeshSummary After = new NavMeshSummary();

        /// <summary>
        /// How many pieces the footprint had to be broken into, and how many of those could still
        /// not be cut. Both are zero for the ordinary case of a footprint cut whole.
        ///
        /// Worth reporting rather than hiding: a partly cut footprint is a partly walkable building,
        /// and the count is the difference between "a corner overlapped its neighbour" and "most of
        /// this fence is still walk-through".
        /// </summary>
        public int PiecesCut, PiecesAbandoned;

        /// <summary>Ground the abandoned pieces would have covered, in square metres.</summary>
        public double AbandonedArea;

        public string Describe() {
            string text =
                $"removed {FacesRemoved} faces, added {RepairFacesAdded}; "
                + $"v {VerticesBefore}->{VerticesAfter}, e {EdgesBefore}->{EdgesAfter}, "
                + $"f {FacesBefore}->{FacesAfter}";

            // Empty summaries mean this cutout was applied as part of a batch whose structure is
            // checked once at the end. Printing "components 0->0" would read as a broken mesh.
            if (PiecesAbandoned > 0) {
                text += $"; split into {PiecesCut + PiecesAbandoned} pieces and {PiecesAbandoned} "
                        + $"could not be cut ({AbandonedArea:0.#} m2 left walkable)";
            } else if (PiecesCut > 1) {
                text += $"; split into {PiecesCut} pieces";
            }

            return Before.Faces > 0
                ? text + $"; components {Before.ConnectedFaceComponents}->{After.ConnectedFaceComponents}"
                : text;
        }
    }

    /// <summary>
    /// Cuts a footprint out of a navmesh and re-triangulates the ring left behind.
    ///
    /// This is the C# port of the Python cutout tools, and it works entirely in memory on decoded
    /// data - no file paths, no TaleWorlds types. That is what lets the editor run it at save time
    /// and lets another mod (Homesteads Reloaded) call it when a settlement scene is written.
    ///
    /// The passes are conservative by design. Anything ambiguous is refused rather than guessed at,
    /// because a wrong navmesh does not throw: agents simply walk through walls, or refuse to path,
    /// and the cause is invisible from inside the game.
    /// </summary>
    public static class NavMeshCutout {

        /// <summary>
        /// Works out the affected patch, its boundary, and the repair triangulation.
        ///
        /// Read-only. A plan whose <see cref="CutoutPlan.CanApply"/> is false is still returned in
        /// full, because the reason it failed is what the user needs to see.
        /// </summary>
        public static CutoutPlan Plan(NavMeshData data, Point2[] corners) {
            return Plan(data, corners, double.NegativeInfinity, double.PositiveInfinity);
        }

        /// <summary>
        /// Plans a polygon cutout against only faces intersecting the requested vertical band.
        /// This is essential when two walkable storeys overlap in XY.
        /// </summary>
        public static CutoutPlan Plan(NavMeshData data, Point2[] corners, double minZ, double maxZ) {
            // Any simple polygon, not only a quad. Merged footprints are L-shaped, U-shaped or
            // worse - a fence running round two sides of a yard is the ordinary case - and forcing
            // those back into a rectangle would cut the yard out along with the fence.
            if (corners == null || corners.Length < 3) {
                throw new NavMeshFormatException("a cutout needs at least three corners");
            }
            foreach (Point2 corner in corners) {
                if (double.IsNaN(corner.X) || double.IsInfinity(corner.X) ||
                    double.IsNaN(corner.Y) || double.IsInfinity(corner.Y)) {
                    throw new NavMeshFormatException("cutout corners must be finite");
                }
            }
            if (double.IsNaN(minZ) || double.IsNaN(maxZ) || minZ > maxZ) {
                throw new NavMeshFormatException("the cutout height range is invalid");
            }

            var plan = new CutoutPlan { Corners = corners };

            // A footprint is a few metres across on a mesh that spans hundreds, so almost every face
            // is irrelevant. Rejecting those on a bounding-box test first - before allocating a
            // polygon for them, let alone intersecting it - is the difference between a bake that
            // takes a moment and one that looks like the game has frozen.
            Bounds footprintBounds = Bounds.Of(corners);
            var facePolygons = new Point2[data.Faces.Count][];
            var affected = new List<int>();

            for (int i = 0; i < data.Faces.Count; i++) {
                NavFace face = data.Faces[i];
                if (!OverlapsFace(data, face, footprintBounds)) continue;
                if (!OverlapsHeightBand(data, face, minZ, maxZ)) continue;

                var polygon = new Point2[face.Degree];
                for (int v = 0; v < face.Degree; v++) {
                    NavVertex vertex = data.Vertices[(int)face.Vertices[v]];
                    polygon[v] = new Point2(vertex.X, vertex.Y);
                }
                facePolygons[i] = polygon;

                if (NavMeshGeometry.PolygonsIntersect(polygon, corners)) affected.Add(i);
            }
            if (affected.Count == 0) {
                throw new NavMeshFormatException("the cutout footprint does not touch any navmesh face");
            }
            plan.AffectedFaces = affected.ToArray();

            plan.OuterBoundaryVertices = BoundaryLoop(affected, data, out bool repairable);
            plan.RepairableSingleLoop = repairable;

            var boundarySet = new HashSet<int>(plan.OuterBoundaryVertices);
            var internalVertices = new List<int>();
            var used = new HashSet<int>();
            foreach (int faceIndex in affected) {
                foreach (uint vertex in data.Faces[faceIndex].Vertices) used.Add((int)vertex);
            }
            foreach (int vertex in used) {
                if (!boundarySet.Contains(vertex)) internalVertices.Add(vertex);
            }
            internalVertices.Sort();
            plan.InternalVertices = internalVertices.ToArray();

            // Corner Z comes from the navmesh surface, not from the authored footprint. An object's
            // base can sit well below the ground it stands on, and taking that Z would sink the
            // repair ring through the floor.
            var projected = new NavVertex[corners.Length];
            plan.FullyContained = true;
            for (int i = 0; i < corners.Length; i++) {
                int containing = -1;
                foreach (int faceIndex in affected) {
                    if (!NavMeshGeometry.PointInPolygon(corners[i], facePolygons[faceIndex])) continue;
                    containing = faceIndex;
                    break;
                }
                if (containing < 0) {
                    plan.FullyContained = false;
                    projected[i] = new NavVertex((float)corners[i].X, (float)corners[i].Y, float.NaN);
                    continue;
                }
                double z = NavMeshGeometry.InterpolateZ(
                    corners[i], data.Faces[containing].Vertices, data.Vertices);
                projected[i] = new NavVertex((float)corners[i].X, (float)corners[i].Y, (float)z);
            }
            plan.ProjectedCorners = projected;

            var groups = new List<int>();
            foreach (int faceIndex in affected) {
                int group = data.Faces[faceIndex].Group;
                if (!groups.Contains(group)) groups.Add(group);
            }
            groups.Sort();
            plan.AffectedGroups = groups.ToArray();

            if (plan.RepairableSingleLoop && plan.FullyContained) {
                int firstNewVertex = data.Vertices.Count;
                var holeIndices = new int[corners.Length];

                // Only the boundary ring and the new corners take part in the triangulation.
                // Copying the whole mesh's vertices in here cost more than the triangulation itself.
                var points = new Dictionary<int, Point2>(
                    plan.OuterBoundaryVertices.Length + corners.Length);
                foreach (int index in plan.OuterBoundaryVertices) {
                    points[index] = new Point2(data.Vertices[index].X, data.Vertices[index].Y);
                }
                for (int i = 0; i < corners.Length; i++) {
                    holeIndices[i] = firstNewVertex + i;
                    points[holeIndices[i]] = corners[i];
                }
                List<int[]> repairFaces = NavMeshGeometry.TriangulateWithHole(
                    plan.OuterBoundaryVertices, holeIndices, points, out double areaError);

                // Slivers are merged into their neighbours rather than refused. Ear clipping cannot
                // avoid producing some, and losing a whole cutout over one thin triangle meant losing
                // about half the buildings on a real homestead.
                plan.PlannedTriangles = NavMeshGeometry.MergeSlivers(repairFaces, points);
                plan.TriangulationAreaError = areaError;
            }
            return plan;
        }

        private static bool OverlapsHeightBand(NavMeshData data, NavFace face,
                                               double minZ, double maxZ) {
            double faceMin = double.PositiveInfinity;
            double faceMax = double.NegativeInfinity;
            foreach (uint index in face.Vertices) {
                double z = data.Vertices[(int)index].Z;
                faceMin = Math.Min(faceMin, z);
                faceMax = Math.Max(faceMax, z);
            }
            return faceMax >= minZ && faceMin <= maxZ;
        }

        /// <summary>
        /// Applies a plan to the mesh in place.
        ///
        /// Vertices and edges are only ever added; nothing is renumbered. Faces that reference the
        /// removed patch are the only things deleted. Keeping indices stable is what makes several
        /// cutouts on one mesh safe to apply one after another.
        /// </summary>
        public static CutoutResult Apply(NavMeshData data, CutoutPlan plan) =>
            Apply(data, plan, validate: true);

        /// <summary>
        /// Applies a plan, optionally skipping the structural check.
        /// </summary>
        /// <param name="validate">
        /// False only when a caller is applying several cutouts in a row and will check the result
        /// once at the end. Each check walks the whole mesh, so doing it per cutout makes a bake with
        /// thirty buildings take thirty times longer than it needs to.
        /// </param>
        public static CutoutResult Apply(NavMeshData data, CutoutPlan plan, bool validate) {
            if (!plan.CanApply) {
                throw new NavMeshFormatException("cannot apply cutout: " + plan.BlockingReason);
            }

            NavMeshSummary before = validate ? NavMeshValidation.Summarize(data) : new NavMeshSummary();
            if (validate && !before.IsStructurallySound) {
                throw new NavMeshFormatException(
                    "refusing to edit a navmesh that is already structurally unsound: " + before.Describe());
            }

            // New edges are written as (-1, start, end, -1, -1, 0). That is only safe if the file
            // already uses that convention everywhere - otherwise the edges added here would carry
            // different auxiliary fields from the ones beside them, and there is no basis for
            // guessing what the game expects in them.
            foreach (NavEdge edge in data.Edges) {
                if (edge.A != -1 || edge.D != -1 || edge.E != -1 || edge.F != 0) {
                    throw new NavMeshFormatException(
                        "this navmesh uses edge fields this editor does not understand; refusing to edit it");
                }
            }

            var affectedSet = new HashSet<int>(plan.AffectedFaces);

            // New faces COPY the metadata of the faces they replace. Nothing here invents a value.
            //
            // This code does not know what most of these integers mean. A shipped battle terrain uses
            // exactly two patterns - (2,0,0,0,15) and (2,0,0,0,-1) - and writing anything outside that
            // set produces a file the engine will not load, with no error: it stalls and dies. An
            // earlier version marked repair faces with -15, following a convention seen in a
            // Modding-Kit-rebuilt file, and that single invented value was enough to do it.
            NavFace reference = data.Faces[plan.AffectedFaces[0]];
            byte direction = reference.Direction;

            // Only the last integer is ever seen to vary between neighbouring faces, so the first four
            // must agree exactly; disagreement there means the footprint spans genuinely different
            // kinds of ground and is not something to guess about.
            foreach (int faceIndex in affectedSet) {
                NavFace face = data.Faces[faceIndex];
                if (!SameLeadingMetadata(reference.Metadata, face.Metadata)
                    || direction != face.Direction) {
                    throw new NavMeshFormatException(
                        "the footprint covers faces with incompatible navmesh metadata");
                }
            }

            // Where the last integer does differ across the patch, the value most of the removed faces
            // carried is the one the replacements get. Both observed values occur on ordinary walkable
            // ground, so refusing a building for straddling the boundary between them would cost more
            // than picking the dominant side.
            var repairMetadata = (int[])reference.Metadata.Clone();
            repairMetadata[4] = MajorityLastMetadata(data, plan.AffectedFaces);

            int verticesBefore = data.Vertices.Count;
            int edgesBefore = data.Edges.Count;
            int facesBefore = data.Faces.Count;

            foreach (NavVertex corner in plan.ProjectedCorners) data.Vertices.Add(corner);

            // The repair triangles only ever join ring vertices to each other or to the four new
            // corners, so only edges between ring vertices can be reused. Indexing the whole mesh's
            // edges to find those was most of the cost of a cutout.
            var ringVertices = new HashSet<int>(plan.OuterBoundaryVertices);
            Dictionary<long, int> edgeLookup = NavMeshEdgeKey.NewMap(plan.OuterBoundaryVertices.Length * 2);

            for (int i = 0; i < data.Edges.Count; i++) {
                NavEdge edge = data.Edges[i];
                if (!ringVertices.Contains(edge.B) || !ringVertices.Contains(edge.C)) continue;

                long key = EdgeKey(edge.B, edge.C);
                if (!edgeLookup.ContainsKey(key)) edgeLookup[key] = i;
            }

            var keptFaces = new List<NavFace>(data.Faces.Count);
            for (int i = 0; i < data.Faces.Count; i++) {
                if (!affectedSet.Contains(i)) keptFaces.Add(data.Faces[i]);
            }

            var repairPoints = new Dictionary<int, Point2>();
            foreach (int[] repairFace in plan.PlannedTriangles) {
                foreach (int index in repairFace) {
                    repairPoints[index] = new Point2(data.Vertices[index].X, data.Vertices[index].Y);
                }
            }

            foreach (int[] repairFace in plan.PlannedTriangles) {
                if (NavMeshGeometry.SignedArea(repairFace, repairPoints) <= 0.0) {
                    throw new NavMeshFormatException("the repair ring produced an inverted face");
                }

                // Measured against what the game ships, not against what is mathematically valid. A
                // sliver far below the engine's own floor loads no scene at all - it stalls and dies
                // with no error - so it is refused here, where the reason is still knowable. Merging
                // turns most would-be slivers into quads long before this is reached.
                if (!NavMeshGeometry.IsAcceptableFace(repairFace, repairPoints)) {
                    double slice = Math.Abs(NavMeshGeometry.SignedArea(repairFace, repairPoints));
                    throw new NavMeshFormatException(
                        $"the repair ring produced a sliver ({slice:0.###} m2); the engine ships "
                        + $"nothing below {NavMeshGeometry.MinimumFaceArea:0.##} m2");
                }

                var faceVertices = new uint[repairFace.Length];
                var faceEdges = new uint[repairFace.Length];

                for (int index = 0; index < repairFace.Length; index++) {
                    int start = repairFace[index];
                    int end = repairFace[(index + 1) % repairFace.Length];
                    long key = EdgeKey(start, end);

                    if (!edgeLookup.TryGetValue(key, out int edgeIndex)) {
                        edgeIndex = data.Edges.Count;
                        edgeLookup[key] = edgeIndex;
                        data.Edges.Add(new NavEdge(-1, start, end, -1, -1, 0));
                    }
                    faceVertices[index] = (uint)start;
                    faceEdges[index] = (uint)edgeIndex;
                }

                keptFaces.Add(new NavFace(faceVertices, faceEdges,
                                          (int[])repairMetadata.Clone(), direction));
            }

            data.Faces = keptFaces;

            // An edited mesh is always NMG9 from here on. NMG8 differs only by the missing sixth
            // edge integer, and the Modding Kit performs the same zero-filled upgrade when it saves
            // navigation, so following it keeps our output in a shape the tools already accept.
            data.Signature = "NMG9";

            // The footprint interior must now genuinely be empty. A topologically valid ring can
            // still cover the hole it was supposed to leave, and only sampling catches that.
            AssertInteriorIsClear(data, plan.Corners);

            NavMeshSummary after = validate ? NavMeshValidation.Summarize(data) : new NavMeshSummary();
            if (validate) {
                if (!after.IsStructurallySound) {
                    throw new NavMeshFormatException("the cutout produced an unsound navmesh: " + after.Describe());
                }
                if (after.ConnectedFaceComponents != before.ConnectedFaceComponents) {
                    throw new NavMeshFormatException(
                        $"the cutout split the navmesh into {after.ConnectedFaceComponents} pieces "
                        + $"(was {before.ConnectedFaceComponents}); agents would lose their path");
                }
            }

            return new CutoutResult {
                FacesRemoved = affectedSet.Count,
                RepairFacesAdded = plan.PlannedTriangles.Count,
                VerticesBefore = verticesBefore,
                VerticesAfter = data.Vertices.Count,
                EdgesBefore = edgesBefore,
                EdgesAfter = data.Edges.Count,
                FacesBefore = facesBefore,
                FacesAfter = data.Faces.Count,
                TriangulationAreaError = plan.TriangulationAreaError,
                Before = before,
                After = after,
            };
        }

        /// <summary>Plans and applies in one call.</summary>
        public static CutoutResult Apply(NavMeshData data, Point2[] corners) =>
            Apply(data, Plan(data, corners));

        private static void AssertInteriorIsClear(NavMeshData data, Point2[] corners) {
            // Every sample lies inside the footprint, so a face that does not reach the footprint
            // cannot contain one. Same rejection as the planner, and the same reason.
            Bounds footprintBounds = Bounds.Of(corners);
            var polygons = new List<Point2[]>();

            foreach (NavFace face in data.Faces) {
                if (!OverlapsFace(data, face, footprintBounds)) continue;

                var polygon = new Point2[face.Degree];
                for (int v = 0; v < face.Degree; v++) {
                    NavVertex vertex = data.Vertices[(int)face.Vertices[v]];
                    polygon[v] = new Point2(vertex.X, vertex.Y);
                }
                polygons.Add(polygon);
            }

            for (int row = 1; row < 5; row++) {
                double v = row / 5.0;
                var left = new Point2(
                    corners[0].X + (corners[3].X - corners[0].X) * v,
                    corners[0].Y + (corners[3].Y - corners[0].Y) * v);
                var right = new Point2(
                    corners[1].X + (corners[2].X - corners[1].X) * v,
                    corners[1].Y + (corners[2].Y - corners[1].Y) * v);

                for (int column = 1; column < 5; column++) {
                    double u = column / 5.0;
                    var sample = new Point2(
                        left.X + (right.X - left.X) * u,
                        left.Y + (right.Y - left.Y) * u);

                    foreach (Point2[] polygon in polygons) {
                        if (NavMeshGeometry.PointInPolygon(sample, polygon)) {
                            throw new NavMeshFormatException(
                                $"a repair face still covers the cutout interior at "
                                + $"({sample.X:F3}, {sample.Y:F3})");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Walks the boundary of a face patch, returning its vertex loop.
        ///
        /// Boundary edges are the ones used by exactly one face in the patch. If those edges do not
        /// join into a single closed loop with every vertex having exactly two neighbours, the patch
        /// cannot be repaired by a simple ring, and the caller is told so rather than sold a guess.
        /// </summary>
        private static int[] BoundaryLoop(List<int> faceIndices, NavMeshData data, out bool repairable) {
            var counts = new Dictionary<uint, int>();
            foreach (int faceIndex in faceIndices) {
                foreach (uint edgeIndex in data.Faces[faceIndex].Edges) {
                    counts.TryGetValue(edgeIndex, out int count);
                    counts[edgeIndex] = count + 1;
                }
            }

            var adjacency = new Dictionary<int, List<int>>();
            foreach (KeyValuePair<uint, int> entry in counts) {
                if (entry.Value != 1) continue;
                NavEdge edge = data.Edges[(int)entry.Key];
                AddNeighbor(adjacency, edge.B, edge.C);
                AddNeighbor(adjacency, edge.C, edge.B);
            }

            if (adjacency.Count == 0) {
                repairable = false;
                return new int[0];
            }
            foreach (List<int> neighbors in adjacency.Values) {
                if (neighbors.Count == 2) continue;
                repairable = false;
                return SortedKeys(adjacency);
            }

            int start = int.MaxValue;
            foreach (int key in adjacency.Keys) {
                if (key < start) start = key;
            }

            var loop = new List<int> { start };
            var seen = new HashSet<int> { start };
            int previous = -1;
            int current = start;

            while (true) {
                List<int> choices = adjacency[current];
                int following = choices[0] != previous ? choices[0] : choices[1];
                if (following == start) break;
                if (!seen.Add(following)) {
                    repairable = false;
                    return loop.ToArray();
                }
                loop.Add(following);
                previous = current;
                current = following;
            }

            repairable = loop.Count == adjacency.Count;
            return loop.ToArray();
        }

        private static void AddNeighbor(Dictionary<int, List<int>> adjacency, int from, int to) {
            if (!adjacency.TryGetValue(from, out List<int> neighbors)) {
                neighbors = new List<int>(2);
                adjacency[from] = neighbors;
            }
            neighbors.Add(to);
        }

        private static int[] SortedKeys(Dictionary<int, List<int>> adjacency) {
            var keys = new List<int>(adjacency.Keys);
            keys.Sort();
            return keys.ToArray();
        }

        /// <summary>Compares everything except the last integer, the only one seen to vary.</summary>
        private static bool SameLeadingMetadata(int[] left, int[] right) {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length - 1; i++) {
                if (left[i] != right[i]) return false;
            }
            return true;
        }

        /// <summary>The last metadata integer carried by most of the faces being removed.</summary>
        private static int MajorityLastMetadata(NavMeshData data, int[] affectedFaces) {
            var tally = new Dictionary<int, int>();
            foreach (int faceIndex in affectedFaces) {
                int[] metadata = data.Faces[faceIndex].Metadata;
                int value = metadata[metadata.Length - 1];
                tally.TryGetValue(value, out int seen);
                tally[value] = seen + 1;
            }

            int best = 0, bestCount = -1;
            foreach (KeyValuePair<int, int> entry in tally) {
                if (entry.Value <= bestCount) continue;
                best = entry.Key;
                bestCount = entry.Value;
            }
            return best;
        }

        private static long EdgeKey(int a, int b) => NavMeshEdgeKey.Of(a, b);

        /// <summary>
        /// True when a face could possibly touch the box.
        ///
        /// Deliberately generous - it answers "worth looking at", not "intersects". The margin covers
        /// the tolerance the exact tests use, so a face this rejects can never have been accepted by
        /// the real intersection test.
        /// </summary>
        private static bool OverlapsFace(NavMeshData data, NavFace face, Bounds box) {
            const double margin = 1e-3;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (uint index in face.Vertices) {
                NavVertex vertex = data.Vertices[(int)index];
                if (vertex.X < minX) minX = vertex.X;
                if (vertex.X > maxX) maxX = vertex.X;
                if (vertex.Y < minY) minY = vertex.Y;
                if (vertex.Y > maxY) maxY = vertex.Y;
            }

            return maxX >= box.MinX - margin && minX <= box.MaxX + margin
                && maxY >= box.MinY - margin && minY <= box.MaxY + margin;
        }
    }
}
