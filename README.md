# FiggleFX

FiggleFX is a tool forked from [HydraX](https://github.com/Scobalula/HydraX) by
[Scobalula](https://github.com/Scobalula) that decompiles and exports effects
from Call of Duty: Black Ops 3 and 4.

It rips **effects**, **lens flares** and **beams** out of the running game and
writes them back out as editable source files for the BO3 mod tools.

| Game             | Pool                | Output                                                                                     |
| ---------------- | ------------------- | ------------------------------------------------------------------------------------------ |
| Black Ops 3 (T7) | `fx`, `klf`, `beam` | `.efx` (`iwfx 3`), `.klf`, beam defs as `beam.gdf` `.gdt`                                  |
| Black Ops 4 (T8) | `fx`, `klf`, `beam` | `.efx` ported to BO3, `.klf`, beam defs as `beam.gdf` `.gdt` |

Export options,

| Option         | Effect                                                                              |
| -------------- | ----------------------------------------------------------------------------------- |
| **Keep paths** | write each effect into its folder path under `fx\`, the path it installs to         |
| **Asset list** | also write `<name>_assets.txt`, the materials/models/effects each effect references |
| **Hashes**     | how a BO4 name that isn't in the dictionaries is spelled, Saluki or Greyhound       |

Black Ops 4 names assets by hash, so anything the shipped dictionaries don't
cover is written as a placeholder like `material_4674c74919cd08f7`. Set
**Hashes** to whichever tool you rip the referenced materials, images and
models with, and the names line up on both sides.

Radiant only renders effect materials whose name starts with `gfx`, so
hash-named materials referenced by an effect are written into the `.efx` as
`gfx8_material_<hash>` and the material must be set up in APE under that
name. The asset list keeps the raw name and notes the rename beside it
(`material_<hash> -> gfx8_material_<hash>`). Materials that belong to models
keep their normal names.

## Requirements

- Windows x64, **.NET Framework 4.8**
- Black Ops 3: Steam or Windows Store. Black Ops 4: the SP executable
  (`blackops4.exe`), retail or the Project BO4 / Shield client
- Run **as administrator**, since reading another process's memory needs a
  full-access handle
- Load the map you want first: assets only exist in memory for **loaded
  zones**, so the frontend gives you frontend effects

## Installing what you rip

Exports land in `exported_files\<game>\` (`fx\`, `lensflares\` and `beams\`
subfolders), but have to go back in at the right path:

| File   | Goes in                                          |
| ------ | ------------------------------------------------ |
| `.efx` | `%TA_TOOLS_PATH%share\raw\fx\<effect path>.efx`  |
| `.klf` | `%TA_TOOLS_PATH%share\raw\lensflares\<name>.klf` |
| `.gdt` | `%TA_TOOLS_PATH%source_data\<name>.gdt`          |

An effect's asset name is its path, and exports keep it, so
`blood/fx_blood_decal_impact_ground` is written to
`fx\blood\fx_blood_decal_impact_ground.efx` and belongs at
`share\raw\fx\blood\fx_blood_decal_impact_ground.efx`. Turning off **Keep
paths** exports every effect flat under its leaf name instead.

**Every material, image and model an effect or flare references must exist in
the mod tools first**, or Radiant fails quietly: emitters draw nothing, a
missing flare texture draws a white square. Rip that art separately (Greyhound
or Saluki) and set it up in APE. `<name>_assets.txt` is the checklist.

## Known limitations

- **Both**: emitter names aren't stored in compiled data, so emitters are
  labelled by index. `atlasFps > 255` and partial atlas grids are destroyed by
  the game's own compiler and cannot be recovered.
- **BO3**: `dynamicLight2` emitters are reconstructed including the embedded
  Radiant lightdef (type, colour, radius, falloff, fov and friends, ~99% of
  key values exact); only shadowmapScale, the culling distances and a SPOT
  light's angles stay at template defaults.
- **BO4**: element type 12 is new to BO4 and has no T7 counterpart, so those
  emitters are skipped in the `.efx`. BO4 compiles an embedded `dynamicLight2`
  lightdef down to its baked runtime form: the radius and light colour are
  recovered, the rest of the block is written at OMNI defaults.
  `computeVisuals`, a `|dup` twin of the primary material, is dropped, which
  is lossless.
- **Beams**: a beam def GDT carries the material, effects, size, color, timing,
  curve type, shape, UV mode and waveform values. T8's width/colour curves have
  no BO3 equivalent and are flattened to their base values, and the collision
  settings aren't located in the compiled data yet, so they stay at GDF
  defaults. Beam materials are often hash-named until the dictionaries grow.
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
