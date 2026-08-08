using System;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Makes everything in an editing session immune to damage.
    ///
    /// Editing is not gameplay. Dropping a wall on yourself, falling off a roof you just built, or
    /// landing badly after flying the camera somewhere high should cost nothing - and because the
    /// editor runs inside a real campaign, an injury here would follow the player back out to a save
    /// they care about.
    ///
    /// Same approach the horse races and sparring matches use: <c>MortalityState.Invulnerable</c> on
    /// the agent and its mount, rather than intercepting blows. Setting a state is one call and
    /// cannot be got subtly wrong; filtering damage means catching every path that produces it, and
    /// missing one means an editor that usually does not hurt you.
    ///
    /// Applied to the whole session rather than only while build mode is on. The dangerous moments -
    /// a fall from a structure, a camera flight ending on the ground - happen just as easily while
    /// walking around inspecting what you built, and there is nothing in an editing scene that
    /// damage would be meaningful for anyway.
    /// </summary>
    public class EditorNoDamageLogic : MissionLogic {
        public override void AfterStart() {
            base.AfterStart();
            // Agents that already exist when this starts - the player, usually.
            foreach (Agent agent in Mission.Agents) MakeInvulnerable(agent);
        }

        public override void OnAgentBuild(Agent agent, Banner banner) {
            base.OnAgentBuild(agent, banner);
            MakeInvulnerable(agent);
        }

        private static void MakeInvulnerable(Agent? agent) {
            if (agent == null) return;
            try {
                agent.SetMortalityState(Agent.MortalityState.Invulnerable);
                // A dead horse dismounts the player mid-edit, so the mount needs it too.
                agent.MountAgent?.SetMortalityState(Agent.MortalityState.Invulnerable);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(EditorNoDamageLogic),
                    $"Could not make an agent invulnerable: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
