using System;
using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>One thing found wrong, and where to go and look at it.</summary>
    public class NavMeshFinding {
        public string Kind = "";
        public string Detail = "";
        public double X, Y, Z;

        /// <summary>True for things that will visibly break movement, as opposed to things worth knowing.</summary>
        public bool IsSerious;

        public override string ToString() =>
            $"{(IsSerious ? "!" : "-")} {Kind} at ({X:F1}, {Y:F1}, {Z:F1}): {Detail}";
    }

    /// <summary>What an audit found across the whole navmesh.</summary>
    public class NavMeshAuditReport {
        public NavMeshSummary Summary = new NavMeshSummary();
        public List<NavMeshFinding> Findings = new List<NavMeshFinding>();

        /// <summary>Faces in each connected island, largest first.</summary>
        public List<int> IslandSizes = new List<int>();

        public double WalkableArea;
        public int SeriousCount;

        public string Headline {
            get {
                if (SeriousCount == 0 && Findings.Count == 0) {
                    return $"Navmesh looks good: {Summary.Faces:N0} faces, "
                           + $"{WalkableArea:N0} m2 walkable, one connected area.";
                }
                if (SeriousCount == 0) {
                    return $"Navmesh usable, {Findings.Count} thing(s) worth a look.";
                }
                return $"Navmesh has {SeriousCount} problem(s) that will affect movement.";
            }
        }
    }

    /// <summary>
    /// Checks a whole baked navmesh, rather than a corner of it.
    ///
    /// Watching a handful of agents fight in one spot only ever tests the ground under that fight.
    /// This walks the entire mesh instead and answers the questions a fight cannot: is every part of
    /// the walkable ground reachable from every other part, did each object footprint actually stop
    /// being walkable, and did each marked area actually become walkable.
    ///
    /// Every finding carries a position, so a problem can be gone to and looked at rather than
    /// hunted for.
    /// </summary>
    public static class NavMeshAudit {

        /// <summary>Audits a navmesh on its own, without checking it against any authoring.</summary>
        public static NavMeshAuditReport Audit(NavMeshData data) =>
            Audit(data, new List<Point2[]>(), new List<NavMeshRequiredArea>());

        /// <summary>Audit authoring that also contains explicitly walkable raised/tunnel surfaces.</summary>
        public static NavMeshAuditReport Audit(NavMeshData data,
                                               IList<Point2[]> cutouts,
                                               IList<NavMeshRequiredArea> additions,
                                               IList<NavMeshRampPath> ramps) {
            var report = Audit(data, cutouts, additions);
            if (ramps == null || ramps.Count == 0) return report;

            // A complete cutout is still correct around a gatehouse.  When its centre falls on an
            // explicitly authored tunnel/walkway outline, the ordinary centre-point cutout audit
            // reports a false alarm.  Remove only that finding; island and non-manifold checks
            // remain in force and prove the exception really joins the navmesh.
            for (int finding = report.Findings.Count - 1; finding >= 0; finding--) {
                NavMeshFinding item = report.Findings[finding];
                if (item.Kind != "Object still walkable") continue;
                foreach (NavMeshRampPath ramp in ramps) {
                    if (!ramp.HasOutline) continue;
                    var outline = new Point2[ramp.Outline.Length];
                    for (int point = 0; point < outline.Length; point++)
                        outline[point] = new Point2(ramp.Outline[point].X, ramp.Outline[point].Y);
                    if (!NavMeshGeometry.PointInPolygon(new Point2(item.X, item.Y), outline)) continue;
                    report.Findings.RemoveAt(finding);
                    break;
                }
            }
            report.SeriousCount = 0;
            foreach (NavMeshFinding item in report.Findings) if (item.IsSerious) report.SeriousCount++;
            return report;
        }

        /// <summary>
        /// Audits a navmesh against the authoring it was baked from.
        /// </summary>
        /// <param name="cutouts">Object footprints that should now be holes.</param>
        /// <param name="additions">Marked areas that should now be walkable.</param>
        public static NavMeshAuditReport Audit(NavMeshData data,
                                               IList<Point2[]> cutouts,
                                               IList<NavMeshRequiredArea> additions) {
            var report = new NavMeshAuditReport {
                Summary = NavMeshValidation.Summarize(data),
            };

            var polygons = new List<Point2[]>(data.Faces.Count);
            foreach (NavFace face in data.Faces) {
                var polygon = new Point2[face.Degree];
                for (int i = 0; i < face.Degree; i++) {
                    NavVertex vertex = data.Vertices[(int)face.Vertices[i]];
                    polygon[i] = new Point2(vertex.X, vertex.Y);
                }
                polygons.Add(polygon);
                report.WalkableArea += Math.Abs(Area(polygon));
            }

            ReportStructure(report, data);
            ReportIslands(report, data, polygons);

            if (cutouts != null) ReportCutouts(report, polygons, cutouts);
            if (additions != null) ReportAdditions(report, polygons, additions);

            foreach (NavMeshFinding finding in report.Findings) {
                if (finding.IsSerious) report.SeriousCount++;
            }
            return report;
        }

        private static void ReportStructure(NavMeshAuditReport report, NavMeshData data) {
            NavMeshSummary summary = report.Summary;

            if (!summary.FiniteVertices) {
                Add(report, "Broken geometry", "some vertices are not real numbers", 0, 0, 0, true);
            }
            if (summary.InvalidFaceBoundaries > 0) {
                Add(report, "Broken faces",
                    $"{summary.InvalidFaceBoundaries} face(s) reference edges that do not match their corners",
                    0, 0, 0, true);
            }
            if (summary.NonManifoldEdges > 0) {
                Add(report, "Overlapping ground",
                    $"{summary.NonManifoldEdges} edge(s) are shared by more than two faces", 0, 0, 0, true);
            }
            if (summary.UnusedVertices > 0 || summary.UnusedEdges > 0) {
                // Harmless: the game ignores them, and keeping them is what lets face and edge
                // numbering stay stable across several edits.
                Add(report, "Leftover data",
                    $"{summary.UnusedVertices} unused vertices and {summary.UnusedEdges} unused edges remain "
                    + "from edits; they are ignored by the game",
                    0, 0, 0, false);
            }
        }

        /// <summary>
        /// Finds separated islands of walkable ground.
        ///
        /// This is the check a sparring match cannot make. Two islands can each look perfectly fine
        /// underfoot while no agent can ever cross between them, and nothing about that is visible
        /// until something needs to walk the route.
        /// </summary>
        private static void ReportIslands(NavMeshAuditReport report, NavMeshData data,
                                          List<Point2[]> polygons) {
            var faceForEdge = new Dictionary<uint, int>();
            var adjacency = new List<int>[data.Faces.Count];
            for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<int>();

            for (int faceIndex = 0; faceIndex < data.Faces.Count; faceIndex++) {
                foreach (uint edgeIndex in data.Faces[faceIndex].Edges) {
                    if (!faceForEdge.TryGetValue(edgeIndex, out int other)) {
                        faceForEdge[edgeIndex] = faceIndex;
                    } else if (other != faceIndex) {
                        adjacency[faceIndex].Add(other);
                        adjacency[other].Add(faceIndex);
                    }
                }
            }

            var visited = new bool[data.Faces.Count];
            var islands = new List<List<int>>();

            for (int start = 0; start < data.Faces.Count; start++) {
                if (visited[start]) continue;

                var island = new List<int>();
                var stack = new Stack<int>();
                stack.Push(start);
                visited[start] = true;

                while (stack.Count > 0) {
                    int current = stack.Pop();
                    island.Add(current);
                    foreach (int neighbor in adjacency[current]) {
                        if (visited[neighbor]) continue;
                        visited[neighbor] = true;
                        stack.Push(neighbor);
                    }
                }
                islands.Add(island);
            }

            islands.Sort((left, right) => right.Count.CompareTo(left.Count));
            foreach (List<int> island in islands) report.IslandSizes.Add(island.Count);

            for (int i = 1; i < islands.Count; i++) {
                List<int> island = islands[i];
                Point2 center = Center(polygons[island[0]]);
                double z = data.Vertices[(int)data.Faces[island[0]].Vertices[0]].Z;

                double area = 0.0;
                foreach (int faceIndex in island) area += Math.Abs(Area(polygons[faceIndex]));

                // Slivers are normal in shipped scenes - a step edge, a ledge. Anything an agent
                // could stand on and then be stranded on is not.
                bool serious = area >= 4.0;
                Add(report, "Cut-off ground",
                    $"{island.Count} face(s), {area:F0} m2, not connected to the main walkable area",
                    center.X, center.Y, z, serious);
            }
        }

        private static void ReportCutouts(NavMeshAuditReport report, List<Point2[]> polygons,
                                          IList<Point2[]> cutouts) {
            for (int i = 0; i < cutouts.Count; i++) {
                Point2[] footprint = cutouts[i];

                // Any simple polygon, not just a quad. Merged footprints are L- and U-shaped, and
                // prefab-derived ones follow a wall walk, so a quads-only test silently stopped
                // auditing exactly the shapes most likely to be cut wrongly - reporting nothing and
                // reading as a clean bill of health.
                if (footprint == null || footprint.Length < 3) continue;

                double sumX = 0.0, sumY = 0.0;
                foreach (Point2 corner in footprint) { sumX += corner.X; sumY += corner.Y; }
                var center = new Point2(sumX / footprint.Length, sumY / footprint.Length);

                // A centroid can fall outside a concave outline - the inside of an L - where it
                // proves nothing either way. Sampling the corners as well keeps the check honest
                // for the shapes the quad version used to skip.
                var samples = new List<Point2>(footprint.Length + 1) { center };
                foreach (Point2 corner in footprint) {
                    samples.Add(new Point2((corner.X + center.X) / 2.0, (corner.Y + center.Y) / 2.0));
                }

                bool reported = false;
                foreach (Point2 sample in samples) {
                    foreach (Point2[] polygon in polygons) {
                        if (!NavMeshGeometry.PointInPolygon(sample, polygon)) continue;

                        Add(report, "Object still walkable",
                            $"footprint {i + 1} still has navmesh under it; agents will walk through it",
                            sample.X, sample.Y, 0, true);
                        reported = true;
                        break;
                    }
                    if (reported) break;
                }
            }
        }

        private static void ReportAdditions(NavMeshAuditReport report, List<Point2[]> polygons,
                                            IList<NavMeshRequiredArea> additions) {
            foreach (NavMeshRequiredArea area in additions) {
                var center = new Point2(area.Center.X, area.Center.Y);

                bool covered = false;
                foreach (Point2[] polygon in polygons) {
                    if (!NavMeshGeometry.PointInPolygon(center, polygon)) continue;
                    covered = true;
                    break;
                }
                if (covered) continue;

                Add(report, "Marked ground still unwalkable",
                    $"'{area.Label}' was marked as needing navmesh but has none; agents will refuse to go there",
                    center.X, center.Y, area.Center.Z, true);
            }
        }

        private static void Add(NavMeshAuditReport report, string kind, string detail,
                                double x, double y, double z, bool serious) {
            report.Findings.Add(new NavMeshFinding {
                Kind = kind, Detail = detail, X = x, Y = y, Z = z, IsSerious = serious,
            });
        }

        private static double Area(Point2[] polygon) {
            double total = 0.0;
            for (int i = 0; i < polygon.Length; i++) {
                Point2 a = polygon[i];
                Point2 b = polygon[(i + 1) % polygon.Length];
                total += a.X * b.Y - b.X * a.Y;
            }
            return total / 2.0;
        }

        private static Point2 Center(Point2[] polygon) {
            double x = 0.0, y = 0.0;
            foreach (Point2 point in polygon) { x += point.X; y += point.Y; }
            return new Point2(x / polygon.Length, y / polygon.Length);
        }
    }
}
