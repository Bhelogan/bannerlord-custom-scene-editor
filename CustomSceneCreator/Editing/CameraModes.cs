using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    public enum EditorCameraMode {
        /// <summary>Overhead free camera, placement follows the cursor. Where most building gets
        /// done, but NOT how a scene opens - see CameraModes.</summary>
        Rts = 0,
        ThirdPerson = 1,
        FirstPerson = 2,
    }

    /// <summary>
    /// Editor camera mode, and the placement ray that belongs to each.
    ///
    /// The two player-attached modes cost almost nothing: <c>Mission.CameraIsFirstPerson</c> drives
    /// the native camera and the player aims the same way in both, so one ray source serves them.
    /// RTS is the one with real machinery behind it - see <see cref="RtsCameraView"/> - and it is
    /// also the one that changes placement semantics, from "where I am looking" to "where the cursor
    /// is".
    /// </summary>
    public static class CameraModes {
        /// <summary>
        /// A scene opens in third person, walking around, exactly as any other mission does. The
        /// editor is a thing you turn ON; taking over the camera before being asked makes arriving
        /// in a scene feel broken rather than powerful.
        /// </summary>
        public static EditorCameraMode Current { get; private set; } = EditorCameraMode.ThirdPerson;

        /// <summary>
        /// True once the player has picked a camera themselves. After that the editor stops choosing
        /// for them - an explicit choice should not be silently undone the next time edit mode is
        /// toggled.
        /// </summary>
        private static bool _playerChose;

        private static readonly IPlacementRaySource PlayerRay = new PlayerRaySource();
        private static readonly IPlacementRaySource MouseRay = new MouseRaySource();

        /// <summary>Ray source for the active mode, falling back to the player if the RTS camera is
        /// not up yet - a ray is needed every frame, and one frame of the wrong origin is far better
        /// than a null.</summary>
        public static IPlacementRaySource ActiveRaySource =>
            Current == EditorCameraMode.Rts && MouseRay.IsAvailable ? MouseRay : PlayerRay;

        /// <summary>Player pressed the camera key. Their choice now sticks for the session.</summary>
        public static void Cycle() {
            _playerChose = true;
            switch (Current) {
                case EditorCameraMode.Rts:         Set(EditorCameraMode.ThirdPerson); break;
                case EditorCameraMode.ThirdPerson: Set(EditorCameraMode.FirstPerson); break;
                default:                           Set(EditorCameraMode.Rts);         break;
            }
        }

        public static void Set(EditorCameraMode mode) {
            Current = mode;

            RtsCameraView.Instance?.SetActive(mode == EditorCameraMode.Rts);

            if (Mission.Current != null) {
                Mission.Current.CameraIsFirstPerson = mode == EditorCameraMode.FirstPerson;
            }

            switch (mode) {
                case EditorCameraMode.Rts:
                    EditorHud.ShowMessage(
                        "Camera: RTS. WASD pans, Space/Alt height, Shift+drag rotates, " +
                        "Shift+WASD flies. Placement follows the cursor.");
                    break;
                case EditorCameraMode.ThirdPerson:
                    EditorHud.ShowMessage("Camera: third person. Placement follows your aim.");
                    break;
                case EditorCameraMode.FirstPerson:
                    EditorHud.ShowMessage("Camera: first person. Placement follows your aim.");
                    break;
            }
        }

        /// <summary>
        /// Follows edit mode: RTS while editing, back to third person when editing is off. Does
        /// nothing once the player has chosen a camera themselves.
        ///
        /// This is what makes the default sensible without being bossy - RTS is the right camera for
        /// building and the wrong one for arriving, and the difference between those is exactly
        /// whether edit mode is on.
        /// </summary>
        public static void FollowEditMode(bool editing) {
            if (_playerChose) return;

            EditorCameraMode wanted = editing ? EditorCameraMode.Rts : EditorCameraMode.ThirdPerson;
            if (wanted != Current) Set(wanted);
        }

        /// <summary>
        /// Called when a mission starts so nothing leaks between sessions. Only the declared mode is
        /// reset here - the camera view for the new mission does not exist yet, and syncs itself to
        /// this on its first tick.
        /// </summary>
        public static void Reset() {
            Current = EditorCameraMode.ThirdPerson;
            _playerChose = false;
        }
    }
}
