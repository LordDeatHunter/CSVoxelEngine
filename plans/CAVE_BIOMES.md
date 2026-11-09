# Cave biomes
> Guide: plans/README.md

## Status
Done. Tracked 3D cave biomes: caves that stop being biome-blind, plus the barrier shift that makes room for them. Third of
three efforts, in order: accessories (done), the battle tower (`BATTLE_TOWER.md`, done), cave biomes. Every phase landed: the
barrier shift (CB-0: barrier -300..-400, hell top -401), the cave biome field and palettes (CB-1), the cave shape and ores
(CB-2), the atmosphere and its transport (CB-3, protocol 28), the cave mobs and biome loot (CB-4, protocol 29), and the docs
and the cumulative cost against CB-0 (CB-5, below). The subject summary is `BIOMEGEN.md` §12; the mobs and chests are in
AGENTS.md ("Cave biomes", "Cave mobs"). The as-built notes and costs of each phase stay below as reference. Last updated
2026-11-09 (CB-5).

## In progress
Nothing.

## Open
Nothing.

## Reference
- Decisions (2026-11-08): hybrid model, a surface echo biome that drifts into 3D-noise cave biomes with depth; per-biome palette,
  shape, ores, loot, mobs and atmosphere; new blocks only where needed (11 content ids left as of CB-1, 8 after CB-2; reuse
  first); pixel-art
  palettes per tile; every mob a Blockbench `.bbmodel`.
