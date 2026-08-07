# Custom Bannerlord Scene Creator

A standalone in-game scene editor for **Mount & Blade II: Bannerlord v1.4.7**.

Open any shipped scene from the main menu, place any shipped prefab or logical marker, and export
the result — without needing a campaign, a homestead, or the Modding Kit.

> **Status: early development.** The M1 boot spike is in. There is no scene browser, asset picker or
> export yet. See [CUSTOM_SCENE_CREATOR_PLAN.md](CUSTOM_SCENE_CREATOR_PLAN.md) for the full design
> and milestone list.

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
  first-class objects with proxy meshes, not just meshes with no collision.
- **Script attachment** — attach animation, effect and spawner scripts (fires, windmills, animated
  banners, character spawners) to placed objects, with an auto-generated variable editor.
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

Pass `-GameDir` if your install is not at the default path.

## Logging

Writes to `%ProgramData%\Mount and Blade II Bannerlord\logs\CustomSceneCreator.trace.log`.

## License

TBD.
