using System;
using System.Collections.Generic;

namespace CustomSceneCreator.NavMesh {

    /// <summary>
    /// A walkable strip climbing from one height to another - a ramp, a stair, a raised walkway.
    ///
    /// Described as two RAILS rather than a centre line and a width, because a real ramp is rarely a
    /// clean rectangle: a stair may fan out at the bottom, a walkway may narrow where it meets a
    /// tower. Two rails describe both, and they are also exactly what a surface probe produces when
    /// it walks up each side.
    ///
    /// Every rail point carries the height of the surface actually under it - the tread of a step, the
    /// planks of a ramp - not the terrain beneath the whole structure. That is the difference between
    /// a navmesh agents walk up and one buried inside the geometry.
    /// </summary>
    public class NavMeshRampPath {
        public string Label = "Ramp";

        /// <summary>
        /// A closed perimeter sampled from the physical surface.  Unlike the legacy paired rails,
        /// this can describe a wall-walk with several stairs feeding into it.
        /// </summary>
        public NavVertex[] Outline = new NavVertex[0];

        /// <summary>Left rail, bottom to top. Must be the same length as <see cref="Right"/>.</summary>
        public NavVertex[] Left = new NavVertex[0];

        /// <summary>Right rail, bottom to top.</summary>
        public NavVertex[] Right = new NavVertex[0];

        /// <summary>True when there are enough samples to build a surface from.</summary>
        public bool HasOutline => Outline != null && Outline.Length >= 4;

        public bool IsUsable => HasOutline || (Left != null && Right != null
                                && Left.Length == Right.Length && Left.Length >= 2);

        public int Steps => IsUsable ? Left.Length : 0;
    }

    /// <summary>What a ramp turned into.</summary>
    public class RampResult {
        public int RampsBuilt;
        public int RampsSkipped;
        public List<string> Skipped = new List<string>();
        public int FacesAdded;
        public int BottomsJoined;
        public int TopsJoined;
        public int LandingOpenings;

        public string Describe() {
            string text = $"{RampsBuilt} ramp(s), {FacesAdded} faces, "
                          + $"joined at {BottomsJoined} bottom(s) and {TopsJoined} top(s)";
            if (LandingOpenings > 0) text += $", opened {LandingOpenings} landing seam(s)";
            return RampsSkipped > 0 ? text + $"; {RampsSkipped} skipped" : text;
        }
    }

    /// <summary>
    /// Builds walkable surfaces that climb, and joins them to the mesh at both ends.
    ///
    /// This is what <see cref="NavMeshAddition"/> cannot do. That pass fills flat open ground: it
    /// hulls a cluster of marks and bridges it to one nearby edge, which is right for a courtyard and
    /// wrong for a stair. A stair is narrow, it is not convex once it turns, its height changes along
    /// its length, and - the part that actually matters - it has to connect at BOTH ends. A ramp
    /// joined only at the bottom is a dead end, and the pathfinder will not send anyone up a dead end.
    ///
    /// Joining is done by REUSING existing vertices rather than bridging to them. Where the bottom of
    /// the ramp meets an open edge of the existing mesh, the ramp's first two corners become that
    /// edge's own two vertices - so the ramp's first face and the ground's last face share a real
    /// edge, which is what the engine reads as "you can walk from one to the other". Building a
    /// separate bridge triangle that merely touches would leave a seam agents refuse to cross.
    /// </summary>
    public static class NavMeshRamp {

        /// <summary>
        /// How far an open edge may be from a ramp end and still be joined to it, in metres.
        ///
        /// Generous, because the player marks a ramp by eye and the mesh edge is wherever the scene
        /// author left it. Too tight and nothing ever connects; too loose and a ramp reaches across a
        /// gap it should not.
        /// </summary>
        public const double JoinDistance = 3.0;

        // A perimeter is sampled at visible stair corners, while the source mesh's open edge can
        // sit just beyond a wall's collision lip.  Keep the ordinary strip rule tight, but give a
        // whole elevated surface a modestly wider search before refusing a legitimate landing.
        private const double OutlineJoinDistance = 4.5;
        // Reusing an arbitrary short cutout edge can pinch a physically wider stair mouth into a
        // one-agent portal.  The graph remains connected, so structural audits do not catch it,
        // but groups queue at the first tread (the high-castle-wall live test reproduced this).
        // Outlined landings keep their authored width; when the nearby open edge is narrower or
        // much wider, the cutout-bridge path below creates a tapered connector instead of moving
        // the stair corners onto that edge.
        private const double MinimumLandingWidthRetention = 0.80;
        private const double MaximumLandingWidthExpansion = 1.25;

        // Two elevated outlines are allowed to meet only where the author actually traced the
        // same physical lip.  This is deliberately much tighter than a ground-landing search:
        // it joins a stair to its wall-walk (or a tunnel section to the next section) without
        // guessing that two merely nearby pieces should be connected.
        // The saved gatehouse routes show adjacent physical stair/landing lips that differ by up
        // to 1.79 m at their corresponding corners.  This is still an edge-to-edge assertion
        // (both ends must match), not a generic nearby-point link.  Candidate pairs are resolved
        // one-to-one below: one authored boundary edge can only be handed off to one neighbour.
        // Without that ownership rule, a loose comparison can make three faces claim the same
        // seam and produce a non-manifold mesh.
        private const double SharedElevatedEdgeDistance = 2.0;

        // A stair often meets the middle of a long wall-walk edge instead of one of its two
        // corners.  That is normal authoring, not an error: split the deck boundary there and
        // make a short, real connector rather than requiring the author to subdivide every deck
        // by hand.  These limits deliberately remain much tighter than a ground landing.
        // The converted tall defensive wall has a 2.9 m physical landing between the stair's
        // outer lip and the wall-walk boundary.  Keep this far below the cutout bridge search, but
        // large enough to span that real landing without forcing an extra authoring outline.
        private const double ElevatedSpliceDistance = 3.25;
        private const double ElevatedSpliceHeightDifference = 1.25;


        /// <summary>
        /// Builds every ramp, in place.
        ///
        /// Each is independent: one that cannot be built is skipped with a reason and the rest still
        /// go in.
        /// </summary>
        public static RampResult Apply(NavMeshData data, IList<NavMeshRampPath> ramps,
                                       Action<int, int>? progress = null) {
            if (data == null) throw new ArgumentNullException("data");
            if (ramps == null || ramps.Count == 0) {
                throw new NavMeshFormatException("there are no ramps to build");
            }

            foreach (NavEdge edge in data.Edges) {
                if (edge.A != -1 || edge.D != -1 || edge.E != -1 || edge.F != 0) {
                    throw new NavMeshFormatException(
                        "this navmesh uses edge fields this editor does not understand; refusing to edit it");
                }
            }

            NavMeshSummary before = NavMeshValidation.Summarize(data);
            if (!before.IsStructurallySound) {
                throw new NavMeshFormatException(
                    "refusing to edit a navmesh that is already structurally unsound: " + before.Describe());
            }

            var result = new RampResult();
            var pending = new List<NavMeshRampPath>(ramps);
            var failures = new Dictionary<NavMeshRampPath, string>();
            var settled = new HashSet<NavMeshRampPath>();
            Dictionary<NavMeshRampPath, List<int>> sharedEdges = FindSharedOutlineEdges(ramps);
            // A simple stair/deck composite needs its physical stair mouth preserved: otherwise a
            // nearby short terrain edge can squeeze a two-agent landing into a one-agent portal.
            // A large authored network (the fortified gatehouse fixture) is different. Its many
            // overlapping outlines deliberately hand off through short, pre-approved lips; forcing
            // every one of those lips to retain the full source-edge width rejects valid turns and
            // regresses the tunnel/stair network. In that case the one-to-one shared-edge planner is
            // already the stronger safety rule, so retain its proven joining behaviour.
            bool preserveLandingWidth = ramps.Count <= 4;
            progress?.Invoke(0, ramps.Count);

            // An elevated build is often a small network: a stair joins the ground, its landing
            // joins a wall-walk, and another stair joins the far end.  Those pieces can be drawn
            // in any order, so do not permanently reject a landing merely because its neighbour
            // has not been baked yet.  Each pass keeps only successful surfaces, then gives the
            // remaining outlines another chance to reuse their newly-open perimeter edges.
            while (pending.Count > 0) {
                bool madeProgress = false;
                var retry = new List<NavMeshRampPath>();

                foreach (NavMeshRampPath ramp in pending) {
                    // A landing cutout can replace the entire face list, so list-length rollback
                    // is not enough here. Restore the whole snapshot on a failed attempt.
                    NavMeshData beforeRamp = data.Clone();
                    int facesBefore = result.FacesAdded;
                    int bottomsBefore = result.BottomsJoined;
                    int topsBefore = result.TopsJoined;
                    int openingsBefore = result.LandingOpenings;

                    try {
                        int added = Build(data, ramp, result,
                            sharedEdges.TryGetValue(ramp, out List<int> edges) ? edges : null,
                            preserveLandingWidth);
                        NavMeshSummary afterRamp = NavMeshValidation.Summarize(data);
                        if (!afterRamp.IsStructurallySound || afterRamp.NonManifoldEdges > before.NonManifoldEdges) {
                            throw new NavMeshFormatException(
                                "would leave a non-manifold seam (" + DescribeFirstNonManifoldEdge(data) + ")");
                        }
                        result.FacesAdded += added;
                        result.RampsBuilt++;
                        settled.Add(ramp);
                        progress?.Invoke(settled.Count, ramps.Count);
                        failures.Remove(ramp);
                        madeProgress = true;
                    } catch (NavMeshFormatException ex) {
                        Restore(data, beforeRamp);
                        // Build only commits these counters at the end of a successful surface,
                        // but reset them defensively alongside the mesh snapshot.  A retry must
                        // never claim a landing or face from an attempt we just discarded.
                        result.FacesAdded = facesBefore;
                        result.BottomsJoined = bottomsBefore;
                        result.TopsJoined = topsBefore;
                        result.LandingOpenings = openingsBefore;
                        failures[ramp] = ex.Message;
                        retry.Add(ramp);
                    }
                }

                if (!madeProgress) {
                    foreach (NavMeshRampPath ramp in retry) {
                        result.RampsSkipped++;
                        result.Skipped.Add($"'{ramp.Label}': {failures[ramp]}");
                        settled.Add(ramp);
                        progress?.Invoke(settled.Count, ramps.Count);
                    }
                    break;
                }

                pending = retry;
            }

            data.Signature = "NMG9";

            NavMeshSummary after = NavMeshValidation.Summarize(data);
            if (!after.IsStructurallySound) {
                throw new NavMeshFormatException("the ramp produced an unsound navmesh: " + after.Describe());
            }
            if (after.NonManifoldEdges > before.NonManifoldEdges) {
                throw new NavMeshFormatException(
                    $"the ramp left {after.NonManifoldEdges} edges shared by more than two faces");
            }
            if (after.ConnectedFaceComponents > before.ConnectedFaceComponents) {
                throw new NavMeshFormatException(
                    "the elevated surfaces did not connect back to the main navmesh; "
                    + "check the stair/deck hand-off (" + result.Describe() + ", components "
                    + before.ConnectedFaceComponents + "->" + after.ConnectedFaceComponents
                    + (result.Skipped.Count > 0 ? "; " + string.Join("; ", result.Skipped) : "") + ")");
            }
            return result;
        }

