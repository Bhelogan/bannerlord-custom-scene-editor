using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
using CustomSceneCreator.IO;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Reverses a CSC composite prefab into the addressable prefabs and known editor markers from
    /// which it was exported. Anonymous mesh groups are deliberately not guessed at: there is no
    /// prefab identity with which CSC could save and instantiate them later.
    /// </summary>
    internal static class PrefabBreakApartService {
        internal sealed class Result {
            public readonly List<PlacedEntity> Pieces = new();
            public readonly List<ProjectNavMeshCutout> Cutouts = new();
            public readonly List<ProjectNavMeshRequirement> Requirements = new();
            public readonly List<ProjectNavMeshRamp> Ramps = new();
            public readonly Dictionary<string, PlacedEntity> SourcePieces =
                new(StringComparer.OrdinalIgnoreCase);
            public int NavMeshGroups => Cutouts.Count + Requirements.Count + Ramps.Count;
            public int UnsupportedGroups;
            public bool ApproximatedScale;
        }

        private readonly struct TransformState {
            public TransformState(Vec3 position, Mat3 rotation, Vec3 scale) {
                Position = position;
                Rotation = rotation;
                Scale = scale;
            }

            public Vec3 Position { get; }
            public Mat3 Rotation { get; }
            public Vec3 Scale { get; }
        }

        public static bool IsCandidate(PlacedEntity? owner) {
            if (owner == null) return false;
            Placeable? placeable = PlaceableRegistry.Find(owner.PrefabName);
            return placeable != null &&
                   string.Equals(placeable.Category, PackCatalog.ExportedCategory,
                       StringComparison.OrdinalIgnoreCase) &&
                   PrefabInliner.Find(owner.PrefabName) != null;
        }

        public static Result Extract(PlacedEntity owner) {
            var result = new Result();
            XmlElement? definition = PrefabInliner.Find(owner.PrefabName);
            if (definition == null) return result;

            var root = new TransformState(owner.Position, owner.Rotation, owner.Scale);
            // A script attached to a non-placeable composite container has no unambiguous child
            // owner after expansion. Treat it like anonymous geometry so the caller refuses the
            // operation instead of silently discarding behavior.
            if (DirectChild(definition, "scripts") != null) result.UnsupportedGroups++;
            foreach (XmlElement child in ChildEntities(definition)) {
                Visit(child, root, owner.PrefabName, result);
            }
            foreach (ProjectNavMeshCutout cutout in result.Cutouts) {
                if (cutout.EntityId.Length > 0 &&
                    result.SourcePieces.TryGetValue(cutout.EntityId, out PlacedEntity piece)) {
                    cutout.EntityId = piece.Id;
                    cutout.Prefab = piece.PrefabName;
                } else {
                    // Older exports did not record their owning object. Their cutout is still
                    // restored and bakeable; give it a stable project identity of its own.
                    cutout.EntityId = Guid.NewGuid().ToString("B").ToUpperInvariant();
                }
            }
            return result;
        }

        private static void Visit(XmlElement node, TransformState parent, string compositeName,
                                  Result result) {
            string nodeName = node.GetAttribute("name");
            TransformState world = Compose(parent, ParseLocal(node), result);
            if (TryReadNavMeshMetadata(node, nodeName, world, result)) {
                return;
            }

            string prefab = node.GetAttribute("old_prefab_name");
            if (string.IsNullOrWhiteSpace(prefab)) prefab = node.GetAttribute("prefab");

            // The outer inlining wrapper may carry the same name as the composite itself. It is a
            // container, not a child instance; recurse until an original prefab identity appears.
            if (!string.IsNullOrWhiteSpace(prefab) &&
                !string.Equals(prefab, compositeName, StringComparison.OrdinalIgnoreCase) &&
                CanInstantiate(prefab)) {
                AddPiece(result, CreatePiece(prefab, world, node), node);
                return;
            }

            Placeable? marker = FindMarker(node);
            if (marker != null) {
                PlacedEntity piece = CreatePiece(marker.PrefabName, world, node);
                piece.MarkerIndex = MarkerIndex(nodeName, marker);
                AddPiece(result, piece, node);
                return;
            }

            List<XmlElement> children = ChildEntities(node).ToList();
            if (children.Count > 0) {
                if (DirectChild(node, "scripts") != null) result.UnsupportedGroups++;
                foreach (XmlElement child in children) Visit(child, world, compositeName, result);
                return;
            }

            if (HasDirectGeometry(node)) result.UnsupportedGroups++;
        }

        private static PlacedEntity CreatePiece(string prefab, TransformState world, XmlElement node) {
            return new PlacedEntity {
                Id = Guid.NewGuid().ToString("B").ToUpperInvariant(),
                PrefabName = prefab,
                Position = world.Position,
                Rotation = world.Rotation,
                Scale = world.Scale,
                Scripts = ReadScripts(node),
            };
        }

        private static void AddPiece(Result result, PlacedEntity piece, XmlElement node) {
            result.Pieces.Add(piece);
            string sourceId = node.GetAttribute("csc_source_id");
            if (sourceId.Length > 0) result.SourcePieces[sourceId] = piece;
        }

        private static bool CanInstantiate(string prefab) {
            string spawn = PlaceableRegistry.ResolveSpawnPrefab(prefab);
            return PlaceableRegistry.Find(prefab) != null || GameEntity.PrefabExists(spawn);
        }

        private static Placeable? FindMarker(XmlElement node) {
            HashSet<string> tags = new(StringComparer.OrdinalIgnoreCase);
            XmlElement? tagsNode = DirectChild(node, "tags");
            if (tagsNode != null) {
                foreach (XmlNode tagNode in tagsNode.ChildNodes) {
                    if (tagNode is XmlElement tag && tag.Name == "tag") {
                        string name = tag.GetAttribute("name");
                        if (name.Length > 0) tags.Add(name);
                    }
                }
            }
            if (tags.Count == 0) return null;
            return PlaceableRegistry.All.FirstOrDefault(p => p.ExportTag.Length > 0 && tags.Contains(p.ExportTag));
        }

        private static int MarkerIndex(string nodeName, Placeable marker) {
            if (marker.ExportName.IndexOf("{index}", StringComparison.OrdinalIgnoreCase) < 0) return 0;
            int end = nodeName.Length - 1;
            while (end >= 0 && char.IsDigit(nodeName[end])) end--;
            return int.TryParse(nodeName.Substring(end + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int index) ? index : 0;
        }

        private static List<AttachedScript> ReadScripts(XmlElement node) {
            var scripts = new List<AttachedScript>();
            XmlElement? scriptsNode = DirectChild(node, "scripts");
            if (scriptsNode == null) return scripts;

            foreach (XmlNode scriptNode in scriptsNode.ChildNodes) {
                if (!(scriptNode is XmlElement scriptElement) || scriptElement.Name != "script") continue;
                var attached = new AttachedScript { Name = scriptElement.GetAttribute("name") };
                XmlElement? variables = DirectChild(scriptElement, "variables");
                if (variables != null) {
                    foreach (XmlNode variableNode in variables.ChildNodes) {
                        if (variableNode is XmlElement variable && variable.Name == "variable") {
                            string name = variable.GetAttribute("name");
                            if (name.Length > 0) attached.Variables[name] = variable.GetAttribute("value");
                        }
                    }
                }
                if (attached.Name.Length > 0) scripts.Add(attached);
            }
            return scripts;
        }

        private static TransformState ParseLocal(XmlElement node) {
            XmlElement? transform = DirectChild(node, "transform");
            Vec3 position = ParseVector(transform?.GetAttribute("position"), Vec3.Zero);
            Vec3 euler = ParseVector(transform?.GetAttribute("rotation_euler"), Vec3.Zero);
            Vec3 scale = ParseVector(transform?.GetAttribute("scale"), new Vec3(1f, 1f, 1f));
            Mat3 rotation = Mat3.Identity;
            rotation.ApplyEulerAngles(in euler);
            return new TransformState(position, rotation, scale);
        }

        private static TransformState Compose(TransformState parent, TransformState local, Result result) {
            Vec3 scaledPosition = Multiply(local.Position, parent.Scale);
            Vec3 position = parent.Position + Rotate(parent.Rotation, scaledPosition);

            Vec3 s = Rotate(parent.Rotation, Multiply(local.Rotation.s * local.Scale.x, parent.Scale));
            Vec3 f = Rotate(parent.Rotation, Multiply(local.Rotation.f * local.Scale.y, parent.Scale));
            Vec3 u = Rotate(parent.Rotation, Multiply(local.Rotation.u * local.Scale.z, parent.Scale));
            Vec3 scale = new Vec3(Length(s), Length(f), Length(u));

            // Rotated children beneath non-uniformly scaled parents mathematically contain shear,
            // which PlacedEntity cannot represent. Preserve the axis lengths and nearest orthogonal
            // rotation, and report the approximation in the confirmation/result text.
            if (MathF.Abs(parent.Scale.x - parent.Scale.y) > 0.001f ||
                MathF.Abs(parent.Scale.x - parent.Scale.z) > 0.001f) result.ApproximatedScale = true;

            if (scale.x > 0.0001f) s /= scale.x;
            if (scale.y > 0.0001f) f /= scale.y;
            if (scale.z > 0.0001f) u /= scale.z;
            Mat3 rotation = new Mat3(in s, in f, in u);
            rotation.Orthonormalize();
            return new TransformState(position, rotation, scale);
        }

        private static Vec3 Rotate(Mat3 rotation, Vec3 value) =>
            rotation.s * value.x + rotation.f * value.y + rotation.u * value.z;

        private static Vec3 Multiply(Vec3 a, Vec3 b) => new Vec3(a.x * b.x, a.y * b.y, a.z * b.z);

        private static float Length(Vec3 value) => MathF.Sqrt(Vec3.DotProduct(value, value));

        private static Vec3 ParseVector(string? text, Vec3 fallback) {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            string[] parts = text!.Split(',');
            if (parts.Length != 3) return fallback;
            return TryFloat(parts[0], out float x) && TryFloat(parts[1], out float y) &&
                   TryFloat(parts[2], out float z) ? new Vec3(x, y, z) : fallback;
        }

        private static bool TryFloat(string value, out float parsed) =>
            float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);

        private static bool TryReadNavMeshMetadata(XmlElement node, string name,
                                                   TransformState world, Result result) {
            if (EqualsName(name, "hsr_navcut") || EqualsName(name, "csc_navcut")) {
                Vec3 halfSide = world.Rotation.s * (world.Scale.x * 0.5f);
                Vec3 halfForward = world.Rotation.f * (world.Scale.y * 0.5f);
                Vec3[] corners = {
                    world.Position - halfSide - halfForward,
                    world.Position + halfSide - halfForward,
                    world.Position + halfSide + halfForward,
                    world.Position - halfSide + halfForward,
                };
                result.Cutouts.Add(new ProjectNavMeshCutout {
                    EntityId = node.GetAttribute("csc_owner_id"),
                    Prefab = "Imported prefab cutout",
                    Clearance = AttributeFloat(node, "csc_clearance", 0.75f),
                    MinZ = corners.Min(p => p.z),
                    MaxZ = corners.Max(p => p.z),
                    Corners = Flatten(corners),
                });
                return true;
            }

            if (EqualsName(name, "hsr_navelevated") || EqualsName(name, "csc_navelevated")) {
                Vec3[] points = ReadPointChildren(node, world, result);
                if (points.Length < 4) {
                    result.UnsupportedGroups++;
                    return true;
                }
                result.Ramps.Add(new ProjectNavMeshRamp {
                    Label = "Imported elevated area",
                    Outline = Flatten(points),
                    IsDraft = false,
                });
                return true;
            }

            if (EqualsName(name, "csc_navrequired") || EqualsName(name, "csc_navadded") ||
                EqualsName(name, "hsr_navrequired") || EqualsName(name, "hsr_navadded")) {
                Vec3[] boundary = ReadPointChildren(node, world, result);
                if (boundary.Length < 3) {
                    result.UnsupportedGroups++;
                    return true;
                }
                float radius = boundary.Max(p => DistanceXY(p, world.Position));
                result.Requirements.Add(new ProjectNavMeshRequirement {
                    Label = "Imported navmesh area",
                    Pos = new[] { world.Position.x, world.Position.y, world.Position.z },
                    Radius = MathF.Max(0.5f, radius),
                    Boundary = Flatten(boundary),
                });
                return true;
            }

            // Point children are consumed by their parent group. Any other csc/hsr nav marker is
            // intentionally refused instead of being silently lost during expansion.
            if (name.StartsWith("hsr_nav", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("csc_nav", StringComparison.OrdinalIgnoreCase)) {
                result.UnsupportedGroups++;
                return true;
            }
            return false;
        }

        private static Vec3[] ReadPointChildren(XmlElement node, TransformState parent, Result result) =>
            ChildEntities(node)
                .Where(child => child.GetAttribute("name").EndsWith("navpoint",
                    StringComparison.OrdinalIgnoreCase))
                .Select(child => Compose(parent, ParseLocal(child), result).Position)
                .ToArray();

        private static float[] Flatten(IEnumerable<Vec3> points) => points
            .SelectMany(p => new[] { p.x, p.y, p.z }).ToArray();

        private static float DistanceXY(Vec3 a, Vec3 b) {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private static float AttributeFloat(XmlElement node, string name, float fallback) =>
            TryFloat(node.GetAttribute(name), out float value) ? value : fallback;

        private static bool EqualsName(string actual, string expected) =>
            string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        private static bool HasDirectGeometry(XmlElement node) =>
            DirectChild(node, "components") != null || DirectChild(node, "physics") != null;

        private static IEnumerable<XmlElement> ChildEntities(XmlElement node) {
            XmlElement? children = DirectChild(node, "children");
            if (children == null) yield break;
            foreach (XmlNode child in children.ChildNodes) {
                if (child is XmlElement entity && entity.Name == "game_entity") yield return entity;
            }
        }

        private static XmlElement? DirectChild(XmlElement node, string name) {
            foreach (XmlNode child in node.ChildNodes) {
                if (child is XmlElement element && element.Name == name) return element;
            }
            return null;
        }
    }
}
