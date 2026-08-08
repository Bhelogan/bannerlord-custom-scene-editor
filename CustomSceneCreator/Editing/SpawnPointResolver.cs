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

        public static Vec3 Resolve(Scene scene, out string how) {
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
            List<GameEntity> entities = new();
            try {
                scene.GetEntities(ref entities);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SpawnPointResolver),
                    $"GetEntities failed ({ex.GetType().Name}: {ex.Message}).");
            }

            if (entities.Count > 0) {
                GameEntity? spawnish = entities.FirstOrDefault(e => LooksLikeSpawnPoint(e));
                if (spawnish != null) {
                    Vec3 candidate = SnapAndValidate(scene, spawnish.GlobalPosition);
                    if (candidate.IsValid) {
                        how = $"spawn-like entity '{spawnish.Name}'";
                        return candidate;
                    }
                }

                // 3. Median of all entity positions. Median rather than mean or bounding-box centre
                //    specifically because horizon/skybox entities are extreme outliers, and a median
                //    ignores them while an average or an extent does not.
                Vec3 median = MedianPosition(entities);
                Vec3 fromMedian = SearchOutward(scene, median);
                if (fromMedian.IsValid) {
                    how = "median entity position";
                    return fromMedian;
                }
            }

            // 4. Nothing worked. Return the scene origin and let the caller log loudly.
            how = "FALLBACK origin (no navmesh position found)";
            return Vec3.Zero;
        }

        private static bool LooksLikeSpawnPoint(GameEntity entity) {
            string name = entity.Name ?? "";
            if (name.Length == 0) return false;
            return name.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0
                || name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase);
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
                float terrainHeight = scene.GetTerrainHeight(flat, true);

                // Prefer the entity's own height when it is plausibly just above the ground (spawn
                // markers usually sit a metre up); otherwise trust the terrain.
                float z = (candidate.z > terrainHeight - 0.5f && candidate.z < terrainHeight + 3f)
                    ? terrainHeight
                    : terrainHeight;

                Vec3 result = new Vec3(flat.x, flat.y, z);
                return IsOnNavMesh(scene, result) ? result : Vec3.Invalid;
            } catch {
                return Vec3.Invalid;
            }
        }

        private static bool IsOnNavMesh(Scene scene, Vec3 position) {
            try {
                PathFaceRecord record = PathFaceRecord.NullFaceRecord;
                scene.GetNavMeshFaceIndex(ref record, position.AsVec2, false, false, true);
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
            const int maxRings = 24;   // 24 * 8m = ~192m in each direction

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