        private static int Build(NavMeshData data, NavMeshRampPath ramp, RampResult result,
                                 IList<int>? sharedOutlineEdges, bool preserveLandingWidth) {
            if (!ramp.IsUsable) {
                throw new NavMeshFormatException(
                    "needs at least two samples down each side, and the same number on both");
            }

            if (ramp.HasOutline) return BuildOutline(data, ramp, result, sharedOutlineEdges,
                                                     preserveLandingWidth);

            int steps = ramp.Steps;
            var left = new int[steps];
            var right = new int[steps];

            // The two ends first try to land ON an existing open edge. Where that works the ramp
            // shares real edges with the ground and the join needs no geometry of its own.
            OpenEdges open = OpenEdges.Of(data);
            bool joinedBottom = TryJoin(data, open, ramp.Left[0], ramp.Right[0], out left[0], out right[0]);
            bool joinedTop = TryJoin(data, open, ramp.Left[steps - 1], ramp.Right[steps - 1],
                                     out left[steps - 1], out right[steps - 1]);

            // Most authored stairs begin and end inside an ordinary existing face, rather than at a
            // convenient pre-existing mesh hole. Open a narrow landing mouth beneath the first/last
            // strip segment, then re-use that mouth's real edge. This is deliberately a cutout, not
            // a bridge triangle laid on top of an existing edge: a third face sharing an interior
            // edge is non-manifold and the engine will reject the mesh.
            int landingOpenings = 0;
            if (!joinedBottom || !joinedTop) {
                int openings = OpenLandingSeams(data, ramp, joinedBottom, joinedTop);
                if (openings > 0) {
                    landingOpenings = openings;
                    open = OpenEdges.Of(data);
                    joinedBottom = TryJoin(data, open, ramp.Left[0], ramp.Right[0], out left[0], out right[0]);
                    joinedTop = TryJoin(data, open, ramp.Left[steps - 1], ramp.Right[steps - 1],
                                        out left[steps - 1], out right[steps - 1]);
                }
            }

            // A stair/bridge that does not share a real edge at both landings is not a route. One
            // connected end only produces a decorative dead-end surface: it may look correct in a
            // wireframe, but pathfinding cannot use it to get up the stairs or across the river.
            if (!joinedBottom || !joinedTop) {
                throw new NavMeshFormatException(
                    !joinedBottom && !joinedTop
                        ? "neither landing reaches an open navmesh edge"
                        : "only one landing reaches an open navmesh edge; a ramp must join at both ends");
            }

            Dictionary<long, int> edgeLookup = BuildEdgeLookup(data);

            // Everything not joined becomes a new vertex at the sampled surface height.
            for (int i = 0; i < steps; i++) {
                bool isJoinedEnd = (i == 0 && joinedBottom) || (i == steps - 1 && joinedTop);
                if (isJoinedEnd) continue;

                left[i] = data.Vertices.Count;
                data.Vertices.Add(ramp.Left[i]);
                right[i] = data.Vertices.Count;
                data.Vertices.Add(ramp.Right[i]);
            }

            var points = new Dictionary<int, Point2>(steps * 2);
            for (int i = 0; i < steps; i++) {
                points[left[i]] = new Point2(data.Vertices[left[i]].X, data.Vertices[left[i]].Y);
                points[right[i]] = new Point2(data.Vertices[right[i]].X, data.Vertices[right[i]].Y);
            }

            // Metadata is copied from the face the bottom joins onto, so the new surface belongs to
            // the same level and face group as the ground it leads off.
            NavFace reference = NearestFace(data, open, ramp.Left[0]);
            int[] metadata = (int[])reference.Metadata.Clone();
            byte direction = reference.Direction;

            int added = 0;
            for (int i = 0; i < steps - 1; i++) {
                var quad = new[] { left[i], right[i], right[i + 1], left[i + 1] };

                if (NavMeshGeometry.SignedArea(quad, points) < 0) Array.Reverse(quad);
                if (!NavMeshGeometry.IsAcceptableFace(quad, points)) {
                    double area = Math.Abs(NavMeshGeometry.SignedArea(quad, points));
                    throw new NavMeshFormatException(
                        $"step {i + 1} is too small to be a navmesh face ({area:0.##} m2); "
                        + "widen the ramp or use fewer steps");
                }

                var faceVertices = new uint[4];
                var faceEdges = new uint[4];
                for (int c = 0; c < 4; c++) {
                    faceVertices[c] = (uint)quad[c];
                    faceEdges[c] = (uint)EnsureEdge(data, edgeLookup, quad[c], quad[(c + 1) % 4]);
                }

                data.Faces.Add(new NavFace(faceVertices, faceEdges, (int[])metadata.Clone(), direction));
                added++;
            }
            // Counts are committed only after all faces for this ramp are accepted. A failed ramp
            // restores its mesh snapshot, so its bookkeeping must disappear with it too.
            result.BottomsJoined++;
            result.TopsJoined++;
            result.LandingOpenings += landingOpenings;
            return added;
        }

