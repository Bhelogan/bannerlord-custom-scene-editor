using System;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Api;
using Newtonsoft.Json;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>Serialisable form of an attached script.</summary>
    public class ProjectScript {
        public string Name = "";
        public Dictionary<string, string> Variables = new();
    }

    public class ProjectTextureOverride {
        public string Image = "";
        public string Material = "";
        /// <summary>-1 retains the old material-wide override behavior.</summary>
        public int MeshIndex = -1;

        /// <summary>
        /// Value written into every alpha byte of the uploaded image, 0-255, or -1 to keep the
        /// PNG's own alpha.
        ///
        /// <para>What that channel DOES depends on the target material, which is why the panel
        /// reads the material's flags rather than labelling this one way. Where the material has
        /// UsingSpecularAlpha it is the specular level - low for cloth or fur, high for polished
        /// metal. Elsewhere it is usually opacity.</para>
        ///
        /// <para>Saved with the project and written to the export manifest, so a look someone dials
        /// in survives a reload and travels with the scene.</para>
        /// </summary>
        public int Alpha = -1;

        /// <summary>Alpha-test cutoff 0-100, or -1 to leave the material's own value. Pixels below
        /// the cutoff are discarded, which is how cut-out foliage and banners are drawn.</summary>
        public int AlphaCutoff = -1;

        /// <summary>MBAlphaBlendMode as an int, or -1 to leave the material's own mode.</summary>
        public int BlendMode = -1;
    }

    /// <summary>Serialisable form of one placed object.</summary>
    public class ProjectEntity {
        public string Id = "";
        public string Prefab = "";
        public float[] Pos = new float[3];
        /// <summary>Rotation as three basis vectors (forward, up, side), nine floats. Euler angles
        /// would be smaller but lose precision on tilted and snapped objects.</summary>
        public float[] RotF = { 0, 1, 0 };
        public float[] RotU = { 0, 0, 1 };
        public float[] RotS = { 1, 0, 0 };
        /// <summary>Root-local XYZ scale. Missing in older projects, which means 1,1,1.</summary>
        public float[] Scale = { 1, 1, 1 };

        /// <summary>Marker number, for numbered markers. Zero otherwise. See PlacedEntity.</summary>
        public int Index;

        /// <summary>Attached scene scripts, name -> variables. Empty for most objects.</summary>
        public List<ProjectScript> Scripts = new();
        public List<ProjectTextureOverride> TextureOverrides = new();

        public static ProjectEntity From(PlacedEntity e) => new ProjectEntity {
            Scripts = e.Scripts.Select(s => new ProjectScript {
                Name = s.Name,
                Variables = new Dictionary<string, string>(s.Variables),
            }).ToList(),
            TextureOverrides = (e.TextureOverrides ?? new List<TextureOverride>())
                .Select(t => new ProjectTextureOverride { Image = t.Image, Material = t.Material, MeshIndex = t.MeshIndex, Alpha = t.Alpha, AlphaCutoff = t.AlphaCutoff, BlendMode = t.BlendMode }).ToList(),
            Id = e.Id,
            Index = e.MarkerIndex,
            Prefab = e.PrefabName,
            Pos = new[] { e.Position.x, e.Position.y, e.Position.z },
            RotF = new[] { e.Rotation.f.x, e.Rotation.f.y, e.Rotation.f.z },
            RotU = new[] { e.Rotation.u.x, e.Rotation.u.y, e.Rotation.u.z },
            RotS = new[] { e.Rotation.s.x, e.Rotation.s.y, e.Rotation.s.z },
            Scale = new[] { e.Scale.x, e.Scale.y, e.Scale.z },
        };

        public PlacedEntity To() => new PlacedEntity {
            Id = Id,
            MarkerIndex = Index,
            Scripts = (Scripts ?? new List<ProjectScript>())
                .Select(s => new AttachedScript {
                    Name = s.Name,
                    Variables = new Dictionary<string, string>(s.Variables ?? new Dictionary<string, string>()),
                }).ToList(),
            TextureOverrides = (TextureOverrides ?? new List<ProjectTextureOverride>())
                .Select(t => new TextureOverride { Image = t.Image, Material = t.Material, MeshIndex = t.MeshIndex, Alpha = t.Alpha, AlphaCutoff = t.AlphaCutoff, BlendMode = t.BlendMode }).ToList(),
            PrefabName = Prefab,
            Position = new Vec3(Pos[0], Pos[1], Pos[2]),
            Rotation = new Mat3(
                new Vec3(RotS[0], RotS[1], RotS[2]),
                new Vec3(RotF[0], RotF[1], RotF[2]),
                new Vec3(RotU[0], RotU[1], RotU[2])),
            Scale = ReadScale(Scale),
        };

        private static Vec3 ReadScale(float[]? scale) {
            if (scale == null || scale.Length < 3) return new Vec3(1f, 1f, 1f);
            return new Vec3(SafeScale(scale[0]), SafeScale(scale[1]), SafeScale(scale[2]));
        }

        private static float SafeScale(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? 1f : value;
    }

    /// <summary>
    /// An editor-authored request to remove walkable navmesh beneath a placed solid object.
    ///
    /// This is intentionally portable project data, not a copy of Bannerlord's undocumented binary
    /// navmesh representation. Corners are stored in world space so the request can be inspected,
    /// diffed, handed to the Modding Kit, and eventually consumed by a safe mesh writer.
    /// </summary>
    public class ProjectNavMeshCutout {
        public string Id = Guid.NewGuid().ToString("B").ToUpperInvariant();
        public string Label = "Object cutout";
        public string EntityId = "";
        public string Prefab = "";
        public float Clearance = 0.75f;
        public float MinZ;
        public float MaxZ;
        /// <summary>World-space XYZ perimeter corners, flattened to triples.</summary>
        public float[] Corners = Array.Empty<float>();
        /// <summary>
        /// Node-drawn cutouts are height-limited so an upper floor can be removed without also
        /// deleting walkable ground directly beneath it. Object cutouts retain legacy XY-only behavior.
        /// </summary>
        public bool IsFreeform;
        public bool IsDraft;
        /// <summary>Faces found beneath the footprint when it was last sampled in-game.</summary>
        public List<int> FaceIndices = new();
        public List<int> FaceGroups = new();
        public List<int> FaceIslands = new();
        public DateTime Sampled = DateTime.UtcNow;
    }

    /// <summary>
    /// An editor note marking terrain where walkable navmesh needs to be added. This does not
    /// create geometry; it is a persistent authoring requirement for the later mesh pass.
    /// </summary>
    public class ProjectNavMeshRequirement {
        public string Id = Guid.NewGuid().ToString("B").ToUpperInvariant();
        public string Label = "Navmesh needed";
        public float[] Pos = new float[3];
        public float Radius = 4f;
        /// <summary>
        /// Terrain-snapped perimeter samples, flattened XYZ triples. Older projects leave this
        /// empty and remain valid authoring notes, but safe face generation requires the samples.
        /// </summary>
        public float[] Boundary = Array.Empty<float>();
        public int NearestFaceIndex = -1;
        public float NearestFaceDistance = -1f;
        public DateTime Created = DateTime.UtcNow;
    }

    /// <summary>
    /// An elevated walkable strip: stairs, a ramp, a bridge deck, or a wall walk.
    ///
    /// The rails are stored as paired, world-space XYZ samples from the physical surface itself.
    /// They intentionally do not use terrain height: terrain can be many metres below a bridge or
    /// behind a wall, while agents need to walk on the visible surface.
    /// </summary>
    public class ProjectNavMeshRamp {
        public string Id = Guid.NewGuid().ToString("B").ToUpperInvariant();
        public string Label = "Ramp";
        /// <summary>Left rail, bottom to top, flattened XYZ triples.</summary>
        public float[] Left = Array.Empty<float>();
        /// <summary>Right rail, bottom to top, flattened XYZ triples.</summary>
        public float[] Right = Array.Empty<float>();
        /// <summary>
        /// Preferred authoring format: a clockwise perimeter around one raised walkable surface.
        /// Four corners make a ramp/stair/bridge quad. Left/Right remain solely for compatibility
        /// with projects created by the former two-rail tool.
        /// </summary>
        public float[] Outline = Array.Empty<float>();
        /// <summary>
        /// True while the editor is still collecting this strip. Drafts deliberately persist with
        /// the project so changing tools, saving, or reopening a scene never silently loses a
        /// staircase/bridge outline. The baker ignores a draft until it is completed.
        /// </summary>
        public bool IsDraft = false;
        /// <summary>0 = left rail is being drawn; 1 = right rail is being drawn.</summary>
        public int EditingRail = 0;
        public DateTime Created = DateTime.UtcNow;

        [JsonIgnore]
        public int SampleCount => Left != null && Right != null
                                  && Left.Length == Right.Length
                                  ? Left.Length / 3 : 0;
    }

    /// <summary>
    /// A saved layout: which scene, which levels, and everything placed in it.
    /// </summary>
    public class SceneProject {
        public string Name = "";
        public int Version = 1;
        public DateTime Created = DateTime.UtcNow;
        public DateTime Modified = DateTime.UtcNow;

        public string TargetScene = "";
        public string SceneLevels = "";

        public List<ProjectEntity> Entities = new();

        /// <summary>
        /// Solid-object footprints which should become holes in a future baked navmesh. Existing
        /// projects deserialize with an empty list, so adding this is backwards compatible.
        /// </summary>
        public List<ProjectNavMeshCutout> NavMeshCutouts = new();

        /// <summary>Areas which currently have no navmesh but need walkable coverage.</summary>
        public List<ProjectNavMeshRequirement> NavMeshRequirements = new();

        /// <summary>Elevated, two-rail walkable strips such as stairs, ramps, and bridge decks.</summary>
        public List<ProjectNavMeshRamp> NavMeshRamps = new();

        [JsonIgnore]
        public string FileName => ProjectSerializer.SanitizeFileName(Name) + ".json";
    }

    /// <summary>
    /// <see cref="ISceneEditTarget"/> over a <see cref="SceneProject"/> - the standalone app's own
    /// persistence, and the reference implementation of the seam.
    ///
    /// Writes are buffered in memory and flushed on Commit rather than saved per placement: placing
    /// a hedge one bush at a time should not mean a file write per bush.
    /// </summary>
    public class SceneProjectTarget : ISceneEditTarget {
        private readonly SceneProject _project;
        private readonly List<PlacedEntity> _entities;

        public SceneProjectTarget(SceneProject project) {
            _project = project;
            _project.Entities ??= new List<ProjectEntity>();
            _project.NavMeshCutouts ??= new List<ProjectNavMeshCutout>();
            _project.NavMeshRequirements ??= new List<ProjectNavMeshRequirement>();
            _project.NavMeshRamps ??= new List<ProjectNavMeshRamp>();
            _entities = project.Entities.Select(e => e.To()).ToList();
        }

        public SceneProject Project => _project;

        public string DisplayName => _project.Name.Length > 0 ? _project.Name : _project.TargetScene;

        public int Count => _entities.Count;

        public IEnumerable<PlacedEntity> LoadEntities() => _entities.ToList();

        public void OnEntityAdded(PlacedEntity entity) {
            if (string.IsNullOrEmpty(entity.Id)) {
                entity.Id = Guid.NewGuid().ToString("B").ToUpperInvariant();
            }
            _entities.Add(entity);
        }

        public void OnEntityRemoved(PlacedEntity entity) {
            _entities.Remove(entity);
        }

        public void Commit() {
            _project.Entities = _entities.Select(ProjectEntity.From).ToList();
            _project.Modified = DateTime.UtcNow;
            ProjectSerializer.Save(_project);
        }
    }
}
