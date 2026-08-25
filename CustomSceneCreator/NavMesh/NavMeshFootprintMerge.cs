using System;
using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>
    /// Joins footprints that stand too close together into a single outline before anything is cut.
    ///
    /// <b>Why this exists.</b> Cutouts are applied one after another, and the planner cannot cut a
    /// footprint that touches a hole an earlier one already made: the ring left behind is no longer
    /// a single loop, so the whole footprint is skipped. Two runs of fence meeting at a corner hit
    /// this every time, and the visible result is a walkable wedge exactly at the corner.
    ///
    /// Merging removes the situation rather than coping with it. Once footprints closer than
    /// <see cref="DefaultSeparation"/> have been combined, every remaining footprint is far enough
    /// from every other that no cut ever meets another cut's hole.
    ///
    /// <b>Why that threshold.</b> It is the width of ground worth keeping. A strip narrower than an
    /// agent is not somewhere anyone can walk - it survives as navmesh, is reported as connected,
    /// and leads agents into a gap they cannot pass. Cutting the whole area, buildings and the
    /// useless gap between them, is both simpler and more honest.
    ///
    /// <b>Why a grid.</b> The union of several rotated rectangles is an arbitrary polygon, and exact
    /// polygon clipping is a large amount of delicate code to get right. Filling cells, closing the
    /// gaps, and tracing the boundary gets the same answer to within a fifth of a metre - far finer
    /// than the clearance already built into every footprint. The error is in the safe direction
    /// too: the traced outline rounds outward, cutting slightly more ground from under a solid
    /// object.
    /// </summary>
    public static class NavMeshFootprintMerge {

        /// <summary>
        /// How close two footprints must be before they are cut as one, in metres.
        ///
        /// The engine's own authoring guidance asks for navmesh arranged in a 1.5 m diameter, so a
        /// gap narrower than that cannot hold usable ground however it is triangulated.
        /// </summary>
        public const double DefaultSeparation = 1.5;

        /// <summary>Grid resolution, in metres.</summary>
        private const double CellSize = 0.2;

        /// <summary>How far a traced outline may be simplified, in metres.</summary>
        private const double SimplifyTolerance = 0.25;

        /// <summary>
        /// Shortest edge a finished outline may have, in metres.
        ///
        /// Tracing a grid leaves steps of a fifth of a metre, and simplification turns most but not
        /// all of them into long straight runs - a diagonal corner keeps a couple of stubs. Every
        /// outline edge becomes a navmesh edge, and a stub edge becomes a repair triangle far below
        /// the smallest face the engine will load, which loses the whole cutout. Measured directly:
        /// a twenty-corner stable outline failed on a 0.109 m2 sliver until the stubs went.
        /// </summary>
        private const double MinimumOutlineEdge = 0.8;

        /// <summary>
        /// How much bigger than its parts a merged outline may be before it is rejected.
        ///
        /// A fence running most of the way round a yard can close into a ring, and the outline of a
        /// ring is the yard itself. Cutting that would delete the navmesh from a courtyard the player
        /// walks around in - far worse than the corner gap this pass exists to fix. When the outline
        /// swallows that much extra ground, the group is left alone and cut piece by piece.
        /// </summary>
        private const double MaximumAreaGrowth = 2.0;

        /// <summary>Refuses to grid a group whose bounds are implausibly large, in metres.</summary>
        private const double MaximumGroupExtent = 200.0;

        /// <summary>What merging did, for the bake log.</summary>
        public class MergeResult {
            public List<Point2[]> Footprints = new List<Point2[]>();
            /// <summary>
            /// The original rectangles behind each successful merged outline. A merged wall can
            /// touch the edge of a shipped navmesh even though most of its individual panels are
            /// safely over walkable ground. The baker uses these as a conservative fallback rather
            /// than leaving the entire wall walkable.
            /// </summary>
            public Dictionary<Point2[], List<Point2[]>> FallbackMembers =
                new Dictionary<Point2[], List<Point2[]>>();
            public int GroupsMerged;
            public int FootprintsConsumed;
            public List<string> Rejected = new List<string>();

            public string Describe() =>
                GroupsMerged == 0
                    ? "no footprints were close enough to merge"
                    : $"merged {FootprintsConsumed} footprint(s) into {GroupsMerged} combined outline(s)";
        }

        /// <summary>
        /// Groups footprints standing within <paramref name="separation"/> of one another and
        /// replaces each group with the outline of everything in it.
        ///
        /// Footprints with no close neighbour pass through untouched, so the ordinary case costs
        /// nothing but the proximity test.
        /// </summary>
        public static MergeResult Merge(IList<Point2[]> footprints,
                                        double separation = DefaultSeparation) {
            var result = new MergeResult();
            if (footprints == null || footprints.Count == 0) return result;

            foreach (List<int> group in GroupByProximity(footprints, separation)) {
                if (group.Count == 1) {
                    result.Footprints.Add(footprints[group[0]]);
                    continue;
                }

                var members = new List<Point2[]>(group.Count);
                double partsArea = 0.0;
                foreach (int index in group) {
                    members.Add(footprints[index]);
                    partsArea += Math.Abs(SignedArea(footprints[index]));
                }

                Point2[] outline = Outline(members, separation);
                string reason = Unusable(outline, partsArea);

                if (reason != null) {
                    // Falling back is not a loud failure - the pieces still get cut, just
                    // individually, which is what happened before this pass existed.
                    result.Rejected.Add($"{group.Count} footprints left unmerged: {reason}");
                    foreach (Point2[] member in members) result.Footprints.Add(member);
                    continue;
                }

                result.Footprints.Add(outline);
                result.FallbackMembers[outline] = members;
                result.GroupsMerged++;
                result.FootprintsConsumed += group.Count;
            }
            return result;
        }

        private static string Unusable(Point2[] outline, double partsArea) {
            if (outline == null || outline.Length < 3) {
                return "their combined outline could not be traced";
            }

            double area = Math.Abs(SignedArea(outline));
            if (area > partsArea * MaximumAreaGrowth) {
                return $"the combined outline ({area:0.#} m2) encloses far more ground than the "
                       + $"footprints themselves ({partsArea:0.#} m2), which usually means they ring "
                       + "an open area";
            }

            var points = new Dictionary<int, Point2>(outline.Length);
            var loop = new List<int>(outline.Length);
            for (int i = 0; i < outline.Length; i++) {
                points[i] = outline[i];
                loop.Add(i);
            }
            return NavMeshGeometry.IsSimplePolygon(loop, points)
                ? null
                : "their combined outline crosses itself";
        }

        // ---------------------------------------------------------------------------------------
        // Grouping
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Union-find grouping on "these two are within separation of each other".
        ///
        /// Proximity is transitive on purpose: three fence runs each close to the next become one
        /// outline, which is the point - a chain of buildings has no usable ground anywhere along it.
        /// </summary>
        private static List<List<int>> GroupByProximity(IList<Point2[]> footprints, double separation) {
            int count = footprints.Count;
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;

            var bounds = new Bounds[count];
            for (int i = 0; i < count; i++) {
                if (Usable(footprints, i)) bounds[i] = Bounds.Of(footprints[i]);
            }

            for (int i = 0; i < count; i++) {
                if (!Usable(footprints, i)) continue;
                for (int j = i + 1; j < count; j++) {
                    if (!Usable(footprints, j)) continue;
                    if (!Near(bounds[i], bounds[j], separation)) continue;
                    if (Separation(footprints[i], footprints[j]) > separation) continue;
                    Union(parent, i, j);
                }
            }

            var byRoot = new Dictionary<int, List<int>>();
            for (int i = 0; i < count; i++) {
                if (!Usable(footprints, i)) continue;
                int root = Find(parent, i);
                if (!byRoot.TryGetValue(root, out List<int> group)) {
                    group = new List<int>();
                    byRoot[root] = group;
                }
                group.Add(i);
            }

            var groups = new List<List<int>>(byRoot.Count);
            foreach (List<int> group in byRoot.Values) groups.Add(group);
            return groups;
        }

        private static bool Usable(IList<Point2[]> footprints, int index) =>
            footprints[index] != null && footprints[index].Length >= 3;

        /// <summary>Cheap rejection before the edge-by-edge distance test.</summary>
        private static bool Near(Bounds left, Bounds right, double separation) =>
            left.MinX - separation <= right.MaxX && right.MinX - separation <= left.MaxX
            && left.MinY - separation <= right.MaxY && right.MinY - separation <= left.MaxY;

        private static int Find(int[] parent, int index) {
            while (parent[index] != index) {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }

        private static void Union(int[] parent, int a, int b) {
            int rootA = Find(parent, a), rootB = Find(parent, b);
            if (rootA != rootB) parent[rootB] = rootA;
        }

        /// <summary>Closest approach between two polygons; zero when they overlap or touch.</summary>
        private static double Separation(Point2[] left, Point2[] right) {
            if (NavMeshGeometry.PolygonsIntersect(left, right)) return 0.0;

            double closest = double.MaxValue;
            for (int i = 0; i < left.Length; i++) {
                Point2 a = left[i], b = left[(i + 1) % left.Length];
                for (int j = 0; j < right.Length; j++) {
                    Point2 c = right[j], d = right[(j + 1) % right.Length];
                    double distance = SegmentDistance(a, b, c, d);
                    if (distance < closest) closest = distance;
                    if (closest <= 0.0) return 0.0;
                }
            }
            return closest;
        }

        private static double SegmentDistance(Point2 a, Point2 b, Point2 c, Point2 d) =>
            Math.Min(Math.Min(PointToSegment(a, c, d), PointToSegment(b, c, d)),
                     Math.Min(PointToSegment(c, a, b), PointToSegment(d, a, b)));

        private static double PointToSegment(Point2 point, Point2 a, Point2 b) {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;

            double t = lengthSquared < 1e-12
                ? 0.0
                : ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;

            double nx = a.X + t * dx - point.X, ny = a.Y + t * dy - point.Y;
            return Math.Sqrt(nx * nx + ny * ny);
        }

        // ---------------------------------------------------------------------------------------
        // Subtraction
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Smallest piece of leftover solid worth cutting, in square metres. Below this the panel is
        /// narrower than the faces around it and its repair ring would be slivers.
        /// </summary>
        private const double MinimumRemainderArea = 1.5;

        /// <summary>
        /// Returns the parts of <paramref name="footprint"/> not covered by <paramref name="holes"/>,
        /// as one polygon per surviving piece.
        ///
        /// <b>What this replaces.</b> A gatehouse has a passage through it, so its footprint cannot
        /// be cut whole - the passage has to stay walkable. The previous approach diced the entire
        /// building into 1.25 m grid cells, dropped the cells over the passage, and cut the rest one
        /// cell at a time. That turned twenty-three buildings into 372 footprints, and because a
        /// 1.25 m hole is smaller than the navmesh faces around it, the repair ring for each cell was
        /// usually a sliver: 69 of them were refused in a single group.
        ///
        /// Subtracting properly gives the same answer in two or three pieces instead of hundreds -
        /// typically the solid either side of the passage - each large enough to cut cleanly.
        ///
        /// Pieces are found as connected regions rather than a single outline, because that is
        /// exactly what a passage does: it separates one solid into two. Regions too small to cut
        /// safely are dropped, which is correct - they are the slivers of wall beside a doorway, and
        /// leaving them walkable costs nothing an agent can exploit.
        /// </summary>
        public static List<Point2[]> Subtract(Point2[] footprint, IList<Point2[]> holes, double margin) {
            var pieces = new List<Point2[]>();
            if (footprint == null || footprint.Length < 3) return pieces;
            if (holes == null || holes.Count == 0) {
                pieces.Add(footprint);
                return pieces;
            }

            Bounds bounds = Bounds.Of(footprint);
            if (bounds.MaxX - bounds.MinX > MaximumGroupExtent
                || bounds.MaxY - bounds.MinY > MaximumGroupExtent) {
                return pieces;
            }

            const int Margin = 2;
            double originX = bounds.MinX - Margin * CellSize;
            double originY = bounds.MinY - Margin * CellSize;
            int width = (int)Math.Ceiling((bounds.MaxX - bounds.MinX) / CellSize) + Margin * 2;
            int height = (int)Math.Ceiling((bounds.MaxY - bounds.MinY) / CellSize) + Margin * 2;
            if (width <= 0 || height <= 0) return pieces;

            var solid = new bool[width * height];
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    var centre = new Point2(originX + (x + 0.5) * CellSize,
                                            originY + (y + 0.5) * CellSize);
                    if (!NavMeshGeometry.PointInPolygon(centre, footprint)) continue;

                    bool reserved = false;
                    foreach (Point2[] hole in holes) {
                        if (WithinOrNear(centre, hole, margin)) { reserved = true; break; }
                    }
                    if (!reserved) solid[y * width + x] = true;
                }
            }

            foreach (bool[] region in Regions(solid, width, height)) {
                List<Point2> traced = Trace(region, width, height, originX, originY);
                if (traced == null || traced.Count < 4) continue;

                List<Point2> simplified = Simplify(traced, SimplifyTolerance);
                if (simplified.Count < 3) continue;
                if (Math.Abs(SignedArea(simplified)) < MinimumRemainderArea) continue;

                var points = new Dictionary<int, Point2>(simplified.Count);
                var loop = new List<int>(simplified.Count);
                for (int i = 0; i < simplified.Count; i++) { points[i] = simplified[i]; loop.Add(i); }
                if (!NavMeshGeometry.IsSimplePolygon(loop, points)) continue;

                pieces.Add(simplified.ToArray());
            }
            return pieces;
        }

        /// <summary>True when a point is inside a polygon or within <paramref name="margin"/> of it.</summary>
        private static bool WithinOrNear(Point2 point, Point2[] polygon, double margin) {
            if (NavMeshGeometry.PointInPolygon(point, polygon)) return true;
            for (int i = 0; i < polygon.Length; i++) {
                Point2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
                if (PointToSegment(point, a, b) <= margin) return true;
            }
            return false;
        }

        /// <summary>
        /// Splits a filled grid into its connected regions, four-connected.
        ///
        /// Four-connected rather than eight on purpose: two blocks of wall touching only at a corner
        /// are joined by nothing an agent could walk along, and tracing them as one region would
        /// produce an outline that pinches to a point - which is not a simple polygon.
        /// </summary>
        private static List<bool[]> Regions(bool[] filled, int width, int height) {
            var regions = new List<bool[]>();
            var seen = new bool[filled.Length];
            var queue = new Queue<int>();

            for (int start = 0; start < filled.Length; start++) {
                if (!filled[start] || seen[start]) continue;

                var region = new bool[filled.Length];
                queue.Enqueue(start);
                seen[start] = true;

                while (queue.Count > 0) {
                    int index = queue.Dequeue();
                    region[index] = true;
                    int x = index % width, y = index / width;

                    if (x > 0) Visit(filled, seen, queue, index - 1);
                    if (x < width - 1) Visit(filled, seen, queue, index + 1);
                    if (y > 0) Visit(filled, seen, queue, index - width);
                    if (y < height - 1) Visit(filled, seen, queue, index + width);
                }
                regions.Add(region);
            }
            return regions;
        }

        private static void Visit(bool[] filled, bool[] seen, Queue<int> queue, int index) {
            if (!filled[index] || seen[index]) return;
            seen[index] = true;
            queue.Enqueue(index);
        }

        // ---------------------------------------------------------------------------------------
        // Union outline
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Traces the outline of everything in a group, with gaps narrower than
        /// <paramref name="separation"/> filled in.
        ///
        /// Filling the gaps is a morphological closing - grow every filled cell, then shrink it back
        /// by the same amount. Growing alone would inflate the whole outline; growing and shrinking
        /// leaves the outer shape where it was and seals only the narrow channels between pieces,
        /// which is exactly the rule being asked for.
        /// </summary>
        private static Point2[] Outline(IList<Point2[]> members, double separation) {
            Bounds bounds = Bounds.Of(members[0]);
            for (int i = 1; i < members.Count; i++) bounds = Bounds.Union(bounds, Bounds.Of(members[i]));

            if (bounds.MaxX - bounds.MinX > MaximumGroupExtent
                || bounds.MaxY - bounds.MinY > MaximumGroupExtent) {
                return null;
            }

            // One cell of margin on every side keeps the traced boundary off the edge of the grid,
            // so the tracer never has to special-case running out of array.
            int radius = (int)Math.Ceiling(separation / 2.0 / CellSize);
            int margin = radius + 2;

            double originX = bounds.MinX - margin * CellSize;
            double originY = bounds.MinY - margin * CellSize;
            int width = (int)Math.Ceiling((bounds.MaxX - bounds.MinX) / CellSize) + margin * 2;
            int height = (int)Math.Ceiling((bounds.MaxY - bounds.MinY) / CellSize) + margin * 2;
            if (width <= 0 || height <= 0) return null;

            bool[] filled = Rasterize(members, originX, originY, width, height);
            filled = Close(filled, width, height, radius);

            List<Point2> traced = Trace(filled, width, height, originX, originY);
            if (traced == null || traced.Count < 4) return null;

            List<Point2> simplified = Simplify(traced, SimplifyTolerance);
            return simplified.Count >= 3 ? simplified.ToArray() : null;
        }

        /// <summary>
        /// Marks every cell whose centre lies inside one of the footprints.
        ///
        /// A footprint is at least a metre across in both directions - a building plus clearance -
        /// so testing centres against a fifth-of-a-metre grid cannot miss one entirely.
        /// </summary>
        private static bool[] Rasterize(IList<Point2[]> members,
                                        double originX, double originY, int width, int height) {
            var filled = new bool[width * height];

            foreach (Point2[] member in members) {
                Bounds memberBounds = Bounds.Of(member);
                int minX = Clamp((int)Math.Floor((memberBounds.MinX - originX) / CellSize) - 1, 0, width - 1);
                int maxX = Clamp((int)Math.Ceiling((memberBounds.MaxX - originX) / CellSize) + 1, 0, width - 1);
                int minY = Clamp((int)Math.Floor((memberBounds.MinY - originY) / CellSize) - 1, 0, height - 1);
                int maxY = Clamp((int)Math.Ceiling((memberBounds.MaxY - originY) / CellSize) + 1, 0, height - 1);

                for (int y = minY; y <= maxY; y++) {
                    for (int x = minX; x <= maxX; x++) {
                        if (filled[y * width + x]) continue;
                        var centre = new Point2(originX + (x + 0.5) * CellSize,
                                                originY + (y + 0.5) * CellSize);
                        if (NavMeshGeometry.PointInPolygon(centre, member)) filled[y * width + x] = true;
                    }
                }
            }
            return filled;
        }

        /// <summary>
        /// Dilate then erode by the same radius, sealing channels narrower than 2 x radius, then
        /// grow the result by one cell.
        ///
        /// That last cell is not cosmetic. In continuous space a closing leaves the original shape
        /// untouched; on a grid with a round kernel it shaves the corners, and measuring the result
        /// showed an outline about a tenth of a metre inside its own footprints on every side. That
        /// is the wrong direction to be wrong in - it leaves a rind of navmesh clinging to the wall
        /// of a building, which is precisely the sliver this whole pass exists to avoid. One cell
        /// back out puts the error on the side of cutting slightly too much.
        /// </summary>
        private static bool[] Close(bool[] filled, int width, int height, int radius) {
            if (radius <= 0) return Dilate(filled, width, height, 1);
            bool[] grown = Dilate(filled, width, height, radius);
            bool[] closed = Erode(grown, width, height, radius);
            return Dilate(closed, width, height, 1);
        }

        private static bool[] Dilate(bool[] source, int width, int height, int radius) {
            var target = new bool[source.Length];
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    if (!source[y * width + x]) continue;
                    for (int dy = -radius; dy <= radius; dy++) {
                        int ny = y + dy;
                        if (ny < 0 || ny >= height) continue;
                        for (int dx = -radius; dx <= radius; dx++) {
                            int nx = x + dx;
                            if (nx < 0 || nx >= width) continue;
                            if (dx * dx + dy * dy > radius * radius) continue;
                            target[ny * width + nx] = true;
                        }
                    }
                }
            }
            return target;
        }

        private static bool[] Erode(bool[] source, int width, int height, int radius) {
            var target = new bool[source.Length];
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    if (!source[y * width + x]) continue;

                    bool keep = true;
                    for (int dy = -radius; dy <= radius && keep; dy++) {
                        int ny = y + dy;
                        for (int dx = -radius; dx <= radius; dx++) {
                            int nx = x + dx;
                            if (dx * dx + dy * dy > radius * radius) continue;
                            if (nx < 0 || nx >= width || ny < 0 || ny >= height
                                || !source[ny * width + nx]) {
                                keep = false;
                                break;
                            }
                        }
                    }
                    target[y * width + x] = keep;
                }
            }
            return target;
        }

        /// <summary>
        /// Walks the outer boundary of the filled region, returning it as cell-corner points.
        ///
        /// This is square tracing: keep the filled region on one side and turn at every step. It
        /// only ever finds the OUTER boundary, which is deliberate - an interior hole is a courtyard
        /// the players walk in, and a cutout has no way to express "everything but this middle".
        /// A group whose outline swallows such a hole is rejected by the area check instead.
        /// </summary>
        private static List<Point2> Trace(bool[] filled, int width, int height,
                                          double originX, double originY) {
            int startIndex = -1;
            for (int i = 0; i < filled.Length; i++) {
                if (filled[i]) { startIndex = i; break; }
            }
            if (startIndex < 0) return null;

            int startX = startIndex % width, startY = startIndex / width;

            // Moore-neighbour tracing, clockwise from the west of the first filled cell found on a
            // bottom-up scan. Directions are ordered so the search sweeps around the current cell.
            int[] offsetX = { 1, 1, 0, -1, -1, -1, 0, 1 };
            int[] offsetY = { 0, 1, 1, 1, 0, -1, -1, -1 };

            var boundary = new List<Point2>();
            int currentX = startX, currentY = startY;
            int direction = 6;   // came from below
            int guard = width * height * 8;

            do {
                boundary.Add(new Point2(originX + currentX * CellSize, originY + currentY * CellSize));

                int search = (direction + 6) % 8;   // start looking two steps back, i.e. to the right
                int nextX = -1, nextY = -1, nextDirection = -1;

                for (int step = 0; step < 8; step++) {
                    int candidate = (search + step) % 8;
                    int testX = currentX + offsetX[candidate], testY = currentY + offsetY[candidate];
                    if (testX < 0 || testX >= width || testY < 0 || testY >= height) continue;
                    if (!filled[testY * width + testX]) continue;

                    nextX = testX;
                    nextY = testY;
                    nextDirection = candidate;
                    break;
                }

                if (nextX < 0) return boundary;   // an isolated cell; nothing to trace

                currentX = nextX;
                currentY = nextY;
                direction = nextDirection;
            } while ((currentX != startX || currentY != startY) && --guard > 0);

            return guard > 0 ? boundary : null;
        }

        /// <summary>
        /// Douglas-Peucker, so a traced outline becomes a handful of corners rather than hundreds of
        /// cell steps.
        ///
        /// This matters beyond tidiness: every outline vertex becomes a navmesh vertex and a repair
        /// triangle, and a staircase of 0.2 m steps would produce exactly the slivers the engine
        /// refuses to load.
        /// </summary>
        private static List<Point2> Simplify(List<Point2> points, double tolerance) {
            if (points.Count < 4) return points;

            // The outline is a closed loop, so it is simplified as two open chains split at the two
            // points furthest apart. Running the open-chain algorithm straight round a loop can
            // collapse it to nothing.
            int farthest = 0;
            double best = -1.0;
            for (int i = 1; i < points.Count; i++) {
                double distance = Distance(points[0], points[i]);
                if (distance > best) { best = distance; farthest = i; }
            }

            var first = points.GetRange(0, farthest + 1);
            var second = points.GetRange(farthest, points.Count - farthest);
            second.Add(points[0]);

            // Each chain emits its first point and everything up to - but not including - its last,
            // so the two chains join without repeating the point they share. Emitting it from both
            // would leave a zero-length edge, which reads downstream as a polygon that touches
            // itself.
            var simplified = new List<Point2>();
            Reduce(first, 0, first.Count - 1, tolerance, simplified);
            Reduce(second, 0, second.Count - 1, tolerance, simplified);
            return simplified;
        }

        private static void Reduce(List<Point2> points, int first, int last,
                                   double tolerance, List<Point2> output) {
            if (last <= first + 1) {
                output.Add(points[first]);
                return;
            }

            double worst = -1.0;
            int worstIndex = first;
            for (int i = first + 1; i < last; i++) {
                double distance = PointToSegment(points[i], points[first], points[last]);
                if (distance > worst) { worst = distance; worstIndex = i; }
            }

            if (worst <= tolerance) {
                output.Add(points[first]);
                return;
            }
            Reduce(points, first, worstIndex, tolerance, output);
            Reduce(points, worstIndex, last, tolerance, output);
        }

        /// <summary>
        /// Drops corners that sit too close to the one before them, so no edge is shorter than
        /// <see cref="MinimumOutlineEdge"/>.
        ///
        /// NOT CURRENTLY USED, and kept only so the next person does not try it again. It is an
        /// obvious response to the slivers a traced outline produces, and it makes things worse:
        /// on the seven-marker stable layout it took the bake from one cutout applied to none,
        /// because straightening the outline also pushed a corner off the edge of the navmesh. The
        /// slivers come from the outline being thin and convoluted, not from its corner count.
        ///
        /// Shape is barely affected - the corners removed are the stair-steps left over from the
        /// grid, not real features - and a corner that mattered survives because the run of short
        /// edges around it collapses to the one point furthest along.
        /// </summary>
        private static List<Point2> DropShortEdges(List<Point2> outline) {
            if (outline.Count < 4) return outline;

            var kept = new List<Point2> { outline[0] };
            for (int i = 1; i < outline.Count; i++) {
                if (Distance(outline[i], kept[kept.Count - 1]) >= MinimumOutlineEdge) {
                    kept.Add(outline[i]);
                }
            }

            // The loop closes back onto the first point, so the last corner has to clear it too.
            while (kept.Count > 3 && Distance(kept[kept.Count - 1], kept[0]) < MinimumOutlineEdge) {
                kept.RemoveAt(kept.Count - 1);
            }
            return kept.Count >= 3 ? kept : outline;
        }

        private static double Distance(Point2 a, Point2 b) {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static int Clamp(int value, int low, int high) =>
            value < low ? low : value > high ? high : value;

        public static double SignedArea(IList<Point2> polygon) {
            double sum = 0.0;
            for (int i = 0; i < polygon.Count; i++) {
                Point2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2.0;
        }
    }
}
