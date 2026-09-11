using System;
using System.Collections.Generic;
using System.IO;

namespace CustomSceneCreator.NavMesh {

    /// <summary>Everything one bake should do to a scene's navmesh.</summary>
    public class NavMeshBakeRequest {
        /// <summary>Four-corner footprints to remove walkable ground from, one per placed object.</summary>
        public List<Point2[]> Cutouts = new List<Point2[]>();

        /// <summary>Node-drawn polygon holes constrained to the clicked surface's height band.</summary>
        public List<NavMeshHeightLimitedCutout> HeightLimitedCutouts = new List<NavMeshHeightLimitedCutout>();

        /// <summary>Areas the author marked as needing walkable ground.</summary>
        public List<NavMeshRequiredArea> Additions = new List<NavMeshRequiredArea>();

        /// <summary>
        /// Surfaces that climb: ramps, stairs, raised walkways.
        ///
        /// Separate from Additions because the two solve different problems. An addition fills flat
        /// open ground and needs one connection; a ramp is narrow, changes height along its length,
        /// and is useless unless it connects at BOTH ends.
        /// </summary>
        public List<NavMeshRampPath> Ramps = new List<NavMeshRampPath>();

        /// <summary>
        /// Keep a copy of the original navmesh beside the one being written.
        ///
        /// On by default. A navmesh is scene data a modder may have spent hours on, and this is the
        /// only route back if a bake turns out wrong.
        /// </summary>
        public bool KeepBackup = true;

        /// <summary>
        /// Called with normalized bake progress, as (percent, 100).
        ///
        /// Exists so a caller running this off the main thread can say how far along it is. A bake of
        /// thirty buildings on a large terrain takes several seconds, and silence for several seconds
        /// is indistinguishable from a hang.
        /// </summary>
        public Action<int, int>? Progress;

        public bool HasWork => Cutouts.Count > 0 || HeightLimitedCutouts.Count > 0
            || Additions.Count > 0 || Ramps.Count > 0;
    }

    public class NavMeshHeightLimitedCutout {
        public string Label = "Drawn cutout";
        public Point2[] Corners = Array.Empty<Point2>();
        public double MinZ;
        public double MaxZ;
    }

    /// <summary>What one bake did, in the words the user should see.</summary>
    public class NavMeshBakeReport {
        public bool Changed;
        public int CutoutsApplied;
        public int CutoutsSkipped;
        public int AreasAdded;
        public int AreasSkipped;
        public int RampsBuilt;
        public int RampsSkipped;
        public string OutputPath = "";
        public string BackupPath = "";

        /// <summary>One line per operation, including every refusal and why.</summary>
        public List<string> Log = new List<string>();

        public NavMeshSummary Before = new NavMeshSummary();
        public NavMeshSummary After = new NavMeshSummary();

        /// <summary>A one-line result for a HUD message.</summary>
        public string Headline {
            get {
                if (!Changed) {
                    int skippedOnly = CutoutsSkipped + AreasSkipped + RampsSkipped;
                    return skippedOnly > 0
                        ? $"Navmesh bake could not apply {skippedOnly} authored item(s); the existing bake was kept."
                        : "Navmesh bake had no completed authoring to apply; the existing bake was kept.";
                }

                string text = $"Navmesh baked: {CutoutsApplied} cut out, {AreasAdded} filled in"
                              + (RampsBuilt > 0 ? $", {RampsBuilt} ramp(s) built" : "");
                int skipped = CutoutsSkipped + AreasSkipped + RampsSkipped;
                if (skipped > 0) text += $", {skipped} skipped";
                return text + ".";
            }
        }

        public void Add(string line) => Log.Add(line);
    }

    /// <summary>
    /// Bakes a scene's navmesh: cuts holes under objects, fills marked ground, writes the file.
    ///
    /// This is the whole navmesh feature behind one call. It is what the scene editor runs on save,
    /// and it is deliberately the same call another mod makes - Homesteads Reloaded bakes a
    /// settlement's navmesh the same way when the player leaves and the scene is written out.
    ///
    /// Each operation is applied to a copy, and kept only if it succeeds. One impossible footprint
    /// therefore costs that footprint and nothing else, which matters at save time: a bake that
    /// fails wholesale because of one bad object would leave the user with no navmesh at all and no
    /// idea which object was to blame.
    /// </summary>
    public static class NavMeshBaker {

