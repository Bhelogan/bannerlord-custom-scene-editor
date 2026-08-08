using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Reads and writes <see cref="SceneProject"/> files.
    ///
    /// Projects live in the user's Documents folder rather than inside the module, so they survive a
    /// mod update or reinstall and can be shared by sending one file. That is also where the bake
    /// script expects to find them - both sides read the same JSON, which is what keeps the in-game
    /// exporter and the editable Python script from drifting apart.
    /// </summary>
    public static class ProjectSerializer {
        private const string FolderName = "CustomSceneCreator";
        private static string? _cachedRoot;

        /// <summary>
        /// Everything the editor writes, under Documents so it survives a mod update or reinstall and
        /// can be shared by sending one file.
        ///
        ///   CustomSceneCreator/
        ///     projects/          working files you reopen and keep editing
        ///     exports/prefabs/   reusable objects (also written into the module so the game loads them)
        ///     exports/scenes/    whole-scene fragments for the Modding Kit
        ///
        /// Projects and exports are deliberately separate: a project is the editable source you come
        /// back to, an export is a produced artifact. Mixing them means never being sure which file
        /// is the one worth keeping.
        /// </summary>
        public static string RootPath {
            get {
                if (_cachedRoot != null) return _cachedRoot;

                string documents;
                try {
                    documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                } catch {
                    documents = "";
                }
                if (string.IsNullOrEmpty(documents)) documents = Path.GetTempPath();

                _cachedRoot = Path.Combine(documents, "Mount and Blade II Bannerlord", FolderName);
                try {
                    Directory.CreateDirectory(_cachedRoot);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(ProjectSerializer),
                        $"Could not create '{_cachedRoot}': {ex.Message}");
                }
                return _cachedRoot;
            }
        }

        public static string ProjectsPath => EnsureSubfolder("projects");
        public static string PrefabExportsPath => EnsureSubfolder(Path.Combine("exports", "prefabs"));
        public static string SceneExportsPath => EnsureSubfolder(Path.Combine("exports", "scenes"));

        private static string EnsureSubfolder(string relative) {
            string path = Path.Combine(RootPath, relative);
            try {
                if (!Directory.Exists(path)) {
                    Directory.CreateDirectory(path);
                    MigrateLooseProjects(path, relative);
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(ProjectSerializer), $"Could not create '{path}': {ex.Message}");
            }
            return path;
        }

        /// <summary>
        /// Earlier builds wrote projects straight into the root folder. Move them rather than
        /// stranding them: someone who has already built something should not have to be told their
        /// work is in the wrong place.
        /// </summary>
        private static void MigrateLooseProjects(string projectsPath, string relative) {
            if (relative != "projects") return;
            try {
                foreach (string file in Directory.GetFiles(RootPath, "*.json")) {
                    string destination = Path.Combine(projectsPath, Path.GetFileName(file));
                    if (File.Exists(destination)) continue;
                    File.Move(file, destination);
                    TraceLogger.Write(nameof(ProjectSerializer),
                        $"Moved existing project '{Path.GetFileName(file)}' into projects/.");
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(ProjectSerializer), $"Project migration skipped: {ex.Message}");
            }
        }

        public static bool Save(SceneProject project) {
            if (project == null || string.IsNullOrWhiteSpace(project.Name)) {
                TraceLogger.Write(nameof(ProjectSerializer), "Save skipped: project has no name.");
                return false;
            }

            try {
                string path = Path.Combine(ProjectsPath, project.FileName);
                // Indented on purpose: these files are meant to be opened, diffed and hand-edited.
                File.WriteAllText(path, JsonConvert.SerializeObject(project, Formatting.Indented));
                TraceLogger.Write(nameof(ProjectSerializer),
                    $"Saved '{project.Name}' ({project.Entities.Count} entities) to {path}");
                return true;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ProjectSerializer), $"Failed to save '{project.Name}'", ex);
                return false;
            }
        }

        public static SceneProject? Load(string name) {
            try {
                string path = Path.Combine(ProjectsPath, SanitizeFileName(name) + ".json");
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<SceneProject>(File.ReadAllText(path));
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ProjectSerializer), $"Failed to load '{name}'", ex);
                return null;
            }
        }

        public static List<SceneProject> LoadAll() {
            var result = new List<SceneProject>();
            try {
                if (!Directory.Exists(ProjectsPath)) return result;
                foreach (string file in Directory.GetFiles(ProjectsPath, "*.json")) {
                    try {
                        SceneProject? p = JsonConvert.DeserializeObject<SceneProject>(File.ReadAllText(file));
                        if (p != null) result.Add(p);
                    } catch (Exception ex) {
                        TraceLogger.Write(nameof(ProjectSerializer),
                            $"Skipping unreadable project '{Path.GetFileName(file)}': {ex.Message}");
                    }
                }
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ProjectSerializer), "Failed listing projects", ex);
            }
            return result.OrderByDescending(p => p.Modified).ToList();
        }

        public static string SanitizeFileName(string name) {
            if (string.IsNullOrWhiteSpace(name)) return "untitled";
            var invalid = Path.GetInvalidFileNameChars();
            string cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return cleaned.Length == 0 ? "untitled" : cleaned;
        }
    }
}
