using System;
using CustomSceneCreator.Boot;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator {
    /// <summary>
    /// Entry point. Registers the main-menu option that boots the editor without a campaign.
    ///
    /// M1 SPIKE SCOPE: one hardcoded scene, a walking player, a free camera, and enough logging to
    /// answer the open question — does <c>CampaignGameMode.Tutorial</c> leave <c>Hero.MainHero</c>
    /// null?  No scene browser, no asset catalog, no picker.  See CUSTOM_SCENE_CREATOR_PLAN.md §17.
    /// </summary>
    public class SubModule : MBSubModuleBase {
        /// <summary>Scene the spike opens. A flat multiplayer map with no settlement scripts and no
        /// level masks — the least interesting scene we own, chosen so a failure is a boot failure
        /// and not a scene-content failure.</summary>
        public const string SpikeSceneName = "mp_skirmish_spawn_test";

        protected override void OnSubModuleLoad() {
            base.OnSubModuleLoad();
            try {
                TraceLogger.StartSession("OnSubModuleLoad");

                Module.CurrentModule.AddInitialStateOption(new InitialStateOption(
                    "CustomSceneCreator",
                    new TextObject("{=CSC_MainMenu}Scene Creator"),
                    // Sits just above Options (9998) / Credits (9999) / Exit (10000), below the
                    // game's own play options.
                    9990,
                    StartSceneCreator,
                    () => (false, null)));

                TraceLogger.Write(nameof(SubModule), "Main-menu option 'Scene Creator' registered.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SubModule), "Failed to register main-menu option", ex);
            }
        }

        private static void StartSceneCreator() {
            try {
                TraceLogger.Write(nameof(SubModule),
                    $"Main-menu option clicked — starting boot spike on scene '{SpikeSceneName}'.");
                MBGameManager.StartNewGame(new SceneCreatorGameManager(SpikeSceneName, sceneLevels: ""));
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SubModule), "StartSceneCreator threw", ex);
            }
        }
    }
}
