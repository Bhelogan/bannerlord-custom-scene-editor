using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Catalog;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// The asset picker: browse and search the whole palette, inspect one, and build it.
    ///
    /// Replaces cycling 6,000+ prefabs with two keys. Search is the primary way in - with a palette
    /// this size, categories alone are not navigation - so the search box filters across the whole
    /// catalog rather than only the current category.
    /// </summary>
    public class AssetPickerVM : ViewModel {
        private readonly Action<Placeable, IReadOnlyList<Placeable>> _onBuild;
        private readonly Action _onClose;
        private readonly List<Placeable> _all;

        private readonly List<string> _categories = new();
        private int _categoryIndex;

        private MBBindingList<AssetItemVM> _items = new();
        private string _searchText = "";
        private Placeable? _selected;

        private const string AllCategories = "All";
        private const int MaxRows = 400;

        public AssetPickerVM(IEnumerable<Placeable> placeables,
                             Action<Placeable, IReadOnlyList<Placeable>> onBuild,
                             Action onClose,
                             string initialSearch,
                             string initialCategory) {
            _all = placeables.ToList();
            _onBuild = onBuild;
            _onClose = onClose;

            _categories.Add(AllCategories);
            _categories.AddRange(_all.Select(p => p.Category).Distinct().OrderBy(c => c));

            // Reopening should land you back where you were. Searching for "cart", building one, then
            // having to retype it to build the next is the kind of friction that makes a tool feel
            // hostile.
            _searchText = initialSearch ?? "";
            int categoryIndex = _categories.IndexOf(initialCategory ?? "");
            if (categoryIndex >= 0) _categoryIndex = categoryIndex;

            RefreshList();
        }

        /// <summary>Current filter state, so it can be restored next time the picker opens.</summary>
        public string CurrentSearch => _searchText;
        public string CurrentCategory => _categories[_categoryIndex];

        // -- bindable ---------------------------------------------------------------------------

        [DataSourceProperty] public string TitleText => "Assets";
        [DataSourceProperty] public string BuildText => "Build";
        [DataSourceProperty] public string CloseText => "Close";
        [DataSourceProperty] public string NextCategoryText => ">";
        [DataSourceProperty] public string PrevCategoryText => "<";
        [DataSourceProperty] public string HintText =>
            "Type to search. Double-click an asset to build it, or select and press Build. Esc closes.";

        [DataSourceProperty]
        public string CategoryText {
            get {
                int shown = _items.Count;
                int total = Filtered().Count();
                string category = _categories[_categoryIndex];
                return total > shown
                    ? $"{category}  (showing {shown} of {total})"
                    : $"{category}  ({total})";
            }
        }

        [DataSourceProperty] public bool HasSelection => _selected != null;

        [DataSourceProperty] public string DetailName => _selected?.DisplayName ?? "";
        [DataSourceProperty] public string DetailPrefab => _selected != null ? _selected.PrefabName : "";

        /// <summary>
        /// The info pane. Answers what a scene author actually asks of an asset - can I walk into it,
        /// how big is it, does it carry logic - rather than the economy figures the homestead builder
        /// showed here.
        /// </summary>
        [DataSourceProperty]
        public string DetailBody {
            get {
                if (_selected == null) return "";
                var lines = new List<string>();

                lines.Add($"Category:  {_selected.Category}");
                lines.Add($"Module:    {_selected.Module}");
                if (_selected.Mobility.Length > 0) lines.Add($"Mobility:  {_selected.Mobility}");

                lines.Add(_selected.IsLogical
                    ? "Geometry:  none - marker / logic node"
                    : $"Meshes:    {Wrap(_selected.Meshes)}");

                lines.Add(_selected.HasPhysics || _selected.PhysicsShapes.Length > 0
                    ? $"Collision: yes  {Wrap(_selected.PhysicsShapes)}"
                    : "Collision: none - you can walk through this");

                if (_measuredSize.Length > 0) lines.Add($"Size:      {_measuredSize}");

                if (_selected.Scripts.Length > 0)  lines.Add($"Scripts:   {Wrap(_selected.Scripts)}");
                if (_selected.Tags.Length > 0)     lines.Add($"Tags:      {Wrap(_selected.Tags)}");
                if (_selected.ChildNames.Length > 0 && _selected.ChildNames != _selected.PrefabName) {
                    lines.Add($"Parts:     {Wrap(_selected.ChildNames)}");
                }

                return string.Join(Environment.NewLine, lines);
            }
        }

        [DataSourceProperty]
        public MBBindingList<AssetItemVM> Items {
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

        public void ExecuteNextCategory() { _categoryIndex = (_categoryIndex + 1) % _categories.Count; RefreshList(); }
        public void ExecutePrevCategory() { _categoryIndex = (_categoryIndex - 1 + _categories.Count) % _categories.Count; RefreshList(); }

        public void ExecuteBuild() {
            if (_selected == null) return;
            // Hand back the whole filtered set, not just the selection: the cycle keys should walk
            // the list you were just looking at. Cycling back into all 6,400 prefabs after searching
            // for one thing throws away the filtering you just did.
            _onBuild?.Invoke(_selected, Filtered().ToList());
            _onClose?.Invoke();
        }

        public void ExecuteClose() => _onClose?.Invoke();

        // -- internals --------------------------------------------------------------------------

        private IEnumerable<Placeable> Filtered() {
            IEnumerable<Placeable> items = _all;

            string category = _categories[_categoryIndex];
            if (category != AllCategories) items = items.Where(p => p.Category == category);

            if (!string.IsNullOrWhiteSpace(_searchText)) {
                string q = _searchText.Trim();
                items = items.Where(p =>
                    p.PrefabName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.Tags.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return items.OrderBy(p => p.DisplayName);
        }

        private void RefreshList() {
            _items.Clear();
            // Capped: binding several thousand widgets stalls the UI for seconds, and nobody scrolls
            // past a few hundred anyway. Narrowing the search is the intended way to reach the rest,
            // and CategoryText says plainly when the list is truncated.
            foreach (Placeable p in Filtered().Take(MaxRows)) {
                _items.Add(new AssetItemVM(p, OnItemClicked, OnItemDoubleClicked, _selected?.PrefabName == p.PrefabName));
            }
            OnPropertyChangedWithValue(CategoryText, nameof(CategoryText));
        }

        private void OnItemClicked(Placeable placeable) {
            _selected = placeable;
            foreach (AssetItemVM item in _items) item.IsSelected = item.PrefabName == placeable.PrefabName;

            _measuredSize = Measure(placeable);

            OnPropertyChangedWithValue(HasSelection, nameof(HasSelection));
            OnPropertyChangedWithValue(DetailName, nameof(DetailName));
            OnPropertyChangedWithValue(DetailPrefab, nameof(DetailPrefab));
            OnPropertyChangedWithValue(DetailBody, nameof(DetailBody));
        }

        private void OnItemDoubleClicked(Placeable placeable) {
            OnItemClicked(placeable);
            ExecuteBuild();
        }

        private string _measuredSize = "";

        /// <summary>
        /// Measures a prefab's bounding box by briefly instantiating it out of sight.
        ///
        /// Skipped for anything carrying scripts: instantiating one runs its script, and shipped
        /// scripts routinely assume a mission type we are not in - that is the same class of problem
        /// that made castle scenes crash on load. A number is not worth risking that for.
        /// </summary>
        private static string Measure(Placeable placeable) {
            if (placeable.IsLogical || placeable.HasScripts) return "";
            if (Mission.Current?.Scene == null) return "";

            GameEntity? probe = null;
            try {
                MatrixFrame frame = MatrixFrame.Identity;
                // Far below any plausible scene, so a frame of visibility cannot show it.
                frame.origin = new Vec3(0f, 0f, -5000f);
                probe = GameEntity.Instantiate(Mission.Current.Scene, placeable.PrefabName, frame);
                if (probe == null) return "";

                Vec3 size = probe.GlobalBoxMax - probe.GlobalBoxMin;
                if (!size.IsValid || size.LengthSquared < 0.0001f) return "";
                return $"{size.x:0.0} x {size.y:0.0} x {size.z:0.0} m";
            } catch (Exception ex) {
                TraceLogger.Write(nameof(AssetPickerVM),
                    $"Could not measure '{placeable.PrefabName}': {ex.GetType().Name}: {ex.Message}");
                return "";
            } finally {
                try {
                    probe?.RemoveAllChildren();
                    probe?.Remove(0);
                } catch { }
            }
        }

        /// <summary>Comma lists in the dump can be long; keep the pane readable.</summary>
        private static string Wrap(string value, int max = 90) =>
            value.Length <= max ? value : value.Substring(0, max) + "...";
    }

    public class AssetItemVM : ViewModel {
        private readonly Placeable _placeable;
        private readonly Action<Placeable> _onClick;
        private readonly Action<Placeable> _onDoubleClick;
        private bool _isSelected;

        public AssetItemVM(Placeable placeable, Action<Placeable> onClick, Action<Placeable> onDoubleClick, bool isSelected) {
            _placeable = placeable;
            _onClick = onClick;
            _onDoubleClick = onDoubleClick;
            _isSelected = isSelected;
        }

        [DataSourceProperty] public string PrefabName => _placeable.PrefabName;
        [DataSourceProperty] public string Name => _placeable.DisplayName;

        [DataSourceProperty]
        public string Note =>
            _placeable.IsLogical ? "marker"
            : _placeable.HasPhysics ? ""
            : "no collision";

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } }
        }

        public void ExecuteClick() => _onClick?.Invoke(_placeable);
        public void ExecuteDoubleClick() => _onDoubleClick?.Invoke(_placeable);
    }
}
