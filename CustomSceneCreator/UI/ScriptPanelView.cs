using System;
using CustomSceneCreator.Api;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ScreenSystem;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Hosts the script panel. Same modal arrangement as the asset picker - focus layer, input
    /// restrictions, hotkey category, and the engine pause without which the panel is unresponsive.
    /// </summary>
    public class ScriptPanelView : MissionView {
        public static ScriptPanelView? Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private GauntletLayer? _layer;
        private ScriptPanelVM? _dataSource;

        /// <summary>Raised when scripts change, so the editor can re-apply them and mark the project dirty.</summary>
        public Action<PlacedEntity>? OnScriptsChanged;

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;
        }

        public override void OnMissionScreenFinalize() {
            Close();
            if (Instance == this) Instance = null;
            base.OnMissionScreenFinalize();
        }

        public void Open(PlacedEntity entity) {
            if (IsOpen || MissionScreen == null || entity == null) return;

            try {
                _dataSource = new ScriptPanelVM(entity, Close, () => OnScriptsChanged?.Invoke(entity));

                _layer = new GauntletLayer("CSCScriptPanel", 4000) { IsFocusLayer = true };
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericPanelGameKeyCategory"));
                _layer.LoadMovie("CSCScriptPanel", _dataSource);
                MissionScreen.AddLayer(_layer);
                ScreenManager.TrySetFocus(_layer);

                MBCommon.PauseGameEngine();
                MouseManager.ShowCursor(true);

                IsOpen = true;
                TraceLogger.Write(nameof(ScriptPanelView),
                    $"Script panel opened for '{entity.PrefabName}' ({entity.Scripts.Count} attached).");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ScriptPanelView), "Failed to open script panel", ex);
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
                    TraceLogger.Write(nameof(ScriptPanelView), $"Layer teardown failed: {ex.Message}");
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
