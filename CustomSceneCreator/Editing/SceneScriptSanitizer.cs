using System;
using System.Collections.Generic;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Objects.Siege;
using TaleWorlds.MountAndBlade.View.Scripts;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Removes scene entities whose scripts require a mission type we are not running.
    ///
    /// Shipped settlement scenes are authored for specific missions, and some of their scripts
    /// assume that mission's logic has already populated the world. Outside that context they do not
    /// degrade - they throw during scene start, before any of our code can intervene.
    ///
    /// The confirmed case is <see cref="DeploymentPoint"/> on castle and town scenes:
    ///
    ///     AfterMissionStart()
    ///       if (DeployableWeapons.IsEmpty())        // always true with no siege logic running
    ///         SetBreachSideDeploymentPoint()
    ///           _weapons.FirstOrDefault(w => w is SiegeTower) as IPrimarySiegeWeapon  // null
    ///             .WeaponSide                                                          // NRE
    ///
    /// A real siege mission spawns the siege weapons first, so the cast always succeeds there.  In a
    /// bare editing mission it never can, which means every DeploymentPoint in the scene is a
    /// guaranteed crash - and there are over 1,300 of them across the shipped scenes.
    ///
    /// This is the runtime half of the scene-derivation idea in the plan (section 8): same goal of
    /// separating a scene's geometry from its mission logic, but applied in memory, so it works on
    /// any scene without copying multi-megabyte terrain files first.
    ///
    /// Timing is the whole trick. <c>AfterMissionStart</c> runs on mission objects during
    /// <c>Mission.AfterStart()</c>, so the removal has to happen in <c>EarlyStart</c> - by
    /// <c>AfterStart</c> it is already too late.
    /// </summary>
    public class SceneScriptSanitizer : MissionLogic {
        /// <summary>
        /// Removers for scripts known to require mission-type-specific logic. Kept as explicit
        /// generic calls because the engine's entity query is generic over the managed script type,
        /// and there is no public string-keyed equivalent on Scene.
        ///
        /// Siege equipment being stripped from a castle is not a loss for scene editing: an editor
        /// wants the castle, not the battering ram that a siege would have spawned.
        /// </summary>
        private static readonly (string Name, Func<Scene, List<GameEntity>> Query)[] Targets = {
            ("DeploymentPoint",     s => Collect<DeploymentPoint>(s)),
            ("SiegeTowerSpawner",   s => Collect<SiegeTowerSpawner>(s)),
            ("SiegeLadderSpawner",  s => Collect<SiegeLadderSpawner>(s)),
            ("BatteringRamSpawner", s => Collect<BatteringRamSpawner>(s)),
            ("MangonelSpawner",     s => Collect<MangonelSpawner>(s)),
            ("BallistaSpawner",     s => Collect<BallistaSpawner>(s)),

            // Campaign-map scripts. These belong to the world map, not to a mission, and they read
            // map state that does not exist here. Main_map crashed with an access violation inside
            // MapColorGradeManager.ApplyAtmosphere - not a catchable managed exception, so guarding
            // our own code could never have helped. The browser no longer offers such scenes, but a
            // stray copy of these scripts in an ordinary scene would be just as fatal.
            ("MapColorGradeManager", s => Collect<MapColorGradeManager>(s)),
            ("MapAtmosphereProbe",   s => Collect<MapAtmosphereProbe>(s)),
        };

        public override void EarlyStart() {
            base.EarlyStart();
            try {
                Sanitize(Mission.Scene);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneScriptSanitizer), "Sanitize threw", ex);
            }
        }

        private static void Sanitize(Scene scene) {
            if (scene == null) return;

            int totalRemoved = 0;
            foreach ((string name, Func<Scene, List<GameEntity>> query) in Targets) {
                List<GameEntity> entities;
                try {
                    entities = query(scene);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(SceneScriptSanitizer),
                        $"Query for '{name}' failed ({ex.GetType().Name}: {ex.Message}); skipping.");
                    continue;
                }

                int removed = 0;
                foreach (GameEntity entity in entities) {
                    try {
                        if (entity == null) continue;
                        // removeReason 0 is the plain "removed from scene" reason the engine uses
                        // for ordinary entity teardown.
                        scene.RemoveEntity(entity, 0);
                        removed++;
                    } catch (Exception ex) {
                        TraceLogger.Write(nameof(SceneScriptSanitizer),
                            $"Could not remove a '{name}' entity ({ex.GetType().Name}: {ex.Message}).");
                    }
                }

                if (removed > 0) {
                    TraceLogger.Write(nameof(SceneScriptSanitizer), $"Removed {removed} x {name}.");
                    totalRemoved += removed;
                }
            }

            TraceLogger.Write(nameof(SceneScriptSanitizer),
                totalRemoved > 0
                    ? $"Sanitized scene: {totalRemoved} mission-type-specific entities removed."
                    : "Sanitized scene: nothing needed removing.");
        }

        private static List<GameEntity> Collect<T>(Scene scene) where T : ScriptComponentBehavior {
            var list = new List<GameEntity>();
            scene.GetAllEntitiesWithScriptComponent<T>(ref list);
            return list;
        }
    }
}
