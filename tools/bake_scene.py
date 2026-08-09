#!/usr/bin/env python3
"""
bake_scene.py - post-process a Custom Scene Creator export.

WHAT THIS IS FOR
----------------
The in-game editor writes plain XML: a prefab (one reusable object) or a scene fragment (everything
where it actually sits). That XML is already valid and already loads. Baking is for the things that
are tedious to do by hand afterwards, and that you will want to do the same way every time:

  * turning a bare marker into a real, working entity - a spawn point that an NPC can actually stand
    at needs a UsablePlace and an AnimationPoint under it, not just a tag
  * attaching the same script to everything of a kind - every brazier gets a fire, every torch gets
    a light cycle
  * renaming editor tags to whatever your own mod goes looking for
  * giving entities stable GUIDs, so scripts can reference each other
  * injecting physics shapes into mesh-based prefabs that lost them

Everything is driven by bake_config.json. Nothing here is specific to any one mod or scene - if the
built-in stages do not do what you need, the config is the first place to look and this file is the
second. It is meant to be edited.

USAGE
-----
    python bake_scene.py my_graveyard.xml
    python bake_scene.py my_graveyard.xml -o baked/my_graveyard.xml
    python bake_scene.py my_scene.scene_fragment.xml --stages markers,scripts
    python bake_scene.py --init-config           # write a documented starter config
    python bake_scene.py my_graveyard.xml --list # show what is in the file, change nothing

Exports live in:
    Documents\\Mount and Blade II Bannerlord\\CustomSceneCreator\\exports\\

Python 3.7+. No third-party packages.
"""

import argparse
import json
import os
import re
import shutil
import sys
import uuid
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG = os.path.join(HERE, "bake_config.json")

# A scene fragment is a list of sibling <game_entity> elements with no single root, because it is
# meant to be pasted inside an existing <entities> block. ElementTree needs exactly one root, so a
# wrapper goes on for parsing and comes back off on the way out.
FRAGMENT_ROOT = "csc_fragment_root"


# ---------------------------------------------------------------------------------------------
# Loading and saving
# ---------------------------------------------------------------------------------------------

def load_xml(path):
    """Parse an export. Returns (root, is_fragment, leading_comments)."""
    with open(path, "r", encoding="utf-8-sig") as handle:
        text = handle.read()

    # Comments before the first element are the export's own header - which scene it came from, when
    # it was written, where the anchor is. Worth keeping: it is the only record of where a baked file
    # came from once it has been copied somewhere else.
    # Every comment before the first real element. Two things make this fiddlier than it looks: the
    # XML declaration is not an element (or a prefab export, which always opens with <?xml, would
    # lose its header), and a comment may itself contain something that looks like a tag - the
    # fragment header says "paste inside the <entities> block".
    leading, cursor = [], 0
    while True:
        comment = re.compile(r"<!--.*?-->", re.DOTALL).search(text, cursor)
        between = text[cursor:comment.start()] if comment else text[cursor:]
        if re.search(r"<(?!\?)", between):
            break                          # a real element came first
        if not comment:
            break
        leading.append(comment.group(0))
        cursor = comment.end()

    stripped = text.lstrip()
    is_fragment = not stripped.startswith("<?xml") and "<prefabs" not in text[:2000]

    if is_fragment:
        body = re.sub(r"<\?xml.*?\?>", "", text, flags=re.DOTALL)
        root = ET.fromstring("<%s>%s</%s>" % (FRAGMENT_ROOT, body, FRAGMENT_ROOT))
    else:
        root = ET.fromstring(text)

    return root, is_fragment, leading


def save_xml(root, path, is_fragment, leading, declaration):
    os.makedirs(os.path.dirname(os.path.abspath(path)) or ".", exist_ok=True)
    indent(root)

    if is_fragment:
        # Emit the children only - the wrapper was never part of the file. Their indentation was
        # computed one level deep because of it, so take that level back off.
        pieces = [ET.tostring(child, encoding="unicode") for child in root]
        body = "".join(pieces)
        body = "\n".join(line[2:] if line.startswith("  ") else line
                         for line in body.split("\n"))
    else:
        body = ET.tostring(root, encoding="unicode")

    parts = []
    if declaration and not is_fragment:
        parts.append('<?xml version="1.0" encoding="utf-8"?>')
    parts.extend(leading)
    parts.append("<!-- Baked by tools/bake_scene.py -->")
    parts.append(body)

    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(parts).rstrip() + "\n")


