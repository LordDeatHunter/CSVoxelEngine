# Architecture

## Projects

| Project | Role |
|---|---|
| `OpenGLTest/` (`VoxelEngine.csproj`) | The game. All code is under the `VoxelEngine` namespace, one sub-namespace per folder. |
| `OpenGLTest.ItemDebugger/` | Preview tool for held items, player animations, entity models and trees. |
| `OpenGLTest.Server/` | The dedicated CLI server. |
| `OpenGLTest.Tests/` | xUnit tests, headless (no window). |
| `tools/` | Deterministic asset generators (tiles, sprites, models, clips). |
| `wiki/` | This site. |

## Source layout (`OpenGLTest/src`)

| Folder | Contents |
|---|---|
| `src/` root | The app entry and core types only: `Program`, `World`, `WorldPhase`, `WorldLoadGate`, `WorldSeed`, `BlockType`. |
| `App/` | App components built once by `Program.OnLoad`: input, chat, diagnostics. |
| `Chunks/` | Chunk storage and lifecycle, meshing, world sources, streaming. |
| `Config/` | Every tuning constant, one static class per domain. |
| `Content/` | The data-driven content pipeline (`ContentRegistry`). |
| `Effects/` | GL-free twins of shader terms (sky, water, post, surfaces). |
| `Entities/` | Mobs, drops, projectiles, animation clips. |
| `Interaction/` | Raycast, mining, combat, placement and the click actions. |
| `Rendering/` | The GL passes and their GL-free gates. |
| `Simulation/` | Main-thread simulations: fluids, gravity, TNT, trees, stations, mob managers. |
| `WorldGen/` | Procedural terrain, biomes, caves, trees, and the hell and sky realms. |
| `Networking/` | Wire protocol, client and server-side rules. |
| `Saves/` | World stores and codecs. |
| `Player/`, `Items/`, `Magic/`, `Particles/`, `Lod/`, `UI/`, `Audio/`, `Platform/`, `Status/`, `VisualTest/` | As named. |

## Standing rules

- **No god classes**: one class, one job; past about 700 lines a class is a smell. Prefer composition over `partial`.
- **No god directories**: keep a folder under about 30 files and group by domain.
- **Namespaces mirror the folder tree.** Only six files directly in `src/` live in the root `VoxelEngine` namespace.
- **American English** in identifiers, comments and docs.
- **No dead code**, **no duplication** (a rule that exists twice becomes one tested helper), and **early returns over nesting**.
- **GL-free twins**: a shader term gets a pure C# class next to its renderer (`Foo` beside `FooRenderer`), with one definition and two consumers, and tests prove they agree.
- Animation curves belong in `MathUtils`, never inline.
- `FastNoiseLite.cs` is vendored third-party code: do not reformat it.

## Startup order

1. `ContentRegistry.Load("assets")` parses items, loot, blocks, models and assigns byte ids (no GL).
2. `TextureAtlas.Build` loads the block tiles into a `TEXTURE_2D_ARRAY`.
3. `ContentRegistry.BakeModels()` bakes block models into atlas-ready quads.
4. Shaders, caches, UI, and finally the `World`.

The `World` and its worker threads are created last, so all content tables are immutable by the time anything but the main thread can see them.

## Rendering at a glance

Chunks do not own mesh objects. Their geometry is uploaded into shared vertex and index pools, and each frame's passes (shadow, reflection, opaque front-to-back, translucent back-to-front) are issued as one multi-draw-indirect call each. The world is lit in linear light into an HDR target and tonemapped, with FXAA, in a final post pass. Greedy meshing merges plain cube faces wherever the result is pixel-identical.