- Findings: `ChunkTerrainFill` runs fill, `NoiseCaves`, `WormCarver`, ores, water, lava, then geodes, glow mushrooms and pools; stone
  is chosen only in `ColumnCache.GenerateColumn` and ore `IsHost` tests `block == RockBlock`. `NoiseCaves` already has a per-column
  intensity and a stride-4 lattice, 8 channels before CB-2 and 9 since (the ninth the cave sub-biomes' shape scale). `WormCarver` bakes per 64-block cell, so biome enters through the start position or a
  node-center multiplier. The client receives only `ColumnTerrain` (heights, water levels, warmth, wetness).
- Hell's 2D-lattice-per-layer trick does not fit a continuous depth drift, hence the true 3D lattice.
- **CB-1 as built** (`WorldGen/Caves/`: `CaveBiome`, `CaveCellRole`, `CaveBiomePalette`, `CaveBiomes`; knobs `CaveBiome*`,
  `CaveEcho*`, `CaveDeep*`, `CaveSubBiome*`, `Cave*AccentRate` in `WorldGenerationConfig`):
  - Field: three 3D noises, echo drift (seed offset 179, 1/160), deep drift (180, 1/120) and the sub-biome selector (181, 1/96, 2
    octaves), sampled on a world-aligned stride-8 lattice and blended with `OreNoiseLattice.Trilinear` at the cell center. One rule,
    `CaveBiomes.Classify(y, depth, surface biome, drift, deepDrift, selector)`, each noise clamped to -1..1; `At(x, y, z, surface
    biome, surface height)` takes the column's stored, dithered `ColumnData.Biomes[i]` and ravine-cut `Heights[i]`, so the point
    query equals the stamp bit for bit across every seam (`CaveBiomeTests`).
  - Bands: nothing at or below `BarrierTopY` (-300), so the barrier and hell are untouched. Echo above `-200 + 24 * drift`, under a
    cover of `8 + 6 * (drift + 1) / 2` blocks below the column top: a frozen column echoes as FrozenEcho, a desert one (oases included,
    stored Desert) as DesertEcho, every other column stays plain. Under the echo: MagmaDeep at or below `-264 + 16 * deepDrift`, above
    it Crystal where the selector is over 0.12, Fungal under -0.12, plain between. `LayerBandTests` holds the order (barrier < deep
    line < echo line < sea level at their extreme drifts).
  - Palettes (all reused blocks, 0 new content ids): FrozenEcho frost_stone with permafrost floors; DesertEcho sandstone; Crystal stone
    with crystal_block accents (1 face cell in 10); Fungal stone with fungal_moss floors, fungal_soil ceilings and fungal_glow accents
    (1 in 60); MagmaDeep scorched_hellrock with magma_block accents (1 in 24). The glowing accents keep their own `BlockLightPalette`
    slots, so no light slot was used. Excluded: ice (translucent, would show through), sand and gravel (`falls`: a collapse on every
    dig), basalt (the iron-gated barrier block, so a cave would gate like the barrier). Every palette block is solid, opaque, occluding
    and mined by a pickaxe the barrier refuses (`CaveBiomeTests.PaletteBlocksAreSafeHosts`).
  - Volume swap plus skin accents, not a skin-only rewrite: a chunk cannot see its neighbor's carved air, so a skin-only pass would
    leave plain-stone stripes on every cave face lying on a chunk plane. So every stone cell of a non-plain biome becomes its base
    stone (a no-op for crystal and fungal), and only the floor, ceiling and accent come from in-chunk openness (`CaveBiomes.RoleAt`:
    a non-occluding cell above is a floor, below a ceiling, beside a wall). At a seam only an accent or a floor block can be missed.
  - Pass position: `Stage.CaveBiomes` in `ChunkTerrainFill`, after `UndergroundPools` and before `RuinsUnderground`, gated by
    `CaveBiomes.WriteBand` (`[BarrierTopY + 1, max(-176, MaxHeight - 8 in a frozen or desert chunk column)]`) and a `StampGateTests`
    row. It rewrites only cells that are exactly `stone`, so every ore, geode, pocket, pool, lava cell, plant and support-needing block
    is left as it was and ore counts are unchanged; the underground ruins and waystone crypts still overwrite it afterwards.
  - Stamp cost trick: each lattice cell's eight corners give each noise a range, and a `CellRule` built from the range ends decides
    every cell whose ranges do not straddle a line (exact, since every line is a monotone float expression of its noise); only cells
    near a band line or a selector threshold pay their own blend. That halved the deep-band cost.
  - Known gaps: hard vertical edges in rock where the stored column biome flips between frozen or desert and anything else (a chunk has
    no climate gradient per cell); frozen and desert cliffs and ravine walls deeper than the cover wear the echo stone, while the far
    LOD still draws stone there; accents and floors are missed only on a chunk seam; the client derives nothing from the cave biome itself
    and takes the eye's biome from its `PlayerStatus` (CB-3).
  - Scenario: `visual-examples/cave-biomes.json` (`VOXEL_TIME=147`), at the spots `CaveBiomeSpotTests` prints under the default seed.
- **CB-1 cost** (2026-11-09, dev machine, `WorldgenCostProbe.ChunkFillCostPerStage`, median of 3 runs each, mean ms per chunk; cold
  is dominated by the column cache and unchanged within noise). Ceiling: at most +25% on underground chunks.

  | class | warm before | warm after | CaveBiomes stage (warm) | cold before | cold after |
  |---|---|---|---|---|---|
  | deepStone | 0.672 | 0.655 | 0.015 (+2%) | 159.5 | 162.3 |
  | caveHeavy | 0.543 | 0.565 | 0.020 (+4%) | 111.9 | 114.6 |
  | echoCave | 0.591 | 0.671 | 0.072 (+12%) | 80.7 | 82.7 |
  | deepBand | 0.576 | 0.701 | 0.115 (+20%) | 141.9 | 141.1 |

  The stage share against the before mean is the steadier figure (the whole-chunk warm means swing by 5-10% between runs). The first
  build, which blended every stone cell, cost 0.18 ms on the deep band (+31%); the per-lattice-cell rule brought it under the ceiling.
  A microbenchmark of the stamp alone, run past JIT tiering, gave 0.05-0.16 ms per deep-band chunk, so the probe's few calls overstate it.
- **CB-2 as built** (`WorldGen/Caves/`: `CaveBiomeMap`, `CaveShape`, `CaveRichness`, `CaveOres`; knobs `CaveShapeScale*`,
  `Cave*CheeseBoost`, `CaveMagmaCavernBoost`, `CaveFungalTunnelBoost`, `Cave*WormRadiusFactor`, the `*Shift` richness pairs
  beside each ore's threshold and `CaveOresEnabled`/`CaveAmethyst*`/`CaveJade*`/`CaveCinnabar*` in `WorldGenerationConfig`):
  - One map, classified once: `CaveBiomes.Fill` writes every cell's cave biome into the generation worker's reused
    `CaveBiomeMap` (a byte per cell plus a present-biome mask) right after the column fill (`Stage.CaveBiomeMap`), gated like the
    old palette pass, with an exact fast path to plain at or below the barrier and above the sub band where the column has no
    echo or the cell is under the cover. Every consumer reads it (the noise cave shape, the ore richness, the exclusive ores and
    the palette pass, which now stamps from the map); a chunk whose cells are all plain passes null, so its consumers run their
    old code. `CaveBiomeTests.TheMapMatchesThePointQueryAcrossEverySeam`.
  - Shape: only crystal, fungal and magma-touched cells change the carve. Crystal lowers the cheese threshold by 0.10 (bigger
    rooms), fungal widens both spaghetti radii by 45% (wider tunnels), the magma deep lowers cheese by 0.05 and the cavern
    threshold by 0.10 (more open). Each boost is scaled per region by `1 + 0.5 * s`, s being NoiseCaves' ninth lattice channel (a
    dedicated noise, seed offset 182, 1/192, sampled only in a chunk whose map holds a sub-biome), so cave size varies across a
    biome. A plain or echo cell keeps a boost of 0f and a scale of 1f, which are exact, so it carves bit for bit as before
    (`CaveShapeTests`).
  - Worms: a node's radius is multiplied at creation by `CaveShape.WormRadiusFactor` of `CaveBiomes.At` at its center (fungal
    1.35, magma 1.15), baked into `CaveNode.Radius` so `MakeWorm`'s box holds it; a pure function of the position, drawing
    nothing from the worm's stream, and exactly 1 above the sub band without reading a noise. Worms start at most 60 blocks deep
    and wander up to 112, so few reach the sub band and the factor rarely shows today. The reach relation over config is tested.
  - Richness: `IOreLatticeRule` gained a per-cell `ThresholdShift` and its lower bound `MinThresholdShift`. `OreNoiseLattice`
    culls a lattice cell against `threshold + MinThresholdShift` and tests each host cell with `max > t` and `blend > t` for its
    own `t = threshold + shift`, the very predicate `Exceeds` applies to that sum, so the stamp and the point query stay equal
    bit for bit, and a shift of 0 is the old field exactly (`CaveOreRichnessTests`). Pairs (negative is richer): malachite crystal
    -0.03; coal fungal -0.03, desert echo -0.02, magma deep +0.04 (burned out); hematite magma deep -0.03; tin frozen and desert
    echo -0.02 each. Every other pair is 0.
  - Exclusive ores (`CaveOres`, one `BiomeVeinRule` struct instanced per ore on `OreNoiseLattice`): `amethyst_ore` in crystal
    (seed 183, 1/16, threshold 0.80, highest y -192, copper pickaxe, hardness 12), `jade_ore` in fungal (184, 1/18, 0.80, -192,
    copper, 12) and `cinnabar_ore` in the magma deep (185, 1/14, 0.82, -256, iron, 16). Each hosts only plain stone whose map
    cell is its biome, above the barrier and at or below its highest y; `HasOre(x, y, z, biome)` equals the stamp given
    `CaveBiomes.At`'s biome. They run in `Stage.CaveOres`, right after `Tin` and before the palette pass, which rewrites only
    exact stone, so the ore survives in the magma deep's scorched hellrock; one merged host scan marks all three ores' lattice
    cells (`OreNoiseLattice.StampHostCells`). No loot table, so each drops itself; a cheap reflective `material` and no normal
    map. Tiles are palette swaps of `malachite_ore.png`'s six green shades (`tools/generate-cave-ore-textures.py`, in
    `USER_TODO.md`). The echoes have no exclusive ore. `CaveOreTests`, `MiningTests.TheCaveOresGateByTheirDepth`,
    `LayerBandTests.EveryExclusiveCaveOreGrowsInsideItsBiomesReach`.
  - Content ids: 3 blocks, 0 items (each block shares its id with its `BlockItem`): 247 of 255 used, 8 left, recounted from the
    assets.
  - `StampGateSample`'s deep stack now reaches up to one chunk past the sub band's highest reach (it stopped one chunk past the
    barrier), so the gated-versus-ungated comparison meets real sub-biome cells; the `CaveOres` row has `MustWrite` set, since
    the sample writes it.
  - Known gaps: biome borders step the cave size and the ore richness hard, like the palette (the biome is decided per cell); the
    exclusive ores have no use yet; existing saves regenerate their unmodified deep chunks with the new shape and ores, so seams
    show at edited chunk edges in the sub band.
