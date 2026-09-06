using System;
using System.Collections.Generic;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.UI.PrefabPreview {
    /// <summary>
    /// Owns the small, private scene used by the asset picker's preview.  It deliberately never
    /// touches the editor mission: the chosen prefab is inert, has no physics, and cannot run into
    /// the authoring scene's scripts or collision.
    /// </summary>
    internal sealed class CSCPrefabPreviewScene : IDisposable {
        // This stock scene supplies the camera script SceneTableau requires.  Its ordinary scenery
        // is hidden below; nothing from it is shipped with CSC.
        private const string BaseScene = "scn_born_baby";
        private const string CameraTag = "customcamera";
        private const float MinDistance = 1f;
        private const float MaxDistance = 90f;
        private const float FitFactor = 1.9f;
        private const float BoxAspect = 2.2f;
        private static readonly Vec3 WorldUp = new Vec3(0f, 0f, 1f);

        private Scene? _scene;
        private GameEntity? _subject;
        private GameEntity? _lightHolder;
        private Vec3 _centre;
        private float _floor;
        private float _size;
        private float _distance = 8f;
        private float _yaw;
        private float _pitch;
        private float _zoom = 1f;

        public object? Scene => _scene;

        public bool EnsureScene() => _scene != null || BuildScene();

        public bool Show(string prefabName) {
            if (string.IsNullOrWhiteSpace(prefabName) || !EnsureScene()) return false;
            try {
                ClearSubject();
                if (!GameEntity.PrefabExists(prefabName)) return false;

                _subject = GameEntity.Instantiate(_scene!, prefabName, MatrixFrame.Identity);
                if (_subject == null) return false;
                _subject.SetPhysicsState(false, true);
                _subject.SetVisibilityExcludeParents(true);

                Vec3 min = _subject.GlobalBoxMin;
                Vec3 max = _subject.GlobalBoxMax;
                Vec3 dimensions = max - min;
                if (!min.IsValid || !max.IsValid || dimensions.LengthSquared < 0.0001f) {
                    _centre = Vec3.Zero;
                    _floor = 0f;
                    _size = 4f;
                    _distance = 8f;
                } else {
                    _centre = (min + max) * 0.5f;
                    _floor = min.z;
                    _size = Math.Max(dimensions.z, Math.Max(dimensions.x, dimensions.y));
                    float limiting = Math.Max(dimensions.z, Math.Max(dimensions.x, dimensions.y) / BoxAspect);
                    _distance = MathF.Clamp(limiting * FitFactor, MinDistance, MaxDistance);
                }

                _yaw = 0f;
                _pitch = 0f;
                _zoom = 1f;
                Apply();
                return true;
            } catch {
                ClearSubject();
                return false;
            }
        }

        public void SetYaw(float value) { _yaw = value; Apply(); }
        public void SetPitch(float value) { _pitch = MathF.Clamp(value, -80f, 80f); Apply(); }
        public void SetZoom(float value) { _zoom = MathF.Clamp(value, 0.35f, 3f); Apply(); }

        public void Tick(float dt) {
            try {
                _scene?.WaitWaterRendererCPUSimulation();
                _scene?.Tick(dt);
            } catch { }
        }

        private bool BuildScene() {
            try {
                Scene scene = TaleWorlds.Engine.Scene.CreateNewScene(true, true, DecalAtlasGroup.Battle);
                scene.SetUsesDeleteLaterSystem(true);
                var init = new SceneInitializationData(true) { InitPhysicsWorld = false };
                scene.Read(BaseScene, ref init);
                scene.SetAtmosphereWithName("character_menu_a");
                scene.SetShadow(true);
                scene.SetClothSimulationState(false);
                scene.DisableStaticShadows(true);

                GameEntity? camera = scene.FindEntityWithTag(CameraTag);
                if (camera == null) {
                    scene.ClearAll();
                    scene.ManualInvalidate();
                    return false;
                }

                // Keep the borrowed camera and its lights; hide only the scene's visible props.
                var entities = new List<GameEntity>();
                scene.GetEntities(ref entities);
                foreach (GameEntity entity in entities) {
                    if (entity == null || entity == camera || entity.HasTag(CameraTag)) continue;
                    try {
                        if (entity.GetLight() == null) entity.SetVisibilityExcludeParents(false);
                    } catch { }
                }

                _scene = scene;
                CreateKeyLight(camera);
                return true;
            } catch { return false; }
        }

        private void CreateKeyLight(GameEntity camera) {
            try {
                _lightHolder = GameEntity.CreateEmpty(_scene!);
                Light? light = Light.CreatePointLight(40f);
                if (_lightHolder == null || light == null) return;
                light.Intensity = 300f;
                light.LightColor = new Vec3(1f, 0.96f, 0.88f);
                light.SetShadowType(Light.ShadowType.NoShadow);
                _lightHolder.AddLight(light);
                MatrixFrame frame = MatrixFrame.Identity;
                frame.origin = camera.GetGlobalFrame().origin + new Vec3(0f, 0f, 2.5f);
                _lightHolder.SetGlobalFrame(frame);
            } catch { _lightHolder = null; }
        }

        private void Apply() {
            GameEntity? camera = _scene?.FindEntityWithTag(CameraTag);
            if (camera == null || _subject == null) return;

            MatrixFrame cameraFrame = camera.GetGlobalFrame();
            Vec3 forward = cameraFrame.rotation.f;
            forward.z = 0f;
            if (forward.LengthSquared < 0.0001f) forward = new Vec3(0f, 1f, 0f);
            forward.Normalize();

            float distance = MathF.Clamp(_distance / _zoom, MinDistance, MaxDistance);
            Vec3 desiredCentre = cameraFrame.origin + forward * distance;
            Mat3 rotation = Mat3.Identity;
            rotation.RotateAboutUp(_yaw * (MathF.PI / 180f));
            Vec3 right = Vec3.CrossProduct(forward, WorldUp);
            if (right.LengthSquared > 0.0001f) {
                right.Normalize();
                rotation.RotateAboutAnArbitraryVector(in right, _pitch * (MathF.PI / 180f));
            }

            Vec3 pivot = new Vec3(_centre.x, _centre.y, _floor);
            float halfHeight = _centre.z - _floor;
            Vec3 desiredFloor = desiredCentre - new Vec3(0f, 0f, halfHeight);
            MatrixFrame frame = MatrixFrame.Identity;
            frame.rotation = rotation;
            frame.origin = desiredFloor - rotation.TransformToParent(pivot);
            _subject.SetGlobalFrame(frame);

            if (_lightHolder?.GetLight() is Light light) {
                float reach = Math.Max(12f, Math.Max(_size, distance) * 2.5f);
                light.Radius = reach;
                light.Intensity = 300f * reach / 40f;
                Vec3 towardCamera = cameraFrame.origin - desiredCentre;
                if (towardCamera.LengthSquared > 0.0001f) towardCamera.Normalize();
                MatrixFrame lightFrame = MatrixFrame.Identity;
                lightFrame.origin = desiredCentre + towardCamera * Math.Max(_size, 4f) * 0.6f
                    + new Vec3(0f, 0f, Math.Max(_size, 3f) * 0.8f);
                _lightHolder.SetGlobalFrame(lightFrame);
            }
        }

        private void ClearSubject() {
            try { _subject?.Remove(0); } catch { }
            _subject = null;
        }

        public void Dispose() {
            Scene? scene = _scene;
            _scene = null;
            _subject = null;
            _lightHolder = null;
            if (scene == null) return;

            // NOT ClearAll() here. By the time Dispose runs, the tableau has already called
            // View.AddClearTask() - the SceneView is queued for teardown and still points at this
            // scene. Freeing it in the same frame corrupts the native heap and kills the process
            // later, silently. See PreviewSceneRetirement for the full account.
            PreviewSceneRetirement.Retire(scene);
        }
    }
}
