using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CustomSceneCreator.NavMesh;

namespace NavMeshPortTests {

    /// <summary>
    /// Checks the C# navmesh port against real game files.
    ///
    /// The test that matters is EXACT ROUND-TRIP: read a real navmesh, serialize it back, and require
    /// the bytes to be identical. Anything misread - a field width, an array order, the tail - shows
    /// up immediately as a mismatch rather than as a subtly wrong mesh discovered in game an hour
    /// later.
    ///
    /// Usage:
    ///   dotnet run --project tools/NavMeshPortTests            (scans the default install)
    ///   dotnet run --project tools/NavMeshPortTests -- FILE... (specific files)
    /// </summary>
    internal static class Program {

        private static int _passed;
        private static int _failed;

        private static int Main(string[] args) {
            if (args.Length > 0 && args[0] == "--cutout") {
                return RunCutout(args);
            }
            if (args.Length > 0 && args[0] == "--addition") {
                return RunAddition(args);
            }
            if (args.Length > 2 && args[0] == "--project") {
                return RunProject(args);
            }
            if (args.Length > 1 && args[0] == "--merge") {
                // Prints the combined outline for a manifest's footprints, without touching a
                // navmesh. Tracing a union is the part most likely to be subtly wrong, and it is
                // far easier to see wrong here than as a missing cutout half a bake later.
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(args[1]).TrimStart('﻿'));
                var input = new List<Point2[]>();
                foreach (JsonElement cutout in document.RootElement.GetProperty("Cutouts").EnumerateArray()) {
                    JsonElement corners = cutout.GetProperty("Corners");
                    int count = corners.GetArrayLength() / 3;
                    var polygon = new Point2[count];
                    for (int c = 0; c < count; c++) {
                        polygon[c] = new Point2(corners[c * 3].GetDouble(), corners[c * 3 + 1].GetDouble());
                    }
                    input.Add(polygon);
                }

                NavMeshFootprintMerge.MergeResult merge = NavMeshFootprintMerge.Merge(input);
                Console.WriteLine($"{input.Count} footprint(s) -> {merge.Footprints.Count}; {merge.Describe()}");
                foreach (string rejected in merge.Rejected) Console.WriteLine("  rejected: " + rejected);

                foreach (Point2[] outline in merge.Footprints) {
                    Console.WriteLine($"  outline: {outline.Length} corners, "
                                      + $"{Math.Abs(NavMeshFootprintMerge.SignedArea(outline)):0.##} m2");
                    foreach (Point2 corner in outline) {
                        Console.WriteLine($"     ({corner.X:0.##}, {corner.Y:0.##})");
                    }
                }
                return 0;
            }
            if (args.Length > 0 && args[0] == "--ramp-self-test") {
                return RunRampSelfTest();
            }
            if (args.Length > 2 && args[0] == "--bake") {
                return RunBake(args);
            }
            if (args.Length > 1 && args[0] == "--audit") {
                // Structural read-out for one file, so a baked mesh can be compared against the stock
                // one it came from field by field rather than only "does it parse".
                foreach (string file in new List<string>(args).GetRange(1, args.Length - 1)) {
                    NavMeshData audited = NavMeshData.Parse(File.ReadAllBytes(file));
                    NavMeshAuditReport report = NavMeshAudit.Audit(audited);

                    Console.WriteLine($"{Path.GetFileName(Path.GetDirectoryName(file))}");
                    Console.WriteLine("  " + report.Summary.Describe());
                    Console.WriteLine($"  walkable={report.WalkableArea:N0}m2 islands={report.IslandSizes.Count} "
                                      + $"degrees={DescribeDegrees(audited)} minFace={MinimumFaceArea(audited):0.###}m2");
                }
                return 0;
            }
            if (args.Length == 3 && args[0] == "--elevated-diff") {
                return RunElevatedDiff(args[1], args[2]);
            }
            if (args.Length > 2 && args[0] == "--recompress") {
                // Writes a re-wrapped copy so its bytes can be compared against the Python writer's.
                // Matching hashes are what prove the C# LZ4 encoder is the same encoder, which is the
                // only part of the pipeline the game has already accepted output from.
                NavMeshData source = NavMeshData.Parse(File.ReadAllBytes(args[1]));
                byte[] encoded = source.Serialize(source.OutputSignature);
                File.WriteAllBytes(args[2], encoded);
                Console.WriteLine($"{encoded.Length} bytes  sha256={Sha256(encoded)}");
                return 0;
            }

            List<string> files = args.Length > 0 ? new List<string>(args) : FindNavMeshes();

            if (files.Count == 0) {
                Console.WriteLine("No navmesh.bin files found. Pass paths explicitly.");
                return 1;
            }

            Console.WriteLine($"Checking {files.Count} navmesh file(s)\n");

            foreach (string file in files) {
                try {
                    Check(file);
                } catch (Exception ex) {
                    _failed++;
                    Console.WriteLine($"  FAIL  {Path.GetFileName(Path.GetDirectoryName(file))}");
                    Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"\npassed={_passed} failed={_failed}");
            return _failed == 0 ? 0 : 1;
        }

