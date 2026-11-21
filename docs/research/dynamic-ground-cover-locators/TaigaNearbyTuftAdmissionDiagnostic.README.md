# Opt-in nearby Taiga tuft and column diagnostic

`TaigaNearbyTuftAdmissionDiagnosticTests.cs` is **unrun** research source
outside default test compilation. It is a separate child of the clean local
tuft commit `434a83acf399966b24f8777440fdcb597e7c3dc2`. It changes no
production foliage, terrain, climate, snow, content, assets or test-project
file. Keep every preceding source, README, result, archive and ref unchanged.

## Fixed geometry from retained observations

The accepted aligned run had actual Taiga x groups at `-7285/-7275/-7265`,
`-7195/-7185` and `-7115/-7105`. Its sloping 25-row transect had snow-free
Taiga only in the first pocket, but did not sample two-dimensional interiors
of the other groups. The subsequent fixed 153-point first-pocket run logged
30 snow-free Taiga and **all** public tuft=false; all same-factory patch
samples were below `0.05` in that one pocket. This does not predict the
patch value in the other groups.

The new source freezes seed 1337 Dynamic and exactly one integer lattice:

```text
i = 0..127  outer; x = -7323 + 2*i   ([-7323,-7069])
j = 0..63   inner; z =  8761 + 2*j   ([8761,8887])
```

There are **8,192 distinct ordered coordinate visits**. Root's data-only
comparison found 352 distinct coordinates from the combined aligned and local runs on this
lattice (312 aligned, 45 local, five shared; 54 Taiga, 269 Steppe, 29 Forest),
including eight snow-free Taiga observations in the rejected pocket. The lattice covers the three observed
groups but does not guarantee an admitted tuft, independent noise phases,
higher-octave coverage, a complete boundary search or eligible metadata.
No alternate seed/grid, early stop, adaptive refinement or second region is
permitted by this source.

## Actual authority and explanatory calls

Each row logs the real `FloraBiomeAt`, `BiomeWeights`, climate/classifier
ramps and shift, actual `HasSnow`, conditional direct pack sample and explicit
rejection reason. Only actual Taiga/no-climate-snow rows call the public
`FoliageGenerator.TryGetGrassTuft` and actual
`DynamicGroundCover.CandidateAt`; their conjunction with variant fern is
the **only** pure admission criterion. For those rows, separate explanatory
reads use `TerrainErg.Intersects`, `OasisGenerator.IsDisc`,
`TerrainGenerator.Climate`, `FoliageGenerator.GrassDensityAt` and a local
same-factory patch sample. The local object is constructed once **after**
`WorldSeed.Apply(1337,Dynamic)` via the same internal
`TerrainGenerator.MakeNoise(63,WorldFeatureConfig.GrassPatchFrequency)` call
used by `FoliageGenerator.Reseed`. `TerrainNoise.MakeNoise` supplies seed +
offset, OpenSimplex2 and that frequency, with default one octave. It is an
explanation, not reflection into or mutation of the private field. The
private density hash is not copied: `density-inferred` is explicitly a
source inference when the observed public result is false and the other
reported gates leave density as the remaining path.

## Direct, deduplication and transitive work ledger

Counters increment **before** each direct call and a partial ledger prints
in `finally` even on failure. All 8,192 rows are emitted after any candidate.

| Direct operation | Frozen maximum |
| --- | ---: |
| Ordered visits and raw rows | 8,192 each |
| `FloraBiomeAt`, `BiomeWeights`, `SampleClimate`, `ColdOf`, `DryOf`, `WarmOf`, `SemiDryOf`, `JungleOf`, `EcotoneNoise.GetNoise`, `HasSnow` | 8,192 each, exactly on normal completion |
| Conditional `Classify`, offset `PackIceNoise.GetNoise`, public `TryGetGrassTuft`, actual `CandidateAt` | 8,192 each |
| Local same-factory noise construction | one after seed apply |
| Conditional explanatory core, oasis, same-factory patch sample, `Climate`, `GrassDensityAt` | 8,192 each |
| `FloorDiv` for horizontal column selection | two per full pure candidate, at most 16,384 total |
| Selected distinct horizontal columns / `ColumnCache.Get` | at most eight each, only after all rows |
| Indexed metadata fields (height/stored biome/dirt depth/bed/water level) | at most 40 total |
| Altitude cold, snow depth, beach and clearance | at most eight each |
| Water queries at height and bed | at most 16 total |
| `FloorDiv` for selected vertical band | at most eight; combined maximum 16,392 |
| Chunk fills, block reads, meshes, World, game, GL | zero |

For every full pure candidate, negative-safe `MathUtils.FloorDiv(x,16)` and
`FloorDiv(z,16)` choose its horizontal column. The source stores only the
**first** candidate in fixed numeric traversal order for each of at most
eight distinct columns; no metadata or terrain is read while selecting.
Once eight are selected, later rows and public checks still run, with no
replacement. Each selected column is queried once after all rows, using
cached `Cx/Cz` and the row's earlier core result. Fewer or zero pure
candidates mean fewer or zero column lookups; no fallback point is tried.
One cache miss may construct 256 entries, so eight distinct misses imply
at most 2,048 source-derived metadata entries. Dependent climate, noise,
terrain, site and feature reads inside the direct methods and cache factory
remain **UNMEASURED**. The direct 8,192 budget is not a total engine work
claim. Metadata can predict likely soil but does not inspect actual ground,
air, occupancy or normal/planner-probe final stamping.

## Future procedure only

Root must review this exact source and grant a bounded diagnostic window.
Only then copy it by name to `OpenGLTest.Tests/WorldGen/Features/` in the
assigned isolated checkout, verify byte identity, build the Release
solution with installed SDK 10 and scoped
`DOTNET_ROLL_FORWARD=LatestMajor`, discover exactly one method and run only:

```text
FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.TaigaNearbyTuftAdmissionDiagnosticTests.FixedNearbyLatticeReportsTuftGatesAndDistinctColumns
```

Preserve source, both csprojs, owner APIs and exact inclusion, argv/cwd/
scoped environment/UTC/exits/raw stdout/stderr/TRX/all 8,192 rows, executed
DLL copies/hashes and indexed pre-removal archive/readback. Remove only the
temporary test copy after the complete pre-removal seal; retain/read back
post-removal absence, clean status and unchanged head/tree. This is a
**proposal**, not execution permission. A positive metadata result still
requires another reviewed natural normal/planner-probe stamp source and
runtime phase. A miss is finite and does not justify automatic search.
