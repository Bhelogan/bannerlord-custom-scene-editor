using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
using CustomSceneCreator.Editing;
using TaleWorlds.Library;
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
                return $"{Placeable.ToDisplayName(_selected.PrefabName)}   ({_selected.PrefabName})   " +
                       $"{_selected.Scripts.Count} script(s)";
            }
        }

        [DataSourceProperty] public bool HasSelection => _selected != null;

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
                SortMode.Name => placed.OrderBy(e => e.PrefabName),
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

            DistanceText = $"{(entity.Position - camera).Length:0} m";
        }

        public PlacedEntity Entity { get; }

        [DataSourceProperty] public string Name => Placeable.ToDisplayName(Entity.PrefabName);
        [DataSourceProperty] public string DistanceText { get; }

        [DataSourceProperty]
        public string ScriptText => Entity.Scripts.Count == 0
            ? ""
            : Entity.Scripts.Count == 1 ? Entity.Scripts[0].Name : $"{Entity.Scripts.Count} scripts";

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } }
        }

        public void ExecuteClick() => _onClick?.Invoke(Entity);
        public void ExecuteDoubleClick() => _onDoubleClick?.Invoke(Entity);
    }
}
