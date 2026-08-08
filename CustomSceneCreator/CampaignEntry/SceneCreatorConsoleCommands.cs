using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Library;

namespace CustomSceneCreator.CampaignEntry {
    /// <summary>
    /// Console commands. These are the fastest way to try an arbitrary scene without waiting on the
    /// browser UI, and they stay useful afterwards for reproducing a specific scene + level
    /// combination when something misbehaves.
    /// </summary>
    public static class SceneCreatorConsoleCommands {
        [CommandLineFunctionality.CommandLineArgumentFunction("open", "csc")]
        public static string Open(List<string> args) {
            if (args == null || args.Count == 0) {
                return "Usage: csc.open <scene_name> [scene_levels]\n" +
                       $"Example: csc.open {SceneCreatorEntry.DefaultScene}\n" +
                       "Example: csc.open aserai_town_a \"base level_1 civilian\"";
            }

            string scene = args[0];
            // Levels are space-separated and may be passed either quoted as one argument or as
            // several bare ones; accept both rather than making the caller remember which.
            string levels = args.Count > 1 ? string.Join(" ", args.Skip(1)) : "";

            return SceneCreatorEntry.OpenEditor(scene, levels)
                ? $"Opening scene creator on '{scene}'" + (levels.Length > 0 ? $" (levels: {levels})" : "") + "."
                : $"Failed to open '{scene}'. See CustomSceneCreator.trace.log.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("projects", "csc")]
        public static string Projects(List<string> args) {
            string nl = System.Environment.NewLine;
            var all = Editing.ProjectSerializer.LoadAll();
            if (all.Count == 0) {
                return "No saved projects yet. They are written to:" + nl + Editing.ProjectSerializer.ProjectsPath;
            }
            return $"{all.Count} project(s) in {Editing.ProjectSerializer.ProjectsPath}:" + nl +
                   string.Join(nl, all.Select(p =>
                       $"{p.Name}  [{p.TargetScene}]  {p.Entities.Count} object(s)  {p.Modified:yyyy-MM-dd HH:mm}"));
        }

        /// <summary>Reopens a saved project on its own scene, with everything as it was left.</summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("project", "csc")]
        public static string Project(List<string> args) {
            string nl = System.Environment.NewLine;
            if (args == null || args.Count == 0) {
                return "Usage: csc.project <project_name>" + nl + "Run csc.projects to list them.";
            }

            string name = string.Join(" ", args);
            Editing.SceneProject? project = Editing.ProjectSerializer.Load(name);
            if (project == null) return $"No saved project named '{name}'. Run csc.projects to list them.";

            return SceneCreatorEntry.OpenEditor(project.TargetScene, project.SceneLevels, project.Name)
                ? $"Opening '{project.Name}' on '{project.TargetScene}' ({project.Entities.Count} object(s))."
                : $"Failed to open '{project.Name}'. See CustomSceneCreator.trace.log.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("projects_browse", "csc")]
        public static string ProjectsBrowse(List<string> args) {
            UI.ProjectBrowserScreen.Open();
            return "Opening the saved-project browser.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("browse", "csc")]
        public static string Browse(List<string> args) {
            UI.SceneBrowserScreen.Open();
            return $"Opening scene browser ({Catalog.SceneCatalog.All.Count} scenes).";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("list", "csc")]
        public static string List(List<string> args) {
            string filter = args != null && args.Count > 0 ? args[0] : "";
            var matches = Catalog.SceneCatalog.All
                .Where(s => filter.Length == 0
                         || s.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0
                         || s.Category.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(60)
                .Select(s => $"{s.Name}  [{s.Category}]" + (s.IsWalkable ? "" : "  (no navmesh)"))
                .ToList();

            if (matches.Count == 0) return $"No scenes matched '{filter}'.";
            return $"{matches.Count} shown of {Catalog.SceneCatalog.All.Count}:\n" + string.Join("\n", matches);
        }
    }
}
