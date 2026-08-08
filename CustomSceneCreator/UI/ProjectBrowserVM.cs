using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Editing;
using TaleWorlds.Library;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Reopen a saved project and keep working on it.
    ///
    /// The project is the editable source - it holds the scene, the levels and every object with its
    /// real position, so reopening one restores exactly where you left off. Exports are outputs and
    /// are deliberately not listed here: a prefab is a finished artifact, and editing it means
    /// reopening the project it came from.
    /// </summary>
    public class ProjectBrowserVM : ViewModel {
        private readonly Action<SceneProject> _onOpen;
        private readonly Action _onNewScene;
        private readonly Action _onCancel;

        private readonly List<SceneProject> _all;
        private MBBindingList<ProjectItemVM> _items = new();
        private string _searchText = "";
        private SceneProject? _selected;

        public ProjectBrowserVM(Action<SceneProject> onOpen, Action onNewScene, Action onCancel) {
            _onOpen = onOpen;
            _onNewScene = onNewScene;
            _onCancel = onCancel;
            _all = ProjectSerializer.LoadAll();
            RefreshList();
        }

        [DataSourceProperty] public string TitleText => "Saved Projects";
        [DataSourceProperty] public string OpenText => "Open";
        [DataSourceProperty] public string NewSceneText => "New - Pick a Scene";
        [DataSourceProperty] public string CancelText => "Cancel";

        [DataSourceProperty]
        public string HintText => _all.Count == 0
            ? "Nothing saved yet. Pick a scene to start building; it saves under the scene's name."
            : "Double-click a project to reopen it exactly as you left it.";

        [DataSourceProperty]
        public string SelectionText {
            get {
                if (_selected == null) return $"{_all.Count} project(s)";
                string missing = Catalog.SceneCatalog.Find(_selected.TargetScene) == null
                    ? "   [scene not found in this install]"
                    : "";
                return $"{_selected.Name} - {_selected.Entities.Count} object(s) on " +
                       $"'{_selected.TargetScene}'{missing}";
            }
        }

        [DataSourceProperty] public bool CanOpen => _selected != null;

        [DataSourceProperty]
        public MBBindingList<ProjectItemVM> Items {
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

        public void ExecuteOpen() {
            if (_selected == null) return;
            _onOpen?.Invoke(_selected);
        }

        public void ExecuteNewScene() => _onNewScene?.Invoke();
        public void ExecuteCancel() => _onCancel?.Invoke();

        private void RefreshList() {
            _items.Clear();
            IEnumerable<SceneProject> projects = _all;

            if (!string.IsNullOrWhiteSpace(_searchText)) {
                string q = _searchText.Trim();
                projects = projects.Where(p =>
                    p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.TargetScene.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            foreach (SceneProject project in projects) {
                _items.Add(new ProjectItemVM(project, OnClicked, OnDoubleClicked,
                    _selected?.Name == project.Name));
            }
            OnPropertyChangedWithValue(SelectionText, nameof(SelectionText));
            OnPropertyChangedWithValue(CanOpen, nameof(CanOpen));
        }

        private void OnClicked(SceneProject project) {
            _selected = project;
            foreach (ProjectItemVM item in _items) item.IsSelected = item.Name == project.Name;
            OnPropertyChangedWithValue(SelectionText, nameof(SelectionText));
            OnPropertyChangedWithValue(CanOpen, nameof(CanOpen));
        }

        private void OnDoubleClicked(SceneProject project) {
            OnClicked(project);
            ExecuteOpen();
        }
    }

    public class ProjectItemVM : ViewModel {
        private readonly SceneProject _project;
        private readonly Action<SceneProject> _onClick;
        private readonly Action<SceneProject> _onDoubleClick;
        private bool _isSelected;

        public ProjectItemVM(SceneProject project, Action<SceneProject> onClick,
                             Action<SceneProject> onDoubleClick, bool isSelected) {
            _project = project;
            _onClick = onClick;
            _onDoubleClick = onDoubleClick;
            _isSelected = isSelected;
        }

        [DataSourceProperty] public string Name => _project.Name;

        [DataSourceProperty]
        public string DetailText =>
            $"{_project.Entities.Count} obj   {_project.TargetScene}   {_project.Modified:yyyy-MM-dd HH:mm}";

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } }
        }

        public void ExecuteClick() => _onClick?.Invoke(_project);
        public void ExecuteDoubleClick() => _onDoubleClick?.Invoke(_project);
    }
}
