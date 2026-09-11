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
        /// <summary>Read-only cursor probe and two-point route test for the baked scene navmesh.</summary>
        NavMesh = 5,
        /// <summary>Author a portable solid-object footprint for a future navmesh cutout pass.</summary>
        NavCutout = 6,
        /// <summary>Draw a height-limited polygon hole by placing perimeter nodes.</summary>
        NavPolygonCutout = 7,
        /// <summary>Mark an unmeshed area so the addition pass builds walkable navmesh over it.</summary>
        NavRequired = 8,
        /// <summary>Author an elevated perimeter for stairs, ramps, bridges, or wall walks.</summary>
        NavRamp = 9,
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
        /// <summary>The editor currently receiving direct build-selector commands, if any.</summary>
        public static SceneEditingMissionLogic? Active { get; private set; }

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
        private Vec3 _placementRayOrigin = Vec3.Invalid;
        private Vec3 _placementRayDirection = Vec3.Invalid;

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

        /// <summary>True when the ghost is a template preview built from many pieces rather than one
        /// instantiated prefab. Such a ghost is expensive to build, so it is hidden rather than
        /// rebuilt when the placement ray blinks out.</summary>
        private bool _ghostIsComposed;
        private string? _ghostSource;

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

        /// <summary>
        /// An already-placed object being rotated in place from the Transform action in Scene
        /// Contents.  Unlike <see cref="_carried"/>, this never becomes a ghost: it keeps the
        /// real object exactly where it is while the author uses the three visible axis rings.
        /// </summary>
        private PlacedEntity? _transformTarget;
        private int _transformAxis = 2; // 0=X/pitch, 1=Y/roll, 2=Z/yaw

        private readonly List<PlacedEntity> _live = new();

        // Read-only navmesh diagnostic state. First click stores A, second stores B and measures the
        // route; a third click starts a new A/B pair. These are never serialized into the project.
        private NavMeshProbe? _navPointA;
        private NavMeshProbe? _navPointB;
        private NavMeshRoute? _navRoute;

        // Ramp authoring deliberately keeps its partially-drawn strip in the project. A mode
        // switch must never discard a bridge/stair outline that the player has already clicked.
        private ProjectNavMeshRamp? _activeNavRamp;
        private bool _selectedNavRampLeft;
        private int _selectedNavRampPoint = -1;
        private ProjectNavMeshCutout? _activePolygonCutout;
        private int _selectedPolygonCutoutPoint = -1;

        private SceneProject? Project => (_target as SceneProjectTarget)?.Project;

        /// <summary>
        /// Unsaved changes. Tracked rather than always saving on exit so leaving can offer a real
        /// choice - and so the exit prompt does not appear after a session where nothing changed.
        /// </summary>
        private bool _isDirty;

        // MissionLogic continues receiving ticks while the escape/options/MCM UI is open. Those
        // screens pause the engine and focus their own Gauntlet layer, but global Input still sees
        // the same keypresses. Remember the blocked state both to suppress those presses and to
        // consume the first frame after the menu closes.
        private bool _inputWasBlockedByMenu;

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
        public int EditableObjectCount => _live.Count + (_carried != null ? 1 : 0);
        public int NavMeshCutoutCount => Project?.NavMeshCutouts?.Count ?? 0;
        public int NavMeshRequirementCount => Project?.NavMeshRequirements?.Count ?? 0;
        public int NavMeshRampCount => Project?.NavMeshRamps?.Count ?? 0;
        /// <summary>
        /// Snapshot for the Scene Contents navmesh tab. The list can also select one exact saved
        /// node and return the author to the existing in-world elevated-navmesh editing flow.
        /// </summary>
        public IReadOnlyList<ProjectNavMeshRamp> ElevatedNavMeshAreas =>
            Project?.NavMeshRamps is { } ramps
                ? ramps
                : Array.Empty<ProjectNavMeshRamp>();

        /// <summary>
        /// Selects a specific elevated-navmesh point from Scene Contents. This deliberately uses
        /// the same selected-point state as a world click, so the next normal placement click moves
        /// the chosen point instead of creating a competing editing path in the list UI.
        /// </summary>
        public bool SelectElevatedNavMeshNode(ProjectNavMeshRamp area, bool left, int index) {
            SceneProject? project = Project;
            if (area == null || project?.NavMeshRamps == null || !project.NavMeshRamps.Contains(area))
                return false;
            if (_carried != null) {
                EditorHud.ShowMessage("Place what you are carrying before editing an elevated navmesh point.", warning: true);
                return false;
            }

            bool hasOutline = NavMeshRampAuthoring.HasOutline(area);
            float[] points = hasOutline ? area.Outline : left ? area.Left : area.Right;
            if (index < 0 || index >= NavMeshRampAuthoring.PointCount(points)) return false;

            SetEditMode(EditMode.NavRamp);
            _activeNavRamp = area;
            _selectedNavRampLeft = hasOutline || left;
            _selectedNavRampPoint = index;
            string label = hasOutline ? "elevated outline" : left ? "left rail" : "right rail";
            EditorHud.ShowMessage($"Selected {label} corner {index + 1}. Click its new position to move it.");
            return true;
        }
        public bool HasEditableContent => EditableObjectCount > 0 || NavMeshCutoutCount > 0
            || NavMeshRequirementCount > 0 || NavMeshRampCount > 0;

        public override void AfterStart() {
            base.AfterStart();
            Active = this;
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
                int upgradedRequirements = EnsureRequirementTerrainBoundaries();
                if (upgradedRequirements > 0) {
                    _isDirty = true;
                    EditorHud.ShowMessage(
                        $"Captured terrain geometry for {upgradedRequirements} older navmesh-required " +
                        "area(s). Save to update their handoff manifest.");
                }
                EditorHud.ShowMessage(
                    $"Scene Creator ready. {Keys.Describe(Keys.EditMode)}: cycle edit modes. " +
                    $"{_live.Count} object(s) restored.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneEditingMissionLogic), "AfterStart failed", ex);
            }
        }

        private int EnsureRequirementTerrainBoundaries() {
            if (Project?.NavMeshRequirements == null) return 0;
            int upgraded = 0;
            foreach (ProjectNavMeshRequirement requirement in Project.NavMeshRequirements) {
                if (NavMeshRequirementAuthoring.EnsureTerrainBoundary(Mission.Scene, requirement))
                    upgraded++;
            }
            return upgraded;
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

        /// <summary>Puts the palette back where it was after a rebuild.</summary>
        private void RestoreSelection(string category, string? prefabName) {
            try {
                int index = _categories.FindIndex(c =>
                    string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) SelectCategory(index);

                if (prefabName == null) return;
                int at = _cycleSet.FindIndex(p =>
                    string.Equals(p.PrefabName, prefabName, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) _placeableIndex = at;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    $"Could not restore the palette selection: {ex.Message}");
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
                GameEntity? spawned = Instantiate(entity.PrefabName, entity.Position, entity.Rotation,
                                                   entity.Scale, enablePhysics: true);
                if (spawned == null) {
                    TraceLogger.Write(nameof(SceneEditingMissionLogic),
                        $"Could not restore '{entity.PrefabName}' - prefab missing. Left in the project.");
                    continue;
                }
                entity.SceneEntity = spawned;
                ScriptAttacher.ApplyAll(spawned, entity);
                TextureOverrideApplicator.ApplyAll(spawned, entity, out string textureError);
                if (textureError.Length > 0)
                    TraceLogger.Write(nameof(SceneEditingMissionLogic), $"Texture override on '{entity.PrefabName}': {textureError}");
                _live.Add(entity);

                // Projects saved before numbering existed have markers with no number. Give them one
                // now, rather than leaving a scene where some markers are numbered and some are not
                // and the export quietly resolves the difference.
                if (entity.MarkerIndex <= 0) entity.MarkerIndex = NextMarkerIndex(entity.PrefabName);
            }
        }

        public override void OnMissionTick(float dt) {
            base.OnMissionTick(dt);

            // Above the early return on purpose. Finished preview scenes are freed a few frames
            // after their tableau released them, and that has to keep happening whether or not the
            // player currently has an agent - the picker that created them is long gone by then.
            UI.PrefabPreview.PreviewSceneRetirement.Tick();

            // Deliberately not gated on MainAgent being player-controlled: the RTS camera hands the
            // agent to the AI controller so WASD moves the camera instead of the character.
            if (Mission.MainAgent == null) return;

            try {
                UpdateLookTarget();
                HandleInput(dt);
                UpdateGhost();
                RenderNavMeshVisuals();
                UpdateStatus();
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneEditingMissionLogic), "Tick failed", ex);
            }
        }

        private void UpdateLookTarget() {
            if (_mode == EditMode.Off) {
                _positionLookingAt = Vec3.Invalid;
                _entityLookingAt = WeakGameEntity.Invalid;
                _placementRayOrigin = Vec3.Invalid;
                _placementRayDirection = Vec3.Invalid;
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
            Vec3 direction = source.Direction.NormalizedCopy();
            Vec3 target = origin + direction * MaxPlaceDistance;
            _placementRayOrigin = origin;
            _placementRayDirection = direction;

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
            bool menuOwnsInput = MBCommon.IsPaused
                || UI.AssetPickerView.IsOpen || UI.ExportDialogView.IsOpen
                || UI.ScriptPanelView.IsOpen || UI.TexturePanelView.IsOpen || UI.SceneOutlinerView.IsOpen;

            // Global Input is not filtered by Gauntlet's focused-layer restrictions. Explicitly
            // ignore it while any modal menu owns the keyboard, including Bannerlord's options and
            // MCM screens as well as our own panels.
            if (menuOwnsInput) {
                _inputWasBlockedByMenu = true;
                return;
            }

            if (_inputWasBlockedByMenu) {
                _inputWasBlockedByMenu = false;

                // MCM applies its text settings when the options UI closes. Re-read them on that
                // exact transition, then consume this frame so the key used to close the menu can
                // never also trigger an editor command.
                if (Settings.KeyBindings.Refresh()) {
                    EditorHud.ShowMessage("Key bindings updated.");
                }
                return;
            }

            if (Settings.KeyBindings.KeyDetectionMode) ReportPressedKey();

            if (Input.IsKeyPressed(Keys.EditMode)) { CycleEditMode(); return; }

            // Listing what is in the scene is a read, not an edit, so it works with the editor idle
            // too - otherwise you have to enter a build mode just to look at your own work.
            if (Input.IsKeyPressed(Keys.Outliner)) { OpenOutliner(); return; }

            // Camera choice is useful even when no editing tool is active. In particular, RTS while
            // Off is a navigation-only free camera for inspecting a scene without carrying an asset.
            if (Input.IsKeyPressed(Keys.CameraMode)) { CameraModes.Cycle(); return; }

            if (_mode == EditMode.Off) return;

            if (Input.IsKeyPressed(Keys.AssetPicker)) { OpenAssetPicker(); return; }

            // In elevated-navmesh authoring, Delete applies only to an already selected rail dot.
            // It is deliberately before the ghost guard: ramp dots do not use a placeable ghost.
            if (_mode == EditMode.NavRamp && _selectedNavRampPoint >= 0
                && Input.IsKeyPressed(InputKey.Delete)) {
                DeleteSelectedNavMeshRampPoint();
                return;
            }
            if (_mode == EditMode.NavPolygonCutout && _selectedPolygonCutoutPoint >= 0
                && Input.IsKeyPressed(InputKey.Delete)) {
                DeleteSelectedPolygonCutoutPoint();
                return;
            }

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

            // Transform is a self-contained edit state. Its exact controls live in the side panel;
            // no world rings are rendered or clicked, so the selected object stays unobscured.
            if (_transformTarget != null && clickPlaced) {
                EditorHud.ShowMessage("Use the right-side Transform controls. Done or Escape leaves transform.");
                return;
            }

            // Duplicating is deliberately a Move-only action. Unlike Pick Up, it leaves the source
            // entity in the scene and puts a new, independent copy under the placement ray. It has
            // to happen before the normal ghost guard because there is no ghost yet.
            if (_mode == EditMode.Move && _carried == null && _transformTarget == null &&
                Input.IsKeyPressed(Keys.Duplicate)) {
                DuplicateLookedAt();
                return;
            }

            bool alternatePlace = Input.IsKeyPressed(Keys.PlaceAlt);
            if (clickPlaced || alternatePlace) { HandlePlaceKey(alternatePlace); return; }

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
            if (Input.IsKeyPressed(Keys.AlignToSurface))   { AlignGhostToSurface(); return; }

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
                float precision = Input.IsKeyDown(InputKey.LeftShift) ? 0.1f : 1f;
                _ghostOffset.z += scroll * ScrollHeightStep * precision;
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
            HandleRotateDrag();

            if (_mode != EditMode.Build) return;

            if (Input.IsKeyPressed(Keys.NextCategory)) { SelectCategory(_categoryIndex + 1); AnnouncePlaceable(); return; }
            if (Input.IsKeyPressed(Keys.NextPlaceable)) { CyclePlaceable(1);  return; }
            if (Input.IsKeyPressed(Keys.PrevPlaceable)) { CyclePlaceable(-1); return; }
        }

        private void HandlePlaceKey(bool alternate = false) {
            switch (_mode) {
                case EditMode.Build:
                    if (_ghost != null) PlaceGhost();
                    break;

                case EditMode.Delete:
                    if (_hovered != null) DeleteLookedAt();
                    break;

                case EditMode.Move:
                    if (_transformTarget != null) {
                        EditorHud.ShowMessage("Transform is active: use the right-side X/Y/Z controls. Press \\ to leave.");
                    }
                    else if (_carried != null && _ghost != null) PlaceGhost();
                    else if (_hovered != null) PickUpLookedAt();
                    break;

                case EditMode.NavMesh:
                    SetNavMeshRoutePoint();
                    break;

                case EditMode.NavCutout:
                    ToggleNavMeshCutout();
                    break;

                case EditMode.NavRequired:
                    ToggleNavMeshRequirement();
                    break;

                case EditMode.NavRamp:
                    if (alternate) AdvanceOrFinishNavMeshRamp();
                    else AddNavMeshRampPoint();
                    break;

                case EditMode.NavPolygonCutout:
                    if (alternate) FinishNavMeshPolygonCutout();
                    else AddNavMeshPolygonCutoutPoint();
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
            // The live preview frame includes root scale. Persist the editor's pure rotation so a
            // later angle edit cannot multiply that scale into the basis a second time.
            Mat3 rotation = _ghostRotation;

            // A template drops its contents as separate objects rather than becoming one.
            if (placeable != null && placeable.IsTemplate && _carried == null) {
                PlaceTemplate(placeable, position, rotation);
                RemoveGhost();
                EditorHud.ShowCount(_live.Count);
                return;
            }

            Vec3 placementScale = _carried?.Scale ?? new Vec3(1f, 1f, 1f);
            GameEntity? spawned = Instantiate(prefabName, position, rotation, placementScale,
                                              enablePhysics: true);
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
                TextureOverrideApplicator.ApplyAll(spawned, _carried, out _);
                _target.OnEntityAdded(_carried);
                _live.Add(_carried);
                RefreshNavMeshCutout(_carried);
                _carried = null;
                _isDirty = true;
            } else {
                var placed = new PlacedEntity {
                    PrefabName = prefabName,
                    Position = position,
                    Rotation = rotation,
                    Scale = placementScale,
                    SceneEntity = spawned,
                    // CSC prefab XML cannot embed runtime PNG pixels. Its sibling manifest restores
                    // unambiguous material/image pairs when that prefab comes back through My Prefabs.
                    TextureOverrides = IO.TextureOverrideManifestExporter.LoadForPrefab(prefabName),
                };
                TextureOverrideApplicator.ApplyAll(spawned, placed, out string textureError);
                if (!string.IsNullOrWhiteSpace(textureError))
                    EditorHud.ShowMessage("Texture override: " + textureError, warning: true);
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

        /// <summary>
        /// Drops a saved project into this scene as loose, individually editable pieces.
        ///
        /// The difference from a prefab is the whole point. A prefab arrives as one object and can
        /// never be taken apart, which is right for something reused unchanged and wrong for a
        /// template: nine copies of a refuge, each adapted to its own terrain, need every piece to
        /// stay movable. So the pieces are added exactly as if they had been placed by hand.
        ///
        /// Positions are re-anchored to where the cursor is: the source project's centroid in X/Y
        /// and its LOWEST point in Z, the same rule the prefab exporter uses, so the template lands
        /// sitting on the ground rather than half buried.
        /// </summary>
        private void PlaceTemplate(Placeable placeable, Vec3 position, Mat3 rotation) {
            TextureOverrideApplicator.ImportTemplateImages(placeable.TemplateProject);
            SceneProject? source = ProjectSerializer.LoadFile(placeable.TemplateProject);
            if (source == null || source.Entities.Count == 0) {
                EditorHud.ShowMessage($"'{placeable.DisplayName}' has nothing in it.", warning: true);
                return;
            }

            Vec3 anchor = ComputeTemplateAnchor(source);
            float yaw = rotation.GetEulerAngles().z;

            int placed = 0;
            int skipped = 0;
            foreach (ProjectEntity entity in source.Entities) {
                PlacedEntity piece = entity.To();

                // Rotate the offset with the template, so placing it turned turns the whole layout
                // rather than spinning each piece where it stands.
                Vec3 offset = piece.Position - anchor;
                Vec3 turned = new Vec3(
                    offset.x * MathF.Cos(yaw) - offset.y * MathF.Sin(yaw),
                    offset.x * MathF.Sin(yaw) + offset.y * MathF.Cos(yaw),
                    offset.z);

                piece.Position = position + turned;

                Mat3 turnedRotation = piece.Rotation;
                turnedRotation.RotateAboutUp(yaw);
                piece.Rotation = turnedRotation;

                GameEntity? spawned = Instantiate(piece.PrefabName, piece.Position, piece.Rotation,
                                                 piece.Scale, enablePhysics: true);
                if (spawned == null) { skipped++; continue; }

                piece.SceneEntity = spawned;
                ScriptAttacher.ApplyAll(spawned, piece);
                TextureOverrideApplicator.ApplyAll(spawned, piece, out _);
                _target.OnEntityAdded(piece);
                _live.Add(piece);
                placed++;
            }

            _isDirty = true;

            string note = skipped > 0 ? $"  {skipped} piece(s) could not be created." : "";
            EditorHud.ShowMessage($"Placed {placed} piece(s) from the template." + note);
            TraceLogger.Write(nameof(SceneEditingMissionLogic),
                $"Template '{placeable.TemplateProject}': placed {placed}, skipped {skipped}.");
        }

        /// <summary>Centroid in X/Y, lowest point in Z - the same anchor the prefab exporter uses.</summary>
        private static Vec3 ComputeTemplateAnchor(SceneProject project) {
            float sumX = 0f, sumY = 0f, minZ = float.MaxValue;
            foreach (ProjectEntity e in project.Entities) {
                sumX += e.Pos[0];
                sumY += e.Pos[1];
                if (e.Pos[2] < minZ) minZ = e.Pos[2];
            }
            int count = project.Entities.Count;
            return new Vec3(sumX / count, sumY / count, minZ == float.MaxValue ? 0f : minZ);
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

            RemoveNavMeshCutout(owner.Id);

            DestroyEntity(owner.SceneEntity);
            owner.SceneEntity = null;
            _live.Remove(owner);
            _target.OnEntityRemoved(owner);
            _isDirty = true;
            EditorHud.ShowCount(_live.Count);
        }

        /// <summary>
        /// True only for a composite imported from CSC's My Prefabs folder. Native game prefabs
        /// are intentionally excluded: their internal mesh groups are not independently placeable
        /// assets, so presenting this action for them would promise an editability we cannot save.
        /// </summary>
        public bool CanBreakApart(PlacedEntity owner) => PrefabBreakApartService.IsCandidate(owner);

        /// <summary>Describes what Break Apart can recover before the outliner asks for confirmation.</summary>
        public bool TryDescribeBreakApart(PlacedEntity owner, out int pieces, out int navMeshGroups,
                                          out int unsupportedGroups, out bool approximatedScale) {
            pieces = 0;
            navMeshGroups = 0;
            unsupportedGroups = 0;
            approximatedScale = false;
            if (!CanBreakApart(owner)) return false;

            PrefabBreakApartService.Result result = PrefabBreakApartService.Extract(owner);
            pieces = result.Pieces.Count;
            navMeshGroups = result.NavMeshGroups;
            unsupportedGroups = result.UnsupportedGroups;
            approximatedScale = result.ApproximatedScale;
            return pieces > 0;
        }

        /// <summary>
        /// Replaces one imported composite with its addressable child prefabs. Creation is
        /// transactional: every child is instantiated before the original is removed, so one bad
        /// child cannot turn a recoverable prefab into a partially expanded scene.
        /// </summary>
        public bool TryBreakApart(PlacedEntity owner, out List<PlacedEntity> pieces,
                                  out string message) {
            pieces = new List<PlacedEntity>();
            message = "That object cannot be broken apart.";
            if (!CanBreakApart(owner)) return false;
            if (owner.Scripts.Count > 0) {
                message = "This composite has scripts attached to the whole object. Remove or move " +
                          "those scripts before breaking it apart; the original prefab was left unchanged.";
                return false;
            }

            PrefabBreakApartService.Result plan = PrefabBreakApartService.Extract(owner);
            if (plan.Pieces.Count == 0) {
                message = "No editable child prefabs were found. The original object was left unchanged.";
                return false;
            }
            if (plan.UnsupportedGroups > 0) {
                message = $"This prefab contains {plan.UnsupportedGroups} anonymous geometry group(s) " +
                          "that CSC cannot save as separate objects. The original prefab was left unchanged.";
                return false;
            }

            var spawned = new List<PlacedEntity>();
            foreach (PlacedEntity piece in plan.Pieces) {
                GameEntity? entity = Instantiate(piece.PrefabName, piece.Position, piece.Rotation,
                                                 piece.Scale, enablePhysics: true);
                if (entity == null) {
                    foreach (PlacedEntity created in spawned) DestroyEntity(created.SceneEntity);
                    message = $"Could not create '{PlaceableRegistry.DisplayNameFor(piece.PrefabName)}'. " +
                              "The original prefab was left unchanged.";
                    return false;
                }

                piece.SceneEntity = entity;
                ScriptAttacher.ApplyAll(entity, piece);
                spawned.Add(piece);
            }

            // Only now is the operation committed to the editable scene.
            RemoveNavMeshCutout(owner.Id);
            DestroyEntity(owner.SceneEntity);
            owner.SceneEntity = null;
            _live.Remove(owner);
            _target.OnEntityRemoved(owner);

            foreach (PlacedEntity piece in spawned) {
                // Assign after previous children have entered _live, so several unnumbered markers
                // of the same type receive distinct lowest-free numbers.
                if (piece.MarkerIndex <= 0 && PlaceableRegistry.IsNumberedMarker(piece.PrefabName)) {
                    piece.MarkerIndex = NextMarkerIndex(piece.PrefabName);
                }
                _target.OnEntityAdded(piece);
                _live.Add(piece);
            }

            // Metadata is committed only after every child exists. The recovered records are
            // already transformed into world space by PrefabBreakApartService and therefore show
            // immediately in their normal Cutout / Add Area / Elevated editing modes.
            Project!.NavMeshCutouts.AddRange(plan.Cutouts);
            Project.NavMeshRequirements.AddRange(plan.Requirements);
            Project.NavMeshRamps.AddRange(plan.Ramps);

            pieces = spawned;
            _isDirty = true;
            EditorHud.ShowCount(_live.Count);
            message = $"Broke the prefab into {pieces.Count} editable piece(s).";
            if (plan.NavMeshGroups > 0) {
                message += $" Restored {plan.Cutouts.Count} cutout(s), " +
                           $"{plan.Requirements.Count} added area(s), and " +
                           $"{plan.Ramps.Count} elevated area(s).";
            }
            if (plan.ApproximatedScale) {
                message += " A rotated child under non-uniform scale used the closest editable transform.";
            }
            TraceLogger.Write(nameof(SceneEditingMissionLogic),
                $"Break Apart '{owner.PrefabName}': {pieces.Count} pieces, " +
                $"{plan.NavMeshGroups} navmesh groups, {plan.UnsupportedGroups} unsupported groups.");
            return true;
        }

        /// <summary>
        /// Clears everything authored by this project while leaving the shipped/base scene alone.
        /// The outliner confirms before calling this; persistence still follows the editor's normal
        /// save/discard flow so an accidental clear can be discarded by leaving without saving.
        /// </summary>
        public void ClearAllEditableContent() {
            int objectCount = EditableObjectCount;
            int cutoutCount = NavMeshCutoutCount;
            int requirementCount = NavMeshRequirementCount;
            int rampCount = NavMeshRampCount;
            if (objectCount + cutoutCount + requirementCount + rampCount == 0) return;

            foreach (PlacedEntity entity in _live.ToList()) {
                DestroyEntity(entity.SceneEntity);
                entity.SceneEntity = null;
                _target.OnEntityRemoved(entity);
            }
            _live.Clear();

            // A picked-up object has already been removed from the target and _live; its composed
            // preview is the only remaining representation and must not survive a full clear.
            if (_carried != null) {
                _carried.SceneEntity = null;
                _carried = null;
                RemoveGhost();
            }

            SceneProject? project = Project;
            project?.NavMeshCutouts?.Clear();
            project?.NavMeshRequirements?.Clear();
            project?.NavMeshRamps?.Clear();

            _activeNavRamp = null;
            _selectedNavRampPoint = -1;
            _selectedNavRampLeft = false;
            _activePolygonCutout = null;
            _selectedPolygonCutoutPoint = -1;
            _hovered = null;
            _navPointA = null;
            _navPointB = null;
            _navRoute = null;
            _isDirty = true;

            EditorHud.ShowCount(0);
            EditorHud.ShowMessage(
                $"Cleared {objectCount} object(s), {cutoutCount} cutout(s), " +
                $"{requirementCount} added area(s), and {rampCount} elevated area(s). Save to keep the project empty.");
        }

        private void PickUpLookedAt() {
            if (_hovered == null) {
                EditorHud.ShowMessage("That is part of the original scene - only placed objects can be moved.", warning: true);
                return;
            }
            PickUp(_hovered);
        }

        /// <summary>
        /// Makes an independent carried copy of the placed object under the aim point. This is not
        /// implemented as pick-up/place twice: the original must remain live, and attached scripts,
        /// per-surface textures, scale, and rotation all need to travel with the new copy.
        /// </summary>
        private void DuplicateLookedAt() {
            if (_hovered == null) {
                EditorHud.ShowMessage("Aim at an object placed by this editor to duplicate it.", warning: true);
                return;
            }

            PlacedEntity source = _hovered;
            var duplicate = new PlacedEntity {
                // A duplicate is a distinct scene entity. Keep script variables intact, because
                // they may point at other deliberate scene objects, but never reuse the owner's ID.
                Id = Guid.NewGuid().ToString("B").ToUpperInvariant(),
                PrefabName = source.PrefabName,
                Position = source.Position,
                Rotation = source.Rotation,
                Scale = source.Scale,
                Scripts = (source.Scripts ?? new List<AttachedScript>())
                    .Select(script => script.Clone()).ToList(),
                TextureOverrides = (source.TextureOverrides ?? new List<TextureOverride>())
                    .Select(texture => texture.Clone()).ToList(),
            };

            // Numbered markers are independently addressable exported entities. A copied marker
            // therefore receives the next free number rather than silently creating a second gate
            // or spawn with the source marker's identity.
            duplicate.MarkerIndex = NextMarkerIndex(duplicate.PrefabName);

            _carried = duplicate;
            _ghostRotation = duplicate.Rotation;
            _ghostOffset = Vec3.Zero;
            RemoveGhost();
            EditorHud.ShowMessage(
                $"Duplicating {Placeable.ToDisplayName(duplicate.PrefabName)}. " +
                $"{Keys.Describe(Keys.Place)} to place.");
        }

        /// <summary>
        /// Lifts a placed object so it follows the cursor. Switches to Move mode, since picking
        /// something up from a list and then finding the click does nothing would be baffling.
        /// </summary>
        public void PickUp(PlacedEntity owner) {
            if (owner == null || _carried != null) return;

            if (_transformTarget != null) UI.TransformPanelView.Instance?.Close();
            _transformTarget = null;

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

        /// <summary>Opens the live transform panel for an existing object without moving it.</summary>
        public void StartTransform(PlacedEntity entity) {
            if (entity == null || !_live.Contains(entity)) return;
            if (_carried != null) {
                EditorHud.ShowMessage("Place what you are carrying before transforming another object.", warning: true);
                return;
            }

            _transformTarget = entity;
            _transformEulerDegrees = SafeEulerDegrees(entity.Rotation, Vec3.Zero);
            _transformEulerValid = true;
            _transformAxis = 2;
            _mode = EditMode.Move;
            CameraModes.FollowEditMode(true);
            WeaponSheather.SetEditing(true);
            RemoveGhost();
            UI.TransformPanelView.Instance?.Open(this, entity);
            EditorHud.ShowMessage(
                $"Transforming {Placeable.ToDisplayName(entity.PrefabName)}. " +
                "Use the right-side X/Y/Z controls or enter exact rotation values. " +
                "Press \\ to leave transform.");
        }

        /// <summary>Closes the transform workflow while leaving the object exactly where it is.</summary>
        public void StopTransform(PlacedEntity entity) {
            if (_transformTarget != entity) return;
            _transformTarget = null;
            _transformEulerValid = false;
        }

        /// <summary>Returns the selected object's Euler angles in degrees: X/pitch, Y/roll, Z/yaw.</summary>
        public Vec3 GetTransformEulerDegrees(PlacedEntity entity) {
            if (entity == null) return Vec3.Zero;

            // While a transform is open, the panel's own numbers are authoritative - the matrix is
            // NOT read back. Euler extraction is singular at pitch +/-90:
            //
            //     Mat3.GetEulerAngles() => new Vec3(Asin(f.z), Atan2(-s.z, u.z), Atan2(-f.x, f.y))
            //
            // At that pitch f.z is +/-1, so any floating error at all makes Asin return NaN, and
            // both Atan2 terms collapse toward zero so yaw and roll stop being separable. Reading
            // back through it loses the object's real orientation and can feed NaN into the matrix
            // that is written to the engine next.
            if (_transformTarget == entity && _transformEulerValid) return _transformEulerDegrees;

            return SafeEulerDegrees(entity.Rotation, Vec3.Zero);
        }

        /// <summary>Euler degrees, or <paramref name="fallback"/> when the extraction is degenerate.</summary>
        private static Vec3 SafeEulerDegrees(Mat3 rotation, Vec3 fallback) {
            Vec3 radians = rotation.GetEulerAngles();
            if (!IsFinite(radians)) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    "Euler extraction returned a non-finite angle (gimbal lock at +/-90 pitch); "
                    + "keeping the last known orientation instead.");
                return fallback;
            }

            const float degreesPerRadian = 180f / MathF.PI;
            return new Vec3(radians.x * degreesPerRadian, radians.y * degreesPerRadian,
                radians.z * degreesPerRadian);
        }

        private static bool IsFinite(Vec3 v) =>
            !float.IsNaN(v.x) && !float.IsInfinity(v.x)
            && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
            && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        private static bool IsFinite(Mat3 m) => IsFinite(m.s) && IsFinite(m.f) && IsFinite(m.u);

        /// <summary>The angles the transform panel is showing, in degrees.</summary>
        private Vec3 _transformEulerDegrees;
        private bool _transformEulerValid;

        /// <summary>
        /// Replaces one Euler component with an author-entered degree value. Applying from identity
        /// is intentional: Mat3.ApplyEulerAngles accumulates rather than assigning.
        /// </summary>
        public void SetTransformAxisDegrees(PlacedEntity entity, int axis, float degrees) {
            if (entity == null || !_live.Contains(entity) || axis < 0 || axis > 2
                || float.IsNaN(degrees) || float.IsInfinity(degrees)) return;

            // Built from the panel's own numbers, not from the matrix. Re-extracting here is what
            // made +/-90 destructive: at that pitch the extraction is singular, so the two other
            // axes came back meaningless (or NaN) and were then written straight back out.
            Vec3 current = GetTransformEulerDegrees(entity);
            if (axis == 0) current.x = degrees;
            else if (axis == 1) current.y = degrees;
            else current.z = degrees;

            ApplyTransformEuler(entity, current);
        }

        /// <summary>Rotates the real object by a small, predictable world-axis amount.</summary>
        public void RotateTransformAxis(PlacedEntity entity, int axis, float degrees) {
            if (entity == null || !_live.Contains(entity) || axis < 0 || axis > 2
                || float.IsNaN(degrees) || float.IsInfinity(degrees)) return;

            Vec3 current = GetTransformEulerDegrees(entity);
            if (axis == 0) current.x += degrees;
            else if (axis == 1) current.y += degrees;
            else current.z += degrees;

            ApplyTransformEuler(entity, current);
        }

        /// <summary>
        /// Writes one orientation, in degrees, and remembers it.
        ///
        /// The remembered triple is what the panel reads back, so the object's orientation survives
        /// pitch +/-90 - where the matrix alone can no longer describe it unambiguously. The matrix
        /// is checked before it reaches the engine: a non-finite frame is not a visual glitch, it is
        /// a value the native side will carry into physics and culling.
        /// </summary>
        private void ApplyTransformEuler(PlacedEntity entity, Vec3 degrees) {
            if (!IsFinite(degrees)) return;

            const float radiansPerDegree = MathF.PI / 180f;
            var radians = new Vec3(degrees.x * radiansPerDegree,
                                   degrees.y * radiansPerDegree,
                                   degrees.z * radiansPerDegree);

            Mat3 rotation = Mat3.Identity;
            rotation.ApplyEulerAngles(in radians);

            if (!IsFinite(rotation)) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    $"Refusing a non-finite rotation for '{entity.PrefabName}' at "
                    + $"({degrees.x:0.#}, {degrees.y:0.#}, {degrees.z:0.#}) degrees.");
                return;
            }

            if (_transformTarget == entity) {
                _transformEulerDegrees = degrees;
                _transformEulerValid = true;
            }

            UpdateTransform(entity, entity.Position, rotation, entity.Scale);
        }

        /// <summary>Records the panel's active axis for its +/- operations.</summary>
        public void SelectTransformAxis(int axis) {
            if (_transformTarget == null || axis < 0 || axis > 2) return;
            _transformAxis = axis;
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
        /// Makes the held object's local S/F plane flush with the physical surface under the
        /// cursor. Flat meshes such as picture panels use local U as their face normal; this turns
        /// that normal onto the wall instead of asking the user to eyeball an exact 90-degree tilt.
        ///
        /// Bannerlord's retail raycast does not expose a collision normal. We reconstruct one from
        /// nearby parallel rays on the same entity. A small or highly detailed prop may not provide
        /// enough neighbouring hits, in which case facing the original ray is the safest useful
        /// fallback and remains much more exact than free-hand rotation.
        /// </summary>
        private void AlignGhostToSurface() {
            if (_ghost == null || !_positionLookingAt.IsValid || !_placementRayDirection.IsValid) {
                EditorHud.ShowMessage("Aim at a physical surface while holding an object.", warning: true);
                return;
            }

            Vec3 normal = ReconstructSurfaceNormal();
            if (!normal.IsValid || normal.LengthSquared < 0.001f) {
                EditorHud.ShowMessage("Could not read that surface. Aim at a broader part of it and try again.",
                    warning: true);
                return;
            }
            normal.Normalize();

            // Keep wall-mounted objects upright. On floors and sloped roofs, retain their current
            // forward heading as far as the new plane permits.
            Vec3 forward = MathF.Abs(normal.z) < 0.65f ? Vec3.Up : _ghostRotation.f;
            forward -= normal * Vec3.DotProduct(forward, normal);
            if (forward.LengthSquared < 0.001f) {
                forward = _ghostRotation.s - normal * Vec3.DotProduct(_ghostRotation.s, normal);
            }
            if (forward.LengthSquared < 0.001f) {
                forward = new Vec3(0f, 1f, 0f) - normal * normal.y;
            }
            forward.Normalize();

            Vec3 side = Vec3.CrossProduct(forward, normal);
            side.Normalize();
            _ghostRotation = new Mat3(in side, in forward, in normal);

            EditorHud.ShowMessage(
                $"Aligned flush to the aimed surface. {Keys.Describe(Keys.RotateTurnLeft)}/" +
                $"{Keys.Describe(Keys.RotateTurnRight)} rotates it within that surface.");
        }

        private Vec3 ReconstructSurfaceNormal() {
            Vec3 towardCamera = -_placementRayDirection;

            // Terrain already exposes its real normal; use it rather than approximating a slope.
            if (!_entityLookingAt.IsValid) {
                try {
                    Mission.Scene.GetTerrainHeightAndNormal(_positionLookingAt.AsVec2, out _, out Vec3 terrainNormal);
                    if (terrainNormal.IsValid && terrainNormal.LengthSquared > 0.001f) {
                        if (Vec3.DotProduct(terrainNormal, towardCamera) < 0f) terrainNormal = -terrainNormal;
                        return terrainNormal;
                    }
                } catch { }
                return towardCamera;
            }

            Vec3 reference = MathF.Abs(_placementRayDirection.z) < 0.85f
                ? Vec3.Up
                : new Vec3(1f, 0f, 0f);
            Vec3 sampleRight = Vec3.CrossProduct(_placementRayDirection, reference);
            sampleRight.Normalize();
            Vec3 sampleUp = Vec3.CrossProduct(sampleRight, _placementRayDirection);
            sampleUp.Normalize();

            float[] radii = { 0.12f, 0.3f, 0.65f };
            foreach (float radius in radii) {
                bool hasRight = TrySurfaceTangent(sampleRight, radius, out Vec3 rightTangent);
                bool hasUp = TrySurfaceTangent(sampleUp, radius, out Vec3 upTangent);
                if (!hasRight || !hasUp) continue;

                Vec3 normal = Vec3.CrossProduct(rightTangent, upTangent);
                if (normal.LengthSquared < 0.001f) continue;
                normal.Normalize();
                if (Vec3.DotProduct(normal, towardCamera) < 0f) normal = -normal;
                return normal;
            }

            return towardCamera;
        }

        private bool TrySurfaceTangent(Vec3 sampleAxis, float radius, out Vec3 tangent) {
            tangent = Vec3.Zero;
            bool plus = TrySampleSurface(sampleAxis * radius, out Vec3 plusPoint);
            bool minus = TrySampleSurface(sampleAxis * -radius, out Vec3 minusPoint);

            if (plus && minus) tangent = plusPoint - minusPoint;
            else if (plus) tangent = plusPoint - _positionLookingAt;
            else if (minus) tangent = _positionLookingAt - minusPoint;

            return tangent.IsValid && tangent.LengthSquared > 0.0001f;
        }

        private bool TrySampleSurface(Vec3 offset, out Vec3 point) {
            point = Vec3.Invalid;
            Vec3 origin = _placementRayOrigin + offset;
            Vec3 target = origin + _placementRayDirection * MaxPlaceDistance;
            if (!Mission.Scene.RayCastForClosestEntityOrTerrain(
                    origin, target, out _, out point, out WeakGameEntity hit)) return false;
            if (!hit.IsValid) return false;
            if (hit == _entityLookingAt) return true;

            // A prefab's visible wall can be split across child entities. Nearby samples on another
            // child of the same prefab still describe the same physical surface.
            try {
                WeakGameEntity expectedRoot = _entityLookingAt.Root;
                WeakGameEntity hitRoot = hit.Root;
                return expectedRoot.IsValid && hitRoot.IsValid && expectedRoot == hitRoot;
            } catch {
                return false;
            }
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

        /// <summary>Opens per-material PNG overrides for a specific object. Public for the outliner.</summary>
        public void OpenTextures(PlacedEntity target) {
            if (target == null) return;
            UI.TexturePanelView? view = UI.TexturePanelView.Instance;
            if (view == null) {
                EditorHud.ShowMessage("Texture panel unavailable.", warning: true);
                return;
            }
            view.OnChanged = RefreshTextureOverrides;
            view.Open(target);
        }

        /// <summary>
        /// Recreate the instance from its original prefab before applying overrides. This is what
        /// makes Remove reliable: materials copied for a previous preview cannot otherwise be
        /// restored to the prefab defaults in place.
        /// </summary>
        private void RefreshTextureOverrides(PlacedEntity changed) {
            GameEntity? replacement = Instantiate(changed.PrefabName, changed.Position, changed.Rotation,
                                                  changed.Scale, enablePhysics: true);
            if (replacement == null) {
                EditorHud.ShowMessage("Could not refresh the textured object.", warning: true);
                return;
            }
            ScriptAttacher.ApplyAll(replacement, changed);
            int applied = TextureOverrideApplicator.ApplyAll(replacement, changed, out string error);
            DestroyEntity(changed.SceneEntity);
            changed.SceneEntity = replacement;
            _isDirty = true;
            if (error.Length > 0) EditorHud.ShowMessage("Texture override: " + error, warning: true);
            else EditorHud.ShowMessage(applied > 0 ? $"Texture applied to {applied} mesh(es)." : "Texture override removed.");
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
        /// Right button held: rock the object on camera-relative axes.
        ///
        /// Works in every camera. The RTS camera reports its drag through the scene layer, which is
        /// where its cursor lives; the player-attached cameras have no cursor at all, so the button
        /// and the mouse movement are read from the raw input device instead. Reading only the RTS
        /// path - which is what this did - meant the gesture did nothing in first and third person.
        ///
        /// The axes come from the camera either way, so a drag always matches what is on screen.
        /// </summary>
        private void HandleRotateDrag() {
            RtsCameraView? camera = RtsCameraView.Instance;
            if (camera == null) return;

            bool dragging;
            float dragX, dragY;

            if (CameraModes.Current == EditorCameraMode.Rts) {
                dragging = camera.IsRotateDragging;
                dragX = camera.SceneMouseMoveX;
                dragY = camera.SceneMouseMoveY;
                // A visible RTS cursor normally reports deltas through the scene layer. If an
                // overlay temporarily owns that layer, MissionLogic's raw path remains live.
                if (MathF.Abs(dragX) < 0.0001f) dragX = Input.MouseMoveX;
                if (MathF.Abs(dragY) < 0.0001f) dragY = Input.MouseMoveY;
            } else {
                // Raw input: the scene layer is restricted while editing in these cameras, so its
                // own mouse readings are suppressed along with the attack they would have caused.
                dragging = Input.IsKeyDown(Keys.RotateDrag);
                dragX = Input.MouseMoveX;
                dragY = Input.MouseMoveY;
            }

            if (!dragging) return;

            // A transform target has a live side panel. Do not steal RMB from camera navigation
            // with a drag path that proved unreliable across Bannerlord's camera modes.
            if (_transformTarget != null) return;

            if (MathF.Abs(dragX) > 0.0001f) {
                Vec3 rollAxis = camera.CameraForwardHorizontal;
                _ghostRotation.RotateAboutAnArbitraryVector(in rollAxis, dragX * RotateDragSensitivity);
            }
            if (MathF.Abs(dragY) > 0.0001f) {
                Vec3 tiltAxis = camera.CameraRightHorizontal;
                _ghostRotation.RotateAboutAnArbitraryVector(in tiltAxis, dragY * RotateDragSensitivity);
            }
            // No early return by design: placing on the same frame as a drag should still work.
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
            UpdateTransform(entity, position, rotation, entity?.Scale ?? new Vec3(1f, 1f, 1f));
        }

        /// <summary>Sets position, pure rotation, and root-local scale as one atomic transform.</summary>
        public void UpdateTransform(PlacedEntity entity, Vec3 position, Mat3 rotation, Vec3 scale) {
            if (entity == null) return;

            entity.Position = position;
            entity.Rotation = rotation;
            entity.Scale = scale;

            if (entity.SceneEntity != null) {
                try {
                    MatrixFrame frame = MatrixFrame.Identity;
                    frame.rotation = rotation;
                    frame.rotation.ApplyScaleLocal(scale);
                    frame.origin = position;
                    entity.SceneEntity.SetGlobalFrame(in frame, true);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(SceneEditingMissionLogic),
                        $"UpdateTransform failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            RefreshNavMeshCutout(entity);
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

                UI.ExportDialogView? dialog = UI.ExportDialogView.Instance;
                if (dialog == null) return;

                // A template exported here is placeable straight away, so the palette has to be
                // rebuilt when the dialog closes - otherwise it only appears next time the scene is
                // opened. The current category and object are put back: exporting must not change
                // what you were building.
                dialog.OnClosed = () => {
                    string category = _cycleLabel;
                    string? selected = CurrentPlaceable?.PrefabName;
                    Catalog.PackCatalog.Invalidate();
                    BuildPalette();
                    RestoreSelection(category, selected);
                };
                dialog.Open(projectTarget.Project);
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

            // Choosing an asset ends any transform in progress.
            //
            // This used to be missed because the mode was assigned directly rather than going
            // through SetEditMode, which is where the panel is normally closed. That left a live
            // _transformTarget behind, and HandleRotateDrag refuses to rotate the ghost while one
            // exists - so picking an asset while transforming something silently killed right-mouse
            // rotation for the rest of the session, with a stale panel still ticking and claiming
            // mouse input over its own rectangle.
            //
            // Done here rather than by calling SetEditMode, which also refuses to switch while
            // carrying something; that is a different rule and not one to change in passing. It is
            // unconditional, even when already in build mode: the panel outlives the mode, and it
            // is the TARGET that gates rotation.
            if (_transformTarget != null) {
                UI.TransformPanelView.Instance?.Close();
                _transformTarget = null;
            }

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

            // Said on selection, not only when the click fails. A prefab the game has not loaded yet
            // shows no preview, and silence there reads as the editor being broken.
            if (p != null && p.RequiresRestart) {
                EditorHud.ShowMessage(
                    $"'{p.DisplayName}' is not loaded yet - restart the game to place it.", warning: true);
            }
        }

        /// <summary>Selects an editor tool from the on-screen toolbar.</summary>
        public void SelectEditMode(EditMode mode) {
            if (mode == EditMode.Off) return;
            SetEditMode(mode);
        }

        private void CycleEditMode() {
            SetEditMode((EditMode)(((int)_mode + 1) % 10));
        }

        private void SetEditMode(EditMode mode) {
            if (_carried != null) {
                EditorHud.ShowMessage("Place what you are carrying before switching mode.", warning: true);
                return;
            }

            if (_transformTarget != null) UI.TransformPanelView.Instance?.Close();
            _transformTarget = null;
            _mode = mode;
            RemoveGhost();
            _selectedNavRampPoint = -1;
            _selectedPolygonCutoutPoint = -1;

            // The camera follows the edit mode unless the player has picked one themselves: RTS for
            // editing, third person for walking around. Turning editing on is the moment an overhead
            // view starts being useful, and turning it off is the moment it stops.
            CameraModes.FollowEditMode(_mode != EditMode.Off);
            WeaponSheather.SetEditing(_mode != EditMode.Off);

            switch (_mode) {
                case EditMode.Off:
                    EditorHud.ShowMessage(
                        $"Editing off. {Keys.Describe(Keys.CameraMode)}: free camera. " +
                        $"{Keys.Describe(Keys.Outliner)}: scene contents.");
                    break;
                case EditMode.Build:
                    EditorHud.ShowMessage(
                        $"Build mode. {Keys.Describe(Keys.Place)} (or {Keys.Describe(Keys.PlaceAlt)}): place. " +
                        $"{Keys.Describe(Keys.RotateTurnLeft)}/{Keys.Describe(Keys.RotateTurnRight)} or hold " +
                        $"{Keys.Describe(Keys.RotateDrag)}: rotate. " +
                        $"{Keys.Describe(Keys.ResetRotation)}: reset. " +
                        "Mouse wheel: height; hold Left Shift for fine adjustment. " +
                        $"{Keys.Describe(Keys.SnapToGround)}: drop to ground. " +
                        $"{Keys.Describe(Keys.ToggleGroundLock)}: ground follow. " +
                        $"{Keys.Describe(Keys.AlignToSurface)}: align flush to aimed surface. " +
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
                    EditorHud.ShowMessage(
                        $"Move mode. {Keys.Describe(Keys.Place)}: pick up / put down. " +
                        $"{Keys.Describe(Keys.Duplicate)}: duplicate aimed object.");
                    break;
                case EditMode.Script:
                    EditorHud.ShowMessage(
                        $"Script mode. {Keys.Describe(Keys.Place)}: open the scripts on an object.");
                    break;
                case EditMode.NavMesh:
                    NavMeshFaceVisualizer.Clear();
                    NavMeshSpatialIndex.Reset();
                    _navPointA = null;
                    _navPointB = null;
                    _navRoute = null;
                    EditorHud.ShowMessage(
                        $"Navmesh diagnostics (read only). Aim to inspect face/group/island. " +
                        $"{Keys.Describe(Keys.Place)}: set route point A, then B; a third click starts over.");
                    break;
                case EditMode.NavCutout:
                    NavMeshSpatialIndex.Reset();
                    EditorHud.ShowMessage(
                        $"Navmesh cutout authoring. Aim at a placed solid object and press " +
                        $"{Keys.Describe(Keys.Place)} to add/remove its padded footprint. " +
                        "This previews and exports the request; it does not alter navmesh.bin yet.");
                    break;
                case EditMode.NavRequired:
                    NavMeshSpatialIndex.Reset();
                    EditorHud.ShowMessage(
                        $"Add navmesh area. Aim at unmeshed ground and press " +
                        $"{Keys.Describe(Keys.Place)} to mark a 4 m area that needs walkable navmesh; " +
                        "aim at a marked center to remove it. Marks are saved with the project and " +
                        "drive the navmesh addition pass.");
                    break;
                case EditMode.NavRamp:
                    NavMeshSpatialIndex.Reset();
                    EditorHud.ShowMessage(
                        $"Elevated navmesh. Click the physical corners of one stair, ramp, bridge deck, or wall-walk " +
                        $"in perimeter order, then press {Keys.Describe(Keys.PlaceAlt)} to close it. Click a saved dot " +
                        "to select it; click again to move it; Delete removes it. After closing, click empty ground " +
                        "to start another elevated area. While tracing, hold Left Shift and click near a finished area corner to snap to its exact height and position.");
                    break;
                case EditMode.NavPolygonCutout:
                    NavMeshSpatialIndex.Reset();
                    EditorHud.ShowMessage(
                        $"Draw cutout area. Click perimeter corners on the physical surface, then press " +
                        $"{Keys.Describe(Keys.PlaceAlt)} to close it. Red nodes and edges are removed " +
                        "from navmesh only at the clicked elevation. Click a saved node to move it; Delete removes it.");
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
                            ? $"{_cycleLabel}  {_placeableIndex + 1}/{_cycleSet.Count}   {Keys.Describe(Keys.AlignToSurface)} align to surface   {Keys.Describe(Keys.AssetPicker)} assets"
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
                    if (_transformTarget != null) {
                        status.Set("TRANSFORM",
                            Placeable.ToDisplayName(_transformTarget.PrefabName),
                            "Use the right-side numeric controls. Done or Escape leaves transform.",
                            UI.StatusTone.Move);
                    } else if (_carried != null) {
                        status.Set("MOVE - carrying",
                            Placeable.ToDisplayName(_carried.PrefabName),
                            $"{Keys.Describe(Keys.Place)} to put down   {Keys.Describe(Keys.RotateDrag)} drag to rotate",
                            UI.StatusTone.Move);
                    } else {
                        PlacedEntity? target = _hovered;
                        status.Set("MOVE",
                            target != null ? Placeable.ToDisplayName(target.PrefabName) : "(nothing under cursor)",
                            target != null
                                ? $"{Keys.Describe(Keys.Place)} to pick up   {Keys.Describe(Keys.Duplicate)} to duplicate"
                                : $"Aim at one of your placed objects: {Keys.Describe(Keys.Place)} pick up, {Keys.Describe(Keys.Duplicate)} duplicate",
                            UI.StatusTone.Move);
                    }
                    break;
                }

                case EditMode.NavMesh: {
                    NavMeshProbe cursor = NavMeshDiagnostics.Probe(Mission.Scene, _positionLookingAt);
                    string cursorText = cursor.IsValid
                        ? $"Cursor: {cursor.FaceSummary}   surface/nav ΔZ {cursor.VerticalDelta:0.00} m"
                        : $"Cursor: {cursor.Note}{NearestNavMeshStatus()}";

                    string endpoints =
                        $"A: {(_navPointA?.PointSummary ?? "unset")}   " +
                        $"B: {(_navPointB?.PointSummary ?? "unset")}";

                    string routeText;
                    if (_navRoute == null) {
                        routeText = _navPointA == null
                            ? $"{Keys.Describe(Keys.Place)} to set A"
                            : $"{Keys.Describe(Keys.Place)} to set B";
                    } else if (!string.IsNullOrEmpty(_navRoute.Error)) {
                        routeText = _navRoute.Error;
                    } else if (!_navRoute.Reachable) {
                        routeText = "UNREACHABLE for a 0.4 m infantry radius";
                    } else if (!_navRoute.HasDistance) {
                        routeText = "Reachable; engine did not return a path distance";
                    } else {
                        routeText =
                            $"Reachable   straight {_navRoute.StraightDistance:0.0} m   " +
                            $"path {_navRoute.PathDistance:0.0} m   detour {_navRoute.DetourRatio:0.00}x   " +
                            $"{(_navRoute.RequiresRedirect ? "must go around" : "straight route")}   " +
                            $"line clear {(_navRoute.Direct ? "yes" : "no")}";
                    }

                    status.Set("NAVMESH - READ ONLY", cursorText, endpoints, UI.StatusTone.NavMesh,
                        routeText + "   •   approximated face boundary shown at cursor");
                    break;
                }

                case EditMode.NavCutout: {
                    PlacedEntity? target = _hovered;
                    ProjectNavMeshCutout? existing = target == null ? null : FindNavMeshCutout(target.Id);
                    string detail;
                    if (target == null) {
                        detail = "Aim at an object placed by this editor";
                    } else if (existing == null) {
                        detail = $"{Keys.Describe(Keys.Place)} to mark its padded footprint";
                    } else {
                        detail = $"{existing.FaceIndices.Count} sampled face(s), " +
                                 $"{existing.FaceGroups.Count} group(s)   {Keys.Describe(Keys.Place)} to remove";
                    }
                    string nearest = NavMeshDiagnostics.Probe(Mission.Scene, _positionLookingAt).IsValid
                        ? "Cursor is on existing navmesh"
                        : "Cursor has no navmesh" + NearestNavMeshStatus();
                    status.Set("NAVMESH CUTOUT PLAN",
                        target != null ? Placeable.ToDisplayName(target.PrefabName) : "(nothing under cursor)",
                        detail + "   •   " + nearest, UI.StatusTone.NavMesh,
                        "PLAN ONLY: records affected faces; does not change AI routing or navmesh.bin.");
                    break;
                }

                case EditMode.NavRequired: {
                    SceneProject? project = Project;
                    ProjectNavMeshRequirement? existing = FindNavMeshRequirement(_positionLookingAt);
                    NavMeshProbe cursor = NavMeshDiagnostics.Probe(Mission.Scene, _positionLookingAt);
                    string primary = existing != null
                        ? existing.Label
                        : cursor.IsValid ? "Already covered by navmesh" : "Unmeshed ground";
                    string detail = existing != null
                        ? $"{Keys.Describe(Keys.Place)} to remove this requirement"
                        : cursor.IsValid
                            ? "Move the cursor outside the green navmesh"
                            : $"{Keys.Describe(Keys.Place)} to add a 4 m navmesh-needed area";
                    int count = project?.NavMeshRequirements?.Count ?? 0;
                    string nearest = cursor.IsValid
                        ? cursor.FaceSummary
                        : "No navmesh here" + NearestNavMeshStatus();
                    status.Set("ADD NAVMESH AREA", primary,
                        detail + $"   •   {count} saved note(s)", UI.StatusTone.NavMesh,
                        nearest + "   •   orange ring = navmesh must be added here");
                    break;
                }

                case EditMode.NavRamp: {
                    SceneProject? project = Project;
                    ProjectNavMeshRamp? active = _activeNavRamp;
                    int outlinePoints = active == null ? 0 : NavMeshRampAuthoring.PointCount(active.Outline);
                    ProjectNavMeshRamp? nearby = FindNavMeshRamp(_positionLookingAt);
                    string primary = active != null
                        ? $"{active.Label}: {outlinePoints} perimeter corners" +
                          (_selectedNavRampPoint >= 0 ? " • point selected" : "")
                        : nearby != null ? nearby.Label : "Elevated ramp, stairs, or bridge";
                    string detail = _selectedNavRampPoint >= 0
                        ? $"{Keys.Describe(Keys.Place)} moves selected corner • Delete removes it"
                        : active == null
                            ? $"{Keys.Describe(Keys.Place)} starts a perimeter outline"
                            : $"{Keys.Describe(Keys.Place)} adds the next perimeter corner";
                    string finish = active != null
                        ? $"{Keys.Describe(Keys.PlaceAlt)} closes with 4+ corners; then {Keys.Describe(Keys.Place)} starts another"
                        : nearby != null
                            ? $"Click a blue dot to select it"
                            : $"{Keys.Describe(Keys.Place)} on the physical surface starts a new area";
                    status.Set("ADD ELEVATED NAVMESH", primary,
                        detail + $"   •   {project?.NavMeshRamps?.Count ?? 0} saved ramp(s)",
                        UI.StatusTone.NavMesh,
                        finish + "   •   cyan cross = next corner's exact physical hit height");
                    break;
                }

                case EditMode.NavPolygonCutout: {
                    SceneProject? project = Project;
                    ProjectNavMeshCutout? active = _activePolygonCutout;
                    int points = active == null ? 0 : NavMeshPolygonCutoutAuthoring.PointCount(active);
                    ProjectNavMeshCutout? nearby = FindPolygonCutout(_positionLookingAt);
                    string primary = active != null
                        ? $"{active.Label}: {points} perimeter corners" +
                          (_selectedPolygonCutoutPoint >= 0 ? " • point selected" : "")
                        : nearby?.Label ?? "Node-drawn navmesh hole";
                    string detail = _selectedPolygonCutoutPoint >= 0
                        ? $"{Keys.Describe(Keys.Place)} moves selected corner • Delete removes it"
                        : active?.IsDraft == true
                            ? $"{Keys.Describe(Keys.Place)} adds the next perimeter corner"
                            : $"{Keys.Describe(Keys.Place)} starts a red perimeter outline";
                    string finish = active?.IsDraft == true
                        ? $"{Keys.Describe(Keys.PlaceAlt)} closes with 3+ corners"
                        : nearby != null
                            ? "Click a red dot to select it"
                            : "Click the physical surface around the area AI must not enter";
                    int count = project?.NavMeshCutouts?.Count(c => c.IsFreeform) ?? 0;
                    status.Set("DRAW CUTOUT AREA", primary,
                        detail + $"   •   {count} saved drawn cutout(s)", UI.StatusTone.NavMesh,
                        finish + "   •   red cross = next corner and its exact elevation");
                    break;
                }
            }
        }

        private ProjectNavMeshRequirement? FindNavMeshRequirement(Vec3 position) {
            if (!position.IsValid || Project?.NavMeshRequirements == null) return null;
            ProjectNavMeshRequirement? nearest = null;
            float best = float.MaxValue;
            foreach (ProjectNavMeshRequirement requirement in Project.NavMeshRequirements) {
                Vec3 notePosition = NavMeshRequirementAuthoring.Position(requirement);
                if (!notePosition.IsValid) continue;
                float squared = (notePosition.AsVec2 - position.AsVec2).LengthSquared;
                float pickRadius = Math.Max(1.25f, requirement.Radius * 0.35f);
                if (squared <= pickRadius * pickRadius && squared < best) {
                    best = squared;
                    nearest = requirement;
                }
            }
            return nearest;
        }

        private void ToggleNavMeshRequirement() {
            SceneProject? project = Project;
            if (project == null) {
                EditorHud.ShowMessage("Navmesh notes are only available in saved editor projects.", warning: true);
                return;
            }
            project.NavMeshRequirements ??= new List<ProjectNavMeshRequirement>();

            ProjectNavMeshRequirement? existing = FindNavMeshRequirement(_positionLookingAt);
            if (existing != null) {
                project.NavMeshRequirements.Remove(existing);
                _isDirty = true;
                EditorHud.ShowMessage("Removed navmesh-needed area.");
                return;
            }
            if (!_positionLookingAt.IsValid) {
                EditorHud.ShowMessage("Aim at terrain where navmesh is needed.", warning: true);
                return;
            }

            NavMeshProbe cursor = NavMeshDiagnostics.Probe(Mission.Scene, _positionLookingAt);
            if (cursor.IsValid) {
                EditorHud.ShowMessage(
                    $"That position already has navmesh ({cursor.FaceSummary}); no requirement was added.",
                    warning: true);
                return;
            }

            ProjectNavMeshRequirement requirement =
                NavMeshRequirementAuthoring.Create(Mission.Scene, _positionLookingAt);
            if (NavMeshSpatialIndex.TryFindNearest(_positionLookingAt,
                    out int faceIndex, out _, out float distance)) {
                requirement.NearestFaceIndex = faceIndex;
                requirement.NearestFaceDistance = distance;
            }
            project.NavMeshRequirements.Add(requirement);
            _isDirty = true;
            string distanceText = requirement.NearestFaceDistance >= 0f
                ? $" Nearest existing face is {requirement.NearestFaceDistance:0.0} m away."
                : "";
            EditorHud.ShowMessage("Added a 4 m navmesh-needed area." + distanceText);
        }

        private void ToggleNavMeshCutout() {
            SceneProject? project = Project;
            if (project == null) {
                EditorHud.ShowMessage("Navmesh cutouts are only available in saved editor projects.", warning: true);
                return;
            }
            if (_hovered == null || _hovered.SceneEntity == null) {
                EditorHud.ShowMessage("Aim at an object placed by this editor.", warning: true);
                return;
            }
            if (string.IsNullOrEmpty(_hovered.Id)) _hovered.Id = Guid.NewGuid().ToString("B").ToUpperInvariant();

            ProjectNavMeshCutout? existing = FindNavMeshCutout(_hovered.Id);
            if (existing != null) {
                int removed = project.NavMeshCutouts.RemoveAll(c =>
                    string.Equals(c.EntityId, _hovered.Id, StringComparison.OrdinalIgnoreCase));
                _isDirty = true;
                EditorHud.ShowMessage($"Removed {removed} navmesh cutout area(s) for " +
                                      $"{Placeable.ToDisplayName(_hovered.PrefabName)}.");
                return;
            }

            try {
                ProjectNavMeshCutout cutout = NavMeshCutoutAuthoring.Create(Mission.Scene, _hovered);
                project.NavMeshCutouts.Add(cutout);
                _isDirty = true;
                EditorHud.ShowMessage(
                    $"Marked {Placeable.ToDisplayName(_hovered.PrefabName)}: " +
                    $"{cutout.FaceIndices.Count} navmesh face(s) sampled beneath its footprint.",
                    warning: cutout.FaceIndices.Count == 0);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneEditingMissionLogic), "Cutout authoring failed", ex);
                EditorHud.ShowMessage($"Could not measure that object's footprint: {ex.Message}", warning: true);
            }
        }

        private void AddNavMeshRampPoint() {
            if (Project == null) {
                EditorHud.ShowMessage("Elevated navmesh ramps are only available in saved editor projects.", warning: true);
                return;
            }
            if (!_positionLookingAt.IsValid) {
                EditorHud.ShowMessage("Aim at the physical ramp, stair, bridge, or wall-walk surface.", warning: true);
                return;
            }

            if (_selectedNavRampPoint >= 0 && _activeNavRamp != null) {
                if (NavMeshRampAuthoring.HasOutline(_activeNavRamp))
                    NavMeshRampAuthoring.SetOutlinePoint(_activeNavRamp, _selectedNavRampPoint, _positionLookingAt);
                else
                    NavMeshRampAuthoring.SetPoint(_activeNavRamp, _selectedNavRampLeft,
                        _selectedNavRampPoint, _positionLookingAt);
                _isDirty = true;
                EditorHud.ShowMessage("Moved the selected ramp point. Click another dot to select it.");
                _selectedNavRampPoint = -1;
                return;
            }

            // An outline that is currently being traced owns point picking.  Once an outline has
            // been closed, however, it is no longer the active editing target: the next click may
            // select a node from *any* finished outline.  This is important where a ramp and a
            // landing intentionally share a physical corner.
            ProjectNavMeshRamp? selectionScope = _activeNavRamp?.IsDraft == true
                ? _activeNavRamp
                : null;
            if (TryFindNavMeshRampPoint(_positionLookingAt, selectionScope,
                    out ProjectNavMeshRamp? hit, out bool left, out int index)) {
                _activeNavRamp = hit;
                _selectedNavRampLeft = left;
                _selectedNavRampPoint = index;
                EditorHud.ShowMessage($"Selected elevated outline corner {index + 1}. " +
                                      $"Click its new position to move it.");
                return;
            }

            SceneProject project = Project;
            project.NavMeshRamps ??= new List<ProjectNavMeshRamp>();
            // New authoring is a perimeter, not a pair of rails. Legacy two-rail records remain
            // readable and bakeable, but never capture a newly clicked outline.
            if (_activeNavRamp == null || !_activeNavRamp.IsDraft
                || !project.NavMeshRamps.Contains(_activeNavRamp)
                || !NavMeshRampAuthoring.HasOutline(_activeNavRamp)) {
                _activeNavRamp = new ProjectNavMeshRamp {
                    Label = $"Elevated area {project.NavMeshRamps.Count + 1}", IsDraft = true,
                };
                project.NavMeshRamps.Add(_activeNavRamp);
            }
            // A ramp commonly meets a platform or wall-walk at a shared corner.  Holding Left Shift
            // while clicking near that existing corner stores exactly the other area's physical
            // X/Y/Z, without requiring pixel-perfect placement.  The other area is never selected
            // or edited by this action.
            //
            // This is deliberately opt-in rather than automatic, because snapping is only ever
            // correct when BOTH ends of an edge meet the other outline's matching corners: the
            // baker then welds the two lips onto one shared edge.  Where only one corner lines up,
            // the stair lip is landing part-way along a deck edge, and the baker bridges it with a
            // two-triangle strip that must be at least twice the minimum face area - so closing
            // that gap silently costs the ramp.  The author knows which case they are in; the
            // editor cannot tell from one click.
            Vec3 pointToAppend = _positionLookingAt;
            bool snapRequested = Input.IsKeyDown(InputKey.LeftShift);
            bool snappedToOtherArea = false;
            ProjectNavMeshRamp? sharedRamp = null;
            int sharedIndex = -1;
            if (snapRequested) {
                snappedToOtherArea = TryFindExternalNavMeshRampPoint(
                    _positionLookingAt, _activeNavRamp, out Vec3 sharedPoint, out sharedRamp, out sharedIndex);
                if (snappedToOtherArea) pointToAppend = sharedPoint;
            }

            NavMeshRampAuthoring.AppendOutlinePoint(_activeNavRamp, pointToAppend);
            _isDirty = true;
            int number = NavMeshRampAuthoring.PointCount(_activeNavRamp.Outline);
            EditorHud.ShowMessage(snappedToOtherArea
                ? $"Added outline corner {number}, snapped to {sharedRamp!.Label} corner {sharedIndex + 1}."
                : $"Added outline corner {number}. Trace the physical edge clockwise or counter-clockwise."
                  + (snapRequested ? " No finished corner was close enough to snap to." : ""));
        }

        private void DeleteSelectedNavMeshRampPoint() {
            if (_activeNavRamp == null || _selectedNavRampPoint < 0) return;

            int index = _selectedNavRampPoint;
            bool outline = NavMeshRampAuthoring.HasOutline(_activeNavRamp);
            bool left = _selectedNavRampLeft;
            bool removed = outline
                ? NavMeshRampAuthoring.RemoveOutlinePoint(_activeNavRamp, index)
                : NavMeshRampAuthoring.RemovePoint(_activeNavRamp, left, index);
            if (!removed) {
                EditorHud.ShowMessage("That ramp point could not be removed.", warning: true);
                return;
            }

            // Removing a point from one rail normally leaves the pair counts unequal. Keep the
            // strip and its remaining geometry visible, but make its draft state honest so it
            // cannot accidentally enter a bake until the author restores a valid pair layout.
            if (outline || !NavMeshRampAuthoring.IsUsable(_activeNavRamp)) _activeNavRamp.IsDraft = true;
            _activeNavRamp.EditingRail = left ? 0 : 1;
            _selectedNavRampPoint = -1;
            _isDirty = true;

            int remaining = NavMeshRampAuthoring.PointCount(outline
                ? _activeNavRamp.Outline : left ? _activeNavRamp.Left : _activeNavRamp.Right);
            EditorHud.ShowMessage(outline
                ? $"Removed outline corner {index + 1}. Keep at least four corners, then press F to close."
                : $"Removed legacy {(left ? "left" : "right")} rail point {index + 1}. {remaining} remain on that rail.");
        }

        private void AdvanceOrFinishNavMeshRamp() {
            SceneProject? project = Project;
            if (project == null) {
                EditorHud.ShowMessage("Elevated navmesh ramps are only available in saved editor projects.", warning: true);
                return;
            }
            project.NavMeshRamps ??= new List<ProjectNavMeshRamp>();

            if (_selectedNavRampPoint >= 0) {
                _selectedNavRampPoint = -1;
                EditorHud.ShowMessage("Ramp point selection cleared.");
                return;
            }

            if (_activeNavRamp == null || !project.NavMeshRamps.Contains(_activeNavRamp)) {
                EditorHud.ShowMessage("No elevated area is being traced. Click a saved dot to edit it, or an empty surface to start another area.");
                return;
            }

            if (!_activeNavRamp.IsDraft) {
                _activeNavRamp = null;
                EditorHud.ShowMessage("Elevated area selection cleared. Click any saved dot to edit it, or an empty surface to start another area.");
                return;
            }

            if (!NavMeshRampAuthoring.HasOutline(_activeNavRamp)) {
                EditorHud.ShowMessage("The current elevated area has no perimeter outline yet. Click its physical corners first.", warning: true);
                return;
            }

            int outlineCount = NavMeshRampAuthoring.PointCount(_activeNavRamp.Outline);
            if (outlineCount < 4) {
                EditorHud.ShowMessage($"An elevated area needs at least four perimeter corners (you have {outlineCount}).", warning: true);
                return;
            }
            _activeNavRamp.IsDraft = false;
            _isDirty = true;
            EditorHud.ShowMessage("Closed elevated navmesh area. It will be included in the next bake.");
            _activeNavRamp = null;
        }

        private void AddNavMeshPolygonCutoutPoint() {
            SceneProject? project = Project;
            if (project == null) {
                EditorHud.ShowMessage("Drawn cutouts are only available in saved editor projects.", warning: true);
                return;
            }
            if (!_positionLookingAt.IsValid) {
                EditorHud.ShowMessage("Aim at the physical surface around the area to cut out.", warning: true);
                return;
            }

            if (_selectedPolygonCutoutPoint >= 0 && _activePolygonCutout != null) {
                NavMeshPolygonCutoutAuthoring.SetPoint(
                    _activePolygonCutout, _selectedPolygonCutoutPoint, _positionLookingAt);
                _isDirty = true;
                EditorHud.ShowMessage("Moved the selected cutout corner. Click another red dot to select it.");
                _selectedPolygonCutoutPoint = -1;
                return;
            }

            ProjectNavMeshCutout? selectionScope = _activePolygonCutout?.IsDraft == true
                ? _activePolygonCutout
                : null;
            if (TryFindPolygonCutoutPoint(_positionLookingAt, selectionScope,
                    out ProjectNavMeshCutout? hit, out int index)) {
                _activePolygonCutout = hit;
                _selectedPolygonCutoutPoint = index;
                EditorHud.ShowMessage($"Selected cutout corner {index + 1}. Click its new position to move it.");
                return;
            }

            project.NavMeshCutouts ??= new List<ProjectNavMeshCutout>();
            if (_activePolygonCutout == null || !_activePolygonCutout.IsDraft
                || !project.NavMeshCutouts.Contains(_activePolygonCutout)) {
                int number = project.NavMeshCutouts.Count(c => c.IsFreeform) + 1;
                _activePolygonCutout = new ProjectNavMeshCutout {
                    Label = $"Drawn cutout {number}",
                    Prefab = "Drawn cutout",
                    IsFreeform = true,
                    IsDraft = true,
                    Corners = Array.Empty<float>(),
                };
                project.NavMeshCutouts.Add(_activePolygonCutout);
            }

            NavMeshPolygonCutoutAuthoring.AppendPoint(_activePolygonCutout, _positionLookingAt);
            _isDirty = true;
            int pointNumber = NavMeshPolygonCutoutAuthoring.PointCount(_activePolygonCutout);
            EditorHud.ShowMessage($"Added red cutout corner {pointNumber}. Continue around the perimeter, then press F.");
        }

        private void DeleteSelectedPolygonCutoutPoint() {
            if (_activePolygonCutout == null || _selectedPolygonCutoutPoint < 0) return;
            int index = _selectedPolygonCutoutPoint;
            if (!NavMeshPolygonCutoutAuthoring.RemovePoint(_activePolygonCutout, index)) {
                EditorHud.ShowMessage("That cutout corner could not be removed.", warning: true);
                return;
            }
            _activePolygonCutout.IsDraft = true;
            _selectedPolygonCutoutPoint = -1;
            _isDirty = true;
            int remaining = NavMeshPolygonCutoutAuthoring.PointCount(_activePolygonCutout);
            EditorHud.ShowMessage($"Removed cutout corner {index + 1}. Keep at least three corners, then press F to close.");
        }

        private void FinishNavMeshPolygonCutout() {
            SceneProject? project = Project;
            if (project == null) return;
            if (_selectedPolygonCutoutPoint >= 0) {
                _selectedPolygonCutoutPoint = -1;
                EditorHud.ShowMessage("Cutout point selection cleared.");
                return;
            }
            if (_activePolygonCutout == null || !project.NavMeshCutouts.Contains(_activePolygonCutout)) {
                EditorHud.ShowMessage("No cutout is being traced. Click a saved red dot to edit it, or empty ground to start another.");
                return;
            }
            if (!_activePolygonCutout.IsDraft) {
                _activePolygonCutout = null;
                EditorHud.ShowMessage("Cutout selection cleared. Click a red dot to edit it, or empty ground to start another.");
                return;
            }
            int count = NavMeshPolygonCutoutAuthoring.PointCount(_activePolygonCutout);
            if (count < 3) {
                EditorHud.ShowMessage($"A cutout needs at least three perimeter corners (you have {count}).", warning: true);
                return;
            }
            _activePolygonCutout.IsDraft = false;
            _isDirty = true;
            EditorHud.ShowMessage("Closed the red cutout area. It will be included in the next bake.");
            _activePolygonCutout = null;
        }

        private ProjectNavMeshCutout? FindPolygonCutout(Vec3 position) {
            if (!position.IsValid || Project?.NavMeshCutouts == null) return null;
            ProjectNavMeshCutout? nearest = null;
            float best = 2f * 2f;
            foreach (ProjectNavMeshCutout cutout in Project.NavMeshCutouts) {
                if (!cutout.IsFreeform) continue;
                float distance = NavMeshPolygonCutoutAuthoring.DistanceSquaredTo(cutout, position);
                if (distance >= best) continue;
                best = distance;
                nearest = cutout;
            }
            return nearest;
        }

        private bool TryFindPolygonCutoutPoint(Vec3 position, ProjectNavMeshCutout? scope,
                                                out ProjectNavMeshCutout? cutout, out int index) {
            cutout = null;
            index = -1;
            if (!position.IsValid || Project?.NavMeshCutouts == null) return false;
            float best = 1.25f * 1.25f;
            foreach (ProjectNavMeshCutout candidate in Project.NavMeshCutouts) {
                if (!candidate.IsFreeform || (scope != null && !ReferenceEquals(candidate, scope))) continue;
                int count = NavMeshPolygonCutoutAuthoring.PointCount(candidate);
                for (int i = 0; i < count; i++) {
                    float distance = (NavMeshPolygonCutoutAuthoring.Point(candidate, i) - position).LengthSquared;
                    if (distance >= best) continue;
                    best = distance;
                    cutout = candidate;
                    index = i;
                }
            }
            return cutout != null;
        }

        private ProjectNavMeshRamp? FindNavMeshRamp(Vec3 position) {
            if (!position.IsValid || Project?.NavMeshRamps == null) return null;
            ProjectNavMeshRamp? nearest = null;
            float best = 2.0f * 2.0f;
            foreach (ProjectNavMeshRamp ramp in Project.NavMeshRamps) {
                float distance = NavMeshRampAuthoring.DistanceSquaredTo(ramp, position);
                if (distance < best) {
                    best = distance;
                    nearest = ramp;
                }
            }
            return nearest;
        }

        /// <summary>
        /// Finds a saved elevated point. Passing an active area scopes selection to that area only;
        /// passing null is the deliberate "nothing open, choose an existing area" interaction.
        /// </summary>
        private bool TryFindNavMeshRampPoint(Vec3 position, ProjectNavMeshRamp? scope,
                                             out ProjectNavMeshRamp? ramp, out bool left, out int index) {
            ramp = null; left = false; index = -1;
            if (!position.IsValid || Project?.NavMeshRamps == null) return false;
            float best = 1.25f * 1.25f;
            foreach (ProjectNavMeshRamp candidate in Project.NavMeshRamps) {
                if (scope != null && !ReferenceEquals(candidate, scope)) continue;
                if (NavMeshRampAuthoring.HasOutline(candidate)) {
                    int outlineCount = NavMeshRampAuthoring.PointCount(candidate.Outline);
                    for (int i = 0; i < outlineCount; i++) {
                        float distance = (NavMeshRampAuthoring.Point(candidate.Outline, i) - position).LengthSquared;
                        if (distance >= best) continue;
                        best = distance; ramp = candidate; left = true; index = i;
                    }
                    continue;
                }
                foreach (bool candidateLeft in new[] { true, false }) {
                    float[] rail = candidateLeft ? candidate.Left : candidate.Right;
                    int count = NavMeshRampAuthoring.PointCount(rail);
                    for (int i = 0; i < count; i++) {
                        float distance = (NavMeshRampAuthoring.Point(rail, i) - position).LengthSquared;
                        if (distance >= best) continue;
                        best = distance; ramp = candidate; left = candidateLeft; index = i;
                    }
                }
            }
            return ramp != null;
        }

        /// <summary>
        /// Finds a point belonging to another elevated area for shared-corner authoring.  This is
        /// intentionally narrower than normal point selection: it is used only while appending to
        /// a draft and never changes which outline is being edited.
        /// </summary>
        private bool TryFindExternalNavMeshRampPoint(Vec3 position, ProjectNavMeshRamp active,
                                                     out Vec3 point, out ProjectNavMeshRamp? ramp,
                                                     out int index) {
            point = Vec3.Invalid;
            ramp = null;
            index = -1;
            if (!position.IsValid || active == null || Project?.NavMeshRamps == null) return false;

            // A deliberately close, three-dimensional snap radius.  This lets adjacent surface
            // corners join cleanly while avoiding an accidental snap across different levels.
            float best = 0.65f * 0.65f;
            foreach (ProjectNavMeshRamp candidate in Project.NavMeshRamps) {
                if (ReferenceEquals(candidate, active) || candidate.IsDraft) continue;
                if (NavMeshRampAuthoring.HasOutline(candidate)) {
                    int count = NavMeshRampAuthoring.PointCount(candidate.Outline);
                    for (int i = 0; i < count; i++) {
                        Vec3 candidatePoint = NavMeshRampAuthoring.Point(candidate.Outline, i);
                        float distance = (candidatePoint - position).LengthSquared;
                        if (distance >= best) continue;
                        best = distance;
                        point = candidatePoint;
                        ramp = candidate;
                        index = i;
                    }
                    continue;
                }

                foreach (float[] rail in new[] { candidate.Left, candidate.Right }) {
                    int count = NavMeshRampAuthoring.PointCount(rail);
                    for (int i = 0; i < count; i++) {
                        Vec3 candidatePoint = NavMeshRampAuthoring.Point(rail, i);
                        float distance = (candidatePoint - position).LengthSquared;
                        if (distance >= best) continue;
                        best = distance;
                        point = candidatePoint;
                        ramp = candidate;
                        index = i;
                    }
                }
            }
            return ramp != null;
        }

        private ProjectNavMeshCutout? FindNavMeshCutout(string entityId) =>
            string.IsNullOrEmpty(entityId)
                ? null
                : Project?.NavMeshCutouts.FirstOrDefault(c =>
                    string.Equals(c.EntityId, entityId, StringComparison.OrdinalIgnoreCase));

        private void RemoveNavMeshCutout(string entityId) {
            ProjectNavMeshCutout? cutout = FindNavMeshCutout(entityId);
            if (cutout != null) Project!.NavMeshCutouts.Remove(cutout);
        }

        private void RefreshNavMeshCutout(PlacedEntity placed) {
            ProjectNavMeshCutout? old = FindNavMeshCutout(placed.Id);
            if (old == null || placed.SceneEntity == null) return;
            try {
                int at = Project!.NavMeshCutouts.IndexOf(old);
                Project.NavMeshCutouts[at] = NavMeshCutoutAuthoring.Create(Mission.Scene, placed);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    $"Could not refresh moved navmesh cutout '{placed.PrefabName}': {ex.Message}");
            }
        }

        private void RenderNavMeshCutouts() {
            SceneProject? project = Project;
            if ((_mode != EditMode.NavCutout && _mode != EditMode.NavPolygonCutout)
                || project == null || project.NavMeshCutouts == null) return;
            string hoveredId = _hovered?.Id ?? "";
            ProjectNavMeshCutout? selected = _mode == EditMode.NavPolygonCutout
                ? FindPolygonCutout(_positionLookingAt)
                : null;
            foreach (ProjectNavMeshCutout cutout in project.NavMeshCutouts) {
                if (cutout.IsFreeform) {
                    if (_mode == EditMode.NavPolygonCutout) {
                        bool active = ReferenceEquals(cutout, _activePolygonCutout);
                        NavMeshPolygonCutoutAuthoring.Render(cutout,
                            active || ReferenceEquals(cutout, selected),
                            active ? _selectedPolygonCutoutPoint : -1);
                    }
                    continue;
                }
                if (_mode == EditMode.NavCutout)
                    NavMeshCutoutAuthoring.Render(cutout,
                        string.Equals(cutout.EntityId, hoveredId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void RenderNavMeshRamps() {
            SceneProject? project = Project;
            if (project?.NavMeshRamps == null) return;
            ProjectNavMeshRamp? selected = _mode == EditMode.NavRamp
                ? FindNavMeshRamp(_positionLookingAt)
                : null;
            foreach (ProjectNavMeshRamp ramp in project.NavMeshRamps) {
                bool active = ReferenceEquals(ramp, _activeNavRamp);
                NavMeshRampAuthoring.Render(ramp, ReferenceEquals(ramp, selected) || active,
                    active && _selectedNavRampLeft, active ? _selectedNavRampPoint : -1);
            }
        }

        // -- in-world transform gizmo -----------------------------------------------------------

        private const float TransformGizmoRadius = 1.45f;
        private const float TransformGizmoThickness = 0.075f;
        private const uint TransformAxisXColor = 0xFFFF3030u;
        private const uint TransformAxisYColor = 0xFF30FF58u;
        private const uint TransformAxisZColor = 0xFF30B8FFu;
        private const uint TransformAxisSelectedColor = 0xFFFFA500u;

        /// <summary>Draws the familiar three rotation rings around the object being transformed.</summary>
        private void RenderTransformGizmo() {
            if (_transformTarget == null || !_live.Contains(_transformTarget)) {
                UI.TransformPanelView.Instance?.Close();
                _transformTarget = null;
                return;
            }

            Vec3 centre = _transformTarget.Position + new Vec3(0f, 0f, 0.18f);
            RenderTransformRing(centre, 0, _transformAxis == 0 ? TransformAxisSelectedColor : TransformAxisXColor);
            RenderTransformRing(centre, 1, _transformAxis == 1 ? TransformAxisSelectedColor : TransformAxisYColor);
            RenderTransformRing(centre, 2, _transformAxis == 2 ? TransformAxisSelectedColor : TransformAxisZColor);
            NavMeshVisualMarkers.Show(centre, TransformAxisSelectedColor, 0.14f);
        }

        private static void RenderTransformRing(Vec3 centre, int axis, uint color) {
            const int steps = 32;
            for (int i = 0; i < steps; i++) {
                float a = MathF.PI * 2f * i / steps;
                float b = MathF.PI * 2f * (i + 1) / steps;
                Vec3 from = TransformRingPoint(centre, axis, a);
                Vec3 to = TransformRingPoint(centre, axis, b);
                NavMeshVisualMarkers.ShowLine(from, to, color, size: TransformGizmoThickness);
            }
        }

        private static Vec3 TransformRingPoint(Vec3 centre, int axis, float angle) {
            float x = MathF.Cos(angle) * TransformGizmoRadius;
            float y = MathF.Sin(angle) * TransformGizmoRadius;
            return axis switch {
                0 => centre + new Vec3(0f, x, y), // X axis: Y/Z plane
                1 => centre + new Vec3(x, 0f, y), // Y axis: X/Z plane
                _ => centre + new Vec3(x, y, 0f), // Z axis: X/Y plane
            };
        }

        /// <summary>
        /// Chooses the visible ring curve nearest the current placement ray. The former ray-plane
        /// intersection works only at favorable camera angles: on an oblique view the visible
        /// curve may not lie close to its mathematical plane intersection. Sampling the rendered
        /// curves instead makes the rings pickable wherever they appear on screen.
        /// </summary>
        private bool TrySelectTransformAxis() {
            if (_transformTarget == null || !_placementRayOrigin.IsValid || !_placementRayDirection.IsValid)
                return false;

            Vec3 centre = _transformTarget.Position + new Vec3(0f, 0f, 0.18f);
            const int samplesPerRing = 96;
            float bestDistanceSquared = 0.48f * 0.48f;
            int bestAxis = -1;
            for (int axis = 0; axis < 3; axis++) {
                for (int sample = 0; sample < samplesPerRing; sample++) {
                    float angle = MathF.PI * 2f * sample / samplesPerRing;
                    Vec3 point = TransformRingPoint(centre, axis, angle);
                    Vec3 toPoint = point - _placementRayOrigin;
                    float alongRay = Vec3.DotProduct(toPoint, _placementRayDirection);
                    if (alongRay < 0f)
                        continue;
                    Vec3 nearestOnRay = _placementRayOrigin + _placementRayDirection * alongRay;
                    float distanceSquared = (point - nearestOnRay).LengthSquared;
                    if (distanceSquared < bestDistanceSquared) {
                        bestDistanceSquared = distanceSquared;
                        bestAxis = axis;
                    }
                }
            }

            if (bestAxis < 0) return false;
            _transformAxis = bestAxis;
            string name = bestAxis == 0 ? "X / pitch" : bestAxis == 1 ? "Y / roll" : "Z / yaw";
            EditorHud.ShowMessage($"{name} rotation selected. Hold RMB and drag to rotate.");
            return true;
        }

        /// <summary>
        /// Build mode has a full prefab ghost, but elevated-navmesh authoring previously left
        /// first/third-person users guessing at the exact physics hit. Draw a compact cyan marker
        /// on that hit with the same durable marker system as the saved route.
        /// </summary>
        private void RenderNavMeshRampPlacementGhost() {
            if (_mode != EditMode.NavRamp || !_positionLookingAt.IsValid) return;

            const uint PreviewColor = 0xFF00FFFFu;
            Vec3 surface = _positionLookingAt;
            Vec3 raised = surface + new Vec3(0f, 0f, 0.07f);
            const float radius = 0.36f;

            // Dot = exact click location; cross = its actual physical surface height.
            NavMeshVisualMarkers.Show(surface + new Vec3(0f, 0f, 0.12f), PreviewColor, size: 0.28f);
            NavMeshVisualMarkers.ShowLine(raised + new Vec3(-radius, 0f, 0f),
                raised + new Vec3(radius, 0f, 0f), PreviewColor, size: 0.07f);
            NavMeshVisualMarkers.ShowLine(raised + new Vec3(0f, -radius, 0f),
                raised + new Vec3(0f, radius, 0f), PreviewColor, size: 0.07f);
        }

        private void RenderNavMeshPolygonCutoutPlacementGhost() {
            if (_mode != EditMode.NavPolygonCutout || !_positionLookingAt.IsValid) return;
            const uint PreviewColor = 0xFFFF3030u;
            Vec3 surface = _positionLookingAt;
            Vec3 raised = surface + new Vec3(0f, 0f, 0.07f);
            const float radius = 0.36f;
            NavMeshVisualMarkers.Show(surface + new Vec3(0f, 0f, 0.12f), PreviewColor, size: 0.28f);
            NavMeshVisualMarkers.ShowLine(raised + new Vec3(-radius, 0f, 0f),
                raised + new Vec3(radius, 0f, 0f), PreviewColor, size: 0.07f);
            NavMeshVisualMarkers.ShowLine(raised + new Vec3(0f, -radius, 0f),
                raised + new Vec3(0f, radius, 0f), PreviewColor, size: 0.07f);
        }

        private void RenderNavMeshVisuals() {
            bool inNavMode = _mode == EditMode.NavMesh || _mode == EditMode.NavCutout
                             || _mode == EditMode.NavRequired || _mode == EditMode.NavRamp
                             || _mode == EditMode.NavPolygonCutout;
            // Audit findings stay visible in every mode. They are the answer to "where is the
            // problem", and hunting for one while also holding the right edit mode is needless.
            if (!inNavMode && !NavMeshAuditMarkers.HasFindings) {
                NavMeshVisualMarkers.HideAll();
                return;
            }

            NavMeshVisualMarkers.Begin(Mission.Scene);

            // Elevated authoring needs an uncluttered view of the surface being traced. Do not mix
            // the stock ground-navmesh face overlay, audit findings, cutouts, or ground-addition
            // notes into this mode; show only the elevated areas the user has authored and the
            // exact physical hit preview for the next point.
            if (_mode == EditMode.NavRamp) {
                RenderNavMeshRamps();
                RenderNavMeshRampPlacementGhost();
                NavMeshVisualMarkers.End();
                return;
            }

            if (_mode == EditMode.NavPolygonCutout) {
                RenderNavMeshCutouts();
                RenderNavMeshPolygonCutoutPlacementGhost();
                NavMeshVisualMarkers.End();
                return;
            }

            NavMeshAuditMarkers.Render(Mission.Scene, Project?.TargetScene ?? "");
            if (!inNavMode) {
                NavMeshVisualMarkers.End();
                return;
            }
            // Persistent notes are the authoring intent and must remain visible even when a dense
            // overview or a large cutout set uses the rest of the bounded marker pool.
            RenderNavMeshRequirements();
            RenderNavMeshCutouts();
            RenderNavMeshRamps();
            RenderNavMeshFaceOverlay();
            // Render last so the immediate click-location preview remains visible above the
            // face/route overlays.
            RenderNavMeshRampPlacementGhost();
            NavMeshVisualMarkers.End();
        }

        private void RenderNavMeshRequirements() {
            if (Project?.NavMeshRequirements == null) return;
            ProjectNavMeshRequirement? selected = _mode == EditMode.NavRequired
                ? FindNavMeshRequirement(_positionLookingAt)
                : null;
            foreach (ProjectNavMeshRequirement requirement in Project.NavMeshRequirements)
                NavMeshRequirementAuthoring.Render(requirement, ReferenceEquals(requirement, selected));
        }

        private void RenderNavMeshFaceOverlay() {
            if (_mode == EditMode.NavMesh || _mode == EditMode.NavCutout
                || _mode == EditMode.NavRequired || _mode == EditMode.NavRamp) {
                NavMeshSpatialIndex.Tick(Mission.Scene);
                NavMeshProbe cursor = NavMeshDiagnostics.Probe(Mission.Scene, _positionLookingAt);
                // An elevated route needs clean sight of its two physical boundaries. The broad
                // green overview is useful for diagnostics/ground additions but overwhelms the
                // orange/cyan ramp rails, so ramp mode keeps only the hover/nearest face.
                if (_mode == EditMode.NavMesh || _mode == EditMode.NavRequired)
                    RenderNearbyNavMeshOverview(cursor.IsValid ? cursor.Face.FaceIndex : -1);
                if (cursor.IsValid) {
                    // Render the hovered face last so its brighter cyan contour remains legible
                    // above the subdued green working-area overview.
                    NavMeshFaceVisualizer.Render(Mission.Scene, cursor.Face.FaceIndex,
                        0xFF00FFFFu, drawSampleNodes: true);
                } else if (NavMeshSpatialIndex.IsReady &&
                           NavMeshSpatialIndex.TryFindNearest(_positionLookingAt,
                               out int nearestFace, out Vec3 nearestCenter, out _)) {
                    NavMeshFaceVisualizer.Render(Mission.Scene, nearestFace,
                        0xFFFF00FFu, drawSampleNodes: true);
                    Vec3 from = _positionLookingAt;
                    from.z += 0.8f;
                    nearestCenter.z += 0.8f;
                    NavMeshVisualMarkers.ShowLine(from, nearestCenter,
                        0xFFFF00FFu, spacing: 1.0f, size: 0.14f, maximumPoints: 96);
                }
                if (_mode == EditMode.NavMesh || _mode == EditMode.NavRequired) return;
            }

            if (_mode != EditMode.NavCutout || Project?.NavMeshCutouts == null) return;
            // The manifest's sampled faces are precisely the region we need to inspect. Showing
            // their reconstructed boundaries makes holes, giant faces, and footprint mismatch
            // visible before any future writer is allowed to touch the binary mesh.
            const int maximumDisplayedFaces = 120;
            int displayed = 0;
            foreach (ProjectNavMeshCutout cutout in Project.NavMeshCutouts) {
                foreach (int faceIndex in cutout.FaceIndices) {
                    NavMeshFaceVisualizer.Render(Mission.Scene, faceIndex,
                        0xFFFFA500u, drawSampleNodes: false);
                    if (++displayed >= maximumDisplayedFaces) return;
                }
            }
        }

        private readonly List<int> _nearbyNavMeshFaces = new();

        private void RenderNearbyNavMeshOverview(int highlightedFace) {
            // Reconstructing every face on a 20k+ face battle map is not safe in a retail mission:
            // every uncached polygon needs dozens of native edge queries. This bounded overview
            // follows the cursor's working area and admits only a few new faces each frame, while
            // already reconstructed faces redraw cheaply from the cache.
            const float overviewRadius = 55f;
            const int maximumOverviewFaces = 72;
            const int maximumNewFacesPerFrame = 4;
            const uint overviewColor = 0xFF35A854u;

            NavMeshSpatialIndex.FindNearestWithin(_positionLookingAt, overviewRadius,
                maximumOverviewFaces, _nearbyNavMeshFaces);
            int reconstructed = 0;
            foreach (int faceIndex in _nearbyNavMeshFaces) {
                if (faceIndex == highlightedFace) continue;
                bool cached = NavMeshFaceVisualizer.IsCached(faceIndex);
                if (!cached && reconstructed >= maximumNewFacesPerFrame) continue;
                NavMeshFaceVisualizer.Render(Mission.Scene, faceIndex, overviewColor,
                    drawSampleNodes: false);
                if (!cached) reconstructed++;
            }
        }

        private string NearestNavMeshStatus() {
            if (!NavMeshSpatialIndex.IsReady)
                return NavMeshSpatialIndex.Count > 0
                    ? $"; indexing faces {NavMeshSpatialIndex.Built}/{NavMeshSpatialIndex.Count}"
                    : "; indexing face locations";
            return NavMeshSpatialIndex.TryFindNearest(_positionLookingAt, out _, out _, out float distance)
                ? $"; nearest existing face center is {distance:0.0} m away (magenta)"
                : "; no usable face center found";
        }

        private void SetNavMeshRoutePoint() {
            NavMeshProbe point = NavMeshDiagnostics.Probe(Mission.Scene, _positionLookingAt);
            if (!point.IsValid) {
                EditorHud.ShowMessage($"Cannot set route point: {point.Note}.", warning: true);
                return;
            }

            if (_navPointA == null || _navPointB != null) {
                _navPointA = point;
                _navPointB = null;
                _navRoute = null;
                EditorHud.ShowMessage(
                    $"Navmesh A = {point.PointSummary}; {point.FaceSummary}; " +
                    $"surface delta {point.VerticalDelta:0.00} m. Set point B.");
                return;
            }

            _navPointB = point;
            _navRoute = NavMeshDiagnostics.TestRoute(Mission.Scene, _navPointA, _navPointB);

            string result = !_navRoute.Reachable
                ? "UNREACHABLE"
                : _navRoute.HasDistance
                    ? $"path {_navRoute.PathDistance:0.0} m, straight {_navRoute.StraightDistance:0.0} m, " +
                      $"detour {_navRoute.DetourRatio:0.00}x, " +
                      $"{(_navRoute.RequiresRedirect ? "must go around" : "straight route")}, " +
                      $"line clear {(_navRoute.Direct ? "yes" : "no")}"
                    : "reachable, but no path distance returned";

            EditorHud.ShowMessage(
                $"Navmesh B = {point.PointSummary}; {point.FaceSummary}; route {result}.",
                warning: !_navRoute.Reachable);
        }

        // -- ghost ------------------------------------------------------------------------------

        private void UpdateGhost() {
            bool wantGhost = (_mode == EditMode.Build && CurrentPlaceable != null && !CurrentPlaceable.RequiresRestart)
                          || (_mode == EditMode.Move && _carried != null);

            if (!wantGhost) {
                RemoveGhost();
                return;
            }

            // A template ghost is expensive to build, so a ray that blinks out - aiming at sky, or
            // past the far edge of the terrain - hides it rather than throwing it away and building
            // it again the next frame.
            if (!_positionLookingAt.IsValid) {
                if (_ghostIsComposed && _ghost != null) SetGhostVisible(false);
                else RemoveGhost();
                return;
            }

            // The selection changed under a composed ghost: it is showing the wrong thing.
            if (_ghost != null && _ghostIsComposed &&
                !string.Equals(_ghostSource, CurrentPlaceable?.PrefabName, StringComparison.Ordinal)) {
                RemoveGhost();
            }

            if (_ghost == null) {
                Placeable? current = CurrentPlaceable;
                if (_carried == null && current != null && current.IsTemplate) {
                    _ghost = BuildTemplateGhost(current, _positionLookingAt);
                } else {
                    string prefabName = _carried?.PrefabName ?? current!.PrefabName;
                    Vec3 scale = _carried?.Scale ?? new Vec3(1f, 1f, 1f);
                    _ghost = Instantiate(prefabName, _positionLookingAt, _ghostRotation, scale,
                                         enablePhysics: false);
                }
                if (_ghost == null) return;

                _ghostIsComposed = _carried == null && current != null && current.IsTemplate;
                _ghostSource = current?.PrefabName;

                // No physics on the preview, or it collides with the world and with the player while
                // it is still only a suggestion.
                foreach (GameEntity part in _ghost.GetEntityAndChildren()) {
                    part.SetPhysicsState(false, true);
                }
                TintGhost();
            }

            if (_ghostIsComposed) SetGhostVisible(true);

            Vec3 ghostPosition = _positionLookingAt + _ghostOffset;
            if (!_groundFollow) ghostPosition.z = _lockedHeight + _ghostOffset.z;
            _ghost.SetLocalPosition(ghostPosition);
            MatrixFrame frame = _ghost.GetFrame();
            frame.rotation = _ghostRotation;
            frame.rotation.ApplyScaleLocal(_carried?.Scale ?? new Vec3(1f, 1f, 1f));
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

        /// <summary>
        /// A preview of a whole template - every piece, not a marker standing in for it.
        ///
        /// A pole at the centre tells you nothing about whether a hundred-piece fort will clear the
        /// trees or hang off a slope, which is the only question worth asking before dropping it. So
        /// the pieces are built as children of one empty entity: moving that entity moves all of
        /// them, and the existing ghost code needs no idea it is holding more than one object.
        ///
        /// Offsets are the source layout re-anchored the same way placing it will re-anchor them, so
        /// what is previewed is what lands.
        /// </summary>
        private GameEntity? BuildTemplateGhost(Placeable placeable, Vec3 position) {
            try {
                TextureOverrideApplicator.ImportTemplateImages(placeable.TemplateProject);
                SceneProject? source = ProjectSerializer.LoadFile(placeable.TemplateProject);
                if (source == null || source.Entities.Count == 0) return null;

                GameEntity parent = GameEntity.CreateEmpty(Mission.Scene, true, false);
                MatrixFrame frame = MatrixFrame.Identity;
                frame.origin = position;
                parent.SetGlobalFrame(in frame, true);

                Vec3 anchor = ComputeTemplateAnchor(source);

                int built = 0;
                foreach (ProjectEntity entity in source.Entities) {
                    PlacedEntity piece = entity.To();

                    string prefab = PlaceableRegistry.ResolveSpawnPrefab(piece.PrefabName);
                    if (!GameEntity.PrefabExists(prefab)) continue;

                    GameEntity part = GameEntity.Instantiate(Mission.Scene, prefab, MatrixFrame.Identity);

                    MatrixFrame local = MatrixFrame.Identity;
                    local.rotation = piece.Rotation;
                    local.rotation.ApplyScaleLocal(piece.Scale);
                    local.origin = piece.Position - anchor;
                    part.SetFrame(ref local);
                    TextureOverrideApplicator.ApplyAll(part, piece, out _);

                    // autoLocalizeFrame false: the frame set above is ALREADY the local one, and
                    // letting the engine recompute it would fold the parent's position in twice.
                    parent.AddChild(part, false);
                    built++;
                }

                if (built == 0) {
                    DestroyEntity(parent);
                    return null;
                }

                TraceLogger.Write(nameof(SceneEditingMissionLogic),
                    $"Template preview: {built} piece(s).");
                return parent;
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneEditingMissionLogic), "Template preview failed", ex);
                return null;
            }
        }

        private void RemoveGhost() {
            if (_ghost == null) return;
            DestroyEntity(_ghost);
            _ghost = null;
            _ghostIsComposed = false;
            _ghostSource = null;
        }

        private void SetGhostVisible(bool visible) {
            if (_ghost == null) return;
            try {
                foreach (GameEntity part in _ghost.GetEntityAndChildren()) {
                    part.SetVisibilityExcludeParents(visible);
                }
            } catch { }
        }

        // -- scene helpers ----------------------------------------------------------------------

        /// <param name="enablePhysics">
        /// False only for the preview ghost. TRUE for anything real, and it is not optional: a
        /// prefab instantiated at runtime has no physics bodies registered until SetPhysicsState is
        /// called on it and every child. Without bodies the object is not merely walk-through - it is
        /// invisible to raycasts, so Delete and Move could never find anything you had built. That is
        /// the same trap Homesteads hit with runtime ballistas having no collision and no use prompt.
        /// </param>
        private GameEntity? Instantiate(string prefabName, Vec3 position, Mat3 rotation, bool enablePhysics) =>
            Instantiate(prefabName, position, rotation, new Vec3(1f, 1f, 1f), enablePhysics);

        private GameEntity? Instantiate(string prefabName, Vec3 position, Mat3 rotation, Vec3 scale,
                                        bool enablePhysics) {
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
                frame.rotation.ApplyScaleLocal(scale);
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
                // Removed one by one first. RemoveAllChildren detaches rather than destroys, and a
                // composed template ghost is a hundred entities - detaching them would leave every
                // one of them in the scene, invisible to us and never cleaned up.
                foreach (GameEntity child in entity.GetChildren().ToList()) {
                    try { child.Remove(0); } catch { }
                }
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
                ProjectSerializer.LastNavMeshBakeMessage = "";
                _target.Commit();
                _isDirty = false;

                // The navmesh is baked as part of the save, so the save message says what happened to
                // it. Silently baking would leave the user unsure whether their cutouts took effect.
                string bake = ProjectSerializer.LastNavMeshBakeMessage;
                string saved = $"Saved '{_target.DisplayName}' - {_live.Count} object(s).";
                EditorHud.ShowMessage(bake.Length > 0 ? saved + " " + bake : saved);
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
            // A project that has just had its final object/navmesh area removed is deliberately
            // empty but still dirty. It needs the same save-or-discard choice as any other edit.
            if (!_isDirty) return null;

            return new InquiryData(
                new TextObject("{=CSC_UnsavedTitle}Unsaved Changes").ToString(),
                new TextObject("{=CSC_UnsavedText}You have unsaved changes to this scene. Save before leaving?")
                    .ToString(),
                true, true,
                new TextObject("{=CSC_UnsavedSave}Save and Leave").ToString(),
                new TextObject("{=CSC_UnsavedDiscard}Discard").ToString(),
                // Both answers END the mission themselves.
                //
                // Returning the inquiry only asks the question - the leave request that raised it has
                // already been abandoned by the time an answer arrives, so without this the panel
                // closes, nothing happens, and the player has to press Tab a second time to leave.
                () => { Save(); LeaveNow(); },
                () => {
                    _isDirty = false;
                    TraceLogger.Write(nameof(SceneEditingMissionLogic),
                        $"Left without saving; {_live.Count} placed object(s) discarded.");
                    LeaveNow();
                });
        }

        /// <summary>
        /// Ends the mission after the unsaved-changes prompt has been answered.
        ///
        /// The dirty flag is cleared first so the engine's own leave path does not raise the same
        /// question again on the way out.
        /// </summary>
        private void LeaveNow() {
            _isDirty = false;
            try {
                Mission.EndMission();
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneEditingMissionLogic), "EndMission failed", ex);
            }
        }

        protected override void OnEndMission() {
            base.OnEndMission();

            // Nothing will tick after this, so anything still parked has to go now. Safe here: the
            // engine is discarding its render state wholesale, so a queued SceneView clear task is
            // no longer a hazard - and a static list holding scenes across missions would leak.
            UI.PrefabPreview.PreviewSceneRetirement.FlushAll();
            if (Active == this) Active = null;
            RemoveGhost();
            WeaponSheather.SetEditing(false);
            // Deliberately does NOT commit. OnEndMissionRequest already asked, and committing here
            // too would write the project even after the player chose Discard.
        }
    }
}
