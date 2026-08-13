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
        private static readonly List<string> _loadErrors = new();

        public static IReadOnlyList<Placeable> All => _placeables ??= Load();

        /// <summary>Packs that failed to parse, for reporting where someone will actually see it.</summary>
        public static IReadOnlyList<string> LoadErrors {
            get { _ = All; return _loadErrors; }
        }

        /// <summary>Drops the cache so a just-exported prefab appears without leaving the scene.</summary>
        public static void Invalidate() { _placeables = null; _loadErrors.Clear(); }

        /// <summary>Category holding whatever is in exports/prefabs.</summary>
        public const string ExportedCategory = "My Prefabs";

        /// <summary>Category holding saved projects, placed as loose pieces.</summary>
        public const string TemplateCategory = "My Templates";

        /// <summary>Prefix marking a palette entry as a template rather than a real prefab.</summary>
        public const string TemplatePrefix = "csctemplate:";

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
                    // A malformed pack loses every marker in it. That failed silently once - one
                    // illegal comment in csc_core.xml threw out all ten shipped markers, and the
                    // only symptom was a category that was never there to miss.
                    _loadErrors.Add($"{IOPath.GetFileName(file)}: {ex.Message}");
                    TraceLogger.WriteException(nameof(PackCatalog), $"Failed to read pack '{file}'", ex);
                }
            }

            LoadExportedPrefabs(result);
            LoadTemplates(result);

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

                string moduleDir = ModulePrefabsPath();

                foreach (string file in System.IO.Directory.GetFiles(dir, "*.xml").OrderBy(f => f)) {
                    // The prefab is known to the game by the NAME INSIDE the file, not by the file
                    // name. They usually match, but a renamed file would otherwise be listed under an
                    // id that does not exist and could never be placed.
                    string id = ReadRootName(file) ?? IOPath.GetFileNameWithoutExtension(file);
                    if (id.Length == 0) continue;
                    if (result.Any(p => string.Equals(p.PrefabName, id, StringComparison.OrdinalIgnoreCase))) continue;

                    // Copy it where the game will actually read it.
                    //
                    // The exports folder under Documents is an ARCHIVE - the game never looks there.
                    // A prefab only becomes instantiable from a module's Prefabs folder, so a file
                    // dropped into exports (someone else's prefab, or one brought back from another
                    // machine) was listed in the picker and then had no ghost and would not place.
                    // Mirroring it here is what makes "drop it in and restart" work.
                    if (!Mirror(file, moduleDir)) continue;

                    // The game reads prefab XML only at startup, so something copied a moment ago is
                    // on disk but not yet instantiable. Listed either way, flagged, so the category
                    // is not mysteriously empty right after an export.
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

        /// <summary>
        /// Everything in exports/templates.
        ///
        /// Read straight from the folder, like the exported prefabs: the folder IS the list, so a
        /// template someone sends you works by being dropped in. And because this mod reads the file
        /// itself rather than registering it with the engine, it is placeable immediately - no
        /// restart, unlike a prefab.
        /// </summary>
        private static void LoadTemplates(List<Placeable> result) {
            try {
                string dir = Editing.ProjectSerializer.TemplateExportsPath;
                if (!System.IO.Directory.Exists(dir)) return;

                foreach (string file in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(f => f)) {
                    Editing.SceneProject? project = Editing.ProjectSerializer.LoadFile(file);
                    if (project == null || project.Entities.Count == 0) continue;

                    string id = IOPath.GetFileNameWithoutExtension(file);

                    result.Add(new Placeable {
                        PrefabName = TemplatePrefix + id,
                        DisplayName = $"{id}  ({project.Entities.Count} pieces)",
                        Category = TemplateCategory,
                        Module = "CustomSceneCreator",
                        Source = Placeable.SourceEditor,
                        IsTemplate = true,
                        TemplateProject = file,
                        // Shown as a marker while carried: the pieces only exist once it is placed.
                        ProxyPrefab = "editor_marker",
                        IsLogical = true,
                        Meshes = "editor_marker",
                    });
                }
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(PackCatalog), "Could not list templates", ex);
            }
        }

        /// <summary>The name on the first game_entity - what the engine will know the prefab by.</summary>
        private static string? ReadRootName(string file) {
            try {
                var document = new XmlDocument();
                document.Load(file);
                XmlNodeList? nodes = document.GetElementsByTagName("game_entity");
                if (nodes == null || nodes.Count == 0) return null;
                string name = (nodes[0] as XmlElement)?.GetAttribute("name") ?? "";
                return name.Length > 0 ? name : null;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(PackCatalog),
                    $"'{IOPath.GetFileName(file)}' is not readable prefab XML: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Copies an exported prefab into the module, when it is missing or out of date.
        ///
        /// Refuses anything with a reference the game cannot resolve. Every prefab in a module is
        /// loaded at STARTUP, and one unresolvable name takes the whole game down before the main
        /// menu with nothing to say which file was at fault - so a file that would do that must
        /// never be copied somewhere the engine will read it.
        /// </summary>
        private static bool Mirror(string file, string moduleDir) {
            if (moduleDir.Length == 0) return true;

            string? danger = UnresolvableReference(file);
            if (danger != null) {
                TraceLogger.Write(nameof(PackCatalog),
                    $"NOT loading '{IOPath.GetFileName(file)}': it references '{danger}', which does " +
                    "not exist in this game. A world prefab with an unresolvable reference crashes " +
                    "the game while it loads, so this file is being left alone. Re-export it, or " +
                    "install the pack that defines the missing name.");
                return false;
            }

            try {
                System.IO.Directory.CreateDirectory(moduleDir);
                string target = IOPath.Combine(moduleDir, IOPath.GetFileName(file));

                if (System.IO.File.Exists(target) &&
                    System.IO.File.GetLastWriteTimeUtc(target) >= System.IO.File.GetLastWriteTimeUtc(file)) {
                    return true;              // already there and current
                }

                System.IO.File.Copy(file, target, overwrite: true);
                TraceLogger.Write(nameof(PackCatalog),
                    $"Copied '{IOPath.GetFileName(file)}' into the module so the game can load it. " +
                    "Restart to place it.");
            } catch (Exception ex) {
                TraceLogger.Write(nameof(PackCatalog),
                    $"Could not copy '{IOPath.GetFileName(file)}' into the module: {ex.Message}");
            }
            return true;
        }

        /// <summary>The first child prefab reference that does not resolve, or null when all do.</summary>
        private static string? UnresolvableReference(string file) {
            try {
                var document = new XmlDocument();
                document.Load(file);

                XmlNodeList? nodes = document.GetElementsByTagName("game_entity");
                if (nodes == null) return null;

                foreach (XmlNode node in nodes) {
                    string reference = (node as XmlElement)?.GetAttribute("prefab") ?? "";
                    if (reference.Length == 0) continue;
                    if (!GameEntity.PrefabExists(reference)) return reference;
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(PackCatalog),
                    $"Could not check '{IOPath.GetFileName(file)}': {ex.Message}");
                return "unreadable";
            }
            return null;
        }

        private static string ModulePrefabsPath() {
            try {
                string root = IOPath.Combine(BasePath.Name, "Modules", "CustomSceneCreator");
                if (System.IO.Directory.Exists(root)) return IOPath.Combine(root, "Prefabs");
            } catch { }
            return "";
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

        /// <summary>
        /// What to call something on screen. Prefers the catalog's own name, so a pack marker reads
        /// as "Enemy Spawn" rather than the "Csc Enemy Spawn" its id would spell out.
        /// </summary>
        public static string DisplayNameFor(string prefabName) {
            string display = Find(prefabName)?.DisplayName ?? "";
            return display.Length > 0 ? display : Placeable.ToDisplayName(prefabName);
        }

        /// <summary>True for markers exported with a number in their name - see PlacedEntity.MarkerIndex.</summary>
        public static bool IsNumberedMarker(string prefabName) {
            Placeable? placeable = Find(prefabName);
            return placeable != null &&
                   placeable.ExportName.IndexOf("{index}", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
