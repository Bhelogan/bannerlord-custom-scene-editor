using System;
using CustomSceneCreator.Boot;
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

        public static bool OpenEditor(string sceneName, string sceneLevels) {
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
                TraceLogger.Write(nameof(SceneCreatorEntry),
                    $"Opening editor — scene='{sceneName}' levels='{sceneLevels}'.");
                SceneCreatorMission.Open(sceneName, sceneLevels ?? "");
                return true;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneCreatorEntry),
                    $"Failed to open editor on scene '{sceneName}'", ex);
                return false;
            }
        }
    }
}
