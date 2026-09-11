# Custom Scene Creator navmesh work — agent handoff

Last updated: 2026-09-11 (America/New_York; CSC 1.0.5 development line)

## Node-drawn, elevation-aware cutout tool — 2026-09-11

- The navmesh toolbar now distinguishes **Object Cutout** from **Draw Cutout**. The latter traces
  three or more red perimeter nodes using the Elevated Navmesh interaction: click to add/select/move,
  `Delete` to remove a selected node, and `F` to close.
- Drawn cutouts store arbitrary polygon corners and a clicked Z band. The C# in-tool baker applies
  each one independently against only vertically overlapping faces, so a roof or upper floor can be
  cut without removing ground navmesh underneath it. Legacy object cutouts remain XY-unbounded.
- The portable manifest and Python offline planner/writer/workflow now accept three-or-more-corner
  polygons and honor `IsFreeform`, `MinZ`, and `MaxZ`. Prefab export/import round-trips drawn cutouts
  as `csc_navcut_area` with `csc_navpoint` children.
- Debug build passes with 0 errors. The Python navmesh suite passes 52 tests (3 fixture-dependent
  tests skipped).

## Compact Transform panel with exact coordinates — 2026-09-07

- The live Transform panel now includes exact world-space **Position X / Y / Z** fields alongside
  its rotation controls. They use the same atomic transform path as the Scene Contents list, so
  live placement, saved project data, and any associated navmesh cutout stay synchronized.
- Rotation labels, fields, step field, and +/- buttons are physically smaller, leaving the panel
  compact enough to keep more of the edited scene visible while retaining the interactive Done and
  Escape exits.
- XML parsing, diff checks, and the Release/x64 CSC build pass with 0 errors (32 existing nullable
  warnings). Nothing was deployed.

## Move-mode hovered-object duplicate — 2026-09-06

- In **Move** mode, aim at an editor-placed object and press **C** to create a new carried copy;
  the source remains in the scene and the normal LMB/F placement action drops the copy.
- The copied object keeps its prefab, orientation, scale, attached scripts, and per-surface texture
  overrides. It receives a fresh entity identity; numbered exported markers also receive the next
  available marker number so a duplicate does not silently reuse a gate or spawn identity.
- The shortcut is unavailable while already carrying an object or while the Transform panel owns an
  object, preventing accidental duplicate actions during another edit workflow.

## Scene Contents clean-slate action — 2026-08-31

- The `L` Scene Contents window now has a separate **Clear All** button beside Close.
- A native confirmation lists the exact number of placed objects, navmesh cutouts, added ground
  areas, and elevated areas that will be removed. The underlying base scene is never touched.
- Confirming clears all four authored-data types in memory and marks the project dirty. The author
  can save the empty project normally or leave and choose Discard to recover the prior file.
- The leave prompt now appears for a dirty project even when its final object/area was removed;
  previously an empty live-object list could suppress that prompt.
- The manual and README controls table describe the action. XML validation, diff checks, and the
  Release/x64 build pass with 0 errors (31 pre-existing nullable warnings). Nothing was deployed.

## Placement-control parity — 2026-08-31

- CSC, Homesteads, and Estates now use one-tenth vertical placement steps while Left Shift is held
  during mouse-wheel height adjustment.
- Homesteads and Estates now share CSC-style surface-normal reconstruction. Aim at a physical
  surface while holding an object and press the configurable Align to Surface key (default `N`) to
  make flat props sit flush against walls, floors, roofs, and other meshes.
- Preview entities have physics disabled, so alignment sampling reads the intended physical surface
  rather than the green placement ghost.
- Both CSC and Homesteads Release builds compile successfully. Live placement remains the final
  verification step; nothing was deployed automatically.

## Version transition — 2026-08-30

Custom Scene Creator **1.0.4 was publicly released before texture-editing work began**. Active
development is now **1.0.5**. Preserve the existing 1.0.4 release archive and historical 1.0.4
verification records; new builds, documentation, and eventual packages use 1.0.5.

## CSC texture override handoff — 2026-08-29

The project now contains the first practical texture-editing workflow for version **1.0.5**. From
the scene contents list, an author can select a placed object, choose one of that
object's existing material names, choose a PNG from CSC's Documents-side `textures/` folder, and
apply or remove the diffuse override. Refresh invalidates CSC's image cache so an externally edited
PNG can be previewed again without restarting the game.

Persistence and portability are explicit rather than implied:

- projects, templates, editor reopening, Walk Around, and Raid-Scale Battle restore overrides;
- textured templates travel with a sibling `<name>_textures` folder and import those images before
  preview/placement;
- prefab, scene-fragment, template, and Modding Kit scene exports write a
  `<name>.texture_overrides.json` manifest and `<name>_textures` image folder;
- a CSC-exported prefab placed again from My Prefabs automatically restores any material override
  that is unambiguous across its source children;
- if different source children deliberately use different PNGs on the same named material, the
  sealed prefab cannot address those children safely, so CSC logs and skips that automatic restore;
  the manifest still preserves the original per-object assignments for a consuming mod.

The implementation intentionally changes only an existing material's primary diffuse texture. It
does not invent UV coordinates, switch shaders, or force alpha flags on arbitrary assets. The HSR
picture-frame precedent showed why runtime materials and textures need strong references and why
shader/alpha mutation is safe only for a specifically authored material; CSC follows those rules.

Files added/changed for this slice include `Editing/PngDecoder.cs`,
`Editing/TextureOverrideApplicator.cs`, `UI/TexturePanelVM.cs`, `UI/TexturePanelView.cs`,
`GUI/Prefabs/CSCTexturePanel.xml`, `IO/TextureOverrideManifestExporter.cs`, project DTO mappings,
test-mission restoration, exporters, the outliner, README, integration guide, and user manual.
No installed module was deployed from this work. Release builds and in-game UI/material tests are
the remaining gates before publishing 1.0.5.

## User constraints

- Start with `F:\Bannerlord Mods\BANNERLORD_MODDING_INDEX.md`; the durable knowledge base is
  `F:\Bannerlord Mods\docs\navmesh.md`.
- Project: `F:\Bannerlord Mods\bannerlord-custom-scene-editor`.
- Custom Scene Creator **1.0.4 is released**; active development and the next release are **1.0.5**.
- Update the project only. Do **not** deploy/copy into the installed Bannerlord module; the user does that.
- Preserve the dirty worktree and all unrelated user changes. Never reset it.
- The user wants a non-power-user workflow and wants autonomous work until their input is genuinely needed.

## Current status — 2026-08-24

The shared baker and Homesteads source are ready for another **Homesteads live test after the user
deploys Dist**. Do not copy to the installed module from this task.

The important policy is now transactional at every practical authoring boundary:

- the normal fast cutout batch is retained for performance;
- if the batch would split the shipped walkable ground, it is discarded and replayed one physical
  building group at a time;
- if a merged wall/building group is still unsafe, its original panels are replayed individually;
- elevated outlines are clustered by physical structure, and a failed cluster is replayed one
  outline at a time over multiple passes so a stair can make its deck valid later;
- marked ground additions already isolate overlapping spatial clusters internally;
- a rejected panel, ground cluster, or elevated outline is logged by name/reason and does not erase
  successful work elsewhere.

This directly addresses Nova Kuchen's 2026-08-24 failure, where 370 cutout fragments produced many
valid edits but one final four-island result caused the old all-or-nothing check to discard every
cutout, after which one disconnected elevated network discarded all 17 authored surfaces.

Offline verification after the change:

- the base ramp, embedded landing, and width-retention self-tests pass;
- `battle_terrain_007 (3).json`: one merged cutout and both elevated outlines retained, one island;
- `battle_terrain_007 (4).json`: one tunnel-reserving cutout and both elevated outlines retained,
  one island;
- complex `battle_terrain_007.json`: two safe cutout pieces and 9 of 11 elevated outlines retained,
  one island; only `Elevated area 1` (sub-minimum connector) and `Elevated area 10` (unsafe repair
  triangulation) are skipped and named. The other nine are not rolled back.

Related Homesteads corrections in the same Dist build:

- raw LMB edge detection now runs in RTS, third-person, and first-person modes;
- LMB is always Place while editing, independent of the optional configured keyboard shortcut, and a
  dedicated attached-camera input layer prevents the same click from becoming Attack; normal combat
  input is restored immediately when editing ends;
- leaving build/edit mode explicitly restores third person, while `V` can still change view outside
  build mode;
- elevated-navmesh help uses short explicit lines inside a bounded 1100-pixel HUD column; its live
  category/status row is now a short state label rather than another instruction sentence;
- stuck followers recover beside the player at the player's elevated Z instead of being terrain-
  snapped inside a wall;
- the async bake display reports normalized percent progress rather than pretending the work count
  is a number of buildings.

This historical navmesh status predates the release transition above; active CSC development is now
**1.0.5**.

## Baking now happens in the game, in C#

The Python tools were a post-hoc workflow: export a manifest, leave the game, run a script, copy a
candidate back. That is gone. `CustomSceneCreator/NavMesh/` is a self-contained C# implementation of
the same passes, and it runs while the editor is open.

| File | What it does |
| --- | --- |
| `NavMeshContainer.cs` | RNM1 / length-prefix / raw container, plus a hand-written LZ4 block codec |
| `NavMeshData.cs` | Parse and serialize NMG8 / NMG9; the 260-byte tail is preserved verbatim |
| `NavMeshGeometry.cs` | 2D predicates, ear clipping, bridged hole triangulation, barycentric Z |
| `NavMeshValidation.cs` | Structural counts: bad refs, self/duplicate edges, non-manifold, islands |
| `NavMeshCutout.cs` | Plan and apply one four-corner footprint |
| `NavMeshAddition.cs` | Cluster marked areas, hull them, bridge to an open edge, fill |
| `NavMeshRamp.cs` | Build two-rail elevated strips, opening safe landing seams when necessary |
| `NavMeshAudit.cs` | Whole-mesh check with a position on every finding |
| `NavMeshBaker.cs` | The facade: request in, baked file out |

**This folder takes no TaleWorlds dependency.** That is deliberate and load-bearing: it is what lets
`tools/NavMeshPortTests` compile the same sources under net8.0 and check them against real game
files in seconds, and it is what lets Homesteads Reloaded call the baker directly.

### Calling it from another mod (the Homesteads Reloaded path)

```csharp
var request = new NavMeshBakeRequest();
request.Cutouts.Add(new[] {           // one four-corner XY footprint per solid object
    new Point2(x0, y0), new Point2(x1, y1), new Point2(x2, y2), new Point2(x3, y3),
});
request.Additions.Add(new NavMeshRequiredArea {
    Label = "yard",
    Center = new NavVertex(cx, cy, cz),
    Radius = 4f,
    Boundary = terrainSamples,        // >= 8 terrain-snapped XYZ samples around the rim
});

NavMeshBakeReport report = NavMeshBaker.BakeFile(sceneFolder + "/navmesh.bin", request);
InformationManager.DisplayMessage(new InformationMessage(report.Headline));
```

Each operation is applied to a copy and kept only if it succeeds, so one impossible footprint costs
that footprint and nothing else. `BakeFile` keeps a `.prebake` backup by default, writes through a
temporary, and re-reads the candidate before it replaces anything. `NavMeshBaker.RestoreBackup` puts
the original back.

For a homestead, the natural call site is wherever the settlement scene is written on exit/save: build
the request from the placed buildings' footprints, call `BakeFile` on that homestead's own scene
folder, and show `report.Headline`.

### Saving and exporting now carry the navmesh with the scene

- **Saving a project** (`ProjectSerializer.Save`) writes the layout, the `.navcut.json` manifest, and
  a baked navmesh at `Documents/Mount and Blade II Bannerlord/CustomSceneCreator/exports/navmesh/<project>.navmesh.bin`.
  The save message in the editor now reports what the bake did.
- **Exporting a Modding Kit scene** bakes the navmesh that was copied into the exported SceneObj
  folder, in place, so the exported folder is complete rather than carrying the source scene's mesh.
- Neither path ever writes to the game's own scene files.

### Testing a navmesh across the whole map

`csc.navmesh_audit <project>` checks the entire baked mesh instead of one corner of it, and reports
every finding with a position to walk to:

- ground that is cut off from the main walkable area (the thing a test battle can never reveal)
- object footprints that are still walkable, meaning a cutout did not take
- marked areas that are still unwalkable, meaning an addition did not take
- broken or overlapping topology

The test battle (`csc.navtest`) is still there for watching agents move; the audit is what to run
before shipping a scene.

## Proven result

The offline writer is no longer merely a parser experiment. Its generated NMG9 mesh passed an
isolated Bannerlord engine test on 2026-08-16:

- Test artifact: `TestArtifacts/csc_navmesh_writer_control/navmesh.bin`
- SHA-256: `D85A16067B37BFAD4F29AAF0ED182E76EECB783F2113DAEE908B84F2DE98533A`
- 25,598 vertices; 51,267 edges; 25,638 faces
- Storehouse footprint center: no navmesh; nearest face center 5.0 m away
- Cross-building route: reachable, 16.0 m straight versus 28.4 m path, detour 1.78x, direct no
- This proves the hole blocks the direct route while the repair ring remains connected.
- User screenshots confirming this are in the chat immediately before this handoff was created.

## Implemented project features

The dirty worktree contains the larger navmesh/editor feature set from this task:

- Read-only face/group/island probe and A/B route diagnostics.
- Approximate face-wireframe rendering and nearby-map rendering.
- Navmesh cutout authoring under placed solid objects.
- Persistent "navmesh required" notes for unmeshed terrain.
- Input suppression while menus/MCM are open and binding refresh after MCM closes.
- Whole-scene and Modding Kit scene export work from earlier turns.
- Project/save manifest export: `CustomSceneCreator.NavMeshPlan/2` `.navcut.json`.
- Version pin comments and values in the csproj and `SubModule.xml`.

Important C# files include:

- `CustomSceneCreator/Editing/NavMeshDiagnostics.cs`
- `CustomSceneCreator/Editing/NavMeshFaceVisualizer.cs`
- `CustomSceneCreator/Editing/NavMeshVisualMarkers.cs`
- `CustomSceneCreator/Editing/NavMeshSpatialIndex.cs`
- `CustomSceneCreator/Editing/NavMeshCutoutAuthoring.cs`
- `CustomSceneCreator/Editing/NavMeshRequirementAuthoring.cs`
- `CustomSceneCreator/IO/NavMeshCutoutManifestExporter.cs`

## Decoded NMG9 format

`RNM1` is a 48-byte wrapper followed by a raw LZ4 block. Decoded NMG9 is:

```text
NMG9
u32 vertex_count; Vec3[vertex_count]
u32 edge_count; 6*i32[edge_count]
u32 face_count
faces: u32 degree; u32 vertexIDs[degree]; u32 edgeIDs[degree];
       5*i32 metadata; u8 direction
260-byte common global tail
```

The third face metadata integer is group ID. The fifth is normally `15`; Modding Kit rebuilt faces
use `-15`. The untouched/Kit control comparison is documented fully in `docs/navmesh.md`.

## Offline tools (now the reference implementation)

The Python tools are kept because they are what the C# port was checked against, and because they
remain useful for batch work outside the game. They are no longer the workflow a user follows.

The port reproduces them exactly on real fixtures:

| Case | Python | C# |
| --- | --- | --- |
| `battle_terrain_008`, 2 cutouts | v 13,210 -> 13,218, e 25,500 -> 25,576, f 12,206 -> 12,201 | identical |
| `battle_terrain_005`, 10 marked areas | 1 cluster, 22-vertex hull, 22 faces, 583.3 m2 | identical |

