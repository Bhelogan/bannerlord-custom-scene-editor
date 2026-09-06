# Using Custom Scene Creator output in your own mod

This guide is for **Custom Scene Creator 1.0.5** and Bannerlord **1.4.7**. It explains which export
to choose, which files belong in a release, and what navmesh or texture data does—and does not—do
by itself.

## Choose the artifact first

| What you want | CSC command | Finished artifact | Put in your mod |
|---|---|---|---|
| One reusable building or construction | **Export as Prefab** | `exports/prefabs/<name>.xml` | `YourMod/Prefabs/<name>.xml` |
| A reusable layout whose pieces remain editable in CSC | **Export as Template** | `exports/templates/<name>.json` | Do not ship as runtime content; share with other CSC authors |
| Objects at exact coordinates in an existing scene | **Export Whole Scene** | `exports/scenes/<name>.scene_fragment.xml` | Merge its entities into the target `scene.xscene`; use its navmesh plan when baking |
| A complete new scene based on an existing map | **Create Modding Kit Scene** | `CustomSceneCreator/SceneObj/csc_<name>/` | `YourMod/SceneObj/csc_<name>/` |

Your saved project in `projects/<name>.json` is the editable source. Keep it in source control or a
backup, but Bannerlord does not load it as part of your mod.

If any placed object has a texture override, the export also has
`<name>.texture_overrides.json` and `<name>_textures/`. Keep those together with the main artifact;
the XML/JSON layout cannot embed the PNG itself.

Templates intentionally contain only the visible entities. Their pieces stay editable, but cutouts,
ground additions, and elevated routes are not copied into the destination project. Redraw that
authoring after placement, or use a prefab/complete-scene export when navigation must travel with it.

## Reusable prefab workflow

1. Build the visible object in CSC.
2. Use **Navmesh Cutout** on solid parts that should remove walkable ground.
3. Use **Elevated Navmesh** around stairs, ramps, bridge decks, platforms, tunnels, and wall walks.
   Click at least four perimeter points in order and press `F` to close each area.
4. Save the project, then choose **Export as Prefab**.
5. Copy the XML from Documents into `YourMod/Prefabs/` and give it a unique name owned by your mod.
6. Restart Bannerlord before testing; prefab XML is registered only during startup.

The XML contains the visible children plus lightweight `hsr_navcut`, `hsr_navelevated`,
`csc_navrequired`, and child `hsr_navpoint`/`csc_navpoint` authoring markers in **prefab-local
coordinates**. They rotate and move with the prefab. The markers are metadata: they have no visible
mesh and do not change AI routing merely by being instantiated. If the prefab is later placed in CSC
and **Break Apart** is used, all three authoring types are reconstructed in world coordinates for
editing. New exports also record which broken-out child owns each cutout.

If your mod places the prefab dynamically, your mod must apply those markers with a compatible
file-side baker **before the target scene is loaded**. Homesteads Reloaded uses this architecture.
Copying the XML alone gives you the object, but not a changed stock-scene navmesh.

```csharp
GameEntity instance = GameEntity.Instantiate(mission.Scene, "my_fortified_camp", frame);
```

That code instantiates the already-registered prefab. It is not a navmesh bake call.

## Texture override workflow

1. Put an 8-bit, non-interlaced grayscale, RGB, or RGBA PNG in
   `Documents/Mount and Blade II Bannerlord/CustomSceneCreator/textures/`.
2. In CSC press `L`, select the placed object, then choose **Textures**.
3. Select the exact material name and PNG, then apply it. Save and use both built-in test modes.
4. Export normally. Keep `<name>.texture_overrides.json` beside `<name>_textures/`.

The manifest is versioned JSON. Each entry identifies the source object by CSC ID, prefab, and
position, then lists exact material name → PNG filename replacements. The image folder named by
`ImageFolder` is relative to the manifest:

```json
{
  "Version": 1,
  "Export": "painted_barrier",
  "Kind": "Prefab",
  "ImageFolder": "painted_barrier_textures",
  "Entities": [{
    "Id": "...",
    "Prefab": "barrier_ai_04x04m",
    "Position": [100.0, 200.0, 12.0],
    "Overrides": [{ "Image": "outline.png", "Material": "barrier_mat" }]
  }]
}
```

At runtime the consuming mod must decode the PNG, create a Bannerlord `Texture`, copy the matching
material, replace its `DiffuseMap` and any existing `DiffuseMap2`, and set that copied material on the matching meshes. Hold strong
references to the created textures and materials for the mission lifetime. Homesteads Reloaded's
picture-frame implementation is a working reference for this engine pattern.

Do **not** blindly turn on alpha blending or change shader flags for arbitrary materials. Preserve
the original material state. Transparency is reliable only when the source material already uses a
compatible shader; a custom cutout/transparent asset should be authored with that material in the
official asset pipeline first.

CSC projects and its Walk Around/Raid tests apply overrides automatically. Shared templates do too,
provided `<name>.json` and `<name>_textures/` remain together. Prefab/scene XML used by another mod
does not; that mod must consume the sidecar or convert the PNG to registered resources.

## Complete scene workflow

1. Save the project. The save writes:
   - `projects/<project>.json` — editable source;
   - `exports/navmesh-cutouts/<project>.navcut.json` — readable authoring plan;
   - `exports/navmesh/<project>.navmesh.bin` — durable baked result.
