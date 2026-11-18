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