        /// <summary>
        /// Builds a raised surface from one authored perimeter.  This is intentionally separate
        /// from the two-rail builder above: a wall-walk with two stair entrances is a connected
        /// surface, not one very confused ramp.  The polygon is triangulated at the sampled physical
        /// heights; every low perimeter edge is then considered a possible landing into the base
        /// mesh.  One successful shared landing is sufficient to make the entire elevated network
        /// reachable, and additional stair landings are joined when available.
        /// </summary>
        private static int BuildOutline(NavMeshData data, NavMeshRampPath ramp, RampResult result,
                                        IList<int>? sharedOutlineEdges, bool preserveLandingWidth) {
            NavVertex[] outline = ramp.Outline;
            int count = outline.Length;
            var authored = new Dictionary<int, Point2>(count);
            var loop = new List<int>(count);
            for (int i = 0; i < count; i++) {
                authored[i] = new Point2(outline[i].X, outline[i].Y);
                loop.Add(i);
            }
            if (!NavMeshGeometry.IsSimplePolygon(loop, authored)) {
                throw new NavMeshFormatException(
                    "the elevated perimeter crosses itself; trace only the outside edge of the connected surface");
            }

            List<int> landingEdges = FindLowLandingEdges(outline, out bool hasLowerLanding);

            var indices = new int[count];
            for (int i = 0; i < count; i++) indices[i] = -1;
            int joined = 0;
            int joinedOutlineEdge = -1;
            bool groundJoined = false;
            bool elevatedJoined = false;
            int openings = 0;
            OpenEdges open = OpenEdges.Of(data);
            double lowestOutlineZ = double.MaxValue;
            foreach (NavVertex point in outline) lowestOutlineZ = Math.Min(lowestOutlineZ, point.Z);

            // First look only for another elevated surface that was drawn against this exact
            // perimeter edge.  The first of a pair is still built normally; when its neighbour
            // is built (or retried), this adds a narrow connector between the two open lips.  A
            // climbing outline may have one declared hand-off at its bottom and another at its
            // top, so every non-conflicting declared edge must be considered; stopping after the
            // first hand-off leaves the far end as a disconnected island.  Do
            // not snap the second perimeter onto the first: in a real gatehouse the stair and
            // deck lips are often a metre apart, and snapping can fold a concave roof-walk back
            // across itself.  The connector owns one face on each lip, producing a real graph
            // connection while preserving both authored outlines.
            if (sharedOutlineEdges != null) {
                foreach (int edgeStart in sharedOutlineEdges) {
                    int edgeEnd = (edgeStart + 1) % count;
                    if (indices[edgeStart] >= 0 || indices[edgeEnd] >= 0) continue;
                    bool connected = TryConnectElevatedOutlines(
                        data, open, outline[edgeStart], outline[edgeEnd],
                        out int first, out int second);
                    // When the two authored lips already almost coincide, the connector strip can
                    // be thinner than Bannerlord's minimum face size.  That is not a gap needing
                    // two triangles; it is one physical landing sampled twice.  Reuse the existing
                    // deck edge in that case so the stair and deck share the exact graph edge.
                    // This fallback is reached only for an edge pair pre-approved by
                    // FindSharedOutlineEdges, never for an arbitrary nearby piece of navmesh.
                    double sharedEdgeHeight = (outline[edgeStart].Z + outline[edgeEnd].Z) * 0.5;
                    if (!connected && (!hasLowerLanding || sharedEdgeHeight > lowestOutlineZ + 0.75)) {
                        connected = TryReuseElevatedOutlineEdge(
                            data, open, outline[edgeStart], outline[edgeEnd],
                            out first, out second);
                    }
                    if (connected) {
                        indices[edgeStart] = first;
                        indices[edgeEnd] = second;
                        joined++;
                        if (hasLowerLanding && sharedEdgeHeight <= lowestOutlineZ + 0.75) {
                            groundJoined = true;
                        } else {
                            elevatedJoined = true;
                        }
                        joinedOutlineEdge = edgeStart;
                        open = OpenEdges.Of(data);
                    }
                }
            }

            // Do not infer a mid-edge splice here.  A previous experimental splice could split a
            // deck boundary after another surface had already claimed it, producing a three-face
            // edge in dense gatehouse layouts.  Elevated outlines must currently meet through a
            // declared perimeter lip; the controlled shared-edge pass above handles those joins.
            int landingAttempts = 0;

            foreach (int edgeStart in landingEdges) {
                // One real shared landing makes a single outlined surface reachable.  Trying to
                // attach every locally-low edge to the same large cutout ring can reuse a ring
                // edge and create a non-manifold fan, especially in a gatehouse tunnel.
                if (groundJoined) break;
                // A failed seam opening plans and validates a local cut.  On a large map, trying
                // every decorative corner of a complex building is both slow and no more likely
                // to make a disconnected deck useful.  The lowest two candidate lips cover a
                // normal stair/tunnel while a later retry can use a surface added by its neighbour.
                if (landingAttempts++ >= 2) break;
                int edgeEnd = (edgeStart + 1) % count;
                if (hasLowerLanding
                    && (outline[edgeStart].Z + outline[edgeEnd].Z) * 0.5 > lowestOutlineZ + 1.5) {
                    continue;
                }
                if (indices[edgeStart] >= 0 || indices[edgeEnd] >= 0) continue;
                bool connected = TryJoin(data, open, outline[edgeStart], outline[edgeEnd],
                                         out int first, out int second, OutlineJoinDistance,
                                         preserveLandingWidth: preserveLandingWidth);
                if (!connected) {
                    // Prefer the already-existing boundary of a building cutout.  Cutting a new
                    // seam first can accidentally target a provisional deck face at the same XY,
                    // making the counters say "joined" while the lower stair remains a floating
                    // island.  This bridge accepts only empty cutout space and therefore safely
                    // fails on ordinary live ground, where the seam path below remains correct.
                    NavMeshData beforeBridge = data.Clone();
                    if (TryBridgeAcrossCutout(data, open, outline[edgeStart], outline[edgeEnd])) {
                        open = OpenEdges.Of(data);
                        connected = TryJoin(data, open, outline[edgeStart], outline[edgeEnd],
                                            out first, out second, OutlineJoinDistance,
                                            preserveLandingWidth: preserveLandingWidth);
                        if (connected) {
                            openings++;
                        } else {
                            Restore(data, beforeBridge);
                            open = OpenEdges.Of(data);
                        }
                    }
                }
                if (!connected) {
                    // The first strip above a stair normally sits inside an ordinary face. Open a
                    // small mouth beneath that actual first strip, then reuse its boundary edge.
                    NavMeshData beforeOpening = data.Clone();
                    int before = data.Faces.Count;
                    int previous = (edgeStart - 1 + count) % count;
                    int following = (edgeEnd + 1) % count;
                    if (hasLowerLanding && TryOpenLanding(data, outline[edgeStart], outline[edgeEnd],
                                       outline[following], outline[previous])) {
                        open = OpenEdges.Of(data);
                        connected = TryJoin(data, open, outline[edgeStart], outline[edgeEnd],
                                            out first, out second, OutlineJoinDistance,
                                            preserveLandingWidth: preserveLandingWidth);
                        if (connected) {
                            if (data.Faces.Count != before) openings++;
                        } else {
                            // A speculative landing cut that cannot be reused is not a harmless
                            // preview: it changes edge ownership.  Roll it back before trying the
                            // next lip so it cannot poison a later elevated surface.
                            Restore(data, beforeOpening);
                            open = OpenEdges.Of(data);
                        }
                    }
                }
                if (!connected) {
                    // A complex, solid prefab (for example a gatehouse) is intentionally cut out
                    // before its stairs are baked.  Its first stair tread can therefore sit just
                    // inside the resulting hole: there is no live ground left for TryOpenLanding
                    // to cut a mouth into.  Bridge that explicitly-authored landing to the *open
                    // boundary of the existing hole* instead.  The bridge is accepted only when
                    // its interior is wholly empty navmesh space, so this cannot lay a second
                    // surface across ordinary ground or make agents walk through a solid wall.
                    NavMeshData beforeBridge = data.Clone();
                    if (TryBridgeAcrossCutout(data, open, outline[edgeStart], outline[edgeEnd])) {
                        open = OpenEdges.Of(data);
                        connected = TryJoin(data, open, outline[edgeStart], outline[edgeEnd],
                                            out first, out second, OutlineJoinDistance,
                                            preserveLandingWidth: preserveLandingWidth);
                        if (connected) {
                            openings++;
                        } else {
                            // As above, a bridge is committed only if its far edge becomes the
                            // actual shared seam for this outline.
                            Restore(data, beforeBridge);
                            open = OpenEdges.Of(data);
                        }
                    }
                }
                if (!connected) continue;

                // A concave wall-walk may touch the same landing at more than one sampled edge.
                // Keep the first exact reuse rather than assigning one authored corner two distinct
                // base vertices.
                if (indices[edgeStart] < 0 && indices[edgeEnd] < 0) {
                    indices[edgeStart] = first;
                    indices[edgeEnd] = second;
                    joined++;
                    joinedOutlineEdge = edgeStart;
                    groundJoined = true;
                }
            }

            // A stair outline often has a locally-low *tread* inside the doorway while its actual
            // outer lip is level.  After a full building cutout the outer lip is the only safe
            // place to cross the empty cutout space back to the base mesh.  Try every remaining
            // perimeter edge for that special, empty-space-only bridge; ordinary direct joins and
            // seam cuts above remain restricted to genuine low landings.
            if (joined == 0 && hasLowerLanding) {
                int fallbackAttempts = 0;
                for (int edgeStart = 0; edgeStart < count; edgeStart++) {
                    if (landingEdges.Contains(edgeStart)) continue;
                    if (fallbackAttempts++ >= 2) break;
                int edgeEnd = (edgeStart + 1) % count;
                    NavMeshData beforeBridge = data.Clone();
                    if (!TryBridgeAcrossCutout(data, open, outline[edgeStart], outline[edgeEnd])) continue;

                    open = OpenEdges.Of(data);
                    if (TryJoin(data, open, outline[edgeStart], outline[edgeEnd],
                                out int first, out int second, OutlineJoinDistance,
                                preserveLandingWidth: preserveLandingWidth)) {
                        openings++;
                        indices[edgeStart] = first;
                        indices[edgeEnd] = second;
                        joined++;
                        joinedOutlineEdge = edgeStart;
                        groundJoined = true;
                        break;
                    }
                    Restore(data, beforeBridge);
                    open = OpenEdges.Of(data);
                }
            }

            // The upper lip of a stair commonly ends in the middle of one long deck edge.  The
            // deck may already have been baked as the provisional, level member of this elevated
            // network.  Split that open deck boundary at the projected stair corners and span the
            // small physical gap.  This produces a real shared edge at both sides of the hand-off;
            // merely overlapping the two faces in XY does not make a path in Bannerlord.
            if (hasLowerLanding && !elevatedJoined) {
                NavMeshData beforeSplice = data.Clone();
                open = OpenEdges.Of(data);
                if (TrySpliceElevatedLanding(data, open, outline, indices, out string spliceFailure)) {
                    joined++;
                    elevatedJoined = true;
                } else {
                    Restore(data, beforeSplice);
                    open = OpenEdges.Of(data);
                    if (sharedOutlineEdges != null && sharedOutlineEdges.Count > 0) {
                        throw new NavMeshFormatException(
                            "its upper landing could not join the adjacent elevated surface: " + spliceFailure);
                    }
                }
            }

            // A stair surface is not useful merely because its upper lip found the deck.  It must
            // also share a real edge with the lower/base navmesh; otherwise the whole stair/deck
            // network remains a floating island and followers stop at the first tread.
            if (hasLowerLanding && !groundJoined) {
                throw new NavMeshFormatException(
                    "its lower landing did not connect to the base navmesh");
            }

            if (joined == 0) {
                // A flat deck has no ground landing of its own.  It is useful only as part of an
                // authored network in which a stair/ramp edge projects onto this boundary.  Bake
                // it provisionally so the later stair can split and reuse its open edge.  Apply's
                // final component check rejects the whole elevated batch if that promised hand-off
                // never materialises, so an isolated floating island can never be committed.
                if (hasLowerLanding || sharedOutlineEdges == null || sharedOutlineEdges.Count == 0) {
                    throw new NavMeshFormatException(
                        "none of its lower landings reaches an open navmesh edge");
                }
            }

            NavFace reference = NearestFace(data, OpenEdges.Of(data), outline[landingEdges[0]]);
            int[] metadata = (int[])reference.Metadata.Clone();
            byte direction = reference.Direction;
            Dictionary<long, int> edgeLookup = BuildEdgeLookup(data);
            var points = new Dictionary<int, Point2>(count);
            for (int i = 0; i < count; i++) {
                if (indices[i] < 0) {
                    indices[i] = data.Vertices.Count;
                    data.Vertices.Add(outline[i]);
                }
                points[indices[i]] = new Point2(data.Vertices[indices[i]].X, data.Vertices[indices[i]].Y);
            }

            var polygon = new List<int>(indices);
            if (NavMeshGeometry.SignedArea(polygon, points) < 0) polygon.Reverse();
            List<int[]> triangles = NavMeshGeometry.TriangulateSimplePolygon(polygon, points, out _);
            if (triangles.Count == 0) throw new NavMeshFormatException("the elevated perimeter could not be triangulated");

            // A perfectly valid concave outline can force ear clipping to produce one narrow
            // triangle even though the authored walkway itself is broad.  This is common where a
            // stair landing opens into the side of a long wall-walk.  The cutout/addition paths
            // already solve the same problem by merging the sliver with its neighbour; elevated
            // outlines must use the same rule instead of rejecting the entire route.
            // Keep valid stair/landing triangles separate.  Combining them merely to reach the
            // preferred four-square-metre target can make one warped quad span both the incline
            // and its level landing; Bannerlord then has a legal face graph but followers hesitate
            // at the physical stair-to-deck transition.
            List<int[]> faces = NavMeshGeometry.MergeInvalidSlivers(triangles, points);

            int added = 0;
            foreach (int[] face in faces) {
                if (!NavMeshGeometry.IsAcceptableFace(face, points)) {
                    throw new NavMeshFormatException("the elevated perimeter produced a degenerate face; spread its corners apart");
                }
                var faceVertices = new uint[face.Length];
                var faceEdges = new uint[face.Length];
                for (int i = 0; i < face.Length; i++) {
                    faceVertices[i] = (uint)face[i];
                    faceEdges[i] = (uint)EnsureEdge(data, edgeLookup, face[i], face[(i + 1) % face.Length]);
                }
                data.Faces.Add(new NavFace(faceVertices, faceEdges, (int[])metadata.Clone(), direction));
                added++;
            }

            // Validate this one surface before committing its bookkeeping.  A complex building
            // can contain several nearby stairs; if one authoring outline would make a
            // non-manifold seam, restore only that outline and let the rest of the route network
            // continue baking.  The final Apply audit remains the last line of defence.
            NavMeshSummary surfaceSummary = NavMeshValidation.Summarize(data);
            if (!surfaceSummary.IsStructurallySound || surfaceSummary.NonManifoldEdges > 0) {
                throw new NavMeshFormatException(
                    "its outline edge " + (joinedOutlineEdge + 1) +
                    " conflicts with existing navmesh topology (" +
                    DescribeTopologyConflict(data, surfaceSummary) + ")");
            }

            if (groundJoined) result.BottomsJoined++;
            if (elevatedJoined) result.TopsJoined++;
            result.LandingOpenings += openings;
            return added;
        }

