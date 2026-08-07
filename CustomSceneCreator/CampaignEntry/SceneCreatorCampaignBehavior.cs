using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;

namespace CustomSceneCreator.CampaignEntry {
    /// <summary>
    /// In-campaign entry point for the scene editor.
    ///
    /// WHY THIS IS THE PRIMARY ENTRY AND NOT THE MAIN MENU:
    ///
    /// Booting a campaign from the main menu fires
    /// <c>MBSubModuleBase.OnAfterGameInitializationFinished</c> on EVERY installed module.  That
    /// callback is raised unconditionally at the end of <c>Campaign.DoLoadingForGameType</c>, but
    /// the work that gives a campaign a player - <c>InitializeMainParty()</c> - only runs on the
    /// <c>NewCampaign</c> and <c>SavedCampaign</c> paths.  A <c>Tutorial</c>-mode boot runs neither.
    ///
    /// The result is that every mod on the machine gets told "the game is ready" while
    /// <c>Hero.MainHero</c> is still null.  Mods reasonably assume otherwise and dereference it
    /// immediately.  This is not a bug we can fix in our own code, and it is not limited to one
    /// mod: on this install alone, CharacterReload, DistinguishedServicePlus and ChatAi all
    /// override that callback.  CharacterReload is simply the one that crashed first.
    ///
    /// Entering from inside a live campaign sidesteps the entire problem.  Every module's
    /// initialisation has already completed successfully, the player character and party are real,
    /// and the sandbox mission helpers work unmodified.  It also removes a large amount of boot
    /// machinery we would otherwise own and have to keep working across game updates.
    /// </summary>
    public class SceneCreatorCampaignBehavior : CampaignBehaviorBase {
        /// <summary>Menus the editor option is offered on. Settlement menus are used because they
        /// are where a player already stops, and they are reachable in every campaign regardless of
        /// what else is installed.</summary>
        private static readonly string[] HostMenus = { "town", "village", "castle" };

        public override void RegisterEvents() {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore) {
            // Nothing persisted: editor projects live in their own JSON files, not the campaign save.
        }

        private void OnSessionLaunched(CampaignGameStarter starter) {
            try {
                foreach (string menu in HostMenus) {
                    starter.AddGameMenuOption(
                        menu,
                        $"csc_open_editor_{menu}",
                        "{=CSC_MenuOption}Open Scene Creator",
                        OnConditionOpenEditor,
                        OnConsequenceOpenEditor,
                        isLeave: false,
                        index: -1,
                        isRepeatable: false);
                }
                TraceLogger.Write(nameof(SceneCreatorCampaignBehavior),
                    $"Registered menu option on: {string.Join(", ", HostMenus)}.");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(SceneCreatorCampaignBehavior),
                    "Failed to register game menu options", ex);
            }
        }

        private static bool OnConditionOpenEditor(MenuCallbackArgs args) {
            args.optionLeaveType = GameMenuOption.LeaveType.Mission;
            return true;
        }

        private static void OnConsequenceOpenEditor(MenuCallbackArgs args) {
            // Placeholder until the scene browser lands (plan M6). Opening a known-flat scene keeps
            // this option useful as a smoke test in the meantime.
            SceneCreatorEntry.OpenEditor(SceneCreatorEntry.DefaultScene, sceneLevels: "");
        }
    }
}
