using System;
using System.Collections.Generic;
using System.IO;
using CustomSceneCreator.Editing;
using CustomSceneCreator.NavMesh;
using TaleWorlds.Library;

namespace CustomSceneCreator.IO {
    /// <summary>
    /// Turns a project's navmesh authoring into an actual baked navmesh.
    ///
    /// This is the seam between editor data - object footprints and marked ground, both stored as
    /// plain world-space numbers - and <see cref="NavMeshBaker"/>, which knows the binary format and
    /// nothing about projects. Keeping the two apart is what lets another mod bake a navmesh without
    /// having a project at all.
    ///
    /// The game's own scene files are never written to. A bake either lands beside the project or
    /// inside an exported scene folder, both of which belong to the user.
    /// </summary>
    internal static class SceneNavMeshBaker {

        /// <summary>Result of a bake attempt, in terms the editor can show and log.</summary>
        internal sealed class Result {
            public bool Success;
            public bool Attempted;
            public string Message = "";
            public string Path = "";
            public NavMeshBakeReport? Report;
        }

        /// <summary>True when the project has anything worth baking.</summary>
        public static bool HasWork(SceneProject project) =>
            project != null
            && ((project.NavMeshCutouts != null && project.NavMeshCutouts.Count > 0)
                || (project.NavMeshRequirements != null && project.NavMeshRequirements.Count > 0)
                || (project.NavMeshRamps != null && project.NavMeshRamps.Count > 0));

        /// <summary>
        /// Bakes the project's navmesh next to the project itself.
        ///
        /// Saving a layout without its navmesh produces two files that drift apart - the objects move
        /// and the walkable ground does not - so a save writes both or the pair is not trustworthy.
        /// </summary>
        public static Result BakeForProject(SceneProject project, Action<int, int>? progress = null,
                                            bool force = false) {
            var result = new Result();
            // A normal project save can remain quiet when there is no completed navmesh work.
            // The Project Browser's explicit Bake command passes force so it always refreshes the
            // project's test-scene navmesh slot, even if the user has not changed any authoring.
            if (!force && !HasWork(project)) return result;

            string? source = FindSceneNavMesh(project.TargetScene);
            if (source == null) {
                result.Attempted = true;
                result.Message =
                    $"No navmesh found for scene '{project.TargetScene}', so none was baked. " +
                    "The layout itself saved normally.";
                TraceLogger.Write(nameof(SceneNavMeshBaker), result.Message);
                return result;
            }

            string destination = Path.Combine(
                ProjectSerializer.NavMeshBakesPath,
                ProjectSerializer.SanitizeFileName(project.Name) + ".navmesh.bin");

            return Bake(project, source, destination, keepBackup: false, progress);
        }

        /// <summary>
        /// Bakes the navmesh already copied into an exported scene folder, in place.
        ///
        /// An exported SceneObj folder is meant to be usable as it stands. Leaving the source scene's
        /// navmesh in it would mean the exported scene looks right and paths wrong, which is the
        /// hardest kind of wrong to notice.
        /// </summary>
        public static Result BakeExportedScene(SceneProject project, string sceneFolder) {
            var result = new Result();
            if (!HasWork(project)) return result;

            string navmesh = Path.Combine(sceneFolder, "navmesh.bin");
            if (!File.Exists(navmesh)) {
                result.Attempted = true;
                result.Message = "The exported scene has no navmesh to bake.";
                return result;
            }
            return Bake(project, navmesh, navmesh, keepBackup: false);
        }

