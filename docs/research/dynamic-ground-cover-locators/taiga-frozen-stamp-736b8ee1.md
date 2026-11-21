# Frozen Taiga fern real-fill source preflight

Status: unrun source candidate. This follows the accepted public admission observation at `30f55f54459ab267068a77e68df4569f2490d9e9`; it does not report a generated fern yet. Earlier locator sources, logs and archives remain intact.

## Frozen source and sites

`OpenGLTest.Tests/WorldGen/Features/TaigaFernFrozenFillTests.cs` is an ordinary compiled GL-free test using the existing `Worldgen seed` nonparallel collection. Seed 1337 with `WorldGenProfile.Dynamic` is applied only while no live world exists and reset to `WorldSeed.Default` in `finally`. Content is loaded through `TestContent.EnsureLoaded()`.

The two sites are the first two verified public Taiga/no-snow/tuft/fern admissions with suitable column metadata, and are the entire fixture:

| World x,z | Chunk key cx,cy,cz | Local x,y,z | Frozen height and support |
| --- | --- | --- | --- |
| -15894,6392 | -994,4,399 | 10,3,8 | height 66, grass support at world y66; fern target y67 |
| -15888,6390 | -993,4,399 | 0,3,6 | height 66, grass support at world y66; fern target y67 |

The horizontal keys use floor division for negative x. The target y67 is local 3 in vertical chunk 4; support y66 is local 2 and the diagnostic cell above y68 is local 4. Both sites sit in the same vertical chunk as their support. The second sits at the negative-X chunk boundary. These coordinates and metadata were observed by the prior public admission run, not inferred from a generated-block read.

The test reads each real `ColumnCache.Get` once, confirms the fixed height, stored Taiga, nonzero dirt, absent column water and oasis, actual `FloraBiomeAt` Taiga, `SnowGenerator.DepthAt == 0`, `IsWaterColumn == false`, `SurfaceClearance`, public `TryGetGrassTuft`, `DynamicGroundCover.CandidateAt == fern`, and the real foliage write band containing y67. It then constructs one normal `Chunk(..., generateBlockData:true)` and one planner-style `Chunk(..., generateBlockData:false)` followed by `ChunkTerrainFill.GenerateBlockData(probe, generateLakes:false)` for each key. For both outputs it reads exactly support/base/above IDs and states, logs all six values, and requires normal support grass, normal base fern with default state zero, and matching support/base IDs and states in the planner probe. It does not inject soil/air/plant or bypass any gate. There is no conditional skip, candidate fallback, post-hit search, `World`, full dense comparison, or unrelated whole-chunk equality assertion. An occupied target or later writer overwrite must fail substantively and remain in the raw result.

Source ownership: `DynamicGroundCover.Stamp` requires actual grass, air, tuft, dry column, clearance and a valid in-chunk surface y; `FoliageGenerator.Stamp` dispatches to it under Dynamic. `ChunkTerrainFill` calls foliage after snow/flowers and before tower/sky stamps. `Chunk` normal construction invokes the same fill; the planner variant passes `generateLakes:false`, which changes other internals, so only the local surface IDs and states are compared. The Taiga candidate is the one-cell `fern`, not `tall_fern`; its top cell is logged only. The test does not claim an in-game appearance or a universal Taiga property.

## Direct and transitive work ledger for one future window

The counters increment **before** attempted calls and print from `finally` even if an assertion or fill fails. A failed constructor/fill still counts as attempted work. Bounds for the one test invocation:

| Direct entry/read | Bound | Notes |
| --- | ---: | --- |
| `TestContent.EnsureLoaded`, `WorldSeed.Apply(Dynamic)`, cleanup `WorldSeed.Apply(Default)` | 1 each | Cleanup conditional on successful first apply; no world exists. |
| `ContentRegistry.RequireBlock` | 2 | `grass`, `fern`; static/loader internals unmeasured. |
| Site visits / requested `ColumnCache.Get` | 2 / 2 | 1 per fixed horizontal column. Each cold cache creation may compute 256 column entries and nested climate/height/water/site reads; these are not counted as two terrain evaluations. |
| Fixed metadata array members | 10 | Heights, Biomes, DirtDepth, WaterLevel, Oasis, once each per site. |
| Horizontal `MathUtils.FloorDiv` | 4 | x and z per site; 4 subtraction/local checks. |
| Fixed public/pure precondition APIs | 2 each | `FloraBiomeAt`, `SnowGenerator.DepthAt`, `IsWaterColumn`, `SurfaceClearance.Allows`, `TryGetGrassTuft`, `CandidateAt`, `FoliageGenerator.WriteBand`, `StampBand.Contains`. Each may internally resample noise/classifiers/caches; not a measured total. |
| Normal `Chunk` construction with fill | 2 attempted | Exact keys above, one per site. |
| Probe `Chunk` construction / `ChunkTerrainFill.GenerateBlockData(...,generateLakes:false)` | 2 / 2 attempted | No other fill. |
| `IsBlockDataReady` member reads | 4 | One per output per site. |
| Fixed local `GetLocalBlock` / `GetLocalState` | 12 / 12 | Three cells per output per site; log support, target, above. |

Four real fills imply at least `4 × 16³ = 16,384` source-addressed chunk cells in their fill arrays. This is not a bound on repeated per-pass visits, feature footprints, terrain/noise queries, internal `ColumnCache.Get`, nor planner lake-dependent generation. Those transitive operations remain unmeasured until a granted run. No `ToDense`, full-chunk compare, mesh/LOD, separate finder, World, store or GL call is requested. If internal work or elapsed time is unexpectedly high, retain the raw partial ledger and stop; do not silently expand the window.

## Proposed Release execution, held pending root grant

After root reviews the exact committed source and grants a window: one `DOTNET_ROLL_FORWARD=LatestMajor dotnet build VoxelEngine.sln -c Release`, exact discovery of `VoxelEngine.Tests.WorldGen.Features.TaigaFernFrozenFillTests.FrozenWarmEdgeTaigaFernsReachNormalAndPlannerProbeSurface`, then one focused `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.TaigaFernFrozenFillTests.FrozenWarmEdgeTaigaFernsReachNormalAndPlannerProbeSurface` with TRX and raw argv/cwd/scoped environment/exits/logs and executed DLL copies. This proposal authorizes no execution now and makes no pass claim. Root may stop or change scope after source review.
