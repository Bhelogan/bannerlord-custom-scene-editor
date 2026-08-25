using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TaleWorlds.Library;
using IODirectory = System.IO.Directory;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CustomSceneCreator.IO {

    /// <summary>
    /// Checks every installed module's SceneObj folders for the three faults that make a scene load
    /// but not work, and writes what it finds to the trace log at startup.
    ///
    /// All three are silent in game. The scene loads, the terrain draws, agents spawn and draw their
    /// weapons - and then melee never engages, because nothing can path. Only ranged troops appear
    /// to act. It reads exactly like an AI bug, and it is not one; it cost a full evening of
    /// debugging somebody else's mod before the cause was found.
    ///
    /// Nothing here is fixed automatically. Deleting or rewriting another module's files is not this
    /// editor's business - but saying plainly what is wrong, once, at startup, is.
    ///
    /// The same checks are available offline as tools/check_scene_folders.py, for authors who want
    /// to audit a module they are about to ship.
    /// </summary>
    internal static class SceneFolderAudit {

        /// <summary>Modules whose SceneObj folders are the base game's, not somebody's mod.</summary>
        private static readonly HashSet<string> StockModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Native", "SandBox", "SandBoxCore", "StoryMode", "CustomBattle",
            "Multiplayer", "BirthAndDeath", "NavalDLC", "Cutscenes_Extended",
        };

        private static readonly Regex SceneName =
            new Regex("<scene\\b[^>]*\\bname=\"([^\"]*)\"", RegexOptions.Compiled);

        public static void Run() {
            try {
                string modules = IOPath.Combine(BasePath.Name, "Modules");
                if (!IODirectory.Exists(modules)) return;

                // What the base game provides, so we can tell when a mod folder shadows one.
                var stockScenes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string module in IODirectory.GetDirectories(modules)) {
                    string name = IOPath.GetFileName(module);
                    if (!StockModules.Contains(name)) continue;
                    foreach (string scene in SceneFolders(module))
                        stockScenes[IOPath.GetFileName(scene)] = name;
                }

                int checkedCount = 0, problems = 0;
                foreach (string module in IODirectory.GetDirectories(modules)) {
                    string moduleName = IOPath.GetFileName(module);
                    if (StockModules.Contains(moduleName)) continue;

                    foreach (string sceneDir in SceneFolders(module)) {
                        checkedCount++;
                        if (Check(moduleName, sceneDir, stockScenes)) problems++;
                    }
                }

                if (checkedCount > 0) {
                    TraceLogger.Write(nameof(SceneFolderAudit),
                        problems == 0
                            ? $"Scene folder audit: {checkedCount} folder(s) in non-stock modules, all fine."
                            : $"Scene folder audit: {problems} of {checkedCount} folder(s) need attention - see above.");
                }
            } catch (Exception ex) {
                // A diagnostic must never be the thing that breaks startup.
                TraceLogger.WriteException(nameof(SceneFolderAudit), "Audit failed", ex);
            }
        }

        private static IEnumerable<string> SceneFolders(string moduleDir) {
            string sceneObj = IOPath.Combine(moduleDir, "SceneObj");
            if (!IODirectory.Exists(sceneObj)) return new string[0];
            try {
                return IODirectory.GetDirectories(sceneObj);
            } catch {
                return new string[0];
            }
        }

        /// <summary>Reports any faults in one scene folder. Returns true if something was wrong.</summary>
        private static bool Check(string moduleName, string sceneDir, Dictionary<string, string> stockScenes) {
            string folder = IOPath.GetFileName(sceneDir);
            string where = $"{moduleName}/SceneObj/{folder}";
            bool hasScene = IOFile.Exists(IOPath.Combine(sceneDir, "scene.xscene"));
            bool bad = false;

            if (!hasScene) {
                if (stockScenes.TryGetValue(folder, out string owner)) {
                    // The nasty one. Bannerlord resolves a scene by name across every loaded module,
                    // so this hollow folder can win over the real scene and hand out a scene with no
                    // navmesh. It appears on its own: the engine writes ShaderCache next to whichever
                    // scene folder it resolved.
                    TraceLogger.Write(nameof(SceneFolderAudit),
                        $"PROBLEM {where}: SHADOWS the stock scene '{folder}' from {owner}, but has no " +
                        "scene.xscene of its own. Anything loading that scene can get this folder " +
                        "instead - a scene with no navmesh, where melee never engages and only " +
                        "archers act. Delete this folder.");
                } else {
                    TraceLogger.Write(nameof(SceneFolderAudit),
                        $"PROBLEM {where}: no scene.xscene. Probably a leftover - delete it.");
                }
                return true;
            }

            string declared = DeclaredName(sceneDir);
            if (!string.IsNullOrEmpty(declared) &&
                !string.Equals(declared, folder, StringComparison.OrdinalIgnoreCase)) {
                TraceLogger.Write(nameof(SceneFolderAudit),
                    $"PROBLEM {where}: scene.xscene declares name=\"{declared}\" but the folder is " +
                    $"\"{folder}\". Copying a scene folder and renaming the directory leaves the old " +
                    "name inside. Every stock scene has these matching - make them match.");
                bad = true;
            }

            if (!IOFile.Exists(IOPath.Combine(sceneDir, "navmesh.bin"))) {
                TraceLogger.Write(nameof(SceneFolderAudit),
                    $"PROBLEM {where}: no navmesh.bin. Agents cannot path in this scene: they will " +
                    "spawn, draw weapons and stand still, and only ranged troops will do anything. " +
                    "Adding props to a copied scene does NOT re-bake its navmesh - open the scene " +
                    "once in the Modding Kit and bake it.");
                bad = true;
            }

            return bad;
        }

        private static string DeclaredName(string sceneDir) {
            try {
                // The name attribute is on the root element, so the first few bytes are enough - these
                // files run to hundreds of kilobytes.
                using (var reader = new System.IO.StreamReader(IOPath.Combine(sceneDir, "scene.xscene"))) {
                    var buffer = new char[4096];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    Match match = SceneName.Match(new string(buffer, 0, read));
                    return match.Success ? match.Groups[1].Value : "";
                }
            } catch {
                return "";
            }
        }
    }
}
