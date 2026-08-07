using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace CustomSceneCreator.Boot {
    /// <summary>
    /// Diagnostics for the M1 boot spike.
    ///
    /// The whole reason M1 exists is to find out what a <c>CampaignGameMode.Tutorial</c> campaign
    /// actually gives us.  If <c>Hero.MainHero</c> is null the design shifts to the custom-game
    /// boot pattern instead, which changes how the player agent, equipment and spawn all work — so
    /// it is worth knowing precisely, and worth knowing before any of that code is written.
    ///
    /// Every probe is individually guarded: several of these properties throw rather than return
    /// null when their backing state is missing, and one throwing must not hide the rest.
    /// </summary>
    internal static class BootProbe {
        public static void LogCampaignState(string phase) {
            TraceLogger.Write(nameof(BootProbe), $"───── campaign state probe @ {phase} ─────");

            Probe("Game.Current", () => Game.Current == null ? "NULL" : "present");
            Probe("Campaign.Current", () => Campaign.Current == null ? "NULL" : "present");
            Probe("Campaign.GameMode", () => Campaign.Current!.GameMode.ToString());
            Probe("Campaign.GameStarted", () => Campaign.Current!.GameStarted.ToString());

            // The three that decide the design.
            Probe("Hero.MainHero", () => Hero.MainHero == null ? "NULL" : $"present ('{Hero.MainHero.Name}')");
            Probe("MobileParty.MainParty", () => MobileParty.MainParty == null ? "NULL" : "present");
            Probe("PartyBase.MainParty", () => PartyBase.MainParty == null ? "NULL" : "present");
            Probe("CharacterObject.PlayerCharacter",
                () => CharacterObject.PlayerCharacter == null
                    ? "NULL"
                    : $"present ('{CharacterObject.PlayerCharacter.StringId}')");

            // If the campaign gives us no player character we will need one from the object
            // manager, so confirm the object manager is populated at all.
            Probe("CharacterObject count",
                () => Game.Current!.ObjectManager.GetObjectTypeList<CharacterObject>().Count.ToString());
            Probe("First 5 CharacterObjects", () => string.Join(", ",
                Game.Current!.ObjectManager.GetObjectTypeList<CharacterObject>()
                    .Take(5).Select(c => c.StringId)));

            // Needed by anything that spawns an agent with real equipment.
            Probe("BasicCultureObject count",
                () => Game.Current!.ObjectManager.GetObjectTypeList<BasicCultureObject>().Count.ToString());

            TraceLogger.Write(nameof(BootProbe), "───── end probe ─────");
        }

        private static void Probe(string label, Func<string> read) {
            string value;
            try {
                value = read() ?? "NULL";
            } catch (Exception ex) {
                value = $"THREW {ex.GetType().Name}: {ex.Message}";
            }
            TraceLogger.Write(nameof(BootProbe), $"  {label,-32} = {value}");
        }
    }
}
