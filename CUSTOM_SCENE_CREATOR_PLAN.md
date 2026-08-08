# Custom Bannerlord Scene Creator — Build Plan (v2)

**Status:** plan only, nothing built yet.
**Target game version:** v1.4.7 (verified install at `F:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord`, `Native/SubModule.xml` = `v1.4.7`).
**Module name (proposed):** `CustomSceneCreator`
**Goal:** a standalone in-game scene editor — pick any shipped scene, place any shipped prefab *or logical marker*, save/load/export the layout — with **no Homestead and no campaign required**, and usable as a **library by any mod**.

**v2 changes:** decisions locked (§0); the editor is now a reusable library, not just an app (§7); scene derivation without scripts (§8); non-asset placeables — spawn points, nav points, waypoints (§9); terrain, honestly scoped (§10).

---

## 0. Decisions locked

| # | Question | Decision |
|---|---|---|
| 1 | HomesteadBuilder | **Retire it.** `CustomSceneCreator` supersedes it. |
| 2 | Reusability | **Ship the editor as a library any mod can consume**, with Homesteads as consumer #1. See §7. |
| 3 | Dumps | **Regenerate for 1.4.7** — asset dump *and* API dump. See §6. |
| 4 | Modding Kit | Installed but painful. Therefore: **maximize what never needs the Kit**, and make what does need it a clean one-shot handoff. See §8, §10. |

---

## 1. What exists today

### `HomesteadBuilder` is a Harmony shim, not an editor — retiring it loses nothing
~1,400 lines across four files. `Patches.cs` `Traverse`s into Homesteads' private fields by name and already carries a compat shim for a field that became a property (`Patches.cs:24-47`). Its map picker discards ~90% of shipped scenes (`MapSelectionVM.cs:44`).

Everything worth keeping is carried forward: the asset-dump parser (`AssetLoader.cs`), the prefab-XML exporter (`Patches.cs:175`), and the search-over-placeables behavior.

### The actual editor lives in Homesteads Reloaded, and it is nearly campaign-free
`MissionLogics/HomesteadSceneEditingMissionLogic.cs` — 1,845 lines. A grep for `Campaign.Current` / `Hero.MainHero` across the editor logic, free camera, and building picker returns **one** hit, null-safe:

```
HomesteadSceneEditingMissionLogic.cs:1342
    return Campaign.Current?.ConversationManager?.IsConversationInProgress ?? false;
```

Coupling to `Homestead` is four seams: `IsDefensive` (HUD toggle), `Tier` (placeable gating), `Name` (template label), `GetHomesteadScene()` (all persistence). That's the whole extraction surface.

**Not reusable:** `HomesteadSpawningMissionLogic` (2,742 lines, campaign-bound). Standalone needs a new ~150-line spawner.

### Already standalone-shaped
`HomesteadTemplate` + `HomesteadTemplateManager` (JSON in Documents, no `SaveableTypeDefiner`), `HomesteadFreeCameraView` (666 lines), `HomesteadBuildingPickerVM` + its Gauntlet prefab.

---

## 2. Architecture: fork into a library

Fork the editor into `CustomSceneCreator` with **zero dependency on `Homesteads.dll`**, and structure it so the dependency can run the *other* way later.

```
Modules/CustomSceneCreator/
├── SubModule.xml
├── ModuleData/
│   ├── bannerlord_assets_v1.4.7.txt      ← regenerated (§6)
│   ├── scene_catalog.xml                 ← levels + flags, harvested from scene.xscene (§5)
│   └── logical_placeables.xml            ← marker/spawn/nav definitions (§9)
├── GUI/Prefabs/  CSCSceneBrowser.xml · CSCAssetPicker.xml · CSCEditorHUD.xml
└── bin/Win64_Shipping_Client/CustomSceneCreator.dll
```

```
Source/
├── SubModule.cs
├── Api/                          ← THE PUBLIC LIBRARY SURFACE (§7)
│   ├── ISceneEditTarget.cs             persistence seam
│   ├── IPlaceableProvider.cs           catalog seam
│   ├── SceneEditorSession.cs           façade: Open(target, scene, options)
│   └── SceneEditorOptions.cs           gating, HUD, allowed categories, export modes
├── Boot/       SceneCreatorGameManager.cs · SceneCreatorMission.cs
├── Catalog/    SceneCatalog.cs · AssetCatalog.cs · LogicalPlaceableCatalog.cs · Placeable.cs
├── Editing/    SceneEditingMissionLogic.cs · SceneProject.cs · FreeCameraView.cs
├── UI/         SceneBrowserVM · AssetPickerVM · EditorHudVM (+ Views)
├── IO/         ProjectSerializer.cs · PrefabXmlExporter.cs · SceneXmlWriter.cs (§8)
└── Campaign/   SceneCreatorCampaignEntry.cs
tools/
├── build_asset_dump.ps1
├── build_scene_catalog.ps1
└── derive_scene.ps1              ← script-stripped scene derivation (§8)
```

---

## 3. Entry points

