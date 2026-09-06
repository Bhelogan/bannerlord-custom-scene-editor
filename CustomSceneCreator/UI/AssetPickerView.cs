using System;
using System.Collections.Generic;
using CustomSceneCreator.Catalog;
using CustomSceneCreator.Editing;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ScreenSystem;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Hosts the asset picker over a live mission.
    ///
    /// The setup mirrors the native mission escape menu, which is the proven arrangement for a
    /// clickable modal Gauntlet layer during a running mission: focus layer, input restrictions,
    /// a registered hotkey category, TrySetFocus - AND pausing the engine while it is open.
    ///
    /// The pause is not optional. Without it the live mission re-asserts input focus every frame and
    /// the panel is completely unresponsive: mouse and keyboard both dead. That failure looks like a
    /// broken prefab rather than an input problem, which is why it is worth stating here.
    ///
    /// Close keys are read through the LAYER's own input, because a focus layer with input
    /// restrictions consumes them before global input ever sees them.
    /// </summary>
    public class AssetPickerView : MissionView {
        public static AssetPickerView? Instance { get; private set; }
        public static bool IsOpen { get; private set; }

        private GauntletLayer? _layer;
        private AssetPickerVM? _dataSource;
        private TextureWidget? _previewWidget;
        private GauntletMovieIdentifier? _movie;
        private string _appliedPreviewPrefab = "";
        private float _previewYaw;
        private float _previewPitch;
        private float _previewZoom = 1f;
        private bool _previewDragging;

        /// <summary>
        /// Set by the editor logic. Receives the chosen asset AND the filtered list it came from, so
        /// the cycle keys can continue walking the same results afterwards.
        /// </summary>
        public Action<Placeable, IReadOnlyList<Placeable>, string>? OnAssetChosen;

        // Filter state survives close/reopen. Static because the view is recreated per mission and
        // this is a user preference, not mission state.
        private static string _lastSearch = "";
        private static string _lastCategory = "";

        public override void OnMissionScreenInitialize() {
            base.OnMissionScreenInitialize();
            Instance = this;
        }

        public override void OnMissionScreenFinalize() {
            Close();
            if (Instance == this) Instance = null;
            base.OnMissionScreenFinalize();
        }

        public void Open(IEnumerable<Placeable> placeables) {
            if (IsOpen || MissionScreen == null) return;

            try {
                _dataSource = new AssetPickerVM(placeables, Choose, ChooseMode, Close, _lastSearch, _lastCategory);

                _layer = new GauntletLayer("CSCAssetPicker", 4000) { IsFocusLayer = true };
                _layer.InputRestrictions.SetInputRestrictions();
                _layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericPanelGameKeyCategory"));
                _movie = _layer.LoadMovie("CSCAssetPicker", _dataSource);
                MissionScreen.AddLayer(_layer);
                ScreenManager.TrySetFocus(_layer);

                BindPreviewWidget();

                MBCommon.PauseGameEngine();
                MouseManager.ShowCursor(true);

                IsOpen = true;
                TraceLogger.Write(nameof(AssetPickerView), "Asset picker opened.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(AssetPickerView), "Failed to open asset picker", ex);
                Close();
            }
        }

        public void Close() {
            if (!IsOpen) return;
            IsOpen = false;

            // Remember the filter before the view model goes away.
            if (_dataSource != null) {
                _lastSearch = _dataSource.CurrentSearch;
                _lastCategory = _dataSource.CurrentCategory;
            }

            try { MBCommon.UnPauseGameEngine(); } catch { }

            if (_layer != null) {
                try {
                    _layer.InputRestrictions.ResetInputRestrictions();
                    if (_movie != null) _layer.ReleaseMovie(_movie);
                    MissionScreen?.RemoveLayer(_layer);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(AssetPickerView), $"Layer teardown failed: {ex.Message}");
                }
            }
            _layer = null;
            _movie = null;
            _previewWidget = null;
            _appliedPreviewPrefab = "";
            _previewYaw = 0f;
            _previewPitch = 0f;
            _previewZoom = 1f;
            _previewDragging = false;
            _dataSource = null;
        }

        private void Choose(Placeable placeable, IReadOnlyList<Placeable> filtered, string scopeLabel) {
            try {
                OnAssetChosen?.Invoke(placeable, filtered, scopeLabel);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(AssetPickerView), "OnAssetChosen threw", ex);
            }
        }

        private void ChooseMode(EditMode mode) {
            // This is the build selector the author is already using. Choose the tool and return
            // to the live editor in one action; the next click then does exactly that tool's work.
            Close();
            SceneEditingMissionLogic.Active?.SelectEditMode(mode);
        }

        public override void OnMissionScreenTick(float dt) {
            base.OnMissionScreenTick(dt);
            if (!IsOpen || _layer == null) return;

            UpdatePreview();
            TickPreviewControls();

            bool exit = _layer.Input.IsHotKeyReleased("Exit")
                     || _layer.Input.IsKeyReleased(InputKey.Escape)
                     || (TaleWorlds.InputSystem.Input.IsGamepadActive
                         && _layer.Input.IsKeyReleased(InputKey.ControllerRRight));
            if (exit) Close();
        }

        /// <summary>Finds the lazily-created texture provider after the picker movie is loaded.</summary>
        private void BindPreviewWidget() {
            try {
                Widget? root = _movie?.Movie?.RootWidget;
                _previewWidget = root?.FindChild("PrefabPreviewTexture", includeAllChildren: true) as TextureWidget;
                if (_previewWidget == null) {
                    TraceLogger.Write(nameof(AssetPickerView),
                        "Asset picker preview widget was not found; the picker will remain usable without a preview.");
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(AssetPickerView),
                    $"Could not bind asset preview: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Passes selection changes into the provider once its deferred widget construction completes.</summary>
        private void UpdatePreview() {
            try {
                if (_previewWidget?.TextureProvider == null) return;
                string wanted = _dataSource?.PreviewPrefabName ?? "";
                if (wanted == _appliedPreviewPrefab) return;

                _previewWidget.TextureProvider.SetProperty("PrefabName", wanted);
                _appliedPreviewPrefab = wanted;
                _previewYaw = 0f;
                _previewPitch = 0f;
                _previewZoom = 1f;
                ApplyPreviewTransform();
            } catch (Exception ex) {
                TraceLogger.Write(nameof(AssetPickerView),
                    $"Could not update asset preview: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Drag in the preview to turn it; scroll over it to zoom. The list keeps its normal scroll behaviour.</summary>
        private void TickPreviewControls() {
            if (_previewWidget?.TextureProvider == null || _layer == null) return;
            try {
                Vec2 mouse = _layer.Input.GetMousePositionPixel();
                Vec2 topLeft = _previewWidget.GlobalPosition;
                Vec2 size = _previewWidget.Size;
                bool over = mouse.x >= topLeft.x && mouse.x <= topLeft.x + size.x
                         && mouse.y >= topLeft.y && mouse.y <= topLeft.y + size.y;
                bool held = _layer.Input.IsKeyDown(InputKey.LeftMouseButton);
                if (!held) _previewDragging = false;
                else if (over) _previewDragging = true;

                bool changed = false;
                if (_previewDragging) {
                    float dx = _layer.Input.GetMouseMoveX();
                    float dy = _layer.Input.GetMouseMoveY();
                    if (Math.Abs(dx) > 0.01f) { _previewYaw = (_previewYaw + dx * 0.6f) % 360f; changed = true; }
                    if (Math.Abs(dy) > 0.01f) { _previewPitch = MathF.Clamp(_previewPitch - dy * 0.5f, -80f, 80f); changed = true; }
                }
                if (over) {
                    float scroll = _layer.Input.GetDeltaMouseScroll();
                    if (Math.Abs(scroll) > 0.01f) {
                        _previewZoom *= scroll > 0f ? 1.15f : 1f / 1.15f;
                        _previewZoom = MathF.Clamp(_previewZoom, 0.35f, 3f);
                        changed = true;
                    }
                }
                if (changed) ApplyPreviewTransform();
            } catch (Exception ex) {
                TraceLogger.Write(nameof(AssetPickerView),
                    $"Asset preview controls failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ApplyPreviewTransform() {
            if (_previewWidget?.TextureProvider == null) return;
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            _previewWidget.TextureProvider.SetProperty("Yaw", _previewYaw.ToString(culture));
            _previewWidget.TextureProvider.SetProperty("Pitch", _previewPitch.ToString(culture));
            _previewWidget.TextureProvider.SetProperty("Zoom", _previewZoom.ToString(culture));
        }
    }
}
