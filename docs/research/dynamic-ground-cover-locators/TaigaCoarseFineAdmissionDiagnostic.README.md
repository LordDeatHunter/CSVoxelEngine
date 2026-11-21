# Opt-in coarse-to-fine Taiga admission diagnostic

`TaigaCoarseFineAdmissionDiagnosticTests.cs` is **unrun** source outside the
default test project. It is a sole-parent child of the clean nearby-lattice
head `f8ee7daa093d2b2b67f82da4c2a4f0b89715ea22`. It changes no product
terrain, snow, foliage, content, assets or test-project configuration. All
earlier diagnostics, results, archives, scratch and refs remain intact.

## Why this fixed locator differs from the nearby rectangle

The accepted nearby 8,192-point lattice observed only eight snow-free Taiga
points, all in the previously rejected first pocket. Every public tuft was
false; all eight separate same-factory patch readings were below the actual
threshold. This finite miss does not establish that another region admits no
fern. The new locator selects other land/cold coarse centers using source
gates, then samples one predetermined fine square at each selected center.
No product constant is tuned to obtain a result.

The source's necessary raw-cold band is
`ColdBiomeMinCold - DynamicEcotoneBand <= cold < SnowFullColdThreshold`.
Current constants make that `[0.52,0.60)`: classifier Taiga requires shifted
cold at least `0.70`, the Dynamic ecotone can add at most `0.18`, and climate
snow is certain at raw cold `>=0.60`. This band is only a coarse *necessary
opportunity*: it does not assert actual Taiga, dry gate, dither, pack-ice
snow escape, foliage patch, density or stored ground. Land is actual
`BiomeWeights` base non-Ocean with ocean weight below actual
`OceanShoreWeight`. Scalar frequency scales are not periods, phase
independence or guarantees that eight useful centers will be selected.

## Stage A: fixed coarse grid and deterministic centers

At seed 1337 Dynamic, `i=-64..64` OUTER, `j=-64..64` INNER,
`x=256*i`, `z=256*j`: exactly **16,641** ordered coarse visits and raw rows,
both axes `[-16384,16384]`. Each point directly calls real
`BiomeClassifier.SampleClimate`, `ColdOf` and
`TerrainGenerator.BiomeWeights` once. It logs raw temperature/humidity/cold,
base biome/ocean and an explicit selection/rejection reason. Calls are
counted before execution; all coarse rows emit even after eight centers.

Eligible centers are actual land in the necessary cold band. A center is
excluded if its **closed** `+/-64` fine square intersects the already-tested
nearby rectangle x `[-7323,-7069]`, z `[8761,8887]`, including boundaries.
The first at most eight remaining eligible centers in coarse traversal
order are selected only when their Chebyshev distance from every earlier
selected center is at least 512 blocks. Selection uses the cached scalars
and integer arithmetic, no new terrain/cache query. The separated 129-block
refinement squares cannot overlap. Zero/fewer centers is a valid bounded
outcome; no alternative center, region or seed is tried.

## Stage B: predetermined fine squares

Only after all coarse rows, for selected centers in their fixed order:
`u=-32..32` OUTER, `v=-32..32` INNER,
`x=centerX+2*u`, `z=centerZ+2*v`. This is **4,225 fine visits/rows per
center**, at most **33,800 fine visits**, and at most **50,441 total
stage-specific visits**. Do not call those 50,441 coordinates globally
unique: each selected coarse center is visited again at `u=v=0`; other
coarse/fine overlaps may also exist. The separated fine squares themselves
do not overlap.

Every fine row records the accepted actual flora/classifier/shifted gates,
real `HasSnow`, conditional direct offset pack sample, public
`FoliageGenerator.TryGetGrassTuft` and actual
`DynamicGroundCover.CandidateAt`, plus explicit rejection reasons. Only
actual Taiga/no-climate-snow rows get the latter two calls and separate
explanatory core/oasis/patch/climate/density reads. One local patch object
is constructed after `WorldSeed.Apply` using exactly
`TerrainGenerator.MakeNoise(63,WorldFeatureConfig.GrassPatchFrequency)`,
the existing `FoliageGenerator.Reseed` factory call and default one octave.
It does not inspect/mutate the private field or clone its density hash.
`density-inferred` remains a source inference; public tuft/variant results
alone determine pure admission. All fine rows emit after any candidate.