        private static double MinimumFaceArea(NavMeshData data) {
            double minimum = double.MaxValue;
            foreach (NavFace face in data.Faces) {
                double twice = 0.0;
                for (int i = 0; i < face.Vertices.Length; i++) {
                    NavVertex a = data.Vertices[(int)face.Vertices[i]];
                    NavVertex b = data.Vertices[(int)face.Vertices[(i + 1) % face.Vertices.Length]];
                    twice += a.X * b.Y - b.X * a.Y;
                }
                minimum = Math.Min(minimum, Math.Abs(twice) * 0.5);
            }
            return minimum == double.MaxValue ? 0.0 : minimum;
        }

        private static void Check(string path) {
            byte[] container = File.ReadAllBytes(path);
            string label = Path.GetFileName(Path.GetDirectoryName(path)) ?? path;

            byte[] raw = NavMeshContainer.Unwrap(container, out NavMeshWrapper wrapper);

            var data = new NavMeshData { OriginalContainer = container, Wrapper = wrapper };
            data.ParseRaw(raw);

            // 1. The decoded stream must serialize back to exactly what was decoded.
            byte[] rebuiltRaw = data.SerializeRaw(data.Signature);
            if (!Same(raw, rebuiltRaw)) {
                throw new Exception($"raw round-trip differs ({raw.Length} vs {rebuiltRaw.Length} bytes, "
                                    + $"first difference at {FirstDifference(raw, rebuiltRaw)})");
            }

            // 2. Re-wrapping and re-reading must yield the same decoded stream. The container is NOT
            //    compared byte for byte: our LZ4 encoder is not the game's, so a different but valid
            //    compression of identical content is expected and fine.
            byte[] rewrapped = NavMeshContainer.Wrap(rebuiltRaw, container, wrapper);
            byte[] reread = NavMeshContainer.Unwrap(rewrapped, out NavMeshWrapper rewrapper);
            if (rewrapper != wrapper) throw new Exception("wrapper changed on re-read");
            if (!Same(raw, reread)) throw new Exception("re-wrapped file did not decode to the same bytes");

            _passed++;
            double ratio = wrapper == NavMeshWrapper.Rnm1
                ? 100.0 * rewrapped.Length / container.Length
                : 100.0;

            Console.WriteLine($"  ok    {label,-34} {data.Signature} {wrapper,-14} "
                              + $"v={data.Vertices.Count,6} e={data.Edges.Count,6} f={data.Faces.Count,6} "
                              + $"tail={data.GlobalTail.Length,4}B  size={ratio:F0}%");
        }

        /// <summary>
        /// Runs the cutout pass against a real navmesh and a real CSC manifest.
        ///
        /// The point is to compare the numbers against what the Python writer recorded for the same
        /// inputs. Face and vertex counts matching exactly means the port reproduces the geometry
        /// decisions, not merely something that parses.
        ///
        ///   --cutout NAVMESH MANIFEST [CUTOUT_INDEX | all]
        /// </summary>
        private static int RunCutout(string[] args) {
            if (args.Length < 3) {
                Console.WriteLine("usage: --cutout NAVMESH MANIFEST [INDEX|all]");
                return 1;
            }

            string navmeshPath = args[1];
            string manifestPath = args[2];
            string selection = args.Length > 3 ? args[3] : "0";

            using JsonDocument manifest = JsonDocument.Parse(
                File.ReadAllText(manifestPath).TrimStart('﻿'));
            JsonElement cutouts = manifest.RootElement.GetProperty("Cutouts");

            var indices = new List<int>();
            if (selection == "all") {
                for (int i = 0; i < cutouts.GetArrayLength(); i++) indices.Add(i);
            } else {
                indices.Add(int.Parse(selection));
            }

            byte[] container = File.ReadAllBytes(navmeshPath);
            NavMeshData data = NavMeshData.Parse(container);
            NavMeshSummary start = NavMeshValidation.Summarize(data);
            Console.WriteLine($"source  {Path.GetFileName(Path.GetDirectoryName(navmeshPath))}");
            Console.WriteLine($"        {start.Describe()}\n");

            int applied = 0, refused = 0;
            var footprints = new List<Point2[]>();

            foreach (int index in indices) {
                JsonElement corners = cutouts[index].GetProperty("Corners");
                var footprint = new Point2[4];
                for (int c = 0; c < 4; c++) {
                    footprint[c] = new Point2(
                        corners[c * 3].GetDouble(), corners[c * 3 + 1].GetDouble());
                }
                string prefab = cutouts[index].TryGetProperty("Prefab", out JsonElement name)
                    ? name.GetString() ?? "?"
                    : "?";
                footprints.Add(footprint);

                try {
                    CutoutPlan plan = NavMeshCutout.Plan(data, footprint);
                    if (!plan.CanApply) {
                        refused++;
                        Console.WriteLine($"  skip  [{index}] {prefab,-28} {plan.BlockingReason}");
                        continue;
                    }

                    CutoutResult result = NavMeshCutout.Apply(data, plan);
                    applied++;
                    Console.WriteLine($"  cut   [{index}] {prefab,-28} {result.Describe()}");
                } catch (Exception ex) {
                    refused++;
                    Console.WriteLine($"  skip  [{index}] {prefab,-28} {ex.Message}");
                }
            }

            // The written file has to survive an independent decode, exactly as the Python writer
            // re-reads its own output before leaving it on disk.
            byte[] rebuilt = data.Serialize("NMG9");
            var reread = NavMeshData.Parse(rebuilt);
            NavMeshSummary end = NavMeshValidation.Summarize(reread);

            ReportAudit(reread, footprints, null);
            Console.WriteLine($"\nresult  {end.Describe()}");
            Console.WriteLine($"        applied={applied} refused={refused} "
                              + $"reread={(end.Describe() == NavMeshValidation.Summarize(data).Describe() ? "matches" : "DIFFERS")}");
            return end.IsStructurallySound
                   && end.ConnectedFaceComponents == start.ConnectedFaceComponents ? 0 : 1;
        }

