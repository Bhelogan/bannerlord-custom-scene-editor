using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
using TaleWorlds.Library;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Scripts on one placed object: what is attached, what each one's variables are set to, and a
    /// searchable way to add more.
    ///
    /// The panel opens on the object rather than the other way round. Clicking a brazier and asking
    /// "what is on this" is the question people actually have; a bare script picker would let you
    /// attach things but never see or remove them.
    ///
    /// Add mode reuses this same panel rather than stacking a second modal - the same trick the asset
    /// picker uses for its category list, and for the same reason: no overlay positioning, no
    /// z-order, and one Escape always means one thing.
    /// </summary>
    public class ScriptPanelVM : ViewModel {
        private readonly PlacedEntity _entity;
        private readonly Action _onClose;
        private readonly Action _onChanged;

        private MBBindingList<AttachedScriptItemVM> _attachedItems = new();
        private MBBindingList<ScriptChoiceVM> _choiceItems = new();
        private MBBindingList<ScriptVariableItemVM> _variableItems = new();
        private MBBindingList<ScriptCategoryItemVM> _categoryItems = new();
        private MBBindingList<ScriptValueItemVM> _valueItems = new();

        private readonly List<string> _categories = new();
        private int _categoryIndex;
        private bool _isAdding;
        private bool _isCategoryListOpen;
        private string _searchText = "";

        private ScriptVariableItemVM? _valueTarget;
        private string _valueSearchText = "";

        private AttachedScript? _selectedAttached;
        private ScriptDefinition? _selectedChoice;

        private const string AllCategories = "All";
        private const int MaxRows = 300;

        public ScriptPanelVM(PlacedEntity entity, Action onClose, Action onChanged) {
            _entity = entity;
            _onClose = onClose;
            _onChanged = onChanged;

            _categories.Add(AllCategories);
            _categories.AddRange(ScriptCatalog.Categories);

            RefreshAttached();
            RebuildCategoryItems();
            RefreshChoices();
        }

        // -- bindable ---------------------------------------------------------------------------

        [DataSourceProperty] public string TitleText => "Scripts";

        [DataSourceProperty]
        public string SubjectText => Placeable.ToDisplayName(_entity.PrefabName);

        [DataSourceProperty]
        public string HintText => _isAdding
            ? "Sorted by how often shipped scenes use each script. Type to search."
            : "Attached scripts. Select one to edit its variables, or add another.";

        [DataSourceProperty] public bool IsAdding => _isAdding;
        [DataSourceProperty] public bool IsViewing => !_isAdding;

        /// <summary>
        /// The category list takes over the choice list's region while open, exactly as the asset
        /// picker does - so all three lists in this panel share one space and only ever one shows.
        /// </summary>
        [DataSourceProperty]
        public bool IsCategoryListOpen {
            get => _isCategoryListOpen;
            set {
                if (value == _isCategoryListOpen) return;
                _isCategoryListOpen = value;
                OnPropertyChangedWithValue(value, nameof(IsCategoryListOpen));
                OnPropertyChangedWithValue(IsChoiceListVisible, nameof(IsChoiceListVisible));
            }
        }

        [DataSourceProperty] public bool IsChoiceListVisible => _isAdding && !_isCategoryListOpen;

        [DataSourceProperty]
        public MBBindingList<ScriptCategoryItemVM> CategoryItems {
            get => _categoryItems;
            set { if (value != _categoryItems) { _categoryItems = value; OnPropertyChangedWithValue(value, nameof(CategoryItems)); } }
        }

        [DataSourceProperty] public string AddText => "Add Script...";
        [DataSourceProperty] public string RemoveText => "Remove";
        [DataSourceProperty] public string BackText => "Back";
        [DataSourceProperty] public string ConfirmAddText => "Attach";
        [DataSourceProperty] public string CloseText => "Close";

        [DataSourceProperty]
        public string CategoryButtonText => $"{_categories[_categoryIndex]}   ▼";

        [DataSourceProperty]
        public string StatusText {
            get {
                if (_isAdding) {
                    if (_selectedChoice == null) return $"{FilteredChoices().Count()} script(s)";
                    string preview = _selectedChoice.CanPreview
                        ? ""
                        : "   [will not preview here - still exported]";
                    return $"{_selectedChoice.Name} - used {_selectedChoice.Uses:N0} times in shipped scenes, " +
                           $"{_selectedChoice.Variables.Count} variable(s){preview}";
                }
                return _entity.Scripts.Count == 0
                    ? "Nothing attached yet."
                    : $"{_entity.Scripts.Count} script(s) attached.";
            }
        }

        [DataSourceProperty] public bool HasAttachedSelection => _selectedAttached != null;
        [DataSourceProperty] public bool HasChoiceSelection => _selectedChoice != null;

        [DataSourceProperty]
        public MBBindingList<AttachedScriptItemVM> AttachedItems {
            get => _attachedItems;
            set { if (value != _attachedItems) { _attachedItems = value; OnPropertyChangedWithValue(value, nameof(AttachedItems)); } }
        }

        [DataSourceProperty]
        public MBBindingList<ScriptChoiceVM> ChoiceItems {
            get => _choiceItems;
            set { if (value != _choiceItems) { _choiceItems = value; OnPropertyChangedWithValue(value, nameof(ChoiceItems)); } }
        }

        [DataSourceProperty]
        public MBBindingList<ScriptVariableItemVM> VariableItems {
            get => _variableItems;
            set { if (value != _variableItems) { _variableItems = value; OnPropertyChangedWithValue(value, nameof(VariableItems)); } }
        }

        // -- value presets ------------------------------------------------------------------------
        //
        // A string variable is rarely free text. "Event Path" wants one of a fixed set of sound
        // events, "LoopStartAction" one of a fixed set of animation names - and there is no way to
        // guess either, nor a file to browse: the real lists live inside sound banks and animation
        // data the game never exposes. What CAN be read is what the shipped scenes set them to, so
        // the picker offers those, ordered by how often the game itself uses each one.
        //
        // Like the category list, this takes over the variable list's region rather than stacking a
        // second modal - same reason, and it keeps one Escape meaning one thing.

        [DataSourceProperty] public bool IsValueListOpen => _valueTarget != null;
        [DataSourceProperty] public bool IsVariableListVisible => _valueTarget == null;

        [DataSourceProperty]
        public string ValueTitleText => _valueTarget == null
            ? ""
            : $"{_valueTarget.Name} - values used by shipped scenes";

        [DataSourceProperty] public string ValueBackText => "Back";

        [DataSourceProperty]
        public MBBindingList<ScriptValueItemVM> ValueItems {
            get => _valueItems;
            set { if (value != _valueItems) { _valueItems = value; OnPropertyChangedWithValue(value, nameof(ValueItems)); } }
        }

        [DataSourceProperty]
        public string ValueSearchText {
            get => _valueSearchText;
            set {
                if (value == _valueSearchText) return;
                _valueSearchText = value;
                OnPropertyChangedWithValue(value, nameof(ValueSearchText));
                RefreshValues();
            }
        }

        public void ExecuteCloseValueList() {
            _valueTarget = null;
            _valueSearchText = "";
            _valueItems.Clear();
            OnPropertyChangedWithValue(_valueSearchText, nameof(ValueSearchText));
            NotifyValueList();
        }

        private void OpenValueList(ScriptVariableItemVM variable) {
            _valueTarget = variable;
            _valueSearchText = "";
            OnPropertyChangedWithValue(_valueSearchText, nameof(ValueSearchText));
            RefreshValues();
            NotifyValueList();
        }

        private void RefreshValues() {
            _valueItems.Clear();
            if (_valueTarget == null) return;

            IEnumerable<ScriptPreset> presets = _valueTarget.Presets;
            if (!string.IsNullOrWhiteSpace(_valueSearchText)) {
                string q = _valueSearchText.Trim();
                presets = presets.Where(p => p.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            string current = _valueTarget.Value;
            foreach (ScriptPreset preset in presets.Take(MaxRows)) {
                _valueItems.Add(new ScriptValueItemVM(preset, preset.Text == current, ApplyValue));
            }
        }

        /// <summary>Picking a value is the whole point - it applies and closes, no confirm step.</summary>
        private void ApplyValue(string text) {
            if (_valueTarget == null) return;
            _valueTarget.SetValueExternally(text);
            ExecuteCloseValueList();
        }

        private void NotifyValueList() {
            OnPropertyChangedWithValue(IsValueListOpen, nameof(IsValueListOpen));
            OnPropertyChangedWithValue(IsVariableListVisible, nameof(IsVariableListVisible));
            OnPropertyChangedWithValue(ValueTitleText, nameof(ValueTitleText));
        }

        [DataSourceProperty]
        public string SearchText {
            get => _searchText;
            set {
                if (value == _searchText) return;
                _searchText = value;
                OnPropertyChangedWithValue(value, nameof(SearchText));
                RefreshChoices();
            }
        }

        // -- commands ---------------------------------------------------------------------------

        public void ExecuteBeginAdd() { _isAdding = true; RefreshChoices(); NotifyMode(); }
        public void ExecuteBack() { _isAdding = false; IsCategoryListOpen = false; NotifyMode(); }
        public void ExecuteClose() => _onClose?.Invoke();

        /// <summary>The category button opens a list rather than stepping blindly to the next one.</summary>
        public void ExecuteToggleCategoryList() => IsCategoryListOpen = !IsCategoryListOpen;

        private void SelectCategory(int index) {
            _categoryIndex = ((index % _categories.Count) + _categories.Count) % _categories.Count;

            // Changing category clears the search, which was scoped to the category being left.
            if (!string.IsNullOrWhiteSpace(_searchText)) {
                _searchText = "";
                OnPropertyChangedWithValue(_searchText, nameof(SearchText));
            }

            IsCategoryListOpen = false;
            RebuildCategoryItems();
            RefreshChoices();
        }

        private void RebuildCategoryItems() {
            _categoryItems.Clear();
            for (int i = 0; i < _categories.Count; i++) {
                int index = i;
                string name = _categories[i];
                int count = name == AllCategories
                    ? ScriptCatalog.All.Count
                    : ScriptCatalog.All.Count(s => s.Category == name);
                _categoryItems.Add(new ScriptCategoryItemVM(name, count, index == _categoryIndex,
                    () => SelectCategory(index)));
            }
        }

        public void ExecuteConfirmAdd() {
            if (_selectedChoice == null) return;

            // Seed each variable with the value shipped scenes use most, so an attached script starts
            // in a state that already works rather than empty.
            var attached = new AttachedScript { Name = _selectedChoice.Name };
            foreach (ScriptVariable variable in _selectedChoice.Variables) {
                if (variable.IsEntityReference) continue;   // not editable yet; see ScriptVariable
                attached.Variables[variable.Name] = variable.Default;
            }

            _entity.Scripts.Add(attached);
            _selectedAttached = attached;
            _isAdding = false;

            RefreshAttached();
            RefreshVariables();
            NotifyMode();
            _onChanged?.Invoke();
        }

        public void ExecuteRemove() {
            if (_selectedAttached == null) return;
            _entity.Scripts.Remove(_selectedAttached);
            _selectedAttached = null;
            RefreshAttached();
            RefreshVariables();
            _onChanged?.Invoke();
        }

        // -- internals --------------------------------------------------------------------------

        private IEnumerable<ScriptDefinition> FilteredChoices() {
            IEnumerable<ScriptDefinition> scripts = ScriptCatalog.All;

            string category = _categories[_categoryIndex];
            if (category != AllCategories) scripts = scripts.Where(s => s.Category == category);

            if (!string.IsNullOrWhiteSpace(_searchText)) {
                string q = _searchText.Trim();
                scripts = scripts.Where(s => s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // Already ordered by usage from the catalog; keep that rather than re-sorting by name.
            return scripts;
        }

        private void RefreshChoices() {
            _choiceItems.Clear();
            foreach (ScriptDefinition definition in FilteredChoices().Take(MaxRows)) {
                _choiceItems.Add(new ScriptChoiceVM(definition, OnChoiceClicked, OnChoiceDoubleClicked,
                    _selectedChoice?.Name == definition.Name));
            }
            OnPropertyChangedWithValue(CategoryButtonText, nameof(CategoryButtonText));
            OnPropertyChangedWithValue(StatusText, nameof(StatusText));
        }

        private void RefreshAttached() {
            _attachedItems.Clear();
            foreach (AttachedScript script in _entity.Scripts) {
                _attachedItems.Add(new AttachedScriptItemVM(script, OnAttachedClicked,
                    _selectedAttached == script));
            }
            OnPropertyChangedWithValue(StatusText, nameof(StatusText));
            OnPropertyChangedWithValue(HasAttachedSelection, nameof(HasAttachedSelection));
        }

        private void RefreshVariables() {
            // The open value list belongs to a row that is about to be replaced.
            if (_valueTarget != null) ExecuteCloseValueList();

            _variableItems.Clear();
            if (_selectedAttached == null) return;

            ScriptDefinition? definition = ScriptCatalog.Find(_selectedAttached.Name);
            if (definition == null) return;

            foreach (ScriptVariable variable in definition.Variables.OrderByDescending(v => v.Uses)) {
                _selectedAttached.Variables.TryGetValue(variable.Name, out string? current);
                _variableItems.Add(new ScriptVariableItemVM(variable, current ?? variable.Default,
                    value => {
                        _selectedAttached.Variables[variable.Name] = value;
                        _onChanged?.Invoke();
                    },
                    OpenValueList));
            }
        }

        private void OnAttachedClicked(AttachedScript script) {
            _selectedAttached = script;
            foreach (AttachedScriptItemVM item in _attachedItems) item.IsSelected = item.Script == script;
            RefreshVariables();
            OnPropertyChangedWithValue(HasAttachedSelection, nameof(HasAttachedSelection));
        }

        private void OnChoiceClicked(ScriptDefinition definition) {
            _selectedChoice = definition;
            foreach (ScriptChoiceVM item in _choiceItems) item.IsSelected = item.Name == definition.Name;
            OnPropertyChangedWithValue(StatusText, nameof(StatusText));
            OnPropertyChangedWithValue(HasChoiceSelection, nameof(HasChoiceSelection));
        }

        private void OnChoiceDoubleClicked(ScriptDefinition definition) {
            OnChoiceClicked(definition);
            ExecuteConfirmAdd();
        }

        private void NotifyMode() {
            OnPropertyChangedWithValue(IsAdding, nameof(IsAdding));
            OnPropertyChangedWithValue(IsViewing, nameof(IsViewing));
            OnPropertyChangedWithValue(IsChoiceListVisible, nameof(IsChoiceListVisible));
            OnPropertyChangedWithValue(HintText, nameof(HintText));
            OnPropertyChangedWithValue(StatusText, nameof(StatusText));
        }
    }

    public class AttachedScriptItemVM : ViewModel {
        private readonly Action<AttachedScript> _onClick;
        private bool _isSelected;

        public AttachedScriptItemVM(AttachedScript script, Action<AttachedScript> onClick, bool isSelected) {
            Script = script;
            _onClick = onClick;
            _isSelected = isSelected;
        }

        public AttachedScript Script { get; }

        [DataSourceProperty] public string Name => Script.Name;

        [DataSourceProperty]
        public string Note {
            get {
                ScriptDefinition? definition = ScriptCatalog.Find(Script.Name);
                if (definition != null && !definition.CanPreview) return "export only";
                return Script.Variables.Count > 0 ? $"{Script.Variables.Count} var" : "";
            }
        }

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } }
        }

        public void ExecuteClick() => _onClick?.Invoke(Script);
    }

    public class ScriptChoiceVM : ViewModel {
        private readonly ScriptDefinition _definition;
        private readonly Action<ScriptDefinition> _onClick;
        private readonly Action<ScriptDefinition> _onDoubleClick;
        private bool _isSelected;

        public ScriptChoiceVM(ScriptDefinition definition, Action<ScriptDefinition> onClick,
                              Action<ScriptDefinition> onDoubleClick, bool isSelected) {
            _definition = definition;
            _onClick = onClick;
            _onDoubleClick = onDoubleClick;
            _isSelected = isSelected;
        }

        [DataSourceProperty] public string Name => _definition.Name;
        [DataSourceProperty] public string UsesText => _definition.Uses.ToString("N0");
        [DataSourceProperty] public string CategoryText => _definition.Category;

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } }
        }

        public void ExecuteClick() => _onClick?.Invoke(_definition);
        public void ExecuteDoubleClick() => _onDoubleClick?.Invoke(_definition);
    }

    /// <summary>
    /// One editable variable row.
    ///
    /// The editor shown depends on the inferred type, because typing "true" into a text box is both
    /// slower and easier to get wrong than clicking it - and with the type only inferred from
    /// observed values, showing it as a control is also the clearest way to say what the type IS.
    /// </summary>
    public class ScriptVariableItemVM : ViewModel {
        private readonly ScriptVariable _variable;
        private readonly Action<string> _onChanged;
        private string _value;

        private readonly Action<ScriptVariableItemVM>? _onChoose;

        public ScriptVariableItemVM(ScriptVariable variable, string value, Action<string> onChanged,
                                    Action<ScriptVariableItemVM>? onChoose = null) {
            _variable = variable;
            _value = value ?? "";
            _onChanged = onChanged;
            _onChoose = onChoose;
        }

        [DataSourceProperty] public string Name => _variable.Name;

        public IReadOnlyList<ScriptPreset> Presets => _variable.Presets;

        /// <summary>Only text rows get a picker: bools have buttons, and floats have no preset list.</summary>
        [DataSourceProperty] public bool HasPresets => IsTextEditor && _variable.HasPresets;

        [DataSourceProperty] public string ChooseText => $"Choose ({_variable.Presets.Count})";

        public void ExecuteChoose() => _onChoose?.Invoke(this);
        [DataSourceProperty] public string TypeText => _variable.Type;

        /// <summary>Two clickable buttons instead of a text box.</summary>
        [DataSourceProperty] public bool IsBoolEditor => _variable.Type == "bool";

        /// <summary>A text box: floats, strings, and anything the generator could not classify.</summary>
        [DataSourceProperty] public bool IsTextEditor => !IsBoolEditor && !_variable.IsEntityReference;

        /// <summary>Entity references are shown but not editable - see ScriptVariable.</summary>
        [DataSourceProperty] public bool IsReadOnly => _variable.IsEntityReference;

        // The chosen option is marked in the text as well as highlighted behind it. A brush's selected
        // state is easy to miss on two adjacent buttons, and a variable being silently the wrong way
        // round is the kind of thing you only discover when the script does nothing in game.
        [DataSourceProperty] public string TrueText => IsTrue ? "> true" : "true";
        [DataSourceProperty] public string FalseText => IsFalse ? "> false" : "false";

        [DataSourceProperty] public bool IsTrue => _value == "true";
        [DataSourceProperty] public bool IsFalse => !IsTrue;

        [DataSourceProperty]
        public string HintText {
            get {
                if (_variable.IsEntityReference) return "links to another entity - not editable yet";
                if (IsBoolEditor) return "";
                return _variable.Samples.Length > 0 ? $"e.g. {_variable.Samples}" : "";
            }
        }

        /// <summary>Shown for read-only rows, where there is no input box to display the value in.</summary>
        [DataSourceProperty] public string ReadOnlyValueText => _value;

        [DataSourceProperty]
        public string Value {
            get => _value;
            set {
                if (value == _value) return;
                _value = value ?? "";
                OnPropertyChangedWithValue(_value, nameof(Value));
                NotifyBool();
                _onChanged?.Invoke(_value);
            }
        }

        public void ExecuteSetTrue() => Value = "true";
        public void ExecuteSetFalse() => Value = "false";

        /// <summary>Set from the preset picker, which bypasses the bound text box.</summary>
        public void SetValueExternally(string text) {
            Value = text;
            OnPropertyChangedWithValue(_value, nameof(Value));
        }

        private void NotifyBool() {
            OnPropertyChangedWithValue(IsTrue, nameof(IsTrue));
            OnPropertyChangedWithValue(IsFalse, nameof(IsFalse));
            OnPropertyChangedWithValue(TrueText, nameof(TrueText));
            OnPropertyChangedWithValue(FalseText, nameof(FalseText));
            OnPropertyChangedWithValue(ReadOnlyValueText, nameof(ReadOnlyValueText));
        }
    }

    /// <summary>One value the shipped scenes use, in the preset list.</summary>
    public class ScriptValueItemVM : ViewModel {
        private readonly ScriptPreset _preset;
        private readonly Action<string> _onSelect;

        public ScriptValueItemVM(ScriptPreset preset, bool isSelected, Action<string> onSelect) {
            _preset = preset;
            _isSelected = isSelected;
            _onSelect = onSelect;
        }

        private bool _isSelected;

        [DataSourceProperty] public string Text => _preset.Text;

        /// <summary>
        /// Scene count, not raw uses: one scene setting the same value on 400 torches says far less
        /// about whether a value is generally useful than forty scenes each using it once.
        /// </summary>
        [DataSourceProperty] public string UsesText => $"{_preset.Scenes} scene(s)";

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } }
        }

        public void ExecuteSelect() => _onSelect?.Invoke(_preset.Text);
    }

    public class ScriptCategoryItemVM : ViewModel {
        private readonly Action _onSelect;
        private bool _isSelected;

        public ScriptCategoryItemVM(string name, int count, bool isSelected, Action onSelect) {
            Name = name;
            CountText = count.ToString();
            _isSelected = isSelected;
            _onSelect = onSelect;
        }

        [DataSourceProperty] public string Name { get; }
        [DataSourceProperty] public string CountText { get; }

        [DataSourceProperty]
        public bool IsSelected {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } }
        }

        public void ExecuteSelect() => _onSelect?.Invoke();
    }
}
