using System;
using System.Collections.Generic;
using CustomSceneCreator.Editing;
using SandBox.Missions.MissionLogics;
using SandBox.View.Missions;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.Source.Missions.Handlers;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CustomSceneCreator.Boot {
    /// <summary>
    /// Opens the editing mission itself.
    /// </summary>
    public static class SceneCreatorMission {
        public static Mission? Open(string sceneName, string sceneLevels) {
            TraceLogger.Write(nameof(SceneCreatorMission),
                $"Opening mission — scene='{sceneName}' levels='{sceneLevels}'.");

            MissionInitializerRecord record = CreateRecord(sceneName, sceneLevels);

            return MissionState.OpenNew(
                "CustomSceneCreator",
                record,
                mission => {
                    var behaviors = new List<MissionBehavior> {
                        new MissionOptionsComponent(),
                        new MissionBasicTeamLogic(),
                        new BasicLeaveMissionLogic(),
                        new MissionSingleplayerViewHandler(),
                        new MissionAgentLookHandler(),
                        new MissionFacialAnimationHandler(),

                        // Spike-only: spawns the player and reports what it had to fall back to.
                        new SpikePlayerSpawnLogic(),

                        new MissionMainAgentController(),
                        ViewCreator.CreateMissionAgentStatusUIHandler(mission),
                        ViewCreator.CreateMissionSingleplayerEscapeMenu(false),
                        ViewCreator.CreateOptionsUIHandler(),
                    };
                    return behaviors.ToArray();
                });
        }

        /// <summary>
        /// Builds the record by hand rather than through
        /// <c>SandBoxMissions.CreateSandBoxMissionInitializerRecord</c>.
        ///
        /// That helper dereferences <c>MobileParty.MainParty</c> unguarded on its very first line to
        /// read a map position, so it cannot survive a campaign that has no main party — which is
        /// exactly the situation this boot path may be in.  The fields it would have set are either
        /// irrelevant to an editing session (friendly-fire multipliers, campaign atmosphere) or set
        /// explicitly below.
        /// </summary>
        private static MissionInitializerRecord CreateRecord(string sceneName, string sceneLevels) {
            return new MissionInitializerRecord(sceneName) {
                SceneLevels = sceneLevels ?? "",
                DoNotUseLoadingScreen = false,
                PlayingInCampaignMode = false,
                // Raw scene terrain, no campaign-map heightfield stamp. Map-patch terrain is a
                // deliberate later feature (plan §10b) and must be opt-in, because a patch applied
                // here would silently move every placed object's ground height.
                SceneHasMapPatch = false,
                DecalAtlasGroup = (int)DecalAtlasGroup.Town,
                AtmosphereOnCampaign = AtmosphereInfo.GetInvalidAtmosphereInfo(),
            };
        }
    }
}
