using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Read-only mission-scene navmesh inspection. Nothing in this type imports, attaches, enables,
    /// disables, or otherwise mutates navigation faces.
    /// </summary>
    internal static class NavMeshDiagnostics {
        public const float InfantryRadius = 0.4f;

        // The engine's distance calculation can differ by a few centimetres on a triangulated
        // straight corridor. Anything beyond this is a real routing detour, even if the separate
        // IsLineToPointClear query claims the line is clear.
        private const float RedirectDetourRatio = 1.03f;

        /// <summary>
        /// Allows a cursor hit on a roof or other prop to find the mesh at the same X/Y below it.
        /// The returned vertical delta is displayed, so a mesh far below the visible surface is
        /// evidence rather than a silently accepted result.
        /// </summary>
        private const float VerticalProbeLimit = 25f;

        public static NavMeshProbe Probe(Scene scene, Vec3 position, float verticalProbeLimit = VerticalProbeLimit,
                                         bool excludeDynamicNavigationMeshes = false) {
            if (scene == null || !position.IsValid)
                return NavMeshProbe.Invalid(position, "No scene position");

            try {
                UIntPtr facePointer = scene.GetNavigationMeshForPosition(
                    in position,
                    out int reportedGroup,
                    verticalProbeLimit,
                    excludeDynamicNavigationMeshes);

                if (facePointer == UIntPtr.Zero)
                    return NavMeshProbe.Invalid(position, "No navmesh at this X/Y");

                PathFaceRecord face = scene.GetPathFaceRecordFromNavMeshFacePointer(facePointer);
                if (!face.IsValid())
                    return NavMeshProbe.Invalid(position, "Engine returned an invalid face record");

                // Supplying the exact face pointer keeps Z validation on the face we just probed.
                WorldPosition world = new WorldPosition(scene, facePointer, position, hasValidZ: false);
                Vec3 navPosition = world.GetNavMeshVec3();

                // The face record is authoritative. Preserve the separately reported group only as
                // a diagnostic if an engine/version mismatch ever makes the values disagree.
                string note = reportedGroup == face.FaceGroupIndex
                    ? ""
                    : $"reported group {reportedGroup}";

                return new NavMeshProbe(position, navPosition, face, facePointer, note);
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(NavMeshDiagnostics), "Cursor probe failed", ex);
                return NavMeshProbe.Invalid(position, $"Probe failed: {ex.GetType().Name}");
            }
        }

        public static NavMeshRoute TestRoute(Scene scene, NavMeshProbe start, NavMeshProbe end) {
            if (scene == null || !start.IsValid || !end.IsValid)
                return NavMeshRoute.Invalid("Both endpoints must be on the navmesh");

            try {
                WorldPosition from = new WorldPosition(
                    scene, start.FacePointer, start.NavPosition, hasValidZ: true);
                WorldPosition to = new WorldPosition(
                    scene, end.FacePointer, end.NavPosition, hasValidZ: true);

                bool connected = scene.DoesPathExistBetweenPositions(from, to);
                float pathDistance = 0f;
                bool measured = connected && scene.GetPathDistanceBetweenPositions(
                    ref from, ref to, InfantryRadius, out pathDistance);
                // Connectivity alone ignores the agent radius. A route is useful to infantry only
                // when the radius-aware distance query also succeeds.
                bool reachable = connected && measured;
                bool direct = reachable && scene.IsLineToPointClear(ref from, ref to, InfantryRadius);

                float straightDistance = (end.NavPosition.AsVec2 - start.NavPosition.AsVec2).Length;
                float detourRatio = measured && straightDistance > 0.01f
                    ? pathDistance / straightDistance
                    : 0f;
                int dynamicOnlySamples = CountDynamicOnlySamples(scene, start.NavPosition, end.NavPosition);
                // IsLineToPointClear is a useful fast diagnostic, but its result can disagree
                // with the radius-aware distance query on loaded baked meshes.  Once we have a
                // measured path, its length is the authoritative answer to whether an agent must
                // take a meaningful detour; do not turn a line-query disagreement into a false
                // "must go around" verdict.
                bool requiresRedirect = measured && detourRatio > RedirectDetourRatio;
                TraceLogger.Write(nameof(NavMeshDiagnostics),
                    $"Route A face={start.Face.FaceIndex}/group={start.Face.FaceGroupIndex}/island={start.Face.FaceIslandIndex} " +
                    $"B face={end.Face.FaceIndex}/group={end.Face.FaceGroupIndex}/island={end.Face.FaceIslandIndex}; " +
                    $"connected={connected}, measured={measured}, lineClear={direct}, redirect={requiresRedirect}, " +
                    $"straight={straightDistance:F2}, path={pathDistance:F2}, detour={detourRatio:F3}x, " +
                    $"dynamicOnlySamples={dynamicOnlySamples}.");
                return new NavMeshRoute(
                    reachable,
                    direct,
                    requiresRedirect,
                    measured,
                    straightDistance,
                    pathDistance,
                    detourRatio,
                    dynamicOnlySamples,
                    "");
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(NavMeshDiagnostics), "Route test failed", ex);
                return NavMeshRoute.Invalid($"Route query failed: {ex.GetType().Name}");
            }
        }

        /// <summary>
        /// A baked cutout only removes the static ground mesh. A placed prefab can still contribute
        /// dynamic walkable faces, which reconnect the route through the apparent hole. Sample the
        /// tested straight line with and without dynamic faces so the inspector can name that case.
        /// </summary>
        private static int CountDynamicOnlySamples(Scene scene, Vec3 start, Vec3 end) {
            const float SampleSpacing = 0.75f;
            float distance = (end.AsVec2 - start.AsVec2).Length;
            int steps = Math.Max(1, Math.Min(160, (int)Math.Ceiling(distance / SampleSpacing)));
            int dynamicOnly = 0;

            for (int i = 1; i < steps; i++) {
                float t = (float)i / steps;
                var point = new Vec3(
                    start.x + (end.x - start.x) * t,
                    start.y + (end.y - start.y) * t,
                    start.z + (end.z - start.z) * t);

                NavMeshProbe allFaces = Probe(scene, point);
                if (!allFaces.IsValid) continue;

                NavMeshProbe staticFaces = Probe(scene, point, excludeDynamicNavigationMeshes: true);
                if (!staticFaces.IsValid) dynamicOnly++;
            }

            return dynamicOnly;
        }
    }

    internal sealed class NavMeshProbe {
        public Vec3 RequestedPosition { get; }
        public Vec3 NavPosition { get; }
        public PathFaceRecord Face { get; }
        public UIntPtr FacePointer { get; }
        public string Note { get; }
        public bool IsValid => FacePointer != UIntPtr.Zero && Face.IsValid();
        public float VerticalDelta => IsValid ? RequestedPosition.z - NavPosition.z : 0f;

        public NavMeshProbe(
            Vec3 requestedPosition,
            Vec3 navPosition,
            PathFaceRecord face,
            UIntPtr facePointer,
            string note) {
            RequestedPosition = requestedPosition;
            NavPosition = navPosition;
            Face = face;
            FacePointer = facePointer;
            Note = note ?? "";
        }

        public static NavMeshProbe Invalid(Vec3 position, string note) =>
            new NavMeshProbe(position, Vec3.Invalid, PathFaceRecord.NullFaceRecord, UIntPtr.Zero, note);

        public string FaceSummary => IsValid
            ? $"face {Face.FaceIndex}   group {Face.FaceGroupIndex}   island {Face.FaceIslandIndex}"
            : Note;

        public string PointSummary => IsValid
            ? $"({NavPosition.x:0.0}, {NavPosition.y:0.0}, {NavPosition.z:0.0})"
            : "unset";
    }

    internal sealed class NavMeshRoute {
        public bool Reachable { get; }
        public bool Direct { get; }
        public bool RequiresRedirect { get; }
        public bool HasDistance { get; }
        public float StraightDistance { get; }
        public float PathDistance { get; }
        public float DetourRatio { get; }
        public int DynamicOnlySamples { get; }
        public string Error { get; }

        public NavMeshRoute(
            bool reachable,
            bool direct,
            bool requiresRedirect,
            bool hasDistance,
            float straightDistance,
            float pathDistance,
            float detourRatio,
            int dynamicOnlySamples,
            string error) {
            Reachable = reachable;
            Direct = direct;
            RequiresRedirect = requiresRedirect;
            HasDistance = hasDistance;
            StraightDistance = straightDistance;
            PathDistance = pathDistance;
            DetourRatio = detourRatio;
            DynamicOnlySamples = dynamicOnlySamples;
            Error = error ?? "";
        }

        public static NavMeshRoute Invalid(string error) =>
            new NavMeshRoute(false, false, false, false, 0f, 0f, 0f, 0, error);
    }
}
