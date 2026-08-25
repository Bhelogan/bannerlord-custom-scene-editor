using System;
using System.Collections.Generic;
using CustomSceneCreator.NavMesh;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Shows the last navmesh audit's findings in the world, so a problem can be walked to.
    ///
    /// A list of coordinates in the console tells you a scene is broken but not where to stand to see
    /// it. These are the same findings, drawn where they are: a ring on the ground with a mast above
    /// it, tall enough to be picked out from across the map.
    ///
    /// Findings are held statically because the audit is run from the console, outside any mission,
    /// and has to survive until the editor's next render pass picks them up.
    /// </summary>
    internal static class NavMeshAuditMarkers {
        private const float RingRadius = 2.5f;
        private const int RingSides = 12;
        private const float MastHeight = 6f;

        /// <summary>Findings drawn at once, bounded so the shared marker pool is not exhausted.</summary>
        private const int MaximumMarkedFindings = 24;

        /// <summary>Red for things that break movement.</summary>
        private const uint SeriousColor = 0xFFFF2020u;

        /// <summary>Amber for things worth knowing about.</summary>
        private const uint NoticeColor = 0xFFFFA500u;

        private static readonly List<NavMeshFinding> Findings = new();

        /// <summary>The scene the findings belong to, so another scene's markers are not shown.</summary>
        private static string _scene = "";

        public static bool HasFindings => Findings.Count > 0;

        public static string SceneName => _scene;

        /// <summary>
        /// Takes the findings from an audit.
        ///
        /// Findings with no position - whole-mesh observations like leftover data - are dropped
        /// rather than drawn at the world origin, where they would look like a real problem in a
        /// corner of the map.
        /// </summary>
        public static void Show(NavMeshAuditReport report, string scene) {
            Findings.Clear();
            _scene = scene ?? "";

            // The marker pool is shared with the cutout and face overlays, so a scene with a hundred
            // findings must not consume all of it. Serious ones are taken first: a map covered in
            // markers is no more useful than none, and the ones that break movement come first.
            foreach (NavMeshFinding finding in report.Findings) {
                if (finding.IsSerious) TakeFinding(finding);
            }
            foreach (NavMeshFinding finding in report.Findings) {
                if (!finding.IsSerious) TakeFinding(finding);
            }
        }

        private static void TakeFinding(NavMeshFinding finding) {
            if (Findings.Count >= MaximumMarkedFindings) return;
            if (finding.X == 0.0 && finding.Y == 0.0) return;
            Findings.Add(finding);
        }

        public static void Clear() {
            Findings.Clear();
            _scene = "";
        }

        /// <summary>
        /// Draws the findings. Call between <see cref="NavMeshVisualMarkers.Begin"/> and End.
        /// </summary>
        public static void Render(Scene scene, string currentScene) {
            if (Findings.Count == 0) return;
            if (_scene.Length > 0 && !string.Equals(_scene, currentScene, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            foreach (NavMeshFinding finding in Findings) {
                var center = new Vec3((float)finding.X, (float)finding.Y, (float)finding.Z);

                // The audited Z comes from the navmesh, which can sit under a roof or a slope. Taking
                // the ground height here puts the marker where the user will actually be standing.
                try {
                    float ground = scene.GetGroundHeightAtPosition(new Vec3(center.x, center.y, center.z + 100f));
                    if (!float.IsNaN(ground) && !float.IsInfinity(ground) && ground < 9999f) {
                        center.z = ground;
                    }
                } catch { }

                if (!center.IsValid) continue;
                uint color = finding.IsSerious ? SeriousColor : NoticeColor;

                for (int i = 0; i < RingSides; i++) {
                    Vec3 from = RingPoint(center, i);
                    Vec3 to = RingPoint(center, (i + 1) % RingSides);
                    NavMeshVisualMarkers.ShowLine(from, to, color, spacing: 0.5f, size: 0.16f);
                }

                Vec3 bottom = center;
                bottom.z += 0.2f;
                Vec3 top = center;
                top.z += MastHeight;
                NavMeshVisualMarkers.ShowLine(bottom, top, color, spacing: 0.6f, size: 0.18f);
            }
        }

        private static Vec3 RingPoint(Vec3 center, int index) {
            float angle = index * MathF.PI * 2f / RingSides;
            return new Vec3(
                center.x + MathF.Cos(angle) * RingRadius,
                center.y + MathF.Sin(angle) * RingRadius,
                center.z + 0.3f);
        }
    }
}
