using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Catalog;
using CustomSceneCreator.Editing;
using SandBox;
using SandBox.Missions.MissionLogics;
using SandBox.View.Missions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CustomSceneCreator.Boot {
    /// <summary>A non-editing test of a saved layout with the current party following the player.</summary>
    public static class SceneWalkaroundMission {
        public static Mission? Open(SceneProject project, string? sceneOverride = null) {
            string scene = sceneOverride ?? project?.TargetScene ?? "";
            if (project == null || string.IsNullOrWhiteSpace(scene)) return null;
            MissionInitializerRecord record = CreateRecord(scene, project.SceneLevels ?? "");
            return MissionState.OpenNew("CSCWalkaround", record, mission => new MissionBehavior[] {
                new SceneScriptSanitizer(), new MissionOptionsComponent(), new CampaignMissionComponent(),
                new MissionBasicTeamLogic(), new BasicLeaveMissionLogic(), new MissionSingleplayerViewHandler(),
                new MissionAgentLookHandler(), new HeroSkillHandler(),
                new AgentHumanAILogic(), new MissionBoundaryPlacer(), new SpikePlayerSpawnLogic(),
                new ProjectWalkaroundLogic(project), new MissionMainAgentController(),
                new EquipmentControllerLeaveLogic(), ViewCreator.CreateMissionLeaveView(),
                ViewCreator.CreateMissionAgentStatusUIHandler(mission),
                ViewCreator.CreateMissionSingleplayerEscapeMenu(false), ViewCreator.CreateOptionsUIHandler(),
                ViewCreator.CreatePhotoModeView(),
            });
        }

        private static MissionInitializerRecord CreateRecord(string scene, string levels) {
            try {
                if (MobileParty.MainParty != null) {
                    MissionInitializerRecord record = SandBoxMissions.CreateSandBoxMissionInitializerRecord(
                        scene, levels, false, DecalAtlasGroup.Town);
                    record.SceneHasMapPatch = false;
                    return record;
                }
            } catch (Exception ex) { TraceLogger.Write(nameof(SceneWalkaroundMission), ex.Message); }
            return new MissionInitializerRecord(scene) { SceneLevels = levels, SceneHasMapPatch = false,
                DecalAtlasGroup = (int)DecalAtlasGroup.Town, AtmosphereOnCampaign = AtmosphereInfo.GetInvalidAtmosphereInfo() };
        }
    }

    internal sealed class ProjectWalkaroundLogic : MissionLogic {
        private readonly SceneProject _project;
        private readonly bool _spawnFollowers;
        private bool _objectsPlaced, _partyPlaced;
        private float _elapsed;
        public ProjectWalkaroundLogic(SceneProject project, bool spawnFollowers = true) {
            _project = project;
            _spawnFollowers = spawnFollowers;
        }

        public override void AfterStart() { base.AfterStart(); PlaceProjectObjects(); }
        public override void OnMissionTick(float dt) {
            base.OnMissionTick(dt); _elapsed += dt;
            if (_spawnFollowers && !_partyPlaced && _elapsed > 0.5f && Mission.MainAgent != null) SpawnPartyFollowers();
        }

        private void PlaceProjectObjects() {
            if (_objectsPlaced) return; _objectsPlaced = true;
            int restored = 0;
            int unavailable = 0;
            foreach (ProjectEntity saved in _project.Entities ?? new List<ProjectEntity>()) {
                try {
                    string prefab = PlaceableRegistry.ResolveSpawnPrefab(saved.Prefab);
                    if (!GameEntity.PrefabExists(prefab)) { unavailable++; continue; }
                    MatrixFrame frame = MatrixFrame.Identity; frame.rotation = saved.To().Rotation; frame.origin = saved.To().Position;
                    GameEntity entity = GameEntity.Instantiate(Mission.Scene, prefab, frame);
                    PlacedScriptGuard.Strip(entity, prefab); entity.SetGlobalFrame(in frame, true);
                    EnablePhysics(entity);
                    restored++;
                } catch (Exception ex) { TraceLogger.Write(nameof(ProjectWalkaroundLogic), $"Could not restore '{saved.Prefab}': {ex.Message}"); }
            }
            TraceLogger.Write(nameof(ProjectWalkaroundLogic),
                $"Restored {restored}/{_project.Entities?.Count ?? 0} project object(s) into '{Mission.SceneName}'" +
                (unavailable > 0 ? $"; {unavailable} prefab(s) unavailable." : "."));
        }
        private static void EnablePhysics(GameEntity entity) {
            entity.SetPhysicsState(true, true);
            for (int i = 0; i < entity.ChildCount; i++) EnablePhysics(entity.GetChild(i));
        }
        private void SpawnPartyFollowers() {
            _partyPlaced = true;
            if (MobileParty.MainParty == null || Mission.PlayerTeam == null || Mission.MainAgent == null) return;
            Formation formation = Mission.PlayerTeam.GetFormation(FormationClass.Infantry);
            int i = 0;
            foreach (var member in MobileParty.MainParty.MemberRoster.GetTroopRoster()) {
                CharacterObject character = member.Character;
                if (character == null || !character.IsHero || character == CharacterObject.PlayerCharacter
                    || character.HeroObject == null || character.HeroObject.IsWounded) continue;
                SpawnFollower(character, i++, formation);
            }
            // Fresh campaigns often have no companions. Always give the walk-around a small group
            // so collision and following behaviour can be tested immediately, without touching the
            // campaign roster. These are deliberately additional to companions: a project with a
            // full party should still have the same five disposable pathing probes.
            CharacterObject? peasant = Game.Current?.ObjectManager.GetObject<CharacterObject>("villager_empire");
            for (int peasantIndex = 0; peasantIndex < 5 && peasant != null; peasantIndex++) {
                SpawnFollower(peasant, i++, formation);
            }
            formation.SetControlledByAI(true);
            formation.SetMovementOrder(MovementOrder.MovementOrderFollow(Mission.MainAgent));
            TraceLogger.Write(nameof(ProjectWalkaroundLogic), $"Walk-around restored {_project.Entities?.Count ?? 0} object(s); spawned {i} party follower(s).");
        }
        private void SpawnFollower(CharacterObject character, int index, Formation formation) {
            float angle = index * 1.7f;
            Vec3 pos = Mission.MainAgent.Position + new Vec3((float)Math.Cos(angle) * 2f, (float)Math.Sin(angle) * 2f, 0f);
            pos.z = Mission.Scene.GetGroundHeightAtPosition(pos) + 0.1f;
            Agent agent = Mission.SpawnAgent(new AgentBuildData(character).Team(Mission.PlayerTeam)
                .Formation(formation).InitialPosition(pos).InitialDirection(new Vec2(0f, 1f)).NoHorses(true));
            agent.Controller = AgentControllerType.AI;
        }
    }
}
