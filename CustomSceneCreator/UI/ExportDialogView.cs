using System;
using CustomSceneCreator.Editing;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ScreenSystem;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Hosts the export dialog. Same modal arrangement as the asset picker - focus layer, input
    /// restrictions, hotkey category, and the engine pause without which the panel is unresponsive.
    /// </summary>
    public class ExportDialogView : MissionView {
        public static ExportDialogView? Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private GauntletLayer? _layer;
        private ExportDialogVM? _dataSource;

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;
        }

        public override void OnMissionScreenFinalize() {
            Close();
            if (Instance == this) Instance = null;
            base.OnMissionScreenFinalize();
        }

        public void Open(SceneProject project) {
            if (IsOpen || MissionScreen == null) return;

            try {
                _dataSource = new ExportDialogVM(project, Close);

                _layer = new GauntletLayer("CSCExportDialog", 4000) { IsFocusLayer = true };
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericPanelGameKeyCategory"));
                _layer.LoadMovie("CSCExportDialog", _dataSource);
                MissionScreen.AddLayer(_layer);
                ScreenManager.TrySetFocus(_layer);

                MBCommon.PauseGameEngine();
                MouseManager.ShowCursor(true);

                IsOpen = true;
                TraceLogger.Write(nameof(ExportDialogView), "Export dialog opened.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ExportDialogView), "Failed to open export dialog", ex);
                Close();
            }
        }

        /// <summary>Raised when the dialog closes, so the palette can pick up a new template.</summary>
        public Action? OnClosed;

        public void Close() {
            if (!IsOpen) return;
            IsOpen = false;

            try { MBCommon.UnPauseGameEngine(); } catch { }

            if (_layer != null) {
                try {
                    _layer.InputRestrictions.ResetInputRestrictions();
                    MissionScreen?.RemoveLayer(_layer);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(ExportDialogView), $"Layer teardown failed: {ex.Message}");
                }
            }
            _layer = null;
            _dataSource = null;

            try { OnClosed?.Invoke(); } catch { }
        }

        public override void OnMissionScreenTick(float dt) {
            base.OnMissionScreenTick(dt);
            if (!IsOpen || _layer == null) return;

            bool exit = _layer.Input.IsHotKeyReleased("Exit")
                     || _layer.Input.IsKeyReleased(InputKey.Escape)
                     || (TaleWorlds.InputSystem.Input.IsGamepadActive
                         && _layer.Input.IsKeyReleased(InputKey.ControllerRRight));
            if (exit) Close();
        }
    }
}
