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
  on real wall surfaces; beyond 280 m they are removed. Placement is deterministic, so a wall gets
  the same marks every time the camera comes back.
- **Saving.** Colour overrides are switched off while the game writes a save and switched back on
  afterwards, and the mod's decal entities are left out of the save.

## Decal packs

Decals come only from an approved list, and only from packs the player has installed. All are
optional; the colour layer works without any of them.

| Pack | Paradox Mods |
|---|---|
| Stains and Leakage Decal Pack | [80584](https://mods.paradoxplaza.com/mods/80584/Windows) |
| Cracks and Damage Decal Pack | [80583](https://mods.paradoxplaza.com/mods/80583/Windows) |
| Scribbles and Tags Decal Pack | [93949](https://mods.paradoxplaza.com/mods/93949/Windows) |
| Street Art Decal Pack | [93866](https://mods.paradoxplaza.com/mods/93866/Windows) |
| [G87] Stains and Puddles Decals: Wet Pack | [87948](https://mods.paradoxplaza.com/mods/87948/Windows) |
| [G87] Road Repair: Patch Pack | [92004](https://mods.paradoxplaza.com/mods/92004/Windows) |
| [G87] Trash Decals | [87720](https://mods.paradoxplaza.com/mods/87720/Windows) |
| Fallen leaves decals | [96995](https://mods.paradoxplaza.com/mods/96995/Windows) |

The list lives in `SeenBetterDays/Rendering/DecalPrefabCatalog.cs`.

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
