using System;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ScreenSystem;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Stops player actions while an edit mode is active in a player-attached camera.
    ///
    /// Building in first or third person meant swinging at things, blocking, and cycling weapons with
    /// the wheel that raises and lowers the object you are holding. Sheathing the weapons made it
    /// worse rather than better: an unarmed character punches.
    ///
    /// The scene layer blocks mouse buttons and the wheel but leaves mouse movement available for
    /// looking. <see cref="EditorCombatInputPatch"/> replaces the native player controller with a
    /// movement-only whitelist, so keyboard-bound actions and remapped controls cannot leak through.
    ///
    /// Disabling MissionMainAgentController outright would have been simpler and wrong: its tick
    /// handles movement as well as fighting, so the player would have stood rooted to the spot.
    ///
    /// The RTS camera is left alone. It has its own cursor handling, and the player agent is under AI
    /// control there anyway, so none of this applies.
    /// </summary>
    public class CombatInputSuppressor : MissionView {
        public static CombatInputSuppressor? Instance { get; private set; }

        /// <summary>
        /// Read by the controller patch. While true, the native player controller is replaced with
        /// CSC's movement-only controller, so no combat/action binding can leak into an edit mode.
        /// </summary>
        public static bool IsEditorInputActive { get; private set; }

        private bool _suppressing;
        private bool _releaseControllerAfterScreenTick;

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;
            IsEditorInputActive = false;
            _releaseControllerAfterScreenTick = false;
        }

        public override void OnMissionScreenFinalize() {
            Release(clearControllerGuard: true);
            if (Instance == this) Instance = null;
            base.OnMissionScreenFinalize();
        }

        public override void OnMissionScreenTick(float dt) {
            base.OnMissionScreenTick(dt);

            // The edit-mode key is processed by mission logic before the native player controller.
            // Keep the controller guard alive through that controller tick when leaving the editor,
            // then release it here. This prevents the same Backslash press (or any user-remapped
            // action sharing it) from leaking into gameplay on the exit frame.
            if (_releaseControllerAfterScreenTick) {
                _releaseControllerAfterScreenTick = false;
                IsEditorInputActive = false;
            }
        }

        /// <summary>Called each tick by the editor with what it wants.</summary>
        public void Apply(bool editing) {
            bool wanted = editing && CameraModes.Current != EditorCameraMode.Rts;
            if (wanted) {
                _releaseControllerAfterScreenTick = false;
                IsEditorInputActive = true;
                if (!_suppressing) Suppress();
                return;
            }

            // Switching to RTS is immediately safe because RTS hands the agent to the AI. Leaving
            // editing in a player-attached camera needs one guarded controller tick so the key that
            // closed the editor cannot also trigger a remapped combat/action binding.
            bool delayControllerRelease = !editing && IsEditorInputActive;
            _releaseControllerAfterScreenTick = delayControllerRelease;
            Release(clearControllerGuard: !delayControllerRelease);
        }

        private void Suppress() {
            try {
                if (MissionScreen == null) return;

                // Keyboard only. This blocks mouse buttons and the wheel; mouse movement is not
                // part of the mask, so the camera still turns. The controller patch handles every
                // keyboard-bound action by allowing movement alone.
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

        private void Release(bool clearControllerGuard) {
            if (clearControllerGuard) {
                _releaseControllerAfterScreenTick = false;
                IsEditorInputActive = false;
            }
            if (!_suppressing) return;
            _suppressing = false;
            try {
                if (MissionScreen != null) MissionScreen.SceneLayer.InputRestrictions.ResetInputRestrictions();
                TraceLogger.Write(nameof(CombatInputSuppressor), "Combat input restored.");
            } catch (Exception ex) {
                TraceLogger.Write(nameof(CombatInputSuppressor), $"Could not restore input: {ex.Message}");
            }
        }

        /// <summary>True while editor input restrictions are active.</summary>
        public bool IsSuppressing => _suppressing;
    }
}
