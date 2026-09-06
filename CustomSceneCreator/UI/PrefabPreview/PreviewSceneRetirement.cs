using System.Collections.Generic;
using TaleWorlds.Engine;

namespace CustomSceneCreator.UI.PrefabPreview {
    /// <summary>
    /// Destroys a finished preview scene a few frames LATE, on purpose.
    ///
    /// <b>Why this exists.</b> Tearing the tableau down does not release its SceneView immediately.
    /// <c>SceneTableau.OnFinalize</c> is:
    ///
    ///     View?.SetEnable(value: false);
    ///     View?.AddClearTask();        // QUEUED, not done now
    ///     _texture?.Release();
    ///     _tableauScene = null;
    ///
    /// <c>AddClearTask</c> hands the SceneView to the engine to be cleared later. Until that task
    /// runs, the view still points at the native Scene. Calling <c>Scene.ClearAll()</c> in the same
    /// managed frame - which the provider's Clear() did, in a finally block - frees the scene out
    /// from under a queued task that is about to touch it.
    ///
    /// <b>What that looked like.</b> Not an exception. Native heap corruption: the game kept running
    /// and then died, silently, with no crash report - measured at fifty-nine seconds after the
    /// asset picker closed, while the player was doing something else entirely. The same defect in
    /// the Homesteads copy of this code produced an AccessViolation inside MissionScreen.UpdateCamera
    /// minutes after its picker closed, with no mod frames in the stack at all, and was wrongly
    /// blamed on a ground-plane entity across three rewrites of unrelated rotation maths. The
    /// teardown order was the constant nobody changed.
    ///
    /// So the scene is parked here and cleared once the engine has certainly finished with it. A
    /// preview scene is a few megabytes held for a handful of frames; the alternative is corrupting
    /// the heap.
    /// </summary>
    internal static class PreviewSceneRetirement {

        /// <summary>
        /// Frames to wait before freeing. The clear task should run on the next render, so this is
        /// mostly insurance - and cheap insurance, because at most one or two scenes are ever in
        /// flight.
        /// </summary>
        private const int DelayFrames = 5;

        private static readonly List<Pending> Queue = new List<Pending>();

        private sealed class Pending {
            public Scene Scene;
            public int FramesLeft;
        }

        /// <summary>Hands over a scene that is no longer rendered. Safe to call with null.</summary>
        public static void Retire(Scene scene) {
            if (scene == null) return;
            lock (Queue) Queue.Add(new Pending { Scene = scene, FramesLeft = DelayFrames });
        }

        /// <summary>
        /// Frees anything whose wait is over. Call once per frame from somewhere that keeps ticking
        /// after the preview's own UI has gone - the editor's mission tick, not the picker's.
        /// </summary>
        public static void Tick() {
            lock (Queue) {
                for (int i = Queue.Count - 1; i >= 0; i--) {
                    Pending pending = Queue[i];
                    if (--pending.FramesLeft > 0) continue;

                    Queue.RemoveAt(i);
                    try { pending.Scene.ClearAll(); } catch { }
                    try { pending.Scene.ManualInvalidate(); } catch { }
                }
            }
        }

        /// <summary>
        /// Frees everything at once, for mission teardown.
        ///
        /// At that point the engine is discarding its render state wholesale, so the queued clear
        /// task is no longer a hazard - and leaving scenes parked in a static list across missions
        /// would be a genuine leak.
        /// </summary>
        public static void FlushAll() {
            lock (Queue) {
                foreach (Pending pending in Queue) {
                    try { pending.Scene.ClearAll(); } catch { }
                    try { pending.Scene.ManualInvalidate(); } catch { }
                }
                Queue.Clear();
            }
        }
    }
}
