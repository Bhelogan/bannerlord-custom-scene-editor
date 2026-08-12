using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.InputSystem;
using TaleWorlds.Localization;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    public enum EditMode {
        Off = 0,
        Build = 1,
        Delete = 2,
        Move = 3,
        /// <summary>Click a placed object to open its scripts. Its own mode rather than part of Move,
        /// so a click never means two different things depending on hidden state.</summary>
        Script = 4,
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

        /// <summary>
        /// The placed object under the cursor, resolved WITHOUT relying on physics.
        ///
        /// Many props ship with no collision shape - candle_flame and torch_a_wm_only_flame among
        /// them - so no physics body exists to raycast against and they were simply unselectable.
        /// Testing the ray against each placed object's bounding box instead works for everything
        /// we put there, whether or not the prefab has collision.
        /// </summary>
        private PlacedEntity? _hovered;

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

        /// <summary>
        /// Unsaved changes. Tracked rather than always saving on exit so leaving can offer a real
        /// choice - and so the exit prompt does not appear after a session where nothing changed.
        /// </summary>
        private bool _isDirty;

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
                // Read before anything draws a key hint, and on every scene open rather than once at
                // startup - so rebinding in the options screen takes effect on reopening the editor
                // instead of on restarting the game.
                Settings.KeyBindings.Refresh();

                string? crashed = PrefabCrashGuard.CheckPreviousSession();
                if (crashed != null) {
                    EditorHud.ShowMessage(
                        $"'{Placeable.ToDisplayName(crashed)}' closed the game last session and is now " +
                        "blocked. Everything else still works.", warning: true);
                }

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

            // Said on screen, not just logged: a pack that fails to parse takes every marker in it
            // with it, and the symptom is a category that simply is not there.
            IReadOnlyList<string> packErrors = Catalog.PackCatalog.LoadErrors;
            if (packErrors.Count > 0) {
                EditorHud.ShowMessage(
                    $"{packErrors.Count} marker pack(s) failed to load - see the trace log. " +
                    "Editor markers will be missing.", warning: true);
            }
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
                ScriptAttacher.ApplyAll(spawned, entity);
                _live.Add(entity);

                // Projects saved before numbering existed have markers with no number. Give them one
                // now, rather than leaving a scene where some markers are numbered and some are not
                // and the export quietly resolves the difference.
                if (entity.MarkerIndex <= 0) entity.MarkerIndex = NextMarkerIndex(entity.PrefabName);
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
                _hovered = null;
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
                distance = float.MaxValue;
            }

            _hovered = ResolveHovered(origin, source.Direction, distance);
        }

        /// <summary>
        /// Decides which placed object the cursor is on.
        ///
        /// The physics hit wins when it lands on something we placed, since it is exact. The
        /// bounding-box test covers the rest - props with no collision, and props standing in front
        /// of something solid, where the physics ray reports the wall behind and never mentions the
        /// candle in front. Whichever is CLOSER along the ray wins, so a small object is never lost
        /// to the large one behind it.
        /// </summary>
        private PlacedEntity? ResolveHovered(Vec3 origin, Vec3 direction, float physicsDistance) {
            PlacedEntity? fromPhysics = _entityLookingAt.IsValid ? FindOwner(_entityLookingAt) : null;

            PlacedEntity? fromBox = FindPlacedByRay(origin, direction, out float boxDistance);
            if (fromBox != null && (fromPhysics == null || boxDistance <= physicsDistance)) return fromBox;

            return fromPhysics;
        }

        /// <summary>Half a metre minimum on each axis, so thin props stay clickable.</summary>
        private const float MinimumPickSize = 0.5f;

        /// <summary>Nearest placed object whose bounding box the ray enters.</summary>
        private PlacedEntity? FindPlacedByRay(Vec3 origin, Vec3 direction, out float distance) {
            distance = float.MaxValue;
            PlacedEntity? best = null;

            foreach (PlacedEntity placed in _live) {
                if (placed.SceneEntity == null) continue;

                Vec3 min, max;
                try {
                    min = placed.SceneEntity.GlobalBoxMin;
                    max = placed.SceneEntity.GlobalBoxMax;
                } catch {
                    continue;
                }
                if (!min.IsValid || !max.IsValid) continue;

                // A candle is a couple of centimetres across and near impossible to put a cursor on
                // at its true size. Padding to a minimum clickable volume costs nothing and is the
                // difference between selectable and not.
                Pad(ref min, ref max, MinimumPickSize);

                if (RayHitsBox(origin, direction, min, max, out float hit) && hit < distance) {
                    distance = hit;
                    best = placed;
                }
            }

            return best;
        }

        private static void Pad(ref Vec3 min, ref Vec3 max, float minimumSize) {
            float half = minimumSize * 0.5f;
            if (max.x - min.x < minimumSize) { float c = (min.x + max.x) * 0.5f; min.x = c - half; max.x = c + half; }
            if (max.y - min.y < minimumSize) { float c = (min.y + max.y) * 0.5f; min.y = c - half; max.y = c + half; }
            if (max.z - min.z < minimumSize) { float c = (min.z + max.z) * 0.5f; min.z = c - half; max.z = c + half; }
        }

        /// <summary>
        /// Slab-method ray/box intersection. Returns the distance along the ray to the near face, and
        /// false when the ray misses or the box lies behind the camera.
        /// </summary>
        private static bool RayHitsBox(Vec3 origin, Vec3 direction, Vec3 min, Vec3 max, out float distance) {
            distance = 0f;
            float near = 0f;
            float far = float.MaxValue;

            for (int axis = 0; axis < 3; axis++) {
                float o = axis == 0 ? origin.x : axis == 1 ? origin.y : origin.z;
                float d = axis == 0 ? direction.x : axis == 1 ? direction.y : direction.z;
                float lo = axis == 0 ? min.x : axis == 1 ? min.y : min.z;
                float hi = axis == 0 ? max.x : axis == 1 ? max.y : max.z;

                if (MathF.Abs(d) < 1e-6f) {
                    // Parallel to this slab: a miss unless the origin already sits inside it.
                    if (o < lo || o > hi) return false;
                    continue;
                }

                float t1 = (lo - o) / d;
                float t2 = (hi - o) / d;
                if (t1 > t2) { float swap = t1; t1 = t2; t2 = swap; }

                if (t1 > near) near = t1;
                if (t2 < far) far = t2;
                if (near > far) return false;
            }

            distance = near;
            return far >= 0f;
        }

        private void HandleInput(float dt) {
            // Nothing else may act while the picker owns input: it pauses the engine and takes focus,
            // so a stray keypress reaching here would edit the scene behind a modal panel.
            if (UI.AssetPickerView.IsOpen || UI.ExportDialogView.IsOpen
                || UI.ScriptPanelView.IsOpen || UI.SceneOutlinerView.IsOpen) return;

            if (Settings.KeyBindings.KeyDetectionMode) ReportPressedKey();

            if (Input.IsKeyPressed(Keys.EditMode)) { CycleEditMode(); return; }

            // Listing what is in the scene is a read, not an edit, so it works with the editor idle
            // too - otherwise you have to enter a build mode just to look at your own work.
            if (Input.IsKeyPressed(Keys.Outliner)) { OpenOutliner(); return; }

            if (_mode == EditMode.Off) return;

            if (Input.IsKeyPressed(Keys.AssetPicker)) { OpenAssetPicker(); return; }

            if (Input.IsKeyPressed(Keys.CameraMode)) { CameraModes.Cycle(); return; }

            // Left click is the natural place action with a visible cursor. Read through the scene
            // layer, since Gauntlet consumes mouse buttons on the global path first. F still works
            // everywhere, including the player-attached cameras where the cursor is captured.
            // In RTS the click has to be read through the scene layer, because Gauntlet consumes mouse
            // buttons on the global path once the cursor is visible. In the player-attached cameras
            // there is no cursor and global input is the only source - so both paths are needed, and
            // LMB places in every camera mode rather than only the overhead one.
            bool clickPlaced = CameraModes.Current == EditorCameraMode.Rts
                ? (RtsCameraView.Instance?.IsKeyPressedOnScene(Keys.Place) ?? false)
                : Input.IsKeyPressed(Keys.Place);

            if (clickPlaced || Input.IsKeyPressed(Keys.PlaceAlt)) { HandlePlaceKey(); return; }

            bool savePressed = Input.IsKeyPressed(Keys.Save)
                            || (Input.IsKeyDown(Keys.SaveModifier) && Input.IsKeyPressed(Keys.SaveWithModifier));
            if (savePressed) { Save(); return; }

            // Alt+E rather than a bare key: E is rotate-right, and export is rare enough that it
            // belongs behind a modifier.
            if (Input.IsKeyDown(Keys.SaveModifier) && Input.IsKeyPressed(Keys.ExportWithModifier)) {
                OpenExportDialog();
                return;
            }

            if (Input.IsKeyPressed(Keys.ToggleGroundLock)) { ToggleGroundFollow(); return; }
            if (Input.IsKeyPressed(Keys.SnapToGround))     { SnapToGround();       return; }

            // Rotation and height are handled BEFORE the ghost check on purpose.
            //
            // These used to sit below it, so they only worked while a preview was on screen - and in
            // first and third person the preview comes and goes as you look around: aim at ground
            // inside the minimum placement distance, or at sky, and there is no ghost, so every
            // rotation key silently did nothing. The rotation is editor state, not ghost state; it
            // survives the preview and applies to the next one.
            if (HandleOrientationKeys(dt)) return;

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
                    if (_hovered != null) DeleteLookedAt();
                    break;

                case EditMode.Move:
                    if (_carried != null && _ghost != null) PlaceGhost();
                    else if (_hovered != null) PickUpLookedAt();
                    break;

                case EditMode.Script:
                    if (_hovered != null) OpenScriptsForLookedAt();
                    break;
            }
        }

        private void PlaceGhost() {
            Placeable? placeable = _carried != null ? PlaceableRegistry.Find(_carried.PrefabName) : CurrentPlaceable;
            string prefabName = _carried?.PrefabName ?? placeable?.PrefabName ?? "";
            if (prefabName.Length == 0) return;

            // Exported this session: on disk, but the game only reads prefab XML at startup.
            if (placeable != null && placeable.RequiresRestart) {
                EditorHud.ShowMessage(
                    $"'{placeable.DisplayName}' was exported this session - restart the game to place it.",
                    warning: true);
                return;
            }

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
                ScriptAttacher.ApplyAll(spawned, _carried);
                _target.OnEntityAdded(_carried);
                _live.Add(_carried);
                _carried = null;
                _isDirty = true;
            } else {
                var placed = new PlacedEntity {
                    PrefabName = prefabName,
                    Position = position,
                    Rotation = rotation,
                    SceneEntity = spawned,
                };
                placed.MarkerIndex = NextMarkerIndex(prefabName);
                _target.OnEntityAdded(placed);
                _live.Add(placed);
                _isDirty = true;

                if (placed.MarkerIndex > 0) {
                    EditorHud.ShowMessage(
                        $"{Placeable.ToDisplayName(prefabName)} #{placed.MarkerIndex} placed.");
                }
            }

            RemoveGhost();
            EditorHud.ShowCount(_live.Count);
        }

        private void DeleteLookedAt() {
            if (_hovered == null) {
                // Part of the original scene, not something we placed. Deleting shipped scene
                // geometry is a separate feature with its own persistence problem: it would have to
                // be recorded as a removal, since the scene reloads intact next time.
                EditorHud.ShowMessage("That is part of the original scene - only placed objects can be deleted.", warning: true);
                return;
            }
            Delete(_hovered);
        }

        /// <summary>Removes a placed object. Public so the outliner can act on a listed row.</summary>
        public void Delete(PlacedEntity owner) {
            if (owner == null) return;

            DestroyEntity(owner.SceneEntity);
            owner.SceneEntity = null;
            _live.Remove(owner);
            _target.OnEntityRemoved(owner);
            _isDirty = true;
            EditorHud.ShowCount(_live.Count);
        }

        private void PickUpLookedAt() {
            if (_hovered == null) {
                EditorHud.ShowMessage("That is part of the original scene - only placed objects can be moved.", warning: true);
                return;
            }
            PickUp(_hovered);
        }

        /// <summary>
        /// Lifts a placed object so it follows the cursor. Switches to Move mode, since picking
        /// something up from a list and then finding the click does nothing would be baffling.
        /// </summary>
        public void PickUp(PlacedEntity owner) {
            if (owner == null || _carried != null) return;

            if (_mode != EditMode.Move) {
                _mode = EditMode.Move;
                CameraModes.FollowEditMode(true);
                WeaponSheather.SetEditing(true);
            }

            DestroyEntity(owner.SceneEntity);
            owner.SceneEntity = null;
            _live.Remove(owner);
            _target.OnEntityRemoved(owner);
            _isDirty = true;

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

        /// <summary>
        /// Saves first, then opens the dialog. Exporting a project whose last few placements are not
        /// in the file would silently produce an incomplete artifact.
        /// </summary>
        /// <summary>
        /// Opens the script panel on whatever is under the cursor. Re-applies the scripts to the live
        /// entity whenever the panel changes something, so a fire lit in the panel appears on the
        /// brazier without having to re-place it.
        /// </summary>
        private void OpenScriptsForLookedAt() {
            if (_hovered == null) {
                EditorHud.ShowMessage("That is part of the original scene - only objects you placed can carry scripts.",
                    warning: true);
                return;
            }
            OpenScripts(_hovered);
        }

        /// <summary>Opens the script panel on a specific object. Public for the outliner.</summary>
        public void OpenScripts(PlacedEntity target) {
            if (target == null) return;

            UI.ScriptPanelView? view = UI.ScriptPanelView.Instance;
            if (view == null) {
                EditorHud.ShowMessage("Script panel unavailable.", warning: true);
                return;
            }

            view.OnScriptsChanged = changed => {
                _isDirty = true;
                if (changed.SceneEntity != null) ScriptAttacher.ApplyAll(changed.SceneEntity, changed);
            };
            view.Open(target);
        }

        /// <summary>
        /// Everything placed in this scene, as a list.
        ///
        /// Clicking in the world only reaches what is visible and in front of you. A list reaches
        /// what is buried inside a building, behind you, or too small to put a cursor on - and it is
        /// the only way to see what a scene actually contains without walking it.
        /// </summary>
        /// <summary>
        /// The number a newly placed marker gets: the lowest one not already in use for its type.
        ///
        /// Lowest-free rather than highest-plus-one so that deleting gate 3 and placing a
        /// replacement gives you gate 3 back. Counting up would leave a hole and hand the new gate
        /// the last number, quietly reordering the race.
        ///
        /// Zero for anything whose export name has no number in it - ordinary props, and the
        /// one-per-scene markers like race_start.
        /// </summary>
        private int NextMarkerIndex(string prefabName) {
            Placeable? placeable = PlaceableRegistry.Find(prefabName);
            if (placeable == null || placeable.ExportName.IndexOf("{index}", StringComparison.OrdinalIgnoreCase) < 0) {
                return 0;
            }

            var taken = new HashSet<int>();
            foreach (PlacedEntity entity in _live) {
                if (entity.MarkerIndex > 0 &&
                    string.Equals(entity.PrefabName, prefabName, StringComparison.OrdinalIgnoreCase)) {
                    taken.Add(entity.MarkerIndex);
                }
            }

            int candidate = 1;
            while (taken.Contains(candidate)) candidate++;
            return candidate;
        }

        /// <summary>Renumbers a marker by hand, from the scene contents list.</summary>
        public void SetMarkerIndex(PlacedEntity entity, int index) {
            if (entity == null || index < 0 || entity.MarkerIndex == index) return;
            entity.MarkerIndex = index;
            _isDirty = true;
        }

        /// <summary>
        /// Other markers of the same type sharing a number.
        ///
        /// Duplicates are allowed - two spawn points numbered 2 is a reasonable way to say "either
        /// of these" - but they are worth saying out loud, since the usual cause is a typo.
        /// </summary>
        public int CountMarkersWithIndex(PlacedEntity entity) {
            if (entity == null || entity.MarkerIndex <= 0) return 0;
            int count = 0;
            foreach (PlacedEntity other in _live) {
                if (other != entity && other.MarkerIndex == entity.MarkerIndex &&
                    string.Equals(other.PrefabName, entity.PrefabName, StringComparison.OrdinalIgnoreCase)) {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// The top-right reminder. Shown whenever there is unsaved work, in every mode including
        /// Off, and it names the keys rather than assuming they are remembered.
        /// </summary>
        private void UpdateUnsavedReminder(UI.EditorStatusVM status) {
            status.HasUnsavedChanges = _isDirty;
            if (!_isDirty) return;

            // Built from the live bindings, so a rebound save key is reported correctly rather than
            // sending someone to press a key that no longer saves.
            status.UnsavedText =
                $"{_live.Count} object(s) placed.   " +
                $"{Keys.Describe(Keys.SaveModifier)}+{Keys.Describe(Keys.SaveWithModifier)} " +
                $"or {Keys.Describe(Keys.Save)} to save.";
        }

        /// <summary>
        /// Rotation and height-offset keys. Returns true if one was used.
        ///
        /// Works with or without a live preview, in every camera mode - the orientation belongs to
        /// the editor rather than to the ghost.
        /// </summary>
        private bool HandleOrientationKeys(float dt) {
            if (Input.IsKeyDown(Keys.RotateTiltUp))    { _ghostRotation.RotateAboutSide(dt);     return true; }
            if (Input.IsKeyDown(Keys.RotateTiltDown))  { _ghostRotation.RotateAboutSide(-dt);    return true; }
            if (Input.IsKeyDown(Keys.RotateRollLeft))  { _ghostRotation.RotateAboutForward(dt);  return true; }
            if (Input.IsKeyDown(Keys.RotateRollRight)) { _ghostRotation.RotateAboutForward(-dt); return true; }
            if (Input.IsKeyDown(Keys.RotateTurnLeft))  { _ghostRotation.RotateAboutUp(dt);       return true; }
            if (Input.IsKeyDown(Keys.RotateTurnRight)) { _ghostRotation.RotateAboutUp(-dt);      return true; }

            if (Input.IsKeyDown(Keys.MoveUp))   { _ghostOffset += Vec3.Up * dt; return true; }
            if (Input.IsKeyDown(Keys.MoveDown)) { _ghostOffset -= Vec3.Up * dt; return true; }

            return false;
        }

        /// <summary>
        /// Names whatever key was just pressed, while Key Detection Mode is on in the settings.
        ///
        /// InputKey values are physical US-layout POSITIONS, not the letter printed on the cap: on
        /// AZERTY, InputKey.Q is the key labelled A. Without a way to ask, anyone on a non-US
        /// keyboard is guessing at what to type into the rebinding boxes.
        /// </summary>
        private void ReportPressedKey() {
            foreach (InputKey key in DetectableKeys) {
                if (!Input.IsKeyPressed(key)) continue;
                EditorHud.ShowMessage($"That key is called: {key}");
                return;
            }
        }

        /// <summary>
        /// Keys worth reporting. The whole enum includes controller axes and mouse movement, which
        /// would report constantly and drown out the answer.
        /// </summary>
        private static readonly InputKey[] DetectableKeys =
            ((InputKey[])Enum.GetValues(typeof(InputKey)))
            .Where(key => key != InputKey.Invalid
                       && !key.ToString().StartsWith("Controller", StringComparison.Ordinal)
                       && !key.ToString().StartsWith("Mouse", StringComparison.Ordinal))
            .ToArray();

        private void OpenOutliner() {
            UI.SceneOutlinerView? view = UI.SceneOutlinerView.Instance;
            if (view == null) {
                EditorHud.ShowMessage("Object list unavailable.", warning: true);
                return;
            }
            view.Open(_live, this);
        }

        /// <summary>
        /// Sets an object's exact position and rotation, for typed-in values.
        ///
        /// The whole frame is set rather than the position alone: SetLocalPosition leaves the
        /// construction-time rotation in place, which drifts on nested prefabs - the same trap that
        /// caught placement.
        /// </summary>
        public void UpdateTransform(PlacedEntity entity, Vec3 position, Mat3 rotation) {
            if (entity == null) return;

            entity.Position = position;
            entity.Rotation = rotation;

            if (entity.SceneEntity != null) {
                try {
                    MatrixFrame frame = MatrixFrame.Identity;
                    frame.rotation = rotation;
                    frame.origin = position;
                    entity.SceneEntity.SetGlobalFrame(in frame, true);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(SceneEditingMissionLogic),
                        $"UpdateTransform failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            _isDirty = true;
        }

        /// <summary>Moves the camera to look at an object, so a row in the list can be found in the world.</summary>
        public void FocusOn(PlacedEntity target) {
            if (target?.SceneEntity == null) return;
            try {
                RtsCameraView.Instance?.FocusOn(target.SceneEntity.GlobalPosition);
                EditorHud.ShowMessage($"Moved to {Placeable.ToDisplayName(target.PrefabName)}.");
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic), $"FocusOn failed: {ex.Message}");
            }
        }

        private void OpenExportDialog() {
            if (_target is SceneProjectTarget projectTarget) {
                Save();
                UI.ExportDialogView.Instance?.Open(projectTarget.Project);
            } else {
                EditorHud.ShowMessage("Export is only available for editor projects.", warning: true);
            }
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
        private void ChooseFromPicker(Placeable placeable, IReadOnlyList<Placeable> filtered, string scopeLabel) {
            if (filtered != null && filtered.Count > 0) {
                _cycleSet = filtered.ToList();
                _cycleLabel = string.IsNullOrWhiteSpace(scopeLabel) ? placeable.Category : scopeLabel;
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
                WeaponSheather.SetEditing(true);
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

            _mode = (EditMode)(((int)_mode + 1) % 5);
            RemoveGhost();

            // The camera follows the edit mode unless the player has picked one themselves: RTS for
            // editing, third person for walking around. Turning editing on is the moment an overhead
            // view starts being useful, and turning it off is the moment it stops.
            CameraModes.FollowEditMode(_mode != EditMode.Off);
            WeaponSheather.SetEditing(_mode != EditMode.Off);

            switch (_mode) {
                case EditMode.Off:
                    EditorHud.ShowMessage(
                        $"Editing off. {Keys.Describe(Keys.Outliner)}: scene contents.");
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
                        $"{Keys.Describe(Keys.Outliner)}: scene contents. " +
                        $"{Keys.Describe(Keys.CameraMode)}: camera. {Keys.Describe(Keys.Save)}: save.");
                    AnnouncePlaceable();
                    break;
                case EditMode.Delete:
                    EditorHud.ShowMessage($"Delete mode. {Keys.Describe(Keys.Place)}: delete what you are looking at.");
                    break;
                case EditMode.Move:
                    EditorHud.ShowMessage($"Move mode. {Keys.Describe(Keys.Place)}: pick up / put down.");
                    break;
                case EditMode.Script:
                    EditorHud.ShowMessage(
                        $"Script mode. {Keys.Describe(Keys.Place)}: open the scripts on an object.");
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
            // Mouse held off the combat controls while editing in a player-attached camera.
            CombatInputSuppressor.Instance?.Apply(_mode != EditMode.Off);

            UI.EditorStatusVM? status = UI.EditorStatusView.Instance?.DataSource;
            if (status == null) return;

            UpdateUnsavedReminder(status);

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
                    PlacedEntity? target = _hovered;
                    status.Set("DELETE",
                        target != null ? Placeable.ToDisplayName(target.PrefabName) : "(nothing under cursor)",
                        target != null
                            ? $"{Keys.Describe(Keys.Place)} to delete"
                            : "Only objects you placed can be deleted",
                        UI.StatusTone.Delete);
                    break;
                }

                case EditMode.Script: {
                    PlacedEntity? target = _hovered;
                    status.Set("SCRIPTS",
                        target != null ? Placeable.ToDisplayName(target.PrefabName) : "(nothing under cursor)",
                        target != null
                            ? $"{target.Scripts.Count} attached   {Keys.Describe(Keys.Place)} to edit"
                            : "Only objects you placed can carry scripts",
                        UI.StatusTone.Move);
                    break;
                }

                case EditMode.Move: {
                    if (_carried != null) {
                        status.Set("MOVE - carrying",
                            Placeable.ToDisplayName(_carried.PrefabName),
                            $"{Keys.Describe(Keys.Place)} to put down   {Keys.Describe(Keys.RotateDrag)} drag to rotate",
                            UI.StatusTone.Move);
                    } else {
                        PlacedEntity? target = _hovered;
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
            bool wantGhost = (_mode == EditMode.Build && CurrentPlaceable != null && !CurrentPlaceable.RequiresRestart)
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

                if (PrefabCrashGuard.IsBlocked(prefabName)) {
                    EditorHud.ShowMessage(
                        $"'{Placeable.ToDisplayName(prefabName)}' crashed the game last time it was " +
                        "built, so it is blocked. See csc_unsafe_prefabs.txt in the log folder.",
                        warning: true);
                    return null;
                }

                // Written BEFORE the call, and the log is flushed per line. A prefab whose scripts
                // fault in native code takes the process with it and leaves no crash report, so the
                // last line of the log naming what was being built is the only evidence there is.
                TraceLogger.Write(nameof(SceneEditingMissionLogic), $"Instantiating '{prefabName}'...");
                PrefabCrashGuard.Begin(prefabName);

                MatrixFrame frame = MatrixFrame.Identity;
                frame.rotation = rotation;
                frame.origin = position;

                GameEntity entity = GameEntity.Instantiate(Mission.Scene, prefabName, frame);

                // Before anything ticks it: the prefab's own scripts assume a mission this is not.
                PlacedScriptGuard.Strip(entity, prefabName);

                // Set the whole frame rather than just the position: SetLocalPosition leaves the
                // rotation applied at construction, which drifts for nested prefabs.
                entity.SetGlobalFrame(in frame, true);

                if (enablePhysics) ApplyPhysicsRecursive(entity);
                return entity;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    $"Instantiate('{prefabName}') failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            } finally {
                // Cleared however this ends, including on a managed exception: a prefab that merely
                // threw is still usable, and blocking it would punish the wrong failure. Only a
                // process that never reaches here at all leaves the mark behind.
                PrefabCrashGuard.End();
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

        /// <summary>Writes the project and says so. A save that gives no sign it happened is one you
        /// end up doing three times.</summary>
        private void Save() {
            try {
                _target.Commit();
                _isDirty = false;
                EditorHud.ShowMessage($"Saved '{_target.DisplayName}' - {_live.Count} object(s).");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneEditingMissionLogic), "Save failed", ex);
                EditorHud.ShowMessage("Save FAILED - see CustomSceneCreator.trace.log.", warning: true);
            }
        }

        /// <summary>
        /// Offers to save on the way out when there are unsaved changes.
        ///
        /// canLeave MUST be reported true even though we are asking a question: the engine's leave
        /// loop bails on the first behaviour that says false and never shows the inquiry at all, so
        /// returning false here would silently block leaving instead of prompting. The inquiry itself
        /// is what gates the exit.
        /// </summary>
        public override InquiryData OnEndMissionRequest(out bool canLeave) {
            canLeave = true;
            if (!_isDirty || _live.Count == 0) return null;

            return new InquiryData(
                new TextObject("{=CSC_UnsavedTitle}Unsaved Changes").ToString(),
                new TextObject("{=CSC_UnsavedText}You have unsaved changes to this scene. Save before leaving?")
                    .ToString(),
                true, true,
                new TextObject("{=CSC_UnsavedSave}Save and Leave").ToString(),
                new TextObject("{=CSC_UnsavedDiscard}Discard").ToString(),
                () => Save(),
                () => {
                    _isDirty = false;
                    TraceLogger.Write(nameof(SceneEditingMissionLogic),
                        $"Left without saving; {_live.Count} placed object(s) discarded.");
                });
        }

        protected override void OnEndMission() {
            base.OnEndMission();
            RemoveGhost();
            WeaponSheather.SetEditing(false);
            // Deliberately does NOT commit. OnEndMissionRequest already asked, and committing here
            // too would write the project even after the player chose Discard.
        }
    }
}
