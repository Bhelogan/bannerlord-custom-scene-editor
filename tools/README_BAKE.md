# bake_scene.py

Post-processes an export from the in-game editor. Python 3.7+, no third-party packages.

**You may not need it.** What the editor writes is already valid XML that already loads — a prefab you
can place, or a scene fragment you can paste into a `scene.xscene`. Baking is for the work that comes
*after* layout, and that you want done the same way every time.

## Quick start

```bash
python bake_scene.py --init-config
python bake_scene.py my_graveyard.xml
```

The first command writes a documented `bake_config.json` next to the script. Edit it, then run the
second. Output goes to `my_graveyard.baked.xml` unless you pass `-o`.

Exports live in `Documents\Mount and Blade II Bannerlord\CustomSceneCreator\exports\`.

To see what is in a file without changing anything — every entity and, more usefully, every tag your
mod code could look for:

```bash
python bake_scene.py my_graveyard.xml --list
```

## Stages

Each is optional, driven entirely by `bake_config.json`, and reports what it did. Run a subset with
`--stages markers,scripts`, or preview with `--dry-run`.

| Stage | What it does |
|---|---|
| `markers` | Turns a bare marker into a working entity. A spawn point an NPC actually stands and works at needs a `UsablePlace` with an `AnimationPoint` child — the editor places the marker, this gives it the machinery. |
| `scripts` | Attaches a script to everything matching a rule. The editor attaches one at a time, which is right when only *this* brazier burns; this is for when every torch does. |
| `retag` | Renames tags. The editor ships generic ones (`sp_enemy`, `race_gate`) because it cannot know what your code looks for — map them once here instead of renaming markers by hand. |
| `physics` | Adds `<physics shape="…">` to mesh-based entities, read from the asset dump. |
| `guids` | Gives every entity a GUID. Scripts that reference another entity — `AnimationPoint.PairEntity` — store the target's GUID, so without these there is nothing to point at. |
| `deploy` | Copies the result where it has to live. Forgetting this step is the classic way to spend twenty minutes wondering why nothing changed in game. |

**`physics` usually does nothing to an editor export, and that is correct.** Objects placed from the
catalog export as prefab *references* (`<game_entity prefab="barrel_a">`), and a prefab brings its own
collision. The stage exists for files whose geometry is spelled out as `meta_mesh_component`s — a
Modding Kit export, or a hand-written prefab — which lose their physics and become scenery you walk
straight through.

## Matching rules

`markers`, `scripts` and `retag` select entities the same way. A rule may filter on:

| Key | Matches |
|---|---|
| `tag` | exact tag — the usual way to reach markers |
| `name` / `name_pattern` | exact name / regex |
| `prefab` / `prefab_pattern` | exact prefab / regex |

Anything a rule does not mention is not checked, so `{"tag": "sp_enemy"}` matches every enemy spawn
regardless of name.

**Markers carry a name; catalog objects carry a prefab.** They are different attributes, and an object
placed from the catalog has no name at all — match torches with `prefab_pattern`, spawn points with
`tag`.

Attaching a script that is already on an entity does nothing, so re-baking a file is safe.

## Editing it

This is meant to be edited. If the built-in stages do not do what your mod needs, the config is the
first place to look and the script is the second: stages are plain functions taking `(root, config,
report)`, registered in the `STAGES` dict at the bottom.
