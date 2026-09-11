using System;
using System.IO;
using System.Linq;
using System.Text;
using CustomSceneCreator.Editing;
using Newtonsoft.Json;

namespace CustomSceneCreator.IO {
    /// <summary>
    /// Writes the stable, human-readable handoff consumed by future navmesh tooling. Keeping this
    /// separate from navmesh.bin lets us validate footprints and face sampling before risking a
    /// destructive write to an undocumented native format.
    /// </summary>
    internal static class NavMeshCutoutManifestExporter {
        private sealed class Manifest {
            public string Format = "CustomSceneCreator.NavMeshPlan/2";
            public string Project = "";
            public string Scene = "";
            public string SceneLevels = "";
            public DateTime ExportedUtc;
            public ProjectNavMeshCutout[] Cutouts = Array.Empty<ProjectNavMeshCutout>();
            public ProjectNavMeshRequirement[] RequiredAreas = Array.Empty<ProjectNavMeshRequirement>();
            public ProjectNavMeshRamp[] Ramps = Array.Empty<ProjectNavMeshRamp>();
            public string Note =
                "Authoring handoff only. Applying this file must validate the current navmesh before modifying it.";
        }

        public static string WriteDocumentsCopy(SceneProject project, string name) {
            string safe = ProjectSerializer.SanitizeFileName(name);
            string path = Path.Combine(ProjectSerializer.NavMeshCutoutExportsPath, safe + ".navcut.json");
            Write(project, path);
            return path;
        }

        public static string WriteIntoFolder(SceneProject project, string folder) {
            string path = Path.Combine(folder, "csc_navmesh_cutouts.navcut.json");
            Write(project, path);
            return path;
        }

        private static void Write(SceneProject project, string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var manifest = new Manifest {
                Project = project.Name,
                Scene = project.TargetScene,
                SceneLevels = project.SceneLevels,
                ExportedUtc = DateTime.UtcNow,
                Cutouts = (project.NavMeshCutouts ?? new System.Collections.Generic.List<ProjectNavMeshCutout>())
                    .Where(c => c != null && !c.IsDraft && c.Corners != null
                        && c.Corners.Length >= 9 && c.Corners.Length % 3 == 0).ToArray(),
                RequiredAreas = (project.NavMeshRequirements
                    ?? new System.Collections.Generic.List<ProjectNavMeshRequirement>()).ToArray(),
                Ramps = (project.NavMeshRamps
                    .Where(r => !r.IsDraft)
                    ?? new System.Collections.Generic.List<ProjectNavMeshRamp>()).ToArray(),
            };
            File.WriteAllText(path, JsonConvert.SerializeObject(manifest, Formatting.Indented), Encoding.UTF8);
            TraceLogger.Write(nameof(NavMeshCutoutManifestExporter),
                $"Wrote {manifest.Cutouts.Length} navmesh cutout(s) and " +
                $"{manifest.RequiredAreas.Length} required area(s), and " +
                $"{manifest.Ramps.Length} ramp(s) to {path}");
        }
    }
}
