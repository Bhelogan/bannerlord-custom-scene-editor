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
    public class TexturePanelView : MissionView {
        public static TexturePanelView? Instance { get; private set; }
        public static bool IsOpen { get; private set; }
        public Action<PlacedEntity>? OnChanged;
        private GauntletLayer? _layer;
        private TexturePanelVM? _vm;
        public override void OnMissionScreenInitialize() { base.OnMissionScreenInitialize(); Instance = this; }
        public override void OnMissionScreenFinalize() { Close(); if (Instance == this) Instance = null; base.OnMissionScreenFinalize(); }
        public void Open(PlacedEntity entity) {
            if (IsOpen || MissionScreen == null) return;
            try {
                _vm = new TexturePanelVM(entity, Close, () => OnChanged?.Invoke(entity));
                _layer = new GauntletLayer("CSCTexturePanel", 4000) { IsFocusLayer = true };
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericPanelGameKeyCategory"));
                _layer.LoadMovie("CSCTexturePanel", _vm); MissionScreen.AddLayer(_layer); ScreenManager.TrySetFocus(_layer);
                MBCommon.PauseGameEngine(); MouseManager.ShowCursor(true); IsOpen = true;
            } catch (Exception ex) { TraceLogger.WriteException(nameof(TexturePanelView), "Open failed", ex); Close(); }
        }
        public void Close() {
            if (!IsOpen) return; IsOpen = false; try { MBCommon.UnPauseGameEngine(); } catch { }
            if (_layer != null) { try { _layer.InputRestrictions.ResetInputRestrictions(); MissionScreen?.RemoveLayer(_layer); } catch { } }
            _layer = null; _vm = null;
        }
        public override void OnMissionScreenTick(float dt) {
            base.OnMissionScreenTick(dt); if (!IsOpen || _layer == null) return;
            if (_layer.Input.IsHotKeyReleased("Exit") || _layer.Input.IsKeyReleased(InputKey.Escape)) Close();
        }
    }
}