        /// <summary>Runs a bake in memory. No file is touched.</summary>
        public static NavMeshBakeReport Bake(NavMeshData data, NavMeshBakeRequest request) {
            if (data == null) throw new ArgumentNullException("data");
            if (request == null) throw new ArgumentNullException("request");

            var report = new NavMeshBakeReport { Before = NavMeshValidation.Summarize(data) };
            void ReportProgress(int percent) => request.Progress?.Invoke(
                Math.Max(0, Math.Min(100, percent)), 100);
            ReportProgress(3);
            if (!request.HasWork) {
                report.Add("Nothing to bake: no object footprints and no marked areas.");
                report.After = report.Before;
                ReportProgress(100);
                return report;
            }

            NavMeshData working = data;

            if (request.Cutouts.Count > 0) {
                // One copy and one structural check for the whole set, not one per footprint. Each
                // check walks every face and edge in the mesh, so doing it per footprint made a
                // homestead with thirty buildings take thirty times longer than it needed to - long
                // enough to read as the game having frozen on the way out of the scene.
                working = data.Clone();

                // Footprints standing too close to leave usable ground between them are cut as one
                // outline. This is not an optimisation: a cutout cannot be applied where it touches
                // a hole an earlier cutout made, so without merging, a corner where two runs of
                // fence meet loses the whole second run. Merging first means no cut ever meets
                // another cut's hole.
                // Reserve explicitly-authored, ground-level passages before removing a solid
                // footprint.  A gatehouse is one large collision footprint but may contain a real
                // tunnel through its middle.  Removing the complete rectangle first and hoping an
                // elevated pass can reconstruct that floor is destructive: if the upper stair/deck
                // network is rejected, the valid shipped ground through the arch has already gone.
                // Upper floors do not reserve anything here; only outlines at the source floor are
                // allowed to keep cells, so this cannot leave invisible walkable ground beneath a
                // wall or tower.
                List<Point2[]> plannedCutouts = ExpandCutoutsForElevatedReservations(
                    data, request.Cutouts, request.Ramps, report);
                NavMeshFootprintMerge.MergeResult merged =
                    NavMeshFootprintMerge.Merge(plannedCutouts);
                List<Point2[]> cutouts = merged.Footprints;
                ReportProgress(10);

                if (merged.GroupsMerged > 0) report.Add("Combined footprints: " + merged.Describe());
                foreach (string rejected in merged.Rejected) report.Add("Kept separate - " + rejected);
                int cutoutOperationLogStart = report.Log.Count;

                for (int i = 0; i < cutouts.Count; i++) {
                    int vertexMark = working.Vertices.Count;
                    int edgeMark = working.Edges.Count;
                    List<NavFace> facesMark = working.Faces;

                    try {
                        CutoutResult result = ApplyWithRetries(working, cutouts[i]);
                        report.CutoutsApplied++;
                        report.Add($"Cut out footprint {i + 1}: {result.Describe()}");
                    } catch (Exception ex) {
                        // Never let a failed combined attempt influence its fallback. A split
                        // attempt is allowed to make partial edits internally, so restore the
                        // exact pre-cut state before trying the original panels.
                        working.Faces = facesMark;
                        if (working.Edges.Count > edgeMark) {
                            working.Edges.RemoveRange(edgeMark, working.Edges.Count - edgeMark);
                        }
                        if (working.Vertices.Count > vertexMark) {
                            working.Vertices.RemoveRange(vertexMark, working.Vertices.Count - vertexMark);
                        }

                        // A connected wall is normally one merged outline. If that outline runs
                        // over the edge of the shipped mesh, rejecting it as a whole would make
                        // every panel of an otherwise ordinary wall walkable. Restore the failed
                        // combined attempt, then cut its original panels one at a time. The retry
                        // pass shrinks a panel only where it meets an already-cut neighbour, so
                        // this retains the useful part of an edge-touching wall instead of losing
                        // all of it.
                        if (merged.FallbackMembers.TryGetValue(cutouts[i], out List<Point2[]> members)
                            && TryCutMembers(working, members, out CutoutResult fallback,
                                             out int failedMembers)) {
                            report.CutoutsApplied++;
                            if (failedMembers > 0) report.CutoutsSkipped += failedMembers;
                            report.Add($"Cut out footprint {i + 1} as {fallback.PiecesCut} sub-cut piece(s) "
                                       + $"after its combined outline was unusable ({ex.Message})"
                                       + (failedMembers > 0
                                           ? $"; {failedMembers} panel(s) could not be cut"
                                           : ""));
                            ReportProgress(10 + (int)Math.Round(30.0 * (i + 1) / Math.Max(1, cutouts.Count)));
                            continue;
                        }

                        report.CutoutsSkipped++;
                        report.Add($"Skipped footprint {i + 1}: {ex.Message}");
                    }

                    ReportProgress(10 + (int)Math.Round(30.0 * (i + 1) / Math.Max(1, cutouts.Count)));
                }

                if (report.CutoutsApplied > 0 && !Check(report.Before, working, report)) {
                    // The fast batch path is important on a large settlement, but its old failure
                    // policy was all-or-nothing: one courtyard, wall end, or edge-of-map footprint
                    // could discard dozens of perfectly safe cuts. Remove the provisional report
                    // and replay the merged groups as independent transactions. Only the group
                    // which increases the number of disconnected navmesh components is refused.
                    if (report.Log.Count > cutoutOperationLogStart) {
                        report.Log.RemoveRange(cutoutOperationLogStart,
                                               report.Log.Count - cutoutOperationLogStart);
                    }
                    report.CutoutsApplied = 0;
                    report.CutoutsSkipped = 0;
                    working = data;
                    report.Add("The complete cutout set would split the walkable ground; "
                               + "retrying independent building groups.");
                    SalvageCutouts(ref working, cutouts, merged, report,
                                   percent => ReportProgress(10 + (int)Math.Round(30.0 * percent / 100.0)));
                }
            }
            ReportProgress(42);

            // Hand-drawn cutouts run separately from flat object footprints. Merging them in XY
            // would discard their height bands and could cut the ground floor underneath an upper
            // storey. Each polygon is therefore a small independent transaction.
            if (request.HeightLimitedCutouts.Count > 0) {
                for (int i = 0; i < request.HeightLimitedCutouts.Count; i++) {
                    NavMeshHeightLimitedCutout cutout = request.HeightLimitedCutouts[i];
                    NavMeshData attempt = working.Clone();
                    try {
                        CutoutPlan plan = NavMeshCutout.Plan(attempt, cutout.Corners,
                            cutout.MinZ, cutout.MaxZ);
                        CutoutResult result = NavMeshCutout.Apply(attempt, plan);
                        working = attempt;
                        report.CutoutsApplied++;
                        report.Add($"Cut out {cutout.Label}: {result.Describe()}");
                    } catch (Exception ex) {
                        report.CutoutsSkipped++;
                        report.Add($"Skipped {cutout.Label}: {ex.Message}");
                    }
                    ReportProgress(42 + (int)Math.Round(8.0 * (i + 1)
                        / Math.Max(1, request.HeightLimitedCutouts.Count)));
                }
            }
            ReportProgress(50);

            // Additions run as one pass because overlapping marks are meant to merge into a single
            // surface; splitting them per mark would produce patches that meet without joining.
            if (request.Additions.Count > 0) {
                NavMeshData attempt = working.Clone();
                try {
                    AdditionResult result = NavMeshAddition.Apply(attempt, request.Additions);
                    working = attempt;
                    report.AreasAdded = result.AreasFilled;
                    report.AreasSkipped = result.AreasSkipped;
                    report.Add($"Filled marked ground: {result.Describe()}");

                    // Each skipped patch names itself and its reason. A bare count would leave the
                    // user with no way to tell which mark to redo.
                    foreach (string skipped in result.Skipped) {
                        report.Add("Skipped marked ground - " + skipped);
                    }
                } catch (Exception ex) {
                    report.AreasSkipped = request.Additions.Count;
                    report.Add($"Skipped all {request.Additions.Count} marked area(s): {ex.Message}");
                }
            }
            ReportProgress(55);

            // Ramps last: they join onto the mesh, so they want to see any ground the addition pass
            // has already put down - a stair up to a new platform needs that platform to exist.
            if (request.Ramps.Count > 0) {
                WarnAboutUncutElevations(request, report);

                // Independent buildings must be independent transactions. NavMeshRamp correctly
                // rejects a group that creates a disconnected island, but putting an entire homestead
                // into that one transaction meant one bad player outline rolled back valid gatehouse,
                // stair and wall-walk networks elsewhere in the settlement.
                List<List<NavMeshRampPath>> groups = ClusterRamps(request.Ramps);

                // Structures that contain a slope are built first.
                //
                // A wall walk usually reaches the rest of the world through its stair, and a stair
                // never needs the walk to exist first - so building a deck before its stair asks it
                // to join a seam that has not been opened yet, and it is skipped for having "no lower
                // landing". Clustering does not always put the two together (a stair and the deck it
                // serves can land in separate spatial groups), so the order has to be stated here.
                groups.Sort((a, b) => HasSlope(b).CompareTo(HasSlope(a)));
                int completed = 0;
                for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++) {
                    List<NavMeshRampPath> group = groups[groupIndex];
                    int progressBase = completed;
                    Action<int, int> progress = (done, total) => ReportProgress(55 + (int)Math.Round(
                        38.0 * (progressBase + done) / Math.Max(1, request.Ramps.Count)));

                    // Decks that share a height are unioned before building, so a corner arrives as
                    // one polygon rather than two rectangles the joiner must find a shared edge
                    // between. See MergeCoplanarDecks.
                    var notes = new List<string>();
                    List<NavMeshRampPath> merged = MergeCoplanarDecks(group, notes);

                    NavMeshData attempt = null;
                    RampResult result = null;

                    // Merging is an attempt, never a commitment, and it is kept ONLY when it builds
                    // the whole structure cleanly.
                    //
                    // "Better of the two" is the obvious rule and it is wrong: the authored path has
                    // a salvage pass behind it that rebuilds a failed group one outline at a time, so
                    // an authored attempt that throws can still end up with more walkable deck than a
                    // merged attempt that merely limps. Measured on an outside corner, preferring the
                    // limping merge cost two built ramps. Anything short of a clean merge therefore
                    // falls back to the untouched behaviour below, salvage included.
                    if (merged.Count != group.Count) {
                        NavMeshData mergedAttempt = working.Clone();
                        try {
                            RampResult mergedResult = NavMeshRamp.Apply(mergedAttempt, merged, progress);
                            if (mergedResult.RampsSkipped == 0 && mergedResult.RampsBuilt > 0) {
                                attempt = mergedAttempt;
                                result = mergedResult;
                            }
                        } catch {
                            // Fall through to the authored outlines.
                        }
                        if (attempt == null) notes.Clear();
                    }

                    if (attempt != null) {
                        working = attempt;
                        report.RampsBuilt += result.RampsBuilt;
                        report.RampsSkipped += result.RampsSkipped;
                        foreach (string note in notes) report.Add(note);
                        report.Add($"Built elevated group {groupIndex + 1}/{groups.Count}: {result.Describe()}");
                        completed += group.Count;
                        ReportProgress(55 + (int)Math.Round(
                            38.0 * completed / Math.Max(1, request.Ramps.Count)));
                        continue;
                    }

                    attempt = working.Clone();
                    try {
                        result = NavMeshRamp.Apply(attempt, group, progress);
                        working = attempt;
                        report.RampsBuilt += result.RampsBuilt;
                        report.RampsSkipped += result.RampsSkipped;
                        report.Add($"Built elevated group {groupIndex + 1}/{groups.Count}: {result.Describe()}");

                        foreach (string skipped in result.Skipped) {
                            report.Add("Skipped ramp - " + skipped);
                        }
                    } catch (Exception ex) {

                        report.Add($"Skipped elevated group {groupIndex + 1}/{groups.Count} "
                                   + $"({DescribeRampGroup(group)}): {ex.Message}");

                        // A close-packed castle or homestead can make several otherwise unrelated
                        // stairs and decks one spatial cluster.  If their all-at-once validation
                        // fails, salvage them transactionally one outline at a time.  Multiple
                        // passes matter: a deck may initially float, then become valid after its
                        // stair has joined the ground mesh.
                        SalvageRampGroup(ref working, group, report);
                    }

                    completed += group.Count;
                    ReportProgress(55 + (int)Math.Round(
                        38.0 * completed / Math.Max(1, request.Ramps.Count)));
                }
            }
            ReportProgress(95);

