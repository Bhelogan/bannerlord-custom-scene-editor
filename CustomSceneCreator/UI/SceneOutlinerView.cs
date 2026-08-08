using System;
using System.Collections.Generic;
using CustomSceneCreator.Api;
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
    /// Hosts the scene contents list. Same modal arrangement as the other editor panels - focus
    /// layer, input restrictions, hotkey category, and the engine pause without which it is
    /// unresponsive.
    /// </summary>
    public class SceneOutlinerView : MissionView {
        public static SceneOutlinerView? Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private GauntletLayer? _layer;
        private SceneOutlinerVM? _dataSource;

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;
        }

        public override void OnMissionScreenFinalize() {
            Close();
            if (Instance == this) Instance = null;
            base.OnMissionScreenFinalize();
        }

        public void Open(IEnumerable<PlacedEntity> placed, SceneEditingMissionLogic editor) {
            if (IsOpen || MissionScreen == null) return;

            try {
                _dataSource = new SceneOutlinerVM(placed, editor, Close);

                _layer = new GauntletLayer("CSCSceneOutliner", 4000) { IsFocusLayer = true };
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericPanelGameKeyCategory"));
                _layer.LoadMovie("CSCSceneOutliner", _dataSource);
                MissionScreen.AddLayer(_layer);
                ScreenManager.TrySetFocus(_layer);

                MBCommon.PauseGameEngine();
                MouseManager.ShowCursor(true);

                IsOpen = true;
                TraceLogger.Write(nameof(SceneOutlinerView), "Scene contents list opened.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneOutlinerView), "Failed to open scene contents", ex);
                Close();
            }
        }

        public void Close() {
            if (!IsOpen) return;
            IsOpen = false;

            try { MBCommon.UnPauseGameEngine(); } catch { }

            if (_layer != null) {
                try {
                    _layer.InputRestrictions.ResetInputRestrictions();
                    MissionScreen?.RemoveLayer(_layer);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(SceneOutlinerView), $"Layer teardown failed: {ex.Message}");
                }
            }
            _layer = null;
            _dataSource = null;
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