        private static Result Bake(SceneProject project, string source, string destination, bool keepBackup,
                                   Action<int, int>? progress = null) {
            var result = new Result { Attempted = true, Path = destination };

            try {
                var request = new NavMeshBakeRequest {
                    KeepBackup = keepBackup,
                    Progress = (done, total) => {
                        progress?.Invoke(done, total);
                        TraceLogger.Write(nameof(SceneNavMeshBaker),
                            $"Bake progress for '{project.Name}': {done}/{total} operation(s).");
                    },
                };
                request.Cutouts.AddRange(ToFootprints(project));
                request.Additions.AddRange(ToRequiredAreas(project));
                request.Ramps.AddRange(ToRamps(project));

                if (!request.HasWork) {
                    // A test project still needs a per-project navmesh file even when every
                    // authored item is a draft/invalid record. The reusable test-scene slot reads
                    // this path; leaving it absent made the browser's explicit Bake button report
                    // "nothing changed" and then silently test the source scene instead.
                    string? destinationDirectory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
                    if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination),
                                       StringComparison.OrdinalIgnoreCase)) {
                        File.Copy(source, destination, overwrite: true);
                    }
                    result.Success = true;
                    progress?.Invoke(100, 100);
                    result.Message =
                        "Created a base navmesh bake. No completed cutouts, added areas, or elevated outlines were ready to apply.";
                    TraceLogger.Write(nameof(SceneNavMeshBaker), result.Message + " " + destination);
                    return result;
                }

                NavMeshBakeReport report = NavMeshBaker.BakeFile(source, destination, request);
                result.Report = report;
                result.Success = report.Changed;
                result.Message = report.Headline;