        /// <summary>
        /// Runs the addition pass against a real navmesh and the marked areas in a real manifest.
        ///
        /// The result to watch is the component count. Added ground that does not reduce or hold the
        /// component count has not actually joined the mesh, and agents would never path onto it.
        ///
        ///   --addition NAVMESH MANIFEST
        /// </summary>
        private static int RunAddition(string[] args) {
            if (args.Length < 3) {
                Console.WriteLine("usage: --addition NAVMESH MANIFEST");
                return 1;
            }

            using JsonDocument manifest = JsonDocument.Parse(
                File.ReadAllText(args[2]).TrimStart('﻿'));

            List<NavMeshRequiredArea> areas = ReadAreas(manifest.RootElement.GetProperty("RequiredAreas"));

            NavMeshData data = NavMeshData.Parse(File.ReadAllBytes(args[1]));
            NavMeshSummary start = NavMeshValidation.Summarize(data);
            Console.WriteLine($"source  {Path.GetFileName(Path.GetDirectoryName(args[1]))}  ({areas.Count} marked area(s))");
            Console.WriteLine($"        {start.Describe()}\n");

            AdditionResult result = NavMeshAddition.Apply(data, areas);
            foreach (AdditionOperation operation in result.Operations) {
                Console.WriteLine($"  add   cluster {operation.ClusterId} of {operation.Areas.Length} area(s): "
                                  + $"hull={operation.EnvelopeVertices} faces={operation.FacesAdded} "
                                  + $"area={operation.Area:F1}m2 bridgeEdge={operation.BridgeEdge}");
            }

            byte[] rebuilt = data.Serialize("NMG9");
            NavMeshSummary end = NavMeshValidation.Summarize(NavMeshData.Parse(rebuilt));

            Console.WriteLine($"\n{result.Describe()}");
            Console.WriteLine($"reread  {end.Describe()}");
            ReportAudit(data, null, areas);
            return end.IsStructurallySound && end.NonManifoldEdges == 0 ? 0 : 1;
        }