def indent(elem, level=0):
    """In-place pretty-print. ET.indent() is 3.9+, and this has to run on older Pythons."""
    pad = "\n" + "  " * level
    if len(elem):
        if not (elem.text or "").strip():
            elem.text = pad + "  "
        for child in elem:
            indent(child, level + 1)
        if not (child.tail or "").strip():
            child.tail = pad
    if level and not (elem.tail or "").strip():
        elem.tail = pad


# ---------------------------------------------------------------------------------------------
# Helpers shared by the stages
# ---------------------------------------------------------------------------------------------

def iter_entities(root):
    """Every game_entity in the file, at any depth."""
    return root.iter("game_entity")


def tags_of(entity):
    tags = entity.find("tags")
    if tags is None:
        return []
    return [t.get("name", "") for t in tags.findall("tag")]


def add_tag(entity, name):
    if name in tags_of(entity):
        return False
    tags = entity.find("tags")
    if tags is None:
        tags = ET.SubElement(entity, "tags")
    ET.SubElement(tags, "tag", name=name)
    return True


def matches(entity, rule):
    """
    Does this entity match a rule?

    A rule may filter on "tag" (exact), "name" (exact), "name_pattern" (regex), "prefab" (exact) or
    "prefab_pattern" (regex). Anything the rule does not mention is not checked, so a rule with only
    a tag matches every entity carrying that tag regardless of what it is called.

    Note markers carry a NAME and ordinary objects carry a PREFAB - they are different attributes,
    and an object placed from the catalog has no name at all. Match torches on prefab_pattern, spawn
    points on tag or name.
    """
    keys = ("tag", "name", "name_pattern", "prefab", "prefab_pattern")
    if not any(key in rule for key in keys):
        return False

    if "tag" in rule and rule["tag"] not in tags_of(entity):
        return False
    if "name" in rule and entity.get("name", "") != rule["name"]:
        return False
    if "name_pattern" in rule and not re.search(rule["name_pattern"], entity.get("name", "")):
        return False
    if "prefab" in rule and entity.get("prefab", "") != rule["prefab"]:
        return False
    if "prefab_pattern" in rule and not re.search(rule["prefab_pattern"], entity.get("prefab", "")):
        return False
    return True


def ensure_scripts(entity):
    scripts = entity.find("scripts")
    if scripts is None:
        scripts = ET.SubElement(entity, "scripts")
    return scripts


def has_script(entity, name):
    scripts = entity.find("scripts")
    if scripts is None:
        return False
    return any(s.get("name") == name for s in scripts.findall("script"))


def attach_script(entity, name, variables):
    """Adds a script with its variables. Existing scripts of the same name are left alone."""
    if has_script(entity, name):
        return False
    script = ET.SubElement(ensure_scripts(entity), "script", name=name)
    if variables:
        block = ET.SubElement(script, "variables")
        for key, value in variables.items():
            ET.SubElement(block, "variable", name=key, value=str(value))
    return True


# ---------------------------------------------------------------------------------------------
# Stage: guids
# ---------------------------------------------------------------------------------------------

def stage_guids(root, config, report):
    """
    Give every entity a GUID.

    Scripts that point at another entity - AnimationPoint's PairEntity is the usual one - store the
    target's GUID. Without them there is nothing to point at, so any pairing has to be done by hand
    in the Modding Kit afterwards.
    """
    settings = config.get("guids", {})
    overwrite = settings.get("overwrite_existing", False)

    count = 0
    for entity in iter_entities(root):
        if entity.get("guid") and not overwrite:
            continue
        entity.set("guid", "{%s}" % str(uuid.uuid4()).upper())
        count += 1

    report("guids", "assigned %d GUID(s)" % count)


# ---------------------------------------------------------------------------------------------
# Stage: physics
# ---------------------------------------------------------------------------------------------

def load_physics_map(dump_path):
    """
    mesh name -> (physics shape, physics material), read from the asset dump.

    Column positions are fixed by the dump format: PhysicsShapes is 7, Meshes is 11. The generator
    keeps that layout deliberately so scripts like this one keep working across game versions.
    """
    mapping = {}
    with open(dump_path, "r", encoding="utf-8-sig") as handle:
        for line in handle:
            if line.startswith("#") or not line.strip():
                continue
            parts = [p.strip() for p in line.split("|")]
            if len(parts) < 12:
                continue
            shapes, meshes = parts[7], parts[11]
            material = parts[8] if len(parts) > 8 else ""
            if not shapes or not meshes:
                continue
            first_mesh = meshes.split(",")[0].strip()
            if first_mesh and first_mesh not in mapping:
                mapping[first_mesh] = (shapes.split(",")[0].strip(), material.split(",")[0].strip())
    return mapping


