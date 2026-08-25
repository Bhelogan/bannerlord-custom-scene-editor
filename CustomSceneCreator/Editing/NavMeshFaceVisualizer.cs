using System;
using System.Collections.Generic;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Reconstructs a face outline using the public "last position on this face" query. Bannerlord
    /// exposes face centers and boundary queries, but no managed vertex-array accessor; a radial
    /// sweep therefore gives us a faithful display polygon without parsing navmesh.bin.
    /// </summary>
    internal static class NavMeshFaceVisualizer {
        private const int RadialSamples = 48;
        private const float RayLength = 250f;
        private static readonly Dictionary<int, Vec3[]> Cache = new();

        public static void Clear() => Cache.Clear();

        public static bool IsCached(int faceIndex) => Cache.ContainsKey(faceIndex);

        public static void Render(Scene scene, int faceIndex, uint color, bool drawSampleNodes) {
            if (scene == null || faceIndex < 0) return;
            if (!Cache.TryGetValue(faceIndex, out Vec3[]? boundary)) {
                // A long fly across a large battle map can touch thousands of faces. This is a
                // display cache, not scene data; bound it rather than retaining the whole map.
                if (Cache.Count >= 512) Cache.Clear();
                boundary = Reconstruct(scene, faceIndex);
                Cache[faceIndex] = boundary;
            }
            if (boundary.Length < 3) return;

            for (int i = 0; i < boundary.Length; i++) {
                Vec3 from = boundary[i];
                Vec3 to = boundary[(i + 1) % boundary.Length];
                NavMeshVisualMarkers.ShowLine(from, to, color, spacing: 0.45f, size: 0.22f,
                    maximumPoints: 16);
                if (drawSampleNodes)
                    NavMeshVisualMarkers.Show(from, color, size: 0.34f);
            }
        }

        private static Vec3[] Reconstruct(Scene scene, int faceIndex) {
            try {
                if (faceIndex >= scene.GetNavMeshFaceCount()) return Array.Empty<Vec3>();
                PathFaceRecord face = scene.GetNavMeshPathFaceRecord(faceIndex);
                if (!face.IsValid()) return Array.Empty<Vec3>();

                Vec3 center = Vec3.Zero;
                scene.GetNavMeshCenterPosition(faceIndex, ref center);
                var points = new List<Vec3>(RadialSamples);
                for (int i = 0; i < RadialSamples; i++) {
                    float angle = i * (MathF.PI * 2f / RadialSamples);
                    // Despite the managed wrapper naming its last parameter "destination", the
                    // native method is get_last_position_on_nav_mesh_face_for_point_and_direction:
                    // it expects a DIRECTION vector, not an absolute world-space endpoint. Passing
                    // center + ray here made every vector share the map's large XY offset and
                    // collapse into the same quadrant, producing the small L-shaped fragments seen
                    // in the first retail visualization test.
                    Vec2 direction = new Vec2(MathF.Cos(angle), MathF.Sin(angle)) * RayLength;
                    Vec2 edge = scene.GetLastPositionOnNavMeshFaceForPointAndDirection(
                        face, center.AsVec2, direction);

                    Vec3 point = new Vec3(edge, center.z + 3f);
                    NavMeshProbe height = NavMeshDiagnostics.Probe(scene, point);
                    point.z = (height.IsValid ? height.NavPosition.z : center.z) + 0.40f;

                    // Adjacent rays can land at the same polygon corner. Suppress those duplicates
                    // so the debug renderer does not spend most of its work drawing zero-length lines.
                    if (points.Count == 0 || points[points.Count - 1].DistanceSquared(point) > 0.0004f)
                        points.Add(point);
                }
                if (points.Count > 1 && points[0].DistanceSquared(points[points.Count - 1]) < 0.0004f)
                    points.RemoveAt(points.Count - 1);
                return Simplify(points, 0.10f).ToArray();
            } catch (Exception ex) {
                TraceLogger.Write(nameof(NavMeshFaceVisualizer),
                    $"Could not reconstruct navmesh face {faceIndex}: {ex.GetType().Name}: {ex.Message}");
                return Array.Empty<Vec3>();
            }
        }

        /// <summary>
        /// Radial boundary queries return many points along each straight polygon edge. Collapse
        /// those collinear samples into corner-like points so the retail overlay resembles the
        /// Toolkit's face outline and can draw the complete affected set with a small entity pool.
        /// These remain approximated corners, not native editable vertices.
        /// </summary>
        private static List<Vec3> Simplify(List<Vec3> source, float tolerance) {
            var points = new List<Vec3>(source);
            float toleranceSquared = tolerance * tolerance;
            bool removed;
            do {
                removed = false;
                if (points.Count <= 3) break;
                for (int i = 0; i < points.Count; i++) {
                    Vec2 previous = points[(i - 1 + points.Count) % points.Count].AsVec2;
                    Vec2 current = points[i].AsVec2;
                    Vec2 next = points[(i + 1) % points.Count].AsVec2;
                    Vec2 incoming = current - previous;
                    Vec2 outgoing = next - current;
                    if (Vec2.DotProduct(incoming, outgoing) <= 0f) continue;
                    if (Vec2.DistanceToLineSegmentSquared(previous, next, current)
                        > toleranceSquared) continue;
                    points.RemoveAt(i);
                    removed = true;
                    break;
                }
            } while (removed);
            return points;
        }
    }
}
