using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CustomSceneCreator.Api;
using TaleWorlds.Engine;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Attaches scene scripts to a live entity so they can be previewed while editing.
    ///
    /// <c>GameEntity.CreateAndAddScriptComponent</c> is public, so a fire really can be lit on a
    /// brazier in the editor rather than only appearing after export. Variables are set by reflection
    /// on the component's public fields, which is how the engine's own scene loader gets them there.
    ///
    /// Preview is best-effort ON PURPOSE. Shipped scripts routinely assume a mission type we are not
    /// running - the same thing that made castle scenes crash on load - so a script that refuses to
    /// initialise here is a normal outcome, not a bug. The attachment is recorded in the project and
    /// written on export either way; if the preview does not light up, the exported scene still has
    /// the script on it.
    /// </summary>
    public static class ScriptAttacher {
        /// <summary>
        /// Scripts never attached during preview. These are the ones already known to expect a
        /// running siege or a campaign map, and attaching one is not a cosmetic failure - it is the
        /// class of thing that takes the whole mission down.
        /// </summary>
        private static readonly HashSet<string> PreviewBlocklist = new(StringComparer.OrdinalIgnoreCase) {
            "DeploymentPoint", "SiegeTowerSpawner", "SiegeLadderSpawner", "BatteringRamSpawner",
            "MangonelSpawner", "BallistaSpawner", "MapColorGradeManager", "MapAtmosphereProbe",
            "HideoutBossFightBehavior",
        };

        public static bool CanPreview(string scriptName) => !PreviewBlocklist.Contains(scriptName);

        /// <summary>Attaches everything recorded on a placed entity. Failures are logged, never thrown.</summary>
        public static void ApplyAll(GameEntity entity, PlacedEntity placed) {
            if (entity == null || placed?.Scripts == null) return;
            foreach (AttachedScript script in placed.Scripts) {
                Apply(entity, script);
            }
        }

        public static bool Apply(GameEntity entity, AttachedScript script) {
            if (entity == null || script == null || string.IsNullOrWhiteSpace(script.Name)) return false;

            if (!CanPreview(script.Name)) {
                TraceLogger.Write(nameof(ScriptAttacher),
                    $"Not previewing '{script.Name}' - it expects a mission type this editor does not run. " +
                    "It is still recorded and will be exported.");
                return false;
            }

            try {
                // callScriptCallbacks: false. The callbacks are the part that reaches for mission
                // state, and skipping them gets the component onto the entity - which is what the
                // variable editor and the exporter care about - without inviting an initialisation
                // that has nothing to initialise against.
                entity.CreateAndAddScriptComponent(script.Name, callScriptCallbacks: false);

                ScriptComponentBehavior? component = FindComponent(entity, script.Name);
                if (component == null) {
                    TraceLogger.Write(nameof(ScriptAttacher),
                        $"'{script.Name}' did not attach - the engine does not know that script name.");
                    return false;
                }

                foreach (KeyValuePair<string, string> variable in script.Variables) {
                    SetVariable(component, variable.Key, variable.Value, script.Name);
                }
                return true;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(ScriptAttacher),
                    $"Attaching '{script.Name}' failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static ScriptComponentBehavior? FindComponent(GameEntity entity, string scriptName) {
            try {
                foreach (ScriptComponentBehavior component in entity.GetScriptComponents()) {
                    if (component == null) continue;
                    if (string.Equals(component.GetType().Name, scriptName, StringComparison.OrdinalIgnoreCase)) {
                        return component;
                    }
                }
            } catch { }
            return null;
        }

        /// <summary>
        /// Sets one variable by reflection.
        ///
        /// Engine-side scripts have no managed type at all, so there is nothing to reflect over for
        /// the most-used ones - AnimationPoint among them. Those still export correctly; only the
        /// live preview of their values is unavailable, which is why a miss here is logged quietly
        /// rather than reported as an error.
        /// </summary>
        private static void SetVariable(ScriptComponentBehavior component, string name, string value, string scriptName) {
            try {
                FieldInfo? field = component.GetType().GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (field == null) return;

                object? converted = Convert(field.FieldType, value);
                if (converted != null) field.SetValue(component, converted);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(ScriptAttacher),
                    $"Could not set '{scriptName}.{name}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static object? Convert(Type type, string value) {
            if (type == typeof(string)) return value;
            if (type == typeof(bool)) return value == "true" || value == "1";
            if (type == typeof(float)) {
                return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : (object?)null;
            }
            if (type == typeof(int)) {
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : (object?)null;
            }
            return null;
        }
    }
}
