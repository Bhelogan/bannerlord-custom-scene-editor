using System;
using CustomSceneCreator.Editing;
using CustomSceneCreator.IO;
using TaleWorlds.Library;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// The export dialog: choose which of the two useful shapes to write out.
    ///
    /// Separate from saving on purpose. Saving is reflexive and must never ask a question; exporting
    /// is occasional and deliberate, and has a real choice attached. Putting the choice on the save
    /// key would mean answering it constantly for no reason.
    /// </summary>
    public class ExportDialogVM : ViewModel {
        private readonly SceneProject _project;
        private readonly Action _onClose;
        private string _exportName;
        private string _statusText = "";

        public ExportDialogVM(SceneProject project, Action onClose) {
            _project = project;
            _onClose = onClose;
            _exportName = ProjectSerializer.SanitizeFileName(project.Name);
        }

        [DataSourceProperty] public string TitleText => "Export";

        [DataSourceProperty]
        public string SummaryText =>
            $"{_project.Entities.Count} object(s) placed on '{_project.TargetScene}'.";

        [DataSourceProperty] public string NameLabelText => "Name";

        [DataSourceProperty] public string PrefabButtonText => "Export as Prefab";
        [DataSourceProperty] public string SceneButtonText => "Export Whole Scene";
        [DataSourceProperty] public string TemplateButtonText => "Export as Template";
        [DataSourceProperty] public string TemplateHelpText =>
            "The layout, to place into OTHER scenes as separate pieces you can still move. " +
            "Appears under 'My Templates' immediately - no restart.";
        [DataSourceProperty] public string CloseText => "Close";

        [DataSourceProperty]
        public string PrefabHelpText =>
            "Just what you built, as one reusable object. Positions are relative to its own base, so "
            + "it can be placed anywhere - in a homestead, or back into this editor. Written to the "
            + "module's Prefabs folder and added to the asset picker.";

        [DataSourceProperty]
        public string SceneHelpText =>
            "Everything where it actually sits, tied to this scene. Written as a block you can paste "
            + "into the scene's own scene.xscene and open in the Modding Kit to bake a navmesh.";

        [DataSourceProperty]
        public string ExportName {
            get => _exportName;
            set {
                if (value == _exportName) return;
                _exportName = value;
                OnPropertyChangedWithValue(value, nameof(ExportName));
            }
        }

        [DataSourceProperty]
        public string StatusText {
            get => _statusText;
            set { if (value != _statusText) { _statusText = value; OnPropertyChangedWithValue(value, nameof(StatusText)); } }
        }

        public void ExecuteExportPrefab() => Run(ExportKind.Prefab);
        public void ExecuteExportScene() => Run(ExportKind.SceneFragment);
        public void ExecuteExportTemplate() => Run(ExportKind.Template);
        public void ExecuteClose() => _onClose?.Invoke();

        private void Run(ExportKind kind) {
            ExportResult result = SceneExporter.Export(_project, kind, _exportName);
            StatusText = result.Message;

            // Stay open on failure so the message can be read and the name corrected; the dialog
            // closing on an error would just lose the explanation.
            if (result.Success) {
                EditorHud.ShowMessage(result.Message);
                _onClose?.Invoke();
            }
        }
    }
}