        /// <summary>
        /// Reuses an existing open elevated lip when an explicitly paired authored lip is already
        /// close enough that a connector strip would be a degenerate sliver.  Endpoint orientation
        /// is preserved, both endpoints must independently fit in 3D, and snapping is refused if
        /// it would collapse the authored edge.
        /// </summary>
        private static bool TryReuseElevatedOutlineEdge(NavMeshData data, OpenEdges open,
                                                         NavVertex first, NavVertex second,
                                                         out int firstIndex, out int secondIndex) {
            firstIndex = secondIndex = -1;
            double best = double.MaxValue;
            foreach (int edgeIndex in open.Indices) {
                NavEdge edge = data.Edges[edgeIndex];
                NavVertex a = data.Vertices[(int)edge.B];
                NavVertex b = data.Vertices[(int)edge.C];

                double directA = Distance(a, first), directB = Distance(b, second);
                double reverseA = Distance(b, first), reverseB = Distance(a, second);
                bool direct = directA <= SharedElevatedEdgeDistance
                              && directB <= SharedElevatedEdgeDistance;
                bool reverse = reverseA <= SharedElevatedEdgeDistance
                               && reverseB <= SharedElevatedEdgeDistance;
                if (!direct && !reverse) continue;

                double cost = direct ? directA + directB : reverseA + reverseB;
                if (cost >= best) continue;
                int candidateFirst = direct ? edge.B : edge.C;
                int candidateSecond = direct ? edge.C : edge.B;
                if (candidateFirst == candidateSecond) continue;
                best = cost;
                firstIndex = candidateFirst;
                secondIndex = candidateSecond;
            }
            return firstIndex >= 0;
        }

        /// <summary>
        /// Connects a newly-authored elevated lip to the open lip of an already-baked elevated
        /// surface.  Unlike <see cref="TryJoin"/>, the new surface keeps its own corner positions;
        /// a two-triangle strip spans the small, intentional gap between the stair and the deck.
        /// This avoids turning a slightly offset hand-off into overlapping triangles.
        /// </summary>
        private static bool TryConnectElevatedOutlines(NavMeshData data, OpenEdges open,
                                                        NavVertex first, NavVertex second,
                                                        out int firstIndex, out int secondIndex) {
            firstIndex = secondIndex = -1;
            int bestEdge = -1;
            bool reversed = false;
            double bestError = double.MaxValue;

            foreach (int edgeIndex in open.Indices) {
                NavEdge edge = data.Edges[edgeIndex];
                NavVertex a = data.Vertices[(int)edge.B];
                NavVertex b = data.Vertices[(int)edge.C];
                double direct = Distance(a, first) + Distance(b, second);
                double reverse = Distance(a, second) + Distance(b, first);
                bool directFits = Distance(a, first) <= SharedElevatedEdgeDistance
                                  && Distance(b, second) <= SharedElevatedEdgeDistance;
                bool reverseFits = Distance(a, second) <= SharedElevatedEdgeDistance
                                   && Distance(b, first) <= SharedElevatedEdgeDistance;
                if (!directFits && !reverseFits) continue;
                double error = directFits ? direct : reverse;
                bool candidateReversed = !directFits || reverse < direct;
                if (error >= bestError) continue;
                NavVertex targetA = candidateReversed ? b : a;
                NavVertex targetB = candidateReversed ? a : b;
                if (!IsSimpleConnectorStrip(targetA, targetB, second, first)) continue;
                bestEdge = edgeIndex;
                reversed = candidateReversed;
                bestError = error;
            }

            if (bestEdge < 0) return false;
            NavMeshData before = data.Clone();
            try {
                NavEdge source = data.Edges[bestEdge];
                int sourceFirst = reversed ? (int)source.C : (int)source.B;
                int sourceSecond = reversed ? (int)source.B : (int)source.C;
                firstIndex = data.Vertices.Count;
                data.Vertices.Add(first);
                secondIndex = data.Vertices.Count;
                data.Vertices.Add(second);

                NavFace owner = data.Faces[open.OwnerOf(bestEdge)];
                Dictionary<long, int> lookup = BuildEdgeLookup(data);
                int[][] triangles = {
                    new[] { sourceFirst, sourceSecond, secondIndex },
                    new[] { sourceFirst, secondIndex, firstIndex },
                };
                foreach (int[] triangle in triangles) {
                    var points = new Dictionary<int, Point2> {
                        [triangle[0]] = new Point2(data.Vertices[triangle[0]].X, data.Vertices[triangle[0]].Y),
                        [triangle[1]] = new Point2(data.Vertices[triangle[1]].X, data.Vertices[triangle[1]].Y),
                        [triangle[2]] = new Point2(data.Vertices[triangle[2]].X, data.Vertices[triangle[2]].Y),
                    };
                    if (NavMeshGeometry.SignedArea(triangle, points) < 0) Array.Reverse(triangle);
                    if (!NavMeshGeometry.IsAcceptableFace(triangle, points)) throw new NavMeshFormatException("the elevated hand-off is too narrow");
                    var edges = new uint[3];
                    for (int i = 0; i < 3; i++) {
                        edges[i] = (uint)EnsureEdge(data, lookup, triangle[i], triangle[(i + 1) % 3]);
                    }
                    data.Faces.Add(new NavFace(new uint[] { (uint)triangle[0], (uint)triangle[1], (uint)triangle[2] },
                                               edges, (int[])owner.Metadata.Clone(), owner.Direction));
                }
                NavMeshSummary check = NavMeshValidation.Summarize(data);
                if (!check.IsStructurallySound || check.NonManifoldEdges > 0) {
                    throw new NavMeshFormatException("the elevated hand-off conflicts with an existing seam");
                }
                return true;
            } catch (NavMeshFormatException) {
                Restore(data, before);
                firstIndex = secondIndex = -1;
                return false;
            }
        }

        private static string DescribeFirstNonManifoldEdge(NavMeshData data) {
            var uses = new int[data.Edges.Count];
            foreach (NavFace face in data.Faces) {
                foreach (uint edge in face.Edges) {
                    if (edge < uses.Length) uses[edge]++;
                }
            }
            for (int i = 0; i < uses.Length; i++) {
                if (uses[i] <= 2) continue;
                NavEdge edge = data.Edges[i];
                NavVertex a = data.Vertices[(int)edge.B];
                NavVertex b = data.Vertices[(int)edge.C];
                return $"edge {i} at ({a.X:0.0},{a.Y:0.0})-({b.X:0.0},{b.Y:0.0}), {uses[i]} faces";
            }
            return "unknown edge";
        }

        private static string DescribeTopologyConflict(NavMeshData data, NavMeshSummary summary) {
            if (summary.NonManifoldEdges > 0) return DescribeFirstNonManifoldEdge(data);
            if (summary.DuplicateEdges > 0) {
                HashSet<long> seen = NavMeshEdgeKey.NewSet(data.Edges.Count);
                for (int i = 0; i < data.Edges.Count; i++) {
                    NavEdge edge = data.Edges[i];
                    long key = EdgeKey(edge.B, edge.C);
                    if (seen.Add(key)) continue;
                    NavVertex a = data.Vertices[(int)edge.B];
                    NavVertex b = data.Vertices[(int)edge.C];
                    return $"duplicate edge {i} at ({a.X:0.0},{a.Y:0.0})-({b.X:0.0},{b.Y:0.0})";
                }
            }
            return summary.Describe();
        }

        /// <summary>
        /// Finds the authored edges which really are common boundaries between two elevated
        /// surfaces.  We compare both endpoint orientations in 3D: matching X/Y alone would
        /// accidentally link a lower stair to a wall-walk directly above it.
        /// </summary>
        private sealed class SharedOutlineCandidate {
            public int LeftRamp;
            public int LeftEdge;
            public int RightRamp;
            public int RightEdge;
            public double Error;
        }

