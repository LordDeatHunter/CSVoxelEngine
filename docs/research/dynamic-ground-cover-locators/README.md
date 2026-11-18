# Dynamic ground-cover location observations

These four byte-for-byte C# test sources were moved from
`OpenGLTest.Tests/WorldGen/Features/` after their fixed coordinates missed their
research targets. They retain the original namespaces, filters and failing
assertions as historical observations. The test project uses its default
compile glob under `OpenGLTest.Tests/`; files here are outside that project.
Moving them changes default test membership, not their observed results or the
generation rules. It does not establish natural snow-free Taiga reachability.

| Archived source / original test class | Original observation commit | What the fixed location showed | Raw evidence |
|---|---|---|---|
| `GroundCoverNaturalPreflightTests.cs` / `VoxelEngine.Tests.WorldGen.Features.GroundCoverNaturalPreflightTests.FixedWindowsReportActualDynamicGroundCoverCandidates` | `90e442d556e3f5ceb11ecf0018322b35e43f024e` | Four fixed windows, 1,024 visits: Jungle and Taiga candidate misses. A location miss, not a generated-writer failure. | Nexus report `d431db3055208`, file `tfeffdee23fe454dd3` |
| `DynamicGroundCoverFineWindowTests.cs` / `VoxelEngine.Tests.WorldGen.Features.DynamicGroundCoverFineWindowTests.FixedJungleAndWarmTaigaWindowsReportNaturalCandidateMetadata` | `81a53b63b13a4bdbe860065c4787400a0abcf701` | The selected Jungle window was snow-covered and the Taiga window lacked the required warm, dry metadata. | Nexus report `df0078b2b0d5e`, file `tfb5425a19d7eacdc9` |
| `DynamicSnowAwarePlantLocatorTests.cs` / `VoxelEngine.Tests.WorldGen.Features.DynamicSnowAwarePlantLocatorTests.FixedJungleCentersAndWarmTaigaBoundaryReportSnowFreeMetadata` | `dfd72e12435461942d6137b36f1378daa8511e12` | Its fixed 231-point search found four snow-free Jungle centers and no qualifying Taiga pure hit. | Nexus report `da0e14fd9a29c`, file `tf630fd612c62cd183` |
| `DynamicTaigaBoundaryTransectTests.cs` / `VoxelEngine.Tests.WorldGen.Features.DynamicTaigaBoundaryTransectTests.FixedTaigaToJungleTransectsReportSnowFreeTaigaMetadata` | `71cb09a3c0cddbfb94bbf86b9883e46b3ff42951` | All 78 rows were visited: two Taiga rows had snow; the other 76 were different flora; no height query was attempted. | Nexus report `d3d817171326e`, file `tf3cfa6398132d73ff` |

Reproduce an observation from its **original commit and test project** with
that row's exact fully qualified name filter and retained raw command metadata.
The archived file is not a passing test in the current project. Finite misses
at these coordinates say nothing universal about other seeds or locations.

Durable default controls remain in the test project, including
`DynamicGroundCoverTests`, `GroundCoverStampTests`,
`GroundCoverGateIdentityTests`, `GroundCoverFrozenFillTests`,
`DynamicJungleSnowFreePreflightTests`, `JungleWriterFrozenFillTests`,
the frozen `WorldGenProfileIdentityTests`, and content/name and lifecycle tests.
The fixed Jungle writer result validates two natural chunk keys; it does not
replace the unresolved natural Taiga evidence or the later visual review.

## Opt-in Jungle tall-fern visibility diagnostic

`JungleTallFernVisibilityDiagnosticTests.cs` is a **new, unrun** camera research
source, not one of the four historical failed locators above. It stays outside
the default test compile glob. It fixes two actual f003 tall-fern pairs and four
recorded air-cell eyes in the existing `(-550,4,650)` Jungle chunk. For each
pose, nine rays cover ground, lower stem, joint edges and upper stem. A tenth
ray requires an all-air center selection corridor for the full 10-block reach.
Every other nonair cell, including cutout foliage, is conservatively blocking;
out-of-chunk or capped rays are unsafe. A zero-viable result is a valid visibility
observation, never a worldgen regression or a visual pass.

