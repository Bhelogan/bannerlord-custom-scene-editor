# Advanced navmesh inspection tools

These Python 3 tools inspect and regression-test Bannerlord NMG8/NMG9 navmesh files without loading
the game. They are an advanced companion to Custom Scene Creator 1.0.4, not the normal authoring
workflow.

For ordinary work, author **Cutout**, **Add Area**, and **Elevated Navmesh** inside CSC, save the
project, and use **Rebake Navmesh**. CSC's C# baker is the authoritative implementation and is the
only tool here that supports elevated stairs, ramps, tunnels, bridges, platforms, and wall walks.

The Python writers support solid cutouts and terrain-sampled ground additions. They do **not** apply
the manifest's elevated routes.

## Safety

- Work from a copied scene or backup.
- Writers refuse to overwrite their input or an existing output.
- A `navmesh.bin` belongs to one exact scene and terrain. Never transplant it to another map.
- Test candidates in an isolated scene before using them in a campaign module.
- NMG8 and NMG9 are supported; older revisions are detected and rejected.

## Inspect and compare

```powershell
python tools/navmesh_inspect.py "...\SceneObj\my_scene\navmesh.bin"
python tools/navmesh_inspect.py untouched.bin --compare edited.bin --json
```

Inspection validates the RNM1 wrapper, decompresses the raw mesh, parses vertices, edges, faces, and
face loops, and reports structural counts and hashes.

Validate individual files or scan scene folders:

```powershell
python tools/validate_navmeshes.py first.bin second.bin
python tools/validate_navmeshes.py "...\Modules" --limit 50
```

## Read the CSC authoring plan

CSC writes `exports/navmesh-cutouts/<project>.navcut.json`. This is a human-readable plan, not a file
Bannerlord loads. Preview its cutouts without writing anything:

```powershell
python tools/navmesh_cutout.py `
  "...\SceneObj\my_scene\navmesh.bin" `
  --manifest "...\exports\navmesh-cutouts\my_project.navcut.json"
```

Preview terrain-level additions:

```powershell
python tools/navmesh_addition_plan.py `
  "...\SceneObj\my_scene\navmesh.bin" `
  "...\exports\navmesh-cutouts\my_project.navcut.json"
```

The addition planner requires the boundary samples written by current CSC builds. Older
center/radius-only notes must be re-authored.

## Produce a separately named candidate

Apply the manifest's cutouts as one validated workflow:

```powershell
python tools/navmesh_workflow.py `
  "...\exports\navmesh-cutouts\my_project.navcut.json" `
  --source "...\SceneObj\my_scene\navmesh.bin" `
  --output ".\navmesh.cutout-candidate.bin"
```

Or invoke the cutout writer directly:

```powershell
python tools/navmesh_apply_cutout.py `
  "...\SceneObj\my_scene\navmesh.bin" `
  ".\navmesh.cutout-candidate.bin" `
  --manifest "...\exports\navmesh-cutouts\my_project.navcut.json"
```

Apply terrain-level `RequiredAreas` to a second candidate:

```powershell
python tools/navmesh_apply_addition.py `
  "...\exports\navmesh-cutouts\my_project.navcut.json" `
  --source ".\navmesh.cutout-candidate.bin" `
  --output ".\navmesh.ground-candidate.bin"
```

The ground writer clusters overlapping marks, constructs a terrain-sampled envelope, connects it to
an existing open edge, triangulates it, serializes it as NMG9, reopens it, and validates topology.
Its convex envelope can include space outside a concave or L-shaped set of marks; it also refuses
clusters spanning multiple face groups. Use the in-game C# baker for release output.

## Automated tests

```powershell
python -m unittest discover -s tools -p "test_navmesh*.py" -v
```

The suite covers NMG decoding/encoding, exact round trips, overwrite refusal, cutout geometry,
ground-addition planning/writing, manifest compatibility, and structural validation.
