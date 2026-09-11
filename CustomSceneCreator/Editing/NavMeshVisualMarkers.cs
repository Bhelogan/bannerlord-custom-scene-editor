using System;
using System.Collections.Generic;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Visible retail-build replacement for MBDebug lines. The debug renderer accepts calls in a
    /// shipping mission but produces no pixels; ordinary editor-cylinder assets are forced through
    /// the same visibility/material lifecycle used by Homesteads navigation markers.
    /// A reusable pool avoids creating/removing hundreds of entities every frame.
    /// </summary>
    internal static class NavMeshVisualMarkers {
        // These are DISPLAY ceilings, not data limits.
        //
        // Nothing the author places is bounded by them: the mark list and the elevated-area records
        // are plain unbounded Lists and the whole set reaches the baker regardless. What ran out was
        // the pool of marker entities, and Acquire simply returns null once it is exhausted - so a
        // point past the ceiling was still saved and still baked, it just stopped being DRAWN. Users
        // reported this as "it recycles after about 41", which is what invisible-but-present looks
        // like from the outside.
        //
        // A mark ring costs MarkSides (16) segments, so 512 was about 32 rings before elevated
        // outlines and cutout boxes took their share of the same pool. Raised well past any plausible
        // authoring session. The pool GROWS LAZILY - Acquire only creates an entity when one is
        // actually needed - so a higher ceiling costs nothing until it is used, and these are only
        // ever created while the navmesh editor mode is open.
        private const int MaximumPointMarkers = 4096;
        private const int MaximumSegmentMarkers = 8192;
        private const string NormalMaterialName = "plain_green";
        private const string WarningMaterialName = "plain_red";
        private static bool _reportedExhaustion;
        private static readonly List<GameEntity> PointPool = new();
        private static readonly List<GameEntity> SegmentPool = new();
        private static Scene? _scene;
        private static int _usedPoints;
        private static int _usedSegments;
        private static Material? _normalMaterial;
        private static Material? _warningMaterial;
        private static bool _materialsResolved;

        public static void Begin(Scene scene) {
            if (!ReferenceEquals(_scene, scene)) Reset();
            _scene = scene;
            _usedPoints = 0;
            _usedSegments = 0;
        }

        public static void End() {
            HideUnused(PointPool, _usedPoints);
            HideUnused(SegmentPool, _usedSegments);
        }

        public static void HideAll() {
            _usedPoints = 0;
            _usedSegments = 0;
            End();
        }

        public static void Reset() {
            foreach (GameEntity marker in PointPool) {
                try { marker.Remove(0); } catch { }
            }
            foreach (GameEntity marker in SegmentPool) {
                try { marker.Remove(0); } catch { }
            }
            PointPool.Clear();
            SegmentPool.Clear();
            _scene = null;
            _usedPoints = 0;
            _usedSegments = 0;
        }

        public static void Show(Vec3 position, uint color, float size = 0.24f) {
            if (_scene == null || !position.IsValid || _usedPoints >= MaximumPointMarkers) return;
            GameEntity? marker = Acquire(PointPool, ref _usedPoints,
                MaximumPointMarkers, "editor_cylinder");
            if (marker == null) return;

            MatrixFrame frame = MatrixFrame.Identity;
            frame.origin = position;
            frame.rotation.ApplyScaleLocal(size);
            try {
                marker.SetGlobalFrame(in frame, true);
                ForceVisibleAndColor(marker, color);
            } catch { }
        }

        public static void ShowLine(Vec3 from, Vec3 to, uint color, float spacing = 0.55f,
                                    float size = 0.22f, int maximumPoints = 96) {
            // Kept in the signature for source compatibility with the original dotted renderer.
            // A single stretched cube now draws each edge, so long faces no longer exhaust the
            // marker pool before the complete affected set is visible.
            _ = spacing;
            _ = maximumPoints;
            Vec3 delta = to - from;
            float length = delta.Length;
            if (_scene == null || !from.IsValid || !to.IsValid || length < 0.001f
                || _usedSegments >= MaximumSegmentMarkers) return;

            GameEntity? marker = Acquire(SegmentPool, ref _usedSegments,
                MaximumSegmentMarkers, "editor_cube");
            if (marker == null) return;

            Vec3 along = delta / length;
            Vec3 reference = MathF.Abs(along.z) > 0.9f ? Vec3.Forward : Vec3.Up;
            Vec3 across = Vec3.CrossProduct(reference, along).NormalizedCopy();
            Vec3 normal = Vec3.CrossProduct(along, across).NormalizedCopy();

            MatrixFrame frame = MatrixFrame.Identity;
            frame.origin = from + delta * 0.5f;
            frame.rotation.s = along * length;
            frame.rotation.f = across * size;
            frame.rotation.u = normal * size;
            try {
                marker.SetGlobalFrame(in frame, true);
                ForceVisibleAndColor(marker, color);
            } catch { }
        }

        private static GameEntity? Acquire(List<GameEntity> pool, ref int used,
                                           int maximum, string prefab) {
            if (used < pool.Count) return pool[used++];
            if (_scene == null) return null;
            if (pool.Count >= maximum) {
                // Once per run. Silent truncation is what made the old ceiling look like data loss;
                // if it is ever hit again the log should say so plainly.
                if (!_reportedExhaustion) {
                    _reportedExhaustion = true;
                    TraceLogger.Write(nameof(NavMeshVisualMarkers),
                        $"Marker pool exhausted at {maximum} entities - further markers are not DRAWN "
                      + "this frame. Nothing placed is lost: marks and elevated areas are stored in "
                      + "full and bake in full. Raise MaximumPointMarkers/MaximumSegmentMarkers if a "
                      + "scene genuinely needs this many visible at once.");
                }
                return null;
            }
            try {
                MatrixFrame frame = MatrixFrame.Identity;
                GameEntity marker = GameEntity.Instantiate(_scene, prefab, frame);
                pool.Add(marker);
                used++;
                return marker;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(NavMeshVisualMarkers),
                    $"Could not instantiate {prefab}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Draws a compact, floor-flat seven-segment number above an authored navmesh point.
        /// Debug text is deliberately not used: it produces no visible pixels in retail missions.
        /// The glyph uses the same ordinary marker meshes as the rest of the editor overlay.
        /// </summary>
        public static void ShowNumber(Vec3 position, int number, uint color, float size = 0.18f) {
            if (_scene == null || !position.IsValid || number < 0) return;

            // Keep the label in the world X/Y plane rather than billboarded at the camera.  A
            // billboard becomes nearly edge-on in the normal RTS/third-person editor views; a
            // floor-flat label remains legible when looking down at the authored surface.
            Vec3 right = new Vec3(1f, 0f, 0f);
            Vec3 up = new Vec3(0f, 1f, 0f);

            string text = number.ToString();
            float advance = size * 1.24f;
            Vec3 start = position - right * (advance * (text.Length - 1) * 0.5f);
            for (int digitIndex = 0; digitIndex < text.Length; digitIndex++) {
                int digit = text[digitIndex] - '0';
                if (digit < 0 || digit > 9) continue;
                DrawDigit(start + right * (advance * digitIndex), right, up, digit, color, size);
            }
        }

        private static void DrawDigit(Vec3 centre, Vec3 right, Vec3 up, int digit, uint color, float size) {
            // top, upper-right, lower-right, bottom, lower-left, upper-left, middle
            string segments = digit switch {
                0 => "012345", 1 => "12", 2 => "01346", 3 => "01236", 4 => "1256",
                5 => "02356", 6 => "023456", 7 => "012", 8 => "0123456", 9 => "012356",
                _ => ""
            };
            float halfWidth = size * 0.46f;
            float top = size * 0.70f;
            float mid = 0f;
            float bottom = -top;
            float sideInset = size * 0.08f;
            float lineSize = MathF.Max(0.035f, size * 0.18f);

            foreach (char segment in segments) {
                (float x1, float y1, float x2, float y2) = segment switch {
                    '0' => (-halfWidth, top, halfWidth, top),
                    '1' => (halfWidth, top - sideInset, halfWidth, mid + sideInset),
                    '2' => (halfWidth, mid - sideInset, halfWidth, bottom + sideInset),
                    '3' => (-halfWidth, bottom, halfWidth, bottom),
                    '4' => (-halfWidth, mid - sideInset, -halfWidth, bottom + sideInset),
                    '5' => (-halfWidth, top - sideInset, -halfWidth, mid + sideInset),
                    '6' => (-halfWidth, mid, halfWidth, mid),
                    _ => (0f, 0f, 0f, 0f)
                };
                ShowLine(centre + right * x1 + up * y1,
                    centre + right * x2 + up * y2, color, size: lineSize);
            }
        }

        private static void HideUnused(List<GameEntity> pool, int used) {
            for (int i = used; i < pool.Count; i++) {
                try {
                    foreach (GameEntity child in pool[i].GetEntityAndChildren())
                        child.SetVisibilityExcludeParents(false);
                } catch { }
            }
        }

        private static void ForceVisibleAndColor(GameEntity entity, uint color) {
            ResolveMaterials();
            // Only plain_green and plain_red are confirmed Bannerlord MATERIAL resources. The
            // similarly named blue/yellow resources are textures on current builds; passing their
            // names to Mesh.SetMaterial silently blanks the mesh, which made every navmesh marker
            // disappear even though sampling and entity creation were both succeeding.
            Material? material = color == 0xFFFF00FFu || color == 0xFFFFA500u
                ? _warningMaterial
                : _normalMaterial;
            foreach (GameEntity child in entity.GetEntityAndChildren()) {
                try {
                    child.SetVisibilityExcludeParents(true);
                    child.SetReadyToRender(true);
                    child.SetFactorColor(color);
                    child.SetContourColor(color, alwaysVisible: true);
                    MetaMesh? meta = child.GetMetaMesh(0);
                    if (meta == null || !meta.IsValid) continue;
                    for (int i = 0; i < meta.MeshCount; i++) {
                        Mesh mesh = meta.GetMeshAtIndex(i);
                        if (mesh != null && mesh.IsValid && material != null)
                            mesh.SetMaterial(material);
                    }
                } catch { }
            }
        }

        private static void ResolveMaterials() {
            if (_materialsResolved) return;
            _materialsResolved = true;
            try { _normalMaterial = Material.GetFromResource(NormalMaterialName); } catch { }
            try { _warningMaterial = Material.GetFromResource(WarningMaterialName); } catch { }
            TraceLogger.Write(nameof(NavMeshVisualMarkers),
                $"Marker materials: {NormalMaterialName}={(_normalMaterial != null ? "ok" : "missing")}, " +
                $"{WarningMaterialName}={(_warningMaterial != null ? "ok" : "missing")}.");
        }
    }
}