- **CB-2 cost** (2026-11-09, dev machine, `WorldgenCostProbe.ChunkFillCostPerStage`, three runs each, interleaved against a
  build of the CB-1 commit in a git worktree, median of the per-run means in ms per chunk). Ceiling: at most +30% warm on
  underground chunks. The machine ran about 40% slower than for the CB-1 table, which is why the before figures differ from it.

  | class | warm before | warm after | delta | cold before | cold after |
  |---|---|---|---|---|---|
  | deepStone | 0.956 | 0.920 | -4% | 195.9 | 193.3 |
  | caveHeavy | 0.816 | 0.855 | +5% | 140.3 | 151.4 |
  | echoCave | 0.868 | 0.840 | -3% | 108.6 | 101.3 |
  | deepBand | 0.957 | 1.132 | +18% | 176.8 | 165.6 |

  On the deep band the cave stages went from 0.653 ms (NoiseCaves 0.290, the four ores 0.202, CaveBiomes 0.161) to 0.824 ms
  (CaveBiomeMap 0.143, NoiseCaves 0.325, the ores 0.205, CaveOres 0.092, CaveBiomes 0.059), +0.17 ms. `OreNoiseStride` is 2, so
  each exclusive ore samples up to 729 lattice points in a chunk of its biome, which is most of CaveOres' cost; a first build
  with three separate host scans measured CaveOres at 0.108 ms and the deep band at +33% in one interleaved set, and merging the
  scans brought it under the ceiling. The whole-chunk means swing by about 10% between sets, so the stage sums are the steadier
  figure.
