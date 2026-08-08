using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TaleWorlds.Engine;
using TaleWorlds.Library;
// TaleWorlds.Engine also defines a Path type (spline paths), which collides with System.IO.Path.
using IOPath = System.IO.Path;

namespace CustomSceneCreator.Catalog {
    /// <summary>
    /// Loads the generated prefab dump into <see cref="Placeable"/>s.
    ///
    /// The dump is version-stamped and produced by <c>tools/build_asset_dump.ps1</c> from the local
    /// install. Every entry is still checked with <c>GameEntity.PrefabExists</c> at load: that check
    /// is what makes a stale dump degrade into fewer placeables rather than into a crash when the
    /// game updates and a prefab disappears.
    /// </summary>
    public static class AssetCatalog {
        private static List<Placeable>? _placeables;

        public static IReadOnlyList<Placeable> All => _placeables ??= Load();

        public static IEnumerable<string> Categories =>
            All.Select(p => p.Category).Distinct().OrderBy(c => c);

        public static IEnumerable<Placeable> InCategory(string category) =>
            All.Where(p => p.Category == category);

        public static Placeable? Find(string prefabName) =>
            All.FirstOrDefault(p => string.Equals(p.PrefabName, prefabName, StringComparison.OrdinalIgnoreCase));

        // Column indices in the dump. Kept as named constants because the layout is shared with the
        // external bake scripts and a silent off-by-one here would be hard to spot.
        private const int ColName = 0;
        private const int ColModule = 1;
        private const int ColCategory = 4;
        private const int ColHasPhysics = 5;
        private const int ColMeshes = 11;
        private const int MinColumns = 12;

        private static List<Placeable> Load() {
            var result = new List<Placeable>();
            string path = ResolveDumpPath();

            if (path.Length == 0) {
                TraceLogger.Write(nameof(AssetCatalog),
                    "No asset dump found in ModuleData. Nothing will be placeable. " +
                    "Run tools/build_asset_dump.ps1 and redeploy.");
                return result;
            }

            int rows = 0, missing = 0, duplicates = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try {
                using var reader = new StreamReader(path, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null) {
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("AssetName", StringComparison.OrdinalIgnoreCase)) continue;

                    string[] parts = line.Split('|');
                    if (parts.Length < MinColumns) continue;
                    rows++;

                    string prefabName = parts[ColName].Trim();
                    if (prefabName.Length == 0) continue;
                    if (!seen.Add(prefabName)) { duplicates++; continue; }

                    // The dump is a snapshot; the running game is the authority.
                    if (!GameEntity.PrefabExists(prefabName)) { missing++; continue; }

                    string meshes = parts[ColMeshes].Trim();

                    result.Add(new Placeable {
                        PrefabName = prefabName,
                        DisplayName = Placeable.ToDisplayName(prefabName),
                        Module = parts[ColModule].Trim(),
                        Category = MapCategory(parts[ColCategory].Trim(), prefabName, meshes.Length == 0),
                        HasPhysics = parts[ColHasPhysics].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase),
                        IsLogical = meshes.Length == 0,
                        Source = Placeable.SourceBaseGame,
                    });
                }
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(AssetCatalog), $"Failed reading '{path}'", ex);
            }

            TraceLogger.Write(nameof(AssetCatalog),
                $"Loaded {result.Count} placeables from '{IOPath.GetFileName(path)}' " +
                $"({rows} rows, {missing} no longer in the game, {duplicates} duplicate names, " +
                $"{result.Count(p => p.IsLogical)} logical/marker).");

            return result;
        }

        /// <summary>
        /// Re-cuts the dump's file-derived category into something a scene author looks for.
        ///
        /// The dump infers category from the prefab FILE, which groups by how TaleWorlds organised
        /// their source, not by what a thing is. Prefab names are the better signal for the handful
        /// of cases that matter most - markers especially, since those are scattered across files.
        /// </summary>
        private static string MapCategory(string dumpCategory, string prefabName, bool isLogical) {
            string name = prefabName.ToLowerInvariant();

            if (isLogical || name.StartsWith("sp_") || name.Contains("spawn") || name.StartsWith("editor_")) {
                return "Markers & Logic";
            }

            switch (dumpCategory) {
                case "architecture": return "Buildings";
                case "vegetation":   return "Vegetation";
                case "terrain":      return "Terrain & Rocks";
                case "siege":        return "Siege";
                case "naval":        return "Naval";
                case "furniture":    return "Furniture";
                case "lighting":     return "Lighting";
                case "banner":       return "Banners";
                case "marker":       return "Markers & Logic";
                case "animal":       return "Animals";
                case "prop":         return "Props & Clutter";
                default:             return "Misc";
            }
        }

        private static string ResolveDumpPath() {
            foreach (string dir in new[] {
                IOPath.Combine(BasePath.Name, "Modules", "CustomSceneCreator", "ModuleData"),
                IOPath.Combine(BasePath.Name, "Modules", "CustomSceneCreator", "_Module", "ModuleData"),
            }) {
                if (!System.IO.Directory.Exists(dir)) continue;
                // Glob rather than a hardcoded version, so a regenerated dump for a newer game build
                // is picked up without a code change.
                string[] matches = System.IO.Directory.GetFiles(dir, "bannerlord_assets_v*.txt");
                if (matches.Length > 0) {
                    Array.Sort(matches, StringComparer.OrdinalIgnoreCase);
                    return matches[matches.Length - 1];
                }
            }
            return "";
        }
    }
}
