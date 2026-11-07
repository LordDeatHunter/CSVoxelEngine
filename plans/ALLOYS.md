# ALLOYS: tin, bronze and the alloy furnace

> Guide: plans/README.md

## Status

Tracks `TODO.md` `P-44` (which points here): tin as a second, weaker and commoner metal, bronze as a
smeltable alloy (3 copper + 1 tin = 4 bronze) and the 3x3x3 alloy furnace that makes it. Design interview
done. Last updated 2026-11-07. Everything below is a design decision, not a measurement; numbers are
first guesses. `A-1` (tin) and `A-2` (per-structure multiblock size: `multiblock.size` 2..4, two bits per offset in
the state byte, kiln bytes unchanged) shipped. Work the items in ID order, one phase per prompt; `A-3`
is the remaining risky foundation.

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
  and **melt speed x1.5** (data driven). Assumed, to confirm: 26 shell bricks, the center cell is air or
  a brick at conversion and becomes a part cell; `alloy_bricks` = 1 mud brick + 1 copper ingot + 1 tin
  ingot (26 + 26 ingots per furnace, a tuning knob).
- **Compat.** Old saves and crucibles may break, no migration or warning; the net protocol is bumped.

## In progress

(none)

## Open

### Furnace foundation

- [ ] **`A-3` Data-driven kiln** - P1, M.
  The block's `kiln` flag becomes an object (fuel cap, melt speed factor, can-alloy); `KilnManager`'s
  `MaxFuel`/`MeltSeconds`/`MeltChunkMl` become per-kiln values read through the anchor's block;
  `KilnState`, saves and net stay shared by both sizes.
  **Hook:** `Simulation/KilnManager.cs`, `Content/Blocks/BlockSchemaParser.cs`, `StationNetCodec`.

### Alloying

- [ ] **`A-4` Multi-metal crucible** - P1, M.
  `CrucibleComponent` carries per-metal molten and pending ml (copper, tin, bronze). The kiln loop
  enforces: one metal in a plain kiln, any order in the furnace, 3:1 sets convert to bronze, surplus
  waits. Tooltip, drawn molten/pending, `KilnManager.CanAccept`, saves and the net codecs follow.
  **Hook:** `UI/InventoryPanel/ItemStack.cs`, `KilnManager`, `Items/CrucibleItem.cs`, `KilnRenderer`.
  **Tests:** pure rule tests for the 3:1 resolve, refusal in the plain kiln and conservation.
- [ ] **`A-5` Alloy furnace** - P1, M.
  `alloy_bricks` (cube block + recipe), the furnace anchor and part blocks (3x3x3 model authored in
  0..48 units, opening on north, `multiblock.size: 3`), the converter item entry, loot (the one brick that cell was built
  from), fuel 8 and x1.5 melt through `A-3`. `KilnRenderer`'s spots (`VolumeSize` 2) describe the
  2x2x2 kiln's model only, so the furnace needs its own. Needs `A-3`.
  **Hook:** `data/blocks`, `data/models/block`, `ConvertMultiblockItem`.
- [ ] **`A-6` Bronze** - P1, S-M.
  `data/molten/bronze.json`, bronze ingot, heads and tools, melt and cast recipes (the ingot, axe,
  pickaxe and sword molds), tool tags. Needs `A-4`.

## Reference

- Copper today: pickaxe `blockDamage` 2.0, entity 5, `tool.power` 2.0, hit 250 ms; hematite needs
  pickaxe power 2, so copper and bronze both mine it. The iron sword is entity 5.5 (`attack: "sword"`,
  trail `[200, 230, 255]`); the copper sword must sit below it in the ladder.
- Kiln today: `MaxFuel` 4, `MeltSeconds` 2, `MeltChunkMl` 100, `CrucibleComponent(MaterialId, MoltenMl,
  PendingMl, PendingInputId)`.
- Verify: `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release`; a `visual-examples`
  scenario (`VOXEL_TIME=147`) for the tin ore, the furnace and a bronze cast.