                foreach (string line in report.Log) {
                    TraceLogger.Write(nameof(SceneNavMeshBaker), line);
                }
                return result;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneNavMeshBaker),
                    $"Navmesh bake failed for '{project.Name}'", ex);
                result.Message = "Navmesh bake failed: " + ex.Message;
                return result;
            }
        }

        /// <summary>
        /// Object footprints, as flat XY quads.
        ///
        /// Only the XY corners are passed on. The heights stored with a footprint come from the
        /// object's base, and the cutout pass reads its own heights off the navmesh surface instead -
        /// an object standing on a step would otherwise pull the repaired ground down to its feet.
        /// </summary>
        public static IEnumerable<Point2[]> ToFootprints(SceneProject project) {
            if (project.NavMeshCutouts == null) yield break;

            foreach (ProjectNavMeshCutout cutout in project.NavMeshCutouts) {
                if (cutout?.Corners == null || cutout.Corners.Length < 12) continue;

                var corners = new Point2[4];
                for (int i = 0; i < 4; i++) {
                    corners[i] = new Point2(cutout.Corners[i * 3], cutout.Corners[i * 3 + 1]);
                }
                yield return corners;
            }
        }

        /// <summary>
        /// Marked areas, with the terrain samples taken when they were marked.
        ///
        /// Areas marked before the editor sampled terrain are skipped rather than filled flat: a
        /// flat patch across sloped ground is worse than no patch, because agents commit to it.
        /// </summary>
        public static IEnumerable<NavMeshRequiredArea> ToRequiredAreas(SceneProject project) {
            if (project.NavMeshRequirements == null) yield break;

            foreach (ProjectNavMeshRequirement requirement in project.NavMeshRequirements) {
                if (requirement?.Boundary == null || requirement.Boundary.Length < 24) continue;
                if (requirement.Pos == null || requirement.Pos.Length < 3) continue;

                int count = requirement.Boundary.Length / 3;
                var boundary = new NavVertex[count];
                for (int i = 0; i < count; i++) {
                    boundary[i] = new NavVertex(
                        requirement.Boundary[i * 3],
                        requirement.Boundary[i * 3 + 1],
                        requirement.Boundary[i * 3 + 2]);
                }

                yield return new NavMeshRequiredArea {
                    Id = requirement.Id,
                    Label = requirement.Label,
                    Center = new NavVertex(requirement.Pos[0], requirement.Pos[1], requirement.Pos[2]),
                    Radius = Math.Max(0.5f, requirement.Radius),
                    Boundary = boundary,
                };
            }
        }

        /// <summary>
        /// Elevated strips are already sampled at their physical surface height by the editor.
        /// Do not call <c>GetGroundHeightAtPosition</c> here: it would pull a bridge deck down into
        /// the river or a wall walk down behind the wall.
        /// </summary>
        public static IEnumerable<NavMeshRampPath> ToRamps(SceneProject project) {
            if (project.NavMeshRamps == null) yield break;

            foreach (ProjectNavMeshRamp ramp in project.NavMeshRamps) {
                if (ramp.IsDraft) continue;
                // The editor's current surface workflow is a natural four-corner perimeter:
                // arbitrary corners around a physical raised surface. Keep that perimeter intact:
                // NavMeshRamp now triangulates it as one connected elevated surface, so a wall-walk
                // with several stairs is not incorrectly forced into one pair of rails.
                if (NavMeshRampAuthoring.IsOutlineUsable(ramp)) {
                    int outlineCount = NavMeshRampAuthoring.PointCount(ramp.Outline);
                    var outline = new NavVertex[outlineCount];
                    for (int i = 0; i < outlineCount; i++) {
                        TaleWorlds.Library.Vec3 point = NavMeshRampAuthoring.Point(ramp.Outline, i);
                        outline[i] = new NavVertex(point.x, point.y, point.z);
                    }
                    yield return new NavMeshRampPath {
                        Label = string.IsNullOrWhiteSpace(ramp.Label) ? "Elevated area" : ramp.Label,
                        Outline = outline,
                    };
                    continue;
                }
                if (!NavMeshRampAuthoring.IsUsable(ramp)) continue;

                int count = ramp.SampleCount;
                var left = new NavVertex[count];
                var right = new NavVertex[count];
                for (int i = 0; i < count; i++) {
                    TaleWorlds.Library.Vec3 leftPoint = NavMeshRampAuthoring.Point(ramp.Left, i);
                    TaleWorlds.Library.Vec3 rightPoint = NavMeshRampAuthoring.Point(ramp.Right, i);
                    left[i] = new NavVertex(leftPoint.x, leftPoint.y, leftPoint.z);
                    right[i] = new NavVertex(rightPoint.x, rightPoint.y, rightPoint.z);
                }

                yield return new NavMeshRampPath {
                    Label = string.IsNullOrWhiteSpace(ramp.Label) ? "Ramp" : ramp.Label,
                    Left = left,
                    Right = right,
                };
            }
        }

        /// <summary>
        /// Finds the lowest and highest perimeter edges, then walks the outline's two sides between
        /// them. Each side is resampled to an equal count, producing the paired lanes the original
        /// robust ramp baker requires. This preserves every authored bend as a physical boundary
        /// instead of imposing a four-corner rectangle on the user.
        /// </summary>
        private static NavMeshRampPath? OutlineToRamp(ProjectNavMeshRamp ramp) {
            int count = NavMeshRampAuthoring.PointCount(ramp.Outline);
            if (count < 4) return null;
            var points = new NavVertex[count];
            for (int i = 0; i < count; i++) {
                TaleWorlds.Library.Vec3 point = NavMeshRampAuthoring.Point(ramp.Outline, i);
                points[i] = new NavVertex(point.x, point.y, point.z);
            }

            int low = 0, high = 0;
            double lowest = double.MaxValue, highest = double.MinValue;
            for (int i = 0; i < count; i++) {
                double elevation = (points[i].Z + points[(i + 1) % count].Z) * 0.5;
                if (elevation < lowest) { lowest = elevation; low = i; }
                if (elevation > highest) { highest = elevation; high = i; }
            }
            if (low == high) return null; // flat or degenerate outline: it belongs in Add Navmesh Area.

            List<NavVertex> left = WalkOutline(points, low, -1, (high + 1) % count);
            List<NavVertex> right = WalkOutline(points, (low + 1) % count, 1, high);
            if (left.Count < 2 || right.Count < 2) return null;
            int samples = Math.Max(left.Count, right.Count);
            return new NavMeshRampPath {
                Label = string.IsNullOrWhiteSpace(ramp.Label) ? "Elevated area" : ramp.Label,
                Left = ResamplePath(left, samples),
                Right = ResamplePath(right, samples),
            };
        }

        private static List<NavVertex> WalkOutline(NavVertex[] outline, int start, int step, int end) {
            var path = new List<NavVertex>();
            int index = start;
            for (int guard = 0; guard <= outline.Length; guard++) {
                path.Add(outline[index]);
                if (index == end) return path;
                index = (index + step + outline.Length) % outline.Length;
            }
            return new List<NavVertex>();
        }

        private static NavVertex[] ResamplePath(List<NavVertex> path, int count) {
            if (path.Count == count) return path.ToArray();
            var distances = new double[path.Count];
            for (int i = 1; i < path.Count; i++) distances[i] = distances[i - 1] + Distance(path[i - 1], path[i]);
            double total = distances[distances.Length - 1];
            if (total <= 0.0001) return path.ToArray();

            var result = new NavVertex[count];
            for (int sample = 0; sample < count; sample++) {
                double target = total * sample / (count - 1);
                int segment = 1;
                while (segment < distances.Length - 1 && distances[segment] < target) segment++;
                double span = distances[segment] - distances[segment - 1];
                double t = span <= 0.0001 ? 0 : (target - distances[segment - 1]) / span;
                NavVertex a = path[segment - 1], b = path[segment];
                result[sample] = new NavVertex((float)(a.X + (b.X - a.X) * t),
                    (float)(a.Y + (b.Y - a.Y) * t), (float)(a.Z + (b.Z - a.Z) * t));
            }
            return result;
        }

        private static double Distance(NavVertex a, NavVertex b) {
            double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
            return Math.Sqrt(x * x + y * y + z * z);
        }

        /// <summary>
        /// The navmesh a project's bake would have produced, if one has been written.
        ///
        /// Preferring the bake over the scene's own file is the point: auditing the source navmesh
        /// would only ever confirm what the scene shipped with.
        /// </summary>
        public static string? FindBakedNavMesh(SceneProject project) {
            try {
                string path = Path.Combine(
                    ProjectSerializer.NavMeshBakesPath,
                    ProjectSerializer.SanitizeFileName(project.Name) + ".navmesh.bin");
                return File.Exists(path) ? path : null;
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Checks a project's baked navmesh against the authoring it came from.
        ///
        /// Falls back to the scene's own navmesh when nothing has been baked yet, which is still
        /// worth doing: it shows what the scene starts with and which footprints are still walkable.
        /// </summary>
        public static NavMeshAuditReport? AuditProject(SceneProject project, out string source) {
            source = FindBakedNavMesh(project) ?? FindSceneNavMesh(project.TargetScene) ?? "";
            if (source.Length == 0) return null;

            NavMeshData data = NavMeshData.Parse(File.ReadAllBytes(source));
            return NavMeshAudit.Audit(data,
                new List<Point2[]>(ToFootprints(project)),
                new List<NavMeshRequiredArea>(ToRequiredAreas(project)),
                new List<NavMeshRampPath>(ToRamps(project)));
        }

        /// <summary>Finds a scene's shipped navmesh across every installed module.</summary>
        public static string? FindSceneNavMesh(string sceneName) {
            if (string.IsNullOrWhiteSpace(sceneName)) return null;

            try {
                string modules = Path.Combine(BasePath.Name, "Modules");
                if (!Directory.Exists(modules)) return null;

                foreach (string module in Directory.GetDirectories(modules)) {
                    string candidate = Path.Combine(module, "SceneObj", sceneName, "navmesh.bin");
                    if (File.Exists(candidate)) return candidate;
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneNavMeshBaker),
                    $"Could not search for the navmesh of '{sceneName}': {ex.Message}");
            }
            return null;
        }
    }
}
