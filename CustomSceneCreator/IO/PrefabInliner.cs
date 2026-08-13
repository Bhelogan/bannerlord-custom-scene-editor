using System;
using System.Collections.Generic;
using System.Xml;
using CustomSceneCreator.Catalog;
using TaleWorlds.Library;
using IOPath = System.IO.Path;

namespace CustomSceneCreator.IO {
    /// <summary>
    /// Copies a prefab's real definition out of the game's own files, so an exported composite can
    /// contain it rather than point at it.
    ///
    /// This exists because of a hard limit in the format: a world prefab CANNOT reference another
    /// prefab. Scenes can - a scene.xscene is full of <c>&lt;game_entity prefab="barrel_a"&gt;</c> -
    /// but a file under a module's <c>Prefabs\</c> folder may only spell its geometry out, as
    /// meshes and physics shapes. Across all 20,983 entities in Native's world prefabs there is not
    /// one nested prefab reference.
    ///
    /// Exporting with <c>prefab="…"</c> children therefore produced a prefab the game accepted and
    /// then rendered as nothing: no ghost in the picker, and nothing placeable.
    ///
    /// So each placed object is expanded here into the definition it came from - its meshes, its
    /// physics, its own children, its flags - transformed into position. The source files are read
    /// once and cached; a refuge is 90 objects drawn from a handful of files.
    /// </summary>
    internal static class PrefabInliner {
        /// <summary>Prefab name -> its definition element, cached across one export.</summary>
        private static readonly Dictionary<string, XmlElement?> Cache =
            new Dictionary<string, XmlElement?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>File path -> parsed document, so one file is read once however many prefabs it holds.</summary>
        private static readonly Dictionary<string, XmlDocument?> Documents =
            new Dictionary<string, XmlDocument?>(StringComparer.OrdinalIgnoreCase);

        public static void BeginExport() {
            Cache.Clear();
            Documents.Clear();
        }

        /// <summary>
        /// The definition for a prefab, or null when it cannot be found.
        ///
        /// Null is a real outcome, not just an error path: a prefab from a mod the exporter cannot
        /// locate still deserves an export, so the caller falls back to a plain reference and says
        /// so rather than dropping the object.
        /// </summary>
        public static XmlElement? Find(string prefabName) {
            if (Cache.TryGetValue(prefabName, out XmlElement? cached)) return cached;

            XmlElement? found = null;
            try {
                Placeable? placeable = PlaceableRegistry.Find(prefabName);
                if (placeable != null && placeable.SourcePath.Length > 0 && placeable.Module.Length > 0) {
                    string path = IOPath.Combine(BasePath.Name, "Modules", placeable.Module, placeable.SourcePath);
                    XmlDocument? document = Load(path);
                    if (document != null) found = FindEntity(document, prefabName);
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(PrefabInliner), $"Could not read '{prefabName}': {ex.Message}");
            }

            Cache[prefabName] = found;
            return found;
        }

        private static XmlDocument? Load(string path) {
            if (Documents.TryGetValue(path, out XmlDocument? cached)) return cached;

            XmlDocument? document = null;
            try {
                if (System.IO.File.Exists(path)) {
                    document = new XmlDocument();
                    document.Load(path);
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(PrefabInliner), $"Could not parse '{path}': {ex.Message}");
                document = null;
            }

            Documents[path] = document;
            return document;
        }

        private static XmlElement? FindEntity(XmlDocument document, string prefabName) {
            XmlNodeList? nodes = document.GetElementsByTagName("game_entity");
            if (nodes == null) return null;

            foreach (XmlNode node in nodes) {
                if (node is XmlElement element &&
                    string.Equals(element.GetAttribute("name"), prefabName, StringComparison.OrdinalIgnoreCase)) {
                    // Only a TOP-LEVEL definition counts. The same name can appear on a child
                    // somewhere else, and copying that would inline a fragment of another prefab.
                    if (element.ParentNode is XmlElement parent && parent.Name == "children") continue;
                    return element;
                }
            }
            return null;
        }

        /// <summary>
        /// Writes one placed object as an inlined copy of its definition.
        ///
        /// The wrapper carries the placement; everything inside is the source prefab's own content,
        /// copied unchanged. Child transforms are relative to the wrapper, so they need no
        /// adjustment - which is what makes a multi-mesh prefab come out looking like itself rather
        /// than a pile of meshes at one point.
        /// </summary>
        public static bool Append(System.Text.StringBuilder sb, XmlElement definition, string prefabName,
                                  string transform, string indent, int index) {
            try {
                sb.AppendLine($"{indent}<game_entity name=\"{Escape(prefabName)}_{index}\" " +
                              $"old_prefab_name=\"{Escape(prefabName)}\">");
                sb.AppendLine($"{indent}  {transform}");

                foreach (XmlNode child in definition.ChildNodes) {
                    if (!(child is XmlElement element)) continue;

                    // The definition's own transform is replaced by the placement; everything else -
                    // components, physics, flags, tags, scripts, children - is copied verbatim.
                    if (element.Name == "transform") continue;

                    foreach (string line in element.OuterXml.Split('\n')) {
                        sb.AppendLine($"{indent}  {line.TrimEnd('\r')}");
                    }
                }

                sb.AppendLine($"{indent}</game_entity>");
                return true;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(PrefabInliner), $"Could not inline '{prefabName}': {ex.Message}");
                return false;
            }
        }

        private static string Escape(string value) =>
            value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
