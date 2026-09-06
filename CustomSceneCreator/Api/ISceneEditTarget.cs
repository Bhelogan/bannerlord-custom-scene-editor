using System.Collections.Generic;
using CustomSceneCreator.Catalog;
using TaleWorlds.Library;

namespace CustomSceneCreator.Api {
    /// <summary>
    /// A scene script attached to a placed object - a fire on a brazier, a turn on a windmill.
    ///
    /// Variables are kept as strings keyed by name rather than typed fields: the catalog is mined
    /// from shipped scenes, so the set of variables a script takes is data, not something that can be
    /// known at compile time. The catalog says what type each one is meant to be; this just carries
    /// whatever was entered.
    /// </summary>
    public class AttachedScript {
        public string Name = "";
        public Dictionary<string, string> Variables = new();

        public AttachedScript Clone() => new AttachedScript {
            Name = Name,
            Variables = new Dictionary<string, string>(Variables),
        };
    }

    /// <summary>A runtime PNG applied to one mesh surface on a placed object.</summary>
    public class TextureOverride {
        public string Image = "";
        public string Material = "";
        /// <summary>
        /// Stable traversal index of the target mesh. -1 is the legacy material-wide form used by
        /// projects made before surface selection existed; it intentionally still affects every
        /// mesh which names <see cref="Material"/>.
        /// </summary>
        public int MeshIndex = -1;
        /// <summary>Value forced into every alpha byte of the image, 0-255, or -1 to keep the
        /// PNG's own alpha. What that channel does depends on the target material - see
        /// TextureOverrideApplicator.DescribeMaterial.</summary>
        public int Alpha = -1;

        /// <summary>Alpha-test cutoff 0-100, or -1 to leave the material's own value. Pixels below
        /// the cutoff are discarded, which is how cut-out foliage and banners are drawn.</summary>
        public int AlphaCutoff = -1;

        /// <summary>MBAlphaBlendMode as an int, or -1 to leave the material's own mode.</summary>
        public int BlendMode = -1;
        public TextureOverride Clone() => new TextureOverride { Image = Image, Material = Material, MeshIndex = MeshIndex, Alpha = Alpha, AlphaCutoff = AlphaCutoff, BlendMode = BlendMode };
    }

    /// <summary>One placed object: what it is, and where it sits.</summary>
    public class PlacedEntity {
        public string PrefabName = "";
        public Vec3 Position;
        /// <summary>Full 3x3 rotation. Kept as a matrix rather than Euler angles because snapped and
        /// tilted objects do not survive a round trip through Euler without drifting.</summary>
        public Mat3 Rotation = Mat3.Identity;

        /// <summary>
        /// Local scale applied to the placed prefab root. Kept separate from Rotation so reopening
        /// the list and editing an angle cannot compound scale into the rotation basis.
        /// </summary>
        public Vec3 Scale = new Vec3(1f, 1f, 1f);

        /// <summary>Stable identity, needed by scripts whose variables reference other entities -
        /// AnimationPoint's PairEntity holds a GUID of its partner. Assigned on placement.</summary>
        public string Id = "";

        /// <summary>
        /// Which numbered marker this is - the 3 in <c>sp_enemy_3</c>. Zero for anything that is not
        /// a numbered marker.
        ///
        /// Held on the object rather than counted at export time, because the number is part of what
        /// the marker IS. Race gates have to be passed in order, and an ambush that spawns wave 2
        /// behind you needs to know which spawns are wave 2. Numbering at export instead would mean
        /// deleting gate 3 and placing a replacement silently made it the last gate.
        /// </summary>
        public int MarkerIndex;

        /// <summary>Scripts attached to this object. Written out on export.</summary>
        public List<AttachedScript> Scripts = new();

        /// <summary>Per-surface runtime PNG replacements.</summary>
        public List<TextureOverride> TextureOverrides = new();

        /// <summary>The live entity in the scene, when there is one. Not persisted.</summary>
        public TaleWorlds.Engine.GameEntity? SceneEntity;
    }

    /// <summary>
    /// Where the editor reads and writes placed objects.
    ///
    /// This is the seam that makes the editor reusable. The standalone app implements it over a JSON
    /// project file; Homesteads would implement it over a HomesteadScene, mapping OnEntityAdded to
    /// AddPlaceableEntityToCurrentScene and so on. The editor itself never learns what it is
    /// editing - which is the whole point, since the version this was forked from could only ever
    /// edit a homestead.
    /// </summary>
    public interface ISceneEditTarget {
        /// <summary>Shown in the editor HUD.</summary>
        string DisplayName { get; }

        /// <summary>Objects already placed, restored when the scene opens.</summary>
        IEnumerable<PlacedEntity> LoadEntities();

        void OnEntityAdded(PlacedEntity entity);
        void OnEntityRemoved(PlacedEntity entity);

        /// <summary>Persist. Called on explicit save and when leaving the editor.</summary>
        void Commit();
    }

    /// <summary>Supplies the palette. Implement to filter the base-game catalog or add your own.</summary>
    public interface IPlaceableProvider {
        IEnumerable<Placeable> GetPlaceables();
    }

    /// <summary>Everything placeable: editor-authored packs first, then the base-game catalog.</summary>
    public class CatalogPlaceableProvider : IPlaceableProvider {
        public IEnumerable<Placeable> GetPlaceables() => PlaceableRegistry.All;
    }
}