2. In **Saved Projects**, use **Rebake Navmesh** when you want a forced fresh bake. Wait for the
   progress display and read the result. Resolve every skipped operation before release.
3. Run **Walk Around**. Followers should traverse tunnels, stairs, ramps, and raised surfaces.
4. Run **Raid-Scale Battle** when the scene is intended for combat.
5. Choose **Create Modding Kit Scene**. CSC clones the base scene, inserts the objects, and bakes the
   matching `navmesh.bin` inside the new `SceneObj/csc_<name>/` folder.
6. Copy that entire folder, without renaming it, to `YourMod/SceneObj/csc_<name>/`.

The folder name and the `<scene name="csc_<name>">` value inside `scene.xscene` must match exactly.
Do not place a partial folder named after a stock scene in your module; it can shadow the real scene.

`SceneEditData/csc_<name>/` is useful only if you want to continue editing the scene in the official
Modding Kit. It is not required by the retail game and does not need to be included in a player-facing
release. If CSC reports all navmesh operations applied, opening the Kit is optional. Use the Kit only
for terrain work or navmesh edits CSC cannot express.

## Absolute scene fragments

**Export Whole Scene** writes only the placed entities at their absolute scene coordinates. Paste
them inside the target scene's `<entities>` block. This is useful for a racetrack or another layout
that belongs at one exact location, but it is not a complete scene folder and it does not carry the
base terrain files.

CSC also writes a `.navcut.json` plan when that project has navmesh authoring. The plan is not read by
Bannerlord. Apply it with CSC's baker to the exact matching base scene, or recreate the authoring in a
project and export a complete scene. Never pair a baked navmesh with a different scene or terrain.

## What each navmesh file means

| File | Purpose | Ship it? |
|---|---|---|
| `projects/<name>.json` | CSC source project, including authoring marks | No; keep as source/backup |
| `exports/navmesh-cutouts/<name>.navcut.json` | Human-readable cutout, ground-addition, and elevated-route plan | Only for another tool/mod that deliberately consumes it |
| `exports/navmesh/<name>.navmesh.bin` | Baked mesh for that exact project and base scene | Normally no; the complete scene export places the correct copy for you |
| `SceneObj/<scene>/navmesh.bin` | Runtime navmesh loaded with that scene | Yes, as part of the complete `SceneObj` folder |
| `SceneObj/<scene>/navmesh.bin.prebake` | Safety backup made while baking | No |
| `<export>.texture_overrides.json` | Object/material/PNG mapping for runtime texture replacement | Yes, when the export uses overrides and your mod consumes it |
| `<export>_textures/*.png` | Images named by the texture manifest | Yes, when the export uses overrides |

The disposable `csc_navmesh_test_slot_*` scenes exist only so CSC can load a saved project's mesh in
Walk Around and Raid-Scale tests. Never use or ship a test slot as your finished scene.

## Testing a saved project

Select the project in **Saved Projects** before choosing either test. Both modes restore that
project's visible objects and publish its matching baked navmesh into a disposable test slot. They
do not edit the project, alter the campaign party, or turn the test slot into release content.

### Walk Around

- Opens a non-combat mission with the player, every healthy companion hero currently in the main
  party, and **five additional Empire peasants**. A fresh campaign therefore still has pathing
  probes even when it has no companions.
- All followers are on foot and ordered to follow the player.
- Lead them through every tunnel and doorway and across every ground/elevated transition. Then test
  every stair, ramp, bridge deck, platform, and wall walk in both directions.
- A few followers reaching a surface is not sufficient. Crowding at a stair foot, turning point, or
  top landing usually indicates a narrow, disconnected, or badly aligned transition.

### Raid-Scale Battle

- Opens the same saved layout and baked navmesh as a combat mission.
- Copies healthy members of the current main party beside the player, up to 99 additional agents.
  The opposing bandit/looter force mirrors the healthy party size, capped at 100. All agents fight
  on foot, and casualties remain inside the test mission.
- When the project has navmesh cutouts, CSC places the sides on opposite sides of the cutout cluster
  and at least 200 metres apart. This forces a meaningful route around the authored construction.
- A project without cutouts uses a shorter fallback placement around its objects. Add the required
  solid cutouts before using this mode as proof that agents can route around an obstacle.

Use **Walk Around** to prove exact traversal and **Raid-Scale Battle** to expose crowding, formation,
and combat-routing failures. A scene intended for combat should pass both.

## Release checks

- The project bake reports no unexplained skips.
- **Walk Around** followers enter every required tunnel and climb every required stair/ramp.
- **Raid-Scale Battle** agents can route around solid construction when the scene supports combat.
- The shipped `SceneObj` contains `scene.xscene`, `terrain.bin` (when the base has one), and
  `navmesh.bin`.
- The `SceneObj` folder name matches the internal scene name.
- Reusable prefab XML is in your own module's `Prefabs/` folder and uses a unique name.
- Your module does not contain a partial folder that shadows a stock scene.
- You tested after a full game restart whenever prefab XML changed.
- Every textured export includes its manifest and image directory, and the consuming mod applies or
  converts those overrides rather than expecting prefab XML to embed the PNG.

For visual authoring instructions, open `USER_MANUAL.htm` and read **Edit modes → Navmesh baking**.
For binary inspection and regression tools, see `tools/README_NAVMESH.md`.
