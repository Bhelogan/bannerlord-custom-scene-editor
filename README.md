# Custom Bannerlord Scene Creator

A standalone in-game scene editor for **Mount & Blade II: Bannerlord v1.4.7**.

Open any shipped scene, place any shipped prefab or logical marker, and export the result — without
the Modding Kit.

> **Status: early development, but usable.** Entered from inside a campaign (settlement menu or
> `csc.open`). Scene browser, asset catalog, RTS/third/first-person cameras, and build/delete/move
> with JSON project save-load are all in, plus a searchable asset picker and editor-authored marker
> packs, export as a reusable prefab or a whole-scene fragment, and script attachment. Still to come:
> entity-reference variables, and scene derivation.
> New here? Start with the [user manual](USER_MANUAL.htm).
> See [CUSTOM_SCENE_CREATOR_PLAN.md](CUSTOM_SCENE_CREATOR_PLAN.md) for the full design and
> milestone list.

---

## Why

Building a scene for Bannerlord normally means the Modding Kit: a separate multi-gigabyte download,
a long load, and an editor that runs outside the game you are building for. For laying out objects —
a graveyard, a race track, a fortified camp — that is a lot of machinery to stand up before you can
place a single crate.

This puts the layout half in the game itself. You open the scene you want, walk or fly around it, and
place things where they look right, with the real lighting and the real terrain. What comes out is
plain XML the Modding Kit (or anything else) can pick up.

It is also built as a library rather than an application: the editor talks to an `ISceneEditTarget`
interface and never learns what it is editing, so another mod can host the same editor over its own
storage.

## What it does

- **All 611 shipped scenes** — battle terrain, multiplayer maps, towns, castles, villages, hideouts,
  arenas, interiors, naval. Categorized and searchable, with correct upgrade-level handling.
- **Derived scenes** — copy a shipped scene's terrain, navmesh, flora and atmosphere, but strip its
  mission scripts, so you get a hideout's landscape without the hideout's logic.
- **Logical placeables** — spawn points, navigation nodes, race gates and patrol points as
  first-class objects with proxy meshes. Declared in `ModuleData/packs/*.xml`, so a mod or a user can
  add their own markers without a rebuild. They export under their declared name and tag, so
  `FindEntitiesWithTag("sp_enemy")` finds them.
- **Script attachment** — in **Script** mode, click a placed object to see what scripts are on it,
  add more from a searchable list of the 130 the game ships, and edit their variables. Bools are
  buttons, and string variables offer the values shipped scenes actually use — you cannot type an
  FMOD event path like `event:/mission/ambient/detail/river_01` from memory, and the real list lives
  in sound banks the game never exposes. Attached scripts preview live where they can, and are
  written out on export either way.
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
| `tools/build_script_catalog.ps1` | `ModuleData/script_catalog.xml` — every scene script, its variables, inferred types, how often shipped scenes use it, and the values each string variable is set to (values seen in at least two scenes, so scene-local names are left out) |

Pass `-GameDir` if your install is not at the default path, and `-IncludeAllModules` to
`build_asset_dump.ps1` to pull your own mod's prefabs into the editor's picker — see
[README_REGENERATE.md](tools/README_REGENERATE.md). Keep such a dump local: it describes your
installation, so entries would fail to instantiate for anyone else.

`tools/bake_scene.py` post-processes an export: expanding markers into working entities, attaching
scripts in bulk, renaming tags, assigning GUIDs, and deploying the result. Driven by
`bake_config.json` and meant to be edited — see [README_BAKE.md](tools/README_BAKE.md).

## Editor controls

Rebindable in MCM's options screen if you have it installed (**Mod Options → Custom Scene Creator**).
MCM is optional — without it the editor runs on the defaults below.

Bindings are typed as `InputKey` names, which are physical **US-layout key positions**, not the letter
printed on the cap. Turn on **Key Detection Mode** in the settings and press a key to be told its
name — the only practical way to rebind on AZERTY or QWERTZ.

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
| Mouse wheel | Raise / lower the held object |
| **`** | Open the asset picker: choose a category, search within it, inspect, build |
| `L` | Open the scene contents list: everything placed, with editable position and rotation, plus Go To / Scripts / Pick Up / Delete |
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

## Releasing

```bash
powershell -ExecutionPolicy Bypass -File tools/package_release.ps1
```

Builds, then writes a single `CustomSceneCreator-v<version>.zip` containing the module folder, the
user manual and the bake tools — the whole thing someone needs, in one file. Pass `-SkipBuild` to
package what is already in `Dist`.

## License

[MIT](LICENSE) — use it, fork it, ship things built with it.

The generated catalogs under `ModuleData` are indexes of TaleWorlds' own game data and are not
covered; regenerate them from your own installation with the scripts in `tools/`. Scenes you build
reference game assets by name rather than containing them, so an export is a layout, not a copy of
anyone's art.