        /// <summary>
        /// Small deterministic topology test for an elevated strip. It deliberately has open
        /// landing edges at y=0 and y=10, as a ramp must share a real edge with both platforms.
        /// This keeps the core geometry regression under the fast desktop harness; the separate
        /// editor work is responsible for opening/splicing those landing seams on ordinary maps.
        /// </summary>
        private static int RunRampSelfTest() {
            var data = new NavMeshData();
            var edges = new Dictionary<long, int>();

            int b0 = AddVertex(data, 0f, 0f, 0f);
            int b1 = AddVertex(data, 2f, 0f, 0f);
            int b2 = AddVertex(data, 2f, -3f, 0f);
            int b3 = AddVertex(data, 0f, -3f, 0f);
            AddFace(data, edges, new[] { b0, b1, b2, b3 });

            int t0 = AddVertex(data, 0f, 10f, 5f);
            int t1 = AddVertex(data, 0f, 13f, 5f);
            int t2 = AddVertex(data, 2f, 13f, 5f);
            int t3 = AddVertex(data, 2f, 10f, 5f);
            AddFace(data, edges, new[] { t0, t1, t2, t3 });

            var ramp = new NavMeshRampPath {
                Label = "self-test ramp",
                Left = new[] { new NavVertex(0f, 0f, 0f), new NavVertex(0f, 10f, 5f) },
                Right = new[] { new NavVertex(2f, 0f, 0f), new NavVertex(2f, 10f, 5f) },
            };

            var progressSamples = new List<int>();
            RampResult result = NavMeshRamp.Apply(data, new[] { ramp },
                (done, total) => progressSamples.Add(total == 0 ? 0 : done * 100 / total));
            NavMeshSummary summary = NavMeshValidation.Summarize(data);
            bool ok = result.RampsBuilt == 1 && result.BottomsJoined == 1 && result.TopsJoined == 1
                      && result.FacesAdded == 1 && summary.IsStructurallySound
                      && summary.ConnectedFaceComponents == 1
                      && progressSamples.Count >= 2 && progressSamples[0] == 0
                      && progressSamples[progressSamples.Count - 1] == 100;

            // The realistic case: the two landings are inside ordinary ground faces, so no existing
            // boundary edge matches the ramp. The baker must cut two small mouths, then make the
            // ramp share their newly-open edges. A middle pair keeps those two landing mouths apart.
            var spliced = new NavMeshData();
            var spliceEdges = new Dictionary<long, int>();
            int s0 = AddVertex(spliced, -2f, -3f, 0f);
            int s1 = AddVertex(spliced, 4f, -3f, 0f);
            int s2 = AddVertex(spliced, 4f, 3f, 0f);
            int s3 = AddVertex(spliced, -2f, 3f, 0f);
            AddFace(spliced, spliceEdges, new[] { s0, s1, s2, s3 });
            int e0 = AddVertex(spliced, -2f, 7f, 5f);
            int e1 = AddVertex(spliced, 4f, 7f, 5f);
            int e2 = AddVertex(spliced, 4f, 13f, 5f);
            int e3 = AddVertex(spliced, -2f, 13f, 5f);
            AddFace(spliced, spliceEdges, new[] { e0, e1, e2, e3 });
            var embedded = new NavMeshRampPath {
                Label = "embedded landing self-test",
                Left = new[] {
                    new NavVertex(0f, 0f, 0f), new NavVertex(0f, 2f, 1f),
                    new NavVertex(0f, 8f, 4f), new NavVertex(0f, 10f, 5f),
                },
                Right = new[] {
                    new NavVertex(2f, 0f, 0f), new NavVertex(2f, 2f, 1f),
                    new NavVertex(2f, 8f, 4f), new NavVertex(2f, 10f, 5f),
                },
            };
            RampResult spliceResult = NavMeshRamp.Apply(spliced, new[] { embedded });
            NavMeshSummary spliceSummary = NavMeshValidation.Summarize(spliced);
            bool spliceOk = spliceResult.RampsBuilt == 1 && spliceResult.BottomsJoined == 1
                            && spliceResult.TopsJoined == 1 && spliceResult.LandingOpenings == 2
                            && spliceResult.FacesAdded == 3 && spliceSummary.IsStructurallySound
                            && spliceSummary.ConnectedFaceComponents == 1;

            // A building cutout can leave a short boundary edge near a physically wider stair.
            // Reusing that edge directly pinches the authored stair mouth and makes groups queue at
            // the first tread even though the face graph is technically connected.  The outline
            // builder must keep the two-metre authored lip and taper to the shorter base edge.
            var pinched = new NavMeshData();
            var pinchEdges = new Dictionary<long, int>();
            int p0 = AddVertex(pinched, 0.4f, 0f, 0f);
            int p1 = AddVertex(pinched, 1.6f, 0f, 0f);
            int p2 = AddVertex(pinched, 1.6f, -3f, 0f);
            int p3 = AddVertex(pinched, 0.4f, -3f, 0f);
            AddFace(pinched, pinchEdges, new[] { p0, p1, p2, p3 });
            var wideOutline = new NavMeshRampPath {
                Label = "wide landing self-test",
                Outline = new[] {
                    new NavVertex(0f, 1f, 0f), new NavVertex(2f, 1f, 0f),
                    new NavVertex(2f, 6f, 3f), new NavVertex(0f, 6f, 3f),
                },
            };
            RampResult pinchResult = NavMeshRamp.Apply(pinched, new[] { wideOutline });
            NavMeshSummary pinchSummary = NavMeshValidation.Summarize(pinched);
            bool retainedWideLip = false;
            foreach (NavEdge edge in pinched.Edges) {
                NavVertex a = pinched.Vertices[edge.B];
                NavVertex b = pinched.Vertices[edge.C];
                if (Math.Abs(a.Y - 1f) > 0.01 || Math.Abs(b.Y - 1f) > 0.01) continue;
                double dx = a.X - b.X, dy = a.Y - b.Y;
                if (Math.Sqrt(dx * dx + dy * dy) >= 1.99) retainedWideLip = true;
            }
            bool pinchOk = pinchResult.RampsBuilt == 1 && pinchResult.BottomsJoined == 1
                           && pinchResult.LandingOpenings == 1 && retainedWideLip
                           && pinchSummary.IsStructurallySound
                           && pinchSummary.ConnectedFaceComponents == 1;

            ok &= spliceOk && pinchOk;
            Console.WriteLine($"ramp-self-test {(ok ? "passed" : "FAILED")}: {result.Describe()}; {summary.Describe()}");
            Console.WriteLine($"ramp-landing-seam {(spliceOk ? "passed" : "FAILED")}: {spliceResult.Describe()}; {spliceSummary.Describe()}");
            Console.WriteLine($"ramp-width-retention {(pinchOk ? "passed" : "FAILED")}: {pinchResult.Describe()}; {pinchSummary.Describe()}");
            return ok ? 0 : 1;
        }

        private static int AddVertex(NavMeshData data, float x, float y, float z) {
            int index = data.Vertices.Count;
            data.Vertices.Add(new NavVertex(x, y, z));
            return index;
        }

