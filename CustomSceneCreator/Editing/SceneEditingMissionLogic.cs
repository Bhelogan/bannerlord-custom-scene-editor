using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
using TaleWorlds.Engine;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    public enum EditMode {
        Off = 0,
        Build = 1,
        Delete = 2,
        Move = 3,
    }

    /// <summary>
    /// The editor.
    ///
    /// Forked from the ORIGINAL Homesteads editor (Homesteads-main, 330 lines) rather than the
    /// shipped one (1,845 lines). Same mechanism, before free-build mode, tier caches, template mode
    /// and controller bindings accumulated on top of it - and it is the version that already did
    /// first-person place-where-you-look, which is what we want back.
    ///
    /// Three things changed in the fork:
    ///   - persistence goes through <see cref="ISceneEditTarget"/> instead of a HomesteadScene
    ///   - the palette comes from <see cref="IPlaceableProvider"/> instead of a tier group
    ///   - the placement ray comes from <see cref="IPlacementRaySource"/> instead of the player agent,
    ///     so the same code serves first person, third person and (later) a free camera
    ///
    /// Everything about build points, item costs and tier gating is gone. A scene editor places
    /// things because you want them there.
    /// </summary>
    public class SceneEditingMissionLogic : MissionLogic {
        private readonly ISceneEditTarget _target;
        private readonly IPlaceableProvider _provider;

        // Always read from CameraModes rather than caching: the ray source changes the moment the
        // camera mode does, and a stale one silently places from the wrong place.
        private IPlacementRaySource RaySource => CameraModes.ActiveRaySource;

        private EditMode _mode = EditMode.Off;

        /// <summary>
        /// How far the placement ray reaches. Deliberately long: the RTS camera is routinely a few
        /// hundred metres from the site, and the old 30m cap meant the preview simply vanished at
        /// any useful overview height. Range is not a game rule here - if you can see it, you can
        /// build on it.
        /// </summary>
        private const float MaxPlaceDistance = 2000f;

        // What the ray is currently hitting.
        private Vec3 _positionLookingAt = Vec3.Invalid;
        // 1.4.7 yields a WeakGameEntity from the raycast, not a GameEntity. Kept in that form
        // and compared by pointer, since GameEntity exposes .WeakEntity to bridge across.
        private WeakGameEntity _entityLookingAt = WeakGameEntity.Invalid;

        // The translucent preview of the thing about to be placed.
        private GameEntity? _ghost;
        private Mat3 _ghostRotation = Mat3.Identity;
        private Vec3 _ghostOffset = Vec3.Zero;

        /// <summary>
        /// When true the preview sits on whatever the ray hits, so it walks up and down terrain as
        /// the cursor moves. When false its height is pinned, which is what you want for a row of
        /// windows, a floating walkway, or anything that must stay level across uneven ground.
        /// </summary>
        private bool _groundFollow = true;
        private float _lockedHeight;

        // Palette state.
        private List<string> _categories = new();
        private int _categoryIndex;
        private List<Placeable> _currentCategoryPlaceables = new();
        private int _placeableIndex;

        /// <summary>Set while an existing object has been picked up in Move mode, so placing it puts
        /// the same object down rather than adding a second one.</summary>
        private PlacedEntity? _carried;

        private readonly List<PlacedEntity> _live = new();

        public SceneEditingMissionLogic(ISceneEditTarget target, IPlaceableProvider provider) {
            _target = target;
            _provider = provider;
        }

        public EditMode Mode => _mode;
        public Placeable? CurrentPlaceable =>
            _currentCategoryPlaceables.Count > 0 && _placeableIndex < _currentCategoryPlaceables.Count
                ? _currentCategoryPlaceables[_placeableIndex]
                : null;
        public string CurrentCategory => _categories.Count > 0 ? _categories[_categoryIndex] : "";
        public int PlacedCount => _live.Count;

        public override void AfterStart() {
            base.AfterStart();
            try {
                BuildPalette();
                RestoreExistingEntities();
                EditorHud.ShowMessage(
                    $"Scene Creator ready. {Keys.Describe(Keys.EditMode)}: cycle edit modes. " +
                    $"{_live.Count} object(s) restored.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneEditingMissionLogic), "AfterStart failed", ex);
            }
        }

        private void BuildPalette() {
            List<Placeable> all = _provider.GetPlaceables().ToList();
            _categories = all.Select(p => p.Category).Distinct().OrderBy(c => c).ToList();
            if (_categories.Count == 0) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    "Palette is empty - no placeables. Check that the asset dump deployed.");
                return;
            }
            SelectCategory(0);
            TraceLogger.Write(nameof(SceneEditingMissionLogic),
                $"Palette: {all.Count} placeables across {_categories.Count} categories.");
        }

        private void SelectCategory(int index) {
            _categoryIndex = ((index % _categories.Count) + _categories.Count) % _categories.Count;
            string category = _categories[_categoryIndex];
            _currentCategoryPlaceables = _provider.GetPlaceables()
                .Where(p => p.Category == category)
                .OrderBy(p => p.DisplayName)
                .ToList();
            _placeableIndex = 0;
            RemoveGhost();
        }

        /// <summary>Re-instantiates everything the target already holds, so reopening a project shows
        /// what was built last time.</summary>
        private void RestoreExistingEntities() {
            foreach (PlacedEntity entity in _target.LoadEntities()) {
                GameEntity? spawned = Instantiate(entity.PrefabName, entity.Position, entity.Rotation);
                if (spawned == null) {
                    TraceLogger.Write(nameof(SceneEditingMissionLogic),
                        $"Could not restore '{entity.PrefabName}' - prefab missing. Left in the project.");
                    continue;
                }
                entity.SceneEntity = spawned;
                _live.Add(entity);
            }
        }

        public override void OnMissionTick(float dt) {
            base.OnMissionTick(dt);
            // Deliberately not gated on MainAgent being player-controlled: the RTS camera hands the
            // agent to the AI controller so WASD moves the camera instead of the character.
            if (Mission.MainAgent == null) return;

            try {
                UpdateLookTarget();
                HandleInput(dt);
                UpdateGhost();
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneEditingMissionLogic), "Tick failed", ex);
            }
        }

        private void UpdateLookTarget() {
            if (_mode == EditMode.Off) {
                _positionLookingAt = Vec3.Invalid;
                _entityLookingAt = WeakGameEntity.Invalid;
                return;
            }

            // While the RTS camera is holding the ray still - mid rotation drag, or right button
            // held to rotate an object - keep the last valid target. Recomputing here would chase a
            // cursor the user is not aiming with and jump the preview across the scene.
            if (RtsCameraView.Instance?.IsFreezingRay ?? false) return;

            IPlacementRaySource source = RaySource;
            if (!source.IsAvailable) {
                _positionLookingAt = Vec3.Invalid;
                _entityLookingAt = WeakGameEntity.Invalid;
                return;
            }

            Vec3 origin = source.Origin;
            Vec3 target = origin + source.Direction * MaxPlaceDistance;

            float distance = 0f;
            Mission.Scene.RayCastForClosestEntityOrTerrain(
                origin, target, out distance, out _positionLookingAt, out _entityLookingAt);

            // Only a near cut-off, and only because a first-person ghost placed at arm's length
            // fills the screen. There is deliberately no far limit.
            if (distance < source.MinimumDistance) {
                _positionLookingAt = Vec3.Invalid;
                _entityLookingAt = WeakGameEntity.Invalid;
            }
        }

        private void HandleInput(float dt) {
            if (Input.IsKeyPressed(Keys.EditMode)) { CycleEditMode(); return; }
            if (_mode == EditMode.Off) return;

            if (Input.IsKeyPressed(Keys.CameraMode)) { CameraModes.Cycle(); return; }

            // Left click is the natural place action with a visible cursor. Read through the scene
            // layer, since Gauntlet consumes mouse buttons on the global path first. F still works
            // everywhere, including the player-attached cameras where the cursor is captured.
            bool clickPlaced = CameraModes.Current == EditorCameraMode.Rts
                            && (RtsCameraView.Instance?.IsKeyPressedOnScene(Keys.Place) ?? false);

            if (clickPlaced || Input.IsKeyPressed(Keys.PlaceAlt)) { HandlePlaceKey(); return; }

            if (Input.IsKeyPressed(Keys.Save)) {
                _target.Commit();
                EditorHud.ShowMessage($"Saved. {_live.Count} object(s).");
                return;
            }

            if (Input.IsKeyPressed(Keys.ToggleGroundLock)) { ToggleGroundFollow(); return; }
            if (Input.IsKeyPressed(Keys.SnapToGround))     { SnapToGround();       return; }

            // Everything past here only means something with a ghost on screen.
            if (_ghost == null) return;

            if (Input.IsKeyPressed(Keys.ResetRotation)) {
                _ghostRotation = Mat3.Identity;
                _ghostOffset = Vec3.Zero;
                return;
            }

            // Right button held: drag horizontally to spin the object. Free rotation like this is
            // far quicker than tapping a key for every few degrees, and the RTS camera already
            // freezes the placement ray while the button is down so the preview holds still.
            float dragYaw = RtsCameraView.Instance?.GetRotateDragDelta() ?? 0f;
            if (MathF.Abs(dragYaw) > 0.0001f) {
                _ghostRotation.RotateAboutUp(-dragYaw * RotateDragSensitivity);
                return;
            }

            if (Input.IsKeyDown(Keys.RotateTiltUp))    { _ghostRotation.RotateAboutSide(dt);     return; }
            if (Input.IsKeyDown(Keys.RotateTiltDown))  { _ghostRotation.RotateAboutSide(-dt);    return; }
            if (Input.IsKeyDown(Keys.RotateRollLeft))  { _ghostRotation.RotateAboutForward(dt);  return; }
            if (Input.IsKeyDown(Keys.RotateRollRight)) { _ghostRotation.RotateAboutForward(-dt); return; }
            if (Input.IsKeyDown(Keys.RotateTurnLeft))  { _ghostRotation.RotateAboutUp(dt);       return; }
            if (Input.IsKeyDown(Keys.RotateTurnRight)) { _ghostRotation.RotateAboutUp(-dt);      return; }

            if (Input.IsKeyDown(Keys.MoveUp))   { _ghostOffset += Vec3.Up * dt; return; }
            if (Input.IsKeyDown(Keys.MoveDown)) { _ghostOffset -= Vec3.Up * dt; return; }

            if (_mode != EditMode.Build) return;

            if (Input.IsKeyPressed(Keys.NextCategory)) { SelectCategory(_categoryIndex + 1); AnnouncePlaceable(); return; }
            if (Input.IsKeyPressed(Keys.NextPlaceable)) { CyclePlaceable(1);  return; }
            if (Input.IsKeyPressed(Keys.PrevPlaceable)) { CyclePlaceable(-1); return; }
        }

        private void HandlePlaceKey() {
            switch (_mode) {
                case EditMode.Build:
                    if (_ghost != null) PlaceGhost();
                    break;

                case EditMode.Delete:
                    if (_entityLookingAt.IsValid) DeleteLookedAt();
                    break;

                case EditMode.Move:
                    if (_carried != null && _ghost != null) PlaceGhost();
                    else if (_entityLookingAt.IsValid) PickUpLookedAt();
                    break;
            }
        }

        private void PlaceGhost() {
            Placeable? placeable = _carried != null ? AssetCatalog.Find(_carried.PrefabName) : CurrentPlaceable;
            string prefabName = _carried?.PrefabName ?? placeable?.PrefabName ?? "";
            if (prefabName.Length == 0) return;

            Vec3 position = _ghost!.GlobalPosition;
            Mat3 rotation = _ghost.GetFrame().rotation;

            GameEntity? spawned = Instantiate(prefabName, position, rotation);
            if (spawned == null) {
                EditorHud.ShowMessage($"Could not place '{prefabName}'.", warning: true);
                return;
            }

            if (_carried != null) {
                // Same object, put back down: keep its identity so scripts referencing it by id
                // still point at the right thing.
                _carried.Position = position;
                _carried.Rotation = rotation;
                _carried.SceneEntity = spawned;
                _target.OnEntityAdded(_carried);
                _live.Add(_carried);
                _carried = null;
            } else {
                var placed = new PlacedEntity {
                    PrefabName = prefabName,
                    Position = position,
                    Rotation = rotation,
                    SceneEntity = spawned,
                };
                _target.OnEntityAdded(placed);
                _live.Add(placed);
            }

            RemoveGhost();
            EditorHud.ShowCount(_live.Count);
        }

        private void DeleteLookedAt() {
            PlacedEntity? owner = FindOwner(_entityLookingAt);
            if (owner == null) {
                // Part of the original scene, not something we placed. Deleting shipped scene
                // geometry is a separate feature with its own persistence problem: it would have to
                // be recorded as a removal, since the scene reloads intact next time.
                EditorHud.ShowMessage("That is part of the original scene - only placed objects can be deleted.", warning: true);
                return;
            }

            DestroyEntity(owner.SceneEntity);
            owner.SceneEntity = null;
            _live.Remove(owner);
            _target.OnEntityRemoved(owner);
            EditorHud.ShowCount(_live.Count);
        }

        private void PickUpLookedAt() {
            PlacedEntity? owner = FindOwner(_entityLookingAt);
            if (owner == null) {
                EditorHud.ShowMessage("That is part of the original scene - only placed objects can be moved.", warning: true);
                return;
            }

            DestroyEntity(owner.SceneEntity);
            owner.SceneEntity = null;
            _live.Remove(owner);
            _target.OnEntityRemoved(owner);

            _carried = owner;
            _ghostRotation = owner.Rotation;
            _ghostOffset = Vec3.Zero;
            RemoveGhost();
            EditorHud.ShowMessage($"Picked up {Placeable.ToDisplayName(owner.PrefabName)}. {Keys.Describe(Keys.Place)} to place.");
        }

        /// <summary>
        /// Maps a raycast hit back to the object we placed. The ray can land on any child of a
        /// prefab, so walk up until something matches.
        /// </summary>
        private PlacedEntity? FindOwner(WeakGameEntity hit) {
            WeakGameEntity current = hit;
            for (int depth = 0; current.IsValid && depth < 12; depth++) {
                UIntPtr probe = current.Pointer;
                PlacedEntity? match = _live.FirstOrDefault(
                    e => e.SceneEntity != null && e.SceneEntity.WeakEntity.Pointer == probe);
                if (match != null) return match;
                current = current.Parent;
            }
            return null;
        }

        /// <summary>Sensitivity of right-drag rotation, in radians per pixel of mouse movement.</summary>
        private const float RotateDragSensitivity = 0.01f;

        private void ToggleGroundFollow() {
            _groundFollow = !_groundFollow;
            if (!_groundFollow && _ghost != null) {
                // Pin at wherever the preview is right now, so toggling never makes it jump.
                _lockedHeight = _ghost.GlobalPosition.z;
            }
            EditorHud.ShowMessage(_groundFollow
                ? "Ground follow ON - objects sit on whatever is under the cursor."
                : $"Ground follow OFF - height pinned at {_lockedHeight:0.0}. " +
                  $"{Keys.Describe(Keys.SnapToGround)} to drop back to the ground.");
        }

        private void SnapToGround() {
            _ghostOffset = Vec3.Zero;
            _groundFollow = true;
            EditorHud.ShowMessage("Dropped to ground; ground follow ON.");
        }

        private void CyclePlaceable(int delta) {
            if (_currentCategoryPlaceables.Count == 0) return;
            _placeableIndex = ((_placeableIndex + delta) % _currentCategoryPlaceables.Count
                               + _currentCategoryPlaceables.Count) % _currentCategoryPlaceables.Count;
            RemoveGhost();
            AnnouncePlaceable();
        }

        private void AnnouncePlaceable() {
            Placeable? p = CurrentPlaceable;
            EditorHud.ShowSelection(CurrentCategory,
                p != null ? p.DisplayName : "(empty category)",
                _placeableIndex + 1, _currentCategoryPlaceables.Count);
        }

        private void CycleEditMode() {
            if (_carried != null) {
                EditorHud.ShowMessage("Place what you are carrying before switching mode.", warning: true);
                return;
            }

            _mode = (EditMode)(((int)_mode + 1) % 4);
            RemoveGhost();

            switch (_mode) {
                case EditMode.Off:
                    EditorHud.ShowMessage("Editing off.");
                    break;
                case EditMode.Build:
                    EditorHud.ShowMessage(
                        $"Build mode. {Keys.Describe(Keys.Place)} (or {Keys.Describe(Keys.PlaceAlt)}): place. " +
                        $"{Keys.Describe(Keys.RotateTurnLeft)}/{Keys.Describe(Keys.RotateTurnRight)} or hold " +
                        $"{Keys.Describe(Keys.RotateDrag)}: rotate. " +
                        $"{Keys.Describe(Keys.SnapToGround)}: drop to ground. " +
                        $"{Keys.Describe(Keys.ToggleGroundLock)}: ground follow. " +
                        $"{Keys.Describe(Keys.PrevPlaceable)}/{Keys.Describe(Keys.NextPlaceable)}: cycle. " +
                        $"{Keys.Describe(Keys.NextCategory)}: category. " +
                        $"{Keys.Describe(Keys.CameraMode)}: camera. {Keys.Describe(Keys.Save)}: save.");
                    AnnouncePlaceable();
                    break;
                case EditMode.Delete:
                    EditorHud.ShowMessage($"Delete mode. {Keys.Describe(Keys.Place)}: delete what you are looking at.");
                    break;
                case EditMode.Move:
                    EditorHud.ShowMessage($"Move mode. {Keys.Describe(Keys.Place)}: pick up / put down.");
                    break;
            }
        }

        // -- ghost ------------------------------------------------------------------------------

        private void UpdateGhost() {
            bool wantGhost = (_mode == EditMode.Build && CurrentPlaceable != null)
                          || (_mode == EditMode.Move && _carried != null);

            if (!wantGhost || !_positionLookingAt.IsValid) {
                RemoveGhost();
                return;
            }

            if (_ghost == null) {
                string prefabName = _carried?.PrefabName ?? CurrentPlaceable!.PrefabName;
                _ghost = Instantiate(prefabName, _positionLookingAt, _ghostRotation);
                if (_ghost == null) return;

                // No physics on the preview, or it collides with the world and with the player while
                // it is still only a suggestion.
                foreach (GameEntity part in _ghost.GetEntityAndChildren()) {
                    part.SetPhysicsState(false, true);
                }
                TintGhost();
            }

            Vec3 ghostPosition = _positionLookingAt + _ghostOffset;
            if (!_groundFollow) ghostPosition.z = _lockedHeight + _ghostOffset.z;
            _ghost.SetLocalPosition(ghostPosition);
            MatrixFrame frame = _ghost.GetFrame();
            frame.rotation = _ghostRotation;
            _ghost.SetFrame(ref frame);
        }

        private void TintGhost() {
            if (_ghost == null) return;
            foreach (GameEntity part in _ghost.GetEntityAndChildren()) {
                MetaMesh? mesh = part.GetMetaMesh(0);
                if (mesh == null) continue;
                for (int i = 0; i < mesh.MeshCount; i++) {
                    // Green for a new object, blue for one being carried, so it is obvious at a
                    // glance whether placing will add or move.
                    mesh.GetMeshAtIndex(i).SetMaterial(_carried != null ? "plain_blue" : "plain_green");
                }
            }
        }

        private void RemoveGhost() {
            if (_ghost == null) return;
            DestroyEntity(_ghost);
            _ghost = null;
        }

        // -- scene helpers ----------------------------------------------------------------------

        private GameEntity? Instantiate(string prefabName, Vec3 position, Mat3 rotation) {
            try {
                if (!GameEntity.PrefabExists(prefabName)) return null;
                MatrixFrame frame = MatrixFrame.Identity;
                frame.rotation = rotation;
                GameEntity entity = GameEntity.Instantiate(Mission.Scene, prefabName, frame);
                entity.SetLocalPosition(position);
                return entity;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    $"Instantiate('{prefabName}') failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void DestroyEntity(GameEntity? entity) {
            if (entity == null) return;
            try {
                entity.RemoveAllChildren();
                entity.Remove(0);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    $"Remove failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        protected override void OnEndMission() {
            base.OnEndMission();
            RemoveGhost();
            try {
                _target.Commit();
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneEditingMissionLogic), "Commit on exit failed", ex);
            }
        }
    }
}