        private static Dictionary<NavMeshRampPath, List<int>> FindSharedOutlineEdges(IList<NavMeshRampPath> ramps) {
            var result = new Dictionary<NavMeshRampPath, List<int>>();
            var candidates = new List<SharedOutlineCandidate>();
            for (int leftRamp = 0; leftRamp < ramps.Count; leftRamp++) {
                NavMeshRampPath left = ramps[leftRamp];
                if (!left.HasOutline) continue;
                for (int rightRamp = leftRamp + 1; rightRamp < ramps.Count; rightRamp++) {
                    NavMeshRampPath right = ramps[rightRamp];
                    if (!right.HasOutline) continue;
                    for (int leftEdge = 0; leftEdge < left.Outline.Length; leftEdge++) {
                        NavVertex leftA = left.Outline[leftEdge];
                        NavVertex leftB = left.Outline[(leftEdge + 1) % left.Outline.Length];
                        for (int rightEdge = 0; rightEdge < right.Outline.Length; rightEdge++) {
                            NavVertex rightA = right.Outline[rightEdge];
                            NavVertex rightB = right.Outline[(rightEdge + 1) % right.Outline.Length];
                            bool same = Distance(leftA, rightA) <= SharedElevatedEdgeDistance
                                        && Distance(leftB, rightB) <= SharedElevatedEdgeDistance;
                            bool reversed = Distance(leftA, rightB) <= SharedElevatedEdgeDistance
                                            && Distance(leftB, rightA) <= SharedElevatedEdgeDistance;
                            bool splice = EdgesCanSplice(leftA, leftB, rightA, rightB);
                            if (!same && !reversed && !splice) continue;
                            double directError = Distance(leftA, rightA) + Distance(leftB, rightB);
                            double reversedError = Distance(leftA, rightB) + Distance(leftB, rightA);
                            candidates.Add(new SharedOutlineCandidate {
                                LeftRamp = leftRamp,
                                LeftEdge = leftEdge,
                                RightRamp = rightRamp,
                                RightEdge = rightEdge,
                                Error = same ? directError : reversed ? reversedError
                                    : Math.Min(directError, reversedError) + SharedElevatedEdgeDistance
                            });
                        }
                    }
                }
            }

            // Resolve nearby physical lips into an unambiguous set of hand-offs.  An outline edge
            // may only be owned by one neighbour; the closest edge-pair wins.  This preserves
            // deliberate shared corners while preventing a cluster of nearby stair lips from
            // reusing the same baked seam three times.
            candidates.Sort((left, right) => left.Error.CompareTo(right.Error));
            var claimed = new HashSet<string>();
            foreach (SharedOutlineCandidate candidate in candidates) {
                string leftKey = candidate.LeftRamp + ":" + candidate.LeftEdge;
                string rightKey = candidate.RightRamp + ":" + candidate.RightEdge;
                if (claimed.Contains(leftKey) || claimed.Contains(rightKey)) continue;
                claimed.Add(leftKey);
                claimed.Add(rightKey);
                AddSharedEdge(result, ramps[candidate.LeftRamp], candidate.LeftEdge);
                AddSharedEdge(result, ramps[candidate.RightRamp], candidate.RightEdge);
            }
            return result;
        }

        /// <summary>
        /// True when the shorter of two same-height lips lands in the middle of the longer one.
        /// This records an authored stair/deck relationship before either surface is baked; the
        /// actual boundary split remains transactional in TrySpliceElevatedLanding.
        /// </summary>
        private static bool EdgesCanSplice(NavVertex leftA, NavVertex leftB,
                                           NavVertex rightA, NavVertex rightB) {
            return EdgeProjectsInto(leftA, leftB, rightA, rightB)
                || EdgeProjectsInto(rightA, rightB, leftA, leftB);
        }

        private static bool EdgeProjectsInto(NavVertex shortA, NavVertex shortB,
                                             NavVertex longA, NavVertex longB) {
            if (!TryProjectOnSegment(longA, longB, shortA, out double firstT, out double firstDistance)
                || !TryProjectOnSegment(longA, longB, shortB, out double secondT, out double secondDistance)) {
                return false;
            }
            if (firstT < 0.0 || firstT > 1.0 || secondT < 0.0 || secondT > 1.0
                || Math.Abs(firstT - secondT) < 0.03
                || firstDistance > ElevatedSpliceDistance || secondDistance > ElevatedSpliceDistance) {
                return false;
            }
            NavVertex projectedFirst = Interpolate(longA, longB, firstT);
            NavVertex projectedSecond = Interpolate(longA, longB, secondT);
            return Math.Abs(projectedFirst.Z - shortA.Z) <= ElevatedSpliceHeightDifference
                && Math.Abs(projectedSecond.Z - shortB.Z) <= ElevatedSpliceHeightDifference;
        }

        private static void AddSharedEdge(Dictionary<NavMeshRampPath, List<int>> result,
                                          NavMeshRampPath ramp, int edge) {
            if (!result.TryGetValue(ramp, out List<int> edges)) {
                edges = new List<int>();
                result[ramp] = edges;
            }
            if (!edges.Contains(edge)) edges.Add(edge);
        }

        /// <summary>
        /// Finds every locally-low edge that might be a stair or ramp landing.
        ///
        /// Do not limit this to the globally lowest part of the outline. A bridge can meet ground
        /// at different elevations at its two ends, and a wall-walk can have several stairs that
        /// begin on terraces of different heights. A candidate is only actually joined when there
        /// is a nearby open edge in the existing mesh, so considering every local low is safe and
        /// makes one perimeter useful for those more varied structures.
        /// </summary>
        private static List<int> FindLowLandingEdges(NavVertex[] outline, out bool hasLowerLanding) {
            var result = new List<int>();
            double minimumZ = double.MaxValue, maximumZ = double.MinValue;
            foreach (NavVertex point in outline) {
                minimumZ = Math.Min(minimumZ, point.Z);
                maximumZ = Math.Max(maximumZ, point.Z);
            }
            for (int i = 0; i < outline.Length; i++) {
                int previous = (i - 1 + outline.Length) % outline.Length;
                int next = (i + 1) % outline.Length;
                int following = (i + 2) % outline.Length;
                double height = (outline[i].Z + outline[next].Z) * 0.5;
                double before = (outline[previous].Z + outline[i].Z) * 0.5;
                double after = (outline[next].Z + outline[following].Z) * 0.5;
                if (height + 0.1 < before || height + 0.1 < after) {
                    result.Add(i);
                }
            }

            // Surface probing gives a nominally level wall-walk a few centimetres of variation.
            // Do not mistake that noise for a stair landing and cut a second route down through
            // the terrain. A real authored ramp/stair changes height by substantially more.
            hasLowerLanding = result.Count > 0 && maximumZ - minimumZ >= 0.75;
            if (!hasLowerLanding) result.Clear();
            // An author may trace clockwise from the top of a stair.  Try the lowest physical
            // lips first so the bounded landing search reaches ground/the previous deck rather
            // than spending its attempts on the decorative high end of the same outline.
            result.Sort((left, right) => {
                int leftNext = (left + 1) % outline.Length;
                int rightNext = (right + 1) % outline.Length;
                double leftHeight = outline[left].Z + outline[leftNext].Z;
                double rightHeight = outline[right].Z + outline[rightNext].Z;
                return leftHeight.CompareTo(rightHeight);
            });
            // A level landing/wall-walk has no locally-low edge of its own. It can still be the
            // next member of a valid network by sharing any perimeter edge with a stair or another
            // raised surface baked in a previous retry pass. Do not cut a seam into ground for
            // every such edge: that would be slow and could create a ground hole beneath a deck.
            if (!hasLowerLanding) {
                for (int i = 0; i < outline.Length; i++) result.Add(i);
            }
            return result;
        }

        /// <summary>
        /// Carves only the landing mouths that still need one. For a two-row straight ramp one
        /// four-corner cutout exposes both ends; for a longer path, separate first/last strips keep
        /// a bridge across unmeshed water from requiring navmesh under its entire deck.
        /// </summary>
        private static int OpenLandingSeams(NavMeshData data, NavMeshRampPath ramp,
                                            bool bottomAlreadyOpen, bool topAlreadyOpen) {
            int steps = ramp.Steps;
            int opened = 0;

            if (steps == 2 && !bottomAlreadyOpen && !topAlreadyOpen) {
                if (TryOpenLanding(data, ramp.Left[0], ramp.Right[0], ramp.Left[1], ramp.Right[1])) opened++;
                return opened;
            }

            if (!bottomAlreadyOpen
                && TryOpenLanding(data, ramp.Left[0], ramp.Right[0], ramp.Left[1], ramp.Right[1])) {
                opened++;
            }
            if (!topAlreadyOpen
                && TryOpenLanding(data, ramp.Left[steps - 2], ramp.Right[steps - 2],
                                  ramp.Left[steps - 1], ramp.Right[steps - 1])) {
                opened++;
            }
            return opened;
        }

        /// <summary>
        /// Turns a landing's first/last physical strip into a tiny navmesh hole. The hole boundary
        /// is then the exact two-vertex edge a ramp must share. A failed plan is harmless: the
        /// caller still tries a naturally open edge and otherwise reports a useful refusal.
        /// </summary>
        private static bool TryOpenLanding(NavMeshData data, NavVertex leftNear, NavVertex rightNear,
                                           NavVertex leftFar, NavVertex rightFar) {
            var footprint = new[] {
                new Point2(leftNear.X, leftNear.Y), new Point2(rightNear.X, rightNear.Y),
                new Point2(rightFar.X, rightFar.Y), new Point2(leftFar.X, leftFar.Y),
            };

            try {
                CutoutPlan plan = NavMeshCutout.Plan(data, footprint);
                if (!plan.CanApply) return false;
                NavMeshCutout.Apply(data, plan, validate: false);
                return true;
            } catch (NavMeshFormatException) {
                return false;
            }
        }

