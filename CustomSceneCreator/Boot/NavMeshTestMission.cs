using System;
using System.Collections.Generic;
using CustomSceneCreator.Editing;
using SandBox;
using SandBox.Missions.MissionLogics;
using SandBox.View.Missions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CustomSceneCreator.Boot {
    /// <summary>
    /// Opens a practice skirmish battle on a scene for navmesh routing tests.
    ///
    /// The behavior set mirrors BloodGames' BloodGamesMissionStarter and Homesteads'
    /// CustomMissions.StartHomesteadSparringMission — both proven to produce agents that
    /// actually fight and pathfind. The critical behaviors are:
    ///
    ///   AgentHumanAILogic  — drives AI combat decisions (who to attack, how to move)
    ///   AgentVictoryLogic  — handles mission-end timing
    ///   BannerBearerLogic  — team identification for the AI
    ///
    /// MissionBasicTeamLogic is deliberately omitted (same as BloodGames) because it
    /// pre-creates banner-less teams before AfterStart runs. NavMeshTestBattleLogic
    /// creates teams itself with explicit enemy relations.
    /// </summary>
    public static class NavMeshTestMission {
        public static Mission? Open(string sceneName, string sceneLevels, Vec3 playerPos, Vec3 enemyPos,
                                     int enemyCount, SceneProject? project = null) {
            TraceLogger.Write(nameof(NavMeshTestMission),
                $"Opening navmesh-test skirmish — scene='{sceneName}' levels='{sceneLevels}' " +
                $"player=({playerPos.x:0.##},{playerPos.y:0.##}) enemy=({enemyPos.x:0.##},{enemyPos.y:0.##}) " +
                $"enemies={enemyCount}.");

            MissionInitializerRecord record = CreateRecord(sceneName, sceneLevels ?? "");

            Mission? opened = MissionState.OpenNew(
                "CSCNavMeshTest",
                record,
                mission => {
                    var behaviors = new List<MissionBehavior> {
                        new MissionOptionsComponent(),
                        new CampaignMissionComponent(),
                        // MissionBasicTeamLogic intentionally omitted — see class doc.
                        new BasicLeaveMissionLogic(),
                        new MissionSingleplayerViewHandler(),
                        new MissionAgentLookHandler(),
                        new HeroSkillHandler(),
                        new AgentHumanAILogic(),
                        new MissionBoundaryPlacer(),

                        // The navmesh test battle logic — creates teams, spawns player + enemies,
                        // issues charge orders, and ends the mission when one side is wiped out.
                        new NavMeshTestBattleLogic(playerPos, enemyPos, enemyCount),

                        // Combat and AI behaviors needed for agents to actually fight.
                        new AgentVictoryLogic(),
                        new BannerBearerLogic(),
                        new MissionMainAgentController(),
                        new EquipmentControllerLeaveLogic(),

                        // Views for a playable battle.
                        ViewCreator.CreateMissionLeaveView(),
                        ViewCreator.CreateMissionAgentStatusUIHandler(mission),
                        ViewCreator.CreateMissionSingleplayerEscapeMenu(false),
                        ViewCreator.CreateOptionsUIHandler(),
                        ViewCreator.CreatePhotoModeView(),
                    };
                    // The reusable scene slot supplies terrain/navmesh only. Project objects are
                    // runtime entities, so combat restores them just as walk-around does. Followers
                    // stay disabled because the battle logic supplies the player-party side itself.
                    if (project != null) behaviors.Insert(8, new ProjectWalkaroundLogic(project, spawnFollowers: false));
                    return behaviors.ToArray();
                });

            TraceLogger.Write(nameof(NavMeshTestMission),
                opened == null ? "MissionState.OpenNew returned null." : "Mission opened.");
            return opened;
        }

        private static MissionInitializerRecord CreateRecord(string sceneName, string sceneLevels) {
            try {
                if (MobileParty.MainParty != null) {
                    MissionInitializerRecord sandboxRecord = SandBoxMissions
                        .CreateSandBoxMissionInitializerRecord(sceneName, sceneLevels ?? "", false,
                            DecalAtlasGroup.Battle);
                    sandboxRecord.SceneHasMapPatch = false;
                    return sandboxRecord;
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(NavMeshTestMission),
                    $"Sandbox record creation failed ({ex.GetType().Name}: {ex.Message}); using minimal record.");
            }

            return new MissionInitializerRecord(sceneName) {
                SceneLevels = sceneLevels ?? "",
                DoNotUseLoadingScreen = false,
                PlayingInCampaignMode = false,
                SceneHasMapPatch = false,
                DecalAtlasGroup = (int)DecalAtlasGroup.Battle,
                AtmosphereOnCampaign = AtmosphereInfo.GetInvalidAtmosphereInfo(),
            };
        }
    }
}
