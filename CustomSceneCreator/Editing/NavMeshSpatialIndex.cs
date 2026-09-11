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
        /// <summary>
        /// A face this far above or below the query point counts as a DIFFERENT LEVEL, in metres.
        ///
        /// Roughly a storey. Small enough that a wall walk, a platform deck or a first floor all
        /// register; large enough that ordinary terrain relief under your feet does not.
        /// </summary>
        public const float LevelSeparation = 2.5f;

        /// <summary>
        /// Returns the nearest indexed face centers inside a working radius.
        ///
        /// <para><b>The reserve exists so raised ground is not crowded out.</b> Selection is by 2D
        /// distance, because the overview is answering "what is around here" rather than "what am I
        /// standing on". That means a wall-walk face and the ground face ten metres beneath it are
        /// the same distance away, and with a fixed budget the dense carpet of terrain at your feet
        /// can take every slot - so the deck above you is simply never drawn, and the tool looks like
        /// it is reporting no navmesh up there when the navmesh is fine. Players read that as the
        /// wall being broken.</para>
        ///
        /// <para>With <paramref name="elevatedReserve"/> set, that many slots are held for faces at
        /// least <see cref="LevelSeparation"/> above or below the query point. Unused reserve goes
        /// back to the near set, so a flat scene looks exactly as it did before.</para>
        /// </summary>
        public static void FindNearestWithin(Vec3 position, float radius, int maximum,
                                             List<int> results, int elevatedReserve = 0) {
            results.Clear();
            if (!position.IsValid || radius <= 0f || maximum <= 0 || _built == 0) return;

            float radiusSquared = radius * radius;
            var flat = new List<KeyValuePair<float, int>>();
            var elevated = new List<KeyValuePair<float, int>>();
            for (int i = 0; i < _built; i++) {
                Vec3 candidate = _centers[i];
                if (!candidate.IsValid) continue;
                float squared = (candidate.AsVec2 - position.AsVec2).LengthSquared;
                if (squared > radiusSquared) continue;
                if (elevatedReserve > 0 && MathF.Abs(candidate.z - position.z) >= LevelSeparation)
                    elevated.Add(new KeyValuePair<float, int>(squared, i));
                else
                    flat.Add(new KeyValuePair<float, int>(squared, i));
            }

            flat.Sort((left, right) => left.Key.CompareTo(right.Key));
            elevated.Sort((left, right) => left.Key.CompareTo(right.Key));

            // Take the reserved elevated faces first, then fill the rest from the near set. Whatever
            // the reserve does not use is not wasted - the loop below simply keeps going.
            int reserved = Math.Min(elevatedReserve, elevated.Count);
            for (int i = 0; i < reserved && results.Count < maximum; i++)
                results.Add(elevated[i].Value);
            for (int i = 0; i < flat.Count && results.Count < maximum; i++)
                results.Add(flat[i].Value);
            for (int i = reserved; i < elevated.Count && results.Count < maximum; i++)
                results.Add(elevated[i].Value);
        }

        /// <summary>The indexed center of one face, or false when the index does not hold it.</summary>
        public static bool TryGetCenter(int faceIndex, out Vec3 center) {
            center = Vec3.Invalid;
            if (faceIndex < 0 || faceIndex >= _built) return false;
            center = _centers[faceIndex];
            return center.IsValid;
        }

        /// <summary>
        /// Counts indexed faces within <paramref name="radius"/> (2D) that sit at least
        /// <see cref="LevelSeparation"/> ABOVE the query point, and how high the highest one is.
        ///
        /// <para>This is what lets the inspector answer "is there navmesh on the wall above me"
        /// without the player having to see it. Looking up at a rampart from the ground, the deck is
        /// hidden by the wall itself, so drawing alone can never settle the question.</para>
        /// </summary>
        public static int CountAbove(Vec3 position, float radius, out float highestAbove) {
            highestAbove = 0f;
            if (!position.IsValid || radius <= 0f || _built == 0) return 0;

            float radiusSquared = radius * radius;
            int count = 0;
            for (int i = 0; i < _built; i++) {
                Vec3 candidate = _centers[i];
                if (!candidate.IsValid) continue;
                if ((candidate.AsVec2 - position.AsVec2).LengthSquared > radiusSquared) continue;
                float up = candidate.z - position.z;
                if (up < LevelSeparation) continue;
                count++;
                if (up > highestAbove) highestAbove = up;
            }
            return count;
        }
    }
}