def find_asset_dump(configured):
    if configured and os.path.isfile(configured):
        return configured
    module_data = os.path.join(HERE, "..", "CustomSceneCreator", "_Module", "ModuleData")
    if os.path.isdir(module_data):
        dumps = sorted(f for f in os.listdir(module_data) if f.startswith("bannerlord_assets_"))
        if dumps:
            return os.path.join(module_data, dumps[-1])
    return None


def stage_physics(root, config, report):
    """
    Put physics shapes back on mesh-based entities.

    Note this usually does NOTHING to a Scene Creator export, and that is correct. Entities exported
    from the editor are prefab references - <game_entity prefab="barrel_a"> - and a prefab brings its
    own collision with it. This stage is for files where the geometry is spelled out as
    meta_mesh_components instead, which is what you get from a Modding Kit export or a hand-written
    prefab; those lose their physics and become scenery you can walk through.
    """
    settings = config.get("physics", {})
    dump = find_asset_dump(settings.get("asset_dump"))
    if not dump:
        report("physics", "SKIPPED - no asset dump found (set physics.asset_dump)")
        return

    mapping = load_physics_map(dump)
    override_material = settings.get("override_material") or None

    added, unknown = 0, set()
    for entity in iter_entities(root):
        components = entity.find("components")
        if components is None or entity.find("physics") is not None:
            continue
        for mesh in components.findall("meta_mesh_component"):
            name = mesh.get("name", "")
            if name not in mapping:
                if name:
                    unknown.add(name)
                continue
            shape, material = mapping[name]
            attrs = {"shape": shape}
            chosen = override_material or material
            if chosen:
                attrs["override_material"] = chosen
            ET.SubElement(entity, "physics", **attrs)
            added += 1
            break

    message = "added physics to %d entity(ies) from %s" % (added, os.path.basename(dump))
    if unknown:
        message += "; %d mesh(es) not in the dump" % len(unknown)
    report("physics", message)


# ---------------------------------------------------------------------------------------------
# Stage: markers
# ---------------------------------------------------------------------------------------------

def stage_markers(root, config, report):
    """
    Expand editor markers into working entities.

    A marker from the editor is a name and a tag at a position - which is all the editor can
    reasonably know. What makes it FUNCTION is mod-specific: a spawn point an NPC stands and works at
    needs a UsablePlace with an AnimationPoint child; a spawn point that only tells your code where to
    put an agent needs nothing at all.

    Each rule matches markers and describes what to give them:

        {
          "tag": "sp_npc",
          "scripts":  [ { "name": "UsablePlace", "variables": { ... } } ],
          "children": [ { "name": "animation_point", "scripts": [ ... ] } ],
          "add_tags": [ "my_mod_worker" ]
        }
    """
    rules = config.get("markers", {}).get("rules", [])
    if not rules:
        report("markers", "no rules configured")
        return

    touched = 0
    for entity in list(iter_entities(root)):
        for rule in rules:
            if not matches(entity, rule):
                continue

            changed = False
            for script in rule.get("scripts", []):
                changed |= attach_script(entity, script["name"], script.get("variables", {}))
            for tag in rule.get("add_tags", []):
                changed |= add_tag(entity, tag)

            for child_spec in rule.get("children", []):
                if not build_child(entity, child_spec):
                    continue
                changed = True

            if changed:
                touched += 1

    report("markers", "expanded %d marker(s)" % touched)


def build_child(parent, spec):
    """Adds a child entity under a marker, unless one by that name is already there."""
    children = parent.find("children")
    if children is None:
        children = ET.SubElement(parent, "children")

    name = spec.get("name", "child")
    if any(c.get("name") == name for c in children.findall("game_entity")):
        return False

    child = ET.SubElement(children, "game_entity", name=name, old_prefab_name="")
    if spec.get("position") or spec.get("rotation"):
        ET.SubElement(
            child, "transform",
            position=spec.get("position", "0.000, 0.000, 0.000"),
            rotation_euler=spec.get("rotation", "0.000, 0.000, 0.000"))
    for tag in spec.get("tags", []):
        add_tag(child, tag)
    for script in spec.get("scripts", []):
        attach_script(child, script["name"], script.get("variables", {}))
    return True


# ---------------------------------------------------------------------------------------------
# Stage: scripts
# ---------------------------------------------------------------------------------------------