The frozen poses are A1 `(-8792.5,71.4,10404.5), yaw125/pitch-15`, A2
`(-8789.5,72.4,10406.5), yaw145/pitch-10`, B1
`(-8789.5,72.4,10405.5), yaw68/pitch-16`, and B2
`(-8790.5,72.4,10403.5), yaw60/pitch-12`. Projection uses 16:9 and the
minimum configured vertical FOV 60° with 10° edge margins. This is a static
framing screen, not a claim that alpha, lighting or selection will look clear
in game.

**Proposed later opt-in procedure only, requiring a separate root grant:** copy
this file by name into `OpenGLTest.Tests/WorldGen/Features/` in the assigned
isolated worktree, record source/project identities, then use the installed SDK
10/net9 Release test project with the exact filter
`FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.JungleTallFernVisibilityDiagnosticTests.FixedTallFernEyesReportConservativeAirCorridors`.
Record discovery, raw output/TRX, actual DLL identities and cumulative counters;
archive the temporary inclusion and verify its removal leaves the candidate
branch unchanged. Do not run it as part of the default suite. No inclusion,
build, test, fill, mesh, World or GL execution occurred in this source phase.

Exact proposed commands, **not yet authorized or executed** (from repository
root; use an owned temporary TRX path and record argv/cwd/env/start/end/exits):

```bash
cp docs/research/dynamic-ground-cover-locators/JungleTallFernVisibilityDiagnosticTests.cs OpenGLTest.Tests/WorldGen/Features/JungleTallFernVisibilityDiagnosticTests.cs
DOTNET_ROLL_FORWARD=LatestMajor dotnet build VoxelEngine.sln -c Release
DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --no-build --no-restore --list-tests --filter 'FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.JungleTallFernVisibilityDiagnosticTests.FixedTallFernEyesReportConservativeAirCorridors'
DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.JungleTallFernVisibilityDiagnosticTests.FixedTallFernEyesReportConservativeAirCorridors' --logger 'trx;LogFileName=JungleTallFernVisibilityDiagnostic.trx'
```

The later executor must archive the temporary source, raw output/TRX and
executed DLLs before removing that temporary test-project copy, then confirm
the original tracked candidate and default test membership are unchanged.

Future budget reservation: at most one normal chunk fill at the already known
key, zero explicit `ColumnCache.Get` beyond that fill, zero planner
probes/Worlds/meshes, 40 fixed rays × 32 visited cells and 10 fixed ID checks
= at most 1,290 explicit block-ID reads. The source emits counts even on an
assertion failure. Generation passes, cache construction (including any
internal neighbor-key lookups), dependent pure queries and writes are
transitive work and are **not** covered by the 1,290-read figure or claimed
to be zero. No independent new column-key query or coordinate search is
requested.

## Opt-in Forest tall-fern joint preflight

`ForestTallFernVisibilityDiagnosticTests.cs` is a **separate, unrun** research
source outside the default test project. The retained aa52 normal/probe row
records the Forest tall fern at `(10,10/11/12,2)` (grass/lower/top) in the
already known seed-1337 Dynamic chunk `(0,0,0)`. The F1 eye
`(13.5,11.4,2.5)` lies in recorded air at `(13,11,2)`; the F2 eye
`(14.5,11.4,2.5)` lies in `(14,11,2)`, whose occupancy was **not** recorded.
Both poses are frozen at yaw 158°, pitch +8°. An actually occupied F2 eye
rejects only F2, retaining F1's result. No new coordinate or adaptive angle
search is involved.

The same nine fixed target points and conservative supercover/all-air center
criteria as the Jungle diagnostic apply. Pure source-derived 16:9/FOV60°
projection arithmetic gives F1 target horizontal angles 17.84–25.33° and
vertical angles -16.59–19.53°, F2 horizontal 18.91–24.56° and vertical
-14.46–13.36°. Each stays within the diagnostic's 10° FOV edge margin;
these calculations do **not** establish a clear sightline. Any foreign
nonair, out-of-chunk or 32-visit cap marks a ray unsafe. Zero viable remains
a camera observation rather than a terrain regression or visual acceptance.

**Only if separately granted:** temporarily copy the committed Forest source
by name into `OpenGLTest.Tests/WorldGen/Features/`, then build the Release
solution and discover/run the single filter
`FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.ForestTallFernVisibilityDiagnosticTests.FixedForestEyesReportConservativeAirCorridors`
with `--no-build --no-restore` on the two test commands. Preserve exact argv,
scoped environment, raw output, TRX, executed DLL copies and source identity
before removing that sole temporary copy; verify default membership and clean
tracked status afterward. This is a future procedure, not an action taken now.

