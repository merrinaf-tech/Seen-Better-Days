# Seen Better Days — developer build

Seen Better Days makes individual growable buildings reflect their current circumstances without
replacing textures or changing the shared building prefab. A cheap per-instance colour layer works
city-wide; a decal detail layer adds local dirt, stains, cracks, moss, rust and graffiti only near
the camera.

This is a private developer build. The rendering investigation is recorded in
[`docs/RENDERING_POC.md`](docs/RENDERING_POC.md). Internal development notes are in
`docs/LESSONS.md` and must not be published.

## Current behaviour

- Automatic weathering starts with the city and scans growables in small round-robin batches.
- The target comes from current building condition, efficiency, abandonment, level and the value
  of its street relative to the city. It is not accumulated building age and can recover.
- `CustomMeshColor` supplies the city-wide darkening, fading and slight family tint. Buildings
  with incompatible colour channels are left unchanged rather than repainted incorrectly.
- Decals are created inside 200 m of the camera and removed beyond 280 m. Buildings below 8%
  weathering use colour alone.
- Automatic decals come only from an explicit source whitelist. The current approved packs are
  `Stains and Leakage Decal Pack`, `Scribbles & Tags Decal Pack` and
  `Cracks and Damage Decal Pack`, plus the G87 Wet Decals, Road Repair and Trash packs,
  `Fallen leaves decals` and `Street Art Decal Pack`. Each may supply only its assigned family.
  Missing families stay absent instead of being replaced with unrelated artwork. The G87 Wet
  **Surfaces** pack contains terrain-surface assets rather than decal prefabs and cannot be
  projected onto buildings by this renderer.
- Placement uses real mesh surfaces. The facade bounds express the intended region; collected wall
  triangles supply the actual position and normal.
- Nothing is stored in the city. Colour overrides and decal entities are removed during
  serialization and rebuilt from data the game already saves. Automatic rebuilding and developer
  hotkeys stay paused while the save writer is active and for ten seconds afterwards, so they
  cannot structurally modify the world while native save and rendering work is settling.

## Options

The mod registers an English options page with these controls:

| Option | Default | Meaning |
|---|---:|---|
| Weather buildings | On | Enables or removes the complete effect live. |
| Close-range detail | On | Enables the proximity-driven decal layer. |
| Intensity | 100% | Scales visible colour and decal strength from 25% to 200%. |
| Show maintenance in the tooltip | Off | Shows the state, percentage and current reasons under the cursor. |
| Set a building's state by hand | Off | Enables the F1–F6 comparison keys below. |

## Developer controls

Every key uses **Ctrl+Alt**. Select a growable with its info panel open before a
building-specific action.

| Key | Action |
|---|---|
| `F1`…`F5` | Hold the selected building at Maintained, Aged, Worn, Neglected or Decayed. Requires the advanced option. |
| `F6` | Release the selected building back to the simulation. |
| `U` | Toggle the automatic close-range decal layer for comparison. |
| `W` | Toggle automatic colour weathering for comparison. |
| `V` | Apply the current deterministic decal plan to the selected building immediately. |
| `J` | Cycle manual colour weathering on the selected building. |
| `A` / `S` | Apply a debug profile; `S` changes the seed. |
| `1` / `2` / `3` | Use darkness-only, fading or full colour response. |
| `Q` | Log the city census and weathering distribution. |
| `H` | Place one decal at the cursor's real raycast hit. |
| `P` | Measure nearby hand-placed decals. |
| `Y` | Drop a flat ground-control decal at the camera pivot. |
| `T` | Run the single-facade height diagnostic. |
| `F` | Cycle the facade used by single-facade diagnostics. |
| `K` / `G` | Flip decal projection or change its normal offset. |
| `N` / `B` / `C` | Force a decal, allow non-building decals or dump the catalogue. |
| `I` | Describe the selected building and its rendering state. |
| `D` | Remove colour and decals from the selected or last tracked building. |
| `X` | Remove all tracked overlays and sweep stray decal entities. |
| `Z` | Emergency cleanup: remove all custom colours from growables, including Recolor's. |

## Latest verified run

The 2026-09-19 in-game run verified the final surface-collection and allocation path on a dense
district. It produced 258 detail events across 239 unique buildings, with 6–24 marks per building
and a 12.6 average. All four facades and all six detail families were represented. Of 3,256 placed
marks, 3,249 used the collected wall surface directly, seven used nearest-mesh recovery and none
was skipped. Nineteen buildings were rebuilt after leaving and re-entering the detail radius. The
session logged no warnings or errors.

The `ExclusiveGround` filter was also active: the automatic pool fell from 675 to 650 decals and
the former road-arrow and sports-field candidates disappeared. That run preceded the strict
source whitelist. The next build reduces the automatic pool again to the approved packs and
removes the arbitrary fallback when a family is unavailable; it still needs in-game validation.

The 2026-09-18 save test remains the latest serialization check. The save guard removed 1,560
colour overrides and 30 decal entities before saving, with no warnings or errors.

During visual validation, check:

1. mark density at 8%, 22%, 47%, 72% and 100% weathering;
2. whether large buildings receive more coverage without looking saturated;
3. whether Dirt, Stain, Crack, Moss, Rust and Graffiti remain represented at high intensity;
4. whether any road, sports-field or other ground-only artwork appears on a wall;
5. frame-time behaviour while moving quickly through a dense district;
6. save/reload cleanup and reconstruction.

## Build

Close Cities: Skylines II before building. The deploy target replaces the installed DLL, and the
project blocks deployment while `Cities2.exe` is running to avoid a half-written mod folder.

```powershell
$env:DOTNET_ROLL_FORWARD = 'Major'
dotnet build SeenBetterDays\SeenBetterDays.csproj -c Release
```

The build deploys to
`%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Mods\SeenBetterDays`. Runtime diagnostics are
written to `%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Logs\SeenBetterDays.log`.