> **DECIDED 2026-08-07 by the M1 spike — reversed from the plan above.**
> The editor is entered **from inside a running campaign**. The main-menu boot is abandoned for v1.
>
> The spike reached the mission, then crashed in another mod's code, and the cause is not fixable
> from here. `Campaign.DoLoadingForGameType` raises `OnAfterGameInitializationFinished` on **every
> installed module unconditionally**, but only calls `InitializeMainParty()` on the `NewCampaign` /
> `SavedCampaign` paths (`api_v1.4.5.txt:11034-11051`). A `Tutorial` boot runs neither — so every
> mod is told "the game is ready" while `Hero.MainHero` is still null. CharacterReload threw first
> (`Clan.PlayerClan`); **DistinguishedServicePlus and ChatAi override the same callback** on this
> install alone. Fixing one mod just exposes the next.
>
> That also answers M1's original question by proof: tutorial mode gives **no main hero and no main
> party**.
>
> In-campaign entry removes the whole class of failure — every module has already initialized
> successfully, the player and party are real, and `CreateSandBoxMissionInitializerRecord` becomes
> safe. It also deletes the boot machinery we would otherwise own across game updates.
>
> **If main-menu entry is ever revisited** it must use a non-`Campaign` `GameType` (the
> custom-battle pattern), since `OnAfterGameInitializationFinished` is raised *only* from
> `Campaign`. That gives up `Campaign.Current` and needs a different player-agent path.
> `SceneCreatorGameManager.cs` is kept, unwired, for that.

### 3a. Main menu — ABANDONED for v1 (see box above)
`Module.CurrentModule.AddInitialStateOption` is public (`api_v1.4.5.txt:612620`), used by Native for `Editor`/`CustomBattle`/`Options` (`:934412-934426`) and by third parties (`RealisticBattleProject/RBM/SubModule.cs:79`).

Two native patterns for opening a scene with no sandbox game:

| Pattern | Source | Creates |
|---|---|---|
| **A. Tutorial-campaign** | `EditorSceneMissionManager` (`api_v1.4.5.txt:953857`) | `new Campaign(CampaignGameMode.Tutorial)` + `SetLoadingParameters(Tutorial)` |
| **B. Custom-game** | `NavalCustomGameManager` (`api_v1.4.5.txt:1672010`) | `Game.CreateGame(new NavalCustomGame(), this)` — no campaign |

**Recommend A.** A real `Campaign.Current` means `CampaignMissionComponent`, `HeroSkillHandler`, name markers, and photo mode work unchanged — most of the behavior list at `CustomMissions.cs:429-457`. B would force re-solving player equipment and agent creation for no gain.

Both share the same 6-step `DoLoadingForGameManager`; copy it and replace `OnLoadFinished` with our scene-browser state → `SceneCreatorMission.Open(...)`.

**Risk:** Tutorial mode may leave `Hero.MainHero` null. Gate spike M1.

### 3b. In-campaign entry — **PRIMARY, implemented**
`SceneCreatorCampaignBehavior` adds an "Open Scene Creator" option to the `town`, `village` and
`castle` menus; `csc.open <scene> [levels]` opens any scene directly. Both funnel through
`SceneCreatorEntry`.

A tavernkeeper *dialog* remains possible but is not the cheap option — it costs dialog XML,
per-culture conversation tokens and localization to land in the same place a menu entry already
reaches. Worth adding for flavour once the browser exists, not before.

**Cost of this decision:** a campaign must be loaded to edit. Mitigate with one small dedicated save
kept for editing, and by making the browse ⇄ edit loop (§16) never return to the map.

---

## 4. Retiring HomesteadBuilder

1. `CustomSceneCreator` reaches feature parity (M1–M6).
2. Port the `add_collision_and_spawns.py` / `process_new_assets.py` bake step **into the exporter** (§9) so it stops being a manual out-of-game stage.
3. Ship a migration: read existing `HomesteadsReloaded_Templates/*.json` and `User provided assets/*.xml`, write `SceneProject` JSON.
4. Mark `HomesteadBuilder` deprecated in its SubModule description; leave the folder in place one release, then delete.
5. Homesteads gets the editor back via §7 — no regression for homestead users.

---

## 5. Scene catalog — all 618 shipped scenes

**Confirmed on disk (v1.4.7):** SandBoxCore 202 · SandBox 195 · NavalDLC 114 · Native 79 · Cutscenes_Extended 24 · StoryMode 4 = **618**, all as plain `SceneObj/<name>/` folders. Nothing is packed away.

Coverage: battle terrain 129 · multiplayer 65 (all under `Native`, not `Multiplayer`) · villages 112 · towns 58 · castles 50 · hideouts 15 · interiors 37 · arenas 7.

Categorize by name pattern + owning module; keep the typed-name passthrough (`MapSelectionVM.cs:93`) so unlisted scenes still load.

### Settlement scene levels — solved
`scene.xscene` is **plain XML** and declares its own levels:

```xml
<scene name="aserai_village_b" version="2">
  <levels>
    <level name="base" mask="1"/>
    <level name="level_1" mask="2"/>
    <level name="level_2" mask="4"/>
    <level name="level_3" mask="8"/>
  </levels>
```

So `tools/build_scene_catalog.ps1` harvests the exact level names per scene straight from the file — no guessing, no `settlements.xml` cross-referencing. `MissionInitializerRecord.SceneLevels` (`api_v1.4.5.txt:344966`) takes them as a string; Native's vocabulary is confirmed at `:10444` (`level_1 level_2 level_3 siege raid burned`). Browser exposes a level toggle set per scene.

**Remaining risk:** hideout/cutscene/naval scenes carry scripts expecting a specific mission type. Two mitigations — a catalog good/untested/bad flag seeded by a bulk load test, and §8, which removes the scripts entirely.

---

## 6. Regenerated dumps for 1.4.7

Both dumps are stale and the asset-dump generator **is not in the repo**.

**Asset dump.** Source data present in 1.4.7: Native 77 prefab XMLs / 150 TPACs · NavalDLC 28 / 70 · SandBox 12 / 1 · StoryMode 1 / 0. Write `tools/build_asset_dump.ps1` emitting the existing 12-column pipe format so `AssetLoader`'s parser is reused unchanged. Ship as `bannerlord_assets_v1.4.7.txt`.

Keep: the runtime `GameEntity.PrefabExists` filter (`AssetLoader.cs:101`) — it makes a stale dump degrade to fewer assets rather than crash.