            report.Changed = report.CutoutsApplied > 0 || report.AreasAdded > 0 || report.RampsBuilt > 0;
            if (report.Changed) {
                // Copy the result back into the caller's object so callers holding a reference see
                // the baked mesh, whichever attempts were kept along the way.
                data.Signature = working.Signature;
                data.Vertices = working.Vertices;
                data.Edges = working.Edges;
                data.Faces = working.Faces;
            }

            report.After = NavMeshValidation.Summarize(data);
            ReportProgress(97);
            return report;
        }

        private static void SalvageRampGroup(ref NavMeshData working,
                                             IList<NavMeshRampPath> group,
                                             NavMeshBakeReport report) {
            var pending = new List<NavMeshRampPath>(group);
            var lastErrors = new Dictionary<NavMeshRampPath, string>();

            bool madeProgress;
            do {
                madeProgress = false;
                for (int index = pending.Count - 1; index >= 0; index--) {
                    NavMeshRampPath ramp = pending[index];
                    NavMeshData attempt = working.Clone();
                    try {
                        RampResult result = NavMeshRamp.Apply(attempt, new[] { ramp });
                        if (result.RampsBuilt <= 0) {
                            lastErrors[ramp] = result.Skipped.Count > 0
                                ? string.Join("; ", result.Skipped)
                                : "the outline did not produce a connected walkable surface";
                            continue;
                        }

                        working = attempt;
                        report.RampsBuilt += result.RampsBuilt;
                        report.Add("Recovered ramp independently - " + result.Describe());
                        pending.RemoveAt(index);
                        lastErrors.Remove(ramp);
                        madeProgress = true;
                    } catch (Exception retryError) {
                        lastErrors[ramp] = retryError.Message;
                    }
                }
            } while (madeProgress && pending.Count > 0);

            report.RampsSkipped += pending.Count;
            foreach (NavMeshRampPath ramp in pending) {
                string label = string.IsNullOrWhiteSpace(ramp.Label) ? "unnamed route" : ramp.Label;
                string reason = lastErrors.TryGetValue(ramp, out string error)
                    ? error
                    : "the outline could not connect to the retained navmesh";
                report.Add($"Skipped ramp - '{label}': {reason}");
            }
        }

        /// <summary>
        /// Partitions authored elevated surfaces into physical structures. Touching stairs, landings
        /// and decks stay together so they can share seams; a route on the other side of a homestead
        /// cannot make that structure's successful transaction roll back.
        /// </summary>
        /// <summary>
        /// The largest top-to-bottom spread an outline may have and still count as a FLAT deck, in
        /// metres. A wall walk measures about 0.08; the stair beside it on the same prefab spans over
        /// ten. The two cases are nowhere near each other, so this only has to sit somewhere sensible
        /// in between - it is not a tuned figure.
        /// </summary>
        private const double FlatDeckSpan = 0.60;

        /// <summary>How far apart two flat decks' heights may be and still be treated as one surface.</summary>
        private const double DeckHeightBand = 0.75;

        /// <summary>
        /// How close two decks must be to be unioned, in metres.
        ///
        /// Much tighter than the 1.5 m used for building footprints, and deliberately so. Cutouts are
        /// holes, and a hole that is slightly too generous costs nothing; a deck is a surface agents
        /// stand on, and the union is traced on a 0.2 m grid, so every metre of reach is a metre of
        /// walkway that might be invented over open air. Decks meeting at a corner touch or overlap
        /// already, so reaching further buys nothing.
        /// </summary>
        private const double DeckMergeSeparation = 0.5;

