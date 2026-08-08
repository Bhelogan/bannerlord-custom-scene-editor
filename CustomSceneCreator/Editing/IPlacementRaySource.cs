using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.Screens;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Where the "what am I pointing at" ray starts and which way it goes.
    ///
    /// The editor this was forked from read the player agent directly. That is correct for exactly
    /// one camera arrangement, and the editor is meant to support three (first person, third person,
    /// free fly), so placement asks this instead. Adding the seam up front costs a few lines;
    /// retrofitting it through the editor afterwards would mean touching every placement path.
    /// </summary>
    public interface IPlacementRaySource {
        bool IsAvailable { get; }
        Vec3 Origin { get; }
        Vec3 Direction { get; }
        /// <summary>How far in front of the origin an object may not be placed. First person needs a
        /// gap or the held object fills the screen; a free camera does not care.</summary>
        float MinimumDistance { get; }
    }

    /// <summary>
    /// Ray from the player's eyes along their aim.
    ///
    /// Covers first AND third person with no branching: <c>Agent.Main.LookDirection</c> is the aim
    /// direction, which the native camera already drives in both. Switching
    /// <c>Mission.CameraIsFirstPerson</c> moves the camera without changing where the player aims,
    /// so first-person building needs no placement code of its own.
    ///
    /// This is the original mod's mechanism, unchanged:
    /// Homesteads-main/MissionLogics/HomesteadSceneEditingMissionLogic.cs:55
    /// </summary>
    public class PlayerRaySource : IPlacementRaySource {
        public bool IsAvailable => Agent.Main != null && Agent.Main.IsActive();
        public Vec3 Origin => Agent.Main.GetEyeGlobalPosition();
        public Vec3 Direction => Agent.Main.LookDirection;

        // In first person the camera sits at the eyes, so an object placed at arm's length covers the
        // view. In third person the camera is behind, so a short minimum reads fine.
        public float MinimumDistance => Mission.Current != null && Mission.Current.CameraIsFirstPerson ? 1.5f : 0.5f;
    }

    /// <summary>
    /// Ray from the free camera along its forward axis.
    ///
    /// <c>MissionScreen.CombatCamera.Frame</c> is the camera's world frame; its rotation's forward
    /// vector is where it looks. The existing free-camera view already reads exactly this field.
    /// </summary>
    public class FreeCameraRaySource : IPlacementRaySource {
        private readonly MissionScreen _missionScreen;

        public FreeCameraRaySource(MissionScreen missionScreen) {
            _missionScreen = missionScreen;
        }

        public bool IsAvailable => _missionScreen?.CombatCamera != null;
        public Vec3 Origin => _missionScreen.CombatCamera.Frame.origin;
        public Vec3 Direction => _missionScreen.CombatCamera.Frame.rotation.f;
        public float MinimumDistance => 0.5f;
    }
}
