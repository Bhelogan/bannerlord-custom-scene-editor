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
        private static string? _cachedPath;

        public static string ProjectsPath {
            get {
                if (_cachedPath != null) return _cachedPath;

                string documents;
                try {
                    documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                } catch {
                    documents = "";
                }

                if (string.IsNullOrEmpty(documents)) {
                    documents = Path.GetTempPath();
                }

                _cachedPath = Path.Combine(documents, "Mount and Blade II Bannerlord", FolderName);
                try {
                    Directory.CreateDirectory(_cachedPath);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(ProjectSerializer),
                        $"Could not create projects folder '{_cachedPath}': {ex.Message}");
                }
                return _cachedPath;
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
