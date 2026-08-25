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
        /// <summary>The layout, to place into other scenes as loose pieces and adapt there.</summary>
        Template,
        /// <summary>A complete uniquely named derived scene folder for the official Modding Kit.</summary>
        ModdingKitScene,
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
                switch (kind) {
                    case ExportKind.Prefab: return ExportPrefab(project, safeName);
                    case ExportKind.Template: return ExportTemplate(project, safeName);
                    case ExportKind.ModdingKitScene: return ModdingKitSceneExporter.Export(project, safeName);
                    default: return ExportSceneFragment(project, safeName);
                }
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

            PrefabInliner.BeginExport();

            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var counters = new Dictionary<string, int>();
            foreach (Editing.ProjectEntity entity in project.Entities) {
                AppendEntity(sb, entity, anchor, counters, indent: "      ",
                             inlineDefinitions: true, skipped: skipped);
            }

            int cutoutMarkers = AppendNavMeshCutoutMarkers(sb, project, anchor, "      ");
            int elevatedRoutes = AppendElevatedNavMeshMarkers(sb, project, anchor, "      ");

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

            // The picker reads the exports folder directly, so the new prefab shows up straight
            // away - flagged until a restart, since the game only reads prefab XML at startup.
            Catalog.PackCatalog.Invalidate();

            TraceLogger.Write(nameof(SceneExporter),
                $"Exported prefab '{name}' ({project.Entities.Count} parts) to {path}");
            string note = skipped.Count == 0
                ? ""
                : $"  {skipped.Count} unknown prefab(s) left out - see the log: " +
                  string.Join(", ", skipped.Take(4));

            return new ExportResult {
                Success = true,
                Path = path,
                Message = $"Prefab '{name}' exported ({project.Entities.Count - skipped.Count} parts)"
                          + (cutoutMarkers > 0 ? $" with {cutoutMarkers} cutout marker(s)" : "")
                          + (elevatedRoutes > 0 ? $" and {elevatedRoutes} elevated route(s) attached" : "") + "." +
                          note + " Listed under 'My Prefabs'; restart the game to place it.",
            };
        }

        /// <summary>
        /// Carries a solid object's navmesh exclusion with a reusable composite prefab. A cutout
        /// is stored in a project as four world-space corners, while Homesteads reads prefab-local
        /// hsr_navcut rectangles. The editor's cutout authoring produces rectangular footprints,
        /// so express each as its centre, full side lengths, and yaw relative to the export anchor.
        /// </summary>
        private static int AppendNavMeshCutoutMarkers(StringBuilder sb, Editing.SceneProject project,
                                                      Vec3 anchor, string indent) {
            if (project.NavMeshCutouts == null) return 0;

            int written = 0;
            foreach (Editing.ProjectNavMeshCutout cutout in project.NavMeshCutouts) {
                if (cutout?.Corners == null || cutout.Corners.Length < 12) continue;

                Vec3 a = new Vec3(cutout.Corners[0], cutout.Corners[1], cutout.Corners[2]);
                Vec3 b = new Vec3(cutout.Corners[3], cutout.Corners[4], cutout.Corners[5]);
                Vec3 c = new Vec3(cutout.Corners[6], cutout.Corners[7], cutout.Corners[8]);
                Vec3 d = new Vec3(cutout.Corners[9], cutout.Corners[10], cutout.Corners[11]);
                Vec3 centre = (a + b + c + d) * 0.25f - anchor;

                float width = DistanceXY(a, b);
                float depth = DistanceXY(b, c);
                if (width < 0.05f || depth < 0.05f) continue;
                float yaw = MathF.Atan2(b.y - a.y, b.x - a.x);

                sb.AppendLine($"{indent}<game_entity name=\"hsr_navcut\" old_prefab_name=\"\">");
                sb.AppendLine($"{indent}  <transform position=\"{F(centre.x)}, {F(centre.y)}, 0.000\" "
                              + $"rotation_euler=\"0.000, 0.000, {F(yaw)}\" "
                              + $"scale=\"{F(width)}, {F(depth)}, 1.000\"/>");
                sb.AppendLine($"{indent}</game_entity>");
                written++;
            }
            return written;
        }

        /// <summary>
        /// Carries an elevated surface out with a reusable prefab instead of leaving it as an
        /// absolute scene-only annotation. Homesteads reads these lightweight groups when it bakes
        /// a placed asset and applies the asset's own position and yaw to every point.
        ///
        /// The export anchor is also the prefab root's local origin, so subtracting it is the same
        /// coordinate conversion used for the visible child entities above. The groups are only
        /// authoring metadata: they have no mesh, physics, scripts, or in-game visual.
        /// </summary>
        private static int AppendElevatedNavMeshMarkers(StringBuilder sb, Editing.SceneProject project,
                                                        Vec3 anchor, string indent) {
            if (project.NavMeshRamps == null) return 0;

            int written = 0;
            foreach (Editing.ProjectNavMeshRamp ramp in project.NavMeshRamps) {
                if (ramp == null || ramp.IsDraft || !Editing.NavMeshRampAuthoring.IsOutlineUsable(ramp)) continue;

                int count = Editing.NavMeshRampAuthoring.PointCount(ramp.Outline);
                if (count < 4) continue;

                sb.AppendLine($"{indent}<game_entity name=\"hsr_navelevated\" old_prefab_name=\"\">");
                sb.AppendLine($"{indent}  <children>");
                for (int i = 0; i < count; i++) {
                    Vec3 point = Editing.NavMeshRampAuthoring.Point(ramp.Outline, i) - anchor;
                    sb.AppendLine($"{indent}    <game_entity name=\"hsr_navpoint\" old_prefab_name=\"\">");
                    sb.AppendLine($"{indent}      <transform position=\"{F(point.x)}, {F(point.y)}, {F(point.z)}\"/>");
                    sb.AppendLine($"{indent}    </game_entity>");
                }
                sb.AppendLine($"{indent}  </children>");
                sb.AppendLine($"{indent}</game_entity>");
                written++;
            }
            return written;
        }

        // -- scene fragment ----------------------------------------------------------------------

        /// <summary>
        /// The placed objects at their real positions, as a block that pastes into a scene.xscene.
        ///
        /// This is the absolute-coordinate fragment handoff for a scene that another module already
        /// owns. A matching navmesh plan is written whenever the project contains any authoring.
        /// </summary>
        private static ExportResult ExportSceneFragment(Editing.SceneProject project, string name) {
            var sb = new StringBuilder();
            sb.AppendLine($"<!-- Exported by Custom Scene Creator on {DateTime.Now:yyyy-MM-dd HH:mm:ss} -->");
            sb.AppendLine($"<!-- Scene: {Escape(project.TargetScene)}   Levels: {Escape(project.SceneLevels)} -->");
            sb.AppendLine($"<!-- {project.Entities.Count} entities, absolute scene coordinates. -->");
            sb.AppendLine("<!-- Paste these inside the <entities> block of the target scene.xscene. -->");

            sb.Append(BuildSceneEntityFragment(project, indent: ""));

            string path = IOPath.Combine(
                Editing.ProjectSerializer.SceneExportsPath, name + ".scene_fragment.xml");
            IOFile.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            if ((project.NavMeshCutouts != null && project.NavMeshCutouts.Count > 0)
                || (project.NavMeshRequirements != null && project.NavMeshRequirements.Count > 0)
                || (project.NavMeshRamps != null && project.NavMeshRamps.Any(r => r != null && !r.IsDraft)))
                NavMeshCutoutManifestExporter.WriteDocumentsCopy(project, name);

            TraceLogger.Write(nameof(SceneExporter),
                $"Exported scene fragment '{name}' ({project.Entities.Count} entities) to {path}");
            return new ExportResult {
                Success = true,
                Path = path,
                Message = $"Scene fragment '{name}' exported ({project.Entities.Count} entities).",
            };
        }

        /// <summary>Placed entities in scene.xscene form, shared by fragment and Kit-scene export.</summary>
        internal static string BuildSceneEntityFragment(Editing.SceneProject project, string indent) {
            var sb = new StringBuilder();
            var counters = new Dictionary<string, int>();
            foreach (Editing.ProjectEntity entity in project.Entities)
                AppendEntity(sb, entity, Vec3.Zero, counters, indent);
            return sb.ToString();
        }

        // -- template ----------------------------------------------------------------------------

        /// <summary>
        /// The layout, saved where it can be placed into any other scene.
        ///
        /// Written as project JSON rather than prefab XML on purpose. The engine never needs to know
        /// about it - this mod reads it - so a template is usable the moment it is written, with no
        /// restart, and its pieces stay individually editable when placed. That is the whole point:
        /// a prefab is sealed, a template is a starting point.
        ///
        /// The scene it was built on is kept for reference but means nothing on placement; the
        /// pieces are re-anchored to wherever they are dropped.
        /// </summary>
        private static ExportResult ExportTemplate(Editing.SceneProject project, string name) {
            var copy = new Editing.SceneProject {
                Name = name,
                TargetScene = project.TargetScene,
                SceneLevels = project.SceneLevels,
                Entities = project.Entities,
            };

            string path = IOPath.Combine(Editing.ProjectSerializer.TemplateExportsPath, name + ".json");
            IOFile.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(copy,
                Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);

            // Listed straight away - nothing has to be registered with the engine first.
            Catalog.PackCatalog.Invalidate();

            TraceLogger.Write(nameof(SceneExporter),
                $"Exported template '{name}' ({project.Entities.Count} pieces) to {path}");
            return new ExportResult {
                Success = true,
                Path = path,
                Message = $"Template '{name}' exported ({project.Entities.Count} pieces). " +
                          "It is in the picker under 'My Templates' now - no restart needed.",
            };
        }

        // -- shared ------------------------------------------------------------------------------

        /// <summary>
        /// Writes one entity. Editor-authored markers export under their declared NAME and TAG rather
        /// than as their stand-in mesh - that is the whole point of them. A cube in the editor becomes
        /// sp_enemy_1 tagged sp_enemy, which is what FindEntitiesWithTag will later look for.
        /// </summary>
        private static void AppendEntity(StringBuilder sb, Editing.ProjectEntity entity, Vec3 anchor,
                                         Dictionary<string, int> counters, string indent,
                                         bool inlineDefinitions = false,
                                         HashSet<string>? skipped = null) {
            Placeable? placeable = PlaceableRegistry.Find(entity.Prefab);

            Vec3 position = new Vec3(entity.Pos[0], entity.Pos[1], entity.Pos[2]) - anchor;
            Vec3 euler = entity.To().Rotation.GetEulerAngles();

            string transform =
                $"<transform position=\"{F(position.x)}, {F(position.y)}, {F(position.z)}\" " +
                $"rotation_euler=\"{F(euler.x)}, {F(euler.y)}, {F(euler.z)}\"/>";

            bool isMarker = placeable != null && placeable.ExportName.Length > 0;
            if (isMarker) {
                string entityName = ResolveName(placeable!.ExportName, counters, entity.Index);
                sb.AppendLine($"{indent}<game_entity name=\"{Escape(entityName)}\" old_prefab_name=\"\">");
                sb.AppendLine($"{indent}  {transform}");
                if (placeable.ExportTag.Length > 0) {
                    sb.AppendLine($"{indent}  <tags>");
                    sb.AppendLine($"{indent}    <tag name=\"{Escape(placeable.ExportTag)}\"/>");
                    sb.AppendLine($"{indent}  </tags>");
                }
                AppendScripts(sb, entity, indent + "  ");
                sb.AppendLine($"{indent}</game_entity>");
            } else if (inlineDefinitions) {
                // A world prefab may not reference another prefab - see PrefabInliner. The
                // definition is copied in instead, so the exported file draws something.
                System.Xml.XmlElement? definition = PrefabInliner.Find(entity.Prefab);
                if (definition != null) {
                    counters.TryGetValue(entity.Prefab, out int n);
                    counters[entity.Prefab] = ++n;
                    PrefabInliner.Append(sb, definition, entity.Prefab, transform, indent, n);
                } else {
                    // SKIPPED, not written as a reference.
                    //
                    // An unresolvable prefab reference inside a world prefab is not a missing
                    // object - it is a CRASH. The engine loads every module prefab at startup and
                    // resolves the references as it goes, so one bad name takes the whole game down
                    // before the main menu, with nothing to say which file did it.
                    //
                    // That is exactly what happened: a project referencing marker ids from a pack
                    // that was not installed exported them as references to prefabs that do not
                    // exist, and the game stopped loading entirely.
                    skipped?.Add(entity.Prefab);
                    TraceLogger.Write(nameof(SceneExporter),
                        $"SKIPPED '{entity.Prefab}': no definition found, and a reference to a " +
                        "prefab that does not exist crashes the game at startup. Is the pack or " +
                        "module that defines it installed?");
                }
            } else {
                sb.AppendLine($"{indent}<game_entity prefab=\"{Escape(entity.Prefab)}\">");
                sb.AppendLine($"{indent}  {transform}");
                AppendScripts(sb, entity, indent + "  ");
                sb.AppendLine($"{indent}</game_entity>");
            }
        }

        /// <summary>
        /// Writes attached scripts in the shape the engine reads them back:
        ///
        ///   &lt;scripts&gt;&lt;script name="LightCycle"&gt;&lt;variables&gt;
        ///     &lt;variable name="alwaysBurn" value="true"/&gt;
        ///
        /// This is the half that makes attachment worth anything - a fire attached in the editor is
        /// only a fire in the finished scene if it survives the write-out.
        /// </summary>
        private static void AppendScripts(StringBuilder sb, Editing.ProjectEntity entity, string indent) {
            if (entity.Scripts == null || entity.Scripts.Count == 0) return;

            sb.AppendLine($"{indent}<scripts>");
            foreach (Editing.ProjectScript script in entity.Scripts) {
                if (string.IsNullOrWhiteSpace(script.Name)) continue;

                bool hasVariables = script.Variables != null && script.Variables.Count > 0;
                if (!hasVariables) {
                    sb.AppendLine($"{indent}  <script name=\"{Escape(script.Name)}\"/>");
                    continue;
                }

                sb.AppendLine($"{indent}  <script name=\"{Escape(script.Name)}\">");
                sb.AppendLine($"{indent}    <variables>");
                foreach (KeyValuePair<string, string> variable in script.Variables) {
                    sb.AppendLine($"{indent}      <variable name=\"{Escape(variable.Key)}\" " +
                                  $"value=\"{Escape(variable.Value)}\"/>");
                }
                sb.AppendLine($"{indent}    </variables>");
                sb.AppendLine($"{indent}  </script>");
            }
            sb.AppendLine($"{indent}</scripts>");
        }

        /// <summary>Replaces {index} with a per-pattern counter, giving sp_enemy_1, sp_enemy_2, ...</summary>
        /// <summary>
        /// Fills in a marker's number.
        ///
        /// The number stored on the marker wins: it is what the editor showed while the scene was
        /// being laid out, and renumbering here would break the correspondence between what someone
        /// arranged and what their mod code goes looking for. The running counter is only a fallback
        /// for markers saved before numbering existed.
        /// </summary>
        private static string ResolveName(string pattern, Dictionary<string, int> counters, int stored) {
            if (pattern.IndexOf("{index}", StringComparison.OrdinalIgnoreCase) < 0) return pattern;

            int index = stored;
            if (index <= 0) {
                counters.TryGetValue(pattern, out int next);
                index = next + 1;
            }

            // Tracked either way, so a fallback never lands on a number already claimed.
            if (!counters.TryGetValue(pattern, out int highest) || index > highest) counters[pattern] = index;

            return pattern.Replace("{index}", index.ToString(CultureInfo.InvariantCulture));
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

        private static float DistanceXY(Vec3 a, Vec3 b) {
            float x = b.x - a.x, y = b.y - a.y;
            return MathF.Sqrt(x * x + y * y);
        }

        private static string Escape(string value) =>
            (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private static ExportResult Fail(string message) =>
            new ExportResult { Success = false, Message = message };
    }
}
