using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Finds somewhere in an arbitrary scene that the player can actually stand.
    ///
    /// This has to work on all 611 shipped scenes without per-scene configuration, so it cannot
    /// assume any particular tag or naming convention exists.  The one thing every walkable scene
    /// does have is a navmesh, and that is what the result is validated against: a position that is
    /// not on a navmesh face is a position where the player will hang in the air and be unable to
    /// move, which is exactly the failure this class was written to fix.
    ///
    /// The naive approach - centre of <c>Scene.GetBoundingBox</c> - does not work.  The bounding box
    /// includes horizon and skybox geometry, so on <c>mp_skirmish_spawn_test</c> it returns
    /// (-57.7, -1054.4) while the playable area is around (500, 500).
    /// </summary>
    internal static class SpawnPointResolver {
        /// <summary>Tags worth trying first, most specific first. Drawn from what shipped scenes
        /// actually use rather than from what would be tidy.</summary>
        private static readonly string[] PreferredTags = {
            "sp_player", "spawnpoint_player", "sp_player_1",
            "spawnpoint", "spawn_zone", "starting",
            "attacker", "defender",
            "sp_common", "sp_arena",
        };

        /// <summary>
        /// Cached entity list. A castle scene holds thousands of entities and enumerating plus
        /// ranking them is far too expensive to redo on a retry, let alone every frame.
        /// </summary>
        private static List<GameEntity>? _entityCache;
        private static Scene? _entityCacheScene;

        public static void ResetCache() {
            _entityCache = null;
            _entityCacheScene = null;
        }

        /// <param name="allowExpensiveSearch">
        /// Enables the outward ring search. That costs ~9,600 terrain and navmesh queries, which is
        /// fine once but ruinous per frame - running it every tick is what stalled the retry loop
        /// badly enough that it never reached its own deadline. Callers should pass false while
        /// retrying and true only on the final attempt.
        /// </param>
        public static Vec3 Resolve(Scene scene, bool allowExpensiveSearch, out string how) {
            // 1. An authored spawn tag, if the scene has one we recognise.
            foreach (string tag in PreferredTags) {
                Vec3? tagged = TryTag(scene, tag);
                if (tagged.HasValue) {
                    how = $"tag '{tag}'";
                    return tagged.Value;
                }
            }

            // 2. Any entity that looks like a spawn point. Broader than the tag list and catches
            //    scene-specific names such as sergeant_attack_spawn or skirmish_respawn.
            List<GameEntity> entities = GetEntitiesCached(scene);

            if (entities.Count > 0) {
                // Ranked, not first-match. Villages carry dozens of sp_* entities and the first one
                // encountered is as likely to be sp_notable_lookout_point - which sits on a roof -
                // as it is to be somewhere sensible.
                foreach (GameEntity candidateEntity in entities
                             .Select(e => new { Entity = e, Score = ScoreSpawnPoint(e) })
                             .Where(x => x.Score > 0)
                             .OrderByDescending(x => x.Score)
                             .Select(x => x.Entity)
                             .Take(40)) {
                    Vec3 candidate = SnapAndValidate(scene, candidateEntity.GlobalPosition);
                    if (candidate.IsValid) {
                        how = $"spawn-like entity '{candidateEntity.Name}'";
                        return candidate;
                    }
                }

                // 3. Median of all entity positions. Median rather than mean or bounding-box centre
                //    specifically because horizon/skybox entities are extreme outliers, and a median
                //    ignores them while an average or an extent does not.
                Vec3 median = MedianPosition(entities);
                Vec3 direct = SnapAndValidate(scene, median);
                if (direct.IsValid) {
                    how = "median entity position";
                    return direct;
                }

                // 4. Sweep outward from the median. Expensive; final attempt only.
                if (allowExpensiveSearch) {
                    Vec3 swept = SearchOutward(scene, median);
                    if (swept.IsValid) {
                        how = "outward search from median";
                        return swept;
                    }
                }
            }

            how = allowExpensiveSearch
                ? "FALLBACK origin (no navmesh position found)"
                : "not found yet (cheap pass)";
            return Vec3.Zero;
        }

        private static List<GameEntity> GetEntitiesCached(Scene scene) {
            if (_entityCache != null && ReferenceEquals(_entityCacheScene, scene)) {
                return _entityCache;
            }
            var entities = new List<GameEntity>();
            try {
                scene.GetEntities(ref entities);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SpawnPointResolver),
                    $"GetEntities failed ({ex.GetType().Name}: {ex.Message}).");
            }
            _entityCache = entities;
            _entityCacheScene = scene;
            return entities;
        }

        /// <summary>
        /// How good a stand-in for "where a player should appear" an entity's name is. 0 means not a
        /// spawn point at all.
        ///
        /// The ordering is drawn from what shipped scenes actually contain. Settlement scenes have no
        /// &lt;tag&gt; elements at all - aserai_village_b has zero - so entity names are the only
        /// signal available, and they vary in how safe they are: sp_notable_lookout_point is a real
        /// spawn point on a rooftop, while sp_common_* is ground-level walkable space.
        /// </summary>
        private static int ScoreSpawnPoint(GameEntity entity) {
            string name = entity.Name ?? "";
            if (name.Length == 0) return 0;

            bool Has(string s) => name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;

            if (Has("sp_player")) return 100;
            if (Has("spawnpoint") || Has("respawn")) return 80;
            if (Has("sp_common")) return 70;
            if (Has("sp_battle") || Has("sp_arena")) return 60;
            if (Has("spawn")) return 50;
            if (Has("sp_npc")) return 30;
            // Lookouts, hangouts and guard posts are frequently elevated or enclosed.
            if (Has("sp_notable") || Has("sp_guard") || Has("lookout")) return 15;
            if (name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase)) return 10;
            return 0;
        }

        private static Vec3? TryTag(Scene scene, string tag) {
            try {
                GameEntity? entity = scene.FindEntitiesWithTag(tag).FirstOrDefault();
                if (entity == null) return null;
                Vec3 snapped = SnapAndValidate(scene, entity.GlobalPosition);
                return snapped.IsValid ? snapped : (Vec3?)null;
            } catch {
                // FindEntitiesWithTag throws rather than returning empty on some scenes.
                return null;
            }
        }

        /// <summary>
        /// Drops a candidate onto the terrain and confirms it sits on a navmesh face. Returns an
        /// invalid Vec3 if it does not.
        /// </summary>
        private static Vec3 SnapAndValidate(Scene scene, Vec3 candidate) {
            try {
                Vec2 flat = candidate.AsVec2;

                // The Vec3 navmesh lookup is height-sensitive, so try more than one plausible Z.
                // The candidate's own height matters for interiors, arena stands and upper floors,
                // where the walkable surface is nowhere near the terrain; terrain height matters
                // outdoors, where a marker may float a metre or two above the ground.
                float terrainHeight;
                try {
                    terrainHeight = scene.GetTerrainHeight(flat, true);
                } catch {
                    terrainHeight = candidate.z;
                }

                foreach (float z in new[] { candidate.z, terrainHeight, terrainHeight + 0.5f }) {
                    Vec3 probe = new Vec3(flat.x, flat.y, z);
                    if (IsOnNavMesh(scene, probe)) return probe;
                }
                return Vec3.Invalid;
            } catch {
                return Vec3.Invalid;
            }
        }

        /// <summary>
        /// Scene.GetNavMeshFaceIndex has two overloads and only one of them is for mission scenes.
        ///
        /// The Vec2 overload takes an "isRegion1" flag and is the CAMPAIGN MAP variant - the world
        /// map passes vec2.IsOnLand for that argument, i.e. it selects the land or sea region. Asking
        /// a mission scene for a region-1 face is meaningless, so it never returns a valid face. That
        /// is why every candidate was rejected on scenes that plainly had a navmesh: arena_battania_a
        /// reported 274 faces, aserai_village_c reported 9,449, and not one position validated.
        ///
        /// Every in-mission call in the game itself uses the Vec3 overload. So does this now.
        /// </summary>
        private static bool IsOnNavMesh(Scene scene, Vec3 position) {
            try {
                PathFaceRecord record = PathFaceRecord.NullFaceRecord;
                scene.GetNavMeshFaceIndex(ref record, position, checkIfDisabled: false);
                return record.IsValid();
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Expanding square-ring search around a starting point for the nearest navmesh position.
        /// Cheap, deterministic, and bounded - it gives up rather than scanning a whole map.
        /// </summary>
        private static Vec3 SearchOutward(Scene scene, Vec3 start) {
            Vec3 direct = SnapAndValidate(scene, start);
            if (direct.IsValid) return direct;

            const float step = 8f;
            // Castle and town scenes run to several hundred metres across, and the median entity
            // position can easily start outside the walls, so the search has to reach further than
            // a battle map would need.
            const int maxRings = 48;   // 48 * 8m = ~384m in each direction

            for (int ring = 1; ring <= maxRings; ring++) {
                float offset = ring * step;
                for (int i = -ring; i <= ring; i++) {
                    float slide = i * step;
                    foreach (Vec3 probe in new[] {
                        new Vec3(start.x + slide,  start.y + offset, start.z),
                        new Vec3(start.x + slide,  start.y - offset, start.z),
                        new Vec3(start.x + offset, start.y + slide,  start.z),
                        new Vec3(start.x - offset, start.y + slide,  start.z),
                    }) {
                        Vec3 candidate = SnapAndValidate(scene, probe);
                        if (candidate.IsValid) return candidate;
                    }
                }
            }
            return Vec3.Invalid;
        }

        private static Vec3 MedianPosition(List<GameEntity> entities) {
            List<float> xs = new(entities.Count);
            List<float> ys = new(entities.Count);
            List<float> zs = new(entities.Count);
            foreach (GameEntity e in entities) {
                Vec3 p = e.GlobalPosition;
                if (!p.IsValid) continue;
                xs.Add(p.x); ys.Add(p.y); zs.Add(p.z);
            }
            if (xs.Count == 0) return Vec3.Zero;
            xs.Sort(); ys.Sort(); zs.Sort();
            int mid = xs.Count / 2;
            return new Vec3(xs[mid], ys[mid], zs[mid]);
        }
    }
}
