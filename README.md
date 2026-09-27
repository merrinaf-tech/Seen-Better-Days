# Seen Better Days

A Cities: Skylines II mod that makes growable buildings look the way their circumstances are.

A building's appearance follows what the simulation already knows about it: its condition, the
services it receives, whether it is abandoned, its level and how the value of its street compares
with the rest of the city. Nothing accumulates and nothing ages on a timer, so a building whose
situation improves recovers on its own.

## How it works

- **Colour layer, city-wide.** A per-instance override of the building's own mesh colours
  (`CustomMeshColor`) darkens, fades and slightly tints each building on its own. Two instances of
  the same prefab no longer look identical, and no texture or prefab is changed.
- **Detail layer, near the camera.** Within about 200 m, weathered buildings receive decals placed
  on wall surfaces; beyond 280 m they are removed. Placement is deterministic, so a wall gets
  the same marks every time the camera comes back.
- **Player colours.** A colour set in the game's colour panel or with Recolor becomes the
  building's clean colour and is weathered on top. While the panel's Customize tab is open the
  building shows its clean colour, so the panel reads and edits that rather than the weathered one
  (the small UI module `SeenBetterDays.mjs` reports the tab, which the game keeps to its UI).
- **Saving.** Colour overrides are put back to the clean colour while the game writes a save:
  switched off where that colour is the prefab's own, left on where the player chose it. The mod's
  decal entities are left out of the save.

## States and decal packs

Decals come only from an approved list, and only from packs the player has installed. All are
optional; the colour layer works without any of them.

Each state keeps the marks of the one before it and adds its own:

| State | Adds | From |
|---|---|---|
| Maintained | nothing | - |
| Aged | cracks and leaks; rust on industrial buildings | [Cracks and Damage Decal Pack](https://mods.paradoxplaza.com/mods/80583/Windows), [Stains and Leakage Decal Pack](https://mods.paradoxplaza.com/mods/80584/Windows); [[G87] Moss and Rust](https://mods.paradoxplaza.com/mods/108303/Windows) |
| Worn | dirt, rubbish, fallen leaves, moss, rust, small tags | [[G87] Trash Decals](https://mods.paradoxplaza.com/mods/87720/Windows), [Fallen leaves decals](https://mods.paradoxplaza.com/mods/96995/Windows), [[G87] Moss and Rust](https://mods.paradoxplaza.com/mods/108303/Windows), [Scribbles and Tags Decal Pack](https://mods.paradoxplaza.com/mods/93949/Windows); moss from [Urban Decay Pack 1](https://mods.paradoxplaza.com/mods/120305/Windows) and [Urban Decay Pack 2](https://mods.paradoxplaza.com/mods/120500/Windows) |
| Neglected | street art, peeling plaster, holes, pipe leaks, torn posters | [Street Art Decal Pack](https://mods.paradoxplaza.com/mods/93866/Windows); wall pieces of [Urban Decay Pack 1](https://mods.paradoxplaza.com/mods/120305/Windows) and [Urban Decay Pack 2](https://mods.paradoxplaza.com/mods/120500/Windows) |
| Decayed | heavy stains, patched walls | [[G87] Stains and Puddles Decals: Wet Pack](https://mods.paradoxplaza.com/mods/87948/Windows), [[G87] Road Repair: Patch Pack](https://mods.paradoxplaza.com/mods/92004/Windows) |

From Moss and Rust, each piece supplies its own family. From the two Urban Decay packs only the
wall pieces are used - peeling plaster, holes, leaks, moss and torn posters - listed by number,
since their names carry no keyword; brick walls, rusty roofs and hazard stripes are left out. The list lives in `SeenBetterDays/Rendering/DecalPrefabCatalog.cs`.

## Options

| Option | Default | Meaning |
|---|---:|---|
| Weather buildings | On | Enables or removes the whole effect, live. |
| Close-range detail | On | Enables the decal layer. |
| Intensity | 100% | Scales colour and decal strength from 25% to 200%. |
| Rebuild Seen Better Days appearance | - | Removes everything the mod drew and applies the current rules again. |
| Show maintenance in the tooltip | Off | *Advanced.* How weathered a building is, and why. |
| Set a building's state by hand | Off | *Advanced.* Ctrl+Alt+F1-F5 hold the selected building at a state, F6 releases it. |
| Developer shortcuts | Off | *Advanced.* The Ctrl+Alt keys used to develop and tune the mod. |

## Building

Close Cities: Skylines II first: the build deploys into the game's local Mods folder, and the
project refuses to build while `Cities2.exe` is running.

```powershell
$env:DOTNET_ROLL_FORWARD = 'Major'
dotnet build SeenBetterDays\SeenBetterDays.csproj -c Release
```

The modding toolchain must be installed from the game (Options > Modding). Diagnostics are written
to `%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Logs\SeenBetterDays.log`.

## Licence

MIT. See [LICENSE](LICENSE).