        /// <summary>
        /// Connects an explicitly drawn elevated landing to an open edge bordering a previously
        /// cut footprint.  This is deliberately narrower than the generic ground-addition pass:
        /// every point inside the joining strip must currently be in a navmesh hole.  Consequently
        /// it can restore the intended route through a cut-out gatehouse or stairwell, but can never
        /// bridge across live ground (where it would create overlapping faces) or guess a route
        /// through an unmarked building.
        /// </summary>
        private static bool TryBridgeAcrossCutout(NavMeshData data, OpenEdges open,
                                                  NavVertex landingA, NavVertex landingB) {
            const double maximumHeightDifference = 3.5;
            int bestEdge = -1;
            bool bestSwapped = false;
            var candidates = new List<Tuple<int, bool, double>>();

            foreach (int edgeIndex in open.Indices) {
                NavEdge edge = data.Edges[edgeIndex];
                NavVertex a = data.Vertices[edge.B];
                NavVertex b = data.Vertices[edge.C];

                double straightA = Distance(a, landingA);
                double straightB = Distance(b, landingB);
                double swappedA = Distance(a, landingB);
                double swappedB = Distance(b, landingA);

                // The outer ring made by a large building cutout can use source-face vertices
                // several metres beyond the physical wall.  This special path is still bounded
                // and still restricted to empty cutout space, but needs a wider reach than the
                // ordinary direct-reuse join above.
                // A gatehouse tunnel can be several metres inside a deliberately padded
                // cutout. The outline itself is an explicit author assertion that this strip is
                // physically open, so allow it to reach the exterior ring; the empty-strip check
                // below still forbids a connector through live (uncut) ground.
                const double cutoutBridgeDistance = 18.0;
                bool straight = straightA <= cutoutBridgeDistance && straightB <= cutoutBridgeDistance;
                bool swapped = swappedA <= cutoutBridgeDistance && swappedB <= cutoutBridgeDistance;
                if (!straight && !swapped) continue;

                bool useSwapped = swapped && (!straight || swappedA + swappedB < straightA + straightB);
                double height = useSwapped
                    ? Math.Max(Math.Abs(a.Z - landingB.Z), Math.Abs(b.Z - landingA.Z))
                    : Math.Max(Math.Abs(a.Z - landingA.Z), Math.Abs(b.Z - landingB.Z));
                if (height > maximumHeightDifference) continue;

                candidates.Add(Tuple.Create(edgeIndex, useSwapped,
                    useSwapped ? swappedA + swappedB : straightA + straightB));
            }

            // The closest exterior-ring edges are the only plausible connections. This method is
            // called only after both an ordinary shared-edge join and a local landing seam have
            // failed, so the candidate lies in the hole made by a cutout rather than atop live
            // ground. Avoid a whole-mesh point-in-face scan here: on a 25k-face scene it ran once
            // for every outlined stair edge and made saving look like a lock-up.
            candidates.Sort((left, right) => left.Item3.CompareTo(right.Item3));
            int candidateLimit = Math.Min(candidates.Count, 3);
            for (int candidate = 0; candidate < candidateLimit; candidate++) {
                int edgeIndex = candidates[candidate].Item1;
                bool useSwapped = candidates[candidate].Item2;
                NavEdge edge = data.Edges[edgeIndex];
                NavVertex a = data.Vertices[edge.B];
                NavVertex b = data.Vertices[edge.C];
                NavVertex firstLanding = useSwapped ? landingB : landingA;
                NavVertex secondLanding = useSwapped ? landingA : landingB;
                if (!IsSimpleConnectorStrip(a, b, secondLanding, firstLanding)) continue;
                bestEdge = edgeIndex;
                bestSwapped = useSwapped;
                break;
            }

            if (bestEdge < 0) return false;

            // `open` is normally refreshed after every committed edit, but a complex outline can
            // try several candidate lips in one pass.  Re-check the chosen edge immediately
            // before writing: never add a connector to a stale boundary edge that another face
            // has already claimed.
            OpenEdges liveOpen = OpenEdges.Of(data);
            if (!liveOpen.Indices.Contains(bestEdge)) return false;

            NavEdge baseEdge = data.Edges[bestEdge];
            NavVertex baseA = data.Vertices[baseEdge.B];
            NavVertex baseB = data.Vertices[baseEdge.C];
            NavVertex first = bestSwapped ? landingB : landingA;
            NavVertex second = bestSwapped ? landingA : landingB;

            // From this point the bridge writer mutates the mesh.  Keep it transactional even
            // when a later triangle proves degenerate: callers are entitled to treat `false` as
            // “nothing was changed”, and a half-built strip can otherwise surface much later as
            // an unrelated three-face seam.
            NavMeshData beforeBridge = data.Clone();

            int firstIndex = data.Vertices.Count;
            data.Vertices.Add(first);
            int secondIndex = data.Vertices.Count;
            data.Vertices.Add(second);

            Dictionary<long, int> lookup = BuildEdgeLookup(data);
            NavFace owner = data.Faces[liveOpen.OwnerOf(bestEdge)];
            int[] metadata = (int[])owner.Metadata.Clone();
            byte direction = owner.Direction;

            // The base edge is shared with its existing ground face; the landing edge stays open
            // so BuildOutline can immediately reuse it as the elevated surface's real seam.
            int[][] triangles = {
                new[] { baseEdge.B, baseEdge.C, secondIndex },
                new[] { baseEdge.B, secondIndex, firstIndex },
            };
            var points = new Dictionary<int, Point2> {
                [baseEdge.B] = new Point2(baseA.X, baseA.Y),
                [baseEdge.C] = new Point2(baseB.X, baseB.Y),
                [firstIndex] = new Point2(first.X, first.Y),
                [secondIndex] = new Point2(second.X, second.Y),
            };

            foreach (int[] triangle in triangles) {
                if (NavMeshGeometry.SignedArea(triangle, points) < 0) Array.Reverse(triangle);
                if (!NavMeshGeometry.IsAcceptableFace(triangle, points)) {
                    Restore(data, beforeBridge);
                    return false;
                }

                var edges = new uint[3];
                for (int i = 0; i < 3; i++) {
                    edges[i] = (uint)EnsureEdge(data, lookup, triangle[i], triangle[(i + 1) % 3]);
                }
                data.Faces.Add(new NavFace(new uint[] { (uint)triangle[0], (uint)triangle[1], (uint)triangle[2] },
                                           edges, (int[])metadata.Clone(), direction));
            }
            return true;
        }

