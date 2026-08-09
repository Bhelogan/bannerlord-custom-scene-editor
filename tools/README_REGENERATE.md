# Regenerating the catalogs

**Most people never need these.** The editor ships with everything already generated — 5,838 objects,
611 scenes, 130 scripts — and it works out of the box.

Run them for one of two reasons.

## 1. You want your own mod's assets in the editor

This is the interesting one. The shipped asset dump lists **official modules only**, because a prefab
from a third-party mod is a dead entry for anyone who does not have that mod. If you are building
assets for your own mod, regenerate the dump with every module included and your prefabs appear in
the picker alongside the base game's, ready to place:

```powershell
powershell -ExecutionPolicy Bypass -File build_asset_dump.ps1 -IncludeAllModules
```

Restart the game afterwards. Your prefabs will be grouped under their own module's name.

Keep this local. A dump built with `-IncludeAllModules` describes *your* installation, so it is not
something to redistribute — anyone else would get entries that fail to instantiate.

## 2. The game updated

A new Bannerlord version can add, rename or remove prefabs and scenes. The shipped catalogs are built
for **v1.4.7**. If the editor starts offering things that no longer exist, regenerate all three:

```powershell
powershell -ExecutionPolicy Bypass -File build_asset_dump.ps1
powershell -ExecutionPolicy Bypass -File build_scene_catalog.ps1
powershell -ExecutionPolicy Bypass -File build_script_catalog.ps1
```

## The scripts

| Script | Produces | Notes |
|---|---|---|
| `build_asset_dump.ps1` | `bannerlord_assets_v<version>.txt` | Every placeable prefab. `-IncludeAllModules` adds mods. |
| `build_scene_catalog.ps1` | `scene_catalog.xml` | Every scene, its levels, and whether it is safe to open. Slow — it reads every scene file. |
| `build_script_catalog.ps1` | `script_catalog.xml` | Every scene script, its variables, and the values shipped scenes use for them. |

Each writes into the module's `ModuleData` folder automatically — the source repo if you are working
from one, otherwise your installed copy. Pass `-OutFile` to override.

Pass `-GameDir` if Bannerlord is not at the default path:

```powershell
powershell -ExecutionPolicy Bypass -File build_asset_dump.ps1 -GameDir "D:\Steam\steamapps\common\Mount & Blade II Bannerlord"
```

## "cannot be loaded... is not digitally signed"

Windows refuses unsigned scripts by default. The `-ExecutionPolicy Bypass` in the commands above
handles it — it applies to that one run only and changes nothing on your system. If you copied a
command from elsewhere without it, that is the error you get.

## If something goes wrong

The editor keeps working on whatever catalogs it can read. A missing or malformed one is reported in
the log at:

```
%ProgramData%\Mount and Blade II Bannerlord\logs\CustomSceneCreator.trace.log
```

To go back to the shipped versions, reinstall the mod folder — nothing here touches your scenes,
which live under `Documents`.
