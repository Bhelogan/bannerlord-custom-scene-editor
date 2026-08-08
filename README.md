# Custom Bannerlord Scene Creator

A standalone in-game scene editor for **Mount & Blade II: Bannerlord v1.4.7**.

Open any shipped scene, place any shipped prefab or logical marker, and export the result — without
needing a homestead or the Modding Kit.

> **Status: early development, but usable.** Entered from inside a campaign (settlement menu or
> `csc.open`). Scene browser, asset catalog, RTS/third/first-person cameras, and build/delete/move
> with JSON project save-load are all in, plus a searchable asset picker and editor-authored marker
> packs, export as a reusable prefab or a whole-scene fragment, and script attachment. Still to come:
> entity-reference variables, and scene derivation.
> See [CUSTOM_SCENE_CREATOR_PLAN.md](CUSTOM_SCENE_CREATOR_PLAN.md) for the full design and
> milestone list.

---

## Why

The existing `HomesteadBuilder` mod is a Harmony shim over Homesteads Reloaded's building editor. It
works, but it requires a running campaign and an owned homestead, it reaches into another mod's
private fields by name, and its map picker exposes about 10% of the scenes the game ships.

This replaces it. The editor becomes a library any mod can consume (including Homesteads), and the
app around it is campaign-free.

## What it will do

- **All 611 shipped scenes** — battle terrain, multiplayer maps, towns, castles, villages, hideouts,
  arenas, interiors, naval. Categorized and searchable, with correct upgrade-level handling.
- **Derived scenes** — copy a shipped scene's terrain, navmesh, flora and atmosphere, but strip its
  mission scripts, so you get a hideout's landscape without the hideout's logic.
- **Logical placeables** — spawn points, navigation nodes, race gates and patrol points as
  first-class objects with proxy meshes. Declared in `ModuleData/packs/*.xml`, so a mod or a user can
  add their own markers without a rebuild. They export under their declared name and tag, so
  `FindEntitiesWithTag("sp_enemy")` finds them.
- **Script attachment** — in **Script** mode, click a placed object to see what scripts are on it,
  add more from a searchable list of the 130 the game ships, and edit their variables. Attached
  scripts preview live where they can, and are written out on export either way.
- **Export** — project JSON, prefab XML, or a scene fragment you can paste into a real `scene.xscene`
  for the Modding Kit to bake a navmesh over.

## Building

Requires the .NET SDK. Reference assemblies come from NuGet (`Bannerlord.ReferenceAssemblies
1.4.7.117484`); no local game install is needed to compile.

```bash
build.bat
```

This builds to `Dist/CustomSceneCreator/` and offers to deploy into your game's `Modules` folder.

To compile without deploying:

```bash
dotnet build CustomSceneCreator/CustomSceneCreator.csproj -c Release -p:Platform=x64
```

## Tools

Generators that read your local game install. Regenerate these after a game update.

| Script | Produces |
|---|---|
| `tools/build_scene_catalog.ps1` | `ModuleData/scene_catalog.xml` — every scene, its module, category, upgrade levels, and missing-support-file flags |
| `tools/build_asset_dump.ps1` | `ModuleData/bannerlord_assets_v<version>.txt` — every placeable prefab. Column layout matches the legacy dump so existing bake scripts keep working. |
| `tools/build_script_catalog.ps1` | `ModuleData/script_catalog.xml` — every scene script, its variables, inferred types, and how often shipped scenes use it |

Pass `-GameDir` if your install is not at the default path.

## Editor controls

Matched to Homesteads Reloaded's RTS builder where it has an equivalent. Hardcoded for now; these
become MCM settings later.

A scene opens in **third person, walking around**, like any other mission. Turning edit mode on with
`\` switches to the **RTS camera** - look down at the site, pan around it, and place where the
**cursor** is - and turning editing off returns you to walking.

`V` overrides that at any time, and once you have chosen a camera yourself the editor stops changing
it for you.

| Key | Action |
|---|---|
| `\` | Cycle edit mode: Off - Build - Delete - Move - Script |
| **LMB** | Place / delete / pick up / open scripts — works in every camera mode |
| `F` | Same, from the keyboard |
| **Hold RMB** + move mouse | Rotate the held object |
| `Q` `E` | Rotate left / right |
| **Left Ctrl** | Reset rotation and height offset |
| `G` | Drop to ground, and re-enable ground follow |
| `H` | Toggle ground follow (pin the height instead) |
| **`** | Open the asset picker: choose a category, search within it, inspect, build |
| `[` `]` | Previous / next placeable (quick cycle without the picker) |
| `'` | Next category |
| `V` | Cycle camera: RTS - third person - first person |
| **Alt+S** or `K` | Save the project (confirmation shown) |
| **Alt+E** | Export: prefab, or whole scene |
| Numpad `8` `2` | Tilt up / down |
| Numpad `4` `6` | Roll left / right |
| Numpad `5` `1` | Raise / lower |

RTS camera: `WASD` pans, `Space` / `Left Alt` change height, hold `Shift` and drag to rotate the
view, `Shift`+`WASD` flies along the view direction. Pan speed scales with height.

There is no maximum placement range - if you can see it, you can build on it.

Avoid rebinding onto `P` (the game's pick-up-item bind).

Your character sheathes their weapons while an edit mode is active, so clicking to place does not
swing a sword. They are drawn again as normal once editing is off.

Leaving with unsaved changes offers to save first.

## Where things are written

`Documents\Mount and Blade II Bannerlord\CustomSceneCreator\`

| Folder | Contents |
|---|---|
| `projects/` | Working files (`.json`), one per scene by default. This is what you reopen to keep building. |
| `exports/prefabs/` | Prefab XML — one reusable object, positioned relative to its own base |
| `exports/scenes/` | Scene fragments — everything at its real position, for pasting into a `scene.xscene` |

**A project is what you reopen; an export is a produced artifact.** The settlement menu opens the
saved-project list, with "New - Pick a Scene" one button away. `csc.projects` lists them in the
console and `csc.project <name>` opens one directly. A project remembers its scene and levels, so
reopening restores the whole session rather than dropping objects into whatever scene was last used.

To keep working on something you exported as a prefab, reopen the **project** it came from - the
prefab is the finished artifact, not the source. Prefab exports
are **also** written into `Modules/CustomSceneCreator/Prefabs/`, which is what makes them loadable by
the game — after a restart the exported object appears in the asset picker under **Exported** and can
be placed as a single piece.

## Logging

Writes to `%ProgramData%\Mount and Blade II Bannerlord\logs\CustomSceneCreator.trace.log`.

## License

TBD.
