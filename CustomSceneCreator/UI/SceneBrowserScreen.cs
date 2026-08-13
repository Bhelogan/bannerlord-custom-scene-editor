using System;
using CustomSceneCreator.CampaignEntry;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Shows the scene browser as a Gauntlet layer over whatever screen is current.
    ///
    /// A layer rather than a full GameState: the browser is opened from a settlement menu and needs
    /// to sit on top of it, and a layer keeps the campaign state underneath intact so cancelling
    /// returns exactly where the player was. Making this a real GameState is only necessary once
    /// leaving the editor should return here rather than to the map (plan section 16).
    /// </summary>
    public static class SceneBrowserScreen {
        private static GauntletLayer? _layer;
        private static SceneBrowserVM? _vm;

        public static bool IsOpen => _layer != null;

        public static void Open() {
            if (_layer != null) return;

            try {
                _vm = new SceneBrowserVM(OnConfirm, Close);

                _layer = new GauntletLayer("CSCSceneBrowser", 4000) { IsFocusLayer = true };
                _layer.LoadMovie("CSCSceneBrowser", _vm);

                // Without input restrictions the layer renders but every click falls through to the
                // screen underneath, which reads as a completely unresponsive window.
                _layer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                ScreenManager.TopScreen.AddLayer(_layer);
                ScreenManager.TrySetFocus(_layer);

                TraceLogger.Write(nameof(SceneBrowserScreen), "Scene browser opened.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneBrowserScreen), "Failed to open scene browser", ex);
                Close();
            }
        }

        public static void Close() {
            if (_layer == null) return;
            try {
                _layer.InputRestrictions.ResetInputRestrictions();
                ScreenManager.TopScreen.RemoveLayer(_layer);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneBrowserScreen), "Failed to close scene browser", ex);
            } finally {
                _layer = null;
                _vm = null;
            }
        }

        private static void OnConfirm(string sceneName, string sceneLevels) {
            Close();
            // Come back here on the way out, so trying several scenes does not mean walking through
            // the settlement menu each time.
            ReturnToBrowser.ArmForScenes();
            if (!SceneCreatorEntry.OpenEditorEmpty(sceneName, sceneLevels)) ReturnToBrowser.Cancel();
        }
    }
}