        private static void AddFace(NavMeshData data, Dictionary<long, int> lookup, int[] vertices) {
            var faceVertices = new uint[vertices.Length];
            var faceEdges = new uint[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) {
                int a = vertices[i], b = vertices[(i + 1) % vertices.Length];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!lookup.TryGetValue(key, out int edge)) {
                    edge = data.Edges.Count;
                    lookup[key] = edge;
                    data.Edges.Add(new NavEdge(-1, a, b, -1, -1, 0));
                }
                faceVertices[i] = (uint)a;
                faceEdges[i] = (uint)edge;
            }
            data.Faces.Add(new NavFace(faceVertices, faceEdges, new[] { 2, 0, 0, 0, 15 }, 0));
        }

        /// <summary>
        /// Prints the whole-mesh audit, which is the check that a bake actually did what was asked.
        ///
        /// Counts alone cannot tell the difference between a cutout that removed the right faces and
        /// one that removed some faces and left the ground under the building walkable.
        /// </summary>
        private static void ReportAudit(NavMeshData data, List<Point2[]> cutouts,
                                        List<NavMeshRequiredArea> additions,
                                        List<NavMeshRampPath> ramps = null) {
            NavMeshAuditReport audit = ramps == null
                ? NavMeshAudit.Audit(data, cutouts, additions)
                : NavMeshAudit.Audit(data, cutouts, additions, ramps);
            Console.WriteLine($"\naudit   {audit.Headline}");

            // Printed because an edit that invents a face-metadata pattern the engine has never
            // shipped is a file it refuses to load, silently. Any tuple here that is not also in the
            // source mesh is a bug, however healthy the topology looks.
            Console.WriteLine($"        metadata={DescribeMetadata(data)}");
            Console.WriteLine($"        walkable={audit.WalkableArea:N0}m2 islands={audit.IslandSizes.Count}");

            int shown = 0;
            foreach (NavMeshFinding finding in audit.Findings) {
                Console.WriteLine("        " + finding);
                if (++shown >= 12) {
                    Console.WriteLine($"        ... and {audit.Findings.Count - shown} more");
                    break;
                }
            }
        }

        /// <summary>
        /// Runs a full bake - cutouts then additions - exactly as saving in the editor does.
        ///
        /// The separate cutout and addition modes each test one pass in isolation. This is the order
        /// the game actually uses, and order matters: additions run on a mesh a cutout has already
        /// changed, which is where a pass that works alone can still fail in place.
        ///
        ///   --bake NAVMESH MANIFEST
        /// </summary>
        /// <summary>
        /// Bakes a real Custom Scene Creator project file against a real navmesh, and reports where
        /// the time went.
        ///
        /// The manifest format the other modes take is a convenience for hand-written tests. A whole
        /// homestead is thirty cutouts and a dozen elevated areas authored in game, and the failures
        /// that matter only appear at that size - so this reads the file the editor actually wrote,
        /// with no translation step in between to be wrong.
        /// </summary>
        private static int RunProject(string[] args) {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(args[2]).TrimStart('﻿'));
            JsonElement root = document.RootElement;

            var request = new NavMeshBakeRequest { KeepBackup = false };

            if (root.TryGetProperty("NavMeshCutouts", out JsonElement cutouts)) {
                foreach (JsonElement cutout in cutouts.EnumerateArray()) {
                    if (!cutout.TryGetProperty("Corners", out JsonElement corners)) continue;
                    int count = corners.GetArrayLength() / 3;
                    if (count < 3) continue;
                    var footprint = new Point2[count];
                    for (int c = 0; c < count; c++) {
                        footprint[c] = new Point2(corners[c * 3].GetDouble(), corners[c * 3 + 1].GetDouble());
                    }
                    request.Cutouts.Add(footprint);
                }
            }

            if (root.TryGetProperty("NavMeshRamps", out JsonElement ramps)) {
                foreach (JsonElement ramp in ramps.EnumerateArray()) {
                    if (!ramp.TryGetProperty("Outline", out JsonElement outline)) continue;
                    int count = outline.GetArrayLength() / 3;
                    if (count < 4) continue;

                    var vertices = new NavVertex[count];
                    for (int c = 0; c < count; c++) {
                        vertices[c] = new NavVertex(
                            (float)outline[c * 3].GetDouble(),
                            (float)outline[c * 3 + 1].GetDouble(),
                            (float)outline[c * 3 + 2].GetDouble());
                    }
                    request.Ramps.Add(new NavMeshRampPath {
                        Label = ramp.TryGetProperty("Label", out JsonElement label)
                            ? label.GetString() : "Ramp",
                        Outline = vertices,
                    });
                }
            }

            NavMeshData data = NavMeshData.Parse(File.ReadAllBytes(args[1]));
            Console.WriteLine($"{Path.GetFileName(args[2])}: {request.Cutouts.Count} cutout(s), "
                              + $"{request.Ramps.Count} elevated area(s) on {data.Faces.Count:N0} faces");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            NavMeshBakeReport report = NavMeshBaker.Bake(data, request);
            clock.Stop();

