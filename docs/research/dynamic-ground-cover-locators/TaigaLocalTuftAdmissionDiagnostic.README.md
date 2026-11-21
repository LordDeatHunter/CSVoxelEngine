# Opt-in local Taiga tuft-gate diagnostic

`TaigaLocalTuftAdmissionDiagnosticTests.cs` is **unrun** source outside the
default test project. Its parent is the clean aligned-source commit
`8cb9e33a029a1c35ef75ab9eab8967cc3ddc9283`. It changes no production
terrain, snow, foliage, content, asset or test-project rule. Preserve the
earlier regional and aligned diagnostics, their results and every archive.

## Fixed source of the neighborhood

The root-verified aligned run logged all 3,225 points and passed its one
Release test. Two actual Dynamic Taiga rows lacked climate snow:

| Coordinate | Actual variant | Actual public tuft | Pack noise / snow threshold |
| --- | --- | --- | --- |
| `(-7265,8833)` | fern | false | `.82542384 / .75309926` |
| `(-7265,8834)` | tall grass | false | `.86802006 / .7230332` |

These are genuine upstream climate/classifier/snow admissions, but neither
is a natural fern. The new source freezes **one** numeric-order neighborhood:
seed 1337 Dynamic, x `-7273..-7257` outer, z `8829..8837` inner, exactly
17×9 = **153 distinct ordered integer points**. Both prior coordinates are
inside this rectangle and count once each. There is no adaptive expansion,
alternate seed, early stop or claim that the rectangle must find a tuft.

## Source ownership and explanatory reads

The actual public `FoliageGenerator.TryGetGrassTuft(int,int)` and actual
`DynamicGroundCover.CandidateAt(int,int,Biome)` remain the admission authority.
The source logs the same real flora/climate/classifier/snow/pack decisions
as the aligned driver for **every** row, with explicit rejection reasons.
Only actual Taiga with `HasSnow=false` gets the public tuft/variant calls and
separate explanatory reads:

- `TerrainErg.Intersects(x,z,x,z)` (internal), `OasisGenerator.IsDisc(x,z)`
  (public), `TerrainGenerator.Climate(x,z,out _,out wetness)` (public), and
  `FoliageGenerator.GrassDensityAt(wetness)` (public).
- One local `FastNoiseLite` is built **after** `WorldSeed.Apply` using the
  existing internal `TerrainGenerator.MakeNoise(63,
  WorldFeatureConfig.GrassPatchFrequency)`. `FoliageGenerator.Reseed` uses
  that same factory call for its private grass patch field. `MakeNoise`
  forwards to `TerrainNoise.MakeNoise(int,float,int octaves=1)`, which seeds
  `WorldGenerationConfig.Seed + 63`, sets OpenSimplex2 and the supplied
  frequency, and enables FBm only when octaves > 1. The local object is
  sampled once per qualifying row and labelled **same-factory patch**. It is
  not reflection into or mutation of the private field.
- The private density hash is not cloned. When real core/oasis/flora and
  same-factory patch gates leave density as the only public false path,
  `density-inferred` is a source inference from the actual result, not a
  directly observed hash. A disagreement is retained in raw fields rather
  than repaired or asserted away. The logged density is a threshold, not a
  synthetic roll.

The class namespace `VoxelEngine.Tests.WorldGen.Features` matches temporary
test inclusion. It uses the existing project global usings for `VoxelEngine.Core`
(`FastNoiseLite`, `MathUtils`), `VoxelEngine.Config`, `VoxelEngine.Content` and
`VoxelEngine.Tests.Support`; explicit imports cover `VoxelEngine.WorldGen`,
`VoxelEngine.WorldGen.Features`, xUnit and its output helper. The owning
source signatures are in `OpenGLTest/src/WorldGen/Features/FoliageGenerator.cs`,
`TerrainErg.cs`, `Features/OasisGenerator.cs`, `TerrainGenerator.cs` and
`TerrainNoise.cs`. This is a source read; no initialization or noise sample
has run for this candidate.

## Frozen direct and transitive work ledger

Counters increment before each direct call and a partial ledger prints in
`finally`, including on failure. Every row is logged even after a candidate.

| Direct work | Maximum |
| --- | ---: |
| Ordered points and emitted rows | 153 each |
| `FloraBiomeAt`, `BiomeWeights`, `SampleClimate`, `ColdOf`, `DryOf`, `WarmOf`, `SemiDryOf`, `JungleOf`, `EcotoneNoise.GetNoise`, `HasSnow` | 153 each, exactly on normal completion |
| Conditional `Classify`, offset `PackIceNoise.GetNoise`, public `TryGetGrassTuft`, `CandidateAt` | 153 each |
| Local same-factory noise construction | one after seed apply |
| Conditional explanatory core, oasis, same-factory patch sample, `Climate`, `GrassDensityAt` | 153 each |
| Optional `ColumnCache.Get` | one after all rows, first actual Taiga/no-snow/tuft/fern only |
| Optional indexed column fields | five at that point |
| Optional altitude-cold, snow depth, beach, clearance helpers | one each |
| Optional water queries | two (height and bed) |
| Optional `FloorDiv` | three (x, z, vertical band) |
| Chunk fills, block reads, meshes, World, game, GL | zero |

The optional metadata core flag reuses that point's explanatory read, so it
does not add another direct core call. `FloraBiomeAt`, `BiomeWeights`,
`Climate`, `HasSnow`, tuft, oasis and core methods perform their own nested
climate/noise/site reads. The local object adds its own sampler work. A
`ColumnCache.Get` cache miss can construct 256 metadata entries with dependent
terrain/water/feature work. These **transitive calls are unmeasured**, so the
direct 153-point limit is not a total engine-work bound. Metadata does not
prove real ground/air/occupancy or normal/planner-probe final stamping. Zero
pure candidates is a valid bounded miss.

## Future opt-in procedure only

Root must review this exact committed source and grant a bounded run before
any build, discovery, query, noise construction or cache access. After a
grant, temporarily include only this file by name under
`OpenGLTest.Tests/WorldGen/Features/`, verify byte identity, build Release
with installed SDK 10 and scoped `DOTNET_ROLL_FORWARD=LatestMajor`, discover
one exact test, and run only:

```text
FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.TaigaLocalTuftAdmissionDiagnosticTests.FixedLocalGridReportsPublicTuftAndSameFactoryGates
```

Preserve source and both project identities, exact temporary inclusion,
argv/cwd/scoped environment/UTC/exits/raw stdout/stderr/TRX/all 153 rows,
executed DLL copies/hashes and a pre-removal indexed archive/readback before
removing only the temporary copy. Seal/read back post-removal absence, clean
status and unchanged head/tree separately. This is a **proposal**, not a
runtime grant. Even positive metadata would need another reviewed normal
and planner-probe natural-stamp phase; no automatic fill follows.
