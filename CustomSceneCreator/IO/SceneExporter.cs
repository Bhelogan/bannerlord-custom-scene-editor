using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
using TaleWorlds.Library;
using IODirectory = System.IO.Directory;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;

namespace CustomSceneCreator.IO {
    public enum ExportKind {
        /// <summary>Just what was built, positions relative to an anchor. Reusable as one object.</summary>
        Prefab,
        /// <summary>Everything, where it actually sits, tied to its scene.</summary>
        SceneFragment,
    }

    public class ExportResult {
        public bool Success;
        public string Path = "";
        public string Message = "";
    }

    /// <summary>
    /// Writes a project out in the two forms it is worth having.
    ///
    /// Both come from the SAME project data - absolute positions, rotations, scene name - so this is
    /// two transforms rather than two formats. A prefab is that data re-expressed relative to an
    /// anchor with the scene dropped; a scene fragment is that data as-is. That is why saving never
    /// has to ask which one you meant: saving stores the project, and exporting decides the shape.
    /// </summary>
    public static class SceneExporter {
        public static ExportResult Export(Editing.SceneProject project, ExportKind kind, string name) {
            try {
                if (project == null || project.Entities.Count == 0) {
                    return Fail("Nothing to export - place something first.");
                }
                if (string.IsNullOrWhiteSpace(name)) {
                    return Fail("Give the export a name.");
                }

                string safeName = Editing.ProjectSerializer.SanitizeFileName(name.Trim());
                return kind == ExportKind.Prefab
                    ? ExportPrefab(project, safeName)
                    : ExportSceneFragment(project, safeName);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneExporter), $"Export ({kind}) failed", ex);
                return Fail($"Export failed: {ex.Message}");
            }
        }

        // -- prefab ------------------------------------------------------------------------------

        /// <summary>
        /// One composite prefab, instantiable by name.
        ///
        /// The anchor is the centroid in X/Y and the LOWEST point in Z. Centroid alone would put the
        /// origin halfway up the object, so placing it later would bury the bottom half; taking the
        /// floor for Z means the thing sits on the ground when placed, which is what you want for a
        /// pig pen or a building.
        /// </summary>
        private static ExportResult ExportPrefab(Editing.SceneProject project, string name) {
            Vec3 anchor = ComputeAnchor(project);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine($"<!-- Exported by Custom Scene Creator on {DateTime.Now:yyyy-MM-dd HH:mm:ss} -->");
            sb.AppendLine($"<!-- Source project '{Escape(project.Name)}' on scene '{Escape(project.TargetScene)}' -->");
            sb.AppendLine($"<!-- anchor: {F(anchor.x)}, {F(anchor.y)}, {F(anchor.z)} (centroid XY, lowest Z) -->");
            sb.AppendLine("<prefabs>");
            sb.AppendLine($"  <game_entity name=\"{Escape(name)}\" old_prefab_name=\"\">");
            sb.AppendLine("    <children>");

            var counters = new Dictionary<string, int>();
            foreach (Editing.ProjectEntity entity in project.Entities) {
                AppendEntity(sb, entity, anchor, counters, indent: "      ");
            }

            sb.AppendLine("    </children>");
            sb.AppendLine("  </game_entity>");
            sb.AppendLine("</prefabs>");

            // Written into our own module so the game loads it on next launch and the prefab becomes
            // instantiable by name - including from this editor, via the pack entry below.
            string xml = sb.ToString();

            // Two copies on purpose. The module folder is what makes the prefab loadable by the game;
            // the Documents copy is what survives a reinstall and is what you send to someone else.
            string moduleDir = ModulePath("Prefabs");
            if (moduleDir.Length == 0) return Fail("Could not find the module's Prefabs folder.");
            IODirectory.CreateDirectory(moduleDir);
            string path = IOPath.Combine(moduleDir, name + ".xml");
            IOFile.WriteAllText(path, xml, Encoding.UTF8);

            try {
                IOFile.WriteAllText(
                    IOPath.Combine(Editing.ProjectSerializer.PrefabExportsPath, name + ".xml"), xml, Encoding.UTF8);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneExporter), $"Documents copy failed: {ex.Message}");
            }

            RegisterExportedPrefab(name);

            TraceLogger.Write(nameof(SceneExporter),
                $"Exported prefab '{name}' ({project.Entities.Count} parts) to {path}");
            return new ExportResult {
                Success = true,
                Path = path,
                Message = $"Prefab '{name}' exported ({project.Entities.Count} parts). " +
                          "Restart the game to place it as a single object.",
            };
        }

        /// <summary>
        /// Adds the exported prefab to a pack so it appears in the asset picker next session.
        ///
        /// Kept in its own file rather than csc_core.xml, which ships with the mod and would be
        /// overwritten on update.
        /// </summary>
        private static void RegisterExportedPrefab(string name) {
            try {
                string dir = ModulePath(IOPath.Combine("ModuleData", "packs"));
                if (dir.Length == 0) return;
                IODirectory.CreateDirectory(dir);
                string path = IOPath.Combine(dir, "csc_exported.xml");

                var entries = new List<string>();
                if (IOFile.Exists(path)) {
                    foreach (string line in IOFile.ReadAllLines(path)) {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("<Placeable ", StringComparison.Ordinal)
                            && !trimmed.Contains($"id=\"{name}\"")) {
                            entries.Add(trimmed);
                        }
                    }
                }
                entries.Add($"<Placeable id=\"{Escape(name)}\" display=\"{Escape(Placeable.ToDisplayName(name))}\" " +
                            $"category=\"Exported\" proxy=\"{Escape(name)}\" />");

                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                sb.AppendLine("<!-- Prefabs exported from the editor. Regenerated on each export. -->");
                sb.AppendLine("<Placeables>");
                foreach (string entry in entries) sb.AppendLine("  " + entry);
                sb.AppendLine("</Placeables>");
                IOFile.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            } catch (Exception ex) {
                // Not fatal: the prefab XML is written either way, this only affects discoverability.
                TraceLogger.Write(nameof(SceneExporter), $"Could not register exported prefab: {ex.Message}");
            }
        }

        // -- scene fragment ----------------------------------------------------------------------

        /// <summary>
        /// The placed objects at their real positions, as a block that pastes into a scene.xscene.
        ///
        /// This is the Modding Kit handoff: lay the scene out in-game where it is pleasant, then open
        /// the Kit once to bake a navmesh over the result.
        /// </summary>
        private static ExportResult ExportSceneFragment(Editing.SceneProject project, string name) {
            var sb = new StringBuilder();
            sb.AppendLine($"<!-- Exported by Custom Scene Creator on {DateTime.Now:yyyy-MM-dd HH:mm:ss} -->");
            sb.AppendLine($"<!-- Scene: {Escape(project.TargetScene)}   Levels: {Escape(project.SceneLevels)} -->");
            sb.AppendLine($"<!-- {project.Entities.Count} entities, absolute scene coordinates. -->");
            sb.AppendLine("<!-- Paste these inside the <entities> block of the target scene.xscene. -->");

            var counters = new Dictionary<string, int>();
            foreach (Editing.ProjectEntity entity in project.Entities) {
                AppendEntity(sb, entity, Vec3.Zero, counters, indent: "");
            }

            string path = IOPath.Combine(
                Editing.ProjectSerializer.SceneExportsPath, name + ".scene_fragment.xml");
            IOFile.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            TraceLogger.Write(nameof(SceneExporter),
                $"Exported scene fragment '{name}' ({project.Entities.Count} entities) to {path}");
            return new ExportResult {
                Success = true,
                Path = path,
                Message = $"Scene fragment '{name}' exported ({project.Entities.Count} entities).",
            };
        }

        // -- shared ------------------------------------------------------------------------------

        /// <summary>
        /// Writes one entity. Editor-authored markers export under their declared NAME and TAG rather
        /// than as their stand-in mesh - that is the whole point of them. A cube in the editor becomes
        /// sp_enemy_1 tagged sp_enemy, which is what FindEntitiesWithTag will later look for.
        /// </summary>
        private static void AppendEntity(StringBuilder sb, Editing.ProjectEntity entity, Vec3 anchor,
                                         Dictionary<string, int> counters, string indent) {
            Placeable? placeable = PlaceableRegistry.Find(entity.Prefab);

            Vec3 position = new Vec3(entity.Pos[0], entity.Pos[1], entity.Pos[2]) - anchor;
            Vec3 euler = entity.To().Rotation.GetEulerAngles();

            string transform =
                $"<transform position=\"{F(position.x)}, {F(position.y)}, {F(position.z)}\" " +
                $"rotation_euler=\"{F(euler.x)}, {F(euler.y)}, {F(euler.z)}\"/>";

            bool isMarker = placeable != null && placeable.ExportName.Length > 0;
            if (isMarker) {
                string entityName = ResolveName(placeable!.ExportName, counters);
                sb.AppendLine($"{indent}<game_entity name=\"{Escape(entityName)}\" old_prefab_name=\"\">");
                sb.AppendLine($"{indent}  {transform}");
                if (placeable.ExportTag.Length > 0) {
                    sb.AppendLine($"{indent}  <tags>");
                    sb.AppendLine($"{indent}    <tag name=\"{Escape(placeable.ExportTag)}\"/>");
                    sb.AppendLine($"{indent}  </tags>");
                }
                sb.AppendLine($"{indent}</game_entity>");
            } else {
                sb.AppendLine($"{indent}<game_entity prefab=\"{Escape(entity.Prefab)}\">");
                sb.AppendLine($"{indent}  {transform}");
                sb.AppendLine($"{indent}</game_entity>");
            }
        }

        /// <summary>Replaces {index} with a per-pattern counter, giving sp_enemy_1, sp_enemy_2, ...</summary>
        private static string ResolveName(string pattern, Dictionary<string, int> counters) {
            if (pattern.IndexOf("{index}", StringComparison.OrdinalIgnoreCase) < 0) return pattern;
            counters.TryGetValue(pattern, out int next);
            next++;
            counters[pattern] = next;
            return pattern.Replace("{index}", next.ToString(CultureInfo.InvariantCulture));
        }

        private static Vec3 ComputeAnchor(Editing.SceneProject project) {
            float sumX = 0f, sumY = 0f, minZ = float.MaxValue;
            foreach (Editing.ProjectEntity e in project.Entities) {
                sumX += e.Pos[0];
                sumY += e.Pos[1];
                if (e.Pos[2] < minZ) minZ = e.Pos[2];
            }
            int count = project.Entities.Count;
            return new Vec3(sumX / count, sumY / count, minZ);
        }

        private static string ModulePath(string relative) {
            try {
                foreach (string root in new[] {
                    IOPath.Combine(BasePath.Name, "Modules", "CustomSceneCreator"),
                }) {
                    if (IODirectory.Exists(root)) return IOPath.Combine(root, relative);
                }
            } catch { }
            return "";
        }

        /// <summary>Invariant culture: a comma decimal separator would corrupt the XML outright.</summary>
        private static string F(float value) => value.ToString("0.000", CultureInfo.InvariantCulture);

        private static string Escape(string value) =>
            (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private static ExportResult Fail(string message) =>
            new ExportResult { Success = false, Message = message };
    }
}