Change:
- **Drop the mesh-empty filter as a hard exclusion** (`AssetLoader.cs:98`). It currently deletes every marker and spawn prefab. It becomes a *category flag* instead — see §9. This is the single most important change in the whole plan for your graveyard use case.
- **Reclassify NavalDLC from excluded to gated** (`AssetLoader.cs:94`). It was blanket-excluded because ship scripts crash outside naval missions; now that naval scenes are openable, make it a toggle defaulting off.
- **Re-cut categories.** `GetCategoryForAsset` was written for homestead building (`Housing`, `Productivity`, `Leisure`). For a general editor: `Terrain & Rocks`, `Vegetation`, `Buildings`, `Walls & Fortification`, `Props & Clutter`, `Furniture`, `Lighting`, `Banners`, `Siege`, `Naval`, `Animals`, **`Markers & Logic`**, `Misc`.

**API dump.** Regenerate `api_v1.4.7.txt` via `build_api_dump.ps1` before M4. Everything in this plan was verified against `api_v1.4.5.txt` plus the live 1.4.7 install; the boot and mission APIs are stable, but M4 ports 1,845 lines and deserves a current reference.

---

## 7. Reusable by any mod — the library seam

The editor becomes a library by pulling exactly the four Homestead seams (§1) behind two interfaces plus an options object.

```csharp
// What the editor writes into. Implement this to own your own persistence.
public interface ISceneEditTarget {
    string DisplayName { get; }
    string SceneName { get; }
    string SceneLevels { get; }
    IEnumerable<PlacedEntity> LoadEntities();
    void OnEntityAdded(PlacedEntity e);
    void OnEntityRemoved(PlacedEntity e);
    void OnPlayerSpawnSet(Vec3 pos, Mat3 rot);
    void Commit();
}

// What the editor offers to place. Implement to filter/extend the palette.
public interface IPlaceableProvider {
    IEnumerable<Placeable> GetPlaceables(SceneEditorContext ctx);
}

// Everything a host mod wants to vary without forking.
public class SceneEditorOptions {
    public bool ShowEconomyHud;              // Homesteads: true
    public Func<Placeable,bool> Filter;      // Homesteads: tier gating
    public bool AllowLogicalPlaceables;      // scene authoring: true
    public bool AllowFreeCamera;
    public ExportModes EnabledExports;
}
```

Entry for a consuming mod is one call:

```csharp
SceneEditorSession.Open(myTarget, options);
```

**Three shipped implementations prove the seam:**
1. `SceneProjectTarget` — the standalone app's own JSON project (§11).
2. `TransientTarget` — in-memory, for a mod that just wants a throwaway layout.
3. **`HomesteadSceneTarget`** — a ~60-line adapter in Homesteads: `LoadEntities` → `HomesteadScene.LoadedSavedEntities`, `OnEntityAdded` → `AddPlaceableEntityToCurrentScene`, `Filter` → tier gating, `ShowEconomyHud` → `!IsDefensive`.

Homesteads then **deletes** its 1,845-line copy and calls the library. That is the real test of whether the seam is right, and it's why the adapter should be written during M4 rather than after — but only *landed* in Homesteads once the standalone app has shipped and settled.

**Distribution:** `CustomSceneCreator` is a normal module; consumers add `<DependedModule id="CustomSceneCreator"/>`. Keep the public API in `Api/` genuinely small and versioned — everything else `internal`.

---

## 8. Base maps without the scripts — yes, and it's cleaner than expected

`scene.xscene` is **plain, uncompressed XML**. `aserai_village_b` is 450 KB containing **2,244 `<game_entity>` elements but only 157 `<script>` elements**. Scripts are a small, clearly-delimited minority:

```xml
<game_entity prefab="village_ambient_sound">
  <transform position="324.479, 264.190, 5.946" rotation_euler="0,0,0"/>
  <scripts>
    <script name="AmbientSoundEmitter"> … </script>
  </scripts>
</game_entity>
```

A scene folder is five files: `scene.xscene` · `terrain.bin` · `navmesh.bin` · `flora.bin` · `atmosphere.xml` (+ `ShaderCache`).

**`tools/derive_scene.ps1`** produces a derived scene in our own module:

1. Copy `terrain.bin`, `navmesh.bin`, `flora.bin`, `atmosphere.xml` verbatim — **the terrain, navmesh, vegetation and lighting come across untouched, and none of it requires the Modding Kit.**
2. Filter `scene.xscene` with three configurable passes:
   - **strip scripts** — drop every `<scripts>` block (or a named subset)
   - **strip logic entities** — drop `<game_entity>` whose prefab matches settlement-logic families (`sp_notable`, `sp_merchant`, `sp_guard`, `sp_alley`, passage/door markers)
   - **strip clutter** *(optional)* — drop props by category, leaving a bare shell
3. Write to `Modules/CustomSceneCreator/SceneObj/csc_<name>/`.
4. Register the derived scene in `scene_catalog.xml` under a **"Derived"** category.

This gives you `csc_forest_hideout_003` — the hideout's terrain, rock formations, navmesh and atmosphere, with none of the hideout mission logic. Same for villages, towns, castles.

**Why this matters beyond convenience:** it also neutralizes the §5 "scenes that crash outside their context" risk, because the scripts causing those crashes are exactly what's being removed.

**Caveats to verify in the spike:**
- Removing an entity a *retained* script references could fault. Mitigation: strip scripts before entities, and keep a whitelist of always-safe removals.
- Native navmesh is baked for the original layout; stripping props leaves navmesh over now-empty ground (harmless) but stripping *buildings* leaves navmesh where geometry used to be (agents walk through air). Note in the UI: strip clutter freely, strip buildings knowingly.
- Redistribution: derived scenes reference Native assets and copy Native binaries. Fine for local use; for Workshop, ship `derive_scene.ps1` and have it run on the user's own install at setup, not the derived output. **Decide this before any public release.**
- `.xscene` version is `2` on 1.4.7 — pin the writer to it and fail loudly on a mismatch.

