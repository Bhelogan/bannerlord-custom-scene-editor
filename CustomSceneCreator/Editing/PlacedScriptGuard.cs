using System;
using System.Collections.Generic;
using TaleWorlds.Engine;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Strips the scripts off prefabs the editor instantiates, keeping only the ones worth seeing.
    ///
    /// A prefab brings its own script components, and the engine starts them the moment it is
    /// instantiated. Most of those scripts were written for a specific mission and assume that
    /// mission's logic has already run - a destructible prop expects the combat systems, a spawner
    /// expects something to spawn into, a usable place expects agents. In a bare editing mission
    /// several of them fault in native code, which is not a catchable managed exception: the game
    /// simply exits, with no crash report to send. That is what happened to an archery target.
    ///
    /// Stripping them costs nothing in the finished scene. An export writes the prefab by NAME
    /// (&lt;game_entity prefab="archery_target"&gt;), so the real game re-instantiates it with all of
    /// its own scripts intact. What is removed here only ever affects the preview.
    ///
    /// So this is an allowlist rather than a blocklist. Chasing crashing scripts one at a time means
    /// every fix costs another user another crash; keeping only what is known to be safe means an
    /// unknown script is harmless by default. The cost of getting the list slightly wrong is a prop
    /// that sits still in the editor, which is a fair trade against the game closing itself.
    ///
    /// Scripts the user attaches deliberately in Script mode are added after this runs and are never
    /// touched. Those have their own, separate guard - see <see cref="ScriptAttacher"/>.
    /// </summary>
    public static class PlacedScriptGuard {
        /// <summary>
        /// Scripts that are purely visual or aural, need nothing from the mission, and are the
        /// reason to preview at all: a lit brazier, a turning windmill, a swaying rope.
        /// </summary>
        private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) {
            "LightCycle",              // fires and torches - the most visible of all
            "ScenePropNegativeLight",
            "ReflectionCapturer",
            "mesh_bender",             // bends geometry to terrain; purely a mesh operation
            "mesh_seasonal_material",
            "animation_instance",      // animates the mesh itself, not an agent
            "camera_instance",
            "rope_segment",
            "WaveFloater",
            "WindMill",
            "AmbientSoundEmitter",     // harmless, and useful when placing a fountain or a fire
        };

        /// <summary>Prefabs already reported, so cycling the palette does not fill the log.</summary>
        private static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Removes every non-allowlisted script from an entity and its children.
        ///
        /// Called immediately after instantiation, before the entity has been ticked - the point is
        /// to get them off before they can run, so anything that would fault never gets the chance.
        /// </summary>
        public static void Strip(GameEntity entity, string prefabName) {
            if (entity == null) return;

            var removed = new List<string>();
            try {
                StripRecursive(entity, removed);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(PlacedScriptGuard),
                    $"Stripping scripts from '{prefabName}' threw", ex);
            }

            if (removed.Count == 0 || !Reported.Add(prefabName)) return;
            TraceLogger.Write(nameof(PlacedScriptGuard),
                $"'{prefabName}': removed {removed.Count} script(s) for preview - {string.Join(", ", removed)}");
        }

        private static void StripRecursive(GameEntity entity, List<string> removed) {
            if (entity == null) return;

            // Materialised first: removing a component while walking the engine's own collection
            // would invalidate it mid-iteration.
            var components = new List<ScriptComponentBehavior>(entity.GetScriptComponents());

            foreach (ScriptComponentBehavior component in components) {
                if (component == null) continue;

                string name = component.GetType().Name;
                if (Allowed.Contains(name)) continue;

                try {
                    entity.RemoveScriptComponent(component.ScriptComponent.Pointer, 0);
                    removed.Add(name);
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(PlacedScriptGuard),
                        $"Could not remove '{name}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (GameEntity child in entity.GetChildren()) StripRecursive(child, removed);
        }
    }
}
