using System;
using System.Globalization;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
using CustomSceneCreator.Editing;
using TaleWorlds.Library;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Supplies deterministic controls and exact degree entry without relying on camera-specific
    /// world-space dragging.
    /// </summary>
    public sealed class TransformPanelVM : ViewModel {
        private readonly SceneEditingMissionLogic _editor;
        private readonly PlacedEntity _entity;
        private readonly Action _close;
        private string _stepText = "15";

        public TransformPanelVM(SceneEditingMissionLogic editor, PlacedEntity entity, Action close) {
            _editor = editor;
            _entity = entity;
            _close = close;
        }

        [DataSourceProperty] public string TitleText => "Transform";
        [DataSourceProperty] public string SubjectText => PlaceableRegistry.DisplayNameFor(_entity.PrefabName);
        [DataSourceProperty] public string HintText =>
            "Enter exact degrees, or use - / + for the chosen step. Done or Escape closes this panel.";
        [DataSourceProperty] public string PitchLabel => "X / Pitch (red)";
        [DataSourceProperty] public string RollLabel => "Y / Roll (green)";
        [DataSourceProperty] public string YawLabel => "Z / Yaw (blue)";
        [DataSourceProperty] public string StepLabel => "Step (degrees)";
        [DataSourceProperty] public string CloseText => "Done";

        [DataSourceProperty]
        public string PitchText {
            get => Format(Euler().x);
            set => SetAxis(value, 0, nameof(PitchText));
        }

        [DataSourceProperty]
        public string RollText {
            get => Format(Euler().y);
            set => SetAxis(value, 1, nameof(RollText));
        }

        [DataSourceProperty]
        public string YawText {
            get => Format(Euler().z);
            set => SetAxis(value, 2, nameof(YawText));
        }

        [DataSourceProperty]
        public string StepText {
            get => _stepText;
            set {
                if (!TryParse(value, out float parsed) || parsed <= 0f || parsed > 180f) {
                    OnPropertyChangedWithValue(_stepText, nameof(StepText));
                    return;
                }
                _stepText = Format(parsed);
                OnPropertyChangedWithValue(_stepText, nameof(StepText));
            }
        }

        public void ExecutePitchMinus() => Rotate(0, -Step());
        public void ExecutePitchPlus() => Rotate(0, Step());
        public void ExecuteRollMinus() => Rotate(1, -Step());
        public void ExecuteRollPlus() => Rotate(1, Step());
        public void ExecuteYawMinus() => Rotate(2, -Step());
        public void ExecuteYawPlus() => Rotate(2, Step());
        public void ExecuteClose() => _close();

        /// <summary>Called after any transform button action.</summary>
        public void Refresh() {
            OnPropertyChangedWithValue(PitchText, nameof(PitchText));
            OnPropertyChangedWithValue(RollText, nameof(RollText));
            OnPropertyChangedWithValue(YawText, nameof(YawText));
            OnPropertyChangedWithValue(StepText, nameof(StepText));
        }

        private Vec3 Euler() => _editor.GetTransformEulerDegrees(_entity);
        private float Step() => TryParse(_stepText, out float value) ? value : 15f;
        private void Rotate(int axis, float degrees) {
            _editor.SelectTransformAxis(axis);
            _editor.RotateTransformAxis(_entity, axis, degrees);
            Refresh();
        }

        private void SetAxis(string text, int axis, string propertyName) {
            if (!TryParse(text, out float degrees) || degrees < -36000f || degrees > 36000f) {
                OnPropertyChangedWithValue(axis == 0 ? PitchText : axis == 1 ? RollText : YawText,
                    propertyName);
                return;
            }
            _editor.SelectTransformAxis(axis);
            _editor.SetTransformAxisDegrees(_entity, axis, degrees);
            Refresh();
        }

        private static bool TryParse(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
