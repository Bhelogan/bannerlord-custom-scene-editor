using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Boot {
    /// <summary>
    /// PARKED - NOT WIRED UP. Kept as reference, not called by anything.
    ///
    /// This booted a scene-editing session straight from the main menu.  It reached the mission but
    /// crashed reliably on a modded install, and the cause is not fixable from inside this mod:
    ///
    ///   <c>Campaign.DoLoadingForGameType</c> raises
    ///   <c>OnAfterGameInitializationFinished</c> on every installed module unconditionally, but
    ///   only calls <c>InitializeMainParty()</c> on the <c>NewCampaign</c> / <c>SavedCampaign</c>
    ///   paths.  A <c>Tutorial</c> boot runs neither, so every mod is told the game is ready while
    ///   <c>Hero.MainHero</c> is still null.  CharacterReload threw first
    ///   (<c>Clan.PlayerClan</c>); DistinguishedServicePlus and ChatAi override the same callback.
    ///
    /// The editor is entered from inside a live campaign instead - see
    /// <c>SceneCreatorCampaignBehavior</c>.  This file stays because if a main-menu entry is ever
    /// revisited it must use a non-Campaign <c>GameType</c> (the custom-battle pattern), since
    /// <c>OnAfterGameInitializationFinished</c> is raised only from <c>Campaign</c>.  That route
    /// gives up <c>Campaign.Current</c> and needs a different player-agent path.
    ///
    /// Original notes follow.
    ///
    /// Modelled on the game's own <c>EditorSceneMissionManager</c>, which is what the official
    /// editor uses to open an arbitrary scene from a cold start.  The loading state machine below
    /// is deliberately a faithful copy of it — the step ordering is load-bearing (module data must
    /// be loaded before the Game exists, submodules must finish <c>DoLoading</c> before
    /// <c>StartNewGame</c>, and the mission can only open once <c>OnLoadFinished</c> runs).
    ///
    /// A <c>CampaignGameMode.Tutorial</c> campaign is created rather than a full sandbox one: it is
    /// far cheaper, and it still gives us a live <c>Campaign.Current</c> so that campaign-flavoured
    /// mission behaviours (agent status UI, escape menu, photo mode) work unchanged.
    ///
    /// OPEN QUESTION THIS SPIKE ANSWERS: whether a Tutorial-mode campaign leaves
    /// <see cref="Hero.MainHero"/> / <c>MobileParty.MainParty</c> null.  See <see cref="BootProbe"/>.
    /// </summary>
    public class SceneCreatorGameManager : MBGameManager {
        private readonly string _sceneName;
        private readonly string _sceneLevels;

        public SceneCreatorGameManager(string sceneName, string sceneLevels) {
            _sceneName = sceneName;
            _sceneLevels = sceneLevels ?? "";
        }

        protected override void DoLoadingForGameManager(
            GameManagerLoadingSteps gameManagerLoadingStep, out GameManagerLoadingSteps nextStep) {
            nextStep = GameManagerLoadingSteps.None;

            switch (gameManagerLoadingStep) {
                case GameManagerLoadingSteps.PreInitializeZerothStep: {
                    TraceLogger.Write(nameof(SceneCreatorGameManager), "Step 0: loading module data.");
                    LoadModuleData(isLoadGame: false);
                    MBGlobals.InitializeReferences();

                    Campaign campaign = new Campaign(CampaignGameMode.Tutorial);
                    Game game = Game.CreateGame(campaign, this);
                    campaign.SetLoadingParameters(Campaign.GameLoadingType.Tutorial);
                    game.DoLoading();

                    TraceLogger.Write(nameof(SceneCreatorGameManager), "Step 0: tutorial-mode campaign created.");
                    nextStep = GameManagerLoadingSteps.FirstInitializeFirstStep;
                    break;
                }

                case GameManagerLoadingSteps.FirstInitializeFirstStep: {
                    bool allLoaded = true;
                    foreach (MBSubModuleBase subModule in Module.CurrentModule.CollectSubModules()) {
                        allLoaded = allLoaded && subModule.DoLoading(Game.Current);
                    }
                    nextStep = allLoaded
                        ? GameManagerLoadingSteps.WaitSecondStep
                        : GameManagerLoadingSteps.FirstInitializeFirstStep;
                    break;
                }

                case GameManagerLoadingSteps.WaitSecondStep:
                    StartNewGame();
                    nextStep = GameManagerLoadingSteps.SecondInitializeThirdState;
                    break;

                case GameManagerLoadingSteps.SecondInitializeThirdState:
                    nextStep = Game.Current.DoLoading()
                        ? GameManagerLoadingSteps.PostInitializeFourthState
                        : GameManagerLoadingSteps.SecondInitializeThirdState;
                    break;

                case GameManagerLoadingSteps.PostInitializeFourthState:
                    nextStep = GameManagerLoadingSteps.FinishLoadingFifthStep;
                    break;

                case GameManagerLoadingSteps.FinishLoadingFifthStep:
                    nextStep = GameManagerLoadingSteps.None;
                    break;
            }
        }

        public override void OnAfterCampaignStart(Game game) {
        }

        public override void OnLoadFinished() {
            base.OnLoadFinished();
            try {
                MBGlobals.InitializeReferences();

                // The editor manager calls this for the non-replay path; it wires up campaign
                // gameplay references the mission behaviours expect to exist.
                Campaign.Current?.InitializeGamePlayReferences();

                // Everything the spike exists to learn is captured here, before the mission opens,
                // so a crash inside mission creation still leaves the answers on disk.
                BootProbe.LogCampaignState("OnLoadFinished");

                SceneCreatorMission.Open(_sceneName, _sceneLevels);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneCreatorGameManager), "OnLoadFinished threw", ex);
                throw;
            }
        }
    }
}
