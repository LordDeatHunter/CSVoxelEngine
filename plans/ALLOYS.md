# ALLOYS: tin, bronze and the alloy furnace

> Guide: plans/README.md

## Status

Tracks `TODO.md` `P-44` (which points here): tin as a second, weaker and commoner metal, bronze as a
smeltable alloy (3 copper + 1 tin = 4 bronze) and the 3x3x3 alloy furnace that makes it. Design interview
done. Last updated 2026-11-07. Everything below is a design decision, not a measurement; numbers are
first guesses. `A-1` (tin), `A-2` (per-structure multiblock size: `multiblock.size` 2..4, two bits per offset in
the state byte, kiln bytes unchanged) and `A-3` (the block's `kiln` object, compiled to a `KilnProfile`:
fuel cap, melt speed, can-alloy, read per kiln through the anchor's block) and `A-4` (multi-metal
crucible: one `CrucibleMetal` per molten material, `AlloyRules` for mixing, waiting and the 3:1 resolve,
`recipes/alloy/bronze.json` and `data/molten/bronze.json`, crucible raised to 800 ml so one set fits,
protocol 16) and `A-5` (the alloy furnace: `alloy_bricks`, the 3x3x3 `alloy_furnace` multiblock, a
`KilnLayout` per structure size for the render spots, one converter for every structure) shipped. Work
the items in ID order, one phase per prompt; `A-6` is next.

Decisions already made:

- **Metals.** Copper (existing): entity damage 5, tool power 2.0. Tin: entity 4, power 1.5, more common
  and weaker than copper. Bronze: entity 6, power 2.5. Each of tin, copper and bronze gets axe, pickaxe and
  sword, plus the heads. Copper gains a sword too. All swords use `attack: "sword"` (the iron sword's
  chain) with a per-metal `trail.color`. Bronze has no blocks and no armor.
- **ml.** Mirrors copper: an ingot and a head are 200 ml, the kiln melts 100 ml per 2 s, a cast melts
  back into exactly what it took. 3:1 in ml: 600 copper + 200 tin = 800 bronze = 4 ingots.
- **Plain kiln** melts one metal at a time and refuses a different one in the crucible. Only the alloy
  furnace takes two metals.
- **Alloying** (furnace only): metals may be charged in any order; a full 3:1 set becomes bronze at the
  melt rate, surplus stays pending, nothing is wasted.
- **Alloy furnace**: a 3x3x3 multiblock, hollow shell of a new `alloy_bricks` block, converted by
  right-click like the kiln. Same recipes and interactions as the plain kiln, **fuel cap 8** (plain: 4)
  and **melt speed x1.5** (data driven). 26 shell bricks: the center cell may be air or a brick at
  conversion and becomes a part cell; a brick there is handed back as a drop and a revert leaves the
  center empty, so nothing is made or lost. `alloy_bricks` = 1 mud brick block + 1 copper ingot + 1 tin
  ingot (26 + 26 ingots per furnace, a tuning knob). The one `multiblock_converter` presses any
  structure: it names none and tries every anchor with that anchor's own source block.
- **Compat.** Old saves and crucibles may break, no migration or warning; the net protocol is bumped.

## In progress

(none)

## Open

### Alloying

- [ ] **`A-6` Bronze** - P1, S-M.
  Bronze ingot, heads and tools, melt and cast recipes (the ingot, axe,
  pickaxe and sword molds), tool tags. Needs `A-4`.

## Reference

- Copper today: pickaxe `blockDamage` 2.0, entity 5, `tool.power` 2.0, hit 250 ms; hematite needs
  pickaxe power 2, so copper and bronze both mine it. The iron sword is entity 5.5 (`attack: "sword"`,
  trail `[200, 230, 255]`); the copper sword must sit below it in the ladder.
- Kilns today: the plain kiln's `kiln` object `{fuelCap 4, meltSpeed 1, canAlloy false}`, the alloy
  furnace's `{fuelCap 8, meltSpeed 1.5, canAlloy true}` (`KilnProfile`, with the `KilnLayout` of its size; base melt
  interval `KilnProfile.BaseMeltSeconds` 2 s), `KilnManager.MeltChunkMl` 100; a crucible holds 800 ml as one
  `CrucibleMetal(MaterialId, MoltenMl, PendingMl)` per metal (`CrucibleComponent`). In an alloy kiln a part waits
  while another part of its alloy is in the vessel; each lit tick turns full sets (molten or pending) into pending
  bronze, which melts at the melt rate (`AlloyRules`). A mold pours the molten metal it casts with the most ml.
- Verify: `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release`; `visual-examples/alloy-furnace.json`
  (`VOXEL_TIME=147`) builds, converts, loads, lights and alloys a furnace; a scenario for a bronze cast is
  still to come with `A-6`.
