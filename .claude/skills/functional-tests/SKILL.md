---
name: functional-tests
description: Rules and recipes for writing xUnit tests in CSVoxelEngine (OpenGLTest.Tests) - what is worth asserting, the banned constant-pinning patterns with rewrites, and the headless TestContent fixture. Use before adding or reviewing any test.
---

# Functional tests for CSVoxelEngine

Tests live in `OpenGLTest.Tests/` (headless, no window). Run:

```
DOTNET_ROLL_FORWARD=LatestMajor dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release
```

(The code needs the .NET 10 SDK, the system `dotnet`; the 9.0.100 in `~/.dotnet` cannot compile it. Release only.) There is no CI, so a feature's tests are the only regression net.

## When to add a test
Add one with every feature **only when there is a rule to lock down**: a gate, a formula, a state transition, a conservation law, an invariant that must survive content tuning. If the only thing you could assert is a number you just typed in a JSON file, write no test.

## Ban list - never assert these
Each row is a test that fails on harmless tuning and proves nothing.

| Banned | Why | Write instead |
|---|---|---|
| `Assert.Equal(5, block.Hardness)` / any JSON literal | pins content, not behavior | assert the *relation*: harder block takes more hits, `HardnessOf` is monotone in thickness, a tool tier gate admits/refuses |
| `Assert.Equal(1, inventory holds one converter)` / starting-stock or seed counts | pins a data choice | assert the rule that produces it (e.g. "a new survival world starts with `Inventory.Empty`") only if that IS the rule |
| exact ml / damage / cooldown figures from content | retuning breaks it | conservation: a cast melts back into exactly what it took (`MetalworksRecipeTests`); damage ordering; "result > 0 and <= cap" |
| layout pixel numbers (button x/y, panel widths) | pins layout | assert hit-testing: every button's center `HitAt`s that button, rows do not overlap, scroll clamps |
| a config constant (`WorldGenerationConfig.X == 16`) | tautology | assert the invariant the constant must satisfy (the LOD nesting invariants, `HotAirKeepsAFullLoadRadiusFromSnow`, goblin throw band inside `v^2/g`) |
| "does not throw" with no assertion on the result | proves nothing | assert the output |

Rewrite rule of thumb: ask "what bug would make this fail?" If the answer is "someone retuned a value", delete it.

## Fixture and idioms
- `Support/TestContent.cs` loads content headlessly (`ContentRegistry.Load` + `TextureAtlas.RegisterTilesWithoutUpload` + `ContentRegistry.BakeModels`). Reuse it; do not hand-build content.
- Worldgen needs no GL: `new Chunk(null!, pos, generateBlockData: true)` then `GetLocalBlock`. Worldgen is seeded through `WorldSeed`; tests use `WorldSeed.Default`. Never call `WorldSeed.Apply` while a World is live.
- Name the test after the rule (`NoRiverChannelCrossesAnElevatedPond`), not the method under test.
- Mesh tests: compare the greedy and non-greedy outputs (`GenerateMeshData(manager, greedy: false)` is the seam; see `GreedyMeshingTests`).
- GL-free twins exist for shader math (`SkyPalette`, `Tonemap`, `LightTint`, `ContactAo`, `SurfaceFresnel`): test the twin, and keep the shader in step (see the `shader-sync` skill).
- Existing area-specific test files (`ContentTests`, `BiomeTintTests`, `ChunkSaveStoreTests`, `ServerWorldTests`, ...) are the place to extend; add a new file only for a new area.

## Before finishing
Run the full suite, report the real pass/fail counts, and never claim "tests pass" without having run them.
