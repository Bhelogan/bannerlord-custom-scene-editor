using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Catalog;
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
        private readonly Action<SceneProject> _onWalkaround;
        private readonly Action<SceneProject> _onBattle;
        private readonly Action<SceneProject> _onBake;
        private readonly Action _onNewScene;
        private readonly Action _onCancel;

        private readonly List<SceneProject> _all;
        private MBBindingList<ProjectItemVM> _items = new();
        private string _searchText = "";
        private string _activityText = "";
        private bool _isBaking;
        private int _bakeProgress;
        private string _bakeProgressText = "";
        private SceneProject? _selected;

        public ProjectBrowserVM(Action<SceneProject> onOpen, Action<SceneProject> onWalkaround,
                                Action<SceneProject> onBattle, Action<SceneProject> onBake,
                                Action onNewScene, Action onCancel) {
            _onOpen = onOpen;
            _onWalkaround = onWalkaround;
            _onBattle = onBattle;
            _onBake = onBake;
            _onNewScene = onNewScene;
            _onCancel = onCancel;
            _all = ProjectSerializer.LoadAll();
            // A complete derived scene is immediately usable even before it has its own project
            // JSON. Surface it in the normal entry screen as an empty project; objects exported
            // into scene.xscene must not be instantiated a second time by the project layer.
            foreach (SceneEntry scene in SceneCatalog.Openable.Where(s =>
                         s.Category == "My Derived Scenes" &&
                         !_all.Any(p => string.Equals(p.TargetScene, s.Name,
                             StringComparison.OrdinalIgnoreCase)))) {
                _all.Add(new SceneProject {
                    Name = scene.Name,
                    TargetScene = scene.Name,
                    SceneLevels = scene.Levels,
                });
            }
            RefreshList();
        }

        [DataSourceProperty] public string TitleText => "Saved Projects";
        [DataSourceProperty] public string OpenText => "Open";
        [DataSourceProperty] public string WalkaroundText => "Walk Around";
        [DataSourceProperty] public string BattleText => "Raid-Scale Battle";
        [DataSourceProperty] public string BakeText => "Rebake Navmesh";
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

        [DataSourceProperty] public bool CanOpen => _selected != null && !_isBaking;
        [DataSourceProperty] public bool CanBake => _selected != null && !_isBaking;
        [DataSourceProperty] public bool CanInteract => !_isBaking;
        [DataSourceProperty] public bool IsBaking {
            get => _isBaking;
            private set {
                if (value == _isBaking) return;
                _isBaking = value;
                OnPropertyChangedWithValue(value, nameof(IsBaking));
                NotifyAvailability();
            }
        }
        [DataSourceProperty] public int BakeProgress {
            get => _bakeProgress;
            private set {
                value = Math.Max(0, Math.Min(100, value));
                if (value == _bakeProgress) return;
                _bakeProgress = value;
                OnPropertyChangedWithValue(value, nameof(BakeProgress));
            }
        }
        [DataSourceProperty] public string BakeProgressText {
            get => _bakeProgressText;
            private set {
                if (value == _bakeProgressText) return;
                _bakeProgressText = value;
                OnPropertyChangedWithValue(value, nameof(BakeProgressText));
            }
        }
        [DataSourceProperty] public string ActivityText {
            get => _activityText;
            private set { if (value != _activityText) { _activityText = value; OnPropertyChangedWithValue(value, nameof(ActivityText)); } }
        }

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
            if (_selected == null || _isBaking) return;
            _onOpen?.Invoke(_selected);
        }

        public void ExecuteWalkaround() {
            if (_selected == null || _isBaking) return;
            _onWalkaround?.Invoke(_selected);
        }

        public void ExecuteBattle() {
            if (_selected == null || _isBaking) return;
            _onBattle?.Invoke(_selected);
        }

        public void ExecuteBake() {
            if (_selected == null || _isBaking) return;
            ActivityText = "Baking selected project's navmesh...";
            _onBake?.Invoke(_selected);
        }

        public void SetActivity(string text) => ActivityText = text ?? "";

        public void BeginBake(string projectName) {
            BakeProgress = 0;
            BakeProgressText = "Preparing navmesh bake... 0%";
            ActivityText = $"Baking navmesh for '{projectName}'...";
            IsBaking = true;
        }

        public void SetBakeProgress(int percent) {
            BakeProgress = percent;
            BakeProgressText = $"Baking navmesh... {BakeProgress}%";
        }

        public void FinishBake(string message, bool succeeded) {
            BakeProgress = succeeded ? 100 : BakeProgress;
            BakeProgressText = succeeded ? "Navmesh bake complete." : "Navmesh bake stopped.";
            ActivityText = message ?? "";
            IsBaking = false;
        }

        public void ExecuteNewScene() { if (!_isBaking) _onNewScene?.Invoke(); }
        public void ExecuteCancel() { if (!_isBaking) _onCancel?.Invoke(); }

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
            OnPropertyChangedWithValue(CanBake, nameof(CanBake));
            OnPropertyChangedWithValue(CanInteract, nameof(CanInteract));
        }

        private void OnClicked(SceneProject project) {
            _selected = project;
            foreach (ProjectItemVM item in _items) item.IsSelected = item.Name == project.Name;
            OnPropertyChangedWithValue(SelectionText, nameof(SelectionText));
            OnPropertyChangedWithValue(CanOpen, nameof(CanOpen));
            OnPropertyChangedWithValue(CanBake, nameof(CanBake));
        }

        private void NotifyAvailability() {
            OnPropertyChangedWithValue(CanOpen, nameof(CanOpen));
            OnPropertyChangedWithValue(CanBake, nameof(CanBake));
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

        // Separate columns rather than one packed string: each gets its own fixed slot in the row,
        // which is what stops them overlapping.
        [DataSourceProperty] public string CountText => $"{_project.Entities.Count} obj";
        [DataSourceProperty] public string SceneText => _project.TargetScene;
        [DataSourceProperty] public string ModifiedText => _project.Modified.ToString("yyyy-MM-dd HH:mm");

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } }
        }

        public void ExecuteClick() => _onClick?.Invoke(_project);
        public void ExecuteDoubleClick() => _onDoubleClick?.Invoke(_project);
    }
}
