using TaleWorlds.Core;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// On-screen feedback for the editor.
    ///
    /// Uses the message log rather than a Gauntlet overlay for now: it is enough to make the editor
    /// usable and testable, and it avoids spending a UI layer before the interaction has settled. A
    /// proper HUD (current placeable, category, counts, key hints) is a later step.
    ///
    /// Deliberately NOT a modal inquiry - a modal raised from inside an edit action steals input and
    /// interrupts placement.
    /// </summary>
    public static class EditorHud {
        private static readonly Color Normal = new Color(0.65f, 0.95f, 0.65f);
        private static readonly Color Warning = new Color(1.0f, 0.45f, 0.45f);
        private static readonly Color Info = new Color(0.75f, 0.85f, 1.0f);

        public static void ShowMessage(string text, bool warning = false) {
            InformationManager.DisplayMessage(new InformationMessage(text, warning ? Warning : Normal));
            TraceLogger.Write(nameof(EditorHud), text);
        }

        public static void ShowSelection(string category, string placeable, int index, int total) {
            InformationManager.DisplayMessage(new InformationMessage(
                $"[{category}]  {placeable}   ({index}/{total})", Info));
        }

        public static void ShowCount(int count) {
            InformationManager.DisplayMessage(new InformationMessage($"{count} object(s) placed.", Info));
        }
    }
}