Harness (`tools/NavMeshPortTests`, net8.0, compiles the mod's own NavMesh sources):

```bash
dotnet run --project tools/NavMeshPortTests --verbosity quiet
dotnet run --project tools/NavMeshPortTests --verbosity quiet -- --cutout <navmesh.bin> <manifest> all
dotnet run --project tools/NavMeshPortTests --verbosity quiet -- --addition <navmesh.bin> <manifest>
dotnet run --project tools/NavMeshPortTests --verbosity quiet -- --bake <navmesh.bin> <manifest>
dotnet run --project tools/NavMeshPortTests --verbosity quiet -- --recompress <navmesh.bin> <out.bin>
```

`--bake` is the one that matters for regressions: it runs cutouts then additions in the same order
the editor does, which is where a pass that works alone can still fail on a mesh the other has
already changed. `--recompress` exists to compare container bytes against the Python writer.

Latest round-trip result (2026-08-24): **passed=67 failed=2**. The two failures are NMG7 files
(`mp_sergeant_map_001`, `arabian_house_new_a_interior_a_house`), which the Python reader refuses for
the same reason - that revision is not decoded, and guessing at it would be worse than refusing.



- `tools/navmesh_inspect.py`: RNM1/LZ4/NMG9 decode, validate, compare, serialize, round-trip.
- `tools/navmesh_cutout.py`: resolve footprint intersection, boundary, projected Z, triangulation.
- `tools/navmesh_apply_cutout.py`: copy-only one-cutout writer with structural and hole checks.
- `tools/navmesh_workflow.py`: new non-power-user manifest-to-candidate command.
- `tools/validate_navmeshes.py`: batch validation.
- `tools/README_NAVMESH.md`: user workflow and safety rules.
- Unit tests: `tools/test_navmesh_*.py`.

The writer refuses source overwrite, existing destination, open/multiple boundary, multiple groups,
mixed metadata, unsupported edge records, changed connected-component count, and malformed output.
It retains original unused records deliberately to minimize the rewrite. Validation reports unused
records but does not treat them as an error. Non-manifold edges are an error.

## New one-command workflow

`tools/navmesh_workflow.py` accepts one version-2 manifest. It:

1. Requires one or more cutouts.
2. Reads the target scene from the manifest.
3. Locates exactly one installed `Modules/*/SceneObj/<scene>/navmesh.bin`, or accepts `--source`.
4. Projects each authored footprint XY and applies it sequentially to temporary copies, re-resolving
   and structurally validating after every operation.
5. Writes one timestamped candidate and combined `navmesh_candidate_report.json` under the manifest's own
   `exports/navmesh-candidates/` sibling folder.
6. Never modifies the source and refuses ambiguous matches and existing output.

Usage:

```powershell
python tools/navmesh_workflow.py "D:\...\exports\navmesh-cutouts\project.navcut.json"
```

`--modules-root` and environment variable `BANNERLORD_MODULES_ROOT` support nonstandard installs.
The user's install at `F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\Modules` is an
automatic default.

## Current verification state

- `python -m unittest discover -s tools -p 'test_navmesh*.py' -v`: **22/22 passed**.
- `python tools/validate_navmeshes.py TestArtifacts\csc_navmesh_writer_control\navmesh.bin`:
  **passed=1 failed=0 skipped=0**.
- Release build through `C:\Program Files\dotnet\dotnet.exe`: **0 warnings, 0 errors**.
- Built DLL remains FileVersion `1.0.4.0`, ProductVersion `1.0.4+...`.
- `tools/package_release.ps1` parses and now includes `navmesh_workflow.py` plus all navmesh tools.
- `git diff --check` reports only expected LF-to-CRLF notices, no whitespace errors.

Final build command (plain `dotnet` resolves to an x86 runtime stub with no SDK):

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build `
  'CustomSceneCreator\CustomSceneCreator.csproj' -c Release -p:Platform=x64 -v quiet --nologo --no-restore
```

## Documents path discovery

The user's real Windows `MyDocuments` is redirected:

```text
D:\Users\cball\Documents
```

Do not assume `C:\Users\cball\Documents`. Current CSC root:

```text
D:\Users\cball\Documents\Mount and Blade II Bannerlord\CustomSceneCreator
```

Existing manifest:

```text
D:\Users\cball\Documents\Mount and Blade II Bannerlord\CustomSceneCreator\exports\navmesh-cutouts\battle_terrain_005.navcut.json
```

It is a valid version-2 manifest, but contains **0 cutouts and 10 required-area notes**. Therefore it
cannot yet drive the one-cutout writer. This is expected: required-area notes are documentation for
adding mesh and are not supported geometry generation.

## Next step that needs user input

The user supplied `battle_terrain_008.json`; its redirected-Documents manifest was found and a real
candidate was generated. This also exposed and resolved NMG8 input support:

- Source stock mesh is RNM1/NMG8, SHA-256
  `D775C60C2F080450F7E24E00F9E40DAEFE2FDB86939A8FB18356CC57F6CAE656`.
- NMG8 uses the same vertex/face/tail layout as NMG9 but five edge integers instead of six. The Kit
  upgrade adds a zero sixth edge value. Parser/serializer now exact-round-trips both formats; applied
  output is current NMG9.
- Manifest: `D:\Users\cball\Documents\Mount and Blade II Bannerlord\CustomSceneCreator\exports\navmesh-cutouts\battle_terrain_008.navcut.json`
- One `arabian_house_new_a` cutout; 35 affected faces; group 0; one closed 25-vertex boundary;
  fully contained; 29 repair triangles; area error `8.299e-12`.
- Candidate: `D:\Users\cball\Documents\Mount and Blade II Bannerlord\CustomSceneCreator\exports\navmesh-candidates\battle_terrain_008_20260817T000140Z\navmesh.bin`
- Candidate SHA-256: `60997D825C6F88CF2EDC26B42CCFA90C6FDCAFFF3B365FC2847A48C1E0F92B67`.
- Candidate has 13,214 vertices, 25,533 edges, and 12,200 faces; zero invalid, duplicate, or
  non-manifold topology; and one connected component. Source hash was rechecked unchanged.
- Authored object-base Z was 4.82–7.72 m below the navmesh surface, so projected per-corner navmesh Z
  was correctly used.
- Automated suite after NMG8 addition: **23/23 passed**.

The isolated target now exists as
`F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\Modules\CustomSceneCreator\SceneObj\csc_bt08_test`.
It contains the expected `arabian_house_new_a` in `scene.xscene`, the matching one-cutout manifest,
terrain/flora/atmosphere, and the untouched source navmesh (SHA-256 still
`D775C60C2F080450F7E24E00F9E40DAEFE2FDB86939A8FB18356CC57F6CAE656`). The next user action is to
back up that disposable scene's `navmesh.bin`, replace it with the candidate, open it using
`csc.open csc_bt08_test`, and validate the footprint center plus an A/B route across the house. Do
**not** replace the stock SandBoxCore `battle_terrain_008/navmesh.bin`. Respect the user's standing
preference to perform deployment copies themselves.

Engine result received: **the manifest-driven NMG8-to-NMG9 case passed**. The first attempt to open
`csc_bt08_test` hung and required a game restart; the second attempt loaded. No managed exception was
written. The old trace ends after `SceneCreatorMission: Opening mission`, so it cannot localize the
stall. Screenshots prove the cutout and connected routing:

- Test 1: footprint cursor unmeshed, nearest face center 10.0 m; 28.9 m straight, 49.4 m path,
  detour 1.71x, `direct no`, reachable.
- Test 2: footprint cursor unmeshed, nearest face center 3.9 m; 37.2 m straight, 49.6 m path,
  detour 1.33x, `direct no`, reachable.

This promotes NMG8 input upgrade and a second building shape from offline-only to isolated engine
proof. Do not erase the one-time hang from the record; reproduce on later candidates before deciding
whether it is cache/startup noise or a mesh-related native issue.

Usability fixes made after this result:

- `ProjectBrowserVM` now synthesizes empty launch entries for derived scenes, so a `csc_*` scene is
  visible directly in the normal Saved Projects menu even before it owns a project JSON.
- Future Modding Kit scene exports also save an empty launch project automatically. Exported objects
  are already baked into `scene.xscene`, so the launcher intentionally contains zero project objects.
- `SceneCreatorMission` now logs initializer creation, entry/exit of `MissionState.OpenNew`, and the
  behavior factory. A future hang will show which native boundary was last reached.
- Release build succeeds with 0 errors and the two existing nullable warnings; version remains 1.0.4.

Latest implementation after the user's multi-area request:

- Multi-cutout workflow is implemented. Non-overlapping cutouts are chained atomically; intermediate
  files live in a temporary directory, the source is never touched, and the final report includes
  every prefab/entity operation plus aggregate counts. Synthetic two-hole connectivity passes.
- `ProjectNavMeshRequirement` now stores `Boundary`, a flattened ring of 16 terrain-snapped XYZ
  samples. `NavMeshRequirementAuthoring.Create` samples each new note with
  `Scene.GetGroundHeightAtPosition`; rendering follows the sampled terrain. Old projects deserialize
  with an empty boundary and remain valid notes.
- New read-only `tools/navmesh_addition_plan.py` groups overlapping requirement circles, finds the
  nearest true open navmesh edge, and reports gap, Z mismatch, grade, owning face/group, and geometry
  readiness. It does not write faces yet.
- When an older project is opened, `SceneEditingMissionLogic` now backfills missing terrain boundaries
  for its saved requirements, marks the project dirty, and tells the user to save. This upgrades old
  center/radius notes without requiring them to delete and re-mark every circle.
- Navmesh automated suite: **37/37 passed** (26 original + 10 manifest round-trip + 1 mixed-metadata
  sequential cutout test). C# Release build: 0 errors, 0 warnings.
  `tools/package_release.ps1` includes the addition planner and all navmesh tools.

## Latest saved real fixtures and exact resumption point

The user reported both requested projects saved. The files were then read directly from redirected
Documents; do not ask the user to repeat this save before checking the findings below:

- `D:\Users\cball\Documents\Mount and Blade II Bannerlord\CustomSceneCreator\projects\battle_terrain_008.json`
  and its matching `exports\navmesh-cutouts\battle_terrain_008.navcut.json` were both written at
  `2026-08-16 20:52:55 -04:00`. The project contains **2 entities, 1 navmesh cutout, 0 required
  areas**. The manifest's one cutout is `arabian_house_new_c`, entity
  `{3F55AD32-E89D-4E8C-BC79-F20EFC056547}`, with **33 sampled face indices**. This is a new real
  building fixture, but it does **not** yet prove multiple cutouts were retained: only one of the two
  buildings is marked in the saved project.
- `D:\Users\cball\Documents\Mount and Blade II Bannerlord\CustomSceneCreator\projects\battle_terrain_005.json`
  and its matching `exports\navmesh-cutouts\battle_terrain_005.navcut.json` were both written at
  `2026-08-16 20:53:45 -04:00`. The project contains **1 entity, 0 cutouts, 10 required areas**.
- Important discrepancy: all ten saved `battle_terrain_005` requirements still have the legacy
  fields `Id`, `Label`, `Pos`, `Radius`, `NearestFaceIndex`, `NearestFaceDistance`, and `Created`; none
  contains the new `Boundary` array. The project JSON also contains no boundary field. Therefore the
  terrain-boundary backfill was not present in the deployed runtime, did not execute, or was not
  serialized. Do not claim the boundary upgrade is engine-proven yet.

Steps 1–5 completed on 2026-08-17 by automated pass (no user input required):

1. **Fixtures preserved.** Both real project/manifest files were read and verified in place.
   The `battle_terrain_008` project has 2 entities, 1 cutout (`arabian_house_new_c`), 0 requirements.
   The `battle_terrain_005` project has 1 entity, 0 cutouts, 10 requirements (all legacy, no Boundary).
   Real-fixture regression tests added in `tools/test_navmesh_manifest_roundtrip.py`.

2. **Offline workflow on `arabian_house_new_c` — passed.**
   - Candidate: `D:\Users\cball\Documents\...\exports\navmesh-candidates\battle_terrain_008_20260817T121501Z\navmesh.bin`
   - SHA-256: `FAB6F6B72C56EB1885648B2C25497E40C5857F372C84E605ECA0BA11308D4F5D`
   - 13,210→13,214 vertices, 25,500→25,542 edges, 12,206→12,208 faces.
   - 36 faces removed, 38 repair faces added. 1 connected component, 0 non-manifold edges.
   - Max Z delta 7.79 m (object base below navmesh surface — projected Z used correctly).
   - Triangulation area error: 9.32e-12. Validation: passed=1 failed=0.

3. **Boundary persistence diagnosis.** The C# `ProjectNavMeshRequirement.Boundary` field is a
   `public float[]` with default `Array.Empty<float>()`. Newtonsoft.Json serializes public fields by
   default and will write `"Boundary": []` for an empty array and the full 48-float array when populated.
   The `NavMeshCutoutManifestExporter` passes `ProjectNavMeshRequirement` objects directly into the
   manifest's `RequiredAreas` array, so the field is in the serialization contract. The
   `EnsureTerrainBoundary` backfill in `SceneEditingMissionLogic.AfterStart` calls
   `Scene.GetGroundHeightAtPosition` — a native call that requires an active mission scene. The
   **conclusion**: the code is correct, but the deployed assembly at the time of the user's save
   predates the boundary backfill commit. A fresh build + deploy + re-save is needed to get real
   Boundary data. Automated round-trip tests confirm the JSON contract preserves Boundary when present
   and deserializes cleanly when absent (legacy projects).

4. **Automated save/reload regression added.** `tools/test_navmesh_manifest_roundtrip.py` (10 tests)
   covers: 2-entity/2-cutout project JSON round-trip, manifest preserving both cutouts, multi-cutout
   workflow producing a valid single-component candidate, requirement-with-boundary round-trip,
   legacy-requirement-without-boundary deserialization, addition planner accepting/rejecting based on
   Boundary presence, and real-fixture regression tests for both `battle_terrain_005` and
   `battle_terrain_008`. Full suite: **36/36 passed**.

5. **Addition planner run on `battle_terrain_005` — read-only, completed.**
   - 10 required areas, all in one cluster (all circles overlap within 4 m radius).
   - 0/10 geometry-ready (all lack Boundary samples, as expected from step 3 diagnosis).
   - Nearest open-edge gaps range from 2.99 m to 21.82 m; Z deltas are small (±1.25 m).
   - All 10 notes correctly report "re-mark with a terrain-sampling editor build."
   - The cluster is a single contiguous group — safe cluster-boundary construction will need to
     envelope all 10 circles as one region once Boundary samples are available.

User re-saved both projects on 2026-08-17 12:39–12:40 UTC with updated deployed build:

- **`battle_terrain_008`**: Now has **2 entities, 2 cutouts** (`arabian_house_new_a` + `arabian_house_new_c`),
  0 requirements. Multi-cutout UI/engine proof complete. Modified 2026-08-17T12:40:20Z.
- **`battle_terrain_005`**: Now has **1 entity, 1 cutout** (`european_castle_barn_b`), **10 requirements
  all with Boundary arrays** (48 floats = 16 terrain-snapped XYZ samples each). Boundary persistence
  engine-proven. Modified 2026-08-17T12:39:45Z.

Multi-cutout writer fix and candidate generation:

- The first multi-cutout workflow attempt on the updated `battle_terrain_008` failed with
  "affected faces do not share compatible metadata". Root cause: cutout 0's repair faces carry
  metadata `(2, 0, 0, 0, -15)` (the writer negates the fifth metadata integer for rebuilt faces,
  matching Modding Kit convention), while cutout 1's affected patch includes both original faces
  with `+15` and repair faces from cutout 0 with `-15`. The strict equality check rejected them.
- **Fix applied** in `navmesh_apply_cutout.py`: metadata tuples that differ only in the sign of the
  fifth integer are now treated as compatible via a canonical normalization function. New test
  `test_sequential_cutouts_accept_mixed_metadata_sign` added to `test_navmesh_apply_cutout.py`.
- **Multi-cutout candidate generated successfully**:
  - Candidate: `battle_terrain_008_20260817T124443Z/navmesh.bin`
  - SHA-256: `1A01105F0DD11F15617FA9FDC19C4408FA9A7475B688920E403560653B681F3B`
  - 13,210→13,218 vertices, 25,500→25,576 edges, 12,206→12,201 faces.
  - 73 faces removed, 68 repair faces added across 2 operations. 1 connected component, 0 non-manifold.
  - Validation: passed.

`battle_terrain_005` cutout and addition plan:

- **Cutout candidate generated** for `european_castle_barn_b`:
  - Candidate: `battle_terrain_005_20260817T124135Z/navmesh.bin`
  - SHA-256: `B64965C3704E26AE4F726502D92D768746C4C4BD7FB20B4E43494DA3FF5BAA83`
  - 23,211→23,215 vertices, 46,761→46,788 edges, 23,533→23,538 faces.
  - 18 faces removed, 23 repair faces added. 1 connected component, 0 non-manifold.
  - Validation: passed.
- **Addition planner re-run** with Boundary data — all 10 areas now **geometry-ready**:
  - `all_geometry_ready: true`, 10/10 areas ready, 1 cluster (all 10 overlap).
  - Horizontal gaps: 0.009–17.84 m (area 7 nearly touches the open edge at 0.009 m).
  - Z deltas: ±0.52 m. Max boundary grade: 0.248 (area 0).
  - All notes: "ready for topology design."

Test suite: **37/37 passed**. C# Release build: 0 warnings, 0 errors. Version remains 1.0.4.

Navmesh-test battle command (rewritten with real combat AI):

- The first version's agents stood idle — MissionMode.Battle alone is insufficient. The rewrite
  follows the proven BloodGames/Homesteads sparring pattern exactly:
  - `AgentHumanAILogic` in the behavior list (drives AI combat decisions).
  - `Agent.SetWatchState(Agent.WatchState.Alarmed)` on every combat agent (the critical call that
    makes agents enter combat-ready stance and engage enemies).
  - `BannerBearerLogic` for team identification.
  - `AgentVictoryLogic` for mission-end handling.
  - Weapons wielded after a 0.5s delay (agents aren't fully ready in AfterStart).
  - `MissionBasicTeamLogic` deliberately omitted (same as BloodGames) — it pre-creates banner-less
    teams before AfterStart runs. Teams are created manually with explicit `SetIsEnemyOf` in both
    directions.
  - `MissionMode.Battle` set in AfterStart, not StartUp.
  - `DecalAtlasGroup.Battle` (not Town) in the mission initializer record.
- New files: `CustomSceneCreator/Editing/NavMeshTestBattleLogic.cs` and
  `CustomSceneCreator/Boot/NavMeshTestMission.cs`.
- Console commands (unchanged from first version):
  - `csc.navtest <project_name> [enemy_count]` — derives player/enemy positions from cutout corners.
  - `csc.navtest_at <scene> <px> <py> <ex> <ey> [enemy_count] [levels]` — explicit coordinates.
- Mission ends when one side is wiped out (3s grace period). TAB/ESC to leave.
- C# build: 0 errors, 3 nullable warnings (2 pre-existing + 1 new in NavMeshTestBattleLogic).
  Python suite: 37/37 passed. Version 1.0.4.

**Multi-cutout engine proof — PASSED (2026-08-17).**
The user deployed the 2-cutout candidate for `csc_bt08_test`, ran `csc.navtest_at csc_bt08_test 730 376 830 376 10`,
and confirmed enemy infantry correctly pathed around both houses to reach the player. This is the
first real multi-building engine proof: two different building shapes (`arabian_house_new_a` +
`arabian_house_new_c`), two cutout holes in one candidate mesh, and the AI detoured around both.

Proven so far:
- Single-cutout NMG8→NMG9 (`arabian_house_new_a`, `csc_bt08_test`): ✅ engine-proven (earlier)
- Single-cutout NMG8→NMG9 (`arabian_house_new_c`, `csc_bt08_test`): ✅ engine-proven (earlier)
- Multi-cutout NMG8→NMG9 (both houses, `csc_bt08_test`): ✅ engine-proven (this test)
- Boundary persistence on re-save: ✅ engine-proven (10/10 requirements with Boundary arrays)
- Navmesh-test battle command: ✅ working (agents fight and pathfind)

Navmesh addition writer implemented (2026-08-17):

- New file: `tools/navmesh_apply_addition.py` — the inverse of `navmesh_apply_cutout.py`.
  Instead of removing faces under buildings, it **adds** walkable faces over open ground
  that was left unmeshed by the original scene author.
- New test file: `tools/test_navmesh_apply_addition.py` — 12 tests covering single-area,
  multi-area, cluster merge, elevation-aware Z, structural validation, refusal cases, and
  convex hull.
- Algorithm (generic for any number of points, any terrain elevation):
  1. Read manifest RequiredAreas (must have Boundary samples).
  2. Cluster by overlapping radius circles.
  3. For each cluster: compute convex hull of all boundary sample XY projections.
  4. Find the single nearest open boundary edge on the existing mesh to any hull vertex.
  5. Build a combined polygon: existing edge endpoints + envelope hull vertices.
  6. Ear-clip triangulate the combined polygon as one piece (ensures shared edges
     between bridge and interior → single connected component).
  7. New faces inherit the existing face's group ID and direction; metadata[4] = -15.
  8. Serialize as NMG9, validate (no non-manifold, valid references, finite vertices).
- Elevation handling: every new vertex carries its terrain-sampled Z from the boundary
  ring. Faces on slopes tilt to follow terrain; faces spanning stairs have a Z step.
- Real data test on `battle_terrain_005`:
  - 10 requirements, 1 cluster, 22 envelope vertices, 1 bridge, 22 new faces.
  - 23,211→23,233 vertices, 46,761→46,805 edges, 23,533→23,555 faces.
  - **1 connected component, 0 non-manifold edges.** Validation: passed.
  - Candidate: `battle_terrain_005_20260817T154305Z/navmesh.bin`
  - SHA-256: `EA2011A23308B81F3C8100D0DBF1F42AEB30A59717D93F4CDD2783DE093BFC13`
- Python suite: **49/49 passed**. C# build: 0 errors, 0 warnings. Version 1.0.4.
- `tools/package_release.ps1` updated to include `navmesh_apply_addition.py`.

Next agent actions:

1. **Engine-test the addition candidate.** The user should install the `battle_terrain_005`
   addition candidate (which also includes the barn cutout) into a disposable scene and run
   `csc.navtest_at` to verify AI can now pathfind across the previously unmeshed area.
2. **Improve the algorithm**: replace convex hull with alpha-shape or authored polygon for
   concave cluster shapes. Add triangle subdivision for large faces. Support multi-group
   clusters.
3. **Integrate addition + cutout into one workflow**: the `navmesh_workflow.py` should apply
   cutouts first, then additions, producing one unified candidate.

## Remaining limitations / future work

- Each individual building cutout must still be a fully contained four-corner patch with one repair
  boundary and one face group. Overlapping/unsafe cases must fail rather than be guessed.
- The addition writer uses a **convex hull** envelope — concave cluster shapes (L-shaped pathways)
  will include some area outside the original authoring circles. Future: alpha-shape or authored polygon.
- The addition writer uses **one bridge per cluster** — sufficient for connectivity but may leave
  large gaps between the bridge and non-bridged envelope vertices. Future: multi-bridge with edge
  sharing verification.
- No **triangle subdivision** — large envelope triangles may exceed the engine's preferred face size.
- Each new footprint shape remains an isolated-test gate until coverage grows.
- The in-game editor does not mutate live pathfinding. Baking writes a file; the running mission keeps
  the navmesh it loaded with. To see a bake take effect, load the exported scene.
- Do not reintroduce the earlier dynamic navmesh attachment attempt; it caused a native access violation.
- NMG7 is not decoded. Two shipped scenes use it and are refused rather than guessed at.
- `csc.navmesh_audit` marks findings in the world (red ring + mast for serious, amber otherwise),
  capped at 24 so the shared marker pool is not exhausted. `csc.navmesh_audit_clear` hides them.

## Verification after the C# port

- `dotnet run --project tools/NavMeshPortTests --verbosity quiet` - **passed=64 failed=2** (both NMG7).
- Cutout and addition against real fixtures reproduce the Python numbers exactly (table above).
- Audit after baking `battle_terrain_008`: 1 island, 170,869 m2 walkable, no footprint still walkable.
- Audit after baking `battle_terrain_005`: 1 island, 477,553 m2 walkable, no marked area still unwalkable.
- `dotnet build CustomSceneCreator/CustomSceneCreator.csproj` - **0 errors**.
- **Container bytes are identical to the Python writer's.** `--recompress` on `battle_terrain_008`
  and `navmesh_inspect.py --roundtrip-out` produce the same 695,181 bytes, SHA-256
  `E0F4B9C855A5482635C54B8B86E472E0223BD64D6FE566603324F491E50146CF`. The Python writer's output has
  already passed an isolated engine test, so the C# encoder is not a new format risk.

## Elevated-route implementation (2026-08-20)

The user correctly distinguished this from ordinary required-area fills: a ramp, stairs, bridge deck,
or rampart route must preserve its own elevation, not sample terrain beneath it. The scene editor now
has an **Add Elevated Navmesh** mode and persists `ProjectNavMeshRamp` records:

- The user clicks alternating left/right rails from the bottom upward, directly on the physical
  collision surface. The saved `Left` and `Right` arrays contain paired world-space XYZ samples.
- Two complete pairs are the minimum; extra pairs describe stair treads, changing grades, narrowing
  bridges, and turns. `F` (the alternate place key) finalizes the draft; with no draft, it removes a
  nearby saved ramp. The preview draws cyan saved rails/cross-rungs, gold for the selected strip, and
  orange for the draft.
- Ramps are serialized in both project JSON (`NavMeshRamps`) and the existing version-2
  `.navcut.json` manifest, then passed to `NavMeshBaker` after cutouts and ground additions.
- `NavMeshRamp` builds quads between successive rail pairs at their sampled Z values. It demands
  both landings share real navmesh edges. When a landing sits inside an ordinary face, it first cuts
  a narrow landing mouth and reuses that new open edge; it never lays a third face over an existing
  interior edge.
- A one-ended route is now explicitly refused. Any failed strip restores its entire pre-ramp mesh
  snapshot, so it cannot leave a half-cut landing behind.

Automated checks added/updated:

- `tools/NavMeshPortTests --ramp-self-test` proves a raised strip joins two platforms, and a second
  case proves two embedded landings are safely opened/spliced. Both finish with one connected
  component, zero non-manifold edges, and no invalid references.
- Python navmesh suite now passes **49/49**; the `battle_terrain_005` real fixture expectation was
  updated from 10 to its current **13** terrain-boundary requirements.
- CSC Release build succeeds with 0 errors (existing nullable warnings remain). Version is still
  **1.0.4**. The user has not been asked to deploy this work yet.

Next scene-editor validation, after a user deploys the later build, is a disposable real scene with a
physical stair/ramp/bridge: author at least two rail pairs, save, inspect the bake report, then load
the exported scene in a fresh game process and route agents across it. A generated SceneObj folder
must still exist before process startup, but normal CSC export already satisfies that condition.

## First in-game run, and the two bugs it found

`battle_terrain_005`, 1 footprint and 13 marked areas, saved from the editor:

```
Saved 'battle_terrain_005' - 1 object(s). Navmesh baked: 1 cut out, 0 filled in, 13 skipped.
```

The cutout worked. `csc.navmesh_audit` then independently reported all 13 areas as still unwalkable,
with coordinates - the audit doing exactly its job on its first real run. The trace log named the
cause:

```
Skipped all 13 marked area(s): repair polygon could not be triangulated safely
```

Two defects, both fixed:

1. **Additions were all-or-nothing.** One failing cluster discarded every marked area. Each cluster is
   now built independently, with the vertex/edge/face lists cut back on failure, so a cluster that
   cannot be built costs only itself. `AdditionResult` reports `AreasFilled`, `AreasSkipped`, and a
   named reason per skipped patch, and those reasons reach the trace log.
2. **The bridge ring could cross itself.** The combined polygon always began `[bridgeA, bridgeB, ...]`
   regardless of which side of the mesh edge the new ground lay on - a flaw ported faithfully from the
   Python writer, which the earlier 10-area fixture happened not to trigger. Both endpoint orders are
   now tried and checked with `NavMeshGeometry.IsSimplePolygon`; if neither gives a simple ring the
   cluster is refused with a reason the user can act on rather than a triangulation failure.

Result on the same input, through `--bake` (the full path the game runs, cutouts then additions):

```
Cut out footprint 1: removed 18 faces, added 23; components 1->1
Filled marked ground: 13 area(s) filled in 4 patch(es), added 70 faces; components 1->1, nonManifold=0
Navmesh baked: 1 cut out, 13 filled in.
audit: 23,608 faces, 477,706 m2 walkable, one connected area
```

## The Homesteads Reloaded consumer (and what it taught us)

Homesteads Reloaded is the first mod outside the editor to use `NavMesh/`, and shipping it surfaced
constraints the editor never hit. The lessons are engine-level, so they apply to anything that writes
a navmesh at runtime.

### The slot pattern

Homesteads bakes a navmesh for a stock battle terrain it borrows, which cannot be written to (it is
shared with every field battle in the game and belongs to the install). The obvious answer - a derived
scene folder per homestead - is impossible, because **scene folders must exist before the process
starts**. Creating one during play is the twenty-second stall and silent death documented below.

What works:

| | |
| --- | --- |
| `SceneObj/hsr_slot_00..03` | a small pool of reusable scene folders, created at `OnSubModuleLoad` |
| `hsr_navmesh_state/<campaignId>/<homestead>/` | the real data: `navmesh.bin`, `layout.txt`, `slot.txt` |

Folder NAMES must exist at startup; CONTENTS need not. **The engine re-reads `terrain.bin`,
`scene.xscene` and `navmesh.bin` at every scene load** - proved by swapping a slot's terrain with the
game running and watching the new terrain appear. So a fixed set of slots can be re-pointed at
whatever scene is being entered.

Because only one scene is ever loaded at a time, the pool is a **cache, not a limit**: a homestead
publishes into a slot when entered, and the least recently used slot is taken over when all are busy.
Fifty homesteads work with four slots.

Slots are generated on the player's machine from their own install, never shipped - they hold copies
of TaleWorlds terrain.

### Applies to the scene editor too

- **A scene folder created at runtime is unusable until the next launch.** The editor's Modding Kit
  export writes folders and tells the user to open them in the Kit, so it has never hit this - but
  anything that generates a scene and then tries to load it in the same session will.
- **`SceneHasMapPatch` must be false for a generated scene.** A patched scene is resolved through the
  map-patch system, which only knows shipped battle terrains.
- **`scene.xscene` declares its own name** and must match the folder, or the scene fails silently.
  Rewrite only that attribute; reserialising the XML changes 10 KB of formatting for no reason.

### Consumer-side design worth copying

- **Prepare on the main thread, execute anywhere.** The bake splits into gathering (reads the live
  scene) and working (numbers and files). That split is only possible because `NavMesh/` takes no
  TaleWorlds types, and it is what allows a background bake with progress instead of a ten-second
  freeze on scene exit.
- **Authored spawn points are exempt from snapping.** Agents are placed onto the navmesh when they
  spawn, so a cutout under a spawn point matters. Computed positions are moved to the nearest walkable
  face; points authored in a prefab are used exactly as placed, because moving them stands the smith
  outside his own forge - and those NPCs never walk, so unmeshed ground beneath them costs nothing.
- **Cutout shapes come from the prefab.** A single exclusion radius cannot describe a mine's scattered
  rocks or a pen's fence runs. Homesteads reads `hsr_navcut` marker entities out of prefab XML -
  position, `scale` as full size in metres, Z rotation in radians - and emits one oriented quad per
  marker. The cutout pass already accepted arbitrary convex quads; only the callers were feeding it
  axis-aligned squares.
- **A deploy script that deletes the module folder deletes generated scenes with it.** Back them up
  around the copy, and abort rather than proceed if they cannot be moved.

## Derived scenes must exist BEFORE the game starts

**The engine indexes module scene folders once, at process startup. A scene folder created during
play cannot be loaded at all - and the failure is a twenty-second stall followed by the process
dying with no crash report, no managed exception, and nothing in the mod logs.**

The tell is in the engine's own log, `C:\ProgramData\Mount and Blade II Bannerlord\logs\rgl_log_*.txt`:

```
$BASE/Modules/SandBoxCore/SceneObj/battle_terrain_028/scene.xscene.        <- resolved, loads
$BASE/Modules/HomesteadsReloaded/SceneObj/hsr_nav_control_028/scene.xscene <- resolved, loads
SceneObj/hsr_nav_battle_terrain_028/scene.xscene.                          <- BARE PATH, then death
```

A **module-qualified** path means the engine resolved the scene. A **bare relative** path means it
did not, and what follows is the stall and the silent exit.

Proved by timestamps across three failing runs - folder always created after the game started:

| game started | folder created | scene load |
| --- | --- | --- |
| 18:45 | 18:48 | died |
| 19:53 | 19:55 | died |
| 20:05 | 20:07 | died |

Every success had the folder present at launch.

**Reloading a save is NOT enough.** Tested deliberately with a `homestead.navmesh_force_scene`
override: create the folder mid-session, save, quit to the main menu, reload, force the scene, enter.
It crashed. Only a full process restart re-indexes.

### What Homesteads does about it

- `HomesteadNavMeshBake.PrepareOne` writes the folder the moment a homestead is given a scene, and
  again for every homestead at session launch.
- A folder that was not present at startup is **never offered**; the walk-around and battles quietly
  fall back to the stock scene and log why. That is what makes the crash unreachable rather than
  merely unlikely.
- The first bake tells the player: *"Restart the game once for it to take effect."*

### Also learned the hard way

`build.bat`'s redeploy replaces the module folder and **deletes generated scenes with it**. Several
test results were invalidated by this before it was noticed. Re-bake after any redeploy.

## Two things that were NOT the cause

Both were diagnosed confidently and both were wrong. Recorded so they are not re-investigated.

- **`metadata[4] = -15`.** An invented value; shipped meshes use only 15 and -1. Removing it changed
  nothing. The code still copies rather than invents, which is right on its own merits. The official
  docs describe **visibility masks over six layers**, and a scene declares four levels with masks
  1/2/4/8 - so 15 is almost certainly "visible on all levels", making -15 meaningless but harmless.
- **Map patching.** `SceneHasMapPatch` was disabled for derived scenes on the theory that the
  map-patch system could not resolve them. The log showed the patch correctly disabled and the bare
  path unchanged. The change is kept - a derived scene genuinely is not in that system - but it fixed
  nothing.

## What the official docs add

From <https://moddocs.bannerlord.com/editor/scene-editor/nav_mesh/>:

- **"Designers need to arrange the navigation mesh in a 1.5-meter diameter."** Our sliver floor of
  0.5 m2 is well UNDER TaleWorlds' own guidance, so the fix for refused cutouts is to merge more
  aggressively toward 1.5 m, never to lower the threshold.
- **Face IDs**: 0 = battle/hideout/lords hall, 1 = inactive until raids, 2 = civilian walkable,
  3 = barrier around animal spawns. Shipped battle-terrain faces carry `metadata[0] = 2`, so that
  integer is plausibly the face ID - another reason to copy rather than invent.
- **"Navigation mesh should not be applied under the physical objects."** Cutting under buildings is
  what TaleWorlds prescribes.
- **"Navigation mesh polygons must include spawn point pivot points or spawn points will not work."**
  Homestead NPC spawn points live inside building prefabs as entities tagged
  `spawnpoint_homestead_npc`, so a cutout will swallow the market's and smithy's. Homesteads snaps
  every spawn onto walkable ground in `SpawnHomesteadAgent` rather than sparing those buildings.

## The engine's minimum face size (hard-won, do not lose this)

**A navmesh containing faces below roughly 0.5 m2 will not load. The game stalls and then dies with
no error, no crash report, and no managed exception.** Everything else about the file can be perfect.

This cost a long debugging session and several wrong diagnoses, so the evidence is recorded in full.

### The measurement

Across the 68,244 faces of `battle_terrain_028`:

| | shipped | what our ear clipping produced |
| --- | --- | --- |
| smallest face area | **0.846 m2** | 0.065 m2 |
| shortest edge | **0.479 m** | 0.162 m |
| 1st-percentile area | 3.32 m2 | 0.21 m2 |

Not one shipped face is below 0.85 m2. That floor is clearly deliberate in TaleWorlds' generator.

### How it was proved

By bisection, each step a separate scene folder loaded with `csc.open`:

1. Copy of the stock scene, renamed, **stock navmesh** - loads. Copy/rename is fine.
2. Same, with the stock mesh **re-encoded by our LZ4** (decoded stream byte-identical) - loads. The
   encoder is fine, and our encoder's output is byte-identical to the Python writer's.
3. Same, with an **edited** mesh containing slivers - crashes.
4. Same, edited, **no face below 0.5 m2** (6 cutouts) - loads.
5. Same, edited with merging, 9 cutouts, 110 added faces - loads.

### What was NOT the cause

Recorded because each looked convincing at the time:

- **`metadata[4] = -15`.** An earlier build marked repair faces with it, following a Modding-Kit
  convention. `battle_terrain_028` ships only `(2,0,0,0,15)` and `(2,0,0,0,-1)`, so -15 was an
  invented value - but removing it did not fix the crash. The code still copies rather than invents,
  which is the more defensible behaviour regardless.
- **The 260-byte global tail.** Identical across scenes, all zeros bar a leading 32. Carries nothing
  per-mesh.
- **Face winding.** All shipped faces are counter-clockwise in XY; so are ours.
- **Unused vertices and edges** left behind by removed faces. Present in our output, absent in stock,
  and harmless.
- **Face ordering.** `metadata[4]` is interleaved across 824 runs in stock; nothing requires grouping.

### What the code does now

- `NavMeshGeometry.MinimumFaceArea` = 0.5 m2, `MinimumEdgeLength` = 0.3 m.
- Ear clipping picks the **most equilateral ear** each pass rather than the first valid one.
- `MergeSlivers` joins a thin triangle to its neighbour across their shared edge to form a **quad**,
  when the result is convex. This matches shipped meshes, which are 64,968 quads to 3,276 triangles.
- Anything still below the floor **refuses that cutout** with the measurement in the message.

On a 12-footprint test this took refusals from 6 to 3. The threshold is inferred from what ships, not
measured against the engine - the true limit is somewhere at or below 0.5 m2 and could be found by
bisecting candidates, which would refuse fewer buildings.

### Consequence: we no longer match the Python writer

Deliberately. Python takes the first valid ear and produces the slivers that crash the engine. The
byte-for-byte parity between the two implementations now holds only for **round-trips**, not edits,
and the Python tools should not be used to produce a mesh for the engine.

## Still untested in the game

1. Bake timing on a large scene - is the save hitch noticeable?
2. Does an exported SceneObj folder with a baked navmesh load and route correctly?
3. Do the audit markers render where expected?

## Documentation updated

- `F:\Bannerlord Mods\docs\navmesh.md`
- `README.md`
- `USER_MANUAL.htm`
- `tools/README_NAVMESH.md`
- `TestArtifacts/csc_navmesh_writer_control/README.md`

Keep this handoff current after every material implementation, test, discovered path, or user result.

## Ramp-authoring UX correction — 2026-08-21

The first elevated-ramp UI used alternating left/right clicks and held its partially authored
points only in mission memory. User testing on `battle_terrain_001` correctly exposed both as poor
authoring behaviour: the click order was unintuitive, and cycling away from the mode made all
unfinalized points disappear. The saved project had `NavMeshRamps: []`, confirming that this was a
temporary-draft loss rather than a renderer failure.

The editor now works as a two-rail, persisted workflow:

1. In **Elevated Navmesh**, click the **left physical boundary** from bottom to top.
2. Press the alternate place key (normally `F`) to change to the **right rail**, then click from
   the same bottom end to the top.
3. Press `F` again once both rails have the same number of samples (minimum two) to finish the
   ramp. A completed strip is eligible for baking.

Important implementation details:

- The very first click creates a `ProjectNavMeshRamp` with `IsDraft = true`; drafts serialize in
  the project and remain visible after switching mode, saving, or reopening the project.
- Drafts are **not** sent to `SceneNavMeshBaker` or the exported manifest. Only a completed,
  structurally valid ramp is baked.
- Clicking within 1.25 m of an existing rail dot selects it. The next click moves that exact point
  to the physical collision surface and keeps its actual Z height. `F` while a point is selected
  clears selection.
- Ramp rendering now accepts partial rails. Draft = orange, saved = cyan, hovered/active = gold,
  selected dot = brighter gold.
- The dense green nearby-navmesh overview is intentionally disabled in ramp mode; it hid the rail
  authoring lines. Ramp mode still renders the hovered face (cyan) or nearest face (magenta), so
  the author has local mesh context without the clutter.

Files changed for this correction:

- `CustomSceneCreator/Editing/SceneEditingMissionLogic.cs`
- `CustomSceneCreator/Editing/NavMeshRampAuthoring.cs`
- `CustomSceneCreator/Editing/SceneProject.cs`
- `CustomSceneCreator/IO/SceneNavMeshBaker.cs`
- `CustomSceneCreator/IO/NavMeshCutoutManifestExporter.cs`

Validation after the correction:

- Release build succeeds (0 errors; existing nullable warnings remain).
- `NavMeshPortTests --ramp-self-test` passes both a normal raised strip and the embedded
  landing-seam scenario.
- `python -m unittest discover -s tools -p 'test_navmesh*.py' -v`: **49/49 passed**.

Next user test: deploy the new CSC DLL, open `battle_terrain_001`, and author the wall stair using
the two rails. Verify that draft dots persist after cycling out/back, a dot can be selected/moved,
and completed ramp data appears in the saved project. Then bake/export the finished project and
test the engine route across the elevated surface.

### 2026-08-21 follow-up: multiple strips and user-facing instructions

The HUD now explicitly states that after finishing a complete elevated strip, the user remains in
the same mode and a normal place click on empty physical surface starts the next ramp. The live
status panel repeats this at the finishing step. No mode cycling is required between stairs,
bridges, or wall-walk segments.

The user correctly identified a larger future UX correction: a human naturally traces the full
perimeter of an elevated walkable surface, rather than thinking in paired rails. Keep the current
two-rail persistence/editability as the working implementation, but redesign its authoring front
end around an outline/close workflow before calling it final. The baker will still need to derive
or receive the two route landings needed to make a safely connected elevated path.

## Scene-selector test modes — 2026-08-21

The saved-project selector now exposes the direct launch paths, replacing the need to remember
console commands for ordinary project testing:

- **Open** — reopens the selected existing project in the editor.
- **Walk Around with Party** — opens the selected scene as a normal non-editing mission, restores
  the project matching the scene name, and spawns healthy hero companions currently in the main
  party as AI followers. It is intended for collision/scale/stair/bridge route checks without
  combat. It does not modify the campaign party.
- **Navmesh Skirmish** — opens the existing eight-enemy pathing test directly from the saved project;
  its spawn positions are derived from project cutouts.

Implementation is in `UI/ProjectBrowserVM.cs`, `UI/ProjectBrowserScreen.cs`,
`CampaignEntry/SceneCreatorEntry.cs`, and the new `Boot/SceneWalkaroundMission.cs`. The saved-project
browser prefab has the three buttons. The new-scene picker intentionally contains only Open Editor
and Cancel: a blank base scene is not a meaningful navmesh test. `USER_MANUAL.htm` now documents both these test options and elevated
navmesh authoring. Build passes; first in-game test should verify that companions actually receive
the follow order on a selected saved project.

## Raid-scale combat test and reusable scene slot investigation — 2026-08-21

The combat selector option is now labelled **Raid-Scale Navmesh Battle**. It creates a mission-only
copy of each healthy player-party member beside the player and spawns an equal number of tier-one
Homesteads-style attackers on the other side of the project cutouts. Attackers use the same entry
mix as the Homesteads Angry Mob: forest bandits, mountain bandits and looters, with a bandit boss
when available. This does not change the campaign roster or record casualties. For safety the test
caps each side at 100 agents; normal party sizes remain exactly matched. Files:

- `CustomSceneCreator/Editing/NavMeshTestBattleLogic.cs`
- `CustomSceneCreator/CampaignEntry/SceneCreatorEntry.cs`
- `CustomSceneCreator/UI/SceneBrowserVM.cs`
- `USER_MANUAL.htm`

Release build passes (0 errors; existing nullable warnings remain). This needs an in-game test of
both friendly-party spawning and combat AI.

### Scene-slot implementation

CSC now uses the same reusable-slot architecture through the new
`Boot/CscNavMeshTestSlot.cs`. Homesteads proves the correct architecture:

1. A fixed scene folder name exists under the module's `SceneObj` **before game startup**.
2. At load time, its contents may be overwritten with the selected stock scene's terrain, xscene,
   atmosphere, flora, shader cache, and the previously baked `navmesh.bin`.
3. The test opens the fixed slot name, not the stock scene name. The engine rereads that slot's files
   each scene load. Its *folder name* is what must be indexed at boot.

CSC now maintains two fixed slots, `csc_navmesh_test_slot_00` and `_01`, created under the module's
`SceneObj` at `OnSubModuleLoad`. Bannerlord can retain a prior scene's files briefly after leaving a
mission, so alternating slots avoids publishing into a still-locked folder when a walk-around is
followed immediately by a battle. Both folders need one game restart after this deployment so the
engine indexes them. Before Walk Around or Raid-Scale Battle, CSC copies the selected source scene's
required files and shader cache into the selected slot, renames `scene.xscene` internally to the
slot name, then overwrites `navmesh.bin` with the Documents bake when one exists. The slots are
disposable; durable bakes remain per project in Documents.

This builds successfully (0 errors; existing nullable warnings). It must be tested in game after
deployment: restart once, then launch a saved project through Walk Around and Raid-Scale Battle.
Verify the mission log reports publication to either `csc_navmesh_test_slot_00` or `_01`, and the navmesh overlay
shows the baked cutouts/additions. Do not create arbitrary SceneObj folders on demand: that is the
known stall/crash path.

## Ramp separation, explicit bake, and battle distance — 2026-08-22

User testing of `battle_terrain_006.json` exposed two real UX issues:

- `F` was overloaded: with no active ramp it could delete a saved ramp under the cursor. It now
  starts a clearly separate empty draft instead. Workflow is: left rail → `F` → right rail → `F`
  to finish → `F` once more to start the next independent strip. `F` is no longer destructive.
- The saved project showed one completed but badly sequenced strip and a second incomplete draft;
  incomplete drafts are deliberately excluded by `SceneNavMeshBaker.ToRamps`, explaining why that
  second intended stair was not in any bake. The new explicit-start behavior should prevent that
  accidental "node noodle" continuation. The existing malformed project data was not rewritten.

Saved Projects now has a **Bake Navmesh** button. It calls `SceneNavMeshBaker.BakeForProject` on the
selected project, shows the result in the browser, and records progress to the trace log. This is an
explicit alternative to bake-on-save, useful before testing a project through the reusable slot.

`NavMeshTestBattleLogic.DerivePositionsFromProject` now keeps player and raid starts at least 200 m
apart while retaining the cutout cluster between them. Build passes (0 errors; existing nullable
warnings remain). Required user test after deployment: bake `battle_terrain_006` from Saved Projects,
then launch Raid-Scale Battle and verify both agents are separated and route around the wall.

## Selected elevated-node deletion — 2026-08-22

In **Elevated Navmesh** mode, pressing `Delete` now removes the currently selected rail dot. It
does not remove the strip or its other points. Since a two-rail strip needs equal point counts, a
one-sided deletion is deliberately marked as a draft and excluded from baking until the author
restores matching rail samples. The active drawing rail is switched to the rail that lost the point,
so the next authored point repairs that side. User Manual wording was updated. Implementation:
`Editing/SceneEditingMissionLogic.cs` and `Editing/NavMeshRampAuthoring.cs`.

### Selected-node visibility

A selected elevated-navmesh dot now renders bright blue as well as larger. This distinguishes the
single point being edited from the gold active strip, cyan completed strips, and orange drafts.

## Elevated-navmesh authoring redesign — 2026-08-22

User testing correctly confirmed that the two-rail UI had not actually been replaced: a persisted
legacy draft was still in `EditingRail = right`, so four new clicks became `left 0, right 4`. New
elevated authoring is now a four-corner perimeter: click clockwise or counter-clockwise around the
physical stair/ramp/bridge surface and press `F` to close. New records use `ProjectNavMeshRamp.Outline`;
the bake adapter converts its expected order (bottom-left, top-left, top-right, bottom-right) into
the two equivalent rails only internally, allowing the established two-ended joining baker to stay
in use. Legacy Left/Right records remain supported for old saved projects.

Closed elevated outlines now accept four or more perimeter corners. At bake time the adapter finds
the outline's lowest and highest boundary edges, follows each side between those landings, and
resamples both paths to equal counts for the established two-ended ramp builder. This lets authors
retain bends and extra physical corners while the safe joining code continues to receive paired
paths. A flat/degenerated outline is still rejected by the ramp build; use Add Navmesh Area for
flat unmeshed ground. The HUD, manual, and point selection/deletion all use outline wording.
Build and engine test still required after this change.

The manual's authoring tips are deliberately ordered to match the editor mode cycle: **Cutout Plan**,
then **Add Navmesh Area**, then **Elevated Navmesh**.

## Explicit bake always creates a test navmesh — 2026-08-22

The Saved Projects **Bake Navmesh** action previously returned “Nothing in this project needs a
navmesh change yet” when all authored elevated areas were drafts (or when a project simply had no
valid authoring). It did not write a Documents bake at all, so the reusable test slot had nothing
to load. `SceneNavMeshBaker.Bake` now copies the source scene's base `navmesh.bin` into the
project bake location in that case and reports that it created a base navmesh bake. Valid completed
cutouts, requirements, and elevated outlines still run the normal modifier bake. This gives every
project a concrete navmesh file for testing and makes an incomplete outline's status clear.

## Battle test must restore project assets — 2026-08-22

Trace evidence showed the first battle launch correctly published the baked slot but did not include
`ProjectWalkaroundLogic`, so it had terrain/navmesh only and no placed CSC entities. A later battle
then hit a locked `terrain.bin` while trying to reuse the same slot and silently fell back to the
stock scene, losing both assets and the baked navmesh. `NavMeshTestMission` now restores project
entities (with walk-around followers disabled); `SceneCreatorEntry` refuses a test launch if slot
publication fails rather than falling back to the source scene. The two-slot change above removes
the normal immediate-followup lock conflict. Build passes. Next user test requires a restart after
deploying this version so the new `_01` slot is indexed.

## Direct editor mode toolbar — 2026-08-22

The **Assets** build selector has a top-row direct mode toolbar: **Build, Delete, Move, Scripts,
Inspect**, plus a boxed **NAVMESH** group containing **Cutout, Add Area, Elevated Navmesh**. It is deliberately separate from the scene selector. Clicking
a tool closes the selector and performs the same full transition as cycling with `\\`, including the
camera/combat state and mode-specific reset, while `\\` remains available and is still the way to
reach **Off**. An earlier in-world duplicate row was intentionally removed because its passive
mission layer rendered correctly but could not reliably receive clicks. Implementation:
`UI/AssetPickerVM.cs`, `UI/AssetPickerView.cs`, `_Module/GUI/Prefabs/CSCAssetPicker.xml`, and
`Editing/SceneEditingMissionLogic.cs`. Release build passed (0 errors; existing nullable warnings
remain). In-game click verification is still required after deployment.

## Asset selector mode row and battle restoration trace — 2026-08-22

User clarified that “build selector” means the full-screen **Assets** picker, not the small in-world
status overlay. The eight direct mode buttons are now in that picker, immediately below its
instructions. Clicking one closes the picker and selects that tool for the next editor action.

The 08:00 trace shows `NavMeshTestMission` was called with `project` and the reusable test slot,
but `ProjectWalkaroundLogic` previously logged no restoration count in battle (it only logged its
walk-around follower spawn). It now writes an explicit `Restored x/y project object(s)` record for
both walk-around and raid missions, including unavailable prefab counts. The behavior is still
inserted into `NavMeshTestMission` before `NavMeshTestBattleLogic`; after deployment, use a raid
test once and inspect the trace for that new line. If it says all objects restored but they remain
invisible, investigate battle mission entity lifetime/order next; if it says 0/x, investigate the
prefab/source data. Release build passed (0 errors; existing nullable warnings remain).

## Raid launch returned to browser — trace finding, 2026-08-22

The failed raid was **not** a mission hang or asset-restoration failure. At 08:12:44 the trace says:
`CscNavMeshTestSlot: Baked test slot is not indexed in this game session; restart Bannerlord once`,
followed by `Raid-scale battle refused: reusable test slot could not be published.` The selector then
reopened by design. The two slot folders were first created after the game had already indexed scene
folders, so Bannerlord cannot discover them until the next full game restart. A subsequent session
began at 08:32:57, but no later attempt has reached `SceneCreatorEntry` in the trace yet. After that
full restart, the next raid attempt should either publish a slot or log a concrete publication error;
then the new `Restored x/y project object(s)` line will finally verify battle asset restoration.

## First successful raid: objects restored, detour still unproven — 2026-08-22

The 11:25 trace proves the current test correctly published `battle_terrain_006` into slot `_00`
with its baked navmesh and **restored 14/14 project objects**. The screenshot then showed groups
holding opposite sides of the wall and shooting. The prior battle test only re-issued `Charge` to
the raider/attacker formation; the player-side AI could choose to hold and fire, making the visual
result ambiguous. `NavMeshTestBattleLogic` now orders both AI formations to Charge and logs an
engine radius-aware route at the exact two spawn positions: direct status, route length, detour,
or an explicit unreachable result. Deploy and run one raid to distinguish a baking/connection
failure from combat behavior. Release build passed (0 errors; existing nullable warnings remain).

## Long, connected wall cutouts at a navmesh boundary — 2026-08-22

`battle_terrain_006` exposed the common wall case: 11 touching wall/stair footprints were merged
into one outline, then the old baker skipped the *entire* barrier because part of that outline
crossed the outer boundary of the base navmesh. The raid trace consequently reported a direct,
250.9 m route through the wall (`direct=True`, detour `1.00x`). This was a bake defect, not merely
combat AI behavior.

`NavMeshBaker` now recognises an edge-touching obstacle as an **open-boundary cut**, removes its
affected source faces without attempting an enclosed-hole repair ring, and retains the normal
enclosed-hole path for ordinary buildings. It also retains original members for a merged footprint
as a conservative fallback if a complex merged outline cannot be cut as one. Offline baking against
the stock `battle_terrain_006` navmesh and the saved 13-footprint manifest now reports three cutout
operations and audits as **navmesh usable** (one benign leftover-data warning); all 13 marked object
footprints are no longer reported walkable. This is the required model for long, modular walls with
stairs/ramparts on or against the original navmesh boundary. Release build passed (0 errors; existing
nullable warnings remain). User must deploy this build, re-save/re-bake `battle_terrain_006`, then
run Raid-Scale Battle to confirm the engine route becomes a detour.

## Live battle_terrain_006 bake: wall cutouts applied; elevated outline skipped — 2026-08-22

The latest in-game save/bake of `battle_terrain_006` confirms the wall work is active: the trace
records three cutout operations (12, 4, and 12 source faces removed), rather than skipping the
long modular wall. The HUD's `1 skipped` is **not** a wall segment or cutout. It is the one saved
elevated outline, logged exactly as: `Elevated area 8: neither landing reaches an open navmesh
edge`.

That saved outline traces the full long wall-walk and two separate stair/ramp turnouts as one
connected perimeter. The current elevated adapter still reduces every outline to one two-ended
ramp: it finds one lowest edge and one highest edge, then tries to turn the two perimeter routes
between them into rails. A branched wall-walk with two lower approaches is therefore a valid
authoring shape but not a valid single-ramp bake. This is a product gap, not an authoring mistake.
The next elevated-navmesh work needs to support a raised platform/walkway with multiple ramp
connections (or otherwise explicitly decompose one user outline into those parts) instead of
forcing the entire structure through the single-strip ramp builder.

The Assets picker NAVMESH group was also corrected to use a real horizontal layout for its heading
and buttons; previously they were overlapping siblings, which caused the button row to draw over
the heading at the user's UI scale. The group remains visibly boxed and now allocates a dedicated
96px heading column.

## Walk-around follower probes — 2026-08-22

`ProjectWalkaroundLogic` now explicitly places every spawned companion and temporary
`villager_empire` peasant into the player infantry formation before ordering that formation to
**Follow** the main agent. It also now adds **five peasants in addition to companions**, instead of
only topping the entire group up to five. This makes Walk Around a dependable elevated-navmesh
probe: lead the group up each ramp/stair route and they must path across the baked surface to remain
in formation. Release build passed (0 errors; existing nullable warnings remain).

## Shared elevated corners and the required elevated-network bake — 2026-08-22

The editor now reserves the first click after closing an elevated outline (and the first click of an
explicitly fresh draft) for the new outline. A corner can therefore be recorded at exactly the same
world position in two separate elevated areas instead of selecting the prior area’s point. This is
the required authoring behavior for a stair meeting a wall-walk. Release build passed (0 errors;
existing nullable warnings remain).

This fixes the interaction problem but is not the full baking solution. `NavMeshRamp.Apply` still
requires every individual strip to attach at two open navmesh edges, so a stair whose top joins a
newly authored wall-walk cannot be baked as an independent strip before that wall-walk exists. The
next implementation should replace the `SceneNavMeshBaker.OutlineToRamp` one-ramp reduction with
an **elevated-network** pass: triangulate each simple elevated perimeter at its sampled physical Z,
reuse/open every valid low landing edge, and join all pieces that share an authored edge/vertex into
one connected surface. That is the model needed for one continuous wall walk with multiple stairs
and ramps; it must be tested offline against `battle_terrain_006` before sending another build to
the user.

#### Elevated-outline selection priority correction — 2026-09-01

The earlier “first click after closing always starts a fresh outline” rule above has been replaced.
It made an already closed outline impossible to select after the user pressed `F`: the empty draft
captured every later click, including a visibly highlighted node on another finished area.

`SceneEditingMissionLogic.AddNavMeshRampPoint` now uses this consistent interaction model:

- While a draft is actively being traced, only that draft's nodes can be selected; clicks elsewhere
  add the next physical corner to the same draft.
- With no draft active (including immediately after `F`), every completed outline is eligible for
  node selection. A click on a saved node selects it, and the next click moves it.
- Clicking an empty physical surface while no draft is active is the sole action that begins a new
  elevated area.

This preserves independent overlapping corners without trapping the user in an empty new outline.

#### Shared-corner snap for elevated authoring — 2026-09-02

When an elevated outline is actively being traced, CSC now checks every **finished** other elevated
area for a node within 0.65 m of the physical click. If it finds one, it appends a new node to the
active outline at the other area's exact physical X/Y/Z. It does **not** select or modify the other
area. This makes ramp-to-platform, stair-to-wall-walk, bridge-to-landing, and similar junctions
line up reliably, while retaining independently editable overlapping nodes. Nodes of the active
draft remain normal editable nodes rather than snap targets.

## Elevated-network implementation and offline result — 2026-08-22

The elevated-network replacement is now implemented in `NavMeshRamp.BuildOutline`. A saved elevated
perimeter is no longer reduced to one pair of rails. It is triangulated as the full raised surface at
the sampled physical heights, and every low perimeter segment is considered as a possible landing.
The normal strip join distance remains 3 m; a whole raised surface may search 4.5 m for its landing,
which is necessary where the original navmesh sits just beyond a wall collision lip.

`SceneNavMeshBaker.ToRamps` now passes the original outline directly to that builder. The saved
`battle_terrain_006` manifest was baked offline against the stock scene navmesh and now produces:
`3 cut out, 0 filled in, 1 ramp(s) built` — 29 elevated faces, joined at both low stair landings,
with **zero skipped**. The candidate is only in
`TestArtifacts\\battle_terrain_006_elevated_network.navmesh.bin`; it has not been copied into the
game slot. The release DLL compiles successfully (0 errors; existing nullable warnings remain).

The offline audit still flags 11 "object still walkable" warnings because its old footprint check is
XY-only: it sees the deliberately raised walkway over the wall footprint and mistakes it for ground
inside the wall. Structural validation succeeds and the original wall cutouts still occur. The next
task is to make that audit height-aware (or explicitly exclude the elevated surface) and then send the
user the release DLL for the in-game Walk Around follower test.

The landing detector was then generalized beyond one height level: it now considers every locally-low
perimeter edge as a possible stair/ramp landing, and only connects an edge when it has a valid nearby
open base-mesh edge. This supports bridge ends on uneven banks and several ramps that start from
terraces at different elevations; the `battle_terrain_006` offline result remains zero skipped.

## Homesteads prefab-owned elevated routes — 2026-08-22

Homesteads Reloaded now feeds the same shared elevated-network baker from prefab data rather than
asking a player to draw navmesh. `PrefabNavCutMarkers` recognizes an `hsr_navelevated` group with
four or more `hsr_navpoint` children; their local XYZ positions trace the actual raised walking
surface. `HomesteadNavMeshBake.BuildElevatedSurfaces` transforms those points with each placed
asset, records them in its layout fingerprint, and passes them through `NavMeshBakeRequest.Ramps`.
The Homesteads Release build and CSC's ramp self-tests passed. No production prefab has yet received
markers: its physical surface must be measured first, so we do not guess a route into a wall or
bridge. A bridge usually needs no cutout below its deck; a solid wall or platform may pair its
elevated group with the existing `hsr_navcut` marker.

CSC's **Export as Prefab** now preserves every closed elevated outline as local `hsr_navelevated` /
`hsr_navpoint` metadata in the prefab XML. This is the intended defensive-asset workflow: author the
visible asset and its physical elevated perimeter together in CSC, export under a unique `hsr_` name,
then copy that XML into Homesteads' `Prefabs` folder and register the same prefab name as a
Homesteads placeable. At battle-exit bake time Homesteads reads the owned prefab first (ahead of any
old CSC development copy), transforms the points with the placed asset, and uses the shared elevated
network builder. This export behavior compiled successfully in both Release projects on 2026-08-22.

## Homesteads sidecars and player-authored elevated areas — 2026-08-23

Six measured CSC exports are now copied into `Homesteads/_Module/Prefabs` as **marker-only
sidecars**, not visual prefab replacements:

- `hsr_elevation_battania_platform_naved.xml`
- `hsr_elevation_fortified_gatehouse_naved.xml`
- `hsr_elevation_high_castle_wall_naved.xml`
- `hsr_elevation_tall_defensive_wall_naved.xml`
- `hsr_elevation_wooden_stairs_naved.xml`
- `hsr_elevation_heavy_wooden_stairs_naved.xml`

`PrefabNavCutMarkers.ElevatedSidecar` maps those sidecars to the original Native IDs. This is
intentional: the six named sidecars contain only marker entities, so using one as an override
for `battania_castle_wall_c` would erase that wall's visual geometry. The game continues to spawn
the Native asset; Homesteads reads the same local outline from its sidecar while baking. Existing
placements of the four mapped defensive assets automatically receive their elevated route. The two
standalone Native stair prefab IDs already exist as Misc placeables and now receive their matching
sidecar routes too.

The two `*_withstairs_naved.xml` exports were subsequently inspected. They are **not** marker-only:
each contains the measured elevated markers plus an explicit Aserai stair mesh and its collision.
They do not contain the base Battania wall, so on 2026-08-23 they were converted, per
`Homesteads Reloaded/docs/asset_conversion_guide.md`, into two complete HSR-owned composites:

- `homestead_tall_defensive_wall_with_stairs.xml` — explicit `battania_castle_wall_c` mesh and
  collision, the exported L3 stair mesh/collision, and 2 elevated groups / 11 points.
- `homestead_high_castle_wall_with_stairs.xml` — the complete explicit Native L3 wall hierarchy
  (wall and merlons), the exported L2 stair mesh/collision, and 2 elevated groups / 10 points.

Both are registered as Tier 3 **Defense** placeables, directly beside their plain-wall variants.
They deliberately use unique `homestead_*` root names, so they do not shadow or replace any Native
prefab. `PrefabNavCutMarkers.FindPrefabFile` resolves an HSR-owned prefab by its exact name, so both
routes are automatically included in Homesteads' normal post-battle bake.

The new **Navmesh → Add Elevated Navmesh** card is available only through Homesteads' building
selector. It records a player-clicked, world-space perimeter for a route assembled from several
loose props (tipped planks, mounds, assembled bridges, etc.); `F` closes an outline with four or
more corners. The outline is persisted on `HomesteadScene.ElevatedNavMeshAreas`, added to
`BuildElevatedSurfaces`, included in the bake layout fingerprint, and rendered cyan after saving.
This needs live testing after deployment. It intentionally does **not** yet provide edit/remove of
an already closed Homesteads outline; CSC remains the fuller authoring UI.

Both Homesteads Release builds completed successfully after this work (0 errors; pre-existing
nullable warnings remain). No live deployment was performed.

## Gatehouse regression investigation — 2026-08-23

User project: `D:\Users\cball\Documents\Mount and Blade II Bannerlord\CustomSceneCreator\projects\battle_terrain_007.json`.
It has one full-footprint cutout for `hsr_elevation_fortified_gatehouse` and eleven valid, non-
self-crossing elevated perimeters. This exposed a real limitation rather than an authoring-order
problem.

The bake pipeline is already and deliberately ordered **cutouts → added ground → elevated
surfaces**, irrespective of the order in which the user drew them. Offline test against stock
`battle_terrain_007\navmesh.bin` confirmed the difference:

- without the cutout, only the first low stair surface can attach (1 built, 10 skipped);
- with the full gatehouse cutout, all 11 are skipped because no lower landing reaches an open base
  navmesh edge.

The whole collision footprint includes legitimate doorway/interior/stair threshold space. Once it
is cut, the first surface has no safe base-mesh seam; higher platforms cannot create a connected
network by themselves. This directly explains why Walk Around followers could not follow into the
structure. Do **not** solve it by drawing a long bridge across the footprint: that would re-create
walkability through solid walls.

Two safe improvements were made in CSC's shared `NavMeshRamp` code:

1. Elevated perimeters now bake in retry passes, so dependent stairs/platforms can be authored in
   any order. A later successful surface gives earlier failed neighbours another chance to attach.
2. A level landing/wall-walk considers all of its perimeter edges for a join to an already-baked
   elevated neighbour. It no longer fails merely because a level platform lacks a locally-low edge.

The baker now also reports every individual skipped elevated area instead of collapsing an all-fail
run into the first error. CSC Release build succeeded (0 errors; existing nullable warnings only),
and `NavMeshPortTests --ramp-self-test` and landing-seam tests passed. The offline harness was
extended to accept a saved CSC project JSON directly, allowing this reproduction without manually
rewriting a manifest.

Next implementation task: add a **walkable reservation** concept to a cutout. For complex assets
such as a gatehouse, the cutout must remove only the solid-wall portions while retaining the
explicitly-authored doorway/stair/floor approaches as safe openings which the elevated network can
join. This needs to operate on faces/footprints, not a long generic bridge, and must be tested on
007 before asking the user for another in-game test. No deployment occurred for the retry/reporting
changes.

### Follow-up: why a simple connector is not the fix — 2026-08-23

An offline attempt to bridge the gatehouse's lowest outlined surfaces directly to the boundary left
by its full cutout did **not** produce a usable safe join. The cutout boundary is made from distant
source-face vertices rather than from the physical doorway edges; a generic long bridge either has
no pair of compatible endpoints or risks crossing live ground / solid interior. The experiment was
kept guarded by an empty-space test and has not changed the output for 007. Do not present this as
test-ready or deploy it.

The next implementation must split the cutout boundary at the explicitly authored approach points
and preserve a narrow reserved approach through only the marked doorway/stair geometry. It should
not use a distance-only bridge. The offline acceptance criterion for `battle_terrain_007` is: the
full cutout remains in place, at least one low outline joins the outer base mesh via a real shared
edge, dependent outlines build in subsequent retry passes, and the final mesh is structurally
sound. Only then ask for the Walk Around follower test.

### Gatehouse bake resolution — 2026-08-23

The reservation-grid experiment was discarded. It left broad accidental ground under the
gatehouse, took 20–30 seconds, and was the wrong model. The final pipeline retains the existing
order: **cut the complete physical footprint first, then build each explicitly authored elevated
surface**. A deliberately authored low tunnel surface may make one bounded connection to the
open cutout ring only after ordinary seam/join attempts fail. This uses the author's physical
tunnel outline rather than preserving invisible terrain below the building.

`battle_terrain_007` is now the exact regression test and produces the following verified result
from stock `SandBoxCore/SceneObj/battle_terrain_007/navmesh.bin`:

- `1 cut out, 0 filled in, 11 ramp(s) built, 0 skipped`;
- 25,712 vertices, 51,507 edges, 25,751 faces, one connected component;
- zero non-manifold edges, invalid face references, boundary mismatches, or self-edges;
- full bake took about 20.4 seconds in the offline debug harness. That is longer than desirable,
  but it completes and is no longer an indefinite stall.

The written offline candidate is
`F:\Bannerlord Mods\bannerlord-custom-scene-editor\artifacts\battle_terrain_007_full_candidate2.navmesh.bin`.
It is a test artifact only; do not deploy it. The normal CSC build must be used to bake the user’s
saved project in-game. CSC Release build and both ramp self-tests passed (0 errors; pre-existing
nullable warnings only).

Relevant implementation changes:

- `CustomSceneCreator/NavMesh/NavMeshRamp.cs`: order low landing candidates by actual height,
  bound seam attempts per outline, retry dependent surfaces, and permit one explicit cutout-ring
  connection rather than creating a non-manifold fan.
- `CustomSceneCreator/NavMesh/NavMeshBaker.cs`: always cuts the complete solid footprint before
  elevated surfaces are considered.
- `CustomSceneCreator/NavMesh/NavMeshAudit.cs`: treats an authored raised/tunnel outline crossing
  a cutout centre as an intentional exception; topology/island validation remains mandatory.
- `tools/NavMeshPortTests/Program.cs`: `--ramp-label` can isolate a saved elevated outline when
  diagnosing future assets.

Next user test: rebuild the CSC project, open `battle_terrain_007`, bake through its normal UI,
then launch **Walk Around with Party**. Lead the followers through the gatehouse tunnel and up the
stairs/ramparts. The in-game test is still needed to prove Bannerlord accepts and follows the
completed geometry, but there are no remaining offline structural or skipped-route blockers for
this project.

### Explicit Project Browser rebake — 2026-08-23

The Project Browser button is now labelled **Rebake Navmesh** and explicitly calls
`SceneNavMeshBaker.BakeForProject(..., force: true)`. It always refreshes the project's saved
test-scene navmesh slot, rather than deciding that there is nothing to do because the project is
not dirty or has no newly completed authoring. If there are no completed cutouts, added areas, or
elevated outlines, it writes a fresh base-navmesh copy so the test slot still exists. Normal
project save behaviour is unchanged: it does not initiate an otherwise empty bake.

### Re-evaluation: exported wall/stair prefabs are not yet follower-ready — 2026-08-23

The two latest Documents exports now correctly contain their full visual prefabs, `hsr_navcut`
markers for the solid wall and stairs, and two `hsr_navelevated` groups each. That fixes the prior
export-completeness issue, but it does **not** prove a walkable route.

Measured offline against the actual saved source projects:

- `hsr_elevation_high_castle_wall_withstairs_naved` / `battle_terrain_007 (3)`: both elevated
  outlines bake structurally, but the result reports zero top joins. The top stair lip
  `(965.073,786.681)–(962.166,786.488)` reaches only the *corner/extension* of the wall walk's
  lower edge `(959.581,786.215)–(962.668,786.372)`; it does not share a two-point deck edge.
  This precisely matches the observed “followers stop halfway up” symptom.
- `hsr_elevation_tall_defensive_wall_with_stairs_naved` / `battle_terrain_007 (4)`: after the
  complete cutout, the level wall-walk has no base join of its own and the multi-corner stair
  outline is rejected as degenerate. Offline result is `1 cut out, 0 filled in, 2 skipped`.

An experimental safe boundary-splice routine was added to `NavMeshRamp.cs`: when a high stair lip
actually crosses the middle of an existing elevated deck edge, it subdivides that deck boundary and
adds a narrow, real connector strip. It builds and remains structurally guarded, but neither of the
two exports above satisfies its conservative geometry checks yet. **Do not present it as validated
or deploy it for a user test.**

Required next work: build an explicit elevated-network connection pass that can create a bounded
corner/landing polygon where a stair arrives alongside (rather than directly across) a deck corner,
while refusing any connector that overlaps the solid wall cutout. It must prove a real shared path
from ground → stairs → wall-walk in the offline mesh before requesting another Walk Around test.

### Gatehouse prefab has the same false-positive bake — 2026-08-23

`hsr_elevation_fortified_gatehouse_naved.xml` was rechecked against its source project
`battle_terrain_007.json`. Its current export contains eleven elevated groups but (at the time of
inspection) only authoring markers—no visual prefab children or `hsr_navcut` marker. That may be an
older export, but it does not alter the core diagnosis.

The full offline bake says `1 cut out, 0 filled in, 11 ramp(s) built`, completes in about 24 s,
and passes structural audit. Its more important detailed result is: **11 bottom joins, 0 top
joins**. The successful count therefore means only that every outlined area found one lower seam;
it does not establish a usable route through each stair turn or from a stair onto the next deck.
This matches the report that soldiers traverse only a few turns then stop.

Additionally, elevated groups 4 and 5 contain 19 perimeter points and group 9 contains 33, with
height changes from ~1 m to ~12 m. A simple closed perimeter has no information about internal
stairwell walls/holes. The current ear-clipping pass triangulates its interior as if it were one
continuous surface, so it can create diagonal high-slope faces across a turn while remaining
topologically valid. Structural audit alone cannot call that usable.

Next required architecture change: replace the binary `built/skipped` success with a **surface
network** result. It must track each lower and upper seam, split/bridge compatible deck edges at
turning landings, and reject (rather than silently accept) multi-level outlines whose triangulation
would span an excessive step/slope. This is required before gatehouse or wall/stair prefabs are
considered ready for Homesteads follower testing.

Do not confuse an explicit bake attempt with an applied edit. If a project has authored records
but all of them are invalid/skipped, CSC deliberately keeps the previous baked file rather than
overwriting it with the stock base mesh. The HUD now says that those authored items could not be
applied and that the existing bake was kept; the old misleading text was `Navmesh unchanged -
nothing to bake.`

### `battle_terrain_007` draft selection trap — 2026-08-23

The completed regression project is exactly `battle_terrain_007.json`: one cutout and eleven
elevated outlines. `battle_terrain_007 (4).json` is an earlier draft with **zero cutouts and two
elevated outlines**. It cannot test the gatehouse tunnel or the complete stair network; a failed
bake of that draft is expected to preserve the old navmesh. The full project rechecked offline at
`1 cut out, 0 filled in, 11 ramp(s) built, 0 skipped` and remains structurally sound. The next
work item is not broad extra seam search (that made the 20-second bake much slower), but a
targeted graph/portal join between adjacent authored elevated surfaces so a tunnel has two real
entrances and raised paths form through-routes.

An attempted generic high-edge reuse was intentionally **not kept**: on this dense gatehouse it
made `Elevated area 4` degenerate and reduced the known-good result to 10 built / 1 skipped. Do
not reintroduce broad opportunistic joins. The future connection pass needs to plan compatible
portals between named outline boundaries before triangulation, and only then reuse/bridge those
specific edges. This should also avoid the expensive broad second-seam search, which exceeded the
acceptable bake time.

### Explicit elevated-edge portals — 2026-08-23

The targeted connection pass is now implemented in `NavMeshRamp.cs`. Before baking, CSC compares
the perimeter edges of elevated outlines. It records a portal only when both physical endpoints
match in **3D** within 0.9 m (including reversed edge direction). When the second compatible
outline is baked, it reuses the open edge of the first instead of creating two almost-touching
surfaces. The usual low-ground landing remains available as a separate, bounded connection, so a
stair can connect ground to a wall-walk without a broad high-edge search.

This deliberately supports multiple stairs, platforms and tunnel sections drawn as separate
areas. It does not treat any arbitrary nearby/high edge as a portal, so it avoids the degenerate
`Elevated area 4` regression and does not add the expensive full-map seam scan. The full
`battle_terrain_007.json` regression bake was rerun after this change: `1 cut out, 0 filled in,
11 ramp(s) built, 0 skipped`; its structural audit remains one connected component with no
non-manifold edges. The output is only a local artifact:
`artifacts/battle_terrain_007_portal.navmesh.bin`; it was not deployed.

CSC Release was rebuilt successfully (0 errors; existing nullable warnings only). The next
required action is a manual in-game test of the *completed* `battle_terrain_007` project after
the user copies the Release build: force **Rebake Navmesh**, then use **Walk Around with Party**
to lead followers through the tunnel and each staircase. If a particular authored connection does
not fall within the 0.9 m edge match threshold, the in-editor geometry should show it clearly and
we can add an explicit connector marker rather than widening the automatic matcher blindly.

### Composite prefab export fallback — 2026-08-23

`battle_terrain_007 (3).json` contains both `hsr_elevation_high_castle_wall` and
`aserai_castle_stairs_a_l2`. Its earlier `hsr_elevation_high_castle_wall_withstairs_naved.xml`
included only the stair: `PrefabInliner` could resolve the stock stair through the runtime catalog
but did not find CSC's user-made wall prefab, so the exporter omitted it to avoid a startup-crash
prefab reference. This made the remaining stair look wrongly oriented relative to its missing
companion wall.

`PrefabInliner` now falls back to looking up an exact top-level prefab definition in CSC's module
`Prefabs` directory and the Documents prefab-export directory when the catalog has no source path.
That preserves every placed composite member with its own relative transform. CSC Release rebuilt
successfully (0 errors). The user needs to re-export the composite after copying the build; the
old XML is intentionally not edited in place because it was missing its source part.

### Exported solid-wall cutout markers — 2026-08-23

Verification of the fresh `hsr_elevation_tall_defensive_wall_with_stairs_naved.xml` and
`hsr_elevation_high_castle_wall_withstairs_naved.xml` showed that both now include their wall,
stairs and two `hsr_navelevated` groups. They contained **no** `hsr_navcut` markers, however.
For a solid defensive wall, that leaves its ground footprint walkable and undermines routing.

`SceneExporter` now converts each saved project cutout into a prefab-local `hsr_navcut` rectangle
(centre, yaw, full width and depth) alongside the elevated groups. Homesteads already consumes
that marker format before it adds the elevated surfaces. The user should cut **only the solid wall
entity** in CSC's Cutout mode, save, then re-export after copying the new CSC build. Do not cut out
the stairs: they are an intentionally walkable raised route. Existing exports predate this change
and must be exported again; they are not modified in place. CSC Release build passes (0 errors).

### Gatehouse elevated hand-off repair — 2026-08-23 (ready for live test)

The completed `battle_terrain_007.json` project was rechecked after followers stopped at the
gatehouse stairs and routed around its tunnel. The prior baker snapped the later elevated outline
onto the earlier outline's edge. On this complex building, the physical stair lip and wall-walk
lip are slightly offset, so that snap distorted the later concave outline and reused an interior
edge a third time.

`NavMeshRamp.TryConnectElevatedOutlines` now makes a real two-triangle connector strip between
the two *open* lips instead. Each source outline keeps the physical points the author clicked; the
connector shares one edge with the already-baked stair/deck and one with the new one. This is a
true navmesh graph hand-off, not a visual near-touch or a broad search. Portal candidates remain
one-to-one and require both endpoints within 2.0 m in 3D, so unrelated nearby levels cannot claim
the same seam. Each surface is transactional and structurally validated before it is kept.

Offline regression against the stock `battle_terrain_007` base navmesh now reports: `1 cut out,
0 filled in, 11 ramp(s) built, 0 skipped`, in about 30 seconds. Its read-back audit is structurally
sound and has **one** connected face component. CSC Release was rebuilt successfully with 0 errors
(only existing nullable warnings). No deployment or test-slot copy was made.

This is ready for a live test. Copy/deploy the Release build, reopen the completed
`battle_terrain_007` project, click **Rebake Navmesh**, then choose **Walk Around with Party**.
Lead followers through the tunnel and up every stair to the upper walk. If anyone stops, note the
exact doorway/stair junction; the next fix should be a narrow portal adjustment for that junction,
not a broader proximity rule.

### Wall-with-stairs connector regression — 2026-08-23

The two simpler composite wall projects exposed a concrete defect that the earlier gatehouse
face-count check did not detect: connector landing vertices were created, but the elevated surface
kept its own duplicate vertices at the same coordinates. The result looked joined geometrically
while remaining disconnected in Bannerlord's face graph. `NavMeshRamp.TrySpliceElevatedLanding`
now assigns the actual connector vertex indices back into the surface outline. Near-end deck joins
also reuse the existing deck corner instead of creating a tiny invalid edge, and candidate
selection rejects self-crossing or too-narrow alternatives before choosing a landing.

Both converted wall fixtures were baked from the stock `battle_terrain_007` navmesh and audited:

- `battle_terrain_007 (3).json` (`hsr_elevation_high_castle_wall`): one merged cutout, two
  elevated areas, five elevated faces, **one bottom join and one top join**, one connected island.
- `battle_terrain_007 (4).json` (`hsr_elevation_tall_defensive_wall`): one merged cutout, two
  elevated areas, six elevated faces, one landing seam, **one bottom join and one top join**, one
  connected island.

Both CSC and Homesteads Reloaded Release builds pass with zero errors. Homesteads does not have a
forked copy to update: `Homesteads.csproj` links
`..\..\bannerlord-custom-scene-editor\CustomSceneCreator\NavMesh\*.cs`, so its build compiles this
same corrected baker. No deployment was performed.

The stricter final connected-component guard now refuses an elevated batch if it would add a
detached navmesh island. Under that correct guard, the current eleven-outline gatehouse project
still fails as a whole (six candidate surfaces build, five report local geometry/landing failures,
and the batch is discarded because it would change connected components from one to two). Thus the
two wall-with-stairs assets are ready for live testing, but the complex gatehouse is **not** yet
ready despite the earlier `11 built, 0 skipped` report above; that earlier report did not prove
graph connectivity.

### Live wall-stair follow-up and reusable-slot repair — 2026-08-24

The user's live comparison established an important split result:

- `battle_terrain_007 (4)` is traversable after a forced rebake; all followers reached its wall walk.
- `battle_terrain_007 (3)` has a real top-transition problem. Followers can reach the stair top,
  but normally fail to turn onto the wall walk. One follower crossing accidentally proves the
  faces are connected, but not that the movement surface is reliable.

Binary comparison of the two generated patches found that `(3)`'s entire 10.5 m stair rise had
been merged into one four-corner face with a 0.266 m plane error. The merge was triggered by the
preferred face-size target, not by an invalid source triangle. `NavMeshGeometry` now exposes
`MergeInvalidSlivers`, and elevated outline/landing triangulation uses it: already-valid stair
triangles remain separate, while only a triangle below the hard engine size floor is merged. The
new `(3)` candidate builds six elevated faces (instead of five), keeps one bottom join and one top
join, reads back as one component, and has no non-manifold/bad-reference findings. `(4)` still
passes with the same six-face/one-island result. These are ready for another live comparison after
the user deploys the Release build; no installed module was changed.

The intermittent silent test-launch failure had a separate, proven cause in
`CustomSceneCreator.trace.log`: Bannerlord retained a handle on the previous reusable slot's
`terrain.bin`, while `CscNavMeshTestSlot.Prepare` unnecessarily recopied every base-scene file on
every publish. Opening an unrelated fresh scene released the handle, explaining the user's exact
workaround. The publisher now:

1. recognizes a complete slot already seeded from the same `TargetScene` via
   `csc_source_scene.txt`;
2. refreshes only `navmesh.bin` and metadata for same-base projects;
3. tries the other reusable slot when a genuinely different scene still has a locked slot.

The Saved Projects row also has an explicit translucent blue selected-row fill. It no longer
depends on `Saveload.SaveList.Item`'s selected-state brush, which Realm of Thrones overrides so
completely that the chosen file was visually indistinguishable.

Offline verification after these changes:

- `NavMeshPortTests --ramp-self-test`: both ramp tests passed;
- `(3)` and `(4)` saved-project bakes: one merged cutout, two ramps built, zero skipped, one island;
- CSC Release: 0 warnings, 0 errors; version remains 1.0.4;
- the full eleven-outline `battle_terrain_007.json` gatehouse still correctly rejects its elevated
  batch (six provisional surfaces, five local failures, final component change 1→2). Do not confuse
  the two-wall live-test readiness with gatehouse readiness.

### Complex gatehouse tunnel and multi-handoff repair — 2026-08-24 (ready for live test)

The user's next live test confirmed that the rejected gatehouse batch was not merely conservative:
after the cutout, followers routed around the building and would not enter the central tunnel. Two
separate defects have now been corrected against the actual completed
`battle_terrain_007.json` project:

1. `NavMeshBaker` contained a bounded tunnel-reservation pass, but the bake pipeline never called
   it. The cutout therefore removed the ground route beneath the entire gatehouse before trying
   elevated replacements. The baker now expands the solid cutout into small cells and excludes
   cells covered by the project's explicit low tunnel route. For this fixture it preserves 64
   tunnel/stair cells and cuts the remaining solid footprint as two combined side regions.
2. An elevated outline with a declared shared edge at both ends stopped processing after its first
   successful hand-off. That joined the lower end of the Z50-to-Z56 stair but never joined its top
   to the upper deck. Every non-conflicting declared edge is now processed. A climbing outline can
   therefore join a lower elevated platform and an upper platform in the same transaction.

The second lower stair exposed one additional safe-geometry case: its short stair lip projects into
the middle of a long, narrow triangular deck boundary. Splitting that triangle at both projected
points produced a 0.347 m2 face, below the 0.75 m2 minimum found in the stock
`battle_terrain_007` navmesh. The baker does **not** relax that safety floor. If the split is too
small, it now connects the authored stair lip to the complete pre-approved open deck edge instead,
after the same simple-strip and topology checks. This keeps all resulting faces at or above the
stock minimum.

Final offline regression from the stock `battle_terrain_007/navmesh.bin`:

- the cutout preserves the authored tunnel route;
- 8 elevated outlines build, including both lower stairs, the middle walk, the upper stair, and
  upper deck;
- 3 outlines are skipped: two superseded/redundant low tunnel traces and one redundant small deck
  repair patch;
- 2 solid side regions are cut, 89 elevated faces are added, with 6 lower and 4 upper hand-offs;
- read-back audit: **one connected component**, no non-manifold/duplicate/bad-reference findings,
  and minimum face area remains **0.75 m2**;
- `--ramp-self-test` and the landing-seam regression both pass;
- full CSC Release build succeeds with 0 errors; version remains **1.0.4**.

The candidate is `artifacts/gatehouse_complete.navmesh.bin`; it was not deployed. The normal CSC
**Rebake Navmesh** button will produce this result from the saved project. This is now ready for a
live Walk Around test: verify the central tunnel, both lower stair systems, the middle walk, and the
upper stair/deck. The reusable test-slot lock fix and selected-row blue highlight described above
are included in the same Release output.

### Responsive bake progress in the Saved Projects selector — 2026-08-24

The **Rebake Navmesh** action no longer performs the entire bake inside the Gauntlet button click.
It starts the file-only bake on a worker task and `ProjectBrowserScreen.Tick()` transfers progress
to the ViewModel on the game thread. This matters for the completed gatehouse fixture, whose full
validation can take several minutes: the project popup now remains responsive instead of appearing
to have frozen.

`CSCProjectBrowser.xml` now shows the native Bannerlord boundary-crossing `FillBar` while a bake is
running, with a percentage label. Progress is staged across source-file loading, cutout planning and
application, added-ground processing, each elevated outline that settles as built or skipped,
whole-mesh verification, serialization, and the final atomic file move. Open/test/new/cancel actions
are disabled for the duration so a second scene operation cannot race the output file; they are
restored when the worker finishes. ViewModel changes happen only on the main game thread.

Regression checks:

- CSC Release build: 0 errors; version remains **1.0.4**;
- `CSCProjectBrowser.xml` parses successfully;
- elevated ramp and embedded-landing self-tests pass, including the new 0-to-100 ramp-progress
  callback assertion;
- a complete offline bake of the real eleven-outline `battle_terrain_007.json` gatehouse produced
  a byte-identical result to `artifacts/gatehouse_complete.navmesh.bin`;
- read-back audit remains one component, no non-manifold/duplicate/bad-reference findings, and a
  0.75 m2 minimum face.

Release output was refreshed in `Dist/CustomSceneCreator`; nothing was deployed to the game.

### Stair-mouth throughput and Homesteads release integration — 2026-08-24

The live comparison between `battle_terrain_007 (3)` and `(4)` isolated a graph-quality issue that
the structural audit could not report. `(3)` authored a 1.84 m ground stair lip, but the former
direct join snapped it onto a roughly 1.27 m cutout edge. The route remained one connected island,
yet the resulting portal was narrow enough that part of the follower group queued permanently at
the bottom. `(4)` retained its authored entrance through a tapered landing connector and therefore
handled the group well.

`NavMeshRamp` now preserves 80–125 percent of the authored landing width for simple elevated
composites (up to four outlines). A mismatched edge is rejected as a direct snap so the existing
cutout/landing bridge creates a full-width mouth and tapers safely to the base mesh. Large
multi-level networks retain their one-to-one shared-outline joining policy: applying the simple
width rule to every short internal hand-off rejected valid gatehouse turns. This is deliberate,
not a prefab-name exception.

Regression results from the stock `battle_terrain_007` mesh:

- `(3)`: two ramps, six elevated faces, one bottom and one top hand-off, one component; the full
  1.84 m stair mouth is retained instead of being pinched to 1.27 m.
- `(4)`: two ramps, six elevated faces, one bottom and one top hand-off, one component; its known
  good tapered landing remains unchanged.
- completed gatehouse: two solid cut regions, eight ramps/89 elevated faces, six bottom and four
  top hand-offs, three redundant outlines skipped, one component. The new output is byte-identical
  to `artifacts/gatehouse_complete.navmesh.bin`, the candidate whose tunnel and upper route passed
  the user's live test.
- all three have no non-manifold, duplicate, bad-reference, or bad-bound findings and retain the
  stock 0.75 m2 minimum face area. The new `ramp-width-retention` self-test also passes.

Homesteads Reloaded compiles the exact shared `CustomSceneCreator/NavMesh/*.cs` sources through
linked files in `Homesteads.csproj`; there is no secondary baker to synchronize. Its runtime/online
bake therefore receives this policy automatically. The wall-with-stairs composites, gatehouse
sidecar markers, and Defense-category placeable entries remain in `_Module` and are copied into
`Dist/HomesteadsReloaded` by the Release build. No installed game module or reusable test slot is
modified by this packaging step.

Final packaging verification restored the two authored `hsr_navcut` regions to each combined
wall-with-stairs prefab. Both `homestead_high_castle_wall_with_stairs.xml` and
`homestead_tall_defensive_wall_with_stairs.xml` now contain two exact solid cutouts plus two
elevated route groups, matching their final CSC exports. This is required because Homesteads
deliberately excludes generic linear wall/stair/gate footprints; composite defensive assets must
carry precise prefab markers instead. The fortified gatehouse continues to use its mapped
elevation sidecar and its live-tested complex-network topology.

The Homesteads Release package was rebuilt after that correction with 0 warnings and 0 errors.
Source and `Dist/HomesteadsReloaded` copies of both wall prefabs, the gatehouse sidecar,
`HomesteadsPlaceables.xml`, and `HomesteadsReloaded.dll` were hash-verified identical. The package
is ready for manual deployment from `Homesteads Reloaded/Dist/HomesteadsReloaded`; force a
Homestead navmesh rebuild for existing layouts after copying it.

#### Composite wall transform correction — 2026-08-24

A Homesteads placement test exposed a prefab-conversion error independent of the baker: both
stair child transforms matched the final CSC exports, but their companion wall transforms did not.
The high-castle wall had lost its exported transform entirely; the tall defensive wall retained an
unrelated older transform. Consequently the stairs appeared rotated across the wall even though
the cutout and elevation coordinates were correct.

Both physical wall transforms now match the final `_naved` exports exactly. The remaining stale
tall-wall elevation point was also replaced with its re-exported value. A semantic audit confirms
that, for each composite, the wall transform, stair transform, both cutouts, and both complete
elevation groups now use the same exported coordinate frame. Homesteads Release was rebuilt again
with 0 warnings and 0 errors, and both corrected prefab files plus the DLL are hash-identical in
`Dist/HomesteadsReloaded`. Nothing was copied into the installed game.

#### Homesteads native-preview wrappers — 2026-08-24

The Fortified Gatehouse, Tall Defensive Wall (without stairs), and High Castle Wall (without
stairs) were still catalogued with TaleWorlds native prefab names. Those assets exist, but
`Utils.CreateGameEntityWithPrefab` cannot reliably instantiate these three natives in the
Homestead runtime placement-preview context, leaving no ghost and no placed geometry.

The Defense entries now use explicit module-owned wrappers:

- `homestead_fortified_gatehouse.xml` contains the main gatehouse, its dedicated step-enabled
  stair collision, and all 23 separate front/back/upper merlon mesh-and-physics children;
- `homestead_high_castle_wall.xml` contains the wall body and all seven separate merlon children;
- `homestead_tall_defensive_wall.xml` contains the native wall mesh and collision explicitly.

`HomesteadsPlaceables.xml` points to those wrapper names. `PrefabNavCutMarkers.ElevatedSidecar`
maps both the old native names and the new wrapper names to the previously authored elevation
sidecars, so correcting the visible prefab does not discard any navmesh authoring. The placement
height-offset table likewise recognizes both old saves and new wrapper names.

Validation: all four edited XML files parse; the gatehouse wrapper matches all 25 mesh names and
all 25 collision shapes from the complete CSC export; the high wall matches all eight meshes and
collisions; Release builds with 0 errors; and all three source prefabs are SHA-256-identical to
their copies in `Dist/HomesteadsReloaded/Prefabs`. The package was not deployed to the game.

#### Raised platform wrapper and Homesteads elevation-authoring parity — 2026-08-24

The Raised Fighting Platform used the native name `battania_platform_a`, but an obsolete
marker-only development export with that same native name remained in `Dist`. Bannerlord resolved
the module override instead of Native's visible platform, so the placement preview and built asset
were empty. The catalog now uses the explicit module-owned
`homestead_raised_fighting_platform.xml`, containing the complete 141-entity visible/physics export.
Its authored elevation markers remain in `hsr_elevation_battania_platform_naved.xml`, mapped to the
new wrapper. `Homesteads.csproj` removes the obsolete native-name file from Dist after every build,
and `build and deploy.bat` also removes it from the installed module to make overlay/manual-copy
histories safe.

Homesteads' player-authored Elevated Navmesh mode now matches the relevant CSC interaction model:

- it receives the raw physical ray hit before ordinary building placement snaps/locks Z, fixing
  dirt mound and raised-building-plot points that previously landed on terrain;
- a cyan ghost shows the exact XYZ that will be stored;
- saved corners are cyan, the active draft is orange, a selected area is gold, and its selected
  corner is blue;
- click a saved corner and then a physical surface to move it; `Delete` removes the selected corner
  and reopens that outline for repair;
- while drawing, only the active outline's own points can be selected, so separate routes can share
  or overlap corners; after `F` closes an outline, the next click starts a new route.

Homesteads Release builds with 0 errors and refreshed `Dist/HomesteadsReloaded`. Source and Dist
copies of the full raised-platform wrapper are SHA-256-identical, its marker sidecar is present, and
the obsolete `Dist/Prefabs/battania_platform_a.xml` is absent. Nothing was deployed. Live acceptance
after deploying and restarting the game: verify the Raised Fighting Platform has a visible ghost
and placed model; then use **Navmesh → Add Elevated Navmesh** on a Large Building Plot or dirt mound
and confirm the cyan ghost and saved corners remain on the physical raised surface through close,
save, re-entry, and bake.

#### CSC 1.0.4 documentation and release audit — 2026-08-24

The release documentation was audited against the current editor, file-side baker, test-slot
workflow, and all four export types. `USER_MANUAL.htm` and `README.md` now document Cutout, Add Area,
Elevated Navmesh, forced rebakes/progress, Walk Around/Raid tests, skip handling, the complete derived
scene workflow, and the fact that `SceneEditData` is optional editor data rather than runtime content.
`MOD_INTEGRATION.md` is the concise mod-author shipping guide: it distinguishes projects, templates,
prefabs, absolute fragments, `.navcut.json`, durable per-project bakes, complete `SceneObj` folders,
and disposable test slots. Prefab markers are explicitly described as local authoring metadata that
requires a compatible pre-load baker in the consuming mod; templates explicitly do not carry
navmesh authoring.

`tools/README_NAVMESH.md` no longer presents the old Python candidate workflow as the normal path.
It identifies the in-mod C# baker as authoritative and explains that Python supports inspection,
cutouts, and terrain-level additions but not elevated routes. `package_release.ps1` now includes the
integration guide and scene-folder checker, requires the release documents, validates project and
SubModule versions agree, and generates current first-step/navmesh instructions.

The absolute scene-fragment exporter now writes its matching navmesh manifest for elevated-only
projects as well as cutouts/ground additions. The generated `CSC_MODDING_KIT_HANDOFF.txt` is written
after the bake and records the real bake result instead of always claiming the copied mesh is stale.
All public version declarations remain pinned at 1.0.4.

Final release verification: CSC Release/x64 builds with 0 errors; all 49 Python navmesh tests pass
(one unavailable external fixture is skipped); the HTML manual and module XML parse; source and Dist
copies of both release documents are hash-identical; and `package_release.ps1` produced
`Release/CustomSceneCreator-v1.0.4.zip`. The archive contains the loadable module, current manual,
`MOD_INTEGRATION.md`, generated installation README, license, advanced navmesh guide, and the
scene-folder audit tool. Its packaged `SubModule.xml` is verified as v1.0.4. Nothing was deployed to
the installed game.

#### CSC follow and raid test documentation — 2026-08-24

The 1.0.4 release docs now give **Walk Around** and **Raid-Scale Battle** dedicated, implementation-
accurate instructions in `USER_MANUAL.htm`, `MOD_INTEGRATION.md`, `README.md`, and the packaged
installation README. A prior manual sentence incorrectly said peasants merely filled a short party
to five followers; the code always spawns every healthy companion hero plus five additional Empire
peasants. That is now stated correctly.

Walk Around is documented as the exact traversal test for tunnels, stairs, ramps, bridges, raised
surfaces, narrow turns, and ground/elevated transitions. Raid-Scale Battle is documented as the
crowd/combat-routing test: it copies the healthy player party, creates a matching on-foot raid force
(capped at 100 per side), and—with authored cutouts—places the sides across the obstacle cluster at
least 200 metres apart. The shorter fallback placement for projects without cutouts is disclosed so
authors do not mistake it for an obstacle-routing proof. Both modes are mission-only, restore the
selected project's objects and baked navmesh through a disposable test slot, and do not alter the
campaign roster or casualties. Test slots remain explicitly excluded from shipped scene content.

#### Nova Kuchen all-or-nothing bake report was from a stale installed DLL — 2026-08-24

The reported Nova Kuchen result (`82 of 100 building(s)` repeated, followed by
`Navmesh unchanged - nothing could be baked`) was correlated with the trace at **15:27:17**. That
run used the older settlement-wide rollback path: the trace ends with `Discarded every cutout` and
`Skipped all 17 ramp(s)` and contains no `retrying independent building groups`, `Recovered cutout
group`, or per-elevated-group recovery entries.

The current shared `NavMeshBaker` does not use that failure policy. If the fast combined cutout pass
would split the mesh, it rolls back the provisional batch and retries merged physical structures as
independent transactions; a failing merged wall is then retried panel by panel. Elevated structures
are clustered and committed independently, with a second per-outline retry pass for a mixed cluster.
One unusable building or route is therefore counted and logged without throwing away accepted work.

The UI wording also fingerprints the old binary: current `HomesteadNavMeshBakeJob` reports normalized
progress as `82%`, not `82 of 100 building(s)`. The installed game DLL was timestamped **15:30:08**,
while the fresh verified Dist DLL was rebuilt at **15:54:42**. The latest Dist copies for both client
folders are SHA-256 `7FC05AE9BA1AEA275EDEF02836D8C762D8E1FDB154B293BD239541C3F251B0A9`.
No installed module was overwritten.

Verification completed against this build:

- `NavMeshPortTests --ramp-self-test`: normal ramp, landing seam, and width-retention tests pass;
- Homesteads Reloaded Release/x64: **0 errors** (existing nullable warnings remain);
- both Dist client DLLs are byte-identical.

Next live acceptance after copying the fresh Dist build and restarting Bannerlord: force the Nova
Kuchen navmesh rebuild. During the bake the progress line must contain a percent sign. If the fast
pass is unsafe, the trace must show independent-group retries and the final message must retain any
safe cutouts/elevated routes instead of the old settlement-wide `nothing could be baked` result.

#### CSC editor input isolation — 2026-08-29

CSC first/third-person editing now uses a Harmony prefix on Bannerlord's native main-agent control
tick. While any CSC edit mode is active, the native controller is replaced with a movement-only
whitelist (W/A/S/D); all combat, interaction, posture, weapon, mount, and remapped action bindings
are excluded. Mouse movement remains available for looking. The scene-layer restriction separately
keeps mouse buttons and the wheel away from native controls while CSC continues reading the wheel
directly for preview height.

The three historically troublesome paths were audited explicitly: `E` reaches only CSC's rotate-
right handler, the mouse wheel reaches only CSC's preview-height handler, and Backslash changes edit
mode before native controller processing. Backslash exit now retains the controller guard through
that final screen tick, preventing the same keypress—or an action remapped onto it—from firing as
normal controls return. RTS remains exempt because it assigns the main agent to AI control.

Harmony is now a required module dependency and the runtime patch is installed during submodule
load. The manual and README explain the dependency and the full action lock. CSC remains version
1.0.4. Release/x64 builds with 0 errors (31 existing nullable warnings), and the updated module is
staged in `Dist/CustomSceneCreator`; it has not been copied into the live game installation.

#### Wall-mounted asset alignment — 2026-08-29

CSC 1.0.4 now has a rebindable **Align To Aimed Surface** action, default `N`. It samples several
nearby collision rays to reconstruct the aimed wall/floor/roof plane, aligns the held object's flat
local face to that plane, and preserves an upright orientation on walls. If the surface is too small
for the auxiliary samples it safely falls back to the primary placement ray. Q/E remains available
to rotate the object within the aligned surface. The Build HUD, README, manual, quick-reference
table, and generated controls graphic document the action. The feature is intended for flat props
such as `hsr_picture_panel`, whose exact wall alignment was previously impractical with free-form
tilt alone. No live module was overwritten.

The already-exported `big_cozy_house_naved.xml` was also corrected in place. Its embedded
`hsr_picture_panel_1` keeps the authored position and crest tag, but its rotation is now exactly
vertical and matched to the house wall (`0.000, -1.571, 0.451`) instead of the manually estimated
`0.005, -1.533, 0.354`. The XML parses successfully and its existing navmesh metadata was preserved.

#### CSC saved-object scaling — 2026-08-29

CSC 1.0.4 now exposes root-local X/Y/Z scale in the `L` scene contents list. Scale is stored
separately from the pure rotation matrix to prevent repeated rotation edits from compounding it,
and missing scale in older project JSON defaults to `1,1,1`. It is applied when restoring projects,
placing/picking up objects, expanding templates, and loading Walk Around tests, and is emitted as a
Bannerlord `scale="x, y, z"` transform in prefab and scene exports. Cutout footprints refresh after
a scale edit. User-authored Add Area and Elevated Navmesh geometry remains world-space and should be
redrawn after resizing its supporting object. Invalid, negative, near-zero, or extreme values are
rejected; the UI accepts `0.01` through `100` per axis. Version remains 1.0.4 and no live module was
overwritten.

#### Navigation-only RTS camera — 2026-08-29

The `V` camera-cycle action is now handled while CSC editing is Off. Entering RTS in that state
creates a navigation-only free camera: ordinary WASD panning preserves absolute world Z and skips
the terrain-clearance clamp, while Space/Alt and Shift-flight remain deliberate ways to change
height. Follow-up: fixed-height RTS now also applies during active build/edit/navmesh tools. Horizontal
movement never samples terrain, floors, or roofs to alter camera Z; only Space/Alt or intentional
Shift-flight changes elevation. This prevents camera jumps inside structures and across stacked levels.
Turning an edit tool on or off updates this context even after the player has manually chosen a
camera. The Off-mode HUD and user documentation describe the new free-camera option. Version remains
1.0.4 and no live module was overwritten.

#### CSC 1.0.5 texture-preview false-positive correction — 2026-08-30

The first live PNG swaps logged `Texture applied` but produced no visible change. The PNG decoder,
material-name match, and mesh replacement all completed, which isolated the false positive to the
material-slot assumption: the first implementation replaced only `DiffuseMap`, while Bannerlord
materials can keep their displayed colour in an existing `DiffuseMap2` slot.

The CSC 1.0.5 applicator now replaces `DiffuseMap` plus an existing `DiffuseMap2`, reads both native
texture pointers back after `SetMaterial`, and counts the mesh only when the requested dynamic texture
is retained. The trace records the PNG dimensions, material, source texture names, slots changed, and
verification results. The editor/manual/integration wording now says visible diffuse slots instead of
claiming a single primary slot. Release/x64 builds with 0 errors (31 existing nullable warnings) and
stages the updated 1.0.5 DLL in `Dist/CustomSceneCreator`. No live module was overwritten.

Next live acceptance: deploy the staged module, fully restart Bannerlord, reopen a saved object, choose
an unmistakable PNG and exact material, then press **Apply Preview**. If that material still does not
change, preserve the new `TextureOverrideApplicator` trace line: it will distinguish a slot-retention
failure from a shader/UV/material limitation instead of reporting an unverified success.

#### CSC 1.0.5 imported-prefab Break Apart — 2026-08-31

The `L` Scene Contents list now exposes **Break Apart** for a selected composite imported through
**My Prefabs**. CSC reverses its own composite export into normal editable child prefabs and known
CSC markers, composing the root and nested local transforms so position, rotation, scale, and direct
supported scripts survive. The source prefab file is not changed or removed. The replacement is
transactional: all children must instantiate before the original scene instance is removed, and a
failure rolls back the attempted children.

The command deliberately does not appear for arbitrary native prefabs, whose internal render meshes
are not independently placeable assets. Embedded Cutout, Add Area, and Elevated Navmesh authoring is
restored to the children; a texture override on the whole composite is not distributed to them. If
anonymous geometry has no reusable prefab identity, CSC refuses the operation
and leaves the composite intact instead of silently losing geometry. Scripts attached to the composite
as a whole also block expansion until the author removes or relocates them, since no single child is
their unambiguous owner. README, user manual, and the
shared scene-authoring guide document the workflow and boundary. Release/x64 builds with 0 errors
(31 existing nullable warnings), the outliner XML validates, and CSC remains version 1.0.5. The staged
build has not been copied to the live game installation.

#### CSC 1.0.5 Scene Contents double-click pickup — 2026-08-31

Double-clicking an object in the `L` Scene Contents list now closes the list, makes that object the
active carried selection, and enters Move mode through the same path as the existing **Pick Up**
button. **Go To** remains a separate camera-only action that leaves the list open. The List hint,
README, and user manual describe the distinction. CSC remains version 1.0.5.

#### CSC 1.0.5 Break Apart restores navmesh authoring — 2026-08-31

**Break Apart** now reconstructs embedded Cutout, Add Area, and Elevated Navmesh authoring alongside
the child objects. `PrefabBreakApartService` composes every marker through the composite's complete
position/rotation/scale hierarchy and restores project-space cutout corners, requirement boundaries,
and closed elevated outlines. The transaction adds that authoring only after every child object has
instantiated successfully, so a failed expansion still leaves both the original object and project
navmesh data unchanged.

Future **Export as Prefab** files carry CSC-only `csc_navrequired` boundary markers for Add Area data,
plus `csc_source_id`/`csc_owner_id` links that reconnect one or more cutouts to their newly created
child after expansion. Older exports still restore the cutout and elevated geometry they contain;
because they lack ownership IDs, their cutouts become independent bakeable project areas. Unknown
`csc_nav*`/`hsr_nav*` groups count as unsupported and prevent partial destructive expansion. The user
manual, integration guide, README, and shared scene-authoring guide describe the behavior. CSC remains
version 1.0.5 and no live module was overwritten.

#### CSC 1.0.5 texture override verification — 2026-09-01

`TextureOverrideApplicator` now verifies the material that the mesh actually retains after
`Mesh.SetMaterial`, rather than verifying only the temporary copied material. This closes a false-success
path where CSC could report a PNG as applied while the engine had silently kept the original mesh material.
The trace now records the active material name, whether the native material assignment succeeded, and the
actual DiffuseMap/DiffuseMap2 readback. This source change is built locally but has not been deployed; the
next in-game texture assignment should either visibly update or report a concrete engine-retention failure
instead of claiming success without evidence.

#### Shared documentation refresh — 2026-09-01

The reusable texture-assignment lesson is now recorded in `F:\Bannerlord Mods\docs\runtime_materials.md`
and `F:\Bannerlord Mods\docs\lessons_learned.md`: verify the material returned by `mesh.GetMaterial()`
after `SetMaterial`, then verify its active diffuse slots. A valid detached material copy is not proof
that Bannerlord replaced the material being rendered. `docs\navmesh.md` was reviewed and already carries
the current shared-corner, physical-XYZ, independent-fallback, gatehouse-tunnel, landing-width, and
follower-throughput guidance, so its review date was advanced without duplicating those sections.

#### CSC 1.0.5 Elevated Navmesh Scene Contents tab — 2026-09-02

The CSC `L` **Scene Contents** panel now has separate **Objects** and **Elevated Navmesh** tabs.
The elevated tab is an inspection view: it lists every saved elevated area/segment, whether it is
closed or still being drafted, and its saved perimeter corners nested underneath with exact X/Y/Z
coordinates. It also reads older two-rail project data so legacy saved scenes remain inspectable.
Editing remains in **Add Elevated Navmesh** mode; the list deliberately does not risk changing an
area by accident. The outliner XML was checked as well-formed and the Release/x64 CSC build succeeds
with 0 errors (31 pre-existing nullable warnings). This build has not been deployed to the live game.

#### CSC 1.0.5 In-world elevated perimeter numbering — 2026-09-02

Modern elevated-navmesh outlines now render one-based perimeter numbers above every
corner. The digits are camera-facing seven-segment marker geometry, rather than
debug text that is invisible in retail missions. The numbers exactly match the
`L` → **Elevated Navmesh** segment/node list; the selected corner uses the
selected blue colour. Legacy two-rail data remains labelled as Left/Right in the
list rather than receiving ambiguous perimeter numbers. README updated. Release
x64 build passed with 0 errors (31 existing nullable warnings); not deployed.

#### CSC 1.0.5 Elevated-node label/readback improvements — 2026-09-02

Elevated-outline node numbers are now rendered as larger high-contrast marker geometry, raised
above the authored overlay and laid flat in the world X/Y plane. This prevents the former
camera-facing glyphs becoming edge-on in normal RTS/third-person editing views. The `L` →
**Elevated Navmesh** tab now also supports a double-click on a saved perimeter-corner row: it
closes the list, enters **Add Elevated Navmesh**, and selects that exact node so the next normal
placement click moves it. This routes the list action through the same selected-point path as a
world click; it does not create a separate editing model. Legacy two-rail records remain listed
as Left/Right and can be selected the same way. Build verification is pending after this change.

#### CSC 1.0.5 object transform panel — 2026-09-03

The first in-world ring experiment successfully selected an axis but its RMB drag was not dependable
in live missions, so it has been replaced rather than treated as a user-training issue. `L` →
**Scene Contents** → **Transform** now opens a compact, non-modal right-side panel with exact
X/Pitch, Y/Roll, and Z/Yaw degree fields plus coloured `-` / `+` controls and a configurable step.
The familiar red, green, and blue in-world rings remain as a spatial axis reference, and left-clicking
one still identifies that axis in the editor status. The panel only claims mouse buttons when the
cursor is physically over the panel; outside it, the editor camera and normal scene navigation remain
live for alignment. `\` or **Done** exits transform mode. This is based on the passive Gauntlet input
claim pattern used by Illegitimate Children rather than a full-screen modal layer.

README and outliner wording now describe the panel, not the retired RMB-drag behaviour. The Release/x64
build succeeds with 0 errors (31 existing nullable warnings); it has **not** been deployed or live-tested
yet. The immediate test is: select an object in `L`, choose Transform, enter an exact value and use each
axis button, then move the camera while the pointer is outside the right-side panel.

#### CSC 1.0.5 texture overrides target mesh surfaces — 2026-09-03

The first texture UI grouped all meshes by their material resource name. That is unsafe on ordinary
Bannerlord assets because a roof, walls, and trim can share a single material: applying a picture PNG
to one named material repainted every one of those meshes and produced apparently broken lighting and
shadows. The picker now enumerates actual mesh surfaces (`Surface 1: material_name`, etc.) and writes a
stable traversal `MeshIndex` with the material name. New overrides affect only that surface; the baker
also verifies the saved surface still names the expected material before applying, so a changed prefab
cannot silently paint a different mesh. Existing project and imported-manifest overrides retain
`MeshIndex = -1` and therefore keep the old material-wide behavior for backwards compatibility.

This does not make arbitrary runtime PNGs unlit: the source material's shader, normal/specular maps,
UVs, and baked-lighting assumptions are deliberately preserved. Use a dedicated simple panel/frame for
poster-style artwork rather than a whole building surface. README and the shared runtime-material notes
now document that boundary. Release/x64 build succeeds with 0 errors (31 existing nullable warnings);
no game deployment was performed.

#### CSC 1.0.5 texture panel control layout — 2026-09-05

The texture panel acquired alpha, alpha-cutoff, and blend controls after its original fixed
520px dialog was designed. Those rows were being drawn beyond the popup frame. The popup is
now 840px tall, keeping the full surface/image/alpha/cutoff/blend/action workflow inside the
framed dialog at normal desktop resolutions. Release/x64 build succeeds with 0 warnings and
0 errors; it has not been deployed or live-tested.

#### CSC 1.0.5 asset-picker live prefab preview — 2026-09-05

The CSC asset picker now uses three columns: the filtered asset list, its authored details, and
a live 3D preview of the selected prefab. The preview is a `SceneTextureProvider` backed by a
small private scene, never the active editor mission. Its prefab is instantiated inertly (physics
off and no interaction with the editor scene), so browsing a scripted asset cannot run its normal
mission behavior or affect authored content. The UI provider owns a valid empty scene from its
constructor so Gauntlet's deferred texture sizing cannot hand a null scene to its tableau. The
borrowed stock scene contributes only the camera script and light rig; its scenery is hidden.

Drag within the preview column to turn the selected asset, and scroll while over that column to
zoom. The list's ordinary mouse wheel behaviour is unchanged. The picker grows to 1680px wide so
the new column does not crowd the existing search/list/detail workflow. Release/x64 build succeeds
with 0 errors (31 existing nullable warnings); it has not been deployed or live-tested.

#### CSC 1.0.5 transform-panel exit and presentation — 2026-09-05

The retired in-world transform rings are no longer rendered or used for selection; transform is now
entirely the exact right-side panel. Its status/HUD and panel help no longer describe ring clicks or
RMB dragging. The panel's former 490px root was shorter than its 536px of controls, which put the
visible **Done** button outside its own input hit rectangle. It is now 560px tall, so Done is inside
the clickable panel. Escape also closes through either the Gauntlet layer input or raw mission input,
covering RTS and attached camera modes. Release/x64 build succeeds with 0 errors (31 existing nullable
warnings); it has not been deployed or live-tested.

#### CSC 1.0.5 RMB held-object rotation input unification — 2026-09-06

RMB object rotation previously mixed two input paths. The combat-suppression layer intentionally
removes mouse buttons from the scene-layer input in attached edit cameras, while the camera-hold and
RTS placement-ray code still checked only that blocked source. The result was exactly the observed
split behavior: attached camera movement continued during a valid raw RMB rotation, while RTS could
keep refreshing the placement ray and lose the drag delta through a transient layer owner.

Held-state checks now accept either the scene-layer or raw RMB path. In attached edit cameras the
camera's complete RMB-down frame is captured and restored after the native camera tick (camera frame,
mission frame, scene view, bearing, and elevation), while the object continues receiving the mouse
delta. RTS uses the same raw-button held state to freeze the placement ray and falls back to the
mission-logic raw mouse delta if its scene layer reports zero. The attached-frame hold is explicitly
limited to active CSC editing, so normal gameplay RMB remains untouched. Release/x64 build succeeds
with 0 errors (32 existing nullable warnings); it has not been deployed or live-tested.
