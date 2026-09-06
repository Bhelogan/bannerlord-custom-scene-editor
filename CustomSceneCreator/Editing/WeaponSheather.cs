using System;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Puts the player's weapons away while an edit mode is active, and gives them back afterwards.
    ///
    /// The movement-only controller patch prevents combat input. Instant sheathing complements it by
    /// removing held weapons without playing another character animation as editing starts.
    /// </summary>
    public static class WeaponSheather {
        private static bool _sheathed;

        public static void SetEditing(bool editing) {
            if (editing == _sheathed) return;

            Agent? agent = Agent.Main;
            if (agent == null || !agent.IsActive()) return;

            try {
                if (editing) {
                    agent.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.Instant);
                    agent.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, Agent.WeaponWieldActionType.Instant);
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
