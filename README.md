# FiggleFX

FiggleFX is a tool forked from [HydraX](https://github.com/Scobalula/HydraX) by
[Scobalula](https://github.com/Scobalula) that decompiles and exports effects
from Call of Duty: Black Ops 3 and 4.

It rips **effects** and **lens flares** out of the running game and writes them
back out as editable source files for the BO3 mod tools.

| Game             | Pool        | Output                                                     |
| ---------------- | ----------- | ---------------------------------------------------------- |
| Black Ops 3 (T7) | `fx`, `klf` | `.efx` (`iwfx 3`), `.klf`                                  |
| Black Ops 4 (T8) | `fx`, `klf` | `.efx` ported to BO3, optional BO4-native `.bo4fx`, `.klf` |

Two export toggles,

| Toggle         | Effect                                                                              |
| -------------- | ----------------------------------------------------------------------------------- |
| **Asset list** | also write `<name>_assets.txt`, the materials/models/effects each effect references |
| **BO4 .bo4fx** | also write the BO4-native rip alongside the ported `.efx`                           |

## Requirements

- Windows x64, **.NET Framework 4.8**
- Black Ops 3: Steam or Windows Store. Black Ops 4: the SP executable
  (`blackops4.exe`), retail or the Project BO4 / Shield client
- Run **as administrator**, since reading another process's memory needs a
  full-access handle
- Load the map you want first: assets only exist in memory for **loaded
  zones**, so the frontend gives you frontend effects

## Installing what you rip

Exports land flat in `exported_files\<game>\`, but have to go back in at the
right path:

| File   | Goes in                                          |
| ------ | ------------------------------------------------ |
| `.efx` | `%TA_TOOLS_PATH%share\raw\fx\<effect path>.efx`  |
| `.klf` | `%TA_TOOLS_PATH%share\raw\lensflares\<name>.klf` |

An effect's asset name is its path while the export is named after the leaf, so
`blood/fx_blood_decal_impact_ground` belongs at
`share\raw\fx\blood\fx_blood_decal_impact_ground.efx`.

**Every material, image and model an effect or flare references must exist in
the mod tools first**, or Radiant fails quietly: emitters draw nothing, a
missing flare texture draws a white square. Rip that art separately (Greyhound
or Saluki) and set it up in APE. `<name>_assets.txt` is the checklist.

## Known limitations

- **Both**: emitter names aren't stored in compiled data, so emitters are
  labelled by index. `atlasFps > 255` and partial atlas grids are destroyed by
  the game's own compiler and cannot be recovered.
- **BO3**: `dynamicLight2` light graphs are exact, but the Radiant lightdef
  key/value block doesn't survive compilation, so a default OMNI block is
  written in its place. Trail meshes (`trailDef` verts/inds) are not emitted.
  iwfx 2 beams (element types >= 15) are unhandled.
- **BO4**: element types 15 and 16 are new in T8 and have no T7 counterpart, so
  those emitters are skipped in the `.efx` (they are still in the `.bo4fx`).
  Trail parameters and the inherit/attractor sample arrays aren't decoded, so
  they port at their defaults. `computeVisuals`, a `|dup` twin of the primary
  material, is dropped, which is lossless.
- **Lens flares**: positions/colour-depth fade distances, max angle and rotation
  seeds live in runtime buffer objects, not in the flare def, and are written at
  their defaults (~85% of keys are exact). The checksum is written as 0, which
  Radiant accepts.

## License and credits

GPL-3.0, inherited from HydraX. See [`LICENSE`](LICENSE).

- [**HydraX**](https://github.com/Scobalula/HydraX) by
  [Scobalula](https://github.com/Scobalula): the base tool, attach/pool/export
  scaffolding
- [**Greyhound**](https://github.com/Scobalula/Greyhound) by
  [Scobalula](https://github.com/Scobalula): BO4 pool signatures, the
  `BO3FxEffectDef` header layout
- [**atian-cod-tools**](https://github.com/ate47/atian-cod-tools) and
  [**HashIndex**](https://github.com/ate47/HashIndex) by
  [ate47](https://github.com/ate47): the T8 pool table, `DB_LoadXFile` offsets,
  hash dictionaries
- [**Project BO4 / Shield**](https://github.com/project-bo4/shield-development):
  the client this was developed against
- The T7/T8 layout maps were reversed by diffing compiled memory against the
  ~10k `.efx` sources that ship with the BO3 mod tools