---

## 9. Non-asset placeables — spawn points, nav points, waypoints

This is the current blocker for the graveyard use case, and the fix is small.

**Marker prefabs already ship as real prefabs.** `Modules/*/Prefabs/*.xml` contains **541 unique** marker-family prefab names:

```
sp_npc (344)   sp_troop (144)   sp_cube (119)   sp_skirmish (79)
sp_notable (75) sp_arena (58)   sp_guard (55)   sp_alley (48)
spawnpoint (47) sp_defender (44) sp_attacker (42) sp_player (31)
sp_horse (23)   sp_dog (22)      sp_prisoner (18)
plus editor_cube / editor_cylinder helper meshes
```

And `scene.xscene` confirms they're placed exactly like any prop:

```xml
<game_entity prefab="sp_npc_wait">
  <transform position="…" rotation_euler="…"/>
</game_entity>
```

They are invisible today only because `AssetLoader.cs:98` drops every prefab with no mesh.

**Plan:**

1. **Stop excluding meshless prefabs.** Flag them `IsLogical = true` instead.
2. **Render them with a proxy.** In-editor, a logical placeable draws an `editor_cube` / `editor_cylinder` helper mesh plus a name label and a facing arrow; on export the proxy is dropped and only the real marker entity is written. (Verify `editor_cube` is instantiable at runtime — likely, but it is an editor-side mesh; fallback is any small primitive.)
3. **Add a user-defined marker type.** `ModuleData/logical_placeables.xml` lets you declare markers that *aren't* base-game prefabs — which is what race tracks and the graveyard both need:

```xml
<logical_placeable id="csc_race_waypoint" display="Race Waypoint"
                   proxy_mesh="editor_cylinder" color="0,180,255"
                   export_as="entity" name_pattern="race_wp_{index}"/>
<logical_placeable id="csc_enemy_spawn" display="Enemy Spawn"
                   proxy_mesh="editor_cube" color="220,40,40"
                   export_as="entity" name_pattern="sp_enemy_{index}" tag="sp_enemy"/>
```

`name_pattern` auto-numbers on placement, matching the `sp_enemy_1`, `sp_enemy_2` convention `CUSTOM_SCENE_GUIDE.md` recommends for `FindEntitiesWithTag` lookups. Mods (including Homesteads) can ship their own file and have their markers appear in the palette — this rides the §7 library seam.
4. **Absorb the bake step.** `add_collision_and_spawns.py` currently post-processes exports out-of-game — it reads the asset dump, maps meshes to physics shapes (`parts[7]`), and injects spawn-point XML. Port that logic into `PrefabXmlExporter` so **export is one in-game button and the Python stage disappears**. Keep the script working during transition; delete it once the exporter matches its output byte-for-byte on a known layout.
5. **Round-trip.** Markers persist in `SceneProject` JSON with their type id, so a race track reopens as an editable track rather than an anonymous pile of cylinders.

---

## 10. Terrain editing — honest answer

**Runtime sculpting: no.** The managed terrain surface is read-only. Present in `api_v1.4.5.txt`: `GetTerrainHeight` (`:380874`), `GetTerrainHeightAndNormal` (`:380879`), `FillTerrainHeightData` (`:380418`), `FillTerrainPhysicsMaterialIndexData` (`:380423`), `GetTerrainData` (`:380869`), `GetTerrainMinMaxHeight` (`:380890`), `HasTerrainHeightmap` (`:380983`). Searches for `SetTerrainHeight`, `DeformTerrain`, `TerrainEditor`, `SetHeightAt` return **nothing**. The only writer is `SetTerrainDynamicParams` (`:381662`), which is shader parameters, not geometry. Sculpting lives in the Modding Kit's native editor and is not exposed.

**Four things we can do instead, in ascending cost:**

**(a) Terrain selection — free, ship in v1.** With 618 scenes plus §8 derivation, "choose your terrain" becomes a browsing problem rather than a sculpting one. 129 battle terrains alone cover most needs.

**(b) Map-patch terrain — cheap, high value, ship in v1.** `MissionInitializerRecord.SceneHasMapPatch` + `PatchCoordinates` stamps the *campaign map heightfield* onto a battle scene's terrain. Homesteads already relies on this and documents why (`CustomMissions.cs:38-60`):

> `DecalAtlasGroup.Town` sets `SceneHasMapPatch = false` (raw base terrain). Battle missions set `SceneHasMapPatch = true` (campaign-map heightfield patch).

So a base scene + a pair of campaign-map coordinates yields procedurally different real terrain — every hill and valley in Calradia, usable as a starting surface, with zero sculpting. Expose it as a coordinate picker in the scene browser. *(Verify the coordinate space: Native passes `mapPatchAtPosition.normalizedCoordinates` at `api_v1.4.5.txt:653`, while Homesteads passes raw `GetPosition2D` — one of the two is doing something subtly wrong.)*

**(c) Mesh-based terrain — cheap, ship in v1.** Rock, cliff, dirt-mound and terrain-patch prefabs placed as ordinary props. Not real terrain, but it is how a lot of shipped scenes get their local shape anyway. Falls out of §6's `Terrain & Rocks` category for free.

**(d) Writing `terrain.bin` — a separate project, not part of v1.** The format is at least partly legible. Header from `aserai_village_b/terrain.bin` (14.3 MB):

