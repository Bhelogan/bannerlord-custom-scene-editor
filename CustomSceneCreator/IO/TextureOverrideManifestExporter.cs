using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CustomSceneCreator.Api;
using CustomSceneCreator.Editing;
using Newtonsoft.Json;

namespace CustomSceneCreator.IO {
    /// <summary>
    /// Writes the portable half of a runtime texture override. Bannerlord prefab/scene XML stores
    /// entity geometry and material resource names, but it cannot embed a PNG or a material made at
    /// runtime. The manifest preserves which PNG replaces which material on which exported object;
    /// the image folder makes the export self-contained for a consuming mod.
    /// </summary>
    internal static class TextureOverrideManifestExporter {
        private sealed class Manifest {
            public int Version = 1;
            public string Export = "";
            public string Kind = "";
            public string ImageFolder = "";
            public List<ManifestEntity> Entities = new();
        }

        private sealed class ManifestEntity {
            public string Id = "";
            public string Prefab = "";
            public float[] Position = Array.Empty<float>();
            public List<ProjectTextureOverride> Overrides = new();
        }

        public static int Write(SceneProject project, string exportName, string kind, string outputDirectory) {
            List<ProjectEntity> textured = (project.Entities ?? new List<ProjectEntity>())
                .Where(e => e?.TextureOverrides != null && e.TextureOverrides.Count > 0).ToList();
            if (textured.Count == 0) return 0;

            string imageFolderName = exportName + "_textures";
            string imageDirectory = Path.Combine(outputDirectory, imageFolderName);
            Directory.CreateDirectory(imageDirectory);

            var manifest = new Manifest { Export = exportName, Kind = kind, ImageFolder = imageFolderName };
            var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectEntity entity in textured) {
                var entry = new ManifestEntity {
                    Id = entity.Id ?? "",
                    Prefab = entity.Prefab ?? "",
                    Position = entity.Pos ?? Array.Empty<float>(),
                    Overrides = entity.TextureOverrides.Select(t => new ProjectTextureOverride {
                        Image = Path.GetFileName(t.Image ?? ""), Material = t.Material ?? "", MeshIndex = t.MeshIndex,
                        // Carried so a consuming mod reproduces the look, not just the image.
                        Alpha = t.Alpha, AlphaCutoff = t.AlphaCutoff, BlendMode = t.BlendMode,
                    }).ToList(),
                };
                manifest.Entities.Add(entry);
                foreach (ProjectTextureOverride texture in entry.Overrides)
                    if (!string.IsNullOrWhiteSpace(texture.Image)) images.Add(texture.Image);
            }

            foreach (string image in images) {
                string source = Path.Combine(ProjectSerializer.TexturesPath, Path.GetFileName(image));
                if (File.Exists(source)) File.Copy(source, Path.Combine(imageDirectory, Path.GetFileName(image)), true);
                else TraceLogger.Write(nameof(TextureOverrideManifestExporter), $"Missing texture during export: {source}");
            }

            string path = Path.Combine(outputDirectory, exportName + ".texture_overrides.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(manifest, Formatting.Indented));
            return manifest.Entities.Sum(e => e.Overrides.Count);
        }

        /// <summary>
        /// Restores the portable overrides when a CSC-exported prefab is placed back into CSC.
        ///
        /// A sealed prefab is now one entity, so an override can only be recovered safely when every
        /// source object that named a material chose the same image. If two children intentionally
        /// used different images on the same material, applying either image to the whole composite
        /// would be wrong; leave that material alone and retain the manifest for a consuming mod that
        /// can address the original child identities.
        /// </summary>
        public static List<TextureOverride> LoadForPrefab(string prefabName) {
            var result = new List<TextureOverride>();
            try {
                string manifestPath = Path.Combine(ProjectSerializer.PrefabExportsPath,
                    Path.GetFileName(prefabName) + ".texture_overrides.json");
                if (!File.Exists(manifestPath)) return result;

                Manifest? manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(manifestPath));
                if (manifest == null || !string.Equals(manifest.Kind, "Prefab", StringComparison.OrdinalIgnoreCase))
                    return result;

                string imageDirectory = Path.Combine(ProjectSerializer.PrefabExportsPath,
                    Path.GetFileName(manifest.ImageFolder ?? ""));
                Directory.CreateDirectory(ProjectSerializer.TexturesPath);

                IEnumerable<ProjectTextureOverride> all = (manifest.Entities ?? new List<ManifestEntity>())
                    .SelectMany(e => e?.Overrides ?? new List<ProjectTextureOverride>())
                    .Where(t => !string.IsNullOrWhiteSpace(t.Material) && !string.IsNullOrWhiteSpace(t.Image));

                foreach (IGrouping<string, ProjectTextureOverride> group in
                         all.GroupBy(t => t.Material, StringComparer.OrdinalIgnoreCase)) {
                    string[] images = group.Select(t => Path.GetFileName(t.Image))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (images.Length != 1) {
                        TraceLogger.Write(nameof(TextureOverrideManifestExporter),
                            $"Prefab '{prefabName}' has conflicting images for material '{group.Key}'; automatic CSC restore skipped that material.");
                        continue;
                    }

                    string source = Path.Combine(imageDirectory, images[0]);
                    string destination = Path.Combine(ProjectSerializer.TexturesPath, images[0]);
                    if (!File.Exists(destination) && File.Exists(source)) File.Copy(source, destination, false);
                    if (!File.Exists(destination)) {
                        TraceLogger.Write(nameof(TextureOverrideManifestExporter),
                            $"Prefab '{prefabName}' texture is missing: {source}");
                        continue;
                    }
                    // A sealed composite has no trustworthy source-child-to-runtime-mesh map.
                    // Keep imported prefab manifests compatible via the old material-wide form;
                    // new overrides authored in CSC are always surface-specific.
                    result.Add(new TextureOverride { Material = group.Key, Image = images[0], MeshIndex = -1, Alpha = -1 });
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(TextureOverrideManifestExporter),
                    $"Could not restore prefab textures for '{prefabName}': {ex.GetType().Name}: {ex.Message}");
            }
            return result;
        }
    }
}