        private static bool IsSimpleConnectorStrip(NavVertex a, NavVertex b,
                                                   NavVertex c, NavVertex d) {
            var points = new Dictionary<int, Point2> {
                [0] = new Point2(a.X, a.Y), [1] = new Point2(b.X, b.Y),
                [2] = new Point2(c.X, c.Y), [3] = new Point2(d.X, d.Y),
            };
            var loop = new List<int> { 0, 1, 2, 3 };
            if (!NavMeshGeometry.IsSimplePolygon(loop, points)
                || Math.Abs(NavMeshGeometry.SignedArea(loop, points)) < NavMeshGeometry.MinimumFaceArea * 2.0) {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Joins an elevated outline to the middle of an already-open elevated deck boundary.
        /// A wall walk is frequently one long face, while the top of a stair is only a small
        /// segment along that face.  Requiring the two segment endpoints to be identical made a
        /// perfectly sensible stair stop halfway: it reached ground but was a dead-end at the top.
        ///
        /// The owner face is replaced by equivalent triangles with the two projected points
        /// inserted into its boundary.  A narrow four-sided strip then joins that new mouth to the
        /// unchanged authored stair edge.  No interior edge receives a third face.
        /// </summary>
        private static bool TrySpliceElevatedLanding(NavMeshData data, OpenEdges open,
                                                     NavVertex[] outline, int[] assigned,
                                                     out string failure) {
            failure = "no same-height deck edge is close enough";
            int bestOutlineEdge = -1;
            int bestBaseEdge = -1;
            double bestCost = double.MaxValue;
            double bestFirstT = 0.0, bestSecondT = 0.0;

            double lowest = double.MaxValue;
            for (int i = 0; i < outline.Length; i++) lowest = Math.Min(lowest, outline[i].Z);

            for (int outlineEdge = 0; outlineEdge < outline.Length; outlineEdge++) {
                int outlineNext = (outlineEdge + 1) % outline.Length;
                if (assigned[outlineEdge] >= 0 || assigned[outlineNext] >= 0) continue;
                NavVertex first = outline[outlineEdge];
                NavVertex second = outline[outlineNext];
                if ((first.Z + second.Z) * 0.5 < lowest + 0.75) continue;

                foreach (int baseEdgeIndex in open.Indices) {
                    NavEdge baseEdge = data.Edges[baseEdgeIndex];
                    NavVertex baseA = data.Vertices[baseEdge.B];
                    NavVertex baseB = data.Vertices[baseEdge.C];
                    if (!TryProjectOnSegment(baseA, baseB, first, out double firstT, out double firstDistance)
                        || !TryProjectOnSegment(baseA, baseB, second, out double secondT, out double secondDistance)) continue;
                    if (firstT < 0.0 || firstT > 1.0 || secondT < 0.0 || secondT > 1.0
                        || Math.Abs(firstT - secondT) < 0.03
                        || firstDistance > ElevatedSpliceDistance || secondDistance > ElevatedSpliceDistance) continue;

                    NavVertex projectedFirst = Interpolate(baseA, baseB, firstT);
                    NavVertex projectedSecond = Interpolate(baseA, baseB, secondT);
                    if (Math.Abs(projectedFirst.Z - first.Z) > ElevatedSpliceHeightDifference
                        || Math.Abs(projectedSecond.Z - second.Z) > ElevatedSpliceHeightDifference) continue;

                    NavFace owner = data.Faces[open.OwnerOf(baseEdgeIndex)];
                    double ownerAverageHeight = 0.0;
                    foreach (uint vertex in owner.Vertices) ownerAverageHeight += data.Vertices[(int)vertex].Z;
                    ownerAverageHeight /= owner.Vertices.Length;
                    if (Math.Abs(ownerAverageHeight - (first.Z + second.Z) * 0.5)
                        > ElevatedSpliceHeightDifference) continue;

                    // Several lips can project onto the same deck corner.  Reject a collinear or
                    // folded candidate during ranking so a valid adjacent side is still examined;
                    // choosing the numerically closest one first and failing later made the high
                    // castle wall miss its usable stair hand-off.
                    NavVertex candidateMouthFirst = ProjectedOrEndpoint(baseA, baseB, firstT);
                    NavVertex candidateMouthSecond = ProjectedOrEndpoint(baseA, baseB, secondT);
                    if (!IsSimpleConnectorStrip(candidateMouthFirst, candidateMouthSecond,
                                                second, first)) continue;

                    double cost = firstDistance + secondDistance;
                    if (cost >= bestCost) continue;
                    bestCost = cost;
                    bestOutlineEdge = outlineEdge;
                    bestBaseEdge = baseEdgeIndex;
                    bestFirstT = firstT;
                    bestSecondT = secondT;
                }
            }

            if (bestBaseEdge < 0) return false;

            NavEdge edge = data.Edges[bestBaseEdge];
            NavVertex a = data.Vertices[edge.B];
            NavVertex b = data.Vertices[edge.C];
            int next = (bestOutlineEdge + 1) % outline.Length;
            NavVertex landingFirst = outline[bestOutlineEdge];
            NavVertex landingSecond = outline[next];

            // The mouth points are exact points on the existing deck boundary.  The connector's
            // other edge keeps the user-authored stair corners at their sampled physical heights.
            NavFace deckOwner = data.Faces[open.OwnerOf(bestBaseEdge)];
            int[] metadata = (int[])deckOwner.Metadata.Clone();
            byte direction = deckOwner.Direction;
            NavMeshData beforeMouth = data.Clone();

            int mouthFirstIndex = ReuseOrAddProjectedVertex(data, edge, a, b, bestFirstT,
                                                            out NavVertex mouthFirst);
            int mouthSecondIndex = ReuseOrAddProjectedVertex(data, edge, a, b, bestSecondT,
                                                             out NavVertex mouthSecond);
            if (mouthFirstIndex == mouthSecondIndex
                || !IsSimpleConnectorStrip(mouthFirst, mouthSecond, landingSecond, landingFirst)) {
                failure = "the stair-to-deck connector would cross itself or be too narrow";
                return false;
            }
            if (!ReplaceBoundaryEdge(data, open.OwnerOf(bestBaseEdge), bestBaseEdge,
                                     mouthFirstIndex, mouthSecondIndex, bestFirstT, bestSecondT,
                                     out string splitFailure)) {
                // A very narrow triangular deck owner may be impossible to subdivide without
                // creating a face smaller than any shipped navmesh face.  The authored stair lip
                // still projects wholly onto this declared deck boundary.  In that case span to
                // the complete open boundary edge instead: this keeps every face above the engine
                // floor and joins the same two physical surfaces without inventing a floating
                // midpoint or relaxing global mesh-size safety.
                Restore(data, beforeMouth);
                open = OpenEdges.Of(data);
                if (TryConnectSpecificElevatedEdge(data, open, bestBaseEdge,
                                                   landingFirst, landingSecond,
                                                   out int wholeFirst, out int wholeSecond)) {
                    assigned[bestOutlineEdge] = wholeFirst;
                    assigned[next] = wholeSecond;
                    failure = "";
                    return true;
                }
                failure = "the deck edge could not be safely split at the stair mouth: " + splitFailure;
                return false;
            }

            int firstIndex = data.Vertices.Count;
            data.Vertices.Add(landingFirst);
            int secondIndex = data.Vertices.Count;
            data.Vertices.Add(landingSecond);

            Dictionary<long, int> lookup = BuildEdgeLookup(data);
            int[][] connector = {
                new[] { mouthFirstIndex, mouthSecondIndex, secondIndex },
                new[] { mouthFirstIndex, secondIndex, firstIndex },
            };
            var points = new Dictionary<int, Point2> {
                [mouthFirstIndex] = new Point2(mouthFirst.X, mouthFirst.Y),
                [mouthSecondIndex] = new Point2(mouthSecond.X, mouthSecond.Y),
                [firstIndex] = new Point2(landingFirst.X, landingFirst.Y),
                [secondIndex] = new Point2(landingSecond.X, landingSecond.Y),
            };
            foreach (int[] triangle in connector) {
                if (NavMeshGeometry.SignedArea(triangle, points) < 0) Array.Reverse(triangle);
                if (!NavMeshGeometry.IsAcceptableFace(triangle, points)) {
                    failure = "the stair-to-deck connector produced a face below the engine size limit";
                    return false;
                }
                var edges = new uint[3];
                for (int i = 0; i < 3; i++) {
                    edges[i] = (uint)EnsureEdge(data, lookup, triangle[i], triangle[(i + 1) % 3]);
                }
                data.Faces.Add(new NavFace(new uint[] { (uint)triangle[0], (uint)triangle[1], (uint)triangle[2] },
                                           edges, (int[])metadata.Clone(), direction));
            }
            // The outlined stair face must reuse the connector's landing vertices.  Creating a
            // second pair at identical coordinates only looks joined in a wireframe; Bannerlord's
            // path graph connects faces by shared edge/vertex indices, not by coincident geometry.
            assigned[bestOutlineEdge] = firstIndex;
            assigned[next] = secondIndex;
            failure = "";
            return true;
        }

        private static bool TryConnectSpecificElevatedEdge(NavMeshData data, OpenEdges open,
                                                            int edgeIndex,
                                                            NavVertex first, NavVertex second,
                                                            out int firstIndex, out int secondIndex) {
            firstIndex = secondIndex = -1;
            if (!open.Indices.Contains(edgeIndex)) return false;
            NavMeshData before = data.Clone();
            try {
                NavEdge source = data.Edges[edgeIndex];
                NavVertex a = data.Vertices[(int)source.B];
                NavVertex b = data.Vertices[(int)source.C];
                bool reversed = Distance(a, second) + Distance(b, first)
                              < Distance(a, first) + Distance(b, second);
                int sourceFirst = reversed ? (int)source.C : (int)source.B;
                int sourceSecond = reversed ? (int)source.B : (int)source.C;
                NavVertex targetFirst = data.Vertices[sourceFirst];
                NavVertex targetSecond = data.Vertices[sourceSecond];
                if (!IsSimpleConnectorStrip(targetFirst, targetSecond, second, first)) return false;

                firstIndex = data.Vertices.Count;
                data.Vertices.Add(first);
                secondIndex = data.Vertices.Count;
                data.Vertices.Add(second);
                NavFace owner = data.Faces[open.OwnerOf(edgeIndex)];
                Dictionary<long, int> lookup = BuildEdgeLookup(data);
                int[][] connector = {
                    new[] { sourceFirst, sourceSecond, secondIndex },
                    new[] { sourceFirst, secondIndex, firstIndex },
                };
                foreach (int[] triangle in connector) {
                    var points = new Dictionary<int, Point2> {
                        [triangle[0]] = new Point2(data.Vertices[triangle[0]].X, data.Vertices[triangle[0]].Y),
                        [triangle[1]] = new Point2(data.Vertices[triangle[1]].X, data.Vertices[triangle[1]].Y),
                        [triangle[2]] = new Point2(data.Vertices[triangle[2]].X, data.Vertices[triangle[2]].Y),
                    };
                    if (NavMeshGeometry.SignedArea(triangle, points) < 0) Array.Reverse(triangle);
                    if (!NavMeshGeometry.IsAcceptableFace(triangle, points)) {
                        throw new NavMeshFormatException("the full-edge hand-off is too narrow");
                    }
                    var edges = new uint[3];
                    for (int i = 0; i < 3; i++) {
                        edges[i] = (uint)EnsureEdge(data, lookup, triangle[i], triangle[(i + 1) % 3]);
                    }
                    data.Faces.Add(new NavFace(
                        new uint[] { (uint)triangle[0], (uint)triangle[1], (uint)triangle[2] },
                        edges, (int[])owner.Metadata.Clone(), owner.Direction));
                }
                NavMeshSummary check = NavMeshValidation.Summarize(data);
                if (!check.IsStructurallySound || check.NonManifoldEdges > 0) {
                    throw new NavMeshFormatException("the full-edge hand-off conflicts with an existing seam");
                }
                return true;
            } catch (NavMeshFormatException) {
                Restore(data, before);
                firstIndex = secondIndex = -1;
                return false;
            }
        }

        private static int ReuseOrAddProjectedVertex(NavMeshData data, NavEdge edge,
                                                      NavVertex a, NavVertex b, double t,
                                                      out NavVertex point) {
            point = ProjectedOrEndpoint(a, b, t);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double snap = length <= NavMeshGeometry.MinimumEdgeLength
                ? 0.5 : NavMeshGeometry.MinimumEdgeLength / length;
            if (t <= snap) {
                return edge.B;
            }
            if (t >= 1.0 - snap) {
                return edge.C;
            }
            int index = data.Vertices.Count;
            data.Vertices.Add(point);
            return index;
        }

        private static NavVertex ProjectedOrEndpoint(NavVertex a, NavVertex b, double t) {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double snap = length <= NavMeshGeometry.MinimumEdgeLength
                ? 0.5 : NavMeshGeometry.MinimumEdgeLength / length;
            if (t <= snap) return a;
            if (t >= 1.0 - snap) return b;
            return Interpolate(a, b, t);
        }

        private static bool ReplaceBoundaryEdge(NavMeshData data, int ownerIndex, int edgeIndex,
                                                int firstIndex, int secondIndex,
                                                double firstT, double secondT,
                                                out string failure) {
            failure = "unknown split failure";
            if (ownerIndex < 0 || ownerIndex >= data.Faces.Count) {
                failure = "the boundary owner no longer exists";
                return false;
            }
            NavFace owner = data.Faces[ownerIndex];
            int edgeAt = -1;
            for (int i = 0; i < owner.Edges.Length; i++) {
                if (owner.Edges[i] == (uint)edgeIndex) { edgeAt = i; break; }
            }
            if (edgeAt < 0) {
                failure = "the boundary edge is no longer owned by its face";
                return false;
            }

            var expanded = new List<int>(owner.Vertices.Length + 2);
            for (int i = 0; i < owner.Vertices.Length; i++) {
                int current = (int)owner.Vertices[i];
                int next = (int)owner.Vertices[(i + 1) % owner.Vertices.Length];
                expanded.Add(current);
                if (i != edgeAt) continue;
                bool followsStoredEdge = owner.Vertices[i] == (uint)data.Edges[edgeIndex].B;
                int insertFirst;
                int insertSecond;
                if (followsStoredEdge == (firstT < secondT)) {
                    insertFirst = firstIndex; insertSecond = secondIndex;
                } else {
                    insertFirst = secondIndex; insertSecond = firstIndex;
                }
                if (insertFirst != current && insertFirst != next) expanded.Add(insertFirst);
                if (insertSecond != current && insertSecond != next && insertSecond != insertFirst)
                    expanded.Add(insertSecond);
            }

            var points = new Dictionary<int, Point2>();
            foreach (int index in expanded) {
                NavVertex point = data.Vertices[index];
                points[index] = new Point2(point.X, point.Y);
            }
            if (!NavMeshGeometry.IsSimplePolygon(expanded, points)) {
                failure = $"the expanded {owner.Vertices.Length}-corner owner crosses itself";
                return false;
            }
            if (NavMeshGeometry.SignedArea(expanded, points) < 0) expanded.Reverse();

            List<int[]> faces;
            if (owner.Vertices.Length == 3) {
                // The deck triangulator normally leaves the landing on one long boundary of a
                // triangle.  Feeding that triangle back through the general ear clipper after two
                // collinear mouth points are inserted can select a zero-width ear and reject an
                // otherwise ordinary stair hand-off.  Split the owner deterministically: every
                // consecutive segment of the expanded boundary edge gets a triangle to the one
                // opposite vertex.  This preserves the authored mouth as a real open graph edge.
                int storedCurrent = (int)owner.Vertices[edgeAt];
                int storedNext = (int)owner.Vertices[(edgeAt + 1) % owner.Vertices.Length];
                int opposite = (int)owner.Vertices[(edgeAt + 2) % owner.Vertices.Length];
                int currentAt = expanded.IndexOf(storedCurrent);
                int nextAt = expanded.IndexOf(storedNext);
                if (currentAt < 0 || nextAt < 0) {
                    failure = "the split edge endpoints disappeared from the owner";
                    return false;
                }

                var chain = new List<int> { storedCurrent };
                for (int at = (currentAt + 1) % expanded.Count;
                     at != nextAt;
                     at = (at + 1) % expanded.Count) {
                    if (expanded[at] != opposite) chain.Add(expanded[at]);
                }
                chain.Add(storedNext);

                var split = new List<int[]>();
                for (int part = 0; part + 1 < chain.Count; part++) {
                    var triangle = new[] { chain[part], chain[part + 1], opposite };
                    if (NavMeshGeometry.SignedArea(triangle, points) < 0) Array.Reverse(triangle);
                    split.Add(triangle);
                }
                faces = NavMeshGeometry.MergeInvalidSlivers(split, points);
            } else {
                List<int[]> triangles = NavMeshGeometry.TriangulateSimplePolygon(expanded, points, out _);
                if (triangles.Count == 0) {
                    failure = $"the expanded {owner.Vertices.Length}-corner owner could not be triangulated";
                    return false;
                }
                faces = NavMeshGeometry.MergeInvalidSlivers(triangles, points);
            }
            foreach (int[] face in faces) {
                if (!NavMeshGeometry.IsAcceptableFace(face, points)) {
                    failure = $"the split left an engine-invalid {face.Length}-corner face "
                        + $"({Math.Abs(NavMeshGeometry.SignedArea(face, points)):0.###} m2)";
                    return false;
                }
            }

            Dictionary<long, int> lookup = BuildEdgeLookup(data);
            var replacement = new List<NavFace>(faces.Count);
            foreach (int[] face in faces) {
                var vertices = new uint[face.Length];
                var edges = new uint[face.Length];
                for (int i = 0; i < face.Length; i++) {
                    vertices[i] = (uint)face[i];
                    edges[i] = (uint)EnsureEdge(data, lookup, face[i], face[(i + 1) % face.Length]);
                }
                replacement.Add(new NavFace(vertices, edges, (int[])owner.Metadata.Clone(), owner.Direction));
            }
            data.Faces.RemoveAt(ownerIndex);
            data.Faces.AddRange(replacement);
            failure = "";
            return true;
        }

        private static bool TryProjectOnSegment(NavVertex a, NavVertex b, NavVertex point,
                                                out double t, out double distance) {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            t = 0.0;
            distance = double.MaxValue;
            if (lengthSquared < 0.0001) return false;
            t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared;
            double x = a.X + dx * t, y = a.Y + dy * t;
            double ox = point.X - x, oy = point.Y - y;
            distance = Math.Sqrt(ox * ox + oy * oy);
            return true;
        }

        private static NavVertex Interpolate(NavVertex a, NavVertex b, double t) => new NavVertex(
            (float)(a.X + (b.X - a.X) * t),
            (float)(a.Y + (b.Y - a.Y) * t),
            (float)(a.Z + (b.Z - a.Z) * t));


        /// <summary>
        /// Tries to land a ramp end on an existing open edge, reusing its two vertices.
        ///
        /// Both ends of the edge must be near their matching rail, and the edge must not already be
        /// used by two faces - joining onto a shared edge would make it non-manifold, which the engine
        /// treats as broken geometry.
        /// </summary>
        private static bool TryJoin(NavMeshData data, OpenEdges open, NavVertex leftEnd, NavVertex rightEnd,
                                    out int leftVertex, out int rightVertex, double joinDistance = JoinDistance,
                                    bool preserveLandingWidth = false) {
            leftVertex = rightVertex = -1;

            double authoredWidth = Distance(leftEnd, rightEnd);

            double best = joinDistance * joinDistance * 2.0;
            foreach (int edgeIndex in open.Indices) {
                NavEdge edge = data.Edges[edgeIndex];
                NavVertex a = data.Vertices[edge.B];
                NavVertex b = data.Vertices[edge.C];

                if (preserveLandingWidth && authoredWidth > 0.5) {
                    double edgeWidth = Distance(a, b);
                    if (edgeWidth < authoredWidth * MinimumLandingWidthRetention
                        || edgeWidth > authoredWidth * MaximumLandingWidthExpansion) {
                        continue;
                    }
                }

                // Either orientation: the edge does not know which way the ramp runs.
                double straightLeft = Distance(a, leftEnd);
                double straightRight = Distance(b, rightEnd);
                double swappedLeft = Distance(b, leftEnd);
                double swappedRight = Distance(a, rightEnd);

                // A summed distance alone lets a ramp latch onto a long, nearby perimeter edge
                // whose two endpoints are both several metres from the authored landing. That looks
                // connected in a table of counts and physically teleports the end of the ramp. Both
                // rails must independently reach their matching edge vertex.
                bool straightValid = straightLeft <= joinDistance && straightRight <= joinDistance;
                bool swappedValid = swappedLeft <= joinDistance && swappedRight <= joinDistance;
                if (!straightValid && !swappedValid) continue;

                double straight = straightLeft + straightRight;
                double swapped = swappedLeft + swappedRight;

                bool useSwapped = swapped < straight;
                if (useSwapped && !swappedValid) useSwapped = false;
                if (!useSwapped && !straightValid) useSwapped = true;
                double cost = useSwapped ? swapped : straight;
                if (cost >= best) continue;

                best = cost;
                leftVertex = useSwapped ? edge.C : edge.B;
                rightVertex = useSwapped ? edge.B : edge.C;
            }
            return leftVertex >= 0;
        }

        private static NavFace NearestFace(NavMeshData data, OpenEdges open, NavVertex near) {
            int bestFace = 0;
            double best = double.MaxValue;

            foreach (int edgeIndex in open.Indices) {
                NavEdge edge = data.Edges[edgeIndex];
                double distance = Distance(data.Vertices[edge.B], near);
                if (distance >= best) continue;

                best = distance;
                bestFace = open.OwnerOf(edgeIndex);
            }
            return data.Faces[bestFace];
        }

        private static double Distance(NavVertex a, NavVertex b) {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static int EnsureEdge(NavMeshData data, Dictionary<long, int> lookup, int a, int b) {
            long key = EdgeKey(a, b);
            if (lookup.TryGetValue(key, out int edgeIndex)) return edgeIndex;

            edgeIndex = data.Edges.Count;
            lookup[key] = edgeIndex;
            data.Edges.Add(new NavEdge(-1, a, b, -1, -1, 0));
            return edgeIndex;
        }

        private static Dictionary<long, int> BuildEdgeLookup(NavMeshData data) {
            Dictionary<long, int> lookup = NavMeshEdgeKey.NewMap(data.Edges.Count);
            for (int i = 0; i < data.Edges.Count; i++) {
                long key = EdgeKey(data.Edges[i].B, data.Edges[i].C);
                if (!lookup.ContainsKey(key)) lookup[key] = i;
            }
            return lookup;
        }

        private static void Restore(NavMeshData data, NavMeshData snapshot) {
            data.Signature = snapshot.Signature;
            data.Vertices = snapshot.Vertices;
            data.Edges = snapshot.Edges;
            data.Faces = snapshot.Faces;
            data.GlobalTail = snapshot.GlobalTail;
            data.Wrapper = snapshot.Wrapper;
            data.OriginalContainer = snapshot.OriginalContainer;
        }

        private static long EdgeKey(int a, int b) => NavMeshEdgeKey.Of(a, b);

        /// <summary>The mesh's open boundary, worked out once and reused by every ramp.</summary>
        private sealed class OpenEdges {
            public List<int> Indices = new List<int>();
            private int[] _owner = new int[0];

            public int OwnerOf(int edgeIndex) => _owner[edgeIndex];

            public static OpenEdges Of(NavMeshData data) {
                var counts = new int[data.Edges.Count];
                var owner = new int[data.Edges.Count];

                for (int faceIndex = 0; faceIndex < data.Faces.Count; faceIndex++) {
                    foreach (uint edgeIndex in data.Faces[faceIndex].Edges) {
                        if (counts[edgeIndex]++ == 0) owner[edgeIndex] = faceIndex;
                    }
                }

                var open = new OpenEdges { _owner = owner };
                for (int i = 0; i < counts.Length; i++) {
                    if (counts[i] == 1) open.Indices.Add(i);
                }
                return open;
            }
        }
    }
}
