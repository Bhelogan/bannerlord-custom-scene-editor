using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    public enum EditorCameraMode {
        ThirdPerson = 0,
        FirstPerson = 1,
    }

    /// <summary>
    /// Editor camera mode.
    ///
    /// First and third person cost almost nothing: <c>Mission.CameraIsFirstPerson</c> is a settable
    /// property whose setter drives the native camera, and the player keeps aiming the same way in
    /// both - so <see cref="PlayerRaySource"/> serves them without branching.
    ///
    /// Free fly is not here yet. It is the only mode that needs real work (taking over
    /// MissionScreen's camera and suppressing agent follow), and the seam it plugs into -
    /// <see cref="FreeCameraRaySource"/> - already exists, so adding it does not disturb placement.
    /// </summary>
    public static class CameraModes {
        public static EditorCameraMode Current { get; private set; } = EditorCameraMode.ThirdPerson;

        public static void Cycle() {
            Set(Current == EditorCameraMode.ThirdPerson
                ? EditorCameraMode.FirstPerson
                : EditorCameraMode.ThirdPerson);
        }

        public static void Set(EditorCameraMode mode) {
            Current = mode;
            if (Mission.Current != null) {
                Mission.Current.CameraIsFirstPerson = mode == EditorCameraMode.FirstPerson;
            }
            EditorHud.ShowMessage(mode == EditorCameraMode.FirstPerson
                ? "Camera: first person."
                : "Camera: third person.");
        }

        /// <summary>Called when a mission starts so the mode does not leak between sessions.</summary>
        public static void Reset() {
            Current = EditorCameraMode.ThirdPerson;
        }
    }
}
