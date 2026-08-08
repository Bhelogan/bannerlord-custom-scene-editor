using System;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Puts the player's weapons away while an edit mode is active, and gives them back afterwards.
    ///
    /// In the player-attached cameras the left mouse button belongs to the combat system: pressing it
    /// swings whatever is in hand. There is no engine flag that blocks attacking while leaving
    /// movement and looking intact - <c>MissionMainAgentController.IsDisabled</c> stops the whole
    /// control tick, including WASD and mouse-look, which is worse than the problem.
    ///
    /// Sheathing is the honest way to get there: with nothing in hand, clicking to place no longer
    /// swings a sword. It is reversible, it costs one call, and it reads as intentional - the
    /// character visibly puts their weapon away when you start building.
    /// </summary>
    public static class WeaponSheather {
        private static bool _sheathed;

        public static void SetEditing(bool editing) {
            if (editing == _sheathed) return;

            Agent? agent = Agent.Main;
            if (agent == null || !agent.IsActive()) return;

            try {
                if (editing) {
                    agent.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.WithAnimation);
                    agent.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, Agent.WeaponWieldActionType.WithAnimation);
                    _sheathed = true;
                } else {
                    // Deliberately not re-wielding: the game hands weapons back on its own terms, and
                    // forcing a specific slot back would guess wrong for anyone who had switched.
                    _sheathed = false;
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(WeaponSheather), $"Sheathe toggle failed: {ex.Message}");
            }
        }

        /// <summary>Clears state between missions so it does not leak into the next one.</summary>
        public static void Reset() => _sheathed = false;
    }
}