```
5a47 5236 5254 524e  "ZGR6RTRN"   magic
0200 0000            version 2
4d49 4458 …          MIDX chunk   (index)
4847 4854 …          HGHT chunk   (heightmap)
4e52 4d4c …          NRML chunk   (normals)
5747 4854 …          WGHT chunk   (layer weights / texture painting)
5048 594d …          PHYM chunk   (physics materials)
```

Each chunk carries an id, version, offset and **two sizes** — near-certainly compressed and uncompressed lengths, so payloads need a codec identified before anything can be read, let alone written. Realistically a multi-week reverse-engineering effort with a real risk of producing files the engine rejects.

**Recommendation:** ship (a)+(b)+(c) in v1, which together cover most of what "terrain editing" is actually wanted for. Log (d) as a research spike to run *only if* (a)–(c) prove insufficient in practice — and note that §8 already lets you steal any shipped scene's terrain wholesale, which is usually the cheaper answer.

---

## 11. Data model & output

`SceneProject` JSON in `Documents/Mount and Blade II Bannerlord/CustomSceneCreator/`:

```json
{
  "name": "graveyard_ambush",
  "version": 1,
  "targetScene": "csc_forest_hideout_003",
  "sceneLevels": "base",
  "mapPatch": { "enabled": false, "x": 0, "y": 0 },
  "worldAnchor": { "x": 0, "y": 0, "z": 0 },
  "entities": [
    { "prefab": "gravestone_a", "pos": [x,y,z], "rot": { "f":[…], "u":[…], "s":[…] } },
    { "logical": "csc_enemy_spawn", "name": "sp_enemy_1", "tag": "sp_enemy",
      "pos": [x,y,z], "rot": { … } }
  ]
}
```

Derived from `HomesteadTemplate` + `HomesteadSceneSavedEntity` (`HomesteadSceneSavedEntity.cs:10-34`). **Keep the full 3×3 rotation matrix** — Euler angles will bite on snapped or tilted props. Drop `TotalBuildPoints` / `TotalCost` / `SourceHomesteadName`. Add `logical` entity support (§9).

**Homesteads compatibility (decision 2):** `ProjectSerializer` reads and writes the legacy `HomesteadTemplate` shape as a secondary format, so layouts move both ways. Cheap now, expensive to retrofit.

**Four outputs:**
1. **Project JSON** — round-trips into the editor. Primary.
2. **Prefab XML** — generalized from `Patches.cs:175`, now with physics + spawn baking folded in (§9.4). Drops into `Modules/<mod>/Prefabs/`.
3. **Scene-fragment XML** — a `<game_entity>` block pasteable into a real `scene.xscene`. Now confirmed straightforward: §8 established the file is plain XML at version 2 with a flat `<game_entity><transform position rotation_euler/></game_entity>` shape. This is the Modding-Kit handoff — lay everything out in-game where it's pleasant, open the Kit once to bake navmesh.
4. **Derived scene** — write directly into a `csc_*` scene folder (§8), skipping the Kit entirely for anything that doesn't need a navmesh rebake.

**Out of scope for v1: navmesh generation.** Placed props get collision but no AI pathing. Say it in the README — `CUSTOM_SCENE_GUIDE.md` calls "no navmesh = no AI" the #1 source of broken-scene reports, and users will assume this tool handles it. Output 3 is the sanctioned path to a real navmesh.

---

## 12. The bake script — shipped, generic, user-editable

Rather than hide baking inside the DLL, **the exporter and the script read the same file.** `SceneProject` JSON is the documented contract; the in-game exporter does the standard bake, and `tools/bake_scene.py` does the same job outside the game where anyone can edit it.

```
tools/
├── bake_scene.py          ← generic, commented, edit-me
├── bake_config.json       ← the common knobs, so most users never open the .py
└── README_BAKE.md
```

Ships in `Modules/CustomSceneCreator/tools/` and is copied to the user's project folder on first run, so their edits survive mod updates.

Pipeline stages, each an independently toggleable function:

| Stage | Does | Default |
|---|---|---|
| `assign_guids` | Stable GUIDs for entities referenced by scripts (§15) | on |
| `inject_physics` | Mesh → physics-shape mapping from the asset dump (the `parts[7]` logic in `add_collision_and_spawns.py`) | on |
| `inject_spawns` | Marker entities → `sp_*` game entities with tags | on |
| `attach_scripts` | Script blocks + variables (§15) | on |
| `assign_levels` | Entity → scene level mask | off |
| `emit_prefab` / `emit_scene_fragment` / `emit_derived_scene` | Output form (§11) | prefab |

`bake_config.json` covers path overrides, which stages run, output form, and the physics-mapping overrides — which is what people actually customize. Editing the Python is the escape hatch, not the expected path.

Migration: `add_collision_and_spawns.py` and `process_new_assets.py` are the seed. Keep them working until `bake_scene.py` reproduces their output byte-for-byte on a known layout, then retire them.

---

## 13. The Build Selector — Tilde, search bar, and pluggable asset packs

**The key you want is already the default.** `KeyBindOpenBuildMenu = "Tilde"` (`Settings/…:217`), opened at `HomesteadSceneEditingMissionLogic.cs:269`, D-pad Down on controller. Carry it over unchanged.

The picker itself carries over structurally — categories, a details pane, a Controls pseudo-tab, `IsFocusLayer` at layer 4000. Four changes:

**1. Search moves into the picker.** Today search is a *modal inquiry popup* (`Patches.cs:402 TriggerSearch`) that filters the underlying list. Replace with a live filter bar at the top of the picker, matching against display name, prefab name, description, and tags — the match logic at `Patches.cs:439` is already right, it just needs a better host. This also sidesteps a known trap: never raise a modal inquiry from inside another inquiry's callback.

**2. Tier groups → Source groups.** There are no tiers here. Group by origin instead:

