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

    /// <summary>One placed object: what it is, and where it sits.</summary>
    public class PlacedEntity {
        public string PrefabName = "";
        public Vec3 Position;
        /// <summary>Full 3x3 rotation. Kept as a matrix rather than Euler angles because snapped and
        /// tilted objects do not survive a round trip through Euler without drifting.</summary>
        public Mat3 Rotation = Mat3.Identity;

        /// <summary>Stable identity, needed by scripts whose variables reference other entities -
        /// AnimationPoint's PairEntity holds a GUID of its partner. Assigned on placement.</summary>
        public string Id = "";

        /// <summary>Scripts attached to this object. Written out on export.</summary>
        public List<AttachedScript> Scripts = new();

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
