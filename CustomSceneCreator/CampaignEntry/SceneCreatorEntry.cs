using System;
using CustomSceneCreator.Boot;
using CustomSceneCreator.Editing;
using TaleWorlds.CampaignSystem;

namespace CustomSceneCreator.CampaignEntry {
    /// <summary>
    /// Single funnel for every way the editor can be opened, so the menu option, the console command
    /// and (later) the scene browser all share one code path and one set of guards.
    /// </summary>
    public static class SceneCreatorEntry {
        /// <summary>
        /// Flat multiplayer test scene: terrain, navmesh and atmosphere, no settlement scripts and a
        /// single "base" level. Used until the scene browser lands.
        /// </summary>
        public const string DefaultScene = "mp_skirmish_spawn_test";

        public static bool OpenEditor(string sceneName, string sceneLevels) =>
            OpenEditor(sceneName, sceneLevels, projectName: null);

        /// <param name="projectName">
        /// Which saved layout to load and write back to. Defaults to one project per scene, so
        /// reopening a scene shows what was built there last time rather than an empty copy.
        /// </param>
        public static bool OpenEditor(string sceneName, string sceneLevels, string? projectName) {
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
                SceneProject project = ProjectSerializer.Load(name) ?? new SceneProject { Name = name };

                // A project remembers the scene it was built for. Reopening it on a different scene
                // would drop every object at coordinates that mean nothing there, so the project's
                // own scene wins and the caller's choice of levels is recorded against it.
                project.TargetScene = sceneName;
                project.SceneLevels = sceneLevels ?? "";

                TraceLogger.Write(nameof(SceneCreatorEntry),
                    $"Opening editor — scene='{sceneName}' levels='{sceneLevels}' " +
                    $"project='{name}' ({project.Entities.Count} saved object(s)).");

                SceneCreatorMission.Open(sceneName, sceneLevels ?? "", project);
                return true;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneCreatorEntry),
                    $"Failed to open editor on scene '{sceneName}'", ex);
                return false;
            }
        }
    }
}