```
Base Game · Scene Editor · Effects & Scripts · <User Pack…> · Derived
```

**3. Asset packs.** One schema, three uses — base-game markers (§14), our own editor assets (§14), and user/mod-authored packs:

```
ModuleData/packs/
├── csc_core.xml          ← our nav nodes, race gates, spawn points (ships with the mod)
├── csc_effects.xml       ← curated script composites (§15)
└── <anything>.xml        ← user drops files here; picked up as its own category
```

Consuming mods contribute packs through `IPlaceableProvider` (§7), so Homesteads' placeables appear as a category without the editor knowing what a homestead is. **This is how we handle not having the Homesteads Reloaded asset list — we never need it.** Homesteads supplies its own.

**4. Recents and favourites.** A pinned strip at the top of the picker. Cheap, and with a 3,000-entry palette it stops being optional.

**Gotcha to carry over:** a clickable modal Gauntlet layer over a live mission needs the escape-menu pattern — pause the engine, `RegisterHotKeyCategory`, and handle layer-input escape — or it renders and ignores every click. The existing picker already does this correctly; port it verbatim rather than rebuilding.

---

## 14. Editor-authored assets — nav nodes, race gates, spawn points

Three tiers of placeable, all sharing one definition schema so users can add their own.

### Tier 1 — base-game marker prefabs (541 of them, §9)
`sp_player`, `sp_attacker_infantry`, `sp_defender_archer`, `sp_troop*`, `sp_arena*`, `spawnpoint*`. Free once the meshless-prefab exclusion is lifted.

### Tier 2 — base-game *script* markers
Most navigation and AI markers are **not prefabs — they're script components**, so they belong to §15's machinery. Ranked by real usage across shipped scenes:

| Script | Uses (sample) | What it is |
|---|---|---|
| `AnimationPoint` | 2,292 | NPC pose/animation point — **this is the "usable point" that makes non-universal actions play** |
| `VolumeBox` | 1,145 | Volume/trigger region |
| `UsablePlace` / `ChairUsePoint` / `Chair` | 888 | Usable points |
| `FightAreaMarker` | 348 | Combat bounds |
| `StrategicArea` | 313 | AI strategic point |
| `PatrolPoint` / `DynamicPatrolAreaParent` | 428 | **Patrol navigation nodes** |
| `TacticalPosition` / `TacticalRegion` | 163 | AI tactical anchors |
| `CharacterSpawner` | 62 | NPC spawn with pose, body properties, mount |

That table *is* your navigation-node feature — it already ships, it just has no UI.

### Tier 3 — our own definitions (`csc_core.xml`)
For things the base game has no marker for:

```xml
<placeable id="csc_enemy_spawn" display="Enemy Spawn" group="Spawns"
           proxy_mesh="editor_cube" color="220,40,40"
           export_name="sp_enemy_{index}" export_tag="sp_enemy">
  <field name="Team"      type="enum"  values="Attacker,Defender,Neutral" default="Attacker"/>
  <field name="TroopId"   type="string" default=""/>
  <field name="Count"     type="int"   default="1" min="1" max="50"/>
</placeable>

<placeable id="csc_race_gate" display="Race Gate" group="Race"
           proxy_mesh="editor_cylinder" color="0,180,255"
           export_name="race_gate_{index}" export_tag="race_gate" ordered="true">
  <field name="Radius" type="float" default="4.0"/>
</placeable>
```

Shipping set: `csc_player_spawn`, `csc_ally_spawn`, `csc_enemy_spawn`, `csc_nav_node`, `csc_nav_link`, `csc_race_start`, `csc_race_gate`, `csc_race_finish`, `csc_boundary`.

Key behaviors:
- **`ordered="true"`** auto-numbers on placement and lets you renumber by dragging in a side list — race gates are meaningless unsequenced. Align the output with `Homesteads/Models/RaceTracks.cs` so existing tracks round-trip.
- **`{index}` naming** matches the `sp_enemy_1`, `sp_enemy_2` convention `CUSTOM_SCENE_GUIDE.md` recommends for `FindEntitiesWithTag` lookups.
- **Proxy meshes** (`editor_cube` / `editor_cylinder`, colour-tinted, with a facing arrow and name label) render in-editor and are stripped on export. Verify both are instantiable at runtime — they are editor-side meshes; fallback is any small primitive.
- **Per-type visibility toggles** so a scene with 200 nav nodes is still workable.

---

## 15. Attaching scripts to assets — fires, animations, spawners

`GameEntity.CreateAndAddScriptComponent(string name, bool callScriptCallbacks)` is public (`api_v1.4.5.txt:397636`), so scripts can be attached **live in the editor**, not just written on export.

### Building the script catalog from two sources — both are needed

**Source A — reflection.** 113 `ScriptComponentBehavior` subclasses in the managed assemblies. Public fields give names and types directly; `EditorVisibleScriptComponentVariable` (`:406049`) marks which are editor-facing. Example from `CharacterSpawner`:

```
Enabled:bool  PoseAction:string  LordName:string  ActionSetSuffix:string
HasMount:bool  IsWeaponWielded:bool  AnimationProgress:float  Active:bool
```

**Source B — scene mining.** Harvest `<script name>` and `<variable name>` from all 618 `scene.xscene` files. **This is not optional:** the most-used scripts in the game are engine-side and do not appear in the managed reflection list at all — `AnimationPoint`, `VolumeBox`, `UsablePlace`, `barrier_builder`, `mesh_bender`, `path_converger`. Mining also yields observed value ranges, which is how field *types* get inferred for the non-reflectable ones, plus a real-world usage rank for ordering the UI.

Merged into `ModuleData/script_catalog.xml`, regenerated by `tools/build_script_catalog.ps1` alongside the other dumps (§6).

