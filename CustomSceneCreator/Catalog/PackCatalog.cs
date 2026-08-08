using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using IOPath = System.IO.Path;

namespace CustomSceneCreator.Catalog {
    /// <summary>
    /// Loads editor-authored placeables from <c>ModuleData/packs/*.xml</c>.
    ///
    /// These are the things the base game has no prefab for: navigation nodes, race gates, typed
    /// spawn points. They are declared in data rather than code so a pack can be added, edited or
    /// shared without a rebuild - and so another mod (Homesteads included) can ship its own markers
    /// by dropping a file in the same folder.
    ///
    /// Each one renders as a stand-in mesh while editing and exports as a named, tagged entity. That
    /// split - visible proxy, real identity - is what lets a marker with no geometry of its own be
    /// something you can actually see and click.
    /// </summary>
    public static class PackCatalog {
        private static List<Placeable>? _placeables;

        public static IReadOnlyList<Placeable> All => _placeables ??= Load();

        /// <summary>Drops the cache so a just-exported prefab appears without leaving the scene.</summary>
        public static void Invalidate() => _placeables = null;

        /// <summary>Category holding whatever is in exports/prefabs.</summary>
        public const string ExportedCategory = "My Prefabs";

        private static List<Placeable> Load() {
            var result = new List<Placeable>();
            string dir = ResolvePacksDir();

            if (dir.Length == 0) {
                TraceLogger.Write(nameof(PackCatalog), "No packs folder found; no editor-authored placeables.");
                return result;
            }

            foreach (string file in System.IO.Directory.GetFiles(dir, "*.xml").OrderBy(f => f)) {
                try {
                    var doc = new XmlDocument();
                    doc.Load(file);

                    XmlNodeList? nodes = doc.SelectNodes("/Placeables/Placeable");
                    if (nodes == null) continue;

                    foreach (XmlNode node in nodes) {
                        if (!(node is XmlElement el)) continue;

                        string id = el.GetAttribute("id");
                        string proxy = el.GetAttribute("proxy");
                        if (id.Length == 0 || proxy.Length == 0) {
                            TraceLogger.Write(nameof(PackCatalog),
                                $"Skipping a placeable in '{IOPath.GetFileName(file)}': id and proxy are both required.");
                            continue;
                        }

                        // A pack is only as good as its proxy: without a real mesh the marker would
                        // be invisible and unclickable, which is the exact problem it exists to fix.
                        if (!GameEntity.PrefabExists(proxy)) {
                            TraceLogger.Write(nameof(PackCatalog),
                                $"Skipping '{id}': proxy prefab '{proxy}' does not exist in this game version.");
                            continue;
                        }

                        string display = el.GetAttribute("display");

                        result.Add(new Placeable {
                            PrefabName = id,
                            DisplayName = display.Length > 0 ? display : Placeable.ToDisplayName(id),
                            Category = Fallback(el.GetAttribute("category"), "Editor Markers"),
                            Module = "CustomSceneCreator",
                            Source = Placeable.SourceEditor,
                            ProxyPrefab = proxy,
                            ExportName = el.GetAttribute("exportName"),
                            ExportTag = el.GetAttribute("exportTag"),
                            IsLogical = true,
                            Meshes = proxy,
                            Tags = el.GetAttribute("exportTag"),
                        });
                    }

                    TraceLogger.Write(nameof(PackCatalog), $"Loaded pack '{IOPath.GetFileName(file)}'.");
                } catch (Exception ex) {
                    TraceLogger.WriteException(nameof(PackCatalog), $"Failed to read pack '{file}'", ex);
                }
            }

            LoadExportedPrefabs(result);

            TraceLogger.Write(nameof(PackCatalog), $"{result.Count} editor-authored placeable(s) loaded.");
            return result;
        }

        /// <summary>
        /// Everything in exports/prefabs, as its own category.
        ///
        /// Read from the folder rather than from a generated pack file: the folder IS the list, so
        /// there is nothing to keep in sync and dropping someone else's exported prefab in works
        /// without editing anything.
        /// </summary>
        private static void LoadExportedPrefabs(List<Placeable> result) {
            try {
                string dir = Editing.ProjectSerializer.PrefabExportsPath;
                if (!System.IO.Directory.Exists(dir)) return;

                foreach (string file in System.IO.Directory.GetFiles(dir, "*.xml").OrderBy(f => f)) {
                    string id = IOPath.GetFileNameWithoutExtension(file);
                    if (id.Length == 0) continue;
                    if (result.Any(p => string.Equals(p.PrefabName, id, StringComparison.OrdinalIgnoreCase))) continue;

                    // The game reads prefab XML only at startup, so something exported a moment ago
                    // is on disk but not yet instantiable. Listed either way, flagged, so the
                    // category is not mysteriously empty right after an export.
                    bool loaded = GameEntity.PrefabExists(id);

                    result.Add(new Placeable {
                        PrefabName = id,
                        DisplayName = Placeable.ToDisplayName(id),
                        Category = ExportedCategory,
                        Module = "CustomSceneCreator",
                        Source = Placeable.SourceEditor,
                        RequiresRestart = !loaded,
                        HasPhysics = true,
                        Meshes = loaded ? id : "",
                    });
                }
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(PackCatalog), "Could not read exported prefabs", ex);
            }
        }

        private static string Fallback(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;

        private static string ResolvePacksDir() {
            foreach (string dir in new[] {
                IOPath.Combine(BasePath.Name, "Modules", "CustomSceneCreator", "ModuleData", "packs"),
                IOPath.Combine(BasePath.Name, "Modules", "CustomSceneCreator", "_Module", "ModuleData", "packs"),
            }) {
                if (System.IO.Directory.Exists(dir)) return dir;
            }
            return "";
        }
    }

    /// <summary>
    /// Everything placeable, from every source. One lookup so callers do not have to know whether
    /// something came from the shipped dump or a pack.
    /// </summary>
    public static class PlaceableRegistry {
        public static IEnumerable<Placeable> All => PackCatalog.All.Concat(AssetCatalog.All);

        public static Placeable? Find(string name) =>
            All.FirstOrDefault(p => string.Equals(p.PrefabName, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Prefab to instantiate for a saved name, resolving pack markers to their proxy.</summary>
        public static string ResolveSpawnPrefab(string name) => Find(name)?.SpawnPrefabName ?? name;
    }
}
