using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Library;

namespace CustomSceneCreator.CampaignEntry {
    /// <summary>
    /// Console commands. These are the fastest way to try an arbitrary scene without waiting on the
    /// browser UI, and they stay useful afterwards for reproducing a specific scene + level
    /// combination when something misbehaves.
    /// </summary>
    public static class SceneCreatorConsoleCommands {
        [CommandLineFunctionality.CommandLineArgumentFunction("open", "csc")]
        public static string Open(List<string> args) {
            if (args == null || args.Count == 0) {
                return "Usage: csc.open <scene_name> [scene_levels]\n" +
                       $"Example: csc.open {SceneCreatorEntry.DefaultScene}\n" +
                       "Example: csc.open aserai_town_a \"base level_1 civilian\"";
            }

            string scene = args[0];
            // Levels are space-separated and may be passed either quoted as one argument or as
            // several bare ones; accept both rather than making the caller remember which.
            string levels = args.Count > 1 ? string.Join(" ", args.Skip(1)) : "";

            return SceneCreatorEntry.OpenEditor(scene, levels)
                ? $"Opening scene creator on '{scene}'" + (levels.Length > 0 ? $" (levels: {levels})" : "") + "."
                : $"Failed to open '{scene}'. See CustomSceneCreator.trace.log.";
        }
    }
}
