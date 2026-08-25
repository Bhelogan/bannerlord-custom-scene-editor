#!/usr/bin/env python3
"""
Audit SceneObj folders for the three faults that make a scene load but not work.

WHY
    Every one of these is silent. The scene loads, the terrain draws, agents spawn and draw
    their weapons - and then melee never engages, because nothing can path. Only ranged
    troops appear to work. It reads exactly like an AI bug and it is not one.

WHAT IT CHECKS
    1. SHADOWING          A module folder named after a stock scene, without a scene.xscene
                          of its own. Bannerlord resolves scenes by name across every loaded
                          module, so a hollow folder can win over the real scene and give you
                          a scene with no navmesh. These appear on their own - the engine
                          writes ShaderCache next to whichever scene folder it resolved.

    2. NAME MISMATCH      scene.xscene declares <scene name="X"> while the folder is called Y.
                          Copying a stock scene folder and renaming the directory leaves the
                          old name inside. Every stock scene has these matching.

    3. MISSING PIECES     A scene folder with no navmesh.bin (or no terrain.bin). Adding props
                          to a copied scene does NOT re-bake its navmesh - that is a Modding
                          Kit step, and it is the one people skip.

USAGE
    python check_scene_folders.py "C:\\...\\Mount & Blade II Bannerlord\\Modules"
    python check_scene_folders.py <modules> --module MyModName     # just one module
"""
import argparse
import os
import re
import sys

STOCK_MODULES = {
    "Native", "SandBox", "SandBoxCore", "StoryMode", "CustomBattle",
    "Multiplayer", "BirthAndDeath", "NavalDLC", "Cutscenes_Extended",
}

SCENE_NAME_RE = re.compile(r'<scene\b[^>]*\bname="([^"]*)"')


def scene_folders(modules_root):
    """{module: {scene_folder_name: path}} for every module that has a SceneObj."""
    found = {}
    try:
        modules = sorted(os.listdir(modules_root))
    except OSError as exc:
        print(f"Cannot read {modules_root}: {exc}", file=sys.stderr)
        return found

    for module in modules:
        sceneobj = os.path.join(modules_root, module, "SceneObj")
        if not os.path.isdir(sceneobj):
            continue
        scenes = {}
        for entry in sorted(os.listdir(sceneobj)):
            path = os.path.join(sceneobj, entry)
            if os.path.isdir(path):
                scenes[entry] = path
        if scenes:
            found[module] = scenes
    return found


def declared_name(scene_dir):
    """The name attribute inside scene.xscene, or None if there is no scene.xscene."""
    path = os.path.join(scene_dir, "scene.xscene")
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            head = handle.read(4096)
    except OSError:
        return None
    match = SCENE_NAME_RE.search(head)
    return match.group(1) if match else ""


def main():
    ap = argparse.ArgumentParser(description="Audit SceneObj folders for silent navmesh faults.")
    ap.add_argument("modules", help="path to the game's Modules folder")
    ap.add_argument("--module", help="only check this module")
    args = ap.parse_args()

    everything = scene_folders(args.modules)
    if not everything:
        print("No SceneObj folders found. Is that the Modules path?")
        return 1

    # Which scene names the stock modules provide - what a mod folder could shadow.
    stock_scenes = {}
    for module, scenes in everything.items():
        if module in STOCK_MODULES:
            for name, path in scenes.items():
                stock_scenes.setdefault(name, module)

    problems = 0
    checked = 0

    for module, scenes in everything.items():
        if module in STOCK_MODULES:
            continue
        if args.module and module != args.module:
            continue

        for name, path in sorted(scenes.items()):
            checked += 1
            files = set(os.listdir(path))
            has_scene = "scene.xscene" in files
            issues = []

            if name in stock_scenes and not has_scene:
                issues.append(
                    f"SHADOWS the stock scene '{name}' from {stock_scenes[name]}, but has no "
                    f"scene.xscene of its own. Anything loading '{name}' can get THIS folder - a "
                    f"scene with no navmesh. Delete it; it only holds {sorted(files) or 'nothing'}.")
            elif not has_scene:
                issues.append(f"has no scene.xscene (contains {sorted(files) or 'nothing'}). "
                              "Probably a leftover - delete it.")
            else:
                inside = declared_name(path)
                if inside and inside != name:
                    issues.append(
                        f"scene.xscene declares name=\"{inside}\" but the folder is \"{name}\". "
                        "Copying a scene folder and renaming the directory leaves the old name "
                        "inside. Make them match.")
                if "navmesh.bin" not in files:
                    issues.append("NO navmesh.bin. Agents cannot path: melee will never engage, "
                                  "only archers will act. Bake the navmesh in the Modding Kit.")
                if "terrain.bin" not in files:
                    issues.append("no terrain.bin - unusual for an outdoor scene.")

            if issues:
                problems += 1
                print(f"\n{module}/SceneObj/{name}")
                for issue in issues:
                    print(f"  ! {issue}")

    print(f"\nChecked {checked} scene folder(s) in non-stock modules.")
    if problems == 0:
        print("No problems found.")
    else:
        print(f"{problems} folder(s) need attention.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
