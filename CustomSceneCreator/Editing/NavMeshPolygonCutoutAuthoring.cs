using System;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>Editing and red in-world preview for node-drawn, height-limited cutout polygons.</summary>
    internal static class NavMeshPolygonCutoutAuthoring {
        private const uint SavedColor = 0xFFFF3030u;
        private const uint SelectedColor = 0xFFFFD700u;
        private const uint SelectedPointColor = 0xFFFF8C00u;
        private const uint DraftColor = 0xFFB22222u;

        public static int PointCount(ProjectNavMeshCutout cutout) => cutout?.Corners?.Length / 3 ?? 0;

        public static Vec3 Point(ProjectNavMeshCutout cutout, int index) {
            int at = index * 3;
            return cutout?.Corners != null && at >= 0 && at + 2 < cutout.Corners.Length
                ? new Vec3(cutout.Corners[at], cutout.Corners[at + 1], cutout.Corners[at + 2])
                : Vec3.Invalid;
        }

        public static void AppendPoint(ProjectNavMeshCutout cutout, Vec3 point) {
            float[] old = cutout.Corners ?? Array.Empty<float>();
            var expanded = new float[old.Length + 3];
            Array.Copy(old, expanded, old.Length);
            expanded[old.Length] = point.x;
            expanded[old.Length + 1] = point.y;
            expanded[old.Length + 2] = point.z;
            cutout.Corners = expanded;
            RefreshHeightBand(cutout);
        }

        public static void SetPoint(ProjectNavMeshCutout cutout, int index, Vec3 point) {
            int at = index * 3;
            if (cutout?.Corners == null || at < 0 || at + 2 >= cutout.Corners.Length) return;
            cutout.Corners[at] = point.x;
            cutout.Corners[at + 1] = point.y;
            cutout.Corners[at + 2] = point.z;
            RefreshHeightBand(cutout);
        }

        public static bool RemovePoint(ProjectNavMeshCutout cutout, int index) {
            int at = index * 3;
            if (cutout?.Corners == null || at < 0 || at + 2 >= cutout.Corners.Length) return false;
            var contracted = new float[cutout.Corners.Length - 3];
            if (at > 0) Array.Copy(cutout.Corners, 0, contracted, 0, at);
            int remaining = cutout.Corners.Length - at - 3;
            if (remaining > 0) Array.Copy(cutout.Corners, at + 3, contracted, at, remaining);
            cutout.Corners = contracted;
            RefreshHeightBand(cutout);
            return true;
        }

        public static float DistanceSquaredTo(ProjectNavMeshCutout cutout, Vec3 position) {
            if (cutout == null || !position.IsValid) return float.MaxValue;
            float best = float.MaxValue;
            for (int i = 0; i < PointCount(cutout); i++) {
                float distance = (Point(cutout, i) - position).LengthSquared;
                if (distance < best) best = distance;
            }
            return best;
        }

        public static void Render(ProjectNavMeshCutout cutout, bool selected, int selectedPoint = -1) {
            int count = PointCount(cutout);
            if (count == 0) return;
            uint color = cutout.IsDraft ? DraftColor : selected ? SelectedColor : SavedColor;
            for (int i = 0; i < count; i++) {
                Vec3 point = Point(cutout, i);
                uint pointColor = i == selectedPoint ? SelectedPointColor : color;
                NavMeshVisualMarkers.Show(point + new Vec3(0f, 0f, 0.22f), pointColor,
                    size: i == selectedPoint ? 0.54f : selected ? 0.42f : 0.34f);
                NavMeshVisualMarkers.ShowNumber(point + new Vec3(0f, 0f, 0.55f), i + 1,
                    pointColor, size: i == selectedPoint ? 0.42f : 0.34f);
                if (i > 0) ShowLine(Point(cutout, i - 1), point, color, selected ? 0.22f : 0.16f);
            }
            if (!cutout.IsDraft && count > 2)
                ShowLine(Point(cutout, count - 1), Point(cutout, 0), color, selected ? 0.22f : 0.16f);
        }

        private static void RefreshHeightBand(ProjectNavMeshCutout cutout) {
            int count = PointCount(cutout);
            if (count == 0) { cutout.MinZ = cutout.MaxZ = 0f; return; }
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < count; i++) {
                float z = Point(cutout, i).z;
                min = Math.Min(min, z);
                max = Math.Max(max, z);
            }
            // Faces close to the clicked physical surface belong to this cutout. The band is wide
            // enough for uneven floors and stair lips, but narrow enough to exclude another storey.
            cutout.MinZ = min - 1.25f;
            cutout.MaxZ = max + 1.25f;
            cutout.Sampled = DateTime.UtcNow;
        }

        private static void ShowLine(Vec3 from, Vec3 to, uint color, float size) =>
            NavMeshVisualMarkers.ShowLine(from + new Vec3(0f, 0f, 0.16f),
                to + new Vec3(0f, 0f, 0.16f), color, size: size);
    }
}
