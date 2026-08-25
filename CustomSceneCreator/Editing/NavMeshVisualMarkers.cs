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
        private const int MaximumPointMarkers = 256;
        private const int MaximumSegmentMarkers = 512;
        private const string NormalMaterialName = "plain_green";
        private const string WarningMaterialName = "plain_red";
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
            if (_scene == null || pool.Count >= maximum) return null;
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
