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

        /// <summary>Marker number, for numbered markers. Zero otherwise. See PlacedEntity.</summary>
        public int Index;

        /// <summary>Attached scene scripts, name -> variables. Empty for most objects.</summary>
        public List<ProjectScript> Scripts = new();

        public static ProjectEntity From(PlacedEntity e) => new ProjectEntity {
            Scripts = e.Scripts.Select(s => new ProjectScript {
                Name = s.Name,
                Variables = new Dictionary<string, string>(s.Variables),
            }).ToList(),
            Id = e.Id,
            Index = e.MarkerIndex,
            Prefab = e.PrefabName,
            Pos = new[] { e.Position.x, e.Position.y, e.Position.z },
            RotF = new[] { e.Rotation.f.x, e.Rotation.f.y, e.Rotation.f.z },
            RotU = new[] { e.Rotation.u.x, e.Rotation.u.y, e.Rotation.u.z },
            RotS = new[] { e.Rotation.s.x, e.Rotation.s.y, e.Rotation.s.z },
        };

        public PlacedEntity To() => new PlacedEntity {
            Id = Id,
            MarkerIndex = Index,
            Scripts = (Scripts ?? new List<ProjectScript>())
                .Select(s => new AttachedScript {
                    Name = s.Name,
                    Variables = new Dictionary<string, string>(s.Variables ?? new Dictionary<string, string>()),
                }).ToList(),
            PrefabName = Prefab,
            Position = new Vec3(Pos[0], Pos[1], Pos[2]),
            Rotation = new Mat3(
                new Vec3(RotS[0], RotS[1], RotS[2]),
                new Vec3(RotF[0], RotF[1], RotF[2]),
                new Vec3(RotU[0], RotU[1], RotU[2])),
        };
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