        /// <summary>
        /// Unions flat decks that sit at the same height and touch, so the ramp pass sees ONE surface.
        ///
        /// <para><b>The problem this solves is corners.</b> Each wall prefab contributes its wall walk
        /// as a rectangle along its own axis. Run two walls in a line and their end edges are parallel
        /// and facing, which the joiner stitches happily. Turn a corner and those two end edges are
        /// PERPENDICULAR - they meet at a point, not along an edge - so the joiner reports "no
        /// same-height deck edge is close enough" and skips the deck entirely. That is a live report
        /// of a corner tower with flags on it and no archers, and no amount of careful placement fixes
        /// it, because the geometry is what is wrong.</para>
        ///
        /// <para>Merging turns an L of two rectangles into one L-shaped polygon that COVERS the
        /// corner, so there is no seam left to stitch. It is the same NavMeshFootprintMerge that
        /// already unions a cluster of animal pens into one cutout.</para>
        ///
        /// <para><b>Height banding is what makes this safe.</b> Cutouts are flat and merge freely;
        /// decks are not. A wall walk at z 11.4 and the stair climbing to it from z 0.75 must never be
        /// unioned, or the stair is flattened into the deck and the way up disappears. Only outlines
        /// that are themselves flat, and within <see cref="DeckHeightBand"/> of each other, are
        /// considered - everything else passes through untouched.</para>
        ///
        /// <para>Nothing here is load-bearing: a merged outline records what it replaced, and the
        /// salvage pass restores the originals if the union fails to build.</para>
        /// </summary>
        private static List<NavMeshRampPath> MergeCoplanarDecks(IList<NavMeshRampPath> ramps,
                                                                List<string> notes) {
            var result = new List<NavMeshRampPath>();
            var flat = new List<NavMeshRampPath>();

            foreach (NavMeshRampPath ramp in ramps) {
                if (!ramp.HasOutline) { result.Add(ramp); continue; }
                double low = double.MaxValue, high = double.MinValue;
                foreach (NavVertex v in ramp.Outline) {
                    if (v.Z < low) low = v.Z;
                    if (v.Z > high) high = v.Z;
                }
                if (high - low <= FlatDeckSpan) flat.Add(ramp);
                else result.Add(ramp);            // sloped: a stair or a ramp, left alone
            }
            if (flat.Count < 2) { result.AddRange(flat); return result; }

            flat.Sort((a, b) => MeanHeight(a).CompareTo(MeanHeight(b)));

            int merged = 0, consumed = 0;
            int index = 0;
            while (index < flat.Count) {
                // A band runs while consecutive heights stay within tolerance of the previous one, so
                // a long shallow rampart does not get chopped at an arbitrary absolute height.
                int end = index + 1;
                while (end < flat.Count
                       && MeanHeight(flat[end]) - MeanHeight(flat[end - 1]) <= DeckHeightBand) {
                    end++;
                }

                List<NavMeshRampPath> band = flat.GetRange(index, end - index);
                index = end;

                if (band.Count == 1) { result.Add(band[0]); continue; }

                var outlines = new List<Point2[]>(band.Count);
                foreach (NavMeshRampPath deck in band) outlines.Add(Flatten(deck.Outline));

                NavMeshFootprintMerge.MergeResult union =
                    NavMeshFootprintMerge.Merge(outlines, DeckMergeSeparation);
                if (union.GroupsMerged == 0) { result.AddRange(band); continue; }

                foreach (Point2[] outline in union.Footprints) {
                    if (!union.FallbackMembers.TryGetValue(outline, out List<Point2[]> members)) {
                        // Passed through the merge untouched - hand back the ORIGINAL path rather
                        // than rebuilding one, so its authored heights survive exactly.
                        result.Add(Original(band, outlines, outline));
                        continue;
                    }

                    List<NavMeshRampPath> sources = Sources(band, outlines, members);
                    var rebuilt = new NavVertex[outline.Length];
                    for (int i = 0; i < outline.Length; i++) {
                        rebuilt[i] = new NavVertex((float)outline[i].X, (float)outline[i].Y,
                                                   NearestHeight(sources, outline[i]));
                    }

                    var path = new NavMeshRampPath {
                        Label = $"merged deck ({sources.Count} pieces near {MeanHeight(sources[0]):0.0} m)",
                        Outline = rebuilt
                    };
                    result.Add(path);
                    merged++;
                    consumed += sources.Count;
                }
            }

            if (merged > 0) {
                notes.Add($"Combined elevated decks: {consumed} same-height outline(s) became {merged} "
                          + "surface(s). Wall walks meeting at a corner are now one polygon, so there "
                          + "is no seam left to join.");
            }
            return result;
        }

        /// <summary>True when any outline in the group climbs - a stair, a ramp, a sloped walkway.</summary>
        private static int HasSlope(List<NavMeshRampPath> group) {
            foreach (NavMeshRampPath ramp in group) {
                if (!ramp.HasOutline) return 1;      // rail-described paths are ramps by definition
                double low = double.MaxValue, high = double.MinValue;
                foreach (NavVertex v in ramp.Outline) {
                    if (v.Z < low) low = v.Z;
                    if (v.Z > high) high = v.Z;
                }
                if (high - low > FlatDeckSpan) return 1;
            }
            return 0;
        }

        private static double MeanHeight(NavMeshRampPath ramp) {
            if (ramp.Outline.Length == 0) return 0.0;
            double total = 0.0;
            foreach (NavVertex v in ramp.Outline) total += v.Z;
            return total / ramp.Outline.Length;
        }

        private static Point2[] Flatten(NavVertex[] outline) {
            var points = new Point2[outline.Length];
            for (int i = 0; i < outline.Length; i++) points[i] = new Point2(outline[i].X, outline[i].Y);
            return points;
        }

        /// <summary>Maps a footprint the merge handed back straight through to the path it came from.</summary>
        private static NavMeshRampPath Original(List<NavMeshRampPath> band,
                                                List<Point2[]> outlines, Point2[] footprint) {
            for (int i = 0; i < outlines.Count; i++) {
                if (ReferenceEquals(outlines[i], footprint)) return band[i];
            }
            return band[0];
        }

        private static List<NavMeshRampPath> Sources(List<NavMeshRampPath> band,
                                                     List<Point2[]> outlines, List<Point2[]> members) {
            var sources = new List<NavMeshRampPath>(members.Count);
            foreach (Point2[] member in members) {
                for (int i = 0; i < outlines.Count; i++) {
                    if (ReferenceEquals(outlines[i], member)) { sources.Add(band[i]); break; }
                }
            }
            if (sources.Count == 0) sources.AddRange(band);
            return sources;
        }

        /// <summary>
        /// The height of the nearest authored vertex to a point on the merged outline.
        ///
        /// Not an average: a wall walk is rarely perfectly level, and flattening a band to one height
        /// would make the deck float at one end and sink into the stone at the other.
        /// </summary>
        private static float NearestHeight(List<NavMeshRampPath> sources, Point2 at) {
            float best = 0f;
            double bestDistance = double.MaxValue;
            foreach (NavMeshRampPath deck in sources) {
                foreach (NavVertex v in deck.Outline) {
                    double dx = v.X - at.X, dy = v.Y - at.Y;
                    double distance = dx * dx + dy * dy;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = v.Z;
                }
            }
            return best;
        }

        private static List<List<NavMeshRampPath>> ClusterRamps(IList<NavMeshRampPath> ramps) {
            var groups = new List<List<NavMeshRampPath>>();
            var assigned = new bool[ramps.Count];

            for (int seed = 0; seed < ramps.Count; seed++) {
                if (assigned[seed]) continue;
                var group = new List<NavMeshRampPath>();
                var queue = new Queue<int>();
                assigned[seed] = true;
                queue.Enqueue(seed);

                while (queue.Count > 0) {
                    int current = queue.Dequeue();
                    group.Add(ramps[current]);
                    for (int candidate = 0; candidate < ramps.Count; candidate++) {
                        if (assigned[candidate] || !RampsAreNeighbours(ramps[current], ramps[candidate]))
                            continue;
                        assigned[candidate] = true;
                        queue.Enqueue(candidate);
                    }
                }
                groups.Add(group);
            }
            return groups;
        }

        private static bool RampsAreNeighbours(NavMeshRampPath first, NavMeshRampPath second) {
            const double xyJoin = 4.5;
            const double zJoin = 2.75;
            double xyJoinSquared = xyJoin * xyJoin;
            foreach (NavVertex a in RampPoints(first)) {
                foreach (NavVertex b in RampPoints(second)) {
                    double dx = a.X - b.X;
                    double dy = a.Y - b.Y;
                    if (dx * dx + dy * dy <= xyJoinSquared && Math.Abs(a.Z - b.Z) <= zJoin)
                        return true;
                }
            }
            return false;
        }

        private static IEnumerable<NavVertex> RampPoints(NavMeshRampPath ramp) {
            if (ramp.Outline != null && ramp.Outline.Length > 0) {
                foreach (NavVertex point in ramp.Outline) yield return point;
                yield break;
            }
            if (ramp.Left != null) foreach (NavVertex point in ramp.Left) yield return point;
            if (ramp.Right != null) foreach (NavVertex point in ramp.Right) yield return point;
        }

        private static string DescribeRampGroup(IList<NavMeshRampPath> group) {
            int shown = Math.Min(3, group.Count);
            string text = "";
            for (int i = 0; i < shown; i++) {
                if (i > 0) text += ", ";
                text += string.IsNullOrWhiteSpace(group[i].Label) ? "unnamed route" : group[i].Label;
            }
            if (group.Count > shown) text += $", +{group.Count - shown} more";
            return text;
        }

