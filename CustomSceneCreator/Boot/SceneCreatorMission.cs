using System;
using System.Collections.Generic;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
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
using TaleWorlds.MountAndBlade.Source.Missions.Handlers;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CustomSceneCreator.Boot {
    /// <summary>
    /// Opens the editing mission itself.
    /// </summary>
    public static class SceneCreatorMission {
        public static Mission? Open(string sceneName, string sceneLevels) =>
            Open(sceneName, sceneLevels, null);

        public static Mission? Open(string sceneName, string sceneLevels, SceneProject? project) {
            TraceLogger.Write(nameof(SceneCreatorMission),
                $"Opening mission — scene='{sceneName}' levels='{sceneLevels}'.");
            BootProbe.LogCampaignState("SceneCreatorMission.Open");

            // A project is what makes edits persist. Without one the editor still works, but its
            // target is a throwaway - useful for looking around a scene, not for building in it.
            project ??= new SceneProject {
                Name = sceneName,
                TargetScene = sceneName,
                SceneLevels = sceneLevels ?? "",
            };
            var target = new SceneProjectTarget(project);

            CameraModes.Reset();
            WeaponSheather.Reset();

            MissionInitializerRecord record = CreateRecord(sceneName, sceneLevels);

            return MissionState.OpenNew(
                "CustomSceneCreator",
                record,
                mission => {
                    // Mirrors the behaviour set the shipping homestead walk-around uses, minus its
                    // homestead-specific logics. That configuration is known to give a controllable
                    // free-roaming player in a non-battle scene, so it is a better starting point
                    // than a minimal list assembled by guesswork.
                    var behaviors = new List<MissionBehavior> {
                        // MUST be first: it strips scripts that would throw during scene start, and
                        // it can only do that from EarlyStart, before mission objects initialise.
                        new SceneScriptSanitizer(),

                        new MissionOptionsComponent(),
                        new CampaignMissionComponent(),
                        new MissionBasicTeamLogic(),
                        new BasicLeaveMissionLogic(),
                        new MissionSingleplayerViewHandler(),
                        new MissionAgentLookHandler(),
                        new HeroSkillHandler(),
                        new MissionFacialAnimationHandler(),
                        new AgentHumanAILogic(),
                        // Populates Mission.Boundaries; several view handlers assume it exists.
                        new MissionBoundaryPlacer(),

                        new SpikePlayerSpawnLogic(),
                        new CampaignScreenBlockerLogic(),
                        new EditorNoDamageLogic(),
                        new SceneEditingMissionLogic(target, new CatalogPlaceableProvider()),

                        new MissionMainAgentController(),
                        new EquipmentControllerLeaveLogic(),
                        ViewCreator.CreateMissionLeaveView(),
                        ViewCreator.CreateMissionAgentStatusUIHandler(mission),
                        ViewCreator.CreateMissionSingleplayerEscapeMenu(false),
                        ViewCreator.CreateOptionsUIHandler(),
                        ViewCreator.CreatePhotoModeView(),

                        // A MissionView, not a MissionLogic: it has to override the camera, which
                        // only the view layer gets a say in.
                        new RtsCameraView(),
                        new UI.AssetPickerView(),
                        new UI.EditorStatusView(),
                        new UI.ExportDialogView(),
                        new UI.ScriptPanelView(),
                        new UI.SceneOutlinerView(),
                    };
                    return behaviors.ToArray();
                });
        }

        /// <summary>
        /// Builds the mission record.
        ///
        /// Prefers <c>SandBoxMissions.CreateSandBoxMissionInitializerRecord</c>, which fills in
        /// campaign atmosphere and terrain type so the scene is lit to match the time of day the
        /// player left the map at.  That helper dereferences <c>MobileParty.MainParty</c> unguarded
        /// on its first line, so it is only safe once a campaign has a main party — true for the
        /// in-campaign entry path, and the reason the hand-built fallback below still exists.
        /// </summary>
        private static MissionInitializerRecord CreateRecord(string sceneName, string sceneLevels) {
            try {
                if (MobileParty.MainParty != null) {
                    MissionInitializerRecord sandboxRecord = SandBoxMissions
                        .CreateSandBoxMissionInitializerRecord(sceneName, sceneLevels ?? "", false, DecalAtlasGroup.Town);
                    // Raw scene terrain, never the campaign-map heightfield stamp: a patch applied
                    // here would silently shift the ground height under every placed object.
                    // Map-patch terrain is a deliberate opt-in feature later (plan section 10b).
                    sandboxRecord.SceneHasMapPatch = false;
                    return sandboxRecord;
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(SceneCreatorMission),
                    $"Sandbox record creation failed ({ex.GetType().Name}: {ex.Message}); using minimal record.");
            }

            return new MissionInitializerRecord(sceneName) {
                SceneLevels = sceneLevels ?? "",
                DoNotUseLoadingScreen = false,
                PlayingInCampaignMode = false,
                SceneHasMapPatch = false,
                DecalAtlasGroup = (int)DecalAtlasGroup.Town,
                AtmosphereOnCampaign = AtmosphereInfo.GetInvalidAtmosphereInfo(),
            };
        }
    }
}
