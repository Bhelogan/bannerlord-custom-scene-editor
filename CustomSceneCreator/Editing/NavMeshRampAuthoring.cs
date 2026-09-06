using System;
using System.Collections.Generic;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Persistence and preview helpers for elevated navmesh strips.
    ///
    /// A rail point is the actual physics hit under the editor cursor. Unlike a required-ground
    /// note it must never be projected down onto terrain: the whole point is to retain the height of
    /// a bridge deck, ramp, stair tread, or wall walk.
    /// </summary>
    internal static class NavMeshRampAuthoring {
        private const uint SavedColor = 0xFF00B7D9u;
        private const uint SelectedColor = 0xFFFFD700u;
        private const uint SelectedPointColor = 0xFF1E90FFu;
        private const uint DraftColor = 0xFFFF8C00u;
        private const uint NumberColor = 0xFFFF00FFu;
        private const uint SelectedNumberColor = 0xFFFFA500u;

        public static bool IsUsable(ProjectNavMeshRamp ramp) =>
            ramp != null && ramp.Left != null && ramp.Right != null
            && ramp.Left.Length == ramp.Right.Length && ramp.Left.Length >= 6
            && ramp.Left.Length % 3 == 0;

        public static bool HasOutline(ProjectNavMeshRamp ramp) =>
            ramp?.Outline != null && ramp.Outline.Length > 0 && ramp.Outline.Length % 3 == 0;

        /// <summary>A closed elevated surface needs at least four perimeter corners.</summary>
        public static bool IsOutlineUsable(ProjectNavMeshRamp ramp) =>
            HasOutline(ramp) && PointCount(ramp.Outline) >= 4;

        public static ProjectNavMeshRamp Create(IList<Vec3> left, IList<Vec3> right) {
            if (left == null || right == null || left.Count != right.Count || left.Count < 2) {
                throw new ArgumentException("A ramp needs at least two paired rail samples.");
            }

            return new ProjectNavMeshRamp {
                Left = Flatten(left),
                Right = Flatten(right),
                Created = DateTime.UtcNow,
            };
        }

        public static Vec3 Point(float[] rail, int index) {
            int at = index * 3;
            return rail != null && rail.Length >= at + 3
                ? new Vec3(rail[at], rail[at + 1], rail[at + 2])
                : Vec3.Invalid;
        }

        public static int PointCount(float[] rail) => rail?.Length / 3 ?? 0;

        public static void AppendPoint(ProjectNavMeshRamp ramp, bool left, Vec3 point) {
            float[] rail = left ? ramp.Left ?? Array.Empty<float>() : ramp.Right ?? Array.Empty<float>();
            var expanded = new float[rail.Length + 3];
            Array.Copy(rail, expanded, rail.Length);
            expanded[rail.Length] = point.x;
            expanded[rail.Length + 1] = point.y;
            expanded[rail.Length + 2] = point.z;
            if (left) ramp.Left = expanded; else ramp.Right = expanded;
        }

        public static void AppendOutlinePoint(ProjectNavMeshRamp ramp, Vec3 point) {
            float[] outline = ramp.Outline ?? Array.Empty<float>();
            var expanded = new float[outline.Length + 3];
            Array.Copy(outline, expanded, outline.Length);
            expanded[outline.Length] = point.x;
            expanded[outline.Length + 1] = point.y;
            expanded[outline.Length + 2] = point.z;
            ramp.Outline = expanded;
        }

        public static void SetOutlinePoint(ProjectNavMeshRamp ramp, int index, Vec3 point) {
            SetPointOnRail(ramp?.Outline, index, point);
        }

        public static void SetPoint(ProjectNavMeshRamp ramp, bool left, int index, Vec3 point) {
            SetPointOnRail(left ? ramp.Left : ramp.Right, index, point);
        }

        private static void SetPointOnRail(float[] rail, int index, Vec3 point) {
            int at = index * 3;
            if (rail == null || at < 0 || at + 2 >= rail.Length) return;
            rail[at] = point.x; rail[at + 1] = point.y; rail[at + 2] = point.z;
        }

        /// <summary>Removes one explicitly selected rail sample without disturbing the rest of a strip.</summary>
        public static bool RemovePoint(ProjectNavMeshRamp ramp, bool left, int index) {
            if (ramp == null) return false;
            float[] rail = left ? ramp.Left : ramp.Right;
            int at = index * 3;
            if (rail == null || at < 0 || at + 2 >= rail.Length) return false;

            var contracted = new float[rail.Length - 3];
            if (at > 0) Array.Copy(rail, 0, contracted, 0, at);
            int remaining = rail.Length - at - 3;
            if (remaining > 0) Array.Copy(rail, at + 3, contracted, at, remaining);
            if (left) ramp.Left = contracted; else ramp.Right = contracted;
            return true;
        }

        public static bool RemoveOutlinePoint(ProjectNavMeshRamp ramp, int index) {
            if (ramp == null) return false;
            float[] outline = ramp.Outline;
            int at = index * 3;
            if (outline == null || at < 0 || at + 2 >= outline.Length) return false;
            var contracted = new float[outline.Length - 3];
            if (at > 0) Array.Copy(outline, 0, contracted, 0, at);
            int remaining = outline.Length - at - 3;
            if (remaining > 0) Array.Copy(outline, at + 3, contracted, at, remaining);
            ramp.Outline = contracted;
            return true;
        }

        public static void Render(ProjectNavMeshRamp ramp, bool selected,
                                  bool selectedLeft = false, int selectedPoint = -1) {
            if (ramp == null) return;
            uint color = ramp.IsDraft ? DraftColor : (selected ? SelectedColor : SavedColor);
            if (HasOutline(ramp)) {
                RenderOutline(ramp.Outline, color, selectedPoint, selected, !ramp.IsDraft);
                return;
            }
            RenderRail(ramp.Left, color, selected && selectedLeft ? selectedPoint : -1, selected);
            RenderRail(ramp.Right, color, selected && !selectedLeft ? selectedPoint : -1, selected);

            int paired = Math.Min(PointCount(ramp.Left), PointCount(ramp.Right));
            for (int i = 0; i < paired; i++)
                ShowRaisedLine(Point(ramp.Left, i), Point(ramp.Right, i), color,
                    selected ? 0.22f : 0.15f);
        }

        public static float DistanceSquaredTo(ProjectNavMeshRamp ramp, Vec3 position) {
            if (ramp == null || !position.IsValid) return float.MaxValue;
            float best = float.MaxValue;
            if (HasOutline(ramp)) {
                for (int i = 0; i < PointCount(ramp.Outline); i++) {
                    float distance = (Point(ramp.Outline, i) - position).LengthSquared;
                    if (distance < best) best = distance;
                }
                return best;
            }
            foreach (float[] rail in new[] { ramp.Left, ramp.Right })
                for (int i = 0; i < PointCount(rail); i++) {
                    float distance = (Point(rail, i) - position).LengthSquared;
                    if (distance < best) best = distance;
                }
            return best;
        }

        private static void RenderRail(float[] rail, uint color, int selectedPoint, bool selectedRamp) {
            int count = PointCount(rail);
            for (int i = 0; i < count; i++) {
                Vec3 point = Point(rail, i);
                // The active strip is gold, so a selected dot needs its own contrasting colour.
                // Bright blue also remains legible over the cyan saved-strip preview.
                uint pointColor = i == selectedPoint ? SelectedPointColor : color;
                NavMeshVisualMarkers.Show(point + new Vec3(0f, 0f, 0.22f), pointColor,
                    size: i == selectedPoint ? 0.54f : selectedRamp ? 0.42f : 0.32f);
                if (i > 0) ShowRaisedLine(Point(rail, i - 1), point, color,
                    selectedRamp ? 0.22f : 0.15f);
            }
        }

        private static void RenderOutline(float[] outline, uint color, int selectedPoint, bool selected, bool closed) {
            int count = PointCount(outline);
            for (int i = 0; i < count; i++) {
                Vec3 point = Point(outline, i);
                NavMeshVisualMarkers.Show(point + new Vec3(0f, 0f, 0.22f),
                    i == selectedPoint ? SelectedPointColor : color,
                    size: i == selectedPoint ? 0.54f : selected ? 0.42f : 0.32f);
                // The elevated-area list uses this same one-based perimeter order. A small
                // floor-flat number makes it possible to identify a physical corner while
                // authoring or reviewing a complicated stair/wall-walk outline.
                // Keep the index clear of the thick marker/edge geometry without making it look
                // detached from its corner on elevated floors and stairs.
                NavMeshVisualMarkers.ShowNumber(point + new Vec3(0f, 0f, 0.55f), i + 1,
                    i == selectedPoint ? SelectedNumberColor : NumberColor,
                    size: i == selectedPoint ? 0.42f : selected ? 0.37f : 0.33f);
                if (i > 0) ShowRaisedLine(Point(outline, i - 1), point, color, selected ? 0.22f : 0.15f);
            }
            if (closed && count > 2)
                ShowRaisedLine(Point(outline, count - 1), Point(outline, 0), color, selected ? 0.22f : 0.15f);
        }

        private static float[] Flatten(IList<Vec3> points) {
            var result = new float[points.Count * 3];
            for (int i = 0; i < points.Count; i++) {
                result[i * 3] = points[i].x;
                result[i * 3 + 1] = points[i].y;
                result[i * 3 + 2] = points[i].z;
            }
            return result;
        }

        private static void ShowRaisedLine(Vec3 from, Vec3 to, uint color, float size) {
            if (!from.IsValid || !to.IsValid) return;
            NavMeshVisualMarkers.ShowLine(from + new Vec3(0f, 0f, 0.16f),
                to + new Vec3(0f, 0f, 0.16f), color, size);
        }
    }
}
