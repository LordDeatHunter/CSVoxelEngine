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