- **CB-3 as built** (`Effects/Sky/CaveAtmosphere`, `CaveClimate`; `WorldGen/Caves/EyeCaveBiomeProbe`; knobs `CaveFog*`,
  `CaveAmbient*` in `SkyConfig` and `CaveMote*` in `ParticleConfig`, all first guesses):
  - Look: `CaveClimate.Target` gives each biome a strength plus full-strength fog color, fog near/far and ambient tint; every effect
    lerps from neutral by the strength, so plain caves are exactly neutral. `FrameLighting.SetLighting` eases it (`CaveFogEaseRate`,
    slower than the layer fog; a fade-out keeps the hue, a fade-in from nothing snaps to the target hue) and applies it gated by
    `1 - _hellT`: the sRGB palette shift after `LayerClimate.Apply`, because the dry fog reads `SkySample` from the palette uniforms,
    not `uFogColor` (so no shader changes); the fog near/far before the hell and water lerps; the ambient factor before the wet mix.
    `Classify` already gives plain above the surface, inside the echo cover, in hell and in the sky, so no extra cave test exists.
  - Transport: the byte is computed only on an authoritative world, because `ColumnTerrain` carries no surface biome and a client
    has no seed. `EyeCaveBiomeProbe` reads `CaveBiomes.At` over `IWorldSource.GetColumn`, memoized per eye cell and plain at or
    below the barrier without a read; `LocalPlayer.UpdateHealth` (singleplayer, never a client or a server) and
    `ServerPlayer.TickStatus` (the body eye, so both agree; `StatusDirty` only when the byte changes, no hysteresis) run it. It
    rides the end of `PlayerStatus` (protocol 28); the reader maps an undefined value to plain; `ClientMirror.ApplyNetworkCaveBiome`
    mirrors it.
  - Motes: `AmbientEffects.UpdateCaveMotes` puffs fixed `FireParticleKind`s on the campfire particle system (embers in the magma
    deep, sparks in crystal and fungal caves, steam in a frozen echo, smoke dust in a desert echo) at open cells around the eye,
    paced by a debt and capped per frame; singleplayer and a client, never a server. No new content ids, no new GL resource.
  - Known gaps: the particle colors are fixed per kind (no per-biome tint); a client lags one status after crossing a biome line;
    the tuning is a first guess; the CB-5 run of `cave-biomes.json` shows each sub-biome's tint around its mob, untuned.