        /// <summary>
        /// Conservative fallback for a merged fence/wall that cannot be cut as one outline. A
        /// member that overlaps ground already removed may legitimately fail; the remaining panels
        /// still remove their own walkable ground and are strictly better than leaving the whole
        /// barrier untouched.
        /// </summary>
        private static bool TryCutMembers(NavMeshData data, IList<Point2[]> members,
                                          out CutoutResult combined, out int failedMembers) {
            combined = null;
            failedMembers = 0;

            foreach (Point2[] member in members) {
                try {
                    CutoutResult result = ApplyWithRetries(data, member);
                    combined = Combine(combined, result);
                } catch (Exception) {
                    failedMembers++;
                }
            }

            return combined != null;
        }

        /// <summary>
        /// Replays a cutout batch one physical group at a time after the fast all-at-once attempt
        /// proves that the complete set would disconnect the scene. This is intentionally a slow
        /// recovery path: ordinary bakes keep the single validation pass, while a problematic
        /// homestead keeps every safe building instead of losing its entire bake.
        /// </summary>
        private static void SalvageCutouts(ref NavMeshData working,
                                           IList<Point2[]> cutouts,
                                           NavMeshFootprintMerge.MergeResult merged,
                                           NavMeshBakeReport report,
                                           Action<int> progress) {
            for (int index = 0; index < cutouts.Count; index++) {
                Point2[] footprint = cutouts[index];
                List<Point2[]> members = null;
                merged.FallbackMembers.TryGetValue(footprint, out members);

                if (TryCommitCutout(ref working, footprint,
                                    out CutoutResult result, out string reason)) {
                    report.CutoutsApplied++;
                    report.Add($"Recovered cutout group {index + 1}/{cutouts.Count}: "
                               + result.Describe());
                } else if (members != null && members.Count > 1) {
                    // A merged run can enclose a courtyard or meet the edge of the shipped mesh in
                    // a way that disconnects ground, even though most of its component wall panels
                    // are safe. Give those panels the same transactional treatment individually.
                    int committed = 0;
                    int skipped = 0;
                    CutoutResult combined = null;
                    string lastReason = reason;
                    foreach (Point2[] member in members) {
                        if (TryCommitCutout(ref working, member,
                                            out CutoutResult memberResult,
                                            out string memberReason)) {
                            committed++;
                            combined = Combine(combined, memberResult);
                        } else {
                            skipped++;
                            lastReason = memberReason;
                        }
                    }

                    if (committed > 0) {
                        report.CutoutsApplied++;
                        report.CutoutsSkipped += skipped;
                        report.Add($"Recovered cutout group {index + 1}/{cutouts.Count} as "
                                   + $"{committed} independent panel(s): {combined.Describe()}"
                                   + (skipped > 0 ? $"; {skipped} panel(s) skipped" : ""));
                    } else {
                        report.CutoutsSkipped++;
                        report.Add($"Skipped cutout group {index + 1}/{cutouts.Count}: {lastReason}");
                    }
                } else {
                    report.CutoutsSkipped++;
                    report.Add($"Skipped cutout group {index + 1}/{cutouts.Count}: {reason}");
                }

                progress((int)Math.Round(100.0 * (index + 1) / Math.Max(1, cutouts.Count)));
            }
        }

        /// <summary>The innermost stack frame of an exception, trimmed, or "" when unavailable.</summary>
        private static string FirstFrame(Exception ex) {
            string trace = ex.StackTrace ?? "";
            int end = trace.IndexOf((char)10);   // newline, written as a code to survive tooling
            return (end < 0 ? trace : trace.Substring(0, end)).Trim();
        }

        private static bool TryCommitCutout(ref NavMeshData working,
                                            Point2[] footprint,
                                            out CutoutResult result,
                                            out string reason) {
            NavMeshSummary before = NavMeshValidation.Summarize(working);
            NavMeshData attempt = working.Clone();
            result = null;
            reason = "the footprint did not produce a usable cut";

            try {
                result = ApplyWithRetries(attempt, footprint);
            } catch (NavMeshFormatException ex) {
                // A geometry refusal. Expected, and its message already says what was wrong.
                reason = ex.Message;
                return false;
            } catch (Exception ex) {
                // Anything else is a DEFECT, not a refusal, and "Index was outside the bounds of the
                // array" tells nobody where to look. Carry the type and the top frame so the next
                // bake log points straight at the line.
                string where = FirstFrame(ex);
                reason = $"internal error ({ex.GetType().Name}: {ex.Message}) at {where} - "
                         + $"footprint has {footprint.Length} corner(s)";
                return false;
            }

            NavMeshSummary after = NavMeshValidation.Summarize(attempt);
            if (!IsSafeCutoutTransition(before, after, out reason)) return false;

            working = attempt;
            return true;
        }

        private static bool IsSafeCutoutTransition(NavMeshSummary before,
                                                   NavMeshSummary after,
                                                   out string reason) {
            if (!after.IsStructurallySound) {
                reason = "the result was not a sound navmesh - " + after.Describe();
                return false;
            }
            if (after.ConnectedFaceComponents > before.ConnectedFaceComponents) {
                // Worth being clear about what this check is for, because it looks over-strict and
                // is not. If cutting a ring of walls severs the inside of a homestead from the
                // outside, attackers cannot path in AT ALL - the battle becomes a siege of a wall
                // nobody can cross. Refusing the cut and falling back to per-building cuts keeps the
                // homestead playable, which is the right trade even though it leaves some walls
                // walk-through.
                reason = $"it split the walkable ground into {after.ConnectedFaceComponents} pieces "
                         + $"(was {before.ConnectedFaceComponents}) - agents on one piece could never "
                         + "reach the others";
                return false;
            }

            reason = "";
            return true;
        }

