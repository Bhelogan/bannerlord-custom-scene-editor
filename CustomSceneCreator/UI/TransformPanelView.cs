using System;
using CustomSceneCreator.Api;
using CustomSceneCreator.Editing;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// A small interactive, non-modal transform panel. It deliberately claims mouse buttons only
    /// while the cursor is over one of its widgets; the rest of the game and the editor camera stay
    /// live, which makes it safe to line a rotated object up against real scene geometry.
    /// </summary>
    public sealed class TransformPanelView : MissionView {
        public static TransformPanelView? Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private GauntletLayer? _layer;
        private TransformPanelVM? _vm;
        private SceneEditingMissionLogic? _editor;
        private PlacedEntity? _entity;
        private bool _inputClaimed;

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;
        }

        public override void OnMissionScreenFinalize() {
            Close();
            if (Instance == this) Instance = null;
            base.OnMissionScreenFinalize();
        }

        public void Open(SceneEditingMissionLogic editor, PlacedEntity entity) {
            if (MissionScreen == null) return;
            if (IsOpen) Close();
            try {
                _editor = editor;
                _entity = entity;
                _vm = new TransformPanelVM(editor, entity, Close);
                _layer = new GauntletLayer("CSCTransformPanel", 300);
                _layer.LoadMovie("CSCTransformPanel", _vm);
                MissionScreen.AddLayer(_layer);
                MouseManager.ShowCursor(true);
                IsOpen = true;
                _inputClaimed = false;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(TransformPanelView), "Open failed", ex);
                Close();
            }
        }

        public void Refresh() => _vm?.Refresh();

        public void Close() {
            if (!IsOpen && _layer == null) return;
            IsOpen = false;
            try { _layer?.InputRestrictions.ResetInputRestrictions(); } catch { }
            try { if (_layer != null) MissionScreen?.RemoveLayer(_layer); } catch { }
            _inputClaimed = false;
            SceneEditingMissionLogic? editor = _editor;
            PlacedEntity? entity = _entity;
            _layer = null;
            _vm = null;
            _editor = null;
            _entity = null;
            if (editor != null && entity != null) editor.StopTransform(entity);
        }

        public override void OnMissionScreenTick(float dt) {
            base.OnMissionScreenTick(dt);
            if (!IsOpen || _layer == null) return;
            UpdateLayerInputClaim();
            // This is non-modal, so camera modes can route Escape through either the layer or raw
            // mission input. Accept both paths.
            if (_layer.Input.IsKeyReleased(InputKey.Escape) || Input.IsKeyReleased(InputKey.Escape)) Close();
        }

        private void UpdateLayerInputClaim() {
            if (_layer == null) return;
            try {
                bool cursorOverPanel = _layer.HitTest();
                if (cursorOverPanel == _inputClaimed) return;
                if (cursorOverPanel)
                    _layer.InputRestrictions.SetInputRestrictions(isMouseVisible: false, InputUsageMask.MouseButtons);
                else
                    _layer.InputRestrictions.ResetInputRestrictions();
                _inputClaimed = cursorOverPanel;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(TransformPanelView), $"Input claim failed: {ex.Message}");
            }
        }
    }
}
