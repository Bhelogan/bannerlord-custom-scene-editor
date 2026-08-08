using System;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.Screens;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// RTS-style overhead free camera, and the mouse-to-world ray that goes with it.
    ///
    /// This is the mode most building actually gets done in: you look down at the site, pan around
    /// it, and place where the CURSOR is rather than where the character happens to be facing. That
    /// cursor ray is the real difference from the player-attached modes - not the camera position.
    ///
    /// Ported from Homesteads Reloaded's shipped free camera. Three details in it are load-bearing
    /// and were arrived at the hard way, so they are preserved rather than reinvented:
    ///
    ///   1. The frame must be committed to THREE places. CombatCamera.Frame alone updates the camera
    ///      object; Mission.SetCameraFrame pushes it to the simulation; SceneView.SetCamera pushes it
    ///      to the renderer. Without the third the viewport keeps drawing the agent-follow camera.
    ///   2. Detaching from the agent needs BOTH the LastFollowedAgent property and
    ///      LastFollowedAgentVisuals cleared, and the pending bearing/elevation deltas zeroed, or the
    ///      camera snaps or spins on entry.
    ///   3. The rotation basis is RotateAboutSide(pi/2), then bearing about forward, then elevation
    ///      about side. Any other order gives a camera that rolls as it turns.
    ///
    /// MissionScreen keeps bearing, elevation and the followed agent private, so those are reached by
    /// reflection. Each lookup is null-checked rather than assumed: a rename in a game update should
    /// degrade the camera, not throw on every frame.
    /// </summary>
    public class RtsCameraView : MissionView {
        public static RtsCameraView? Instance { get; private set; }

        private bool _isActive;
        private bool _needsAgentFreeze;

        private Vec3 _cameraPosition;
        private float _cameraBearing;
        private float _cameraElevation = -0.65f;   // ~37 degrees down: a usable overview angle

        // Shift acts as both "rotate the camera" (with mouse movement) and "fly along the view
        // direction" (with WASD). It only becomes a rotation once the mouse actually moves, so a
        // Shift+W fly does not also spin the camera.
        private bool _shiftHeld;
        private float _shiftMouseAccum;
        private bool _shiftWasDrag;
        private const float ShiftDragThreshold = 4f;

        private Vec3 _mouseRayBegin = Vec3.Invalid;
        private Vec3 _mouseRayEnd = Vec3.Invalid;

        // -- reflection into MissionScreen -------------------------------------------------------
        private static readonly MethodInfo? SetCameraBearing =
            typeof(MissionScreen).GetProperty("CameraBearing")?.GetSetMethod(true);
        private static readonly MethodInfo? SetCameraElevation =
            typeof(MissionScreen).GetProperty("CameraElevation")?.GetSetMethod(true);
        private static readonly FieldInfo? BearingDeltaField =
            typeof(MissionScreen).GetField("_cameraBearingDelta", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? ElevationDeltaField =
            typeof(MissionScreen).GetField("_cameraElevationDelta", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo? SetLastFollowedAgent =
            typeof(MissionScreen).GetProperty("LastFollowedAgent")?.GetSetMethod(true);

        public bool IsActive => _isActive;

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;
        }

        public override void OnMissionScreenFinalize() {
            base.OnMissionScreenFinalize();
            if (_isActive) SetActive(false);
            if (Instance == this) Instance = null;
        }

        // -- activation -------------------------------------------------------------------------

        public void SetActive(bool active) {
            if (_isActive == active) return;
            _isActive = active;

            try {
                if (active) {
                    // Start from wherever the camera already is, lifted to a useful height, so the
                    // transition does not teleport the view somewhere unrecognisable.
                    _cameraPosition = MissionScreen.CombatCamera.Frame.origin;
                    _cameraBearing = MissionScreen.CameraBearing;
                    _cameraElevation = -0.65f;

                    float groundZ = Mission.Scene.GetGroundHeightAtPosition(_cameraPosition);
                    if (groundZ < 9999f) _cameraPosition.z = groundZ + 20f;

                    // Pending deltas would be applied on top of our frame and spin it on entry.
                    BearingDeltaField?.SetValue(MissionScreen, 0f);
                    ElevationDeltaField?.SetValue(MissionScreen, 0f);

                    SetLastFollowedAgent?.Invoke(MissionScreen, new object?[] { null });
                    TryClearFollowedAgentVisuals();

                    _shiftHeld = false;
                    _shiftMouseAccum = 0f;
                    _shiftWasDrag = false;

                    ShowCursor(true);

                    // The player must stop responding to WASD, or the character walks while the
                    // camera pans. Handing them to the AI controller is enough - they simply stand.
                    if (Agent.Main != null) Agent.Main.Controller = AgentControllerType.AI;
                    else _needsAgentFreeze = true;

                } else {
                    _needsAgentFreeze = false;
                    if (Agent.Main != null) Agent.Main.Controller = AgentControllerType.Player;

                    ShowCursor(false);

                    // Hand our orientation back, or the native camera jumps on the next frame.
                    SetCameraBearing?.Invoke(MissionScreen, new object[] { _cameraBearing });
                    SetCameraElevation?.Invoke(MissionScreen, new object[] { _cameraElevation });

                    _mouseRayBegin = Vec3.Invalid;
                    _mouseRayEnd = Vec3.Invalid;
                }
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(RtsCameraView), $"SetActive({active}) failed", ex);
            }
        }

        private void ShowCursor(bool visible) {
            try {
                MissionScreen.MouseVisible = visible;
                MouseManager.ShowCursor(visible);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(RtsCameraView), $"Cursor toggle failed: {ex.Message}");
            }
        }

        private void TryClearFollowedAgentVisuals() {
            // Name has moved between versions; clearing it is an optimisation for a clean handover,
            // not a requirement, so a miss is logged once and ignored.
            try {
                PropertyInfo? prop = typeof(MissionScreen).GetProperty("LastFollowedAgentVisuals");
                prop?.SetValue(MissionScreen, null);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(RtsCameraView),
                    $"Could not clear LastFollowedAgentVisuals: {ex.Message}");
            }
        }

        // -- camera override --------------------------------------------------------------------

        public override bool UpdateOverridenCamera(float dt) {
            if (!_isActive) return base.UpdateOverridenCamera(dt);
            try {
                UpdateFrame(dt);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(RtsCameraView), "UpdateFrame failed", ex);
            }
            return true;
        }

        private void UpdateFrame(float dt) {
            if (_shiftHeld && _shiftWasDrag) HandleRotate();

            MatrixFrame frame = MatrixFrame.Identity;
            frame.rotation.RotateAboutSide(MathF.PI / 2f);
            frame.rotation.RotateAboutForward(_cameraBearing);
            frame.rotation.RotateAboutSide(_cameraElevation);
            frame.origin = _cameraPosition;

            HandleMove(dt, ref frame);
            ClampToTerrain(ref frame);
            _cameraPosition = frame.origin;

            MissionScreen.CombatCamera.Frame = frame;
            Mission.SetCameraFrame(ref frame, 1f);
            MissionScreen.SceneView?.SetCamera(MissionScreen.CombatCamera);

            SetCameraBearing?.Invoke(MissionScreen, new object[] { _cameraBearing });
            SetCameraElevation?.Invoke(MissionScreen, new object[] { _cameraElevation });
        }

        private void HandleRotate() {
            float sensitivity = MissionScreen.SceneLayer.Input.GetMouseSensitivity();
            float scale = 5.4e-5f * sensitivity * MissionScreen.CameraViewAngle;

            _cameraBearing -= MissionScreen.SceneLayer.Input.GetMouseMoveX() * scale;
            _cameraElevation += (NativeConfig.InvertMouse ? 1f : -1f)
                                * MissionScreen.SceneLayer.Input.GetMouseMoveY() * scale;
            // Upper bound stops the camera flipping over the top; lower bound is roughly straight down.
            _cameraElevation = MBMath.ClampFloat(_cameraElevation, -1.36591f, 0.5f);
        }

        private void HandleMove(float dt, ref MatrixFrame frame) {
            // Pan speed scales with height, so the same input feels right whether you are inspecting
            // a doorway or laying out a village.
            float groundZ = Mission.Scene.GetGroundHeightAtPosition(frame.origin);
            float heightAboveGround = frame.origin.z - (groundZ < 9999f ? groundZ : frame.origin.z - 5f);
            float speed = 3f * MathF.Clamp(1f + heightAboveGround / 2f, 1f, 30f);

            Vec3 forward3D = (-frame.rotation.u).NormalizedCopy();
            Vec3 rightXY = frame.rotation.s.AsVec2.ToVec3().NormalizedCopy();

            // Flat forward: the view direction projected onto the ground plane. Panning with a tilted
            // camera should not change height, or you sink toward the ground as you travel.
            float flatLength = MathF.Sqrt(forward3D.x * forward3D.x + forward3D.y * forward3D.y);
            Vec3 forwardXY = flatLength > 0.001f
                ? new Vec3(forward3D.x / flatLength, forward3D.y / flatLength, 0f)
                : forward3D;

            bool fly = Input.IsKeyDown(InputKey.LeftShift);
            Vec3 moveForward = fly ? forward3D : forwardXY;

            if (Input.IsKeyDown(InputKey.W)) frame.origin += moveForward * speed * dt;
            if (Input.IsKeyDown(InputKey.S)) frame.origin -= moveForward * speed * dt;
            if (Input.IsKeyDown(InputKey.A)) frame.origin -= rightXY * speed * dt;
            if (Input.IsKeyDown(InputKey.D)) frame.origin += rightXY * speed * dt;

            if (Input.IsKeyDown(InputKey.Space)) frame.origin.z += speed * dt;
            if (Input.IsKeyDown(InputKey.LeftAlt)) frame.origin.z -= speed * dt;
        }

        private void ClampToTerrain(ref MatrixFrame frame) {
            // Sampled from well above so the probe does not start underground and return a floor
            // below the one we care about.
            float groundZ = Mission.Scene.GetGroundHeightAtPosition(frame.origin + new Vec3(0f, 0f, 100f));
            if (groundZ < 9999f) frame.origin.z = MathF.Max(frame.origin.z, groundZ + 1.5f);
        }

        // -- per-frame --------------------------------------------------------------------------

        public override void OnMissionScreenTick(float dt) {
            base.OnMissionScreenTick(dt);
            if (!_isActive) return;

            try {
                if (_needsAgentFreeze && Agent.Main != null) {
                    Agent.Main.Controller = AgentControllerType.AI;
                    _needsAgentFreeze = false;
                }

                TrackShiftDrag();
                RefreshMouseRay();
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(RtsCameraView), "Tick failed", ex);
            }
        }

        /// <summary>
        /// Shift is only a camera rotation once the mouse has moved past a small threshold. Without
        /// that, holding Shift to fly forward would also count as a rotate and freeze the placement
        /// ray for no reason.
        /// </summary>
        private void TrackShiftDrag() {
            bool shiftNow = MissionScreen.SceneLayer.Input.IsKeyDown(InputKey.LeftShift);

            if (shiftNow && !_shiftHeld) {
                _shiftHeld = true;
                _shiftMouseAccum = 0f;
                _shiftWasDrag = false;
            } else if (!shiftNow && _shiftHeld) {
                _shiftHeld = false;
                _shiftWasDrag = false;
            }

            if (_shiftHeld && !_shiftWasDrag) {
                _shiftMouseAccum += MathF.Abs(MissionScreen.SceneLayer.Input.GetMouseMoveX())
                                  + MathF.Abs(MissionScreen.SceneLayer.Input.GetMouseMoveY());
                if (_shiftMouseAccum > ShiftDragThreshold) _shiftWasDrag = true;
            }
        }

        // -- mouse ray --------------------------------------------------------------------------

        /// <summary>
        /// The cursor's ray into the world. This is what makes the mode RTS rather than just
        /// overhead: placement follows the pointer, not the camera's centre.
        /// </summary>
        public bool TryGetMouseRay(out Vec3 begin, out Vec3 end) {
            begin = _mouseRayBegin;
            end = _mouseRayEnd;
            return _isActive && _mouseRayBegin.IsValid;
        }

        /// <summary>
        /// True while the ray is deliberately held still: mid camera-rotation drag, or with the right
        /// button held for object rotation. In both cases the preview should stay where it is instead
        /// of chasing a cursor the user is not aiming with.
        /// </summary>
        public bool IsFreezingRay =>
            _isActive && ((_shiftHeld && _shiftWasDrag)
                          || (MissionScreen?.SceneLayer?.Input?.IsKeyDown(InputKey.RightMouseButton) ?? false));

        /// <summary>
        /// Reads a key from the SCENE layer rather than global input. Gauntlet panels consume mouse
        /// buttons on the global path before mission logic sees them, so a click meant for the world
        /// is silently dropped when the cursor happens to be over the HUD.
        /// </summary>
        public bool IsKeyPressedOnScene(InputKey key) =>
            _isActive
            && !(_shiftHeld && _shiftWasDrag)
            && (MissionScreen?.SceneLayer?.Input?.IsKeyPressed(key) ?? false);

        private void RefreshMouseRay() {
            if (IsFreezingRay) return;   // keep the last valid ray

            _mouseRayBegin = Vec3.Invalid;
            _mouseRayEnd = Vec3.Invalid;

            if (MissionScreen?.SceneLayer == null) return;

            try {
                Vec2 mouse = MissionScreen.SceneLayer.Input.GetMousePositionRanged();
                MissionScreen.ScreenPointToWorldRay(mouse, out Vec3 begin, out Vec3 end);
                if (begin.IsValid && (end - begin).LengthSquared > 0.001f) {
                    _mouseRayBegin = begin;
                    _mouseRayEnd = end;
                    return;
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(RtsCameraView), $"ScreenPointToWorldRay failed: {ex.Message}");
            }

            // Fall back to a centre-screen ray so placement still works if the screen-point
            // conversion is unavailable.
            try {
                Vec3 camPos = MissionScreen.CombatCamera.Position;
                Vec3 camDir = MissionScreen.CombatCamera.Direction.NormalizedCopy();
                if (camDir.IsValid && camDir.LengthSquared > 0.001f) {
                    _mouseRayBegin = camPos;
                    _mouseRayEnd = camPos + camDir * 1000f;
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(RtsCameraView), $"Centre-ray fallback failed: {ex.Message}");
            }
        }
    }

    /// <summary>Placement ray taken from the RTS camera's cursor.</summary>
    public class MouseRaySource : IPlacementRaySource {
        public bool IsAvailable =>
            RtsCameraView.Instance != null
            && RtsCameraView.Instance.TryGetMouseRay(out _, out _);

        public Vec3 Origin {
            get {
                RtsCameraView.Instance!.TryGetMouseRay(out Vec3 begin, out _);
                return begin;
            }
        }

        public Vec3 Direction {
            get {
                RtsCameraView.Instance!.TryGetMouseRay(out Vec3 begin, out Vec3 end);
                return (end - begin).NormalizedCopy();
            }
        }

        public float MinimumDistance => 0.5f;
    }
}
