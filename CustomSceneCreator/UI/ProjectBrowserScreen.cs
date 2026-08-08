using System;
using CustomSceneCreator.CampaignEntry;
using CustomSceneCreator.Editing;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Shows the saved-project list over whatever screen is current. Same layer approach as the scene
    /// browser: opened from a settlement menu, so it sits on top of it and cancelling returns exactly
    /// where the player was.
    /// </summary>
    public static class ProjectBrowserScreen {
        private static GauntletLayer? _layer;
        private static ProjectBrowserVM? _vm;

        public static bool IsOpen => _layer != null;

        public static void Open() {
            if (_layer != null) return;

            try {
                _vm = new ProjectBrowserVM(OnOpenProject, OnNewScene, Close);

                _layer = new GauntletLayer("CSCProjectBrowser", 4000) { IsFocusLayer = true };
                _layer.LoadMovie("CSCProjectBrowser", _vm);
                _layer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                ScreenManager.TopScreen.AddLayer(_layer);
                ScreenManager.TrySetFocus(_layer);

                TraceLogger.Write(nameof(ProjectBrowserScreen), "Project browser opened.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ProjectBrowserScreen), "Failed to open project browser", ex);
                Close();
            }
        }

        public static void Close() {
            if (_layer == null) return;
            try {
                _layer.InputRestrictions.ResetInputRestrictions();
                ScreenManager.TopScreen.RemoveLayer(_layer);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ProjectBrowserScreen), "Failed to close project browser", ex);
            } finally {
                _layer = null;
                _vm = null;
            }
        }

        private static void OnOpenProject(SceneProject project) {
            Close();
            // The project carries its own scene and levels, so reopening restores the whole session
            // rather than dropping the objects into whatever scene happened to be chosen last.
            SceneCreatorEntry.OpenEditor(project.TargetScene, project.SceneLevels, project.Name);
        }

        private static void OnNewScene() {
            Close();
            SceneBrowserScreen.Open();
        }
    }
}
