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
        //
        // _cycleSet is what [ and ] walk. It is normally the current category, but choosing from the
        // asset picker replaces it with the picker's FILTERED results - so after searching "cart" and
        // building one, the cycle keys step through the other carts instead of dumping you back into
        // all 6,400 prefabs.
        private List<string> _categories = new();
        private int _categoryIndex;
        private List<Placeable> _cycleSet = new();
        private string _cycleLabel = "";
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
            _cycleSet.Count > 0 && _placeableIndex < _cycleSet.Count
                ? _cycleSet[_placeableIndex]
                : null;
        public string CurrentCategory => _cycleLabel;
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
            _cycleSet = _provider.GetPlaceables()
                .Where(p => p.Category == category)
                .OrderBy(p => p.DisplayName)
                .ToList();
            _cycleLabel = category;
            _placeableIndex = 0;
            RemoveGhost();
        }

        /// <summary>Re-instantiates everything the target already holds, so reopening a project shows
        /// what was built last time.</summary>
        private void RestoreExistingEntities() {
            foreach (PlacedEntity entity in _target.LoadEntities()) {
                GameEntity? spawned = Instantiate(entity.PrefabName, entity.Position, entity.Rotation, enablePhysics: true);
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
                UpdateStatus();
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
            // Nothing else may act while the picker owns input: it pauses the engine and takes focus,
            // so a stray keypress reaching here would edit the scene behind a modal panel.
            if (UI.AssetPickerView.IsOpen) return;

            if (Input.IsKeyPressed(Keys.EditMode)) { CycleEditMode(); return; }
            if (_mode == EditMode.Off) return;

            if (Input.IsKeyPressed(Keys.AssetPicker)) { OpenAssetPicker(); return; }

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
                // Rotation and height offset only. Ground follow is left alone on purpose: G owns
                // that, and clearing it here would make one key quietly do two unrelated things.
                _ghostRotation = Mat3.Identity;
                _ghostOffset = Vec3.Zero;
                EditorHud.ShowMessage("Rotation and height offset reset.");
                return;
            }

            // Scroll wheel raises and lowers the held object. The most-reached adjustment after
            // rotation, and the wheel is otherwise unused while an object is held.
            float scroll = Input.DeltaMouseScroll;
            if (MathF.Abs(scroll) > 0.0001f) {
                _ghostOffset.z += scroll * ScrollHeightStep;
                return;
            }

            // Right button held: rock the object on CAMERA-relative axes.
            //
            // Rotating about the object's own axes is the obvious implementation and feels wrong in
            // use: once something is yawed, "drag up" tilts it in a direction that has nothing to do
            // with the screen. Rolling about the camera's horizontal forward vector and tilting about
            // its horizontal right vector means the drag always matches what you see, whatever the
            // object's current orientation.
            //
            // Yaw is deliberately absent here - that is Q/E. Duplicating it on the drag, which is
            // what this did before, wastes the gesture and leaves the other two axes unreachable
            // without the numpad.
            RtsCameraView? camera = RtsCameraView.Instance;
            if (camera != null && camera.IsRotateDragging) {
                float dragX = camera.SceneMouseMoveX;
                float dragY = camera.SceneMouseMoveY;

                if (MathF.Abs(dragX) > 0.0001f) {
                    Vec3 rollAxis = camera.CameraForwardHorizontal;
                    _ghostRotation.RotateAboutAnArbitraryVector(in rollAxis, dragX * RotateDragSensitivity);
                }
                if (MathF.Abs(dragY) > 0.0001f) {
                    Vec3 tiltAxis = camera.CameraRightHorizontal;
                    _ghostRotation.RotateAboutAnArbitraryVector(in tiltAxis, dragY * RotateDragSensitivity);
                }
                // No early return: placing on the same frame as a drag should still work.
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
            Placeable? placeable = _carried != null ? PlaceableRegistry.Find(_carried.PrefabName) : CurrentPlaceable;
            string prefabName = _carried?.PrefabName ?? placeable?.PrefabName ?? "";
            if (prefabName.Length == 0) return;

            Vec3 position = _ghost!.GlobalPosition;
            Mat3 rotation = _ghost.GetFrame().rotation;

            GameEntity? spawned = Instantiate(prefabName, position, rotation, enablePhysics: true);
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

            // If a hit resolves to nothing we own, record what was hit - once per distinct name. If
            // the physics registration ever regresses, this line distinguishes "the ray is not
            // reaching our objects" from "the ray is landing on original scene geometry".
            try {
                string hitName = hit.IsValid ? (hit.Name ?? "?") : "(invalid)";
                if (_unownedHitsLogged.Add(hitName)) {
                    TraceLogger.Write(nameof(SceneEditingMissionLogic),
                        $"Hit '{hitName}' belongs to no placed object ({_live.Count} placed).");
                }
            } catch { }

            return null;
        }

        private readonly HashSet<string> _unownedHitsLogged = new();

        /// <summary>Radians per pixel of drag. Matches the shipped Homesteads builder.</summary>
        private const float RotateDragSensitivity = 0.005f;

        /// <summary>
        /// Metres per unit of scroll delta. DeltaMouseScroll reports roughly 120 per notch, not 1 -
        /// treating it as a notch count sent objects tens of metres underground on a single click.
        /// 0.003 gives about 0.36m a notch, matching the shipped Homesteads builder.
        /// </summary>
        private const float ScrollHeightStep = 0.003f;

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

        private void OpenAssetPicker() {
            UI.AssetPickerView? view = UI.AssetPickerView.Instance;
            if (view == null) {
                EditorHud.ShowMessage("Asset picker unavailable.", warning: true);
                return;
            }
            view.OnAssetChosen = ChooseFromPicker;
            view.Open(_provider.GetPlaceables());
        }

        /// <summary>
        /// Makes a picked asset the active one. The palette is per-category, so selecting something
        /// from another category has to move the category too - otherwise the cycle keys would
        /// immediately jump away from what was just chosen.
        /// </summary>
        private void ChooseFromPicker(Placeable placeable, IReadOnlyList<Placeable> filtered) {
            if (filtered != null && filtered.Count > 0) {
                _cycleSet = filtered.ToList();
                _cycleLabel = _cycleSet.Count == 1 ? placeable.Category : "Search results";
            } else {
                int categoryIndex = _categories.IndexOf(placeable.Category);
                if (categoryIndex >= 0) SelectCategory(categoryIndex);
            }

            int index = _cycleSet.FindIndex(
                p => string.Equals(p.PrefabName, placeable.PrefabName, StringComparison.OrdinalIgnoreCase));
            _placeableIndex = index >= 0 ? index : 0;

            // Picking an asset is a build intent; drop straight into build mode rather than making
            // the user also remember to switch.
            if (_mode != EditMode.Build) {
                _mode = EditMode.Build;
                CameraModes.FollowEditMode(true);
                EditorHud.ShowMessage("Build mode.");
            }

            RemoveGhost();
            AnnouncePlaceable();
        }

        private void CyclePlaceable(int delta) {
            if (_cycleSet.Count == 0) return;
            _placeableIndex = ((_placeableIndex + delta) % _cycleSet.Count
                               + _cycleSet.Count) % _cycleSet.Count;
            RemoveGhost();
            AnnouncePlaceable();
        }

        private void AnnouncePlaceable() {
            Placeable? p = CurrentPlaceable;
            EditorHud.ShowSelection(CurrentCategory,
                p != null ? p.DisplayName : "(nothing here)",
                _placeableIndex + 1, _cycleSet.Count);
        }

        private void CycleEditMode() {
            if (_carried != null) {
                EditorHud.ShowMessage("Place what you are carrying before switching mode.", warning: true);
                return;
            }

            _mode = (EditMode)(((int)_mode + 1) % 4);
            RemoveGhost();

            // The camera follows the edit mode unless the player has picked one themselves: RTS for
            // editing, third person for walking around. Turning editing on is the moment an overhead
            // view starts being useful, and turning it off is the moment it stops.
            CameraModes.FollowEditMode(_mode != EditMode.Off);

            switch (_mode) {
                case EditMode.Off:
                    EditorHud.ShowMessage("Editing off.");
                    break;
                case EditMode.Build:
                    EditorHud.ShowMessage(
                        $"Build mode. {Keys.Describe(Keys.Place)} (or {Keys.Describe(Keys.PlaceAlt)}): place. " +
                        $"{Keys.Describe(Keys.RotateTurnLeft)}/{Keys.Describe(Keys.RotateTurnRight)} or hold " +
                        $"{Keys.Describe(Keys.RotateDrag)}: rotate. " +
                        $"{Keys.Describe(Keys.ResetRotation)}: reset. " +
                        $"{Keys.Describe(Keys.SnapToGround)}: drop to ground. " +
                        $"{Keys.Describe(Keys.ToggleGroundLock)}: ground follow. " +
                        $"{Keys.Describe(Keys.PrevPlaceable)}/{Keys.Describe(Keys.NextPlaceable)}: cycle. " +
                        $"{Keys.Describe(Keys.NextCategory)}: category. " +
                        $"{Keys.Describe(Keys.AssetPicker)}: asset picker. " +
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

        /// <summary>
        /// Keeps the top-left readout answering "what happens if I click now".
        ///
        /// In Move this deliberately reports the CARRIED object rather than whatever is under the
        /// cursor: mid-move the cursor sweeps across other objects, and naming those would make the
        /// panel flicker through things you are not acting on.
        /// </summary>
        private void UpdateStatus() {
            UI.EditorStatusVM? status = UI.EditorStatusView.Instance?.DataSource;
            if (status == null) return;

            if (_mode == EditMode.Off) {
                status.IsVisible = false;
                return;
            }
            status.IsVisible = true;

            switch (_mode) {
                case EditMode.Build: {
                    Placeable? p = CurrentPlaceable;
                    status.Set("BUILD",
                        p?.DisplayName ?? "(nothing selected)",
                        p != null
                            ? $"{_cycleLabel}  {_placeableIndex + 1}/{_cycleSet.Count}   {Keys.Describe(Keys.AssetPicker)} for assets"
                            : $"{Keys.Describe(Keys.AssetPicker)} to choose an asset",
                        UI.StatusTone.Build);
                    break;
                }

                case EditMode.Delete: {
                    PlacedEntity? target = _entityLookingAt.IsValid ? FindOwner(_entityLookingAt) : null;
                    status.Set("DELETE",
                        target != null ? Placeable.ToDisplayName(target.PrefabName) : "(nothing under cursor)",
                        target != null
                            ? $"{Keys.Describe(Keys.Place)} to delete"
                            : "Only objects you placed can be deleted",
                        UI.StatusTone.Delete);
                    break;
                }

                case EditMode.Move: {
                    if (_carried != null) {
                        status.Set("MOVE - carrying",
                            Placeable.ToDisplayName(_carried.PrefabName),
                            $"{Keys.Describe(Keys.Place)} to put down   {Keys.Describe(Keys.RotateDrag)} drag to rotate",
                            UI.StatusTone.Move);
                    } else {
                        PlacedEntity? target = _entityLookingAt.IsValid ? FindOwner(_entityLookingAt) : null;
                        status.Set("MOVE",
                            target != null ? Placeable.ToDisplayName(target.PrefabName) : "(nothing under cursor)",
                            target != null
                                ? $"{Keys.Describe(Keys.Place)} to pick up"
                                : "Only objects you placed can be moved",
                            UI.StatusTone.Move);
                    }
                    break;
                }
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
                _ghost = Instantiate(prefabName, _positionLookingAt, _ghostRotation, enablePhysics: false);
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

        /// <param name="enablePhysics">
        /// False only for the preview ghost. TRUE for anything real, and it is not optional: a
        /// prefab instantiated at runtime has no physics bodies registered until SetPhysicsState is
        /// called on it and every child. Without bodies the object is not merely walk-through - it is
        /// invisible to raycasts, so Delete and Move could never find anything you had built. That is
        /// the same trap Homesteads hit with runtime ballistas having no collision and no use prompt.
        /// </param>
        private GameEntity? Instantiate(string prefabName, Vec3 position, Mat3 rotation, bool enablePhysics) {
            try {
                // Editor-authored markers save under their own id but instantiate a stand-in mesh,
                // so resolve through the registry rather than trusting the saved name to be a prefab.
                prefabName = PlaceableRegistry.ResolveSpawnPrefab(prefabName);
                if (!GameEntity.PrefabExists(prefabName)) return null;

                MatrixFrame frame = MatrixFrame.Identity;
                frame.rotation = rotation;
                frame.origin = position;

                GameEntity entity = GameEntity.Instantiate(Mission.Scene, prefabName, frame);
                // Set the whole frame rather than just the position: SetLocalPosition leaves the
                // rotation applied at construction, which drifts for nested prefabs.
                entity.SetGlobalFrame(in frame, true);

                if (enablePhysics) ApplyPhysicsRecursive(entity);
                return entity;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    $"Instantiate('{prefabName}') failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Registers physics bodies on an entity and all of its children.</summary>
        private static void ApplyPhysicsRecursive(GameEntity entity) {
            if (entity == null) return;
            try { entity.SetPhysicsState(true, true); } catch { }
            foreach (GameEntity child in entity.GetChildren()) ApplyPhysicsRecursive(child);
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
