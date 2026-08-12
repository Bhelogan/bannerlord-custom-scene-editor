using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.CampaignEntry {
    /// <summary>
    /// Reopens the browser you came from when you leave the editor.
    ///
    /// Trying a few scenes is the normal way to find the one you want, and without this every
    /// attempt costs a walk back through the settlement menu. The browser is a Gauntlet layer rather
    /// than a game state, so it cannot simply be left underneath the mission - the mission screen
    /// replaces the screen it was attached to, and the layer goes with it.
    ///
    /// So it is reopened afterwards instead: the editor asks for it on the way out, and the campaign
    /// tick performs it once the mission is really gone and there is a screen to attach to again.
    /// Doing it during mission teardown attaches the layer to a screen that is about to be popped.
    /// </summary>
    public static class ReturnToBrowser {
        /// <summary>Which browser to come back to, if any.</summary>
        private enum Target { None, Scenes, Projects }

        private static Target _pending = Target.None;

        /// <summary>Set when the editor is opened FROM a browser - not for a bare console command,
        /// where returning to a window nobody opened would be a surprise.</summary>
        public static void ArmForScenes() => _pending = Target.Scenes;
        public static void ArmForProjects() => _pending = Target.Projects;

        public static void Cancel() => _pending = Target.None;

        /// <summary>
        /// Called every frame from SubModule.OnApplicationTick. Cheap, and does nothing at all in
        /// the normal case.
        ///
        /// Deliberately NOT a campaign tick: that one is gated on the campaign clock, so on a paused
        /// map it never fires and the browser waited until the player started moving.
        /// </summary>
        public static void Tick() {
            if (_pending == Target.None) return;
            if (TaleWorlds.CampaignSystem.Campaign.Current == null) { _pending = Target.None; return; }

            // Wait for the mission to be fully gone. Mission.Current survives the leave request for
            // a few frames while the screen unwinds.
            if (Mission.Current != null) return;

            Target target = _pending;
            _pending = Target.None;

            if (UI.SceneBrowserScreen.IsOpen || UI.ProjectBrowserScreen.IsOpen) return;

            TraceLogger.Write(nameof(ReturnToBrowser), $"Reopening the {target} browser after the editor.");
            if (target == Target.Projects) UI.ProjectBrowserScreen.Open();
            else UI.SceneBrowserScreen.Open();
        }
    }
}