        /// <summary>
        /// Splits a cutout into small quads when a low elevated outline passes through it.  This is
        /// a conservative alternative to a boolean polygon-with-holes implementation: cells within
        /// the explicit physical route are retained, all remaining cells are fed through the same
        /// proven cutout code as an ordinary wall, and the footprint merger reunites adjacent solid
        /// cells before anything is written.
        /// </summary>
        private static List<Point2[]> ExpandCutoutsForElevatedReservations(
            NavMeshData data, IList<Point2[]> cutouts, IList<NavMeshRampPath> ramps,
            NavMeshBakeReport report) {
            var result = new List<Point2[]>();
            if (ramps == null || ramps.Count == 0) {
                result.AddRange(cutouts);
                return result;
            }

            foreach (Point2[] footprint in cutouts) {
                var reserved = new List<Point2[]>();
                double baseFloor = LowestBaseHeightWithin(data, footprint);
                foreach (NavMeshRampPath ramp in ramps) {
                    if (!ramp.HasOutline) continue;
                    Point2[] outline = ToPoints(ramp.Outline);
                    if (!NavMeshGeometry.PolygonsIntersect(footprint, outline)) continue;

                    // Only surfaces close to their surrounding base mesh reserve a tunnel/entry.
                    // Upper decks retain their real elevated mesh but must not leave invisible
                    // walkable ground beneath a solid wall.
                    float lowest = float.MaxValue;
                    foreach (NavVertex point in ramp.Outline) lowest = Math.Min(lowest, point.Z);
                    // A reservation is for a ground-level passage through a cut-out solid,
                    // not every floor of a multi-storey prefab.  Keeping the threshold close to
                    // the source floor preserves a gate/tunnel without also retaining an
                    // invisible column of walkable ground under its upper decks and stairs.
                    if (lowest > baseFloor + 0.5) continue;
                    reserved.Add(outline);
                }

                if (reserved.Count == 0) {
                    result.Add(footprint);
                    continue;
                }

                // Subtract the passage from the building and cut what is left, rather than dicing
                // the building into cells and cutting them one by one. The cell grid produced 372
                // footprints from twenty-three buildings, and because a 1.25 m cell is smaller than
                // the navmesh faces around it, most of its repair rings were slivers the engine
                // refuses - 69 skipped in a single group. Subtraction gives the same shape in two or
                // three pieces, each big enough to cut cleanly.
                List<Point2[]> remainder =
                    NavMeshFootprintMerge.Subtract(footprint, reserved, PassageClearance);

                if (remainder.Count == 0) {
                    // A passage that covers the ENTIRE footprint is not a passage. A gate is a gap
                    // through a solid, so subtracting it must leave the solid behind; when nothing is
                    // left, the marked route is the top of the object rather than a way through it,
                    // and the height test that classified it as ground-level was wrong.
                    //
                    // Leaving it uncut costs twice. The building stays walk-through, and - measured
                    // directly - its own ramp dies with it: a wall-walk stair joins the ground through
                    // the hole the wall's cutout makes, so with no cutout there is no open edge to
                    // land on. Removing the cutout from the High Castle Wall and Tall Defensive Wall
                    // fixtures reproduces exactly the two failures seen on a real homestead:
                    // "none of its lower landings reaches an open navmesh edge" and "its lower landing
                    // did not connect to the base navmesh".
                    //
                    // So cut it whole. The elevated surface is separate mesh at its own height and is
                    // unaffected; what gets removed is the ground underneath a solid object.
                    result.Add(footprint);
                    report.Add($"Cut one footprint whole: its {reserved.Count} marked route(s) covered "
                               + "all of it, so they describe its top rather than a way through it.");
                    continue;
                }

                result.AddRange(remainder);
                report.Add($"Reserved {reserved.Count} low route(s) through one cutout; "
                           + $"cut the remaining solid as {remainder.Count} piece(s).");
            }
            return result;
        }

        /// <summary>
        /// How much room to leave around a marked passage, in metres.
        ///
        /// Chosen to reproduce what the old cell grid actually reserved. That test kept a whole
        /// 1.25 m cell when its CENTRE was within 0.55 m of the route, so the effective clearance
        /// was about 1.2 m, not 0.55. Subtracting at the literal 0.55 pinched every passage and cost
        /// two ramps on the gatehouse fixture, because a ground-level ramp joins the mesh right
        /// where the passage meets the building.
        ///
        /// Measured on that fixture: 0.55 built 7 ramps, 0.9 and above built 8. Erring wide is the
        /// safe direction - too little leaves a doorway agents refuse to path through, too much
        /// leaves a slightly wider opening.
        /// </summary>
        private const double PassageClearance = 1.2;

