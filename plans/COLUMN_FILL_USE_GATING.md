# Column fill use-gating: measured candidate

Status: **candidate evidence; change is not landed.** This record describes the scoped feature on
branch `nexus/avoid-unused-material-and-water-dec-c9bb57ca-65ba-4252-b013-7f1ea3c69b69`, based on
frozen commit `ed97f2911fc75446a7b843c3fbe936ede8e395ff` (tree
`f2fee3d88919ab7f077eac116b4eba495397ae05`). The reviewed code/test head is
`2153854694f995e04126ab52748af8e00ff11953` (tree
`63be207f4bb8e79fc4f0e79c1dbec6f842b01f6c`). The production change is limited to
`ColumnCache.GenerateColumn`; tests use a test-local legacy implementation and sentinel-filled full
chunk arrays.

## Paired whole-generation run

Release, SDK 10.0.114, net9 target, .NET 10.0.14 runtime with `LatestMajor`, Workstation GC in
Interactive latency mode, 8 reported processors, headless, one generating thread. The fixed fixture
used seeds `1337, 424242, -90210`, these ten literal columns:
`(0,0), (1,0), (-1,-1), (5,3), (-6,7), (12,-9), (-20,15), (37,-31), (400,300), (-350,-420)`,
and chunk Y values `-7..13`. Every process generated 630 fresh chunks cold and then 630 warm.
Stopwatch frequency was 1,000,000,000 Hz. The primary measure is the sum for warm whole
`GenerateBlockData`, not the Fill substage.

Twenty-two fresh processes ran in the predeclared fixed sequence: eight P/C pairs and three P/P
controls. No performance samples were dropped, replaced or retried. P is the frozen baseline and C
the candidate.

| Slot | Order | Warm P (ms) | Warm C (ms) | Warm reduction | Cold reduction |
|---:|:---:|---:|---:|---:|---:|
| 1 | P>C | 141.342 | 128.691 | +8.951% | -1.546% |
| 2 | C>P | 154.823 | 123.615 | +20.157% | +0.412% |
| 4 | C>P | 144.651 | 122.303 | +15.450% | +1.385% |
| 5 | P>C | 140.581 | 140.740 | -0.113% | +2.089% |
| 7 | C>P | 145.498 | 120.239 | +17.361% | +2.395% |
| 8 | P>C | 136.889 | 128.752 | +5.944% | -11.698% |
| 10 | P>C | 138.997 | 128.331 | +7.673% | +2.075% |
| 11 | C>P | 155.434 | 123.655 | +20.445% | +1.436% |

The paired warm median reduction was **12.2002604%**, saving **17.499709 ms per 630 warm chunks**;
C was faster in 7/8 pairs. The pair range was -0.1131887% to +20.4453009%. Per-seed warm wins were
8/8, 8/8 and 7/8. The only warm slowdown was slot 5 for seed -90210. C-first pairs had larger gains,
showing order sensitivity. P/P warm first-to-second changes were -3.4210%, -6.3529% and -0.3444%;
these are descriptive controls, not a confidence bound.

Cold median reduction was 1.41036%, with C faster in 6/8 pairs. A cold improvement is **not
established** against variable controls (+3.6758%, -4.8969%, +2.9789%). Two pairs regressed; slot 8
was 11.697925% slower. This result is reported as measured and is not discarded as an outlier.

## Call counts and identity

The corrected external temporary counter run measured the cold 630-chunk fixture:

| Callee | Baseline | Candidate | Change |
|---|---:|---:|---:|
| `GenerateColumn` | 74,752 | 74,752 | unchanged |
| `IsWaterColumn` | 74,752 | 5,744 | 69,008 fewer (92.3159%) |
| `SurfaceWaterWeight` | 240 | 48 | 192 fewer |
| Gravel-patch decisions | 0 | 0 | unchanged |

All 1,260 chunk hashes and 30 column hashes matched in all 22 timing processes and both count modes.
Candidate call-count and Fill-substage reductions are work evidence, not timing evidence by
themselves. Skipped calls can still change cache residency and diagnostic workload.

The fixed timing fixture has no Desert, Frozen, positive-beach-weight or soil-depth-zero coverage.
The bounded legacy-equivalence tests cover all seven biomes and default enum behavior, material
depths including 255, top/soil/deep/above boundaries, negative chunk/floor coordinates, overflow and
manual-input cases, beach-positive gravel and sand, soil-only beach, zero weight, differing bed and
height, frozen/desert/ocean behavior, and oasis fill-biome override. They compare complete sentinel
arrays against the pinned legacy implementation. Full Release solution build passed with zero
warnings/errors and the full headless suite passed 3060/3060 on parent `cc62c2b4`; focused tests
passed 3/3 on test-only successor `2153854`. The full-suite result is not attributed to the
successor.

## Scope and limits

This is a single-threaded, fixed headless fixture and does not establish an application, FPS,
streaming-ring or representative-worldgen improvement. Warm paired evidence supports a measured
improvement on this fixture, with order sensitivity and one near-zero pair. Cold improvement is
unestablished and includes a substantial regression. Continue to report both. This does not prove
universal purity or absence of indirect side effects, and does not address broader worldgen
opportunities.

## Raw evidence references

The source evidence remains on research task
`fa8e6b53-795b-4e6f-8c2d-778e5c315596`; exact captured text and byte/hash manifest are retained
there. The parent task holds the independently accepted log archive
`tf95204e1f092dccd1` and review follow-up archive `tf14815aa9c608b164`.

- Prelaunch declaration: doc `d71bb2ed07b3b`.
- Paired order, analyzer output, process status/PIDs, run summaries and complete 179-file
  SHA-256/byte manifest: `daaf259ee2998`.
- All 22 per-chunk timing row files: `d090795ecc95b`, `dae8843cc88fa`, `d497965bc0ddb`,
  `d856fd776e693`, `d29ea56535f3b`, `dd22324124f12`, `d3a2bbd848e47`, `d0c2fc270b018`,
  `d9ade9c1c0828`.
- Identical identity data parts: `d54707065c60d`, `d5d2ef99bab31`, `d14cec6d96ab0`;
  concatenated SHA-256 is
  `463dbfede2c4200575d7bafc15b9a8cddf3bc30ce7d30fbbd1631d693fdc512f`.
- Count-mode column hashes/empty stderr records: `d95600fca6a6b`; columns SHA-256
  `e1bc8ef5e2f72de3e148ee27d6bec52fa2ab6786ea86c529c0353883b8701e64`.
- Counter rows: `db7d4cc1c3289`, `dd7c03a07f337`; count source/build manifest:
  `d508c0836dbd4`, `db8e5da8d11fa`.
- Uninstrumented harness/manifest helper v5: `d70d72c6f8dfa`; analyzer v6:
  `d855fedd43b41`; baseline/candidate builds and manifest: `da121515a376e`; paired runner:
  `fad84cf8`; colocated analyzer: `b1174b0d`.

Research artifacts and dependency provenance are preserved unchanged. The production branch has no
diagnostic counters or measurement harness.
