using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// M1 spike only: gets a controllable player agent into the scene and reports how it managed
    /// it.  Replaced in M4 by the real editor spawner.
    ///
    /// The fallback ladder is the point of this class.  Which rung it lands on tells us whether a
    /// tutorial-mode campaign can supply a player character, which is the question M1 exists to
    /// answer.
    /// </summary>
    public class SpikePlayerSpawnLogic : MissionLogic {
        private bool _spawned;

        /// <summary>How many frames to keep retrying before giving up and spawning anyway.</summary>
        private const int SpawnRetryFrameBudget = 300;

        /// <summary>
        /// Frames between retries. Even the cheap resolve pass walks a ranked candidate list, so
        /// running it every frame on a scene with thousands of entities is not free.
        /// </summary>
        private const int RetryEveryFrames = 20;

        private int _framesWaited;

        public override void AfterStart() {
            base.AfterStart();
            try {
                // Without this the mission sits in its default mode and the player has no control:
                // no movement, no camera. StartUp is the mode the shipping walk-around missions use
                // for a free-roaming player with no battle running.
                Mission.SetMissionMode(MissionMode.StartUp, atStart: true);

                SpawnPointResolver.ResetCache();

                // Decisive one-line diagnostic: if this is 0 the scene has no usable navmesh at the
                // chosen upgrade levels, and no amount of searching will find a walkable spot. That
                // distinguishes "our search is wrong" from "there is nothing to find".
                try {
                    TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                        $"Scene navmesh face count: {Mission.Scene.GetNavMeshFaceCount()}.");
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                        $"GetNavMeshFaceCount threw {ex.GetType().Name}: {ex.Message}");
                }

                TrySpawn(force: false);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SpikePlayerSpawnLogic), "AfterStart spawn threw", ex);
            }
        }

        /// <summary>
        /// On large multi-level scenes the navmesh is not always queryable by the time AfterStart
        /// runs, so a first attempt can legitimately find nothing walkable anywhere. Retrying across
        /// a frame budget costs nothing when the first attempt works and rescues the case where it
        /// does not - which is what happened on aserai_castle_002, where the player ended up at the
        /// origin with no navmesh found at all.
        /// </summary>
        public override void OnMissionTick(float dt) {
            base.OnMissionTick(dt);
            if (_spawned) return;

            _framesWaited++;
            bool force = _framesWaited >= SpawnRetryFrameBudget;
            if (!force && _framesWaited % RetryEveryFrames != 0) return;

            TrySpawn(force);
        }

        private void TrySpawn(bool force) {
            try {
                SpawnPlayer(force);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SpikePlayerSpawnLogic), "SpawnPlayer threw", ex);
                _spawned = true;   // stop retrying a throwing path every frame
            }
        }

        private void SpawnPlayer(bool force) {
            if (_spawned) return;

            Vec3 position = SpawnPointResolver.Resolve(Mission.Scene, allowExpensiveSearch: force, out string how);

            bool found = position.IsValid && position != Vec3.Zero;
            if (!found && !force) {
                // Say it once, then stay quiet while retrying - this runs every frame.
                if (_framesWaited <= 1) {
                    TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                        "No navmesh position yet; retrying while the scene finishes loading.");
                }
                return;
            }

            CharacterObject? character = ResolvePlayerCharacter(out string source);
            if (character == null) {
                TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                    "FATAL: no CharacterObject available to spawn as the player.");
                _spawned = true;
                return;
            }
            TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                $"Player character resolved via {source}: '{character.StringId}'.");
            TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                $"Spawn position resolved via {how} after {_framesWaited} frame(s): " +
                $"({position.x:0.##}, {position.y:0.##}, {position.z:0.##}).");

            if (!found) {
                TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                    "WARNING: gave up looking for a navmesh position; spawning at the origin. " +
                    "The player will likely be unable to move. Check scene_catalog.xml for " +
                    "noNavMesh on this scene, and check the selected upgrade levels — a scene " +
                    "opened without its 'base' level can be missing most of its walkable ground.");
                position = Vec3.Zero;
            }
            Vec2 direction = new Vec2(0f, 1f);

            AgentBuildData buildData = new AgentBuildData(character)
                .Team(Mission.PlayerTeam)
                .InitialPosition(position)
                .InitialDirection(direction)
                .NoHorses(true)
                .Controller(AgentControllerType.Player);

            Agent agent = Mission.SpawnAgent(buildData);
            Mission.MainAgent = agent;
            _spawned = true;

            TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                $"Player spawned at ({position.x:0.##}, {position.y:0.##}, {position.z:0.##}); " +
                $"Mission.MainAgent set = {Mission.MainAgent != null}.");
        }

        /// <summary>
        /// Tries, in order: the campaign's own player character, then a plain troop from the object
        /// manager.  <paramref name="source"/> records which rung succeeded — that string is the
        /// spike's headline result.
        /// </summary>
        private CharacterObject? ResolvePlayerCharacter(out string source) {
            source = "none";

            try {
                if (CharacterObject.PlayerCharacter != null) {
                    source = "CharacterObject.PlayerCharacter";
                    return CharacterObject.PlayerCharacter;
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                    $"CharacterObject.PlayerCharacter threw {ex.GetType().Name}: {ex.Message}");
            }

            try {
                var all = Game.Current?.ObjectManager.GetObjectTypeList<CharacterObject>();
                if (all != null && all.Count > 0) {
                    // A non-hero, non-template troop: something guaranteed to have real equipment
                    // and a normal humanoid monster.
                    CharacterObject? troop = all.FirstOrDefault(c =>
                        c.HeroObject == null && !c.IsTemplate && c.IsSoldier);
                    troop ??= all.FirstOrDefault(c => c.HeroObject == null && !c.IsTemplate);
                    if (troop != null) {
                        source = "ObjectManager fallback troop";
                        return troop;
                    }
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                    $"ObjectManager fallback threw {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }

    }
}
