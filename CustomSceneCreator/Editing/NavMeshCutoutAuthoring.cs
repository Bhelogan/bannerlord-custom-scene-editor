using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Api;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Builds and previews portable navmesh cutout requests. This class only queries the live mesh;
    /// it never calls a native mutation API and never writes navmesh.bin.
    /// </summary>
    internal static class NavMeshCutoutAuthoring {
        public const float DefaultClearance = 0.75f;
        private const float SampleSpacing = 0.75f;

        public static ProjectNavMeshCutout Create(Scene scene, PlacedEntity placed) {
            if (placed.SceneEntity == null) throw new InvalidOperationException("The object has no live scene entity.");

            GameEntity entity = placed.SceneEntity;
            Vec3 localMin;
            Vec3 localMax;
            bool localBounds = true;
            try {
                if (!TryLocalBounds(entity, out localMin, out localMax))
                    throw new InvalidOperationException();
                if (!localMin.IsValid || !localMax.IsValid) throw new InvalidOperationException();
                if (localMax.x - localMin.x < 0.01f || localMax.y - localMin.y < 0.01f)
                    throw new InvalidOperationException();
            } catch {
                localBounds = false;
                localMin = entity.GlobalBoxMin;
                localMax = entity.GlobalBoxMax;
            }

            float clearance = DefaultClearance;
            var corners = new Vec3[4];
            if (localBounds) {
                localMin.x -= clearance;
                localMin.y -= clearance;
                localMax.x += clearance;
                localMax.y += clearance;

                MatrixFrame frame = entity.GetGlobalFrame();
                corners[0] = ToWorld(frame, new Vec3(localMin.x, localMin.y, localMin.z));
                corners[1] = ToWorld(frame, new Vec3(localMax.x, localMin.y, localMin.z));
                corners[2] = ToWorld(frame, new Vec3(localMax.x, localMax.y, localMin.z));
                corners[3] = ToWorld(frame, new Vec3(localMin.x, localMax.y, localMin.z));
            } else {
                localMin.x -= clearance;
                localMin.y -= clearance;
                localMax.x += clearance;
                localMax.y += clearance;
                corners[0] = new Vec3(localMin.x, localMin.y, localMin.z);
                corners[1] = new Vec3(localMax.x, localMin.y, localMin.z);
                corners[2] = new Vec3(localMax.x, localMax.y, localMin.z);
                corners[3] = new Vec3(localMin.x, localMax.y, localMin.z);
            }

            float minZ = Math.Min(corners.Min(c => c.z), entity.GlobalBoxMin.z);
            float maxZ = Math.Max(corners.Max(c => c.z), entity.GlobalBoxMax.z);
            var cutout = new ProjectNavMeshCutout {
                EntityId = placed.Id,
                Prefab = placed.PrefabName,
                Clearance = clearance,
                MinZ = minZ,
                MaxZ = maxZ,
                Corners = Flatten(corners),
                Sampled = DateTime.UtcNow,
            };
            SampleFaces(scene, cutout);
            return cutout;
        }

        /// <summary>
        /// The object's bounds in its own frame, unioned over every child that has physics.
        ///
        /// <b>Why this is not just GetPhysicsMinMax.</b> That call takes an includeChildren flag and
        /// a returnLocal flag, and asking for both gives a wrong answer for a composite whose
        /// children are ROTATED: the children's boxes are unioned without their own rotations
        /// applied, so several copies of one mesh at different yaws all collapse onto the same
        /// un-rotated box.
        ///
        /// Measured on the large building plot, which is one dirt mesh placed three times at 0, 120
        /// and 240 degrees. The engine reported 11.505 x 7.783 m - exactly one block - where the real
        /// footprint is 11.510 x 10.151. The width matched because the un-rotated copy sets it; the
        /// depth was short by 2.76 m because only the rotated copies extend it. The cutout looked
        /// correct on one side of the mound and left the other side walkable.
        ///
        /// So the union is done here instead: each child's own box is transformed by its frame
        /// relative to the root, all eight corners of it, which is what makes rotation count.
        /// </summary>
        private static bool TryLocalBounds(GameEntity entity, out Vec3 min, out Vec3 max) {
            var low = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            var high = new Vec3(float.MinValue, float.MinValue, float.MinValue);

            MatrixFrame root = entity.GetGlobalFrame();
            root.rotation.Orthonormalize();

            bool any = Accumulate(root, entity, ref low, ref high);
            min = any ? low : Vec3.Invalid;
            max = any ? high : Vec3.Invalid;
            return any;
        }

        /// <summary>
        /// Adds one entity's own physics box - expressed in the root's frame - then recurses into
        /// its children.
        ///
        /// Every child is read with <c>includeChildren: false</c> and placed by its OWN frame, which
        /// is the whole point: asking the engine for a composite's local bounds in one call unions
        /// the children without their rotations, so copies of one mesh at different yaws collapse
        /// onto each other.
        /// </summary>
        private static bool Accumulate(MatrixFrame root, GameEntity node, ref Vec3 min, ref Vec3 max) {
            bool any = false;

            try {
                node.GetPhysicsMinMax(includeChildren: false, out Vec3 nodeMin, out Vec3 nodeMax,
                                      returnLocal: true);

                if (nodeMin.IsValid && nodeMax.IsValid
                    && nodeMax.x - nodeMin.x > 0.001f && nodeMax.y - nodeMin.y > 0.001f) {

                    // Rotation and position only - NO scale.
                    //
                    // GetPhysicsMinMax with returnLocal already returns the box with this node's own
                    // scale baked in, while a MatrixFrame carries scale in the LENGTH of its axis
                    // vectors. Multiplying the one by the other applies scale twice. Measured on the
                    // building plot, whose parts are scaled 2.1: the cutout came out 29.9 x 26.4 m
                    // for an object 11.5 x 10.2 m, and orthonormalising brings it back to about
                    // 12.5 x 13.9 before clearance.
                    //
                    // This assumes a node's own scale is the only one in play. A scaled child of a
                    // scaled parent would still come out small, because the local box carries only
                    // the node's own factor - no prefab in use does that, and getting it wrong in
                    // that direction is worth knowing about rather than silently compensating for.
                    MatrixFrame nodeFrame = node.GetGlobalFrame();
                    nodeFrame.rotation.Orthonormalize();

                    // All eight corners. A rotated box's extent is not the rotation of its min and
                    // max alone - those two points only bound it when the box is axis-aligned.
                    for (int i = 0; i < 8; i++) {
                        var corner = new Vec3(
                            (i & 1) == 0 ? nodeMin.x : nodeMax.x,
                            (i & 2) == 0 ? nodeMin.y : nodeMax.y,
                            (i & 4) == 0 ? nodeMin.z : nodeMax.z);

                        Vec3 inRoot = ToRootLocal(root, ToWorld(nodeFrame, corner));
                        min = new Vec3(Math.Min(min.x, inRoot.x), Math.Min(min.y, inRoot.y),
                                       Math.Min(min.z, inRoot.z));
                        max = new Vec3(Math.Max(max.x, inRoot.x), Math.Max(max.y, inRoot.y),
                                       Math.Max(max.z, inRoot.z));
                    }
                    any = true;
                }
            } catch {
                // A node without a physics body simply contributes nothing.
            }

            foreach (GameEntity child in node.GetChildren()) {
                any |= Accumulate(root, child, ref min, ref max);
            }
            return any;
        }

        /// <summary>
        /// A world point in the root's own frame.
        ///
        /// Projection onto the root's axes rather than a matrix inverse: the rotation is
        /// orthonormal, so the transpose is the inverse and three dot products say the same thing
        /// without needing an inverse helper the engine's Mat3 does not expose.
        /// </summary>
        private static Vec3 ToRootLocal(MatrixFrame root, Vec3 world) {
            Vec3 offset = world - root.origin;
            return new Vec3(
                Vec3.DotProduct(offset, root.rotation.s),
                Vec3.DotProduct(offset, root.rotation.f),
                Vec3.DotProduct(offset, root.rotation.u));
        }

        private static Vec3 ToWorld(MatrixFrame frame, Vec3 local) =>
            frame.origin + frame.rotation.s * local.x + frame.rotation.f * local.y + frame.rotation.u * local.z;

        private static float[] Flatten(IEnumerable<Vec3> corners) =>
            corners.SelectMany(c => new[] { c.x, c.y, c.z }).ToArray();

        public static Vec3[] Corners(ProjectNavMeshCutout cutout) {
            if (cutout.Corners == null || cutout.Corners.Length < 12) return Array.Empty<Vec3>();
            var result = new Vec3[4];
            for (int i = 0; i < 4; i++)
                result[i] = new Vec3(cutout.Corners[i * 3], cutout.Corners[i * 3 + 1], cutout.Corners[i * 3 + 2]);
            return result;
        }

        private static void SampleFaces(Scene scene, ProjectNavMeshCutout cutout) {
            Vec3[] c = Corners(cutout);
            if (c.Length != 4) return;

            float width = (c[1].AsVec2 - c[0].AsVec2).Length;
            float depth = (c[3].AsVec2 - c[0].AsVec2).Length;
            int columns = Math.Max(2, (int)Math.Ceiling(width / SampleSpacing));
            int rows = Math.Max(2, (int)Math.Ceiling(depth / SampleSpacing));
            var faces = new HashSet<int>();
            var groups = new HashSet<int>();
            var islands = new HashSet<int>();

            // Include the boundary and interior. Sampling from above lets roofs report their own
            // navmesh if present; a ground-only building reports the terrain faces underneath it.
            for (int y = 0; y <= rows; y++) {
                float v = y / (float)rows;
                for (int x = 0; x <= columns; x++) {
                    float u = x / (float)columns;
                    Vec3 a = c[0] + (c[1] - c[0]) * u;
                    Vec3 b = c[3] + (c[2] - c[3]) * u;
                    Vec3 point = a + (b - a) * v;
                    // Probe just above the object's base, not above its roof. Starting above a tall
                    // barn and using the normal 25 m cursor limit missed perfectly valid ground mesh.
                    // A 50 m downward allowance also covers deep foundations and uneven terrain.
                    point.z = cutout.MinZ + 3f;
                    NavMeshProbe probe = NavMeshDiagnostics.Probe(scene, point, verticalProbeLimit: 50f);
                    if (!probe.IsValid) continue;
                    faces.Add(probe.Face.FaceIndex);
                    groups.Add(probe.Face.FaceGroupIndex);
                    islands.Add(probe.Face.FaceIslandIndex);
                }
            }

            cutout.FaceIndices = faces.OrderBy(i => i).ToList();
            cutout.FaceGroups = groups.OrderBy(i => i).ToList();
            cutout.FaceIslands = islands.OrderBy(i => i).ToList();
        }

        public static void Render(ProjectNavMeshCutout cutout, bool selected) {
            Vec3[] corners = Corners(cutout);
            if (corners.Length != 4) return;
            uint color = selected ? 0xFF00FFFFu : 0xFF00A5FFu;
            float z = cutout.MinZ + 0.45f;
            for (int i = 0; i < 4; i++) {
                Vec3 from = corners[i];
                Vec3 to = corners[(i + 1) % 4];
                from.z = z;
                to.z = z;
                NavMeshVisualMarkers.ShowLine(from, to, color, spacing: 0.45f, size: 0.24f);
                NavMeshVisualMarkers.Show(from, color, size: 0.38f);
            }
        }
    }
}
