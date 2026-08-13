using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
using CustomSceneCreator.Editing;
using TaleWorlds.Library;
using System.Globalization;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Everything placed in the scene, as a list you can act on.
    ///
    /// Clicking in the world only reaches what is visible and in front of you. This reaches what is
    /// inside a building, behind you, or too small to put a cursor on - and it is the only way to see
    /// what a scene contains without walking the whole thing.
    ///
    /// Every action here is one the editor already has; the list is a second way to point at an
    /// object, not a second set of behaviour.
    /// </summary>
    public class SceneOutlinerVM : ViewModel {
        private readonly List<PlacedEntity> _all;
        private readonly SceneEditingMissionLogic _editor;
        private readonly Action _onClose;

        private MBBindingList<OutlinerItemVM> _items = new();
        private string _searchText = "";
        private PlacedEntity? _selected;
        private SortMode _sort = SortMode.Nearest;

        private enum SortMode { Nearest, Name, Newest, Scripts }

        public SceneOutlinerVM(IEnumerable<PlacedEntity> placed, SceneEditingMissionLogic editor, Action onClose) {
            _all = placed.ToList();
            _editor = editor;
            _onClose = onClose;
            RefreshList();
        }

        // -- bindable ---------------------------------------------------------------------------

        [DataSourceProperty] public string TitleText => "Scene Contents";
        [DataSourceProperty] public string HintText =>
            "Double-click to move the camera to an object. Type to search.";

        [DataSourceProperty] public string FocusText => "Go To";
        [DataSourceProperty] public string ScriptsText => "Scripts";
        [DataSourceProperty] public string MoveText => "Pick Up";
        [DataSourceProperty] public string DeleteText => "Delete";
        [DataSourceProperty] public string CloseText => "Close";

        [DataSourceProperty]
        public string SortButtonText => _sort switch {
            SortMode.Nearest => "Sort: nearest   ▼",
            SortMode.Name => "Sort: name   ▼",
            SortMode.Newest => "Sort: newest   ▼",
            _ => "Sort: scripts   ▼",
        };

        [DataSourceProperty]
        public string StatusText {
            get {
                if (_selected == null) {
                    int scripted = _all.Count(e => e.Scripts.Count > 0);
                    return $"{_all.Count} object(s) placed, {scripted} carrying scripts.";
                }
                return $"{PlaceableRegistry.DisplayNameFor(_selected.PrefabName)}   ({_selected.PrefabName})   " +
                       $"{_selected.Scripts.Count} script(s)";
            }
        }

        [DataSourceProperty] public bool HasSelection => _selected != null;

        // -- transform ---------------------------------------------------------------------------
        //
        // Typed numbers are the only way to place something exactly: lining a wall up with the one
        // beside it, or spacing race gates evenly, is arithmetic, not aim. Rotation is shown in
        // DEGREES because that is what anyone doing that arithmetic is thinking in, and converted
        // back on the way in.

        [DataSourceProperty] public string PositionLabel => "Position (m)";
        [DataSourceProperty] public string RotationLabel => "Rotation (degrees)";
        [DataSourceProperty] public string XLabel => "X";
        [DataSourceProperty] public string YLabel => "Y";
        [DataSourceProperty] public string ZLabel => "Z";
        [DataSourceProperty] public string YawLabel => "Yaw";
        [DataSourceProperty] public string PitchLabel => "Pitch";
        [DataSourceProperty] public string RollLabel => "Roll";

        [DataSourceProperty]
        public string PositionX {
            get => Format(_selected?.Position.x ?? 0f);
            set => SetPosition(value, axis: 0, nameof(PositionX));
        }

        [DataSourceProperty]
        public string PositionY {
            get => Format(_selected?.Position.y ?? 0f);
            set => SetPosition(value, axis: 1, nameof(PositionY));
        }

        [DataSourceProperty]
        public string PositionZ {
            get => Format(_selected?.Position.z ?? 0f);
            set => SetPosition(value, axis: 2, nameof(PositionZ));
        }

        /// <summary>Yaw is euler Z - the compass heading, and the one people actually adjust.</summary>
        [DataSourceProperty]
        public string RotationYaw {
            get => Format(Degrees(Euler().z));
            set => SetRotation(value, axis: 2, nameof(RotationYaw));
        }

        [DataSourceProperty]
        public string RotationPitch {
            get => Format(Degrees(Euler().x));
            set => SetRotation(value, axis: 0, nameof(RotationPitch));
        }

        [DataSourceProperty]
        public string RotationRoll {
            get => Format(Degrees(Euler().y));
            set => SetRotation(value, axis: 1, nameof(RotationRoll));
        }



        // -- marker number -------------------------------------------------------------------------
        //
        // Numbers are handed out on placement, so laying down eight enemy spawns gives you 1 to 8
        // without touching anything. This is for the cases that need a hand: renumbering gates after
        // rerouting a track, or splitting spawns into a first and second wave.

        [DataSourceProperty]
        public bool IsMarkerSelected =>
            _selected != null && PlaceableRegistry.IsNumberedMarker(_selected.PrefabName);

        [DataSourceProperty] public string MarkerLabel => "Marker #";

        [DataSourceProperty]
        public string MarkerNote {
            get {
                if (_selected == null) return "";
                int clashes = _editor.CountMarkersWithIndex(_selected);
                if (clashes == 0) return $"exports as {ExportNameFor(_selected)}";
                return $"exports as {ExportNameFor(_selected)} - shared with {clashes} other marker(s)";
            }
        }

        [DataSourceProperty]
        public string MarkerIndexText {
            get => _selected == null || _selected.MarkerIndex <= 0
                ? ""
                : _selected.MarkerIndex.ToString(CultureInfo.InvariantCulture);
            set {
                if (_selected == null) return;
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)) return;
                if (index < 1) return;

                _editor.SetMarkerIndex(_selected, index);
                OnPropertyChangedWithValue(value, nameof(MarkerIndexText));
                OnPropertyChangedWithValue(MarkerNote, nameof(MarkerNote));

                foreach (OutlinerItemVM item in _items) {
                    if (item.Entity == _selected) item.RefreshName();
                }
            }
        }

        /// <summary>The name this marker will carry in the exported scene, numbering applied.</summary>
        private static string ExportNameFor(PlacedEntity entity) {
            Placeable? placeable = PlaceableRegistry.Find(entity.PrefabName);
            if (placeable == null || placeable.ExportName.Length == 0) return entity.PrefabName;
            return placeable.ExportName.Replace("{index}",
                entity.MarkerIndex.ToString(CultureInfo.InvariantCulture));
        }

        [DataSourceProperty]
        public MBBindingList<OutlinerItemVM> Items {
            get => _items;
            set { if (value != _items) { _items = value; OnPropertyChangedWithValue(value, nameof(Items)); } }
        }

        [DataSourceProperty]
        public string SearchText {
            get => _searchText;
            set {
                if (value == _searchText) return;
                _searchText = value;
                OnPropertyChangedWithValue(value, nameof(SearchText));
                RefreshList();
            }
        }

        // -- commands ---------------------------------------------------------------------------

        public void ExecuteCycleSort() {
            _sort = _sort switch {
                SortMode.Nearest => SortMode.Name,
                SortMode.Name => SortMode.Newest,
                SortMode.Newest => SortMode.Scripts,
                _ => SortMode.Nearest,
            };
            RefreshList();
            OnPropertyChangedWithValue(SortButtonText, nameof(SortButtonText));
        }

        public void ExecuteFocus() {
            if (_selected == null) return;
            _editor.FocusOn(_selected);
            // Deliberately stays open: finding something usually means looking at several in turn.
        }

        public void ExecuteScripts() {
            if (_selected == null) return;
            PlacedEntity target = _selected;
            _onClose?.Invoke();
            _editor.OpenScripts(target);
        }

        public void ExecuteMove() {
            if (_selected == null) return;
            PlacedEntity target = _selected;
            _onClose?.Invoke();
            _editor.PickUp(target);
        }

        public void ExecuteDelete() {
            if (_selected == null) return;
            _editor.Delete(_selected);
            _all.Remove(_selected);
            _selected = null;
            RefreshList();
        }

        public void ExecuteClose() => _onClose?.Invoke();

        // -- internals --------------------------------------------------------------------------

        private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static float Degrees(float radians) => radians * 180f / MathF.PI;
        private static float Radians(float degrees) => degrees * MathF.PI / 180f;

        private Vec3 Euler() => _selected?.Rotation.GetEulerAngles() ?? Vec3.Zero;

        private static bool TryParse(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private void SetPosition(string text, int axis, string propertyName) {
            if (_selected == null || !TryParse(text, out float parsed)) return;

            Vec3 position = _selected.Position;
            if (axis == 0) position.x = parsed;
            else if (axis == 1) position.y = parsed;
            else position.z = parsed;

            _editor.UpdateTransform(_selected, position, _selected.Rotation);
            OnPropertyChangedWithValue(text, propertyName);
            RefreshDistances();
        }

        private void SetRotation(string text, int axis, string propertyName) {
            if (_selected == null || !TryParse(text, out float degrees)) return;

            Vec3 euler = Euler();
            float radians = Radians(degrees);
            if (axis == 0) euler.x = radians;
            else if (axis == 1) euler.y = radians;
            else euler.z = radians;

            // ApplyEulerAngles ACCUMULATES onto the matrix it is called on, so it has to start from
            // identity - which is exactly what the game does when it round-trips a rotation.
            Mat3 rotation = Mat3.Identity;
            rotation.ApplyEulerAngles(in euler);

            _editor.UpdateTransform(_selected, _selected.Position, rotation);
            OnPropertyChangedWithValue(text, propertyName);
        }

        /// <summary>Distances go stale once something is moved by hand.</summary>
        private void RefreshDistances() {
            Vec3 camera = CameraPosition;
            foreach (OutlinerItemVM item in _items) item.RefreshDistance(camera);
        }

        /// <summary>Pushes every transform field, after selection changes.</summary>
        private void NotifyTransform() {
            OnPropertyChangedWithValue(PositionX, nameof(PositionX));
            OnPropertyChangedWithValue(PositionY, nameof(PositionY));
            OnPropertyChangedWithValue(PositionZ, nameof(PositionZ));
            OnPropertyChangedWithValue(RotationYaw, nameof(RotationYaw));
            OnPropertyChangedWithValue(RotationPitch, nameof(RotationPitch));
            OnPropertyChangedWithValue(RotationRoll, nameof(RotationRoll));
            OnPropertyChangedWithValue(IsMarkerSelected, nameof(IsMarkerSelected));
            OnPropertyChangedWithValue(MarkerIndexText, nameof(MarkerIndexText));
            OnPropertyChangedWithValue(MarkerNote, nameof(MarkerNote));
        }

        private Vec3 CameraPosition {
            get {
                try {
                    return Mission.Current?.Scene?.LastFinalRenderCameraPosition ?? Vec3.Zero;
                } catch {
                    return Vec3.Zero;
                }
            }
        }

        private void RefreshList() {
            _items.Clear();

            IEnumerable<PlacedEntity> placed = _all;
            if (!string.IsNullOrWhiteSpace(_searchText)) {
                string q = _searchText.Trim();
                placed = placed.Where(e =>
                    e.PrefabName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Scripts.Any(s => s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            Vec3 camera = CameraPosition;
            placed = _sort switch {
                // Nearest first is the default because the thing you want is usually the thing you
                // are looking at, and the list is otherwise in placement order, which means nothing.
                SortMode.Nearest => placed.OrderBy(e => (e.Position - camera).LengthSquared),
                // Numerically within a type, so gate 10 follows gate 9 rather than gate 1.
                SortMode.Name => placed.OrderBy(e => e.PrefabName).ThenBy(e => e.MarkerIndex),
                SortMode.Newest => placed.Reverse(),
                _ => placed.OrderByDescending(e => e.Scripts.Count).ThenBy(e => e.PrefabName),
            };

            foreach (PlacedEntity entity in placed) {
                _items.Add(new OutlinerItemVM(entity, camera, OnClicked, OnDoubleClicked,
                    _selected == entity));
            }

            OnPropertyChangedWithValue(StatusText, nameof(StatusText));
            OnPropertyChangedWithValue(HasSelection, nameof(HasSelection));
        }

        private void OnClicked(PlacedEntity entity) {
            _selected = entity;
            foreach (OutlinerItemVM item in _items) item.IsSelected = item.Entity == entity;
            OnPropertyChangedWithValue(StatusText, nameof(StatusText));
            OnPropertyChangedWithValue(HasSelection, nameof(HasSelection));
            NotifyTransform();
        }

        private void OnDoubleClicked(PlacedEntity entity) {
            OnClicked(entity);
            ExecuteFocus();
        }
    }

    public class OutlinerItemVM : ViewModel {
        private readonly Action<PlacedEntity> _onClick;
        private readonly Action<PlacedEntity> _onDoubleClick;
        private bool _isSelected;

        public OutlinerItemVM(PlacedEntity entity, Vec3 camera, Action<PlacedEntity> onClick,
                              Action<PlacedEntity> onDoubleClick, bool isSelected) {
            Entity = entity;
            _onClick = onClick;
            _onDoubleClick = onDoubleClick;
            _isSelected = isSelected;

            RefreshDistance(camera);
        }

        public PlacedEntity Entity { get; }

        /// <summary>
        /// "Enemy Spawn  #3". The number is part of the name here because it is what you are looking
        /// for when you open this list - which gate, which spawn - not a detail of it.
        /// </summary>
        [DataSourceProperty]
        public string Name => Entity.MarkerIndex > 0
            ? $"{PlaceableRegistry.DisplayNameFor(Entity.PrefabName)}  #{Entity.MarkerIndex}"
            : PlaceableRegistry.DisplayNameFor(Entity.PrefabName);

        public void RefreshName() => OnPropertyChangedWithValue(Name, nameof(Name));

        private string _distanceText = "";

        [DataSourceProperty]
        public string DistanceText {
            get => _distanceText;
            private set { if (value != _distanceText) { _distanceText = value; OnPropertyChangedWithValue(value, nameof(DistanceText)); } }
        }

        public void RefreshDistance(Vec3 camera) =>
            DistanceText = $"{(Entity.Position - camera).Length:0} m";

        [DataSourceProperty]
        public string ScriptText => Entity.Scripts.Count == 0
            ? ""
            : Entity.Scripts.Count == 1 ? Entity.Scripts[0].Name : $"{Entity.Scripts.Count} scripts";

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set {
                if (value == _isSelected) return;
                _isSelected = value;
                OnPropertyChangedWithValue(value, nameof(IsSelected));
                // The row colour is what actually reads as "this one" in a list of forty identical
                // wall segments - the brush's own selected state is far too subtle to find.
                OnPropertyChangedWithValue(RowColor, nameof(RowColor));
                OnPropertyChangedWithValue(DetailColor, nameof(DetailColor));
            }
        }

        /// <summary>Selected rows go yellow; the rest keep the panel's normal text colour.</summary>
        [DataSourceProperty] public string RowColor => _isSelected ? SelectedColor : NormalColor;

        /// <summary>Same idea for the dimmer columns, kept a step darker so they stay secondary.</summary>
        [DataSourceProperty] public string DetailColor => _isSelected ? SelectedDim : NormalDim;

        private const string SelectedColor = "#FFD34AFF";
        private const string SelectedDim   = "#E0B84AFF";
        private const string NormalColor   = "#D8D0BEFF";
        private const string NormalDim     = "#9A9285FF";

        public void ExecuteClick() => _onClick?.Invoke(Entity);
        public void ExecuteDoubleClick() => _onDoubleClick?.Invoke(Entity);
    }
}
