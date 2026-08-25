using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>Creates and renders persistent notes for places where walkable mesh is missing.</summary>
    internal static class NavMeshRequirementAuthoring {
        public const float DefaultRadius = 4f;

        public static ProjectNavMeshRequirement Create(Scene scene, Vec3 position) {
            return new ProjectNavMeshRequirement {
                Pos = new[] { position.x, position.y, position.z },
                Radius = DefaultRadius,
                Boundary = SampleTerrainBoundary(scene, position, DefaultRadius),
                Created = DateTime.UtcNow,
            };
        }

        public static bool EnsureTerrainBoundary(Scene scene, ProjectNavMeshRequirement requirement) {
            if (requirement.Boundary != null && requirement.Boundary.Length >= 24) return false;
            Vec3 center = Position(requirement);
            if (!center.IsValid) return false;
            requirement.Boundary = SampleTerrainBoundary(scene, center, Math.Max(0.5f, requirement.Radius));
            return true;
        }

        private static float[] SampleTerrainBoundary(Scene scene, Vec3 center, float radius) {
            const int sides = 16;
            var samples = new float[sides * 3];
            for (int i = 0; i < sides; i++) {
                float angle = i * MathF.PI * 2f / sides;
                float x = center.x + MathF.Cos(angle) * radius;
                float y = center.y + MathF.Sin(angle) * radius;
                float z = scene.GetGroundHeightAtPosition(new Vec3(x, y, center.z + 100f));
                if (float.IsNaN(z) || float.IsInfinity(z)) z = center.z;
                int at = i * 3;
                samples[at] = x;
                samples[at + 1] = y;
                samples[at + 2] = z;
            }
            return samples;
        }

        public static Vec3 Position(ProjectNavMeshRequirement requirement) {
            float[] pos = requirement.Pos ?? Array.Empty<float>();
            return pos.Length >= 3 ? new Vec3(pos[0], pos[1], pos[2]) : Vec3.Invalid;
        }

        public static void Render(ProjectNavMeshRequirement requirement, bool selected) {
            Vec3 center = Position(requirement);
            if (!center.IsValid) return;
            float radius = Math.Max(0.5f, requirement.Radius);
            uint color = selected ? 0xFF00FFFFu : 0xFFFFA500u;

            const int sides = 16;
            for (int i = 0; i < sides; i++) {
                Vec3 from = BoundaryPoint(requirement, i, center, radius);
                Vec3 to = BoundaryPoint(requirement, (i + 1) % sides, center, radius);
                from.z += 0.45f;
                to.z += 0.45f;
                NavMeshVisualMarkers.ShowLine(from, to, color, size: selected ? 0.20f : 0.14f);
            }

            center.z += 0.45f;
            NavMeshVisualMarkers.ShowLine(center + new Vec3(-radius, 0f, 0f),
                center + new Vec3(radius, 0f, 0f),
                color, size: selected ? 0.20f : 0.14f);
            NavMeshVisualMarkers.ShowLine(center + new Vec3(0f, -radius, 0f),
                center + new Vec3(0f, radius, 0f),
                color, size: selected ? 0.20f : 0.14f);
            NavMeshVisualMarkers.Show(center, color, size: selected ? 0.52f : 0.38f);
        }

        private static Vec3 BoundaryPoint(
            ProjectNavMeshRequirement requirement, int index, Vec3 center, float radius) {
            float[] boundary = requirement.Boundary ?? Array.Empty<float>();
            int at = index * 3;
            if (boundary.Length >= at + 3)
                return new Vec3(boundary[at], boundary[at + 1], boundary[at + 2]);
            float angle = index * MathF.PI * 2f / 16f;
            return center + new Vec3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
        }
    }
}
