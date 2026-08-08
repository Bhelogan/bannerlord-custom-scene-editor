using System;

namespace CustomSceneCreator.Catalog {
    /// <summary>
    /// One thing the editor can place.
    ///
    /// Deliberately free of the homestead model's economy fields - no build points, no item costs,
    /// no tier. A scene editor places things because you want them there, not because you can afford
    /// them, and carrying those fields forward would drag the gating logic with them.
    /// </summary>
    public class Placeable {
        /// <summary>Prefab name passed to <c>GameEntity.Instantiate</c>. The identity of a placeable.</summary>
        public string PrefabName = "";

        public string DisplayName = "";
        public string Category = "";
        public string Module = "";

        /// <summary>Where this came from: shipped asset, editor-authored marker, user pack. Drives
        /// the browser's grouping and lets a pack be shown separately from base-game content.</summary>
        public string Source = SourceBaseGame;

        /// <summary>
        /// True when the prefab has no visible geometry - spawn points, patrol points, animation
        /// points and other logic nodes. They are not junk to filter out: they are most of the point
        /// of a scene editor. They just need a stand-in mesh to be visible while editing.
        /// </summary>
        public bool IsLogical;

        public bool HasPhysics;

        // Descriptive detail, straight from the dump. Shown in the picker's info pane: for a scene
        // author, "what meshes does this have and does it collide" is the useful question, where the
        // homestead builder showed economy stats.
        public string Meshes = "";
        public string PhysicsShapes = "";
        public string Scripts = "";
        public string Tags = "";
        public string ChildNames = "";
        public string Mobility = "";

        /// <summary>Prefabs carrying scripts are not measured in the picker - instantiating one runs
        /// its script, and some of them assume a mission type we are not in.</summary>
        public bool HasScripts => Scripts.Length > 0;

        // -- editor-authored placeables (packs) --------------------------------------------------

        /// <summary>
        /// Prefab actually instantiated. For editor-authored markers this is a stand-in mesh
        /// (editor_cube, editor_cylinder) so the thing is visible while building; the real identity
        /// stays <see cref="PrefabName"/>, which is what gets saved and exported.
        /// </summary>
        public string ProxyPrefab = "";

        /// <summary>Entity name pattern written on export, e.g. <c>sp_enemy_{index}</c>. Empty for
        /// base-game prefabs, which export under their own name.</summary>
        public string ExportName = "";

        /// <summary>Tag written on export, so code can find these with FindEntitiesWithTag.</summary>
        public string ExportTag = "";

        /// <summary>
        /// True for something exported this session: the XML is on disk, but the game only reads
        /// prefab files at startup, so it cannot be instantiated until a restart. Listed anyway -
        /// an empty category right after exporting would read as the export having failed.
        /// </summary>
        public bool RequiresRestart;

        /// <summary>What to hand GameEntity.Instantiate.</summary>
        public string SpawnPrefabName => ProxyPrefab.Length > 0 ? ProxyPrefab : PrefabName;

        public const string SourceBaseGame = "Base Game";
        public const string SourceEditor = "Scene Editor";

        public override string ToString() => $"{DisplayName} ({PrefabName})";

        /// <summary>Turns snake_case prefab names into something readable in a list.</summary>
        public static string ToDisplayName(string prefabName) {
            if (string.IsNullOrEmpty(prefabName)) return "";
            string[] words = prefabName.Split('_');
            for (int i = 0; i < words.Length; i++) {
                if (words[i].Length == 0) continue;
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1).ToLowerInvariant();
            }
            return string.Join(" ", words);
        }
    }
}