def stage_scripts(root, config, report):
    """
    Attach scripts in bulk.

    The editor can attach a script to one object at a time, which is right when a fire belongs on
    this brazier and not that one. This is for the other case: every torch in the scene burns.
    """
    rules = config.get("scripts", {}).get("rules", [])
    if not rules:
        report("scripts", "no rules configured")
        return

    attached = 0
    for entity in iter_entities(root):
        for rule in rules:
            if not matches(entity, rule):
                continue
            for script in rule.get("attach", []):
                if attach_script(entity, script["name"], script.get("variables", {})):
                    attached += 1

    report("scripts", "attached %d script(s)" % attached)


# ---------------------------------------------------------------------------------------------
# Stage: retag
# ---------------------------------------------------------------------------------------------

def stage_retag(root, config, report):
    """
    Rename tags, so the editor's names and your mod's names do not have to agree.

    The editor ships generic tags - sp_enemy, race_gate - because it cannot know what your code
    looks for. Rather than renaming markers by hand, map them here once.
    """
    mapping = config.get("retag", {}).get("map", {})
    if not mapping:
        report("retag", "no mappings configured")
        return

    renamed = 0
    for entity in iter_entities(root):
        tags = entity.find("tags")
        if tags is None:
            continue
        for tag in tags.findall("tag"):
            old = tag.get("name", "")
            if old in mapping:
                tag.set("name", mapping[old])
                renamed += 1

    report("retag", "renamed %d tag(s)" % renamed)


# ---------------------------------------------------------------------------------------------
# Stage: deploy
# ---------------------------------------------------------------------------------------------

def stage_deploy(output_path, config, report):
    """
    Copy the baked file wherever it has to live - a module's Prefabs folder, a Dist folder.

    Kept as a stage rather than a manual step because forgetting it is the classic way to spend
    twenty minutes wondering why a change did nothing in game.
    """
    destinations = config.get("deploy", {}).get("destinations", [])
    if not destinations:
        report("deploy", "no destinations configured")
        return

    copied = 0
    for destination in destinations:
        target = os.path.expandvars(os.path.expanduser(destination))
        if os.path.isdir(target):
            target = os.path.join(target, os.path.basename(output_path))
        parent = os.path.dirname(os.path.abspath(target))
        if not os.path.isdir(parent):
            report("deploy", "SKIPPED '%s' - folder does not exist" % parent)
            continue
        shutil.copy2(output_path, target)
        copied += 1

    report("deploy", "copied to %d destination(s)" % copied)


# ---------------------------------------------------------------------------------------------
# Inspection
# ---------------------------------------------------------------------------------------------

def list_contents(root):
    """What is in this file: entity counts by prefab, and every tag. Changes nothing."""
    prefabs, tags, markers = {}, {}, []
    total = 0
    for entity in iter_entities(root):
        total += 1
        key = entity.get("prefab") or entity.get("name") or "(unnamed)"
        prefabs[key] = prefabs.get(key, 0) + 1
        for tag in tags_of(entity):
            tags[tag] = tags.get(tag, 0) + 1
            markers.append(entity.get("name", ""))

    print("%d entities" % total)
    print("\nBy prefab or name:")
    for name, count in sorted(prefabs.items(), key=lambda kv: (-kv[1], kv[0]))[:40]:
        print("  %-48s %d" % (name, count))
    if tags:
        print("\nTags (these are what your mod code looks for):")
        for name, count in sorted(tags.items(), key=lambda kv: (-kv[1], kv[0])):
            print("  %-48s %d" % (name, count))
    else:
        print("\nNo tags. Nothing here is addressable from code yet.")


# ---------------------------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------------------------

