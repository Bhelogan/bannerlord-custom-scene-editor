using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using TaleWorlds.MountAndBlade;

namespace CustomSceneCreator {

    /// <summary>
    /// Calls into game methods whose SIGNATURE differs between the Bannerlord versions we support, so one
    /// build runs on both 1.4.x and 1.5.x.
    ///
    /// <para><b>Why this exists.</b> A call compiled against 1.4.x binds to the exact 1.4.x signature. 1.5.x added two
    /// optional parameters to <c>Mission.SpawnAgent</c> (<c>Equipment agentSpawnEquipment = null,
    /// ItemObject formationBannerItem = null</c>). The source still compiles, but the compiled call matches nothing,
    /// so the game throws MissingMethodException the first time a spawn runs: on 1.5.x the player never appeared
    /// in a loaded scene, which also locked out build mode, and walkaround followers and the navmesh test battle
    /// failed the same way. Found by checking the built DLL against the 1.5.2 beta (scratch\binchk).</para>
    ///
    /// <para><b>How.</b> The method is looked up once by name and leading parameters, whatever its full signature is
    /// in the running game, and any extra parameters receive their declared defaults - exactly what the compiler
    /// fills in when the source omits them. Exceptions from the game are rethrown unwrapped, so existing try/catch
    /// blocks behave as if the call were direct.</para>
    ///
    /// <para>When 1.4.x support is dropped, replace these with direct calls again.</para>
    /// </summary>
    internal static class GameCompat {

        private static MethodInfo? _spawnAgent;

        /// <summary><c>mission.SpawnAgent(data, spawnFromAgentVisuals)</c> on any supported game version.</summary>
        internal static Agent SpawnAgent(Mission mission, AgentBuildData agentBuildData, bool spawnFromAgentVisuals = false) {
            _spawnAgent ??= Find(typeof(Mission), nameof(Mission.SpawnAgent), typeof(AgentBuildData), typeof(bool));
            return (Agent)Invoke(_spawnAgent, mission, agentBuildData, spawnFromAgentVisuals)!;
        }

        /// <summary>The public instance method whose leading parameters are exactly <paramref name="leading"/> and whose
        /// remaining parameters (if any) are all optional. Throws if none, so a future change is loud.</summary>
        private static MethodInfo Find(Type type, string name, params Type[] leading) {
            MethodInfo? match = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == name)
                .Select(m => (Method: m, Params: m.GetParameters()))
                .Where(x => x.Params.Length >= leading.Length
                            && leading.Select((t, i) => x.Params[i].ParameterType == t).All(ok => ok)
                            && x.Params.Skip(leading.Length).All(p => p.IsOptional))
                .OrderBy(x => x.Params.Length)
                .Select(x => x.Method)
                .FirstOrDefault();
            return match ?? throw new MissingMethodException(type.FullName, name);
        }

        private static object? Invoke(MethodInfo method, object target, params object?[] given) {
            ParameterInfo[] parameters = method.GetParameters();
            var args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                args[i] = i < given.Length ? given[i] : parameters[i].DefaultValue is DBNull ? null : parameters[i].DefaultValue;
            try {
                return method.Invoke(target, args);
            } catch (TargetInvocationException ex) when (ex.InnerException != null) {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;   // unreachable
            }
        }
    }
}
