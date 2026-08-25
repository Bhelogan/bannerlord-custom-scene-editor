# NMG9 writer engine-load control

`navmesh.bin` is an experimental copy generated from the untouched current-format
`csc_battle_terrain_007` control. It applies the same four-corner storehouse hole as the successful
Modding Kit edit, but its repair ring was created entirely by the offline CSC tools.

- SHA-256: `D85A16067B37BFAD4F29AAF0ED182E76EECB783F2113DAEE908B84F2DE98533A`
- Size: 1,487,229 bytes
- Counts: 25,598 vertices; 51,267 edges; 25,638 faces
- Operation: 8 intersected faces removed; 15 repair triangles added
- Offline validation: passed exact decode, all reference/edge-loop checks, zero repair-area error,
  16 unmeshed footprint-interior samples, zero non-manifold edges, and the original connected-face
  component count

Engine-load control **passed on 2026-08-16** in the disposable
`csc_homestead_storehouse_navmesh` scene. Bannerlord loaded the generated file, the footprint center
reported no navmesh with the nearest face center 5.0 m away, and the surrounding network remained
connected. A route across the storehouse measured 16.0 m straight and 28.4 m by navmesh
(`1.78x`, `direct no`).

Observed success in CSC Navmesh mode:

1. The center/roof probe reports no navmesh at that XY.
2. Two points across the storehouse remain reachable by a substantial detour.
3. The direct route reports `no`.
4. Nearby open ground still resolves to the same connected group/island.
5. Agents route around the storehouse without a startup freeze or native crash.

Restore the Modding Kit control immediately if the scene fails to load or Bannerlord terminates.
