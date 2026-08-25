using System;
using System.IO;
using System.Text;
using CustomSceneCreator.Editing;
using CustomSceneCreator.IO;
using TaleWorlds.Library;

namespace CustomSceneCreator.Boot {
    /// <summary>
    /// One disposable SceneObj cache for testing a saved CSC navmesh without restarting for each
    /// project. The engine indexes scene *folder names* only at process start, but it re-reads the
    /// files in a known folder whenever that scene loads. This intentionally mirrors Homesteads'
    /// reusable scene-slot design.
    /// </summary>
    internal static class CscNavMeshTestSlot {
        // Two fixed folders are indexed at startup. Bannerlord keeps scene files locked briefly
        // after a mission ends; alternating slots lets the next selected test launch immediately
        // instead of falling back to the source scene while the previous slot unlocks.
        private static readonly string[] SceneNames = {
            "csc_navmesh_test_slot_00", "csc_navmesh_test_slot_01",
        };

        private static readonly string[] SceneFiles = {
            "terrain.bin", "scene.xscene", "atmosphere.xml", "flora.bin", "navmesh.bin",
        };

        private static bool _checked;
        private static bool _readyThisRun;
        private static int _nextSlot;

        private static string SlotFolder(string sceneName) => Path.Combine(
            BasePath.Name, "Modules", "CustomSceneCreator", "SceneObj", sceneName);

