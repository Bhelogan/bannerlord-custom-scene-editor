using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Boot;
using CustomSceneCreator.Editing;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

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

        /// <summary>Hides the markers left by the last navmesh audit.</summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("navmesh_audit_clear", "csc")]
        public static string NavMeshAuditClear(List<string> args) {
            NavMeshAuditMarkers.Clear();
            return "Navmesh audit markers hidden.";
        }

        /// <summary>
        /// Checks a project's whole baked navmesh and reports every problem with a position.
        ///
        /// This is the check to run before shipping a scene. A test battle only ever exercises the
        /// ground the fight happens on; this walks the entire mesh and names the places that will
        /// misbehave, so a problem forty metres away is found here rather than by a player.
        ///
        /// Usage:
        ///   csc.navmesh_audit &lt;project_name&gt;
        /// </summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("navmesh_audit", "csc")]
        public static string NavMeshAuditCommand(List<string> args) {
            string nl = System.Environment.NewLine;
            if (args == null || args.Count == 0) {
                return "Usage: csc.navmesh_audit <project_name>" + nl +
                       "Run csc.projects to list available projects.";
            }

            SceneProject? project = ProjectSerializer.Load(args[0]);
            if (project == null) {
                return $"No saved project named '{args[0]}'. Run csc.projects to list them.";
            }

            try {
                NavMesh.NavMeshAuditReport? report =
                    IO.SceneNavMeshBaker.AuditProject(project, out string source);
                if (report == null) {
                    return $"No navmesh found for '{project.TargetScene}', and this project has not baked one yet.";
                }

                // Hand the findings to the editor so they appear as rings in the world. The console
                // list says what is wrong; the markers say where to stand to see it.
                NavMeshAuditMarkers.Show(report, project.TargetScene);

                var lines = new List<string> {
                    report.Headline,
                    "Checked: " + source,
                    $"Faces {report.Summary.Faces:N0}, walkable {report.WalkableArea:N0} m2, "
                        + $"{report.IslandSizes.Count} separate area(s).",
                };

                if (report.Findings.Count == 0) {
                    lines.Add("Nothing to fix.");
                } else {
                    lines.Add("Findings are marked in the scene with rings and masts - "
                              + "red breaks movement, amber is worth a look. "
                              + "Run csc.navmesh_audit_clear to hide them.");
                    // Serious findings first: the list can be long on a busy scene, and the things
                    // that break movement should not be below the things that merely take up space.
                    foreach (NavMesh.NavMeshFinding finding in report.Findings) {
                        if (finding.IsSerious) lines.Add(finding.ToString());
                    }
                    foreach (NavMesh.NavMeshFinding finding in report.Findings) {
                        if (!finding.IsSerious) lines.Add(finding.ToString());
                    }
                }
                return string.Join(nl, lines);
            } catch (Exception exc) {
                return $"Navmesh audit failed: {exc.GetType().Name}: {exc.Message}";
            }
        }

        /// <summary>
        /// Opens a battle mission on the project's scene with enemy infantry on the opposite side of
        /// the placed buildings. Used to test whether the candidate navmesh.bin routes enemies around
        /// cutout holes rather than letting them walk through houses.
        ///
        /// Usage:
        ///   csc.navtest &lt;project_name&gt; [enemy_count]
        ///   csc.navtest battle_terrain_008 10
        ///
        /// Player and enemy positions are derived from the project's navmesh cutout corners. If the
        /// project has no cutouts, the player spawns at the entity centroid and enemies 40 m away.
        /// </summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("navtest", "csc")]
        public static string NavTest(List<string> args) {
            string nl = System.Environment.NewLine;
            if (args == null || args.Count == 0) {
                return "Usage: csc.navtest <project_name> [enemy_count]" + nl +
                       "Example: csc.navtest battle_terrain_008 10" + nl +
                       "Example: csc.navtest battle_terrain_008" + nl +
                       "Run csc.projects to list available projects.";
            }

            string name = args[0];
            int enemyCount = args.Count > 1 && int.TryParse(args[1], out int n) ? n : 8;

            SceneProject? project = ProjectSerializer.Load(name);
            if (project == null) {
                return $"No saved project named '{name}'. Run csc.projects to list them.";
            }

            if (Campaign.Current == null) {
                return "This command requires an active campaign.";
            }

            try {
                (var playerPos, var enemyPos) = NavMeshTestBattleLogic.DerivePositionsFromProject(project);

                Mission? mission = NavMeshTestMission.Open(
                    project.TargetScene, project.SceneLevels, playerPos, enemyPos, enemyCount);

                return mission != null
                    ? $"Opening navmesh-test battle on '{project.TargetScene}' — {enemyCount} enemies. " +
                      $"Player: ({playerPos.x:0.#},{playerPos.y:0.#})  " +
                      $"Enemies: ({enemyPos.x:0.#},{enemyPos.y:0.#})"
                    : $"Failed to open battle on '{project.TargetScene}'. See CustomSceneCreator.trace.log.";
            } catch (Exception exc) {
                return $"Error opening navmesh test: {exc.GetType().Name}: {exc.Message}";
            }
        }

        /// <summary>
        /// Opens a battle mission on any scene with explicit player and enemy coordinates.
        /// Use this when you want to test a specific navmesh without a saved project.
        ///
        /// Usage:
        ///   csc.navtest_at &lt;scene_name&gt; &lt;player_x&gt; &lt;player_y&gt; &lt;enemy_x&gt; &lt;enemy_y&gt; [enemy_count] [scene_levels]
        ///
        /// Example:
        ///   csc.navtest_at csc_bt08_test 750 374 810 380 10
        /// </summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("navtest_at", "csc")]
        public static string NavTestAt(List<string> args) {
            string nl = System.Environment.NewLine;
            if (args == null || args.Count < 5) {
                return "Usage: csc.navtest_at <scene> <px> <py> <ex> <ey> [enemy_count] [levels]" + nl +
                       "Example: csc.navtest_at csc_bt08_test 750 374 810 380 10";
            }

            string scene = args[0];
            if (!float.TryParse(args[1], out float px) || !float.TryParse(args[2], out float py)
                || !float.TryParse(args[3], out float ex) || !float.TryParse(args[4], out float ey)) {
                return "Could not parse coordinates. Use numeric values for px py ex ey.";
            }

            int enemyCount = 8;
            string levels = "";
            if (args.Count > 5) {
                if (int.TryParse(args[5], out int n)) {
                    enemyCount = n;
                    if (args.Count > 6) levels = string.Join(" ", args.Skip(6));
                } else {
                    levels = string.Join(" ", args.Skip(5));
                }
            }

            if (Campaign.Current == null) {
                return "This command requires an active campaign.";
            }

            try {
                Vec3 playerPos = new Vec3(px, py, 0f);
                Vec3 enemyPos = new Vec3(ex, ey, 0f);

                Mission? mission = NavMeshTestMission.Open(
                    scene, levels, playerPos, enemyPos, enemyCount);

                return mission != null
                    ? $"Opening navmesh-test battle on '{scene}' — {enemyCount} enemies. " +
                      $"Player: ({px:0.#},{py:0.#})  Enemies: ({ex:0.#},{ey:0.#})"
                    : $"Failed to open battle on '{scene}'. See CustomSceneCreator.trace.log.";
            } catch (Exception exc2) {
                return $"Error opening navmesh test: {exc2.GetType().Name}: {exc2.Message}";
            }
        }
    }
}
