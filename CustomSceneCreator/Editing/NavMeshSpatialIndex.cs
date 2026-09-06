using System;
using System.Collections.Generic;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Incrementally indexes native face centers so an unmeshed cursor can still locate the nearest
    /// real navmesh. Building over several frames avoids a long single-frame hitch on 20k+ faces.
    /// </summary>
    internal static class NavMeshSpatialIndex {
        private const int FacesPerTick = 750;
        private static Vec3[] _centers = Array.Empty<Vec3>();
        private static int _built;
        private static int _faceCount = -1;

        public static bool IsReady => _faceCount >= 0 && _built >= _faceCount;
        public static int Built => _built;
        public static int Count => Math.Max(0, _faceCount);

        public static void Reset() {
            _centers = Array.Empty<Vec3>();
            _built = 0;
            _faceCount = -1;
        }

        public static void Tick(Scene scene) {
            if (scene == null) return;
            try {
                int count = scene.GetNavMeshFaceCount();
                if (_faceCount != count) {
                    _faceCount = count;
                    _centers = new Vec3[count];
                    _built = 0;
                }

                int end = Math.Min(_faceCount, _built + FacesPerTick);
                for (int i = _built; i < end; i++) {
                    Vec3 center = Vec3.Invalid;
                    scene.GetNavMeshCenterPosition(i, ref center);
                    _centers[i] = center;
                }
                _built = end;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(NavMeshSpatialIndex),
                    $"Face-center indexing failed: {ex.GetType().Name}: {ex.Message}");
                Reset();
            }
        }

        public static bool TryFindNearest(Vec3 position, out int faceIndex, out Vec3 center, out float distance) {
            faceIndex = -1;
            center = Vec3.Invalid;
            distance = float.MaxValue;
            if (!position.IsValid || _built == 0) return false;

            // THREE dimensions, not two.
            //
            // This compared AsVec2, so height counted for nothing - and directly below any elevated
            // surface there is ground at a 2D distance of about zero. An archer placed on the
            // gatehouse wall walk was therefore "moved onto walkable ground" eleven metres straight
            // down, every time, however good the navmesh on the deck above him was.
            //
            // With height counted, a deck face half a metre away wins over ground far below, and the
            // ground is still chosen when the deck genuinely has no navmesh - which is the fallback
            // this method exists to provide.
            float bestSquared = float.MaxValue;
            for (int i = 0; i < _built; i++) {
                Vec3 candidate = _centers[i];
                if (!candidate.IsValid) continue;
                float squared = (candidate - position).LengthSquared;
                if (squared >= bestSquared) continue;
                bestSquared = squared;
                faceIndex = i;
                center = candidate;
            }
            if (faceIndex < 0) return false;
            distance = MathF.Sqrt(bestSquared);
            return true;
        }

        /// <summary>
        /// Returns the nearest indexed face centers inside a working radius. The retail API does
        /// not expose the baked vertex array, so the overview reconstructs only this bounded set
        /// instead of attempting tens of thousands of native boundary sweeps at once.
        /// </summary>
        public static void FindNearestWithin(Vec3 position, float radius, int maximum,
                                             List<int> results) {
            results.Clear();
            if (!position.IsValid || radius <= 0f || maximum <= 0 || _built == 0) return;

            float radiusSquared = radius * radius;
            var candidates = new List<KeyValuePair<float, int>>();
            for (int i = 0; i < _built; i++) {
                Vec3 candidate = _centers[i];
                if (!candidate.IsValid) continue;
                float squared = (candidate.AsVec2 - position.AsVec2).LengthSquared;
                if (squared <= radiusSquared)
                    candidates.Add(new KeyValuePair<float, int>(squared, i));
            }

            candidates.Sort((left, right) => left.Key.CompareTo(right.Key));
            int count = Math.Min(maximum, candidates.Count);
            for (int i = 0; i < count; i++) results.Add(candidates[i].Value);
        }
    }
}
