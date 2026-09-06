using HarmonyLib;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Replaces the native player control tick while CSC owns first/third-person input.
    /// A movement whitelist is robust against remapped and newly added combat/action bindings.
    /// Mouse-look is handled separately by the mission screen and remains available.
    /// </summary>
    [HarmonyPatch(typeof(MissionMainAgentController), "ControlTick")]
    internal static class EditorCombatInputPatch {
        [HarmonyPrefix]
        private static bool Prefix() {
            if (!CombatInputSuppressor.IsEditorInputActive) return true;

            Agent? agent = Agent.Main;
            if (agent == null || !agent.IsActive()) return false;

            Agent.MovementControlFlag movement = Agent.MovementControlFlag.None;
            Vec2 input = Vec2.Zero;

            if (Input.IsKeyDown(InputKey.W)) {
                movement |= Agent.MovementControlFlag.Forward;
                input.y += 1f;
            }
            if (Input.IsKeyDown(InputKey.S)) {
                movement |= Agent.MovementControlFlag.Backward;
                input.y -= 1f;
            }
            if (Input.IsKeyDown(InputKey.A)) {
                movement |= Agent.MovementControlFlag.StrafeLeft;
                input.x -= 1f;
            }
            if (Input.IsKeyDown(InputKey.D)) {
                movement |= Agent.MovementControlFlag.StrafeRight;
                input.x += 1f;
            }

            if (input.LengthSquared > 1f) input.Normalize();

            agent.EventControlFlags = Agent.EventControlFlag.None;
            agent.MovementFlags = movement;
            agent.MovementInputVector = input;
            return false;
        }
    }
}
