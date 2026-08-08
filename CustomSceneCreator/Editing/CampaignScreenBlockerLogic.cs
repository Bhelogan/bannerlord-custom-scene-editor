using System;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Shuts off the campaign panels inside an editing session: inventory, character, banner editor,
    /// clan, kingdom, party, quests, encyclopedia.
    ///
    /// <c>MissionSingleplayerViewHandler</c> - which we want for its other duties - registers the
    /// GenericCampaignPanelsGameKeyCategory and opens those screens on their hotkeys in any
    /// non-battle mission. In an editing session that is at best a distraction and at worst
    /// destructive: the banner editor and character screen push a whole GameState over the mission,
    /// and re-equipping the player mid-edit serves no purpose here.
    ///
    /// The handler already checks a per-mission flag before opening each one, so this needs no
    /// patching - just set the flags. That also keeps the "you cannot reach that right now" message
    /// the game shows, rather than making the key silently do nothing.
    /// </summary>
    public class CampaignScreenBlockerLogic : MissionLogic {
        public override void AfterStart() {
            base.AfterStart();
            try {
                Mission.IsInventoryAccessible = false;
                Mission.IsCharacterWindowAccessible = false;
                Mission.IsBannerWindowAccessible = false;
                Mission.IsPartyWindowAccessible = false;
                Mission.IsClanWindowAccessible = false;
                Mission.IsKingdomWindowAccessible = false;
                Mission.IsQuestScreenAccessible = false;
                Mission.IsEncyclopediaWindowAccessible = false;

                TraceLogger.Write(nameof(CampaignScreenBlockerLogic),
                    "Campaign panels disabled for this editing session.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(CampaignScreenBlockerLogic),
                    "Could not disable campaign panels", ex);
            }
        }
    }
}
