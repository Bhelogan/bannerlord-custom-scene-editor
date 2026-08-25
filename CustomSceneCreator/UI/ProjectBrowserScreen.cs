using System;
using System.Threading;
using System.Threading.Tasks;
using CustomSceneCreator.CampaignEntry;
using CustomSceneCreator.Editing;
using CustomSceneCreator.IO;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// Shows the saved-project list over whatever screen is current. Same layer approach as the scene
    /// browser: opened from a settlement menu, so it sits on top of it and cancelling returns exactly
    /// where the player was.
    /// </summary>
    public static class ProjectBrowserScreen {
        private static GauntletLayer? _layer;
        private static ProjectBrowserVM? _vm;
        private static Task<SceneNavMeshBaker.Result>? _bakeTask;
        private static int _bakeDone;
        private static int _bakeTotal = 100;
        private static int _lastDisplayedProgress = -1;

        public static bool IsOpen => _layer != null;

        public static void Open() {
            if (_layer != null) return;

            try {
                _vm = new ProjectBrowserVM(OnOpenProject, OnWalkaround, OnBattle, OnBake, OnNewScene, Close);

                _layer = new GauntletLayer("CSCProjectBrowser", 4000) { IsFocusLayer = true };
                _layer.LoadMovie("CSCProjectBrowser", _vm);
                _layer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
                ScreenManager.TopScreen.AddLayer(_layer);
                ScreenManager.TrySetFocus(_layer);

                TraceLogger.Write(nameof(ProjectBrowserScreen), "Project browser opened.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ProjectBrowserScreen), "Failed to open project browser", ex);
                Close();
            }
        }

        public static void Close() {
            // The worker writes the candidate through a temporary file and must not outlive the
            // browser that owns its status.  Keep the popup open until it has finished rather than
            // letting another project start a competing bake against the same test slot.
            if (_bakeTask != null) return;
            if (_layer == null) return;
            try {
                _layer.InputRestrictions.ResetInputRestrictions();
                ScreenManager.TopScreen.RemoveLayer(_layer);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ProjectBrowserScreen), "Failed to close project browser", ex);
            } finally {
                _layer = null;
                _vm = null;
            }
        }

        private static void OnOpenProject(SceneProject project) {
            Close();
            // The project carries its own scene and levels, so reopening restores the whole session
            // rather than dropping the objects into whatever scene happened to be chosen last.
            CampaignEntry.ReturnToBrowser.ArmForProjects();
            if (!SceneCreatorEntry.OpenEditor(project.TargetScene, project.SceneLevels, project.Name)) {
                CampaignEntry.ReturnToBrowser.Cancel();
            }
        }

        private static void OnNewScene() {
            Close();
            SceneBrowserScreen.Open();
        }

        private static void OnWalkaround(SceneProject project) {
            Close();
            ReturnToBrowser.ArmForProjects();
            if (!SceneCreatorEntry.OpenWalkaround(project)) {
                ReturnToBrowser.Cancel();
                Open();
            }
        }

        private static void OnBattle(SceneProject project) {
            Close();
            ReturnToBrowser.ArmForProjects();
            if (!SceneCreatorEntry.OpenNavmeshSkirmish(project)) {
                ReturnToBrowser.Cancel();
                Open();
            }
        }

        private static void OnBake(SceneProject project) {
            if (_bakeTask != null) return;

            EditorHud.ShowMessage($"Baking navmesh for '{project.Name}'...");
            Interlocked.Exchange(ref _bakeDone, 0);
            Interlocked.Exchange(ref _bakeTotal, 100);
            _lastDisplayedProgress = -1;
            _vm?.BeginBake(project.Name);

            // Navmesh triangulation and validation are deliberately done away from the Gauntlet
            // thread.  Updating a ViewModel from this worker is unsafe, so the progress callback
            // only stores integers; Tick() copies them into the UI on the game's main thread.
            _bakeTask = Task.Run(() => SceneNavMeshBaker.BakeForProject(
                project,
                (done, total) => {
                    Interlocked.Exchange(ref _bakeDone, done);
                    Interlocked.Exchange(ref _bakeTotal, Math.Max(1, total));
                },
                force: true));
        }

        /// <summary>Moves worker progress into the Gauntlet ViewModel on the main game thread.</summary>
        public static void Tick() {
            Task<SceneNavMeshBaker.Result>? task = _bakeTask;
            if (task == null) return;

            int total = Math.Max(1, Volatile.Read(ref _bakeTotal));
            int done = Math.Max(0, Math.Min(total, Volatile.Read(ref _bakeDone)));
            int percent = (int)Math.Round(done * 100.0 / total);
            if (percent != _lastDisplayedProgress) {
                _lastDisplayedProgress = percent;
                _vm?.SetBakeProgress(percent);
            }

            if (!task.IsCompleted) return;

            _bakeTask = null;
            try {
                SceneNavMeshBaker.Result result = task.GetAwaiter().GetResult();
                string message = result.Message.Length > 0
                    ? result.Message
                    : "Nothing in this project needs a navmesh bake.";
                _vm?.FinishBake(message, succeeded: result.Success || !result.Attempted);
                EditorHud.ShowMessage(message, warning: !result.Success && result.Attempted);
            } catch (Exception ex) {
                _vm?.FinishBake("Navmesh bake failed - see trace log.", succeeded: false);
                TraceLogger.WriteException(nameof(ProjectBrowserScreen),
                    "Project-browser navmesh bake failed", ex);
                EditorHud.ShowMessage("Navmesh bake failed - see CustomSceneCreator.trace.log.",
                    warning: true);
            }
        }
    }
}
