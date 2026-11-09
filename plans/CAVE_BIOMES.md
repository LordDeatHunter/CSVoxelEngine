# Cave biomes
> Guide: plans/README.md

## Status
CB-0 (barrier shift) and CB-1 (the cave biome field, its palette pass and the debug scenario) done: barrier -300..-400, core
-330..-370, hell top -401 (a chunk edge, so no chunk straddles hell); the overworld caves wear a frozen or desert echo down to
about -200 and crystal, fungal or magma-touched sub-biomes from there to the barrier. CB-2 (shape and ores) done: the
sub-biomes carve bigger rooms and wider tunnels, the Y-band ores' richness follows the cave biome and each sub-biome has an
exclusive ore (amethyst, jade, cinnabar). CB-3 (atmosphere and transport) done: each cave biome eases in its own fog,
ambient tint and drifting motes, and the server sends the eye's cave biome in `PlayerStatus` (protocol 28). Tracks 3D cave biomes: caves that stop
being biome-blind, plus the barrier shift that makes room for them. Third of three efforts, in order: accessories (done), the
battle tower (`BATTLE_TOWER.md`), cave biomes. One phase per prompt; each leaves the game building and the tests green. Last
updated 2026-11-09 (CB-3).

## In progress
Nothing.

## Open
- [ ] **`CB-4` Mobs and loot** - P2, XL.
  A biome filter at the final `if` of `MobSpawner.TryFindCaveSpawn`, biome-gated slimes and goblins, and **one new mob per biome**
  (a cave mob manager on `MobCaps`, `RealmMob` frame, Blockbench rigs from deterministic generator scripts with `--check`, saves,
  `NetEntityKind`, item debugger modes), plus per-biome chest loot tables.
- [ ] **`CB-5` Document** - P2, S.
  A `BIOMEGEN.md` section on the cave biomes (the seed table already lists 179-181), a final AGENTS.md pass once CB-2..CB-4 land;
  then delete this file's finished entries.

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
    the tuning is a first guess, not yet checked against the scenario shots.
