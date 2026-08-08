using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Catalog;
using TaleWorlds.Library;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Scene browser view model: every shipped scene, grouped by category, filtered by a live search
    /// box, with per-scene level selection.
    /// </summary>
    public class SceneBrowserVM : ViewModel {
        private readonly Action<string, string> _onConfirm;   // (sceneName, sceneLevels)
        private readonly Action _onCancel;

        private readonly List<string> _categories = new();
        private int _categoryIndex;

        private MBBindingList<SceneItemVM> _sceneItems = new();
        private MBBindingList<LevelItemVM> _levelItems = new();
        private string _searchText = "";
        private SceneEntry? _selected;

        public SceneBrowserVM(Action<string, string> onConfirm, Action onCancel) {
            _onConfirm = onConfirm;
            _onCancel = onCancel;

            _categories.Add(AllCategories);
            _categories.AddRange(SceneCatalog.Categories);

            RefreshList();
        }

        private const string AllCategories = "All";

        // -- bindable ---------------------------------------------------------------------------

        [DataSourceProperty] public string TitleText => "Select a Scene";
        [DataSourceProperty] public string OpenText => "Open Editor";
        [DataSourceProperty] public string CancelText => "Cancel";
        [DataSourceProperty] public string NextCategoryText => ">";
        [DataSourceProperty] public string PrevCategoryText => "<";

        [DataSourceProperty]
        public string CategoryText {
            get {
                string category = _categories[_categoryIndex];
                int count = FilteredScenes().Count();
                return $"{category}  ({count})";
            }
        }

        [DataSourceProperty]
        public string SelectionText {
            get {
                if (_selected == null) return "Nothing selected - pick a scene, or type an exact name";
                string levels = SelectedLevels();
                string warn = _selected.IsWalkable ? "" : "   [no navmesh - you will not be able to walk]";
                return $"{_selected.Name}   ({_selected.Module})"
                     + (levels.Length > 0 ? $"   levels: {levels}" : "")
                     + warn;
            }
        }

        [DataSourceProperty]
        public bool CanOpen => _selected != null || !string.IsNullOrWhiteSpace(_searchText);

        [DataSourceProperty]
        public bool HasLevels => _levelItems.Count > 0;

        /// <summary>
        /// Explains the level row. Without this the toggles are unlabelled jargon - "level_2" and
        /// "sally" mean nothing until you know a scene is one geometry set with named layers.
        /// </summary>
        [DataSourceProperty]
        public string LevelHelpText {
            get {
                if (_levelItems.Count == 0) return "";
                LevelItemVM? on = _levelItems.FirstOrDefault(l => l.IsOn);
                return on != null
                    ? $"{on.LevelName} - {SceneLevelInfo.Describe(on.LevelName)}"
                    : "Scene layers ('base' is always on). Tick a tier for how developed the "
                    + "settlement looks, or a state such as siege or raid.";
            }
        }

        [DataSourceProperty]
        public MBBindingList<SceneItemVM> SceneItems {
            get => _sceneItems;
            set { if (value != _sceneItems) { _sceneItems = value; OnPropertyChangedWithValue(value, nameof(SceneItems)); } }
        }

        [DataSourceProperty]
        public MBBindingList<LevelItemVM> LevelItems {
            get => _levelItems;
            set { if (value != _levelItems) { _levelItems = value; OnPropertyChangedWithValue(value, nameof(LevelItems)); } }
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

        public void ExecuteNextCategory() {
            _categoryIndex = (_categoryIndex + 1) % _categories.Count;
            RefreshList();
        }

        public void ExecutePrevCategory() {
            _categoryIndex = (_categoryIndex - 1 + _categories.Count) % _categories.Count;
            RefreshList();
        }

        public void ExecuteOpen() {
            // A typed name wins when nothing is selected, so a scene missing from the catalog (a
            // derived scene, or one added by another mod after the catalog was generated) can still
            // be opened by name.
            string scene = _selected?.Name ?? _searchText?.Trim() ?? "";
            if (scene.Length == 0) return;
            _onConfirm?.Invoke(scene, SelectedLevels());
        }

        public void ExecuteCancel() => _onCancel?.Invoke();

        // -- internals --------------------------------------------------------------------------

        private IEnumerable<SceneEntry> FilteredScenes() {
            string category = _categories[_categoryIndex];
            IEnumerable<SceneEntry> scenes = SceneCatalog.All;

            if (category != AllCategories) {
                scenes = scenes.Where(s => s.Category == category);
            }
            if (!string.IsNullOrWhiteSpace(_searchText)) {
                string q = _searchText.Trim();
                scenes = scenes.Where(s =>
                    s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.Module.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return scenes.OrderBy(s => s.Name);
        }

        private void RefreshList() {
            _sceneItems.Clear();
            foreach (SceneEntry scene in FilteredScenes()) {
                _sceneItems.Add(new SceneItemVM(scene, OnSceneClicked, _selected?.Name == scene.Name));
            }
            OnPropertyChangedWithValue(CategoryText, nameof(CategoryText));
            OnPropertyChangedWithValue(SelectionText, nameof(SelectionText));
            OnPropertyChangedWithValue(CanOpen, nameof(CanOpen));
        }

        private void OnSceneClicked(SceneEntry scene) {
            _selected = scene;
            foreach (SceneItemVM item in _sceneItems) {
                item.IsSelected = item.SceneName == scene.Name;
            }
            RebuildLevels(scene);
            OnPropertyChangedWithValue(SelectionText, nameof(SelectionText));
            OnPropertyChangedWithValue(CanOpen, nameof(CanOpen));
        }

        private const string BaseLevel = "base";

        private void RebuildLevels(SceneEntry scene) {
            _levelItems.Clear();
            foreach (string level in scene.LevelNames) {
                // "base" is deliberately not offered as a toggle. It is mask 1, the foundation layer
                // every shipped multi-level scene builds on, and opening a scene without it strips
                // out most of the ground and navmesh - which reads as "the editor is broken", not as
                // "you turned off a layer". It is always included instead; see SelectedLevels.
                if (string.Equals(level, BaseLevel, StringComparison.OrdinalIgnoreCase)) continue;
                _levelItems.Add(new LevelItemVM(level, false, OnLevelToggled));
            }
            OnPropertyChangedWithValue(HasLevels, nameof(HasLevels));
            OnPropertyChangedWithValue(SelectionText, nameof(SelectionText));
        }

        private void OnLevelToggled() {
            OnPropertyChangedWithValue(SelectionText, nameof(SelectionText));
            OnPropertyChangedWithValue(LevelHelpText, nameof(LevelHelpText));
        }

        private string SelectedLevels() {
            if (_selected == null) return "";
            // Single-level scenes want an empty string, not "base" - they have no level masks at all.
            if (_selected.LevelNames.Length == 0) return "";

            var levels = new List<string>();
            if (_selected.LevelNames.Any(l => string.Equals(l, BaseLevel, StringComparison.OrdinalIgnoreCase))) {
                levels.Add(BaseLevel);
            }
            levels.AddRange(_levelItems.Where(l => l.IsOn).Select(l => l.LevelName));
            return string.Join(" ", levels);
        }
    }

    public class SceneItemVM : ViewModel {
        private readonly Action<SceneEntry> _onClick;
        private readonly SceneEntry _entry;
        private bool _isSelected;

        public SceneItemVM(SceneEntry entry, Action<SceneEntry> onClick, bool isSelected) {
            _entry = entry;
            _onClick = onClick;
            _isSelected = isSelected;
        }

        [DataSourceProperty] public string SceneName => _entry.Name;

        [DataSourceProperty]
        public string DetailText =>
            _entry.Module + (_entry.IsWalkable ? "" : "  - no navmesh");

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } }
        }

        public void ExecuteClick() => _onClick?.Invoke(_entry);
    }

    public class LevelItemVM : ViewModel {
        private readonly Action _onToggled;
        private bool _isOn;

        public LevelItemVM(string levelName, bool isOn, Action onToggled) {
            LevelName = levelName;
            _isOn = isOn;
            _onToggled = onToggled;
        }

        [DataSourceProperty] public string LevelName { get; }

        [DataSourceProperty]
        public bool IsOn {
            get => _isOn;
            set {
                if (value == _isOn) return;
                _isOn = value;
                OnPropertyChangedWithValue(value, nameof(IsOn));
                _onToggled?.Invoke();
            }
        }

        public void ExecuteToggle() => IsOn = !IsOn;
    }
}