Future reservation is one normal fill of existing chunk `(0,0,0)`, zero
explicit separate column lookup, no planner/Legacy fill, World, mesh, game or
GL, two eyes × (nine target rays + one full 10-block center ray) = at most
20 rays × 32 visits = 640 ray-cell reads plus three target and two eye checks
= at most 645 explicit ID reads. The generated chunk's dependent cache/noise,
feature and voxel passes are transitive **unmeasured** work, not zero.

## Opt-in Taiga warm-edge admission diagnostic

`TaigaWarmEdgeAdmissionDiagnosticTests.cs` is new, unrun research source outside
the default test project. It is a separate child of the clean S4a candidate
`40433894ff9feb4ce0cacdfba0d54f161fce45de`. The earlier retained
seed-1337, 78-point transect (`tf3cfa6398132d73ff`) found Taiga with snow at
`(-7192,8808)` (climate cold `.60435545`), followed by Steppe without snow at
`(-7160,8840)` (cold `.51548576`). That observation motivates exactly the 31
intermediate integer points `(-7192+i,8808+i)`, `i=1..31`; it does not promise
that dither, pack-ice snow, tuft and fern selection overlap there. All points
are logged in order, even after an early pure candidate. Zero candidates is a
valid diagnostic result. Only the first actual Taiga/no-climate-snow/tuft/fern
point may spend one same-region metadata column read. There is no fallback to
a second point if that metadata fails.

The direct point ledger has exactly 31 calls each to `FloraBiomeAt`,
`BiomeWeights`, `SampleClimate`, `ColdOf`, `DryOf`, `WarmOf`, `SemiDryOf`,
`JungleOf`, `EcotoneNoise.GetNoise` and `HasSnow`; at most 31 calls to
`Classify`, and at most 31 conditional calls each to `TryGetGrassTuft` and
`CandidateAt`. Each counter increments **before** its call. The one optional
`ColumnCache.Get` reads one selected column's height, stored biome, dirt
depth, bed and water level (five indexed metadata reads), with at most one
direct call each to `ColdWeightAt`, `DepthAt`, `TerrainErg.Intersects`,
`IsBeachHeight`, `SurfaceClearance.Allows` and two `IsWaterColumn` calls
(height and bed), plus three `FloorDiv` calls. The test reports partial
counters in `finally` even on failure and checks normal completion budgets.

Those direct counts **do not** count the repeated underlying noise and helper
work: `FloraBiomeAt` and the separate `BiomeWeights` each compute continental,
island and climate weights; Dynamic dither resamples climate and ecotone;
`TryGetGrassTuft` rereads core/oasis, flora, grass-patch noise, climate and a
density hash; `CandidateAt` hashes and selects a weighted row; `HasSnow` reads
the pack-ice field only in its intermediate cold band. An uncached
`ColumnCache.Get` computes 256 metadata entries through `TerrainGenerator.Compute`,
including height/water/feature/noise dependencies and corner climate; its
transitive invocation count must be instrumented and recorded before any
later run. `DepthAt` also rereads core, cold/snow and drift noise. The source
performs no chunk fill, mesh, block-ID read, World, game or GL work. Metadata
can only predict likely grass; cave/feature edits, actual ground, air and
final stamp remain unobserved.

**Future procedure, requiring a separate root execution grant:** copy this
file by name into `OpenGLTest.Tests/WorldGen/Features/`, record exact source
and both project identities, build the Release solution with installed SDK 10
and scoped `DOTNET_ROLL_FORWARD=LatestMajor`, discover the exact filter below,
then run only that filter with `--no-build --no-restore` and an owned TRX path:

```text
FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.TaigaWarmEdgeAdmissionDiagnosticTests.FixedWarmEdgeReportsEveryPureGateAndOneConditionalMetadataColumn
```

Before execution, freeze the complete transitive column/helper counter
instrumentation and cumulative budget. Preserve raw argv, cwd, scoped env,
UTC bounds, exits, stdout/stderr, TRX and executed DLL copies/hashes. Archive
the temporary inclusion and confirm its removal restores default membership
and the committed branch. This paragraph authorizes no build, test, query,
column read or cleanup now.

## Opt-in two-dimensional Taiga admission observation

