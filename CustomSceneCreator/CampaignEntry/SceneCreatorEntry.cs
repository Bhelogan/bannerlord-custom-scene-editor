using System;
using CustomSceneCreator.Boot;
using CustomSceneCreator.Editing;
using TaleWorlds.Library;
using TaleWorlds.CampaignSystem;

namespace CustomSceneCreator.CampaignEntry {
    /// <summary>
    /// Single funnel for every way the editor can be opened, so the menu option, the console command
    /// and (later) the scene browser all share one code path and one set of guards.
    /// </summary>
    public static class SceneCreatorEntry {
        /// <summary>
        /// A project name that is not taken yet, so a new session cannot overwrite an old one on its
        /// first save. "battle_terrain_001", then "battle_terrain_001 (2)", and so on.
        /// </summary>
        private static string UnusedProjectName(string preferred) {
            try {
                if (ProjectSerializer.Load(preferred) == null) return preferred;
                for (int n = 2; n < 100; n++) {
                    string candidate = $"{preferred} ({n})";
                    if (ProjectSerializer.Load(candidate) == null) return candidate;
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneCreatorEntry), $"Could not check project names: {ex.Message}");
            }
            return preferred;
        }

        /// <summary>
        /// Flat multiplayer test scene: terrain, navmesh and atmosphere, no settlement scripts and a
        /// single "base" level. Used until the scene browser lands.
        /// </summary>
        public const string DefaultScene = "mp_skirmish_spawn_test";

        public static bool OpenEditor(string sceneName, string sceneLevels) =>
            OpenEditor(sceneName, sceneLevels, projectName: null);

        /// <summary>
        /// Opens a scene with NOTHING in it, whatever has been built there before.
        ///
        /// "New - Pick a Scene" means new. Loading the project that happens to share the scene's name
        /// makes a fresh start impossible: the old work reappears, and saving then writes over it.
        /// The project list is how you get back to existing work.
        /// </summary>
        public static bool OpenEditorEmpty(string sceneName, string sceneLevels) =>
            OpenEditor(sceneName, sceneLevels, projectName: null, startEmpty: true);

        /// <param name="projectName">
        /// Which saved layout to load and write back to. Defaults to one project per scene, so
        /// reopening a scene shows what was built there last time rather than an empty copy.
        /// </param>
        public static bool OpenEditor(string sceneName, string sceneLevels, string? projectName) =>
            OpenEditor(sceneName, sceneLevels, projectName, startEmpty: false);

        /// <param name="startEmpty">
        /// Ignore any saved project for this scene and begin with an empty one. The project keeps a
        /// name derived from the scene, so the first save still lands somewhere sensible - but
        /// nothing is READ, so previous work is neither shown nor silently overwritten until saved.
        /// </param>
        public static bool OpenEditor(string sceneName, string sceneLevels, string? projectName,
                                      bool startEmpty) {
            if (string.IsNullOrWhiteSpace(sceneName)) {
                TraceLogger.Write(nameof(SceneCreatorEntry), "OpenEditor called with no scene name.");
                return false;
            }

            if (Campaign.Current == null) {
                TraceLogger.Write(nameof(SceneCreatorEntry),
                    "OpenEditor called with no active campaign — refusing. " +
                    "The editor is entered from inside a campaign; see SceneCreatorCampaignBehavior.");
                return false;
            }

            try {
                string name = string.IsNullOrWhiteSpace(projectName) ? sceneName : projectName!;
                SceneProject project = startEmpty
                    ? new SceneProject { Name = UnusedProjectName(name) }
                    : ProjectSerializer.Load(name) ?? new SceneProject { Name = name };

                // A project remembers the scene it was built for. Reopening it on a different scene
                // would drop every object at coordinates that mean nothing there, so the project's
                // own scene wins and the caller's choice of levels is recorded against it.
                project.TargetScene = sceneName;
                project.SceneLevels = sceneLevels ?? "";

                TraceLogger.Write(nameof(SceneCreatorEntry),
                    $"Opening editor — scene='{sceneName}' levels='{sceneLevels}' " +
                    $"project='{project.Name}' ({project.Entities.Count} saved object(s))" +
                    (startEmpty ? " [new, existing work not loaded]" : "") + ".");

                SceneCreatorMission.Open(sceneName, sceneLevels ?? "", project);
                return true;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneCreatorEntry),
                    $"Failed to open editor on scene '{sceneName}'", ex);
                return false;
            }
        }

        /// <summary>Launches a saved layout as a non-editing walk-around with the player's party.</summary>
        public static bool OpenWalkaround(string sceneName, string sceneLevels) {
            try {
                SceneProject project = ProjectSerializer.Load(sceneName) ?? new SceneProject {
                    Name = sceneName, TargetScene = sceneName, SceneLevels = sceneLevels ?? ""
                };
                project.TargetScene = sceneName;
                project.SceneLevels = sceneLevels ?? "";
                return OpenWalkaround(project);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneCreatorEntry), "Could not open party walk-around", ex);
                return false;
            }
        }

        /// <summary>Launches a selected saved project directly, retaining projects whose name differs from their scene.</summary>
        public static bool OpenWalkaround(SceneProject project) {
            try {
                if (project == null || string.IsNullOrWhiteSpace(project.TargetScene)) return false;
                string? slot = CscNavMeshTestSlot.Prepare(project);
                if (slot == null) {
                    TraceLogger.Write(nameof(SceneCreatorEntry),
                        "Walk-around refused: reusable test slot could not be published.");
                    return false;
                }
                return SceneWalkaroundMission.Open(project, slot) != null;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneCreatorEntry), "Could not open party walk-around", ex);
                return false;
            }
        }

        /// <summary>Launches the existing navmesh skirmish directly from the selected saved layout.</summary>
        public static bool OpenNavmeshSkirmish(string sceneName, string sceneLevels) {
            try {
                SceneProject? project = ProjectSerializer.Load(sceneName);
                if (project == null) return false;
                project.TargetScene = sceneName;
                project.SceneLevels = sceneLevels ?? "";
                return OpenNavmeshSkirmish(project);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneCreatorEntry), "Could not open navmesh skirmish", ex);
                return false;
            }
        }

        /// <summary>Launches a selected saved project directly, retaining projects whose name differs from their scene.</summary>
        public static bool OpenNavmeshSkirmish(SceneProject project) {
            try {
                if (project == null || string.IsNullOrWhiteSpace(project.TargetScene)) return false;
                var positions = NavMeshTestBattleLogic.DerivePositionsFromProject(project);
                int raiderCount = NavMeshTestBattleLogic.DeriveRaidEnemyCount();
                string? slot = CscNavMeshTestSlot.Prepare(project);
                if (slot == null) {
                    TraceLogger.Write(nameof(SceneCreatorEntry),
                        "Raid-scale battle refused: reusable test slot could not be published.");
                    return false;
                }
                return NavMeshTestMission.Open(slot, project.SceneLevels ?? "", positions.playerPos,
                    positions.enemyPos, raiderCount, project) != null;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneCreatorEntry), "Could not open navmesh skirmish", ex);
                return false;
            }
        }
    }
}