            foreach (string line in report.Log) Console.WriteLine("  " + line);
            Console.WriteLine();
            Console.WriteLine($"  {report.Headline}");
            Console.WriteLine($"  TOTAL {clock.ElapsedMilliseconds:N0} ms");
            return 0;
        }

        private static int RunBake(string[] args) {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(args[2]).TrimStart('﻿'));
            JsonElement root = manifest.RootElement;
            bool withoutCutouts = Array.Exists(args, value => value == "--without-cutouts");
            string rampLabel = null;
            for (int argument = 0; argument + 1 < args.Length; argument++) {
                if (args[argument] == "--ramp-label") rampLabel = args[argument + 1];
            }

            var request = new NavMeshBakeRequest();
            // Accept both the compact portable manifest and a saved CSC project.  The latter keeps
            // a reported in-game bake reproducible without hand-copying its authoring data.
            if (!withoutCutouts && TryGetArray(root, "Cutouts", "NavMeshCutouts", out JsonElement cutouts)) {
                foreach (JsonElement cutout in cutouts.EnumerateArray()) {
                    JsonElement corners = cutout.GetProperty("Corners");
                    var footprint = new Point2[4];
                    for (int c = 0; c < 4; c++) {
                        footprint[c] = new Point2(
                            corners[c * 3].GetDouble(), corners[c * 3 + 1].GetDouble());
                    }
                    request.Cutouts.Add(footprint);
                }
            }
            if (TryGetArray(root, "RequiredAreas", "NavMeshRequirements", out JsonElement areas)) {
                request.Additions.AddRange(ReadAreas(areas));
            }
            if (TryGetArray(root, "Ramps", "NavMeshRamps", out JsonElement ramps)) {
                foreach (JsonElement ramp in ramps.EnumerateArray()) {
                    if (ramp.TryGetProperty("IsDraft", out JsonElement draft) && draft.GetBoolean()) continue;
                    if (!ramp.TryGetProperty("Outline", out JsonElement outlineJson)) continue;
                    string labelText = ramp.TryGetProperty("Label", out JsonElement label)
                        ? label.GetString() ?? "Elevated area"
                        : "Elevated area";
                    if (rampLabel != null && !string.Equals(labelText, rampLabel, StringComparison.OrdinalIgnoreCase)) continue;
                    int pointCount = outlineJson.GetArrayLength() / 3;
                    if (pointCount < 4) continue;
                    var outline = new NavVertex[pointCount];
                    for (int point = 0; point < pointCount; point++) {
                        outline[point] = new NavVertex(outlineJson[point * 3].GetSingle(),
                            outlineJson[point * 3 + 1].GetSingle(), outlineJson[point * 3 + 2].GetSingle());
                    }
                    request.Ramps.Add(new NavMeshRampPath {
                        Label = labelText,
                        Outline = outline,
                    });
                }
            }

            NavMeshData data = NavMeshData.Parse(File.ReadAllBytes(args[1]));
            Console.WriteLine($"source  {Path.GetFileName(Path.GetDirectoryName(args[1]))}  "
                              + $"({request.Cutouts.Count} footprint(s), {request.Additions.Count} marked area(s), {request.Ramps.Count} elevated outline(s))");

            // Timed because this runs on the main thread as the player leaves a scene: anything over
            // a moment reads as the game having frozen, which is exactly how the first version was
            // reported.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            NavMeshBakeReport report = NavMeshBaker.Bake(data, request);
            clock.Stop();

            foreach (string line in report.Log) Console.WriteLine("  " + line);
            Console.WriteLine($"\nbake took {clock.ElapsedMilliseconds} ms "
                              + $"({request.Cutouts.Count} footprint(s), {data.Faces.Count:N0} faces)");
            Console.WriteLine($"\n{report.Headline}");

            if (report.RampsSkipped > 0) PrintNearestMeshVertices(data, request.Ramps);

            ReportAudit(data, request.Cutouts, request.Additions, request.Ramps);

            if (Array.Exists(args, value => value == "--debug-disconnected") && request.Ramps.Count > 0) {
                int originalFaces = data.Faces.Count;
                try {
                    NavMeshRamp.Apply(data, request.Ramps);
                } catch (Exception ex) {
                    Console.WriteLine("\ndisconnected elevated diagnostic: " + ex.Message);
                    ReportGeneratedFaceReachability(data, originalFaces);
                }
            }

            // Optional fourth argument: write the baked file, so a candidate can be dropped into a
            // scene folder and actually loaded by the game. Reasoning about a mesh only goes so far.
            if (args.Length > 3 && report.Changed) {
                File.WriteAllBytes(args[3], data.Serialize(data.OutputSignature));
                Console.WriteLine($"wrote   {args[3]}");
            }
            return report.After.IsStructurallySound ? 0 : 1;
        }

        private static void ReportGeneratedFaceReachability(NavMeshData data, int originalFaceCount) {
            var owners = new Dictionary<uint, List<int>>();
            for (int face = 0; face < data.Faces.Count; face++) {
                foreach (uint edge in data.Faces[face].Edges) {
                    if (!owners.TryGetValue(edge, out List<int> list)) owners[edge] = list = new List<int>();
                    list.Add(face);
                }
            }
            var queue = new Queue<int>();
            var reached = new bool[data.Faces.Count];
            for (int face = 0; face < originalFaceCount && face < reached.Length; face++) {
                reached[face] = true;
                queue.Enqueue(face);
            }
            while (queue.Count > 0) {
                int face = queue.Dequeue();
                foreach (uint edge in data.Faces[face].Edges) {
                    foreach (int neighbour in owners[edge]) {
                        if (reached[neighbour]) continue;
                        reached[neighbour] = true;
                        queue.Enqueue(neighbour);
                    }
                }
            }
            int connected = 0, floating = 0;
            for (int face = originalFaceCount; face < data.Faces.Count; face++) {
                NavFace generated = data.Faces[face];
                double x = 0, y = 0, z = 0;
                foreach (uint vertex in generated.Vertices) {
                    NavVertex point = data.Vertices[(int)vertex];
                    x += point.X; y += point.Y; z += point.Z;
                }
                int count = generated.Vertices.Length;
                if (reached[face]) connected++; else floating++;
                Console.WriteLine($"  generated face {face}: {(reached[face] ? "CONNECTED" : "FLOATING"),9} "
                                  + $"degree {count} center ({x / count:0.0},{y / count:0.0},{z / count:0.0})");
            }
            Console.WriteLine($"  generated reachability: {connected} connected, {floating} floating");
        }

        private static bool TryGetArray(JsonElement root, string manifestName, string projectName,
                                        out JsonElement value) {
            if (root.TryGetProperty(manifestName, out value) && value.ValueKind == JsonValueKind.Array) return true;
            return root.TryGetProperty(projectName, out value) && value.ValueKind == JsonValueKind.Array;
        }

        private static void PrintNearestMeshVertices(NavMeshData data, List<NavMeshRampPath> ramps) {
            foreach (NavMeshRampPath ramp in ramps) {
                if (!ramp.HasOutline) continue;
                double minimum = double.MaxValue;
                for (int i = 0; i < ramp.Outline.Length; i++) minimum = Math.Min(minimum, ramp.Outline[i].Z);
                for (int i = 0; i < ramp.Outline.Length; i++) {
                    NavVertex point = ramp.Outline[i];
                    if (point.Z > minimum + 1.0) continue;
                    double bestXy = double.MaxValue, best3d = double.MaxValue;
                    NavVertex nearestXy = new NavVertex(), nearest3d = new NavVertex();
                    foreach (NavVertex vertex in data.Vertices) {
                        double dx = vertex.X - point.X, dy = vertex.Y - point.Y, dz = vertex.Z - point.Z;
                        double xy = Math.Sqrt(dx * dx + dy * dy);
                        double all = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (xy < bestXy) { bestXy = xy; nearestXy = vertex; }
                        if (all < best3d) { best3d = all; nearest3d = vertex; }
                    }
                    Console.WriteLine($"  low corner ({point.X:0.0},{point.Y:0.0},{point.Z:0.0}): nearest XY {bestXy:0.0}m at ({nearestXy.X:0.0},{nearestXy.Y:0.0},{nearestXy.Z:0.0}), nearest 3D {best3d:0.0}m");
                }
            }
        }

        private static List<NavMeshRequiredArea> ReadAreas(JsonElement areas) {
            var result = new List<NavMeshRequiredArea>();

            foreach (JsonElement entry in areas.EnumerateArray()) {
                JsonElement position = entry.GetProperty("Pos");
                var area = new NavMeshRequiredArea {
                    Id = entry.TryGetProperty("Id", out JsonElement id) ? id.GetString() : "",
                    Label = entry.TryGetProperty("Label", out JsonElement label) ? label.GetString() : "Navmesh needed",
                    Center = new NavVertex(
                        position[0].GetSingle(), position[1].GetSingle(), position[2].GetSingle()),
                    Radius = entry.TryGetProperty("Radius", out JsonElement radius) ? radius.GetDouble() : 4.0,
                };

                if (entry.TryGetProperty("Boundary", out JsonElement boundary)) {
                    int count = boundary.GetArrayLength() / 3;
                    area.Boundary = new NavVertex[count];
                    for (int i = 0; i < count; i++) {
                        area.Boundary[i] = new NavVertex(
                            boundary[i * 3].GetSingle(),
                            boundary[i * 3 + 1].GetSingle(),
                            boundary[i * 3 + 2].GetSingle());
                    }
                }
                result.Add(area);
            }
            return result;
        }

        /// <summary>Face metadata tuples by count, e.g. "(2,0,0,0,15)x51250".</summary>
        private static string DescribeMetadata(NavMeshData data) {
            var counts = new Dictionary<string, int>();
            foreach (NavFace face in data.Faces) {
                string key = "(" + string.Join(",", Array.ConvertAll(face.Metadata, m => m.ToString())) + ")";
                counts.TryGetValue(key, out int seen);
                counts[key] = seen + 1;
            }

            var parts = new List<string>();
            foreach (KeyValuePair<string, int> entry in counts) parts.Add(entry.Key + "x" + entry.Value);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts);
        }

        /// <summary>Face degrees by count, e.g. "3x120 4x900" - a shape signature for the mesh.</summary>
        private static string DescribeDegrees(NavMeshData data) {
            var counts = new SortedDictionary<int, int>();
            foreach (NavFace face in data.Faces) {
                counts.TryGetValue(face.Degree, out int seen);
                counts[face.Degree] = seen + 1;
            }

            var parts = new List<string>();
            foreach (KeyValuePair<int, int> entry in counts) parts.Add($"{entry.Key}x{entry.Value}");
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Prints every face that depends on vertices/faces created by a bake.  Structural audits
        /// cannot reveal a highly warped quad at a stair landing; this report makes the physical
        /// shape, edge ownership and non-planarity of the generated patch visible.
        /// </summary>
        private static int RunElevatedDiff(string stockPath, string bakedPath) {
            NavMeshData stock = NavMeshData.Parse(File.ReadAllBytes(stockPath));
            NavMeshData baked = NavMeshData.Parse(File.ReadAllBytes(bakedPath));
            var owners = new int[baked.Edges.Count];
            foreach (NavFace face in baked.Faces) {
                foreach (uint edge in face.Edges) if (edge < owners.Length) owners[edge]++;
            }

            Console.WriteLine($"stock v/e/f={stock.Vertices.Count}/{stock.Edges.Count}/{stock.Faces.Count}");
            Console.WriteLine($"baked v/e/f={baked.Vertices.Count}/{baked.Edges.Count}/{baked.Faces.Count}");
            for (int faceIndex = 0; faceIndex < baked.Faces.Count; faceIndex++) {
                NavFace face = baked.Faces[faceIndex];
                bool generated = faceIndex >= stock.Faces.Count;
                foreach (uint vertex in face.Vertices) if (vertex >= stock.Vertices.Count) generated = true;
                if (!generated) continue;

                double zMin = double.MaxValue, zMax = double.MinValue, area = 0;
                for (int i = 0; i < face.Vertices.Length; i++) {
                    NavVertex a = baked.Vertices[(int)face.Vertices[i]];
                    NavVertex b = baked.Vertices[(int)face.Vertices[(i + 1) % face.Vertices.Length]];
                    area += a.X * b.Y - b.X * a.Y;
                    zMin = Math.Min(zMin, a.Z); zMax = Math.Max(zMax, a.Z);
                }
                Console.WriteLine($"face {faceIndex} degree={face.Degree} area={Math.Abs(area) * 0.5:0.###} z={zMin:0.###}..{zMax:0.###} planeError={PlaneError(face, baked):0.###}");
                for (int i = 0; i < face.Vertices.Length; i++) {
                    int vertex = (int)face.Vertices[i], edge = (int)face.Edges[i];
                    NavVertex p = baked.Vertices[vertex];
                    Console.WriteLine($"  v{vertex} ({p.X:0.###},{p.Y:0.###},{p.Z:0.###}) e{edge} owners={owners[edge]}");
                }
            }
            return 0;
        }

        private static double PlaneError(NavFace face, NavMeshData data) {
            if (face.Vertices.Length < 4) return 0;
            NavVertex a = data.Vertices[(int)face.Vertices[0]];
            NavVertex b = data.Vertices[(int)face.Vertices[1]];
            NavVertex c = data.Vertices[(int)face.Vertices[2]];
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-9) return double.PositiveInfinity;
            double worst = 0;
            for (int i = 3; i < face.Vertices.Length; i++) {
                NavVertex p = data.Vertices[(int)face.Vertices[i]];
                worst = Math.Max(worst, Math.Abs(nx * (p.X - a.X) + ny * (p.Y - a.Y) + nz * (p.Z - a.Z)) / length);
            }
            return worst;
        }

        private static List<string> FindNavMeshes() {
            var found = new List<string>();
            string modules = Environment.GetEnvironmentVariable("BANNERLORD_MODULES_ROOT")
                ?? @"F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\Modules";
            if (!Directory.Exists(modules)) return found;

            // A spread rather than everything: enough shapes and both format revisions, without
            // spending a minute walking every shipped scene.
            foreach (string module in Directory.GetDirectories(modules)) {
                string sceneObj = Path.Combine(module, "SceneObj");
                if (!Directory.Exists(sceneObj)) continue;

                int taken = 0;
                foreach (string scene in Directory.GetDirectories(sceneObj)) {
                    string navmesh = Path.Combine(scene, "navmesh.bin");
                    if (!File.Exists(navmesh)) continue;
                    found.Add(navmesh);
                    if (++taken >= 12) break;
                }
            }
            return found;
        }

        private static bool Same(byte[] a, byte[] b) {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static string FirstDifference(byte[] a, byte[] b) {
            int limit = Math.Min(a.Length, b.Length);
            for (int i = 0; i < limit; i++) {
                if (a[i] != b[i]) return "0x" + i.ToString("X");
            }
            return "end of the shorter array";
        }

        internal static string Sha256(byte[] data) {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }
    }
}
