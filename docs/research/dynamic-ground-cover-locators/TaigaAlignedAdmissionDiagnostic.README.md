# Opt-in locally aligned Taiga admission diagnostic

`TaigaAlignedAdmissionDiagnosticTests.cs` is **unrun** research source outside
the default test project. It is a separate child of the clean S4a head
`3f4cb52085750aa754188df9def9fe694ca8a5ea`. It changes no production
generator, content, asset, profile, snow or biome rule. Keep the older
31-point line, 1,625-point grid, their README and every retained miss intact.

## Retained measurements and fixed coordinate choice

The approved seed-1337 Dynamic 1,625-point grid logged 190 Taiga flora rows,
all with climate snow. Five were in the partial-snow raw-cold band and still
snowed. Its 1,625 ordered raw rows, TRX, executed binaries and source are in
S4a attachment `tf435dcce0d6699abf`; parent issue64 document
`dc1cc23a76028` retains the exact post-removal texts and final manifest.
The final gzip is preserved locally, not uploaded. Those finite misses do
not prove Taiga fern absence or a product defect.

Retained row at `(-7195,8829)` has raw cold `.5513618`, ecotone `.83940315`,
pack-ice-derived snow noise `.32205224`, and Taiga with snow. Its measured
axis neighbors give:

| Axis | Retained cold readings | Centered finite difference |
| --- | --- | --- |
| x | `(-7219,8829): .55769074`; `(-7171,8829): .5457134` | `-.0002495279167` per x |
| z | `(-7195,8805): .6127908`; `(-7195,8853): .48887962` | `-.0025814829167` per z |

Thus the **local** isocold slope is `dz/dx ≈ -.09666`; a `(10,-1)` step is
close to that tangent. Moving the center from z=8829 to z=8824 predicts raw
cold about `.5643` under this *local linear approximation*. This is arithmetic
on retained rows, not a new generator query, and neither the slope nor the
prediction is asserted over the full grid. Nonlinear climate warp, distinct
noise channels and changing humidity/continentalness can move the gates away
from the anchor. Root's fixed-fraction bilinear screen of 73 cells predicted
no simultaneous margin from four known pack samples; it is not a source
result for unobserved interiors and supplies no promised hit.

The source freezes seed 1337 Dynamic and **one** ordered grid:

```text
normal  = -12 .. 12   (outer loop)
tangent = -64 .. 64   (inner loop)
x = -7195 + 10*tangent
z =  8824 + normal - tangent
```

That is exactly 25×129 = **3,225 unique coordinate visits**. The x bounds
are `[-7835,-6555]`; z bounds are `[8748,8900]`. Near the anchor, the
`(0,1)` normal spans the narrow possible cold band while `(10,-1)` travels
approximately along it across multiple spatial offsets. The tangent step
has length about 10.05 world units. The Dynamic temperature/humidity scales
are 1/9000 and 1/3200 with 1/1400 warp; ecotone uses 1/96 and pack ice
1/36 with two octaves. These frequencies are not literal periods, and the
grid does not guarantee independent phases, higher-octave resolution, a
Taiga/snow-free overlap, or a universal result. The old `(12,-12)` tangent
was not aligned with this measured cold boundary and only 106 of its 1,625
rows sampled the intermediate snow band. No adaptive refinement, second
region, alternate seed, rerun or early stop is in the new source.

## Exact direct work and evidence limits

Every one of the 3,225 rows logs base/ocean biome, actual Dynamic flora,
temperature/humidity, cold/dry/warm/semi-dry/jungle ramps, ecotone and
shifted gates, actual `HasSnow`, and an explicit first rejection reason. In
the intermediate raw-cold band only, it separately reads the pack-ice field
at `x+4096,z-4096` and logs the snow threshold; `HasSnow` internally reads
that field again. Only actual Taiga with `climateHasSnow=false` gets one
`TryGetGrassTuft` and one `CandidateAt` call. The first full pure
Taiga/no-snow/tuft/fern candidate is remembered, but all rows are emitted
before any metadata read. Zero candidates is a valid diagnostic result.
Counters increment **before** each direct call and a partial ledger prints
in `finally` even if execution fails.

| Direct operation | Frozen maximum |
| --- | ---: |
| Coordinate visits and emitted rows | 3,225 each |
| `FloraBiomeAt`, `BiomeWeights`, `SampleClimate`, `ColdOf`, `DryOf`, `WarmOf`, `SemiDryOf`, `JungleOf`, `EcotoneNoise.GetNoise`, `HasSnow` | exactly 3,225 each on normal completion |
| `Classify` | at most 3,225, skipped for base Ocean |
| Direct offset `PackIceNoise.GetNoise` | at most 3,225, intermediate raw-cold band only |
| `TryGetGrassTuft`, `CandidateAt` | at most 3,225 each, conditional together |
| `ColumnCache.Get` | at most one after all rows, first complete pure candidate only |
| Indexed column metadata fields | at most five at that one point |
| `ColdWeightAt`, `DepthAt`, `TerrainErg.Intersects`, `IsBeachHeight`, `SurfaceClearance.Allows` | at most one each |
| `IsWaterColumn` | at most two: height and bed |
| `FloorDiv` | at most three: x, z and vertical band |
| Chunk fills, block reads, mesh, World, game and GL | zero |

The **direct** call ledger is not a total noise/feature-work count.
`FloraBiomeAt` and separate `BiomeWeights` recompute continental/island/
climate inputs; Dynamic dither resamples climate and ecotone;
`TryGetGrassTuft` rereads core/oasis/flora/patch/climate/hash; `HasSnow`
internally rereads pack ice in its intermediate band. A conditional cache
miss would compute 256 metadata entries through terrain, water, site and
noise dependencies; those transitive calls remain unmeasured and must be
qualified in any later runtime grant. Metadata only predicts likely grass.
It cannot prove actual cave/feature edits, occupied air or final stamping.
If the selected metadata point fails, the diagnostic does not try another.

## Future opt-in procedure only

Root must review this exact source and grant execution separately. Only then
temporarily copy it by name to `OpenGLTest.Tests/WorldGen/Features/` in the
assigned isolated worktree, verify byte identity, build the Release solution
with installed SDK 10 and scoped `DOTNET_ROLL_FORWARD=LatestMajor`, discover
exactly one test, and run only this filter with `--no-build --no-restore` and
an owned TRX destination:

```text
FullyQualifiedName=VoxelEngine.Tests.WorldGen.Features.TaigaAlignedAdmissionDiagnosticTests.FixedAlignedGridReportsPureGatesAndAtMostOneMetadataColumn
```

Preserve full source and both csproj identities, the temporary inclusion,
argv/cwd/scoped environment/UTC/exits/raw stdout/stderr/TRX, all 3,225
rows/partial counters and executed game/test-output/test DLL copies/hashes.
Archive and read back the actual executed source before removing only that
temporary inclusion, then retain exact post-removal path/status/head/tree
proof. This proposal grants no build, discovery, test, climate/candidate
query, cache/column construction or fill now. Even a positive metadata
result would require another reviewed source and runtime grant for actual
normal/planner-probe natural stamping.
