using System;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Hosts the top-left status readout.
    ///
    /// Unlike the asset picker this is a passive overlay: no focus, no input restrictions, no engine
    /// pause. It must never take input, or it would swallow the clicks that place objects - which is
    /// why the layer priority is low and the prefab marks everything unclickable.
    /// </summary>
    public class EditorStatusView : MissionView {
        public static EditorStatusView? Instance { get; private set; }

        private GauntletLayer? _layer;
        private EditorStatusVM? _dataSource;

        public EditorStatusVM? DataSource => _dataSource;

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;

            try {
                _dataSource = new EditorStatusVM { IsVisible = false };
                _layer = new GauntletLayer("CSCEditorStatus", 1);
                _layer.LoadMovie("CSCEditorStatus", _dataSource);
                MissionScreen.AddLayer(_layer);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(EditorStatusView), "Failed to create status overlay", ex);
            }
        }

        public override void OnMissionScreenFinalize() {
            try {
                if (_layer != null) MissionScreen?.RemoveLayer(_layer);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(EditorStatusView), $"Teardown failed: {ex.Message}");
            }
            _layer = null;
            _dataSource = null;
            if (Instance == this) Instance = null;
            base.OnMissionScreenFinalize();
        }
    }
}