- **CB-4 as built** (`Simulation/Caves/`: `CaveSpawnSearch`, `CaveBiomeResolver`, `CaveSpawnRules`, `CaveMobManager`;
  `Entities/CaveMobs/`: `CaveMob`, `CaveDifficulty`, `Shardling`, `Sporeling`, `Magmaborn`; knobs `GoblinCave*` and `Cave*` in
  `MobConfig`, all first guesses):
  - Spawn filter: `MobSpawner.TryFindCaveSpawn` takes a `CaveSpawnSearch` record (ring radii, tries, search half height, scan
    depth, max sky light, block-light limit and an allowed-biome mask, `CaveSpawnRules.Bit`) and an injectable
    `CaveBiomeResolver` (null reads `CaveBiomes.At` over the world source's stored column biome and height), and reports the
    biome. The biome is resolved last, only for a candidate that passed the floor, hell, depth and light checks: one
    `CaveBiomes.At` (three noise blends) per light-passing try, never per frame. `EyeCaveBiomeProbe` is not reused (per-eye
    state).
  - Slimes and goblins (`CaveSpawnRules`): cave slimes everywhere but the magma deep (`SlimeMask`); goblins now spawn in caves
    for a target standing underground (`MobSpawner.TargetUnderground`, moved from `SlimeManager`), caves first, then the
    night-gated surface path. A cave goblin skips the night gate (caves are always dark) and is accepted against a roll of
    `GoblinWeight` (1.0 in crystal and fungal, 0.3 in plain and the echoes, 0 in the magma deep, so none there); its pack mates
    are ringed on the leader's floor at eye height with the mask narrowed to the leader's biome.
  - Mobs (one per sub-biome, `CaveSpawnRules.CaveMobKindFor`), `RealmMob`s on a `CaveMob` base run by one `CaveMobManager` on
    `RealmMobManager<CaveMob>`, joined to `MobCaps`: the **shardling** (crystal; a Cinderwitch-style caster that throws
    `crystal_block` shards through `MobMissiles`, damage plus `frostburn`), the **sporeling** (fungal; a melee walker whose `melee`
    impact is a spore burst, `MobTargets.InShockwave`, hurting every non-immune target inside and applying `spore`) and the
    **magmaborn** (the magma deep; a brute whose blow also sets the target `burning`). The ring spawns only around an underground
    target outside hell, with the mask `CaveMobMask`; pack mates keep the leader's biome and tier. Loot uses existing content
    only (the exclusive ores, crystal block, fungal glow, bones, ember shards, coal).
  - Difficulty is a quantized depth tier fixed at spawn (`CaveDifficulty.TierAt`: 0 at or above `CaveEchoBottomY`, one tier per
    `CaveDifficultyBandBlocks` below, capped at `CaveMaxTier`; the multiplier scales pool and damage). It is saved
    (`MobData.Tier`, an old file falls back to the saved position's tier) and sent (the `ItemId` byte), so a restore and a
    mirror keep the server's pool; a `spawn` op's non-zero `tier` overrides it. A continuous y-read difficulty was rejected
    because mirrors and restores see a different y.
  - The `spore` status effect (`assets/data/effects/spore.json`, no icon): 3 of the 8 effect slots used; it sorts after
    `burning` and `frostburn`, so their ids are unchanged.
  - Wire: `NetEntityKind` `Shardling` 15, `Sporeling` 16, `Magmaborn` 17, the tier in `ItemId` (clamped on read, a different
    tier replaces the mirror), the attack count in `Ammo`; protocol 29. Saved in `entities.json` by kind name.
  - Chests: `ruin_chest_underground` uses `chest.tiers` in `CaveBiome` ordinal order (`ruin_underground` for plain, then
    `_frozen`, `_desert`, `_crystal`, `_fungal`, `_magma`, each nesting `ruin_underground` and adding a flavor pool and one
    uncommon..rare charm or ring). The tier is the chest cell's own cave biome, stamped into state bits 2-7 by
    `StructureStamper.Stamp(..., chestBiomes:)` from the `CaveBiomeMap` `ChunkTerrainFill` already built
    (`RuinPlacement.StampUnderground(..., caves)`), so `ChestLoot` and `ChestManager` are unchanged, opening and breaking
    unopened agree, and a client or an imported world source never reads worldgen. Resolving the biome at open time was rejected
    (`NotifyBlockRemoved` has no column access, clients must not touch worldgen).
  - Content ids: 0 new (247 of 255 used); entities use none.
  - Measured (default seed, generated and lit chunks around the `cave-biomes.json` spots, 200 spawn turns each): at the shared
    block-light limit of 8 the fungal spot still lands a fungal floor on 33 turns (0.17 a turn; 131 turns land on some mob biome
    with the real mask), the crystal and magma spots 164 and 169 turns; raising the limit to 15 only lifts fungal to 63. So
    `CaveMobSpawnBlockLightLimit` stays at `MobSpawnBlockLightLimit` (fungal glow does not starve the sporeling).
  - Scenario: `visual-examples/cave-mobs.json` (`VOXEL_TIME=147`) spawns each mob at its biome's spot and films idle and attack.
    CB-5 moved its crystal and magma spots to the ones `CaveBiomeSpotTests` prints since CB-2's shape change (and in
    `cave-biomes.json`), and the sporeling out of the fungal spot's wall onto open moss; its shoulder-camera attack shots
    are filled by the player's own head in these tight caves.
  - Known gaps: the shards, like goblin stones and hell bolts, are not on the wire (a client sees only their damage); cave mobs
    are not drawn in the water mirror; chests in chunks saved before CB-4 keep tier 0 (plain); the numbers are first guesses.
- **Cumulative cost (CB-5)** (2026-11-09, dev machine, `WorldgenCostProbe.ChunkFillCostPerStage`, interleaved against a build
  of the CB-0 commit 69fd8666 in a git worktree with HEAD's probe copied in, which compiled there unchanged; median of the
  per-run means in ms per chunk). Flag: more than +50% warm on underground chunks. First the default probe, six interleaved
  pairs against the CB-4 commit f8ea475d:

  | class | warm CB-0 | warm CB-4 | delta | cold CB-0 | cold CB-4 |
  |---|---|---|---|---|---|
  | deepStone | 0.760 | 0.919 | +21% | 171.3 | 171.8 |
  | caveHeavy | 0.684 | 0.667 | -3% | 117.8 | 120.4 |
  | echoCave | 0.599 | 0.735 | +23% | 85.7 | 88.0 |
  | deepBand | 0.650 | 1.002 | +54% | 144.9 | 147.7 |

  The cave stage sums say the same: deep band 0.396 ms at CB-0 (NoiseCaves 0.225, WormCaves 0.002, the four ores 0.171) against
  0.738 at CB-4 (CaveBiomeMap 0.124, NoiseCaves 0.297, the ores 0.182, CaveOres 0.085, CaveBiomes 0.052), +53% of the CB-0 mean;
  echo cave 0.394 against 0.525, +22%. deepStone's +21% whole-chunk figure is not cave work (its cave stages moved +2%). So the
  deep band crossed the flag on the default probe. But the probe calls each cave stage only a few dozen times a process, so much
  of that is unoptimized tier-0 code: `CaveBiomeMap` read 0.125 ms there, and 0.105 ms with tiered compilation off
  (`DOTNET_TieredCompilation=0`, the optimized code a long-running game settles on). Measured that way, six interleaved triples
  of CB-0, CB-4 and the fix below:

  | class | warm CB-0 | warm CB-4 | delta | warm CB-5 | delta | cold CB-0 | cold CB-5 |
  |---|---|---|---|---|---|---|---|
  | deepStone | 0.711 | 0.749 | +5% | 0.727 | +2% | 204.3 | 202.3 |
  | caveHeavy | 0.631 | 0.666 | +6% | 0.658 | +4% | 146.5 | 145.0 |
  | echoCave | 0.759 | 0.790 | +4% | 0.796 | +5% | 107.9 | 103.5 |
  | deepBand | 0.662 | 0.934 | +41% | 0.841 | +27% | 179.5 | 178.0 |

  The fix: `CaveBiomes.Fill` now classifies a lattice cell lying wholly in the sub band (above the barrier, under its lowest echo
  line) a row at a time (`FillSubBandCell`): a row at or below the lowest deep line is filled as magma deep, a row above the
  highest one with the cell's decided sub-biome, or from each cell's selector blend alone, and only a row between the two deep
  lines pays the full classification. Bit-exact (`CaveBiomeTests.TheMapMatchesThePointQueryAcrossEverySeam`, now with more
  sub-band samples). On the deep band `CaveBiomeMap` went from 0.105 to 0.040 ms with optimized code and the stage sum from
  +41% to +27% of the CB-0 mean (0.397, 0.665, 0.576 ms); the default probe does not see it (`CaveBiomeMap` stayed at 0.126 ms
  there, six more pairs), which is the tier-0 inflation again. Verdict: under the flag in optimized code; over it on the
  default probe only through tier-0 code. The largest remaining cave cost is NoiseCaves' shape channel and boosts (+0.04 ms
  optimized) and `CaveOres` (0.062 ms), both sampled noise. Machine speed drifts between sessions, so only interleaved runs
  compare, and the default and tiering-off figures are separate sets. Not probed: the cave-mob spawn search runs once per
  spawn interval and resolves the cave biome last, only for a floor that passed every other check; the CB-3 atmosphere is one
  eased lookup per frame.
