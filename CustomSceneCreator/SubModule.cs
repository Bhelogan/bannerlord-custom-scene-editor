using System;
using CustomSceneCreator.CampaignEntry;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator {
    /// <summary>
    /// Entry point.
    ///
    /// The editor is opened from inside a running campaign — via a settlement menu option or the
    /// <c>csc.open</c> console command — rather than from the main menu.  The reasoning, and the
    /// evidence behind it, is documented on <see cref="SceneCreatorCampaignBehavior"/>; the short
    /// version is that a main-menu campaign boot tells every installed mod the game is ready while
    /// the player character does not yet exist, and several mods crash on that.
    /// </summary>
    public class SubModule : MBSubModuleBase {
        protected override void OnSubModuleLoad() {
            base.OnSubModuleLoad();
            TraceLogger.StartSession("OnSubModuleLoad");
            TraceLogger.Write(nameof(SubModule),
                "Loaded. Editor entry: settlement menu option, or console command 'csc.open <scene>'.");
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject) {
            base.OnGameStart(game, gameStarterObject);
            try {
                // Guarded on both the game type and the starter type: OnGameStart is raised for
                // every game type, including custom battle and multiplayer, where there is no
                // campaign to add a behaviour to.
                if (game.GameType is Campaign
                    && gameStarterObject is CampaignGameStarter campaignStarter) {
                    campaignStarter.AddBehavior(new SceneCreatorCampaignBehavior());
                    TraceLogger.Write(nameof(SubModule), "Campaign behaviour registered.");
                }
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SubModule), "OnGameStart failed", ex);
            }
        }

        /// <summary>
        /// A real per-frame tick, which is what reopening the browser after the editor needs.
        ///
        /// It used to run on CampaignEvents.TickEvent, and that event is gated on campaign time:
        ///
        ///     if (_dt &gt; 0f || CurrentTickCount &lt; 3) CampaignEventDispatcher.Instance.Tick(_dt);
        ///
        /// Leaving a mission drops the player onto a PAUSED map, so _dt is zero and the event never
        /// fires - the browser only appeared once they started moving and time began flowing again.
        /// This tick runs regardless of the campaign clock.
        /// </summary>
        protected override void OnApplicationTick(float dt) {
            base.OnApplicationTick(dt);
            try {
                ReturnToBrowser.Tick();
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SubModule), "Return-to-browser tick failed", ex);
            }
        }
    }
}