`TaigaRegionalAdmissionDiagnosticTests.cs` is a separate **unrun** research
source outside the default test project. It preserves the complete 31-point
line and its finite miss. At seed 1337 Dynamic, that line's first eight
Taiga points were all snowed; its first snow-free point was Steppe. The
coherent ecotone support fell below Taiga before snow disappeared on that
diagonal, so refining it would repeat the observed correlation.

The fixed grid uses `normal=-12..12` outside, `tangent=-32..32` inside, and
exactly **1,625** points in numeric loop order:

```text
x = -7181 + 2*normal + 12*tangent
z =  8819 + 2*normal - 12*tangent
```

The `2*normal` term follows the observed cold gradient through the narrow
possible raw-cold band near the measured `.5735` center. The `12*tangent`
term crosses it in the independent direction over `±384` in each horizontal
coordinate. This covers multiple ecotone phases (frequency 1/96, seed
offset 204) and pack-ice snow phases (frequency 1/36, two octaves, offset
152), with three or more tangent samples per nominal pack-ice scale.
Temperature and humidity are much broader (Dynamic frequencies 1/9000 and
1/3200) and share a 1/1400 domain warp; some outer points may leave the
cold band. The retained 231-point grid used spacing 64 in a nearby region,
larger than the pack-ice scale, so this fixed orientation and spacing provide
different evidence. The scale comparison does **not** predict a hit or turn a
finite scan into a global proof. There is no adaptive refinement, second
region, seed change, or early stop when a candidate appears.

Each of the 1,625 rows logs base/ocean and actual dithered flora, raw
climate, classifier ramps, actual ecotone and shifted gates, climate
`HasSnow`, conditional tuft/variant and a specific rejection reason. In the
necessary middle cold band it also makes one direct pack-ice noise read at
the actual snow offset `(x+4096,z-4096)` and logs its smoothstep threshold;
`HasSnow` performs its own internal read. The source counts **before** each
direct call, retains every row, prints a partial ledger in `finally`, and
treats zero pure Taiga/no-snow/tuft/fern candidates as a valid observation.

Direct point ceilings: exactly 1,625 calls each to `FloraBiomeAt`,
`BiomeWeights`, `SampleClimate`, `ColdOf`, `DryOf`, `WarmOf`, `SemiDryOf`,
`JungleOf`, `EcotoneNoise.GetNoise` and `HasSnow`; at most 1,625 each to
`Classify`, conditional `PackIceNoise.GetNoise`, `TryGetGrassTuft` and
`CandidateAt`. The direct coordinate-visit ceiling is 1,625, below the
root's 8,192 source ceiling. These are **not** total noise/feature operations:
`FloraBiomeAt` and `BiomeWeights` resample climate/continent/island,
Dynamic dither resamples climate/ecotone, `TryGetGrassTuft` rereads core,
oasis, flora, patch, climate and hash, and `HasSnow` rereads pack ice in the
middle band. Their transitive counts remain unmeasured until a separate
execution grant defines treatment.

After all rows, only the **first** actual Taiga/no-climate-snow/tuft/fern
candidate may make one negative-coordinate-safe `ColumnCache.Get`. If none,
zero columns; if its metadata fails, no fallback. The optional work is five
indexed fields in one selected column, one each `ColdWeightAt`, `DepthAt`,
core, beach and clearance, two `IsWaterColumn` calls (height and bed) and
three `FloorDiv` calls. A cache miss constructs 256 metadata entries through
the existing terrain/feature/noise graph. That indirect work is not measured
by the direct ledger and must be accepted or separately instrumented by root
before execution. The diagnostic does no chunk fill, mesh, block-ID read,
World, game or GL work. Its likely grass value is a source prediction only;
actual ground/air/occupancy/final natural stamp remain unobserved.

**Proposed future inclusion only, requiring a separate root grant:** copy
the file by name into `OpenGLTest.Tests/WorldGen/Features/`, record source
and project identities, Release build and discover exactly the filter below,
then run only that filter with `--no-build --no-restore` and an owned TRX:

```text
FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.TaigaRegionalAdmissionDiagnosticTests.FixedRegionalGridReportsPureGatesAndAtMostOneMetadataColumn
```

Record argv/cwd/scoped environment/UTC bounds/exits/raw output/TRX and
actual executed DLL copies/hashes. Archive the temporary source and raw
evidence before removing only that inclusion; prove the clean branch and
default test membership afterward. This source preparation authorizes no
build, test, discovery, query, column generation or cleanup.