        /// <summary>
        /// Creates a placeholder early enough for the *next* game boot to index it. The first CSC
        /// version containing this code therefore needs one restart after installation, not one
        /// restart for every scene or every bake.
        /// </summary>
        public static void EnsureSlotExists() {
            if (_checked) return;
            _checked = true;
            _readyThisRun = true;
            foreach (string sceneName in SceneNames) {
                if (!IsComplete(SlotFolder(sceneName))) { _readyThisRun = false; break; }
            }
            if (_readyThisRun) return;

            try {
                string? source = SceneNavMeshBaker.FindSceneNavMesh("mp_skirmish_spawn_test");
                if (source == null) source = FindAnyBattleNavMesh();
                if (source == null) {
                    TraceLogger.Write(nameof(CscNavMeshTestSlot),
                        "Could not create navmesh test slot: no installed template scene was found.");
                    return;
                }
                foreach (string sceneName in SceneNames) {
                    string folder = SlotFolder(sceneName);
                    if (!IsComplete(folder)) Seed(Path.GetDirectoryName(source)!, sceneName);
                }
                TraceLogger.Write(nameof(CscNavMeshTestSlot),
                    "Created reusable navmesh test slots. Restart Bannerlord once before using baked scene tests.");
            } catch (Exception ex) {
                TraceLogger.Write(nameof(CscNavMeshTestSlot),
                    $"Could not create navmesh test slot: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Publishes the selected base scene plus its saved bake and returns the fixed slot name.</summary>
        public static string? Prepare(SceneProject project) {
            EnsureSlotExists();
            if (!_readyThisRun) {
                TraceLogger.Write(nameof(CscNavMeshTestSlot),
                    "Baked test slot is not indexed in this game session; restart Bannerlord once.");
                return null;
            }
            if (project == null || string.IsNullOrWhiteSpace(project.TargetScene)) return null;

            int preferredSlot = _nextSlot++ % SceneNames.Length;
            for (int attempt = 0; attempt < SceneNames.Length; attempt++) {
                string sceneName = SceneNames[(preferredSlot + attempt) % SceneNames.Length];
                try {
                string slotFolder = SlotFolder(sceneName);
                string? sourceNavmesh = SceneNavMeshBaker.FindSceneNavMesh(project.TargetScene);
                if (sourceNavmesh == null) return null;
                string sourceFolder = Path.GetDirectoryName(sourceNavmesh)!;

                // The durable bake belongs in Documents. The slot is merely the engine-readable,
                // single-use cache of that result. If there is no edit to bake, the stock navmesh is
                // valid and still lets the selector open a normal scene through the slot.
                string navmesh = SceneNavMeshBaker.FindBakedNavMesh(project) ?? sourceNavmesh;

                // Bannerlord holds terrain.bin/scene.xscene open for a while after a test mission.
                // Re-copying those immutable files made a perfectly valid subsequent launch fail
                // with an IOException.  A slot already seeded from this base scene needs only its
                // navmesh refreshed; the visual scene files are identical for every saved project
                // using that base.
                bool sameSource = IsComplete(slotFolder)
                                  && SourceMatches(slotFolder, project.TargetScene);
                if (!sameSource && !Seed(sourceFolder, sceneName)) continue;
                File.Copy(navmesh, Path.Combine(slotFolder, "navmesh.bin"), true);
                File.WriteAllText(Path.Combine(slotFolder, "csc_source_scene.txt"), project.TargetScene,
                    Encoding.UTF8);

                TraceLogger.Write(nameof(CscNavMeshTestSlot),
                    $"Published '{project.Name}' ({project.TargetScene}) into '{sceneName}': " +
                    $"{project.Entities?.Count ?? 0} runtime object(s), navmesh='{navmesh}'.");
                return sceneName;
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(CscNavMeshTestSlot),
                        $"Could not publish navmesh test slot '{sceneName}' (attempt " +
                        $"{attempt + 1}/{SceneNames.Length}): {ex.GetType().Name}: {ex.Message}");
                }
            }

            TraceLogger.Write(nameof(CscNavMeshTestSlot),
                $"Could not publish '{project.Name}': both reusable test slots are still locked.");
            return null;
        }

        private static bool SourceMatches(string slotFolder, string targetScene) {
            try {
                string marker = Path.Combine(slotFolder, "csc_source_scene.txt");
                return File.Exists(marker)
                       && string.Equals(File.ReadAllText(marker).Trim().TrimStart('\uFEFF'),
                                        targetScene, StringComparison.OrdinalIgnoreCase);
            } catch {
                return false;
            }
        }

        private static bool Seed(string sourceFolder, string sceneName) {
            try {
                string slotFolder = SlotFolder(sceneName);
                Directory.CreateDirectory(slotFolder);
                foreach (string file in SceneFiles) {
                    string source = Path.Combine(sourceFolder, file);
                    if (!File.Exists(source)) continue;
                    string destination = Path.Combine(slotFolder, file);
                    if (file == "scene.xscene") RewriteSceneName(source, destination, sceneName);
                    else File.Copy(source, destination, true);
                }

                string sourceShaders = Path.Combine(sourceFolder, "ShaderCache");
                string slotShaders = Path.Combine(slotFolder, "ShaderCache");
                if (Directory.Exists(slotShaders)) Directory.Delete(slotShaders, true);
                if (Directory.Exists(sourceShaders)) CopyDirectory(sourceShaders, slotShaders);
                return IsComplete(slotFolder);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(CscNavMeshTestSlot),
                    $"Could not seed navmesh test slot: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool IsComplete(string folder) {
            foreach (string file in SceneFiles) {
                FileInfo info = new FileInfo(Path.Combine(folder, file));
                if (!info.Exists || info.Length == 0) return false;
            }
            return true;
        }

        private static void RewriteSceneName(string source, string destination, string sceneName) {
            string text = File.ReadAllText(source);
            int open = text.IndexOf("<scene ", StringComparison.Ordinal);
            int nameAt = open >= 0 ? text.IndexOf("name=\"", open, StringComparison.Ordinal) : -1;
            int start = nameAt >= 0 ? nameAt + 6 : -1;
            int end = start > 0 ? text.IndexOf('"', start) : -1;
            if (end < 0) { File.Copy(source, destination, true); return; }
            File.WriteAllText(destination, text.Substring(0, start) + sceneName + text.Substring(end));
        }

        private static void CopyDirectory(string source, string destination) {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (string child in Directory.GetDirectories(source))
                CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }

        private static string? FindAnyBattleNavMesh() {
            try {
                string modules = Path.Combine(BasePath.Name, "Modules");
                foreach (string module in Directory.GetDirectories(modules)) {
                    string sceneRoot = Path.Combine(module, "SceneObj");
                    if (!Directory.Exists(sceneRoot)) continue;
                    foreach (string folder in Directory.GetDirectories(sceneRoot, "battle_terrain_*")) {
                        string navmesh = Path.Combine(folder, "navmesh.bin");
                        if (File.Exists(navmesh) && IsComplete(folder)) return navmesh;
                    }
                }
            } catch { }
            return null;
        }
    }
}
