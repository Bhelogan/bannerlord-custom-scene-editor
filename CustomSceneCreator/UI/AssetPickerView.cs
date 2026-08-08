using System;
using System.Collections.Generic;
using CustomSceneCreator.Catalog;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ScreenSystem;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Hosts the asset picker over a live mission.
    ///
    /// The setup mirrors the native mission escape menu, which is the proven arrangement for a
    /// clickable modal Gauntlet layer during a running mission: focus layer, input restrictions,
    /// a registered hotkey category, TrySetFocus - AND pausing the engine while it is open.
    ///
    /// The pause is not optional. Without it the live mission re-asserts input focus every frame and
    /// the panel is completely unresponsive: mouse and keyboard both dead. That failure looks like a
    /// broken prefab rather than an input problem, which is why it is worth stating here.
    ///
    /// Close keys are read through the LAYER's own input, because a focus layer with input
    /// restrictions consumes them before global input ever sees them.
    /// </summary>
    public class AssetPickerView : MissionView {
        public static AssetPickerView? Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private GauntletLayer? _layer;
        private AssetPickerVM? _dataSource;

        /// <summary>
        /// Set by the editor logic. Receives the chosen asset AND the filtered list it came from, so
        /// the cycle keys can continue walking the same results afterwards.
        /// </summary>
        public Action<Placeable, IReadOnlyList<Placeable>, string>? OnAssetChosen;

        // Filter state survives close/reopen. Static because the view is recreated per mission and
        // this is a user preference, not mission state.
        private static string _lastSearch = "";
        private static string _lastCategory = "";

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;
        }

        public override void OnMissionScreenFinalize() {
            Close();
            if (Instance == this) Instance = null;
            base.OnMissionScreenFinalize();
        }

        public void Open(IEnumerable<Placeable> placeables) {
            if (IsOpen || MissionScreen == null) return;

            try {
                _dataSource = new AssetPickerVM(placeables, Choose, Close, _lastSearch, _lastCategory);

                _layer = new GauntletLayer("CSCAssetPicker", 4000) { IsFocusLayer = true };
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericPanelGameKeyCategory"));
                _layer.LoadMovie("CSCAssetPicker", _dataSource);
                MissionScreen.AddLayer(_layer);
                ScreenManager.TrySetFocus(_layer);

                MBCommon.PauseGameEngine();
                MouseManager.ShowCursor(true);

                IsOpen = true;
                TraceLogger.Write(nameof(AssetPickerView), "Asset picker opened.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(AssetPickerView), "Failed to open asset picker", ex);
                Close();
            }
        }

        public void Close() {
            if (!IsOpen) return;
            IsOpen = false;

            // Remember the filter before the view model goes away.
            if (_dataSource != null) {
                _lastSearch = _dataSource.CurrentSearch;
                _lastCategory = _dataSource.CurrentCategory;
            }

            try { MBCommon.UnPauseGameEngine(); } catch { }

            if (_layer != null) {
                try {
                    _layer.InputRestrictions.ResetInputRestrictions();
                    MissionScreen?.RemoveLayer(_layer);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(AssetPickerView), $"Layer teardown failed: {ex.Message}");
                }
            }
            _layer = null;
            _dataSource = null;
        }

        private void Choose(Placeable placeable, IReadOnlyList<Placeable> filtered, string scopeLabel) {
            try {
                OnAssetChosen?.Invoke(placeable, filtered, scopeLabel);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(AssetPickerView), "OnAssetChosen threw", ex);
            }
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