        private static Point2[] ToPoints(NavVertex[] vertices) {
            var result = new Point2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) result[i] = new Point2(vertices[i].X, vertices[i].Y);
            return result;
        }

        private static double LowestBaseHeightWithin(NavMeshData data, Point2[] footprint) {
            double lowest = double.MaxValue;
            foreach (NavVertex vertex in data.Vertices) {
                if (NavMeshGeometry.PointInPolygon(new Point2(vertex.X, vertex.Y), footprint)) {
                    lowest = Math.Min(lowest, vertex.Z);
                }
            }
            return lowest == double.MaxValue ? 0.0 : lowest;
        }

        private static Point2 Bilinear(Point2[] quad, double u, double v) {
            Point2 left = new Point2(quad[0].X + (quad[3].X - quad[0].X) * v,
                                     quad[0].Y + (quad[3].Y - quad[0].Y) * v);
            Point2 right = new Point2(quad[1].X + (quad[2].X - quad[1].X) * v,
                                      quad[1].Y + (quad[2].Y - quad[1].Y) * v);
            return new Point2(left.X + (right.X - left.X) * u, left.Y + (right.Y - left.Y) * u);
        }

        private static bool PointInOrNear(Point2 point, Point2[] polygon, double distance) {
            if (NavMeshGeometry.PointInPolygon(point, polygon)) return true;
            double squared = distance * distance;
            for (int i = 0; i < polygon.Length; i++) {
                Point2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double length = dx * dx + dy * dy;
                double t = length <= 0.0 ? 0.0 : Math.Max(0.0, Math.Min(1.0,
                    ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / length));
                double px = a.X + dx * t - point.X, py = a.Y + dy * t - point.Y;
                if (px * px + py * py <= squared) return true;
            }
            return false;
        }

        private static double FootprintDistance(Point2 a, Point2 b) {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// How much to offset a footprint on each retry, in metres.
        ///
        /// A sliver comes from a cutout corner landing a few centimetres from an existing navmesh
        /// vertex: the triangle between them is unavoidably a needle, whichever way the ring is
        /// triangulated. Growing the footprint slightly moves the corner away from that vertex and
        /// the problem disappears at its source rather than being patched afterwards.
        ///
        /// Growing is the safe direction. The footprint is already a building plus clearance, so a
        /// little extra cuts a little more ground out from under a solid object - which is exactly
        /// what the pass is for.
        ///
        /// The negative steps are for the opposite problem, and they matter more than they look.
        /// Cutouts are applied one after another, so a footprint that overlaps one already cut finds
        /// a corner sitting in the hole its neighbour made. That is reported as "reaches past the
        /// edge of the navmesh" - which is true of the mesh as it now stands, and completely
        /// misleading: the ground there is not missing, it is already cut. Skipping the footprint
        /// then leaves a walkable wedge exactly where two runs of fence meet, which is the one place
        /// a gap is most visible and most exploitable.
        ///
        /// Pulling the corners in until they land back on live mesh cuts the remainder. Nothing is
        /// left walkable by doing so, because whatever the shrunken footprint no longer covers was
        /// removed by the neighbour that caused the overlap.
        /// </summary>
        /// <summary>
        /// How close a shrunken corner may come to the footprint's centre, in metres. Below this the
        /// quad is too small to be worth cutting and the repair ring it needs would be slivers.
        /// </summary>
        private const double MinimumHalfDiagonal = 0.35;

        // Try the authored clearance first, then slightly wider cuts for slivers, then narrower
        // cuts for a merged wall that reaches the edge of the source mesh. The latter is normal at
        // a map boundary or where an existing elevated/navmesh section already left a gap: keeping
        // the whole connected wall walkable is never an acceptable fallback.
        private static readonly double[] RetryExpansions = {
            0.0, 0.25, 0.6, 1.0,
            -0.25, -0.6, -1.0, -1.5, -2.0
        };

        /// <summary>
        /// How many times a stubborn footprint may be halved before it is given up on. Three levels
        /// turn one footprint into at most eight pieces, which is far past the point where a piece
        /// is small enough to be the whole problem.
        /// </summary>
        private const int MaxSplitDepth = 3;

        /// <summary>A footprint shorter than this on its long side is not worth halving.</summary>
        private const double MinimumSplittableLength = 2.0;

        /// <summary>
        /// Warns when an elevated surface has no cutout under it.
        ///
        /// A raised walkway joins the ground through the hole its own structure cuts in the navmesh.
        /// With no cutout there is no open edge to land on, and the ramp fails with "none of its
        /// lower landings reaches an open navmesh edge" - a message that describes the symptom
        /// perfectly and says nothing about the cause. That one gap cost a full debugging session on
        /// a real homestead, where a substring test had been quietly dropping every wall and
        /// gatehouse before a footprint was ever built for them.
        ///
        /// Only ground-level surfaces are worth warning about: a wall walk twelve metres up is
        /// SUPPOSED to have solid ground beneath it, and its stair is a separate outline that will
        /// have its own. The test is whether any part of the surface comes near the height of its
        /// own lowest point, which is where a stair or ramp meets the ground.
        /// </summary>
        private static void WarnAboutUncutElevations(NavMeshBakeRequest request, NavMeshBakeReport report) {
            foreach (NavMeshRampPath ramp in request.Ramps) {
                if (ramp?.Outline == null || ramp.Outline.Length < 3) continue;

                double centreX = 0.0, centreY = 0.0;
                foreach (NavVertex point in ramp.Outline) { centreX += point.X; centreY += point.Y; }
                centreX /= ramp.Outline.Length;
                centreY /= ramp.Outline.Length;
                var centre = new Point2(centreX, centreY);

                bool covered = false;
                foreach (Point2[] footprint in request.Cutouts) {
                    if (footprint == null || footprint.Length < 3) continue;
                    if (NavMeshGeometry.PointInPolygon(centre, footprint)) { covered = true; break; }
                }
                if (covered) continue;

                report.Add($"'{ramp.Label}' has no cutout beneath it. A raised surface joins the "
                           + "ground through the hole its own structure makes, so if its lower end "
                           + "does not connect, mark the object below it for cutout first.");
            }
        }

        private static CutoutResult ApplyWithRetries(NavMeshData data, Point2[] footprint) =>
            Cut(data, footprint, 0);

        /// <summary>
        /// Cuts one footprint, halving it and cutting the pieces when it cannot be cut whole.
        ///
        /// Splitting exists because failures are LOCAL and footprints are not. Cutouts are applied
        /// one after another, so where two runs of fence meet, the second one's end overlaps the
        /// hole the first already made; the repair ring there is a shape the triangulator refuses,
        /// and the whole footprint is skipped. That trades a 2 m problem for 22 m of fence left
        /// walkable - which is exactly the gap that shows up in game, always at a corner.
        ///
        /// Halving isolates the bad end. The clean half cuts normally, and the piece that still
        /// fails is the piece already covered by whatever it overlapped, so nothing walkable is
        /// left behind by abandoning it.
        ///
        /// A half that succeeds is kept even when its sibling fails: a mostly-cut fence is strictly
        /// better than an uncut one, and the caller only ever sees success or failure for the
        /// footprint as a whole.
        /// </summary>
        private static CutoutResult Cut(NavMeshData data, Point2[] footprint, int depth) {
            Exception last = null;

            foreach (double expansion in RetryExpansions) {
                Point2[] attempt = expansion == 0.0 ? footprint : Expand(footprint, expansion);
                if (attempt == null) continue;   // shrunk away to nothing
                try {
                    CutoutResult cut = NavMeshCutout.Apply(
                        data, NavMeshCutout.Plan(data, attempt), validate: false);
                    cut.PiecesCut = 1;
                    return cut;
                } catch (NavMeshFormatException ex) {
                    last = ex;
                }
            }

            // A wall at the outer edge of an existing navmesh is not an enclosed hole. Trying to
            // repair a ring around it is geometrically wrong: one side of that ring is already the
            // navmesh boundary. Remove the affected patch instead. This deliberately cuts a little
            // more than the physical wall when it crosses a very large source face, but it is the
            // safe outcome: agents cannot route through a solid wall just because its footprint
            // reaches the edge of the source mesh.
            try {
                return CutToExistingBoundary(data, footprint);
            } catch (NavMeshFormatException ex) {
                last = ex;
            }

            if (depth < MaxSplitDepth && Halve(footprint, out Point2[] first, out Point2[] second)) {
                CutoutResult firstResult = null, secondResult = null;
                double abandoned = 0.0;

                try { firstResult = Cut(data, first, depth + 1); }
                catch (NavMeshFormatException) { abandoned += Area(first); }

                try { secondResult = Cut(data, second, depth + 1); }
                catch (NavMeshFormatException) { abandoned += Area(second); }

                if (firstResult != null || secondResult != null) {
                    CutoutResult combined = Combine(firstResult, secondResult);
                    combined.AbandonedArea += abandoned;
                    if (abandoned > 0.0) combined.PiecesAbandoned++;
                    return combined;
                }
            }

            throw last;
        }

        /// <summary>
        /// Removes faces touched by an obstacle that extends past the existing navmesh boundary.
        /// No repair faces are created because the removed patch is open to that boundary rather
        /// than a hole inside walkable ground.
        /// </summary>
        private static CutoutResult CutToExistingBoundary(NavMeshData data, Point2[] footprint) {
            CutoutPlan plan = NavMeshCutout.Plan(data, footprint);
            if (plan.FullyContained) {
                throw new NavMeshFormatException("the cutout is enclosed and still needs a repair ring");
            }
            if (plan.AffectedFaces == null || plan.AffectedFaces.Length == 0) {
                throw new NavMeshFormatException("the edge-touching footprint does not touch any navmesh face");
            }
            if (plan.AffectedGroups == null || plan.AffectedGroups.Length != 1) {
                int groups = plan.AffectedGroups == null ? 0 : plan.AffectedGroups.Length;
                throw new NavMeshFormatException($"the edge-touching footprint crosses {groups} face groups");
            }

            int verticesBefore = data.Vertices.Count;
            int edgesBefore = data.Edges.Count;
            int facesBefore = data.Faces.Count;
            var removed = new HashSet<int>(plan.AffectedFaces);
            var kept = new List<NavFace>(facesBefore - removed.Count);
            for (int i = 0; i < data.Faces.Count; i++) {
                if (!removed.Contains(i)) kept.Add(data.Faces[i]);
            }
            if (kept.Count == facesBefore) {
                throw new NavMeshFormatException("the edge-touching footprint did not remove any faces");
            }

            data.Faces = kept;
            return new CutoutResult {
                FacesRemoved = removed.Count,
                RepairFacesAdded = 0,
                VerticesBefore = verticesBefore,
                VerticesAfter = data.Vertices.Count,
                EdgesBefore = edgesBefore,
                EdgesAfter = data.Edges.Count,
                FacesBefore = facesBefore,
                FacesAfter = data.Faces.Count,
                PiecesCut = 1,
            };
        }

        /// <summary>
        /// Splits a footprint across the middle of its longer side.
        ///
        /// The long axis is taken from the two longest opposite edges rather than from a bounding
        /// box, so a fence run lying at an angle halves along its own length instead of along
        /// north-south.
        ///
        /// Returns false when the shape is too small to be worth halving.
        /// </summary>
        private static bool Halve(Point2[] footprint, out Point2[] first, out Point2[] second) {
            first = null;
            second = null;
            if (footprint == null || footprint.Length != 4) return false;

            // Corners are in order, so edge 0-1 and edge 2-3 are one opposite pair and 1-2 / 3-0 the
            // other. Halving means cutting both edges of the longer pair at their midpoints.
            double lengthA = Distance(footprint[0], footprint[1]) + Distance(footprint[2], footprint[3]);
            double lengthB = Distance(footprint[1], footprint[2]) + Distance(footprint[3], footprint[0]);

            if (Math.Max(lengthA, lengthB) / 2.0 < MinimumSplittableLength) return false;

            if (lengthA >= lengthB) {
                Point2 midTop = Midpoint(footprint[0], footprint[1]);
                Point2 midBottom = Midpoint(footprint[2], footprint[3]);
                first = new[] { footprint[0], midTop, midBottom, footprint[3] };
                second = new[] { midTop, footprint[1], footprint[2], midBottom };
            } else {
                Point2 midRight = Midpoint(footprint[1], footprint[2]);
                Point2 midLeft = Midpoint(footprint[3], footprint[0]);
                first = new[] { footprint[0], footprint[1], midRight, midLeft };
                second = new[] { midLeft, midRight, footprint[2], footprint[3] };
            }
            return true;
        }

        /// <summary>Area of a four-corner footprint, by the shoelace formula.</summary>
        private static double Area(Point2[] quad) {
            double sum = 0.0;
            for (int i = 0; i < quad.Length; i++) {
                Point2 a = quad[i], b = quad[(i + 1) % quad.Length];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(sum) / 2.0;
        }

        private static double Distance(Point2 a, Point2 b) {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Point2 Midpoint(Point2 a, Point2 b) =>
            new Point2((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

        /// <summary>
        /// Reports two piece-cuts as one.
        ///
        /// The before/after counts are stitched end to end - the first piece's "before" and the
        /// last piece's "after" - because the pieces were cut in sequence against the same mesh, so
        /// that pair really is the whole footprint's effect.
        /// </summary>
        private static CutoutResult Combine(CutoutResult first, CutoutResult second) {
            if (first == null) return second;
            if (second == null) return first;

            return new CutoutResult {
                FacesRemoved = first.FacesRemoved + second.FacesRemoved,
                RepairFacesAdded = first.RepairFacesAdded + second.RepairFacesAdded,
                VerticesBefore = first.VerticesBefore, VerticesAfter = second.VerticesAfter,
                EdgesBefore = first.EdgesBefore, EdgesAfter = second.EdgesAfter,
                FacesBefore = first.FacesBefore, FacesAfter = second.FacesAfter,
                TriangulationAreaError = first.TriangulationAreaError + second.TriangulationAreaError,
                PiecesCut = first.PiecesCut + second.PiecesCut,
                PiecesAbandoned = first.PiecesAbandoned + second.PiecesAbandoned,
                AbandonedArea = first.AbandonedArea + second.AbandonedArea,
            };
        }

        /// <summary>
        /// Moves a footprint's corners out from its centre by the given margin, or in when the
        /// margin is negative. Returns null when shrinking would collapse the shape, so a caller
        /// stepping through margins can simply skip that attempt.
        /// </summary>
        private static Point2[] Expand(Point2[] footprint, double margin) {
            double centreX = 0.0, centreY = 0.0;
            foreach (Point2 corner in footprint) { centreX += corner.X; centreY += corner.Y; }
            centreX /= footprint.Length;
            centreY /= footprint.Length;

            var grown = new Point2[footprint.Length];
            for (int i = 0; i < footprint.Length; i++) {
                double dx = footprint[i].X - centreX;
                double dy = footprint[i].Y - centreY;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-6) { grown[i] = footprint[i]; continue; }

                // Shrinking past the centre would fold the corner through to the far side and
                // produce a bow-tie the triangulator cannot repair. Refuse the whole attempt
                // instead, and let the caller move on to the next margin.
                double scaled = length + margin;
                if (scaled < MinimumHalfDiagonal) return null;

                grown[i] = new Point2(centreX + dx * scaled / length,
                                      centreY + dy * scaled / length);
            }
            return grown;
        }

        /// <summary>
        /// The structural check the per-cutout path used to do, run once for the whole batch.
        ///
        /// Reports rather than throws: the caller's answer to a bad batch is to keep the original
        /// mesh, which is a better outcome than losing the bake and the reason for it together.
        /// </summary>
        private static bool Check(NavMeshSummary before, NavMeshData working, NavMeshBakeReport report) {
            NavMeshSummary after = NavMeshValidation.Summarize(working);

            if (!after.IsStructurallySound) {
                report.Add("Discarded every cutout: the result was not a sound navmesh - " + after.Describe());
                return false;
            }
            if (after.ConnectedFaceComponents != before.ConnectedFaceComponents) {
                report.Add($"Discarded every cutout: the buildings split the walkable ground into "
                           + $"{after.ConnectedFaceComponents} pieces (was {before.ConnectedFaceComponents}); "
                           + "agents on one piece could never reach the others");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Bakes a navmesh file in place, keeping a backup first.
        /// </summary>
        /// <param name="path">The scene's navmesh.bin.</param>
        public static NavMeshBakeReport BakeFile(string path, NavMeshBakeRequest request) =>
            BakeFile(path, path, request);

        /// <summary>
        /// Bakes <paramref name="sourcePath"/> and writes the result to <paramref name="outputPath"/>.
        ///
        /// The file is written through a temporary beside the destination and moved into place, so an
        /// interrupted bake cannot leave a half-written navmesh where the game will try to read one.
        /// </summary>
        public static NavMeshBakeReport BakeFile(string sourcePath, string outputPath,
                                                 NavMeshBakeRequest request) {
            if (string.IsNullOrEmpty(sourcePath)) throw new ArgumentNullException("sourcePath");
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentNullException("outputPath");
            if (!File.Exists(sourcePath)) {
                throw new NavMeshFormatException("no navmesh at " + sourcePath);
            }

            byte[] container = File.ReadAllBytes(sourcePath);
            request.Progress?.Invoke(1, 100);
            NavMeshData data = NavMeshData.Parse(container);

            NavMeshBakeReport report = Bake(data, request);
            report.OutputPath = outputPath;
            if (!report.Changed) {
                request.Progress?.Invoke(100, 100);
                return report;
            }

            // OutputSignature, not Signature: an NMG7 source is read-only and must go out as
            // NMG9. Everything else round-trips as the format it arrived in.
            byte[] baked = data.Serialize(data.OutputSignature);

            // Read the candidate back before it goes anywhere near the destination. A navmesh that
            // cannot be parsed does not crash the game, it just breaks pathfinding invisibly, so the
            // decode has to happen here where it can still be refused.
            NavMeshSummary verified = NavMeshValidation.Summarize(NavMeshData.Parse(baked));
            if (!verified.IsStructurallySound) {
                throw new NavMeshFormatException(
                    "the baked navmesh did not survive being read back: " + verified.Describe());
            }

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) {
                Directory.CreateDirectory(directory);
            }

            if (request.KeepBackup && File.Exists(outputPath)) {
                string backup = outputPath + ".prebake";
                if (!File.Exists(backup)) {
                    File.Copy(outputPath, backup);
                    report.BackupPath = backup;
                    report.Add("Kept the original navmesh as " + Path.GetFileName(backup) + ".");
                } else {
                    report.BackupPath = backup;
                    report.Add("An earlier original is already kept as " + Path.GetFileName(backup) + ".");
                }
            }

            string temporary = outputPath + ".baking";
            File.WriteAllBytes(temporary, baked);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(temporary, outputPath);
            request.Progress?.Invoke(100, 100);

            report.Add($"Wrote {baked.Length:N0} bytes to {outputPath}.");
            return report;
        }

        /// <summary>
        /// Restores the backup a previous bake kept.
        /// </summary>
        /// <returns>True when a backup was found and put back.</returns>
        public static bool RestoreBackup(string path) {
            string backup = path + ".prebake";
            if (!File.Exists(backup)) return false;

            File.Copy(backup, path, true);
            File.Delete(backup);
            return true;
        }
    }
}