### The UI
With an entity selected in the editor, the picker gains a **Scripts** tab: add script (searchable, ranked by usage) → an **auto-generated variable editor** built from the catalog schema. No hand-written UI per script.

### Entity-reference variables — the thing that will bite
Some variables hold GUIDs pointing at other entities. From a shipped village:

```xml
<script name="AnimationPoint">
  <variables>
    <variable name="PairEntity" value="{F8E9CAD0-1B39-4989-A0D0-EDA646A00E20}"/>
  </variables>
</script>
```

So three requirements fall out: the project format must assign **stable GUIDs** to placed entities; the variable editor needs a **"pick target entity"** click mode for `entity`-typed fields; and the exporter must emit matching GUIDs. Without this, paired animation points — two NPCs facing each other, a conversation pose — can't be authored at all.

### Fires and effects specifically
Two distinct mechanisms, both wrapped so users never see the plumbing:
- **Particle systems** — `GameEntity.AddParticleSystemComponent(particleId)` (`:372763`)
- **Script-driven** — `LightCycle` (`alwaysBurn`), `BurningNode`, `BurningSoundNode`, `RandomParticleSpawner` (`spawnInterval`), `ChangeLightIntensityScript`

`csc_effects.xml` presents these as a curated **Effects & Scripts** category: *Campfire (lit)*, *Torch (flickering)*, *Brazier*, *Windmill (turning)*, *Animated Banner*, *Ambient Sound*. Each entry is a prefab + script + preset variables. The generic script tab stays available underneath for anything not curated.

### Preview honesty
Some scripts initialize at scene load and won't behave when attached mid-session. Every catalog entry carries a preview flag — `live` (works in editor), `static` (renders but inert), `none` (exports correctly, invisible until the baked scene is loaded) — surfaced in the details pane so nobody files a bug against a windmill that won't turn until export.

---

## 16. Map selection and the browse ⇄ edit loop

Today's flow works and stays: browser → pick scene → mission. Two additions.

**Exiting a build returns to the scene browser, not the main menu.** That requires the browser to be a real `GameState` that mission-leave can pop back to, rather than a Gauntlet layer over whatever screen happened to be underneath (`MapSelectionService.cs:25` currently attaches to `ScreenManager.TopScreen`). `BasicLeaveMissionLogic` returns to the campaign map today; the standalone app needs `SceneCreatorBrowserState` as the return target, with quit-to-main-menu as an explicit second option.

```
Main Menu → Scene Browser ⇄ Editor
                 ↑              │
                 └── Esc ───────┘   (Save · Save & Exit · Discard · Quit to Menu)
```

**Browser state persists across the loop** — last scene, last search text, last category, last level selection, and a Recent Scenes list. Round-tripping between two maps to compare a layout should be two clicks, not a re-search.

Plus an **unsaved-changes guard** on leave, and **resume-last-project** on entry.

---

## 16b. Editor camera modes

Requested 2026-08-07: some people build in first person, standing where the thing will be seen from,
rather than looking down at a layout from above. Both are legitimate and they suit different jobs -
free-fly for laying out a village footprint, player-attached for judging whether a doorway feels
right.

So the editor ships **three camera modes on one toggle**, not a single approach:

| Mode | Use | Cost |
|---|---|---|
| **First person** | Eye-level judgement of scale, doorways, sightlines | ~free (see below) |
| **Third person** | Default. Build near yourself with body context | ~free |
| **Free fly** | Overview layout, reaching rooftops and awkward angles | port `HomesteadFreeCameraView` |

The first two are close to free: `Mission.CameraIsFirstPerson` is a public settable property whose
setter drives the native camera (`api_v1.4.5.txt:564580`). Only free-fly needs real code, and that
already exists to port.

**The design consequence is the part worth getting right, and it must land in M4 rather than after.**
The ported editor takes its placement ray from the player's view. With three cameras that becomes
wrong, so placement needs a single seam:

```csharp
interface IPlacementRaySource {
    Vec3 Origin { get; }
    Vec3 Direction { get; }
}
```

implemented once per camera mode, with the editor logic asking it rather than reading the player.
Retrofitting that through 1,845 ported lines afterwards is far more expensive than building it in.

**Both implementations already exist in working code.**

Player-attached (covers first *and* third person, because `LookDirection` is the aim direction the
native camera already drives - so first-person build needs no new placement code at all). From the
original mod, `Reference Mods and Bakcups/Homesteads-main/MissionLogics/HomesteadSceneEditingMissionLogic.cs:55`:

```csharp
Vec3 eyeGlobalPos = Agent.Main.GetEyeGlobalPosition();
Vec3 maximumPos   = eyeGlobalPos + (Agent.Main.LookDirection * maximumPlaceDistance);
Mission.Current.Scene.RayCastForClosestEntityOrTerrain(
    eyeGlobalPos, maximumPos, out collisionDistance, out positionLookingAt, out gameEntityLookingAt);
if (collisionDistance > maximumPlaceDistance) { positionLookingAt = Vec3.Invalid; gameEntityLookingAt = null; }
```

Free camera: same raycast, with the origin and forward taken from
`MissionScreen.CombatCamera.Frame` (`HomesteadFreeCameraView.cs:164` already reads exactly that).

**Fork the placement core from the ORIGINAL mod, not the current one.** `Homesteads-main`'s editing
logic is **330 lines** against the shipped version's 1,845, and it is the same mechanism before it
accumulated free-build mode, tier caches, template mode, controller bindings and category cycling.
Starting from the small version and re-adding what we actually want is less work and far less
inherited coupling than stripping the large one - and it is the version that already did
first-person place-where-you-look.

