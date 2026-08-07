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

        public override void AfterStart() {
            base.AfterStart();
            try {
                SpawnPlayer();
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SpikePlayerSpawnLogic), "SpawnPlayer threw", ex);
            }
        }

        private void SpawnPlayer() {
            if (_spawned) return;

            CharacterObject? character = ResolvePlayerCharacter(out string source);
            if (character == null) {
                TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                    "FATAL: no CharacterObject available to spawn as the player. " +
                    "Tutorial-mode boot cannot supply a player character.");
                return;
            }
            TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                $"Player character resolved via {source}: '{character.StringId}'.");

            Vec3 position = FindSpawnPosition();
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

        /// <summary>
        /// Prefers an authored spawn point, then the scene's own centre, then the origin.  Dropping
        /// the player at the origin of an arbitrary scene often means dropping them off the map, so
        /// the ground-height lookup matters more than it looks.
        /// </summary>
        private Vec3 FindSpawnPosition() {
            foreach (string tag in new[] { "sp_player", "spawnpoint_player", "sp_player_1" }) {
                try {
                    GameEntity? entity = Mission.Scene.FindEntitiesWithTag(tag).FirstOrDefault();
                    if (entity != null) {
                        TraceLogger.Write(nameof(SpikePlayerSpawnLogic), $"Using spawn tag '{tag}'.");
                        return entity.GlobalPosition;
                    }
                } catch {
                    // FindEntitiesWithTag throws on some scenes rather than returning empty.
                }
            }

            try {
                Vec3 min, max;
                Mission.Scene.GetBoundingBox(out min, out max);
                Vec2 centre = new Vec2((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f);
                float height = Mission.Scene.GetTerrainHeight(centre, true);
                TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                    $"No spawn tag found; using scene centre ({centre.x:0.##}, {centre.y:0.##}) " +
                    $"at terrain height {height:0.##}.");
                return new Vec3(centre.x, centre.y, height);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SpikePlayerSpawnLogic),
                    $"Scene-centre lookup failed ({ex.GetType().Name}); falling back to origin.");
                return Vec3.Zero;
            }
        }
    }
}