## Stage C: bounded metadata, after every raw row

One full pure candidate requires actual Taiga, no climate snow, public
tuft=true and actual fern variant. For **each** such fine point, two
negative-safe `FloorDiv` calls compute its horizontal chunk column. The
first pure point in traversal order for each of at most eight distinct
columns is retained, with its cached explanatory core status. Selection
reads no column/terrain metadata and never replaces an earlier candidate.
Only after *all* coarse and fine rows may the source call `ColumnCache.Get`
once for each retained column, up to eight. It reads five indexed metadata
fields per selected point and bounded altitude/snow/beach/water/clearance
helpers. Metadata cannot prove actual ground, air, occupancy, or a final
normal/planner-probe stamp. Zero candidates means zero metadata queries.

## Full direct, helper and transitive work ledger

| Direct work | Frozen maximum |
| --- | ---: |
| Coarse visits/raw rows | 16,641 each |
| Coarse `SampleClimate`, `ColdOf`, `BiomeWeights` | 16,641 each, exactly on normal completion |
| Closed-square rejected-rectangle checks | 16,641, once per coarse visit |
| Chebyshev center-spacing comparisons | at most 16,641 × 8 = 133,128; each performs two `Math.Abs` and one `Math.Max` |
| Selected centers | eight |
| Fine visits/raw rows | 4,225 per selected center, 33,800 total |
| Fine `FloraBiomeAt`, `BiomeWeights`, `SampleClimate`, `ColdOf`, `DryOf`, `WarmOf`, `SemiDryOf`, `JungleOf`, `EcotoneNoise.GetNoise`, `HasSnow` | 33,800 each maximum, exactly one per fine visit |
| Coarse + fine `BiomeWeights`, `SampleClimate`, `ColdOf` combined | 50,441 each maximum |
| Fine conditional `Classify`, direct offset `PackIceNoise.GetNoise`, public tuft, actual variant | 33,800 each maximum |
| Local same-factory noise construction | one, after seed apply/coarse stage |
| Fine conditional explanatory core, oasis, patch sample, `Climate`, `GrassDensityAt` | 33,800 each maximum |
| Horizontal `FloorDiv` | two per full pure fine point; 67,600 hard maximum |
| Selected-column deduplication comparisons | at most 33,800 × 8 = 270,400 scalar key comparisons |
| Distinct metadata columns / `ColumnCache.Get` | eight each maximum, after all rows |
| Indexed fields | five per selected, 40 total maximum |
| Altitude-cold, snow depth, beach, clearance | eight each maximum |
| Water queries at height and bed | 16 total maximum |
| Vertical `FloorDiv` | eight maximum; combined horizontal + vertical maximum 67,608 |
| Chunk fills, block reads, meshes, World, game, GL | zero |

Every direct counter increments before its call; a `finally` ledger prints
even on failure. `FloraBiomeAt`, `BiomeWeights`, `Climate`, `HasSnow`, tuft,
oasis and core may resample internally. At most eight distinct cache misses
could construct 2,048 metadata entries, with dependent terrain/noise/site/
feature work. These transitive operations are **UNMEASURED**; the table is
not a total engine-work bound. The source makes no candidate-existence
assertion, skips, synthetic gate or adaptive retry.

## Future opt-in procedure only

Root must first review this exact source/ledger and separately grant
execution. Only then copy this file by name into
`OpenGLTest.Tests/WorldGen/Features/`, verify byte identity, build the
Release solution with installed SDK 10 and scoped
`DOTNET_ROLL_FORWARD=LatestMajor`, discover exactly one method and run only:

```text
FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.TaigaCoarseFineAdmissionDiagnosticTests.FixedCoarseThenFineGridReportsPublicAdmission
```

Retain exact source, both csprojs, owning APIs, temporary inclusion,
argv/cwd/scoped environment/UTC/exits/raw stdout/stderr/TRX/all coarse and
fine rows, selected metadata, counters and actual executed DLL copies/hashes.
Seal and read back an indexed pre-removal archive before removing only the
temporary test file; seal/read back clean post-removal absence and unchanged
head/tree. This procedure is **data**, not a runtime grant. Even positive
metadata requires a separate reviewed natural normal/planner-probe stamp
phase. A finite miss does not authorize another search.