Two details that will otherwise bite: in first person the placement ghost must not clip into the
camera (offset the minimum placement distance), and switching camera mode mid-placement must carry
the held object across rather than dropping it.

---

## 17. Milestones

| # | Milestone | Content | Est. |
|---|---|---|---|
| **M0** | Skeleton | Module, `SubModule.xml`, csproj vs 1.4.7 refs, loads clean | 0.5 d |
| ~~M1~~ | ~~Boot spike~~ **DONE** | Answered: tutorial-mode campaign has no main hero/party, and main-menu boot breaks other mods' init hooks. Entry moved in-campaign (§3). | done |
| ~~M2~~ | ~~Scene catalog~~ **PARTLY DONE** | `build_scene_catalog.ps1` done: 611 scenes, **405 multi-level**, 127 without navmesh. Browser UI + bulk load test still open (folded into M6/M7). | 0.5 d left |
| **M3** | Dumps | `build_asset_dump.ps1` → 1.4.7 dump; regen `api_v1.4.7.txt`; re-cut categories; naval + logical flags | 1.5 d |
| **M4** | Editor fork + API | Port editor/camera/picker onto `ISceneEditTarget`; strip tier/cost gating; **three camera modes behind `IPlacementRaySource`** (16b); write `HomesteadSceneTarget` adapter to validate the seam | 3.5 d |
| **M5** | Persistence | `SceneProject` save/load/list, autosave, resume-last, legacy template read/write | 1 d |
| **M6** | **Browse ⇄ edit loop** (§16) | `SceneCreatorBrowserState` as a real GameState; leave-mission returns to it; persisted browser state, recents, unsaved guard | 1 d |
| **M7** | **Build Selector** (§13) | Port picker; Tilde open; **in-picker search bar**; Source groups; pack loading; recents/favourites | 2 d |
| **M8** | **Logical placeables** (§9, §14) | Meshless prefabs un-excluded; proxy meshes + labels + facing arrows; `csc_core.xml` shipping set; auto-numbering + reorder list; per-type visibility | 2 d |
| **M9** | **Script catalog** (§15) | `build_script_catalog.ps1` — reflection pass + scene-mining pass over 618 scenes; merged schema with usage ranks and preview flags | 1.5 d |
| **M10** | **Script attachment** (§15) | Scripts tab; auto-generated variable editor; **stable GUIDs + entity-reference picker**; `csc_effects.xml` curated fire/animation set | 2.5 d |
| **M11** | Export + bake (§12) | In-game exporter (prefab XML, scene fragment) with physics/spawn/script/GUID stages; ship `bake_scene.py` + `bake_config.json`; retire `add_collision_and_spawns.py` | 2 d |
| **M12** | **Scene derivation** (§8) | `derive_scene.ps1`, "Derived" category, strip-level UI, redistribution decision | 1.5 d |
| **M13** | Map-patch terrain (§10b) | Coordinate picker; resolve the normalized-vs-raw coordinate question | 0.5 d |
| **M14** | ~~Campaign entry~~ + migration | Menu option + `csc.open` **done**; HomesteadBuilder template migration and deprecation notice remain | 0.5 d |
| **M15** | Polish | Localization (EN master + `Utils.GetLocalizedString`), README + `README_BAKE.md`, Workshop packaging | 1–2 d |
| — | *Deferred* | Homesteads adopts the library and deletes its copy (§7) | post-v1 |
| — | *Research only* | `terrain.bin` write support (§10d) | ? |

**M1 and M2 are gates** — both cheap, both able to invalidate the design. Run before M4.

---

## 18. Reference index

| Thing | Where |
|---|---|
| Editor logic to fork | `Homesteads Reloaded/Homesteads/MissionLogics/HomesteadSceneEditingMissionLogic.cs` |
| Free camera | `Homesteads Reloaded/Homesteads/Models/HomesteadFreeCameraView.cs` |
| Picker UI | `…/Views/HomesteadBuildingPickerVM.cs` + `_Module/GUI/Prefabs/HomesteadBuildingPicker.xml` |
| Template JSON model | `…/Models/HomesteadTemplate*.cs` |
| Mission bootstrap | `…/CustomMissions.cs:404-460` |
| Map-patch terrain | `…/CustomMissions.cs:38-60` |
| Prefab XML export | `HomesteadBuilder/HomesteadBuilder/Patches.cs:175` |
| Asset dump parser | `HomesteadBuilder/HomesteadBuilder/AssetLoader.cs` |
| Bake script to absorb | `add_collision_and_spawns.py`, `process_new_assets.py` |
| Main-menu API | `api_v1.4.5.txt:584912`, `:612620` |
| Game manager patterns | `api_v1.4.5.txt:953857`, `:1672010` |
| `MissionInitializerRecord` | `api_v1.4.5.txt:344949` (`SceneLevels` at `:344966`) |
| Terrain read APIs | `api_v1.4.5.txt:380418`, `:380869-380900`, `:380983` |
| Scene level vocabulary | `api_v1.4.5.txt:10444` |
| Picker open key (Tilde) | `Homesteads/Settings/…:217`, opened at `HomesteadSceneEditingMissionLogic.cs:269` |
| Current (modal) search | `HomesteadBuilder/Patches.cs:402` `TriggerSearch`, match logic `:439` |
| Runtime script attach | `api_v1.4.5.txt:397636` `CreateAndAddScriptComponent` |
| Script variable attribute | `api_v1.4.5.txt:406049` `EditorVisibleScriptComponentVariable` |
| Particle attach | `api_v1.4.5.txt:372763` `AddParticleSystemComponent` |
| Race track format | `Homesteads/Models/RaceTracks.cs` |
| Scene authoring rules | `docs/scene_authoring.md`, `Homesteads Reloaded/CUSTOM_SCENE_GUIDE.md` |
