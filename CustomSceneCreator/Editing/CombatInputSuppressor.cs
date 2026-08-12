using System;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ScreenSystem;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Stops the mouse from reaching the player's combat controls while an edit mode is active in a
    /// player-attached camera.
    ///
    /// Building in first or third person meant swinging at things, blocking, and cycling weapons with
    /// the wheel that raises and lowers the object you are holding. Sheathing the weapons made it
    /// worse rather than better: an unarmed character punches.
    ///
    /// The fix is at the input layer rather than the agent. The scene layer is restricted to KEYBOARD
    /// input only, which blocks mouse buttons and the wheel - attack, block, weapon swap - while
    /// leaving mouse MOVEMENT alone, so looking around still works, and leaving the keyboard alone,
    /// so WASD still walks.
    ///
    /// Disabling MissionMainAgentController outright would have been simpler and wrong: its tick
    /// handles movement as well as fighting, so the player would have stood rooted to the spot.
    ///
    /// The RTS camera is left alone. It has its own cursor handling, and the player agent is under AI
    /// control there anyway, so none of this applies.
    /// </summary>
    public class CombatInputSuppressor : MissionView {
        public static CombatInputSuppressor? Instance { get; private set; }

        private bool _suppressing;

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;
        }

        public override void OnMissionScreenFinalize() {
            Release();
            if (Instance == this) Instance = null;
            base.OnMissionScreenFinalize();
        }

        /// <summary>Called each tick by the editor with what it wants.</summary>
        public void Apply(bool editing) {
            bool wanted = editing && CameraModes.Current != EditorCameraMode.Rts;
            if (wanted == _suppressing) return;

            if (wanted) Suppress();
            else Release();
        }

        private void Suppress() {
            try {
                if (MissionScreen == null) return;

                // Keyboard only. Mouse buttons and the wheel are what reach the combat controls;
                // mouse movement is not part of the mask, so the camera still turns.
                MissionScreen.SceneLayer.InputRestrictions.SetInputRestrictions(
                    false, InputUsageMask.Keyboardkeys);
                _suppressing = true;
                TraceLogger.Write(nameof(CombatInputSuppressor), "Combat input suppressed for editing.");

                // Said once, because blocking the mouse may also block the click that places. F is
                // the keyboard place key and is unaffected either way, so it is the one to name.
                EditorHud.ShowMessage(
                    $"Combat controls off while editing. {Keys.Describe(Keys.PlaceAlt)} places.");
            } catch (Exception ex) {
                TraceLogger.Write(nameof(CombatInputSuppressor), $"Could not suppress input: {ex.Message}");
            }
        }

        private void Release() {
            if (!_suppressing) return;
            _suppressing = false;
            try {
                if (MissionScreen != null) MissionScreen.SceneLayer.InputRestrictions.ResetInputRestrictions();
                TraceLogger.Write(nameof(CombatInputSuppressor), "Combat input restored.");
            } catch (Exception ex) {
                TraceLogger.Write(nameof(CombatInputSuppressor), $"Could not restore input: {ex.Message}");
            }
        }

        /// <summary>True while the mouse is being held off the scene - the HUD says so.</summary>
        public bool IsSuppressing => _suppressing;
    }
}
