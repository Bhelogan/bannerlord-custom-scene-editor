using System;
using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>
    /// An axis-aligned XY box, used to reject work cheaply before any real geometry runs.
    ///
    /// Shared rather than per-file because the cutout planner and the footprint merger reject
    /// against the same boxes, and two copies of a bounding box would be two chances to disagree
    /// about what "touching" means.
    /// </summary>
    public struct Bounds {
        public double MinX, MinY, MaxX, MaxY;

        public static Bounds Of(IList<Point2> points) {
            var bounds = new Bounds {
                MinX = double.MaxValue, MinY = double.MaxValue,
                MaxX = double.MinValue, MaxY = double.MinValue,
            };
            foreach (Point2 point in points) {
                if (point.X < bounds.MinX) bounds.MinX = point.X;
                if (point.X > bounds.MaxX) bounds.MaxX = point.X;
                if (point.Y < bounds.MinY) bounds.MinY = point.Y;
                if (point.Y > bounds.MaxY) bounds.MaxY = point.Y;
            }
            return bounds;
        }

        public static Bounds Union(Bounds left, Bounds right) => new Bounds {
            MinX = Math.Min(left.MinX, right.MinX), MinY = Math.Min(left.MinY, right.MinY),
            MaxX = Math.Max(left.MaxX, right.MaxX), MaxY = Math.Max(left.MaxY, right.MaxY),
        };
    }


    /// <summary>A point in the XY plane. Navmesh work is planar; Z is carried, not reasoned about.</summary>
    public struct Point2 {
        public double X;
        public double Y;

        public Point2(double x, double y) { X = x; Y = y; }
    }

    /// <summary>
    /// The 2D predicates the cutout and addition passes are built from.
    ///
    /// All of it works in the XY plane and treats Z as a value to be interpolated afterwards. That is
    /// not a simplification of the problem - navmesh faces are walkable surfaces, so two faces never
    /// legitimately overlap in XY, and planar reasoning is therefore exact rather than approximate.
    ///
    /// The tolerances are deliberately explicit and shared. Geometry code that invents a different
    /// epsilon in each function produces answers that disagree with each other, and the failure looks
    /// like a bad mesh rather than a bad constant.
    /// </summary>
    public static class NavMeshGeometry {

        /// <summary>Collinearity and on-segment tolerance, in metres.</summary>
        public const double OnSegmentEpsilon = 1e-5;

        /// <summary>Strict-interior tolerance for ear clipping.</summary>
        public const double InteriorEpsilon = 1e-6;

        /// <summary>
        /// The smallest face the engine is known to accept, in square metres.
        ///
        /// Measured, not guessed: across the 68,244 faces of a shipped battle terrain the smallest is
        /// 0.85 m2 and the shortest edge 0.48 m. Nothing smaller than that appears in any navmesh the
        /// game ships, and feeding it a sliver an order of magnitude below that floor is the
        /// difference between a scene that loads and one that stalls and dies without an error.
        /// </summary>
        public const double MinimumFaceArea = 0.5;

        /// <summary>The shortest edge the engine is known to accept, in metres.</summary>
        public const double MinimumEdgeLength = 0.3;

        /// <summary>
        /// The size faces are aimed at, from TaleWorlds' own authoring guidance: "arrange the
        /// navigation mesh in a 1.5-meter diameter".
        ///
        /// This is a target, not a limit. Faces below it are candidates for merging into a neighbour;
        /// faces below <see cref="MinimumFaceArea"/> are refused outright. Separating the two is what
        /// lets the merge pass work hard to produce authored-looking geometry without throwing away a
        /// cutout that merely ended up a bit smaller than ideal.
        /// </summary>
        public const double TargetEdgeLength = 1.5;

        /// <summary>The area of a 1.5 m square - the shape the guidance describes.</summary>
        public const double TargetFaceArea = TargetEdgeLength * TargetEdgeLength;

        /// <summary>Shape quality of a triangle: 1 for equilateral, approaching 0 for a sliver.</summary>
        public static double TriangleQuality(Point2 a, Point2 b, Point2 c) {
            double area = Math.Abs(Cross(a, b, c)) / 2.0;
            double sides = SquaredLength(a, b) + SquaredLength(b, c) + SquaredLength(c, a);
            return sides <= 0.0 ? 0.0 : 4.0 * Math.Sqrt(3.0) * area / sides;
        }

        /// <summary>The shortest side of a triangle, in metres.</summary>
        public static double ShortestEdge(Point2 a, Point2 b, Point2 c) =>
            Math.Sqrt(Math.Min(SquaredLength(a, b), Math.Min(SquaredLength(b, c), SquaredLength(c, a))));

        private static double SquaredLength(Point2 a, Point2 b) {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        public static double Cross(Point2 a, Point2 b, Point2 c) =>
            (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        public static bool OnSegment(Point2 a, Point2 b, Point2 p) =>
            Math.Abs(Cross(a, b, p)) <= OnSegmentEpsilon
            && Math.Min(a.X, b.X) - OnSegmentEpsilon <= p.X && p.X <= Math.Max(a.X, b.X) + OnSegmentEpsilon
            && Math.Min(a.Y, b.Y) - OnSegmentEpsilon <= p.Y && p.Y <= Math.Max(a.Y, b.Y) + OnSegmentEpsilon;

        public static bool SegmentsIntersect(Point2 a, Point2 b, Point2 c, Point2 d) {
            double v0 = Cross(a, b, c), v1 = Cross(a, b, d), v2 = Cross(c, d, a), v3 = Cross(c, d, b);

            // Touching or collinear cases are resolved by containment rather than by sign, because
            // the sign test is meaningless once a cross product is within tolerance of zero.
            if (Math.Abs(v0) <= OnSegmentEpsilon || Math.Abs(v1) <= OnSegmentEpsilon ||
                Math.Abs(v2) <= OnSegmentEpsilon || Math.Abs(v3) <= OnSegmentEpsilon) {
                return OnSegment(a, b, c) || OnSegment(a, b, d)
                    || OnSegment(c, d, a) || OnSegment(c, d, b);
            }
            return (v0 > 0) != (v1 > 0) && (v2 > 0) != (v3 > 0);
        }

        /// <summary>Even-odd containment. A point exactly on an edge counts as inside.</summary>
        public static bool PointInPolygon(Point2 point, IList<Point2> polygon) {
            bool inside = false;
            Point2 previous = polygon[polygon.Count - 1];

            foreach (Point2 current in polygon) {
                if (OnSegment(previous, current, point)) return true;

                if ((current.Y > point.Y) != (previous.Y > point.Y)) {
                    double crossingX = (previous.X - current.X) * (point.Y - current.Y)
                                       / (previous.Y - current.Y) + current.X;
                    if (point.X < crossingX) inside = !inside;
                }
                previous = current;
            }
            return inside;
        }

        /// <summary>True when two polygons share any area or boundary.</summary>
        public static bool PolygonsIntersect(IList<Point2> left, IList<Point2> right) {
            foreach (Point2 point in left) {
                if (PointInPolygon(point, right)) return true;
            }
            foreach (Point2 point in right) {
                if (PointInPolygon(point, left)) return true;
            }

            for (int i = 0; i < left.Count; i++) {
                Point2 a = left[i], b = left[(i + 1) % left.Count];
                for (int j = 0; j < right.Count; j++) {
                    if (SegmentsIntersect(a, b, right[j], right[(j + 1) % right.Count])) return true;
                }
            }
            return false;
        }

        public static double SignedArea(IList<int> indices, IDictionary<int, Point2> points) {
            double total = 0.0;
            for (int i = 0; i < indices.Count; i++) {
                Point2 a = points[indices[i]];
                Point2 b = points[indices[(i + 1) % indices.Count]];
                total += a.X * b.Y - b.X * a.Y;
            }
            return total / 2.0;
        }

        public static bool StrictlyInsideTriangle(Point2 point, Point2 a, Point2 b, Point2 c) {
            double v0 = Cross(a, b, point), v1 = Cross(b, c, point), v2 = Cross(c, a, point);
            return (v0 > InteriorEpsilon && v1 > InteriorEpsilon && v2 > InteriorEpsilon)
                || (v0 < -InteriorEpsilon && v1 < -InteriorEpsilon && v2 < -InteriorEpsilon);
        }

        /// <summary>
        /// Finds the shortest unobstructed line joining an outer ring to a hole ring.
        ///
        /// Ear clipping cannot handle a polygon with a hole, so the hole is stitched into the outer
        /// ring along this bridge, turning two rings into one traversable loop. The bridge must not
        /// cross either ring, and its midpoint must lie in the material between them - otherwise the
        /// stitch runs through the hole and the triangulation covers ground that should be empty.
        /// </summary>
        public static void FindVisibleBridge(List<int> outer, List<int> hole,
                                             IDictionary<int, Point2> points,
                                             out int outerPosition, out int holePosition) {
            var outerPolygon = new List<Point2>(outer.Count);
            foreach (int index in outer) outerPolygon.Add(points[index]);

            var holePolygon = new List<Point2>(hole.Count);
            foreach (int index in hole) holePolygon.Add(points[index]);

            double best = double.MaxValue;
            int bestOuter = -1, bestHole = -1;

            for (int o = 0; o < outer.Count; o++) {
                for (int h = 0; h < hole.Count; h++) {
                    Point2 a = points[outer[o]], b = points[hole[h]];

                    if (Blocked(a, b, outer[o], hole[h], outer, points)) continue;
                    if (Blocked(a, b, outer[o], hole[h], hole, points)) continue;

                    var midpoint = new Point2((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
                    if (!PointInPolygon(midpoint, outerPolygon)) continue;
                    if (PointInPolygon(midpoint, holePolygon)) continue;

                    double distance = (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
                    if (distance < best) { best = distance; bestOuter = o; bestHole = h; }
                }
            }

            if (bestOuter < 0) {
                throw new NavMeshFormatException("no visible bridge between cutout and repair boundary");
            }
            outerPosition = bestOuter;
            holePosition = bestHole;
        }

        private static bool Blocked(Point2 a, Point2 b, int aIndex, int bIndex,
                                    List<int> ring, IDictionary<int, Point2> points) {
            for (int i = 0; i < ring.Count; i++) {
                int c = ring[i], d = ring[(i + 1) % ring.Count];
                if (c == aIndex || d == aIndex || c == bIndex || d == bIndex) continue;
                if (SegmentsIntersect(a, b, points[c], points[d])) return true;
            }
            return false;
        }

        /// <summary>
        /// Triangulates a ring with one hole, by ear clipping the bridged loop.
        ///
        /// Winding is normalized first - outer counter-clockwise, hole clockwise - because the ear
        /// test depends on a consistent orientation and the input rings come from mesh traversal,
        /// which makes no promise about direction.
        /// </summary>
        public static List<int[]> TriangulateWithHole(IList<int> outer, IList<int> hole,
                                                      IDictionary<int, Point2> points,
                                                      out double areaError) {
            var outerList = new List<int>(outer);
            var holeList = new List<int>(hole);

            if (SignedArea(outerList, points) < 0) outerList.Reverse();
            if (SignedArea(holeList, points) > 0) holeList.Reverse();

            FindVisibleBridge(outerList, holeList, points, out int outerPosition, out int holePosition);

            var bridged = new List<int>(outerList.Count + holeList.Count + 2);
            for (int i = 0; i <= outerPosition; i++) bridged.Add(outerList[i]);
            for (int i = holePosition; i < holeList.Count; i++) bridged.Add(holeList[i]);
            for (int i = 0; i <= holePosition; i++) bridged.Add(holeList[i]);
            bridged.Add(outerList[outerPosition]);
            for (int i = outerPosition + 1; i < outerList.Count; i++) bridged.Add(outerList[i]);

            var triangles = EarClip(bridged, points);

            double triangleArea = 0.0;
            foreach (int[] triangle in triangles) {
                triangleArea += Math.Abs(Cross(points[triangle[0]], points[triangle[1]], points[triangle[2]])) / 2.0;
            }

            double expected = Math.Abs(SignedArea(outerList, points)) - Math.Abs(SignedArea(holeList, points));
            areaError = Math.Abs(triangleArea - expected);

            // The area check is the one that catches a triangulation which is topologically valid and
            // geometrically wrong - covering the hole, or folding back over itself.
            if (areaError > Math.Max(1e-3, expected * 1e-5)) {
                throw new NavMeshFormatException(
                    $"triangulation area mismatch: {triangleArea:F6} vs {expected:F6}");
            }
            return triangles;
        }

        /// <summary>
        /// Triangulates one simple polygon with no hole.
        ///
        /// Winding is normalized to counter-clockwise first, because ear clipping tests convexity by
        /// sign and a clockwise ring would report every corner as reflex.
        /// </summary>
        public static List<int[]> TriangulateSimplePolygon(IList<int> polygon,
                                                           IDictionary<int, Point2> points,
                                                           out double area) {
            var loop = new List<int>(polygon);
            if (loop.Count < 3) {
                throw new NavMeshFormatException("a polygon needs at least three corners to triangulate");
            }
            if (SignedArea(loop, points) < 0) loop.Reverse();

            List<int[]> triangles = EarClip(loop, points);

            area = 0.0;
            foreach (int[] triangle in triangles) {
                area += Math.Abs(Cross(points[triangle[0]], points[triangle[1]], points[triangle[2]])) / 2.0;
            }
            return triangles;
        }

        /// <summary>
        /// True when a ring never crosses itself.
        ///
        /// Ear clipping assumes a simple polygon and has no way to notice it was handed one that is
        /// not; it fails later, from a direction that says nothing about the real cause. Asking this
        /// first turns "could not be triangulated" into a statement about the actual shape.
        /// </summary>
        public static bool IsSimplePolygon(IList<int> loop, IDictionary<int, Point2> points) {
            int count = loop.Count;
            if (count < 3) return false;

            for (int i = 0; i < count; i++) {
                Point2 a = points[loop[i]], b = points[loop[(i + 1) % count]];

                for (int j = i + 1; j < count; j++) {
                    // Neighbouring edges legitimately share an endpoint; only non-adjacent ones
                    // touching means the ring folds back over itself.
                    if (j == i || (j + 1) % count == i || (i + 1) % count == j) continue;

                    Point2 c = points[loop[j]], d = points[loop[(j + 1) % count]];
                    if (SegmentsIntersect(a, b, c, d)) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Merges thin faces into their neighbours, turning pairs of triangles into quads.
        ///
        /// Ear clipping a ring against a rectangular hole cannot avoid the occasional needle: the
        /// hole's corners land wherever they land relative to the ring's vertices, and some of the
        /// resulting triangles are unavoidably thin. Refusing those loses the whole cutout, which in
        /// practice meant refusing half the buildings on a homestead.
        ///
        /// Merging is the better answer and it matches what the engine ships: of a battle terrain's
        /// 68,244 faces, 64,968 are quads. A sliver joined to the triangle across its longest edge
        /// usually becomes a perfectly ordinary quad.
        /// </summary>
        /// <returns>Faces of degree 3 or 4, in the same winding as the input.</returns>
        public static List<int[]> MergeSlivers(List<int[]> faces, IDictionary<int, Point2> points) {
            return MergeSlivers(faces, points, false);
        }

        /// <summary>
        /// Elevated surfaces preserve every already-valid triangle.  Merging merely small-but-valid
        /// triangles across a stair/landing slope break creates a warped quad: structurally legal,
        /// but a poor movement surface for Bannerlord agents.  Only a triangle below the engine's
        /// hard size floor is merged here.
        /// </summary>
        public static List<int[]> MergeInvalidSlivers(List<int[]> faces, IDictionary<int, Point2> points) {
            return MergeSlivers(faces, points, true);
        }

        private static List<int[]> MergeSlivers(List<int[]> faces, IDictionary<int, Point2> points,
                                                 bool invalidOnly) {
            var working = new List<int[]>(faces);

            // Bounded: every pass either merges something or stops. Two faces become one, so the list
            // can only shrink, and one sweep per possible merge is the most that can ever be useful.
            for (int pass = 0; pass < faces.Count; pass++) {
                int merged = MergeOneSliver(working, points, invalidOnly);
                if (merged < 0) break;
            }
            return working;
        }

        private static int MergeOneSliver(List<int[]> faces, IDictionary<int, Point2> points,
                                          bool invalidOnly) {
            for (int i = 0; i < faces.Count; i++) {
                // Anything under the TARGET is worth trying to merge, not just anything under the
                // hard floor. Merging a 1 m2 triangle into a neighbour costs nothing and moves the
                // result towards what an author would have drawn; waiting until a face is nearly
                // illegal before acting is how slivers survived to reach the engine.
                if (faces[i].Length != 3
                    || (invalidOnly ? IsAcceptableFace(faces[i], points)
                                    : IsWellFormedFace(faces[i], points))) continue;

                for (int j = 0; j < faces.Count; j++) {
                    if (j == i || faces[j].Length != 3) continue;

                    int[] quad = TryMerge(faces[i], faces[j], points);
                    if (quad == null) continue;

                    faces[i] = quad;
                    faces.RemoveAt(j);
                    return i;
                }
            }
            return -1;
        }

        /// <summary>True when a face is at least as large as anything the engine ships.</summary>
        public static bool IsAcceptableFace(int[] face, IDictionary<int, Point2> points) =>
            MeetsSize(face, points, MinimumFaceArea, MinimumEdgeLength);

        /// <summary>True when a face is the size TaleWorlds' guidance asks for.</summary>
        public static bool IsWellFormedFace(int[] face, IDictionary<int, Point2> points) =>
            MeetsSize(face, points, TargetFaceArea, TargetEdgeLength);

        private static bool MeetsSize(int[] face, IDictionary<int, Point2> points,
                                      double minimumArea, double minimumEdge) {
            if (Math.Abs(SignedArea(face, points)) < minimumArea) return false;

            for (int i = 0; i < face.Length; i++) {
                Point2 a = points[face[i]], b = points[face[(i + 1) % face.Length]];
                double dx = a.X - b.X, dy = a.Y - b.Y;
                if (dx * dx + dy * dy < minimumEdge * minimumEdge) return false;
            }
            return true;
        }

        /// <summary>
        /// Joins two triangles sharing an edge into a quad, or returns null when they cannot be.
        ///
        /// The result has to be convex: a reflex corner would make the face fold over itself, and the
        /// engine's own faces are all convex.
        /// </summary>
        private static int[] TryMerge(int[] left, int[] right, IDictionary<int, Point2> points) {
            for (int i = 0; i < 3; i++) {
                int a = left[i], b = left[(i + 1) % 3];

                for (int j = 0; j < 3; j++) {
                    // The shared edge runs the opposite way in the neighbour, which is what says the
                    // two faces sit side by side rather than back to back.
                    if (right[j] != b || right[(j + 1) % 3] != a) continue;

                    int leftApex = left[(i + 2) % 3];
                    int rightApex = right[(j + 2) % 3];

                    var quad = new[] { leftApex, a, rightApex, b };
                    if (!IsConvex(quad, points)) return null;

                    // The merged face only has to clear the hard floor, not the target: joining two
                    // small triangles into one merely-adequate quad is still strictly better than
                    // leaving a needle behind.
                    if (!IsAcceptableFace(quad, points)) return null;
                    return quad;
                }
            }
            return null;
        }

        private static bool IsConvex(int[] face, IDictionary<int, Point2> points) {
            bool positive = false, negative = false;

            for (int i = 0; i < face.Length; i++) {
                Point2 a = points[face[i]];
                Point2 b = points[face[(i + 1) % face.Length]];
                Point2 c = points[face[(i + 2) % face.Length]];

                double cross = Cross(a, b, c);
                if (cross > InteriorEpsilon) positive = true;
                else if (cross < -InteriorEpsilon) negative = true;
                else return false;                       // a straight or doubled-back corner

                if (positive && negative) return false;
            }
            return true;
        }

        /// <summary>Ear clipping over one simple loop of vertex indices.</summary>
        public static List<int[]> EarClip(List<int> loop, IDictionary<int, Point2> points) {
            var remaining = new List<int>(loop.Count);
            for (int i = 0; i < loop.Count; i++) remaining.Add(i);

            var triangles = new List<int[]>();
            int guard = 0;
            int guardLimit = loop.Count * loop.Count;

            while (remaining.Count > 3) {
                // The BEST ear each pass, not the first one found.
                //
                // Taking the first valid ear is the textbook version and it produces slivers: long
                // thin triangles that are perfectly legal and unlike anything the engine ships. Since
                // the whole ring has to be consumed either way, choosing the most equilateral ear each
                // time costs one extra scan and spreads the awkward geometry instead of concentrating
                // it into a few needles.
                int bestPosition = -1;
                double bestQuality = -1.0;
                int[] bestTriangle = null;

                for (int position = 0; position < remaining.Count; position++) {
                    int previousSlot = remaining[(position - 1 + remaining.Count) % remaining.Count];
                    int currentSlot = remaining[position];
                    int nextSlot = remaining[(position + 1) % remaining.Count];

                    int ia = loop[previousSlot], ib = loop[currentSlot], ic = loop[nextSlot];
                    if (ia == ib || ib == ic || ia == ic) continue;

                    Point2 a = points[ia], b = points[ib], c = points[ic];
                    if (Cross(a, b, c) <= InteriorEpsilon) continue;      // reflex or degenerate

                    bool contains = false;
                    foreach (int slot in remaining) {
                        if (slot == previousSlot || slot == currentSlot || slot == nextSlot) continue;
                        int candidate = loop[slot];
                        if (candidate == ia || candidate == ib || candidate == ic) continue;
                        if (StrictlyInsideTriangle(points[candidate], a, b, c)) { contains = true; break; }
                    }
                    if (contains) continue;

                    double quality = TriangleQuality(a, b, c);
                    if (quality <= bestQuality) continue;

                    bestQuality = quality;
                    bestPosition = position;
                    bestTriangle = new[] { ia, ib, ic };
                }

                guard++;
                if (bestPosition < 0 || guard > guardLimit) {
                    throw new NavMeshFormatException("repair polygon could not be triangulated safely");
                }

                triangles.Add(bestTriangle);
                remaining.RemoveAt(bestPosition);
            }

            int f0 = loop[remaining[0]], f1 = loop[remaining[1]], f2 = loop[remaining[2]];
            if (f0 == f1 || f1 == f2 || f0 == f2) {
                throw new NavMeshFormatException("triangulation ended with a degenerate face");
            }
            triangles.Add(new[] { f0, f1, f2 });
            return triangles;
        }

        /// <summary>
        /// The Z of a point inside a face, by barycentric interpolation over its triangle fan.
        ///
        /// Used to place cutout corners ON the navmesh surface. The authored footprint comes from an
        /// object's base, which can sit metres below the walkable surface - taking that Z directly
        /// would drop the repair ring through the floor.
        /// </summary>
        public static double InterpolateZ(Point2 point, uint[] polygon, IList<NavVertex> vertices) {
            NavVertex p0 = vertices[(int)polygon[0]];

            for (int index = 1; index < polygon.Length - 1; index++) {
                NavVertex vb = vertices[(int)polygon[index]];
                NavVertex vc = vertices[(int)polygon[index + 1]];

                var a = new Point2(p0.X, p0.Y);
                var b = new Point2(vb.X, vb.Y);
                var c = new Point2(vc.X, vc.Y);

                double denominator = Cross(a, b, c);
                if (Math.Abs(denominator) < 1e-8) continue;

                double wa = Cross(point, b, c) / denominator;
                double wb = Cross(point, c, a) / denominator;
                double wc = 1.0 - wa - wb;

                if (Math.Min(wa, Math.Min(wb, wc)) >= -1e-5) {
                    return wa * p0.Z + wb * vb.Z + wc * vc.Z;
                }
            }
            throw new NavMeshFormatException(
                $"cannot project point ({point.X:F3}, {point.Y:F3}) onto the affected faces");
        }
    }
}