STARTER_CONFIG = {
    "_comment": [
        "Config for bake_scene.py. Every stage is optional; a stage with nothing configured",
        "reports that it did nothing and moves on.",
        "Run stages in a chosen order with --stages markers,scripts,guids"
    ],

    "stages": ["markers", "scripts", "retag", "physics", "guids", "deploy"],

    "guids": {
        "overwrite_existing": False
    },

    "physics": {
        "_comment": "Only affects mesh-component entities. Prefab references already have physics.",
        "asset_dump": "",
        "override_material": ""
    },

    "markers": {
        "_comment": [
            "Turn markers into working entities. The example below makes every sp_npc marker",
            "a place an NPC can stand and work at. Delete it if you do not want that."
        ],
        "rules": [
            {
                "tag": "sp_npc",
                "scripts": [
                    {
                        "name": "UsablePlace",
                        "variables": {
                            "PilotStandingPointTag": "Pilot",
                            "AmmoPickUpTag": "ammopickup",
                            "WaitStandingPointTag": "Wait",
                            "NavMeshPrefabName": ""
                        }
                    }
                ],
                "children": [
                    {
                        "name": "animation_point",
                        "scripts": [
                            {
                                "name": "AnimationPoint",
                                "variables": {
                                    "LoopStartAction": "act_npc_villager_shoveling",
                                    "MinWaitinSeconds": "50.000",
                                    "MaxWaitInSeconds": "320.000",
                                    "AutoSheathWeapons": "true",
                                    "TranslateUser": "true"
                                }
                            }
                        ]
                    }
                ]
            }
        ]
    },

    "scripts": {
        "_comment": [
            "Bulk attachment. The example lights every torch in the scene.",
            "Objects placed from the catalog have a prefab, not a name - match them on prefab_pattern."
        ],
        "rules": [
            {
                "prefab_pattern": "torch",
                "attach": [
                    {"name": "LightCycle", "variables": {"alwaysBurn": "true"}}
                ]
            }
        ]
    },

    "retag": {
        "_comment": "Editor tag on the left, the tag your mod looks for on the right.",
        "map": {}
    },

    "deploy": {
        "_comment": "Folders or full paths. Environment variables and ~ are expanded.",
        "destinations": []
    }
}


def load_config(path):
    if not path or not os.path.isfile(path):
        return {}
    with open(path, "r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def write_starter_config(path):
    if os.path.exists(path):
        print("%s already exists - not overwriting." % path)
        return 1
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(STARTER_CONFIG, handle, indent=2)
        handle.write("\n")
    print("Wrote %s" % path)
    print("Edit it, then run: python bake_scene.py <your-export.xml>")
    return 0


# ---------------------------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------------------------

STAGES = {
    "guids": stage_guids,
    "physics": stage_physics,
    "markers": stage_markers,
    "scripts": stage_scripts,
    "retag": stage_retag,
}


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Post-process a Custom Scene Creator export.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="Exports live in Documents\\Mount and Blade II Bannerlord\\CustomSceneCreator\\exports\\")
    parser.add_argument("input", nargs="?", help="exported prefab or scene fragment XML")
    parser.add_argument("-o", "--output", help="where to write (default: alongside, .baked.xml)")
    parser.add_argument("-c", "--config", default=DEFAULT_CONFIG, help="config file")
    parser.add_argument("--stages", help="comma-separated stages to run, overriding the config")
    parser.add_argument("--list", action="store_true", help="show what is in the file and stop")
    parser.add_argument("--dry-run", action="store_true", help="run the stages but write nothing")
    parser.add_argument("--init-config", action="store_true", help="write a starter config and stop")
    args = parser.parse_args(argv)

    if args.init_config:
        return write_starter_config(args.config)

    if not args.input:
        parser.error("an input file is required (or use --init-config)")
    if not os.path.isfile(args.input):
        print("Not found: %s" % args.input, file=sys.stderr)
        return 1

    try:
        root, is_fragment, leading = load_xml(args.input)
    except ET.ParseError as error:
        print("Could not parse %s: %s" % (args.input, error), file=sys.stderr)
        return 1

    if args.list:
        list_contents(root)
        return 0

    config = load_config(args.config)
    if not config:
        print("No config at %s - running with defaults, which do very little." % args.config)
        print("Write one with: python bake_scene.py --init-config")

    order = [s.strip() for s in args.stages.split(",")] if args.stages \
        else config.get("stages", list(STAGES) + ["deploy"])

    lines = []

    def report(stage, message):
        lines.append("  %-9s %s" % (stage, message))

    print("Baking %s (%s)" % (os.path.basename(args.input),
                              "scene fragment" if is_fragment else "prefab"))

    for stage in order:
        if stage == "deploy":
            continue                       # runs after the file exists
        handler = STAGES.get(stage)
        if handler is None:
            report(stage, "unknown stage - skipped")
            continue
        handler(root, config, report)

    if args.dry_run:
        print("\n".join(lines))
        print("\nDry run - nothing written.")
        return 0

    output = args.output
    if not output:
        base, ext = os.path.splitext(args.input)
        output = base + ".baked" + (ext or ".xml")

    save_xml(root, output, is_fragment, leading, declaration=not is_fragment)

    if "deploy" in order:
        stage_deploy(output, config, report)

    print("\n".join(lines))
    print("Wrote %s" % output)
    return 0


if __name__ == "__main__":
    sys.exit(main())
