# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project overview

A Minecraft-style voxel engine written in C# on .NET 9, using **Silk.NET** (windowing, input, OpenGL bindings) and StbImageSharp for texture loading. It renders an infinite, procedurally generated world (FastNoiseLite OpenSimplex2 noise) with chunk streaming, an underground of 3D-noise caves, worm-carved tunnels and heightmap ravines, binary greedy chunk meshing with ambient occlusion (plain cubes are culled by bitmask and merged into larger quads wherever the result is pixel-identical; procedural blocks and fluids keep per-quad emitters — see `MESHER_PLAN.md`), frustum culling, block breaking/placement via raycast, and simple player physics (gravity, jumping, collision). The player spawns standing on the terrain at the origin (`Camera.SpawnPosition`).

The renderer requires an **OpenGL 4.6 core** context: `WindowManager` requests `APIVersion(4, 6)`
with `ContextProfile.Core`, and every shader under `assets/shaders/` declares `#version 460 core`.
The chunk draw path uses those 4.6 core features directly — `gl_DrawID`, `BufferStorage`,
persistent buffer mapping and `glMultiDrawElementsIndirect` (see "Chunk drawing" below) — so this
is a hard requirement rather than a preference; any 2015+ desktop driver on Windows or Linux
provides it, but **macOS (stuck at GL 4.1) is no longer supported**. The optional Phase 5 in
`MESHER_PLAN.md` (a compute cull writing the indirect buffer) would add
`glMultiDrawElementsIndirectCount`.

There is a single executable project. Historical naming quirks to be aware of:

- The solution is `OpenGLTest.sln`, the project folder is `OpenGLTest/`, the csproj is `OpenGLTest/silkgltriangle.csproj`, and **all code lives in the `silkgltriangle` namespace**. Keep new files in that namespace unless deliberately renaming everything.
- Verification is by building and running the game **and** by the xUnit tests in `OpenGLTest.Tests/` (headless, no window: `dotnet test OpenGLTest.Tests/silkgltriangle.Tests.csproj -c Release`) plus the scripted visual scenarios under `OpenGLTest/visual-examples/` (see `VOXEL_SCRIPT` below). There is no CI.

## Build and run

```powershell
dotnet build OpenGLTest.sln -c Release
dotnet run --project OpenGLTest/silkgltriangle.csproj -c Release
```

Always build and run **Release**. Debug is several times slower in the voxel loops (mesh
builds, light propagation, the per-frame chunk walks), so a framerate or hitch observed in Debug
says little about the game. `Directory.Build.props` at the repo root makes Release the default
when no `-c` is given to a project build or `dotnet run`; a solution build still needs the
explicit `-c Release`, and Rider's run configuration follows the solution configuration
dropdown, which must be set to `Release | Any CPU`. Use `-c Debug` only when you need
breakpoints. The startup line `Build: Release | debugger ...` confirms which one is running.

Both csprojs target **net9.0**. On a machine that only has the .NET 10 SDK installed, prefix every
`dotnet` command with `DOTNET_ROLL_FORWARD=LatestMajor` (and use the SDK's own launcher, e.g.
`~/.dotnet/dotnet.exe`) or the build and the tests refuse to run at all.

Setting the `VOXEL_SPAWN` environment variable to `x,y,z` (three comma-separated floats, e.g. `-40,-107,-75`) starts the camera eye at exactly that world position instead of on the surface at the origin, which is handy for inspecting a specific cave or feature without flying to it; an unparseable value is ignored with a console warning. `VOXEL_LOOK` takes `yaw,pitch` in degrees (two floats, e.g. `-90,-20`; yaw 0 faces +X so the default `-90,0` faces -Z, and pitch is clamped to ±89 exactly as the mouse look is) and pins the initial facing, so a scripted verification run frames the same thing every time. `VOXEL_TIME` takes a number of seconds and is added to the window clock before the lighting pass reads it (`Program.timeOffset`), which jumps the day/night cycle straight to noon, dusk or night for a screenshot instead of waiting out the orbit (one full cycle is `WorldGenerationConfig.DayLengthSeconds`, 1200 s like Minecraft's 20 minutes, with the sun starting mid-morning, so noon is about 147 and sunset about 447). All three parse once at startup and ignore garbage with a console warning. Setting `VOXEL_SCRIPT` to a scenario JSON path (see `OpenGLTest/visual-examples/basic.json`) runs a scripted visual test instead of playing: `teleport`/`look`/`wait`/`walk`/`mine`/`place`/`fly`/`give`/`screenshot` steps drive the real update functions through a scripted keyboard and save numbered PNGs to `visual/<name>/` (`screenshot` takes an optional `hideHud`), then the window closes. `fly` turns creative flight on (or off with `"on": false`, `Camera.TestSetFlight`) so a scripted camera can hover over a spot instead of falling. Scenario parsing is covered by `VisualScenarioTests`; prefer a scenario over ad-hoc scripts for screenshot verification. `VOXEL_DEBUG=1` starts with the X debug line already on, so such a run logs FPS, position and the profiler breakdown once a second.

The app opens a window and grabs the mouse. Controls: WASD to move, mouse to look, Space to jump, `C` to toggle creative mode (placing costs nothing, breaking drops nothing; double-tap Space toggles flight, but only while creative — flight is noclip and Space/Left Ctrl move up/down while flying), Left Shift to sprint (1.5x on foot, 10x while flying), hold left-click to punch first, then mine: a slime inside the forward punch cone (3 blocks, 30° half-angle, line of sight required; only the nearest is hit) takes the held item's `entityDamage` (1 bare-hand, 3 for the primitive tools, 5 for the copper tools, 6 for the obsidian blade) and a knockback shove instead of being mined through (an item declaring `attack: "slice"` — the obsidian blade — instead swings a two-phase blade sweep: each click hits the nearest two slimes in the forward hemisphere of the same reach, the buffered return swing lands at half power, and the arm and blade are carried across the front), while a clear cone mines the targeted block (hit-based: one hit per the held item's `tool.hitDelayMs`, deals the item's `blockDamage`, 0.5 bare-hand, against the block's float `hardness`, declared in its JSON - 5 for most blocks, 8 for malachite ore, 24 for the full-width log, with each thinner taper step down to the twig shaving 15% of that log's base off its hardness, 2 for leaves, and `0` means it pops on first contact like a pebble; a block with `requiresTool` and a `tools` dict needs a listed tool tag at or above its required tier, and the wrong tool whiffs for 0 damage and no progress; damage resets if you release, switch target or switch tool; instant while creative, which ignores the gate; a click with no block under the crosshair still swings the fist); a stepped crack-sprite overlay drawn on the block's faces via `break-*.glsl` from `assets/textures/environment/break_progress.png` (8 stacked 32x32 frames, the top one blank); each hit pops the block out and back via `RenderManager.RenderBlockPop`), right-click to place the selected block or, when the selected slot holds an item with a `use` action, to use it (the bucket scoops a water source and pours it back out — see the item schema below), 1-10 to pick one of the 10 hotbar slots, `E` for the list inventory (blocks/tools/misc tabs with shared caps of 2048/8/2048; click a row to preview, click a hotbar slot to equip, same item again to unequip; the world keeps running and `E`/Escape closes it), `X` to toggle debug logging (FPS/position/water state to console), Escape to quit.

`X` also raises the **frame profiler overlay** under the FPS counter, and prints the same
snapshot to the console once a second so a headless run's log carries it. Console writes go
through the async `Log` queue (`OpenGLTest/src/Log.cs`), so a slow terminal never stalls the
frame that logged; `Log.Shutdown` drains it on exit. Each row is a section
of the frame — `camera`, the three `chunk*` rows from `ChunkManager.Update`, `lod`, `entities`,
`fluidWake`, `fluidTick`, `raycast`, `breaking`, plus the `progUpdate` input shell and the
`player` spawn/animation shell on the update side (right-click use/place work and console
logging are split out as `usePlace` and `debugLog`), then `lighting`, `shadows`,
`lodPass`, `worldPass`, `entityPass`, `waterPass`, `selectPass`, `uiPass` on the render side,
and the `rendShell`/`screenshot`/`publish` frame shell —
with its average and worst millisecond cost over the last second; `other` is the wall time no
section claimed (buffer swap, window pump, OS/GC jitter). The header carries the frame time and FPS, the CPU
sum against the GPU's own `TIME_ELAPSED` measurement of the whole frame (`--` until the first
query comes back), GC collections per second and the loaded/visible chunk counts. CPU timings are
collected every frame; per-pass GPU timing is gated (`GpuTimingGate.ShouldTime`, latched once per
frame in `RenderManager.BeginGpuFrame`) — every frame while the overlay is up, and one frame in
`WorldGenerationConfig.GpuTimingIdleInterval` while it is down, which keeps enough samples in each
once-a-second window that the snapshot standing when `X` is pressed already carries GPU figures.
The caveat is that a pass which does not run every frame (`selectPass`) can collect no samples at
all across an overlay-down window and read `--` in that first snapshot. The overlay's own glyph
draws land in `uiPass`, so compare that row with `X` on and off before reading it as a baseline.
`Program.OnLoad` and the `RenderManager` constructor each print one startup line — build
configuration, debugger, GC mode and CPU count, then the GL
renderer, vendor and version (which must report a 4.6 core context) — so a measurement always
records which build and which GPU produced it. Timing lives in `OpenGLTest/src/FrameProfiler.cs`;
wrap new work in `FrameProfiler.Begin`/`End` (main thread only) rather than adding another ad-hoc
stopwatch.

Standing in water switches the player to **swimming** (`Camera.IsInWater`, recomputed every frame by `Camera.UpdateWaterState` from the real fluid surface heights under the body): horizontal speed halves, gravity is replaced by an eased sink toward `SinkSpeed` (Space eases up toward `SwimUpSpeed` instead, both through `MathUtils.Damp`), and swimming into a bank while holding Space adds `SwimExitBoost` so the player can climb out without a ramp. Flight ignores water entirely, and a player standing on the bottom of a shallow puddle still jumps normally. Flowing water also pushes the player and floating item drops along its flow direction (`ChunkManager.GetFluidFlowGlobal`, Minecraft's `FlowingFluid.getFlow`: a horizontal vector summed from how much lower each neighbouring cell's surface sits, zero in a still pool). `Camera.IsEyeInWater` is the separate eye-only test and is the source of truth for the underwater tint.

**Water rendering** lives in the shared world shader plus two extra passes in `RenderManager`. Just before `RenderWorldTranslucent` draws, `CopyScene` snapshots the default framebuffer's colour and depth into two textures on units 3 and 4 (`CopyTexSubImage2D`, no FBO — the window has no MSAA); the water fragment then composes its own look out of them: a refracted lookup offset along the analytic wave normal, Beer-Lambert absorption over the water's measured thickness (`WaterAbsorb`) plus in-scattered `WaterScatter`, and a Schlick fresnel mix into the planar reflection. The mirror plane itself is the single `RenderManager.WaterPlaneY` at sea level, so a lake whose surface stands above it reflects against that plane rather than its own - an accepted approximation, since only one plane can be mirrored per frame. Seen from below — decided per fragment from the face normal (`fromBehind` in `test-fragment.glsl`), never from the sea plane, since a pond stands above it — the surface is a Snell window: the same scene copy shows through it distorted by the wave normal, the inside fresnel goes to total internal reflection past the critical angle and that band fades to the underwater fog colour, and the mirror is never sampled from that side; `ReflectionReuse.MirrorApplies` also skips rendering it whenever `Camera.IsEyeInWater`, and `SetLighting` keeps the clear colour and the sun disc dry in water (lava still fades them, eased through `_lavaT`) so the window shows real sky. The reflection pass now also draws the sun disc and the cloud layer into the mirror, and both it and the scene copies are skipped whenever `CullWater` (one frustum pass a frame, feeding the water pass its draw lists) found no water on screen — glass shares the translucent mesh, so `Chunk.HasWaterMesh` is the flag that decides, not `HasTranslucentMesh`. Opaque faces that look into water are flagged by the mesher in bit 3 of `Vertex.Normal` (bits 0-2 stay the face index) and get the animated Voronoi caustic net. While the eye is submerged, `UnderwaterRaysRenderer` adds one additive full-screen ray-march for sun shafts. Both measure depth from the **local** water surface, never the sea plane: the caustics from the surface baked into each submerged face's vertex (`Vertex.WaterSurface`, read as `fWaterSurfaceY`), the rays from `Camera.EyeWaterSurfaceY` (`ChunkManager.GetFluidColumnTopGlobal` over the eye's column), so a pond above sea level gets both — only the planar mirror still uses the sea plane. The rays are why the shadow map keeps rendering underwater (`_shadowStrengthDry`, the strength before the underwater fade) even though the world diffuse no longer uses it. **Depth convention**: `Camera.GetProjectionMatrix` comes from `System.Numerics`, which is Direct3D-style (clip depth 0..1), so window depth is *not* the GL -1..1 mapping — never write a near/far linearization. Reconstruct a world position by inverting the very matrix that wrote the depth (`uInvViewProj`), and compare raw depth values directly for "which is nearer". `visual-examples/water.json` (run it with `VOXEL_TIME=360`) frames the shallows, the grazing surface, deep water from height and the underwater shots, including the surface seen from below in the sea and in a pond above sea level (a liquid one — a frozen pond has an ice lid, not a water top face).

Shaders (`assets/shaders/**/*.glsl`), textures (`assets/textures/**/*.png`), fonts (`assets/fonts/**/*.ttf`) and all content JSON (`assets/**/*.json` — blocks, items, loot tables, models) are copied to the output directory via `CopyToOutputDirectory` rules in the csproj — **new asset types/extensions need a matching `<None Update>` entry** or they won't be found at runtime. The JSON rule is a wildcard over the whole `assets` tree, so adding a block, item, loot table or model needs no csproj edit.

Assets are opened by **relative** path (`FileUtils.ReadFile("assets/shaders/...")`, `TextureAtlas.Build(gl, "assets/textures/blocks")` in `RenderManager.LoadResources`), so the app must be launched with a working directory that contains `assets/` — either the build output directory or `OpenGLTest/`. `dotnet run --project OpenGLTest/silkgltriangle.csproj` satisfies this (it runs from the project folder); launching the built executable from an arbitrary directory does not.

**Textures and the vertex layout.** Every texture a `Vertex`-layout mesh samples is a
`TEXTURE_2D_ARRAY` (`TextureLoader.Upload`/`UploadArray`): the block atlas has one slice per
`assets/textures/blocks/*.png` in sorted file-name order (`TextureAtlas.TileOf(name)` returns the
slice as a `ushort`, `BlockType.FaceTiles/Top/Bottom/Side` hold it), and item sprites, the font,
the crosshair and the sun are single-slice arrays on layer 0. All eight world/shadow/UI/sun
shaders therefore declare `sampler2DArray` and read `vec3(fTexCoord, float(fLayer))`; there is
no 2D-texture path for meshes. `Vertex` is 32 bytes: position (3 floats), packed rgba, **UV as
two floats in tile units** (0..1 across one tile, 0..W across a quad merged over W cells — the
sampler's REPEAT tiles it, so UVs are never atlas-absolute), sun/block/normal/sway bytes at
24-27, the `ushort Layer` at 28 (attribute location 7, an integer attribute) and the `ushort
WaterSurface` at 30 (attribute location 8, also integer): the baked water surface over a
submerged face in quarter blocks (`Vertex.EncodeWaterSurface`, 0 on every dry face). The mesher
gets it from `Chunk.SubmergedSurfaceCode`, which walks the fluid column in the padded snapshot and
resolves a column that leaves the snapshot from the generated `WaterLevel`; it rides the greedy key
as a 7-bit code in bits 25-31, so a merge never crosses two water surfaces. Baked model
quads (`BakedQuad.Uvs` + `.Tile`) and every procedural emitter go through `Chunk.TiledUv(face,
local, extent)`, the single definition of per-face texture orientation; `CropUv` is its
extent-1 case. Bind these textures with `TextureTarget.Texture2DArray`.
The block and material atlases sample their mip chains (`TextureLoader.UploadArray(...,
sampleMips: true)`: min filter `NearestMipmapLinear` plus `AtlasAnisotropy`, mag stays `Nearest`,
so close blocks keep crisp pixels while distant ground stops sparkling); every other texture stays
plain `Nearest`. Right after `ContentRegistry.BakeModels`, `TextureAtlas.ApplyCutoutMips`
overwrites levels 1..n of every tile a cutout block wears (`BlockType.CutoutTiles`) with a
`CutoutMipmap` chain whose alpha is rescaled per level to keep the coverage at
`WorldGenerationConfig.AlphaCutoff` that level 0 has, so leaf canopies do not thin with distance.

**The material atlas.** `assets/textures/blocks_material/` holds a second `TEXTURE_2D_ARRAY`
(`TextureAtlas.BuildMaterial`, texture unit 6) that is *index-parallel* to the block atlas: slice
`i` describes the same tile as atlas slice `i`, so the world shader's existing `fLayer` addresses
both and nothing new rides the vertex. RGB is a tangent-space normal and A a metallic multiplier;
a tile with no file there gets a flat `(128, 128, 255, 255)` default, which is why only the three
reflective ores need art. **Put normal maps in that sibling folder, never in
`assets/textures/blocks/`** - `TextureAtlas.Load` globs `*.png` unfiltered, so a file dropped in
there becomes an extra atlas slice and renumbers every tile after it.

How reflective each slice is comes from the owning block's `material` JSON, gathered by
`TileMaterials.Build` during `ContentRegistry.BakeModels` into one `vec4` per slice and uploaded as
an SSBO at binding 1 (binding 0 is `ChunkDrawBatch`'s `ChunkInfo`). Material is keyed by tile rather
than by block precisely so the feature stays off the vertex - no new attribute, no wider `Vertex`,
no change to the greedy merge key - at the cost that two blocks sharing a texture share its
material, which `TileMaterials.Build` warns about instead of silently resolving. `uMaterialCount`
is the switch for the whole path and stays 0 wherever the buffer is not bound (the ItemDebugger
loads the same shaders), so an unbound buffer is never read. `test-vertex.glsl` emits the `fTangent`
/ `fBitangent` frame the normal map is read in, and it has to agree with `Chunk.TiledUv`'s per-face
UV orientation or the relief comes out rotated or mirrored per face. Regenerate the art with
`tools/generate-ore-materials.ps1` (deterministic, byte-identical re-runs);
`visual-examples/ores.json` is the screenshot check, and wants a `VOXEL_TIME` since the highlight
tracks the sun.

**Chunk meshing.** `Chunk.GenerateMeshDataFromSnapshot` runs two passes over the padded 18³
block snapshot. The binary greedy pass (`EmitGreedyFaces`, scratch in the `[ThreadStatic]`
`MeshScratch`) takes every block flagged `BlockType.IsGreedy`: a `Model`-renderer block whose
baked model is exactly the six untransformed cube faces (`ContentRegistry.BakesGreedy`; all four
built-in parents qualify, so stone, dirt, grass, logs, TNT, lamps, leaves and glass all do). It
builds per-axis bit columns of greedy and occluding cells, finds visible faces with one
shift-and-mask per column (the halo bits cull border faces exactly like the neighbour read),
keys each face by block id, the four corner AO values, sun and lamp light and the submerged
flag, gathers each key's faces per depth slice into a 16×16 bit plane and merges rectangles —
but only along an axis where the key's AO is constant, so the merged quad's interpolated shade
equals the per-cell shade exactly. Merged quads carry UVs 0..W / 0..H in tile units (`TiledUv`)
and the sampler's REPEAT tiles them. The per-voxel loop then skips greedy cells and emits
everything else as before (tapered wood, pebbles, fluids, any model with cropped or rotated
UVs). `GenerateMeshData(manager, greedy: false)` is the test seam; `GreedyMeshingTests`
rasterises both outputs into unit cell faces and requires them to be equal. Because a merge
never crosses a lighting or AO difference, this world's rough noise terrain and leaf canopies
only give ~15-20 % fewer vertices (`PERF_TODO.md` item 11), not the 5-10× of flat worlds.

**Chunk drawing.** Chunks do not own `Mesh` objects. `Chunk.UpdateMesh` uploads its vertex and
index lists into the two `ChunkGeometryPool`s owned by `RenderManager` (opaque and translucent:
one `BufferStorage` VBO + EBO + VAO each, allocated by `GeometryAllocator`, a first-fit free list
with coalescing in units of 64 vertices / 96 indices — one unit covers both buffers at the same
offset, since quads are exactly 4:6 — growing by doubling plus `CopyBufferSubData`), and keeps a
`GeometrySlice` per list (`OpaqueSlice`/`TranslucentSlice`; `HasMesh`/`HasTranslucentMesh` are
just those being non-null). Every frame each chunk pass — shadow, reflection, opaque
front-to-back, translucent back-to-front — frustum-culls and sorts on the CPU exactly as before,
but instead of a draw call per chunk it appends a `DrawElementsIndirectCommand` plus a
`vec4(origin, fade alpha)` per draw into `ChunkDrawBatch`'s persistent-mapped 3-slot ring (fenced
with `FenceSync`/`ClientWaitSync`, since VSync is off) and issues one
`MultiDrawElementsIndirect`. The chunk vertex shaders are compiled twice from the same source
(`Shader`'s `defines` prefix): the `#define CHUNK_DRAW` variant reads the `ChunkInfo` SSBO at
binding 0 at `uDrawBase + gl_DrawID` and transforms with `uViewProj`, while the plain variant
keeps `uModel`/`uMVP`/`uAlpha` for LOD, drops, TNT, falling blocks, trees and block pops
(`SetDrawUniforms`), and takes its light per draw as `uEntitySun`/`uEntityBlock`, sampled from the
world at the entity's cell by `EntityLight.Sample` (each chunk keeps its mesh-time light snapshot
for that) and passed through `SetDrawUniforms`'s required `EntityLightSample` argument. Per-draw fade alpha reaches the fragment stage as the `flat` varying
`fAlpha`, because a uniform cannot vary between the sub-draws of one multi-draw. The wave and
sway blocks of `test-vertex.glsl` and `shadow-vertex.glsl` must stay byte-identical — a
`TO_WORLD(p)` macro is what lets them be, and `ChunkDrawShaderTests` guards it.

## Layout

```
OpenGLTest/
  src/
    Program.cs                 # entry point, wires window callbacks, input polling
    World.cs                   # owns Camera, ChunkManager, ChunkGenerationWorker, LodManager; raycast block edit API
    Chunk.cs                   # 16^3 block storage (byte IDs), block/column fill + cave carving via WorldGen/, mesh data generation (greedy pass + per-quad emitters) + AO
    MeshScratch.cs             # per-thread scratch buffers of the binary greedy mesh pass
    CutoutMipmap.cs            # GL-free coverage-preserving mip chain for cutout tiles (alpha rescaled per level)
    LightFlicker.cs            # GL-free per-cell flicker phase + curve for the campfire point lights
    SelectionOutlineMesh.cs    # GL-free selection outline: the 12 box edges as thin beams (no glLineWidth)
    ChunkGenerationWorker.cs   # background threads creating chunks around the camera
    Camera.cs                  # FPS camera, physics (gravity/jump/collision), DDA raycast
    LodRegion.cs               # far-terrain LOD: region columns meshed at reduced resolution from heightmaps; per-column chunk-Y ranges for burial
    FluidState.cs              # encoding of the per-block state byte for fluid cells (source / falling / level 1-7)
    BlockType.cs               # runtime block record built from JSON, plus the per-id lookup tables
                               #   the mesher and gameplay read (ById, Emission, IsSolid/Occludes/...)
    Mining.cs                  # GL-free mining rules: the tools/requiresTool gate, block damage per
                               #   hit and hit pace shared by the break loop and the tests
    Combat.cs                  # GL-free punch rules: the forward cone (3 blocks, 30° half-angle)
                               #   and entity damage, shared by the punch loop and the tests
    SliceCombo.cs              # the blade's two-phase slice combo: press/queue/advance state
    Placement.cs               # GL-free placement rules: which cell a right-click fills (a stack
                               #   flagged replaceableByPlacement, layered snow, is taken over in
                               #   place) and what a placement may write over
    Multiblock.cs              # GL-free multiblock structure rules: the eight cells of the 2x2x2
                               #   volume, the state byte (facing + offset from the anchor),
                               #   anchor resolution, the integrity check, Convert and Revert
    MultiblockPlacement.cs     # which 2x2x2 volume a right-click meant: the right-down/right-up/
                               #   left-down/left-up search and the facing the opening takes
    ItemType.cs                # pure items (vine, branch, flint): ids handed out after the blocks;
                               #   never placed or meshed, only drops and inventory slots
    WorldGenerationConfig.cs   # all tuning constants (view radius, per-frame budgets, thread counts) and every worldgen knob (seed, ravine, cave, carver)
    MathUtils.cs               # lerp/easing/pulse helpers for animation curves + shared constants
    Mesh.cs, Shader.cs, TextureLoader.cs, FileUtils.cs, FastNoiseLite.cs (vendored, don't reformat)
    Data/                      # Vertex struct (32 B: float pos, rgba, float tile-local UV, sun/block/normal/sway bytes,
                               #   ushort array-texture layer), cube geometry, frustum/plane math, direction tables
    Content/                   # the data-driven content pipeline (see "Content" below)
      ContentRegistry.cs       # loads assets/data + assets/models, assigns byte ids, fills the tables
      BlockDefinition.cs, ItemDefinition.cs, LootTableDefinition.cs, ModelDefinition.cs  # JSON DTOs
      BlockModel.cs, ModelLoader.cs         # Minecraft model format: parent chains, #texture refs
      ModelBaker.cs, BakedModel.cs, BakedQuad.cs   # elements -> atlas-ready quads + full-cube flags
      LootTable.cs, LootContext.cs          # compiled loot tables and the world cell they roll at
      BlockRenderer.cs, BlockBreakAction.cs, AttackPattern.cs, ItemUseAction.cs  # geometry/break/attack/use enums
      WoodShape.cs, SnowShape.cs            # state/neighbour-aware hitbox shapes (tapered wood, layered snow)
      MultiblockShape.cs                    # the 2x2x2 volume's geometry: rotating a point, a
                                            #   direction or a whole baked model to a facing
      KilnRenderer.cs, QuernRenderer.cs, CastingRenderer.cs  # GL-free placement/animation
                                            #   constants tying a station's render pass to its
                                            #   authored model (the kiln opening and its bowl,
                                            #   fire and fuel spots; the mold pocket levels and
                                            #   the cooling dissolve curves)
      SpritePose.cs                         # the face-up pose and size-dependent resting lift
                                            #   shared by every item sprite lying on a station
                                            #   (the cooled cast, kiln fuel)
      MoltenMaterial.cs, MoltenMaterialDefinition.cs  # a molten metal: id, tile, tint
      MeltRecipe.cs, MeltRecipeDefinition.cs          # block/item -> (material, ml)
      CastRecipe.cs, CastRecipeDefinition.cs          # (fired mold, material) -> result + cooling
    Items/                     # the Item class hierarchy ItemFactory hands out per use action
      CrucibleItem.cs          # a vessel whose molten contents ride on its own ItemStack
      CrucibleInventory.cs     # GL-free helper that adds a crucible and hands back its stack
      ConvertMultiblockItem.cs # the converter's right-click: find the volume, convert in place
    Systems/
      WindowManager.cs         # Silk.NET window setup
      InputManager.cs          # keyboard/mouse context
      RenderManager.cs         # GL state, world/selection/UI passes, viewport
                               #   a depth-only directional shadow pass (RenderShadows) into its
                               #   own FBO runs first each frame, and SampleShadow in
                               #   test-fragment.glsl consumes that depth texture on unit 1
                               #   the sun disc (assets/textures/environment/sun.png) is drawn
                               #   additively right after the clear (RenderSun), depth off, so
                               #   the terrain that follows occludes it
                               #   CopyScene snapshots the frame's colour/depth to units 3/4
                               #   for the water pass's refraction (see Water rendering above)
                               #   owns the two ChunkGeometryPools, the ChunkDrawBatch and the
                               #   CHUNK_DRAW variants of the world/shadow programs; the four
                               #   chunk passes are one MultiDrawElementsIndirect each
                               #   the station passes ride inside entityPass: RenderQuerns,
                               #   RenderKilns (crucible mesh, molten pool, the last charged
                               #   input hovering over the rim and shrinking into it, stored
                               #   fuel lying flat) and RenderCastings (the pouring liquid
                               #   dissolving into the cast, drawn at CastingRenderer.ResultSize
                               #   — the pocket's width — not drop size)
      UnderwaterRaysRenderer.cs # additive full-screen sun shafts while the eye is submerged
      ChunkGeometryPool.cs     # the two shared chunk vertex/index buffers (opaque, translucent):
                               #   BufferStorage VBO+EBO+VAO, NamedBufferSubData uploads, grow by copy
      GeometryAllocator.cs     # GL-free first-fit free list with coalescing, in units of
                               #   64 vertices / 96 indices; hands out the pools' GeometrySlices
      ChunkDrawBatch.cs        # persistent-mapped 3-slot ring of DrawElementsIndirectCommands +
                               #   the vec4(origin, alpha) ChunkInfo SSBO read via gl_DrawID
      ChunkManager.cs          # chunk lifecycle: load/unload, dirty tracking, mesh scheduling
      LodManager.cs            # LOD region lifecycle: worker thread builds, main thread uploads/unloads, burial flags
      FluidManager.cs          # the fluid tick: scheduled cells, level recompute + spread, batched remeshes
      KilnManager.cs           # kiln block entities keyed by anchor: fuel, ignition, the crucible
                               #   slot and its melt loop, plus the multiblock integrity sweep
      CastingManager.cs        # one cast per mold cell: pour from a held crucible, cool, collect
    WorldGen/
      TerrainGenerator.cs      # the 2D half: owns every 2D noise channel and the biome split; Compute -> ColumnData
      Biome.cs                 # the Forest / Desert / Ocean / Taiga / Frozen enum
      ColumnData.cs            # per-chunk-column arrays: Heights (ravines cut in), DirtDepth, EntranceWeight,
                               #   CaveIntensity, Biomes, Bed (pre-ravine profile: decides wetness)
      CactusGenerator.cs       # desert saguaros: tapered stem + pitchfork arms, no leaves (wood.family "cactus")
      SnowGenerator.cs         # cold weather: freezes surface water to ice, lays one layered-snow cell per cold column
      IceSpikeGenerator.cs     # rare single-column ice spikes on the barren frozen plains
      NoiseCaves.cs            # 3D-noise caves (cheese rooms, wide/narrow spaghetti, deep caverns) on a strided lattice
      WormCarver.cs            # worm-carved tunnels: per-XZ-cell deterministic paths, cached, carved sphere by sphere
      LavaGenerator.cs         # deep lava pockets: cave floors at/below LavaMaxY pond over inside patch mask + pool roll
      CaveNode.cs, Worm.cs     # one tunnel sphere (center + radius); a worm's node array plus its AABB
      SplitMix64.cs            # deterministic integer-hash PRNG so worms don't depend on System.Random internals
  assets/shaders/, assets/textures/   # GLSL shaders; block tiles (loaded into a TEXTURE_2D_ARRAY,
                                      #   one slice per file, at startup), item sprites, crosshair
  assets/fonts/                       # TTF for the UI text
  assets/data/blocks/<name>.json      # one file per block; the file name is its string id
  assets/data/items/<name>.json       # vine, branch, flint, bucket, water_bucket, lava_bucket
  assets/data/loot/<name>.json        # stone, wood, leaves, pebble
  assets/data/molten/<name>.json      # molten materials (copper): tile + tint the liquid is drawn with
  assets/data/recipes/melt/<in>.json  # what a block or item melts into, and how many ml
  assets/data/recipes/cast/<out>.json # (fired mold + material) -> result, amount and cooling time
  assets/models/block/<name>.json     # Minecraft Java block models, incl. the built-in parents
                                      #   cube, cube_all, cube_column, cube_bottom_top
```

## Content: blocks, items, models and loot are data

Nothing about a block is hardcoded any more. `ContentRegistry` reads `assets/data` and
`assets/models` at startup, hands out byte ids and publishes read-only tables that the rest of
the engine indexes by id. **Adding a block means adding JSON, not editing C#.**

Ids are `byte` and chunk storage is a `byte[]`: id 0 is air (it has no definition file, and a
block may not be named `air`), then blocks sorted by file name, then items sorted by file name.
Blocks and items therefore share 255 ids and the registry throws if they overflow. Code that has
to name a specific block or item asks for it by file name — `ContentRegistry.RequireBlock("stone")`,
`ContentRegistry.RequireItem("bucket")` — which throws naming the missing content at startup
instead of silently mis-indexing. Hot paths must not do that per voxel: worldgen and the LOD
mesher resolve their palette once in the owning feature's static constructor
(`TerrainGenerator.SurfaceBlock/SoilBlock/RockBlock/SeaBlock`, `TreeGenerator.LeavesBlock/SpruceLeavesBlock`,
`PebbleGenerator.PebbleBlock`) and everything else reads those bytes. Best of all is not naming
a block: when the question is "what does this block do", add a data flag and read it (see the
convention below). Every content error names the file it came from.

### Startup order (`RenderManager.LoadResources`)

1. `ContentRegistry.Load("assets")` - parses items, loot tables, blocks and every model they
   reference, and assigns ids. No GL, no atlas.
2. `TextureAtlas.Build(gl, "assets/textures/blocks")` - loads the tiles into the array texture.
3. `ContentRegistry.BakeModels()` - bakes each block's model into atlas-ready quads and settles
   the defaults that need to know whether the result is a full cube (`occludes`, `light.passes`).
4. Shaders, `BlockCubeCache`, `ItemMeshCache`, `UIRenderer`, then the `World`.

The `World` (and with it every worker thread) is created last, so all content tables are
immutable by the time anything but the main thread can see them. Keep it that way.

### Block schema (`assets/data/blocks/<name>.json`)

Every field is optional except `model`. Parsing is case-insensitive and tolerates comments and
trailing commas.

| field | default | meaning |
|---|---|---|
| `model` | required | a `"block/cube_all"`-style path under `assets/models/`, **or** an inline model object with the same schema |
| `name` | file name | display name for inventory rows and preview headings (`BlockType.DisplayName`) |
| `hardness` | 5 | float damage the block absorbs before breaking; each hit deals the held tool's power (`Item.ToolPower`, bare hand 1), so 5 with a power-1 tool is five hits; `0` breaks on first contact (pebble). See `Mining` |
| `requiresTool` | false | when true, a hit only damages the block if the held item belongs to one of the `tools` tags at or above the required power; the wrong kind or a lower tier deals 0 damage and no progress. When false, any tool or bare hand mines it and `tools` is ignored |
| `tools` | none | tag name to minimum tool power, e.g. `{"pickaxe": 1.0}` admits any item listed in the `pickaxe` tag (`assets/data/tags/pickaxe.json`) whose `tool.power` is at least 1.0; `0` admits any member. Only read while `requiresTool` is true; a tag no item provides warns yellow at startup |
| `solid` | true | collides with bodies (`BlockType.IsSolid`) |
| `movementSpeed` | 0 | how fast the player moves through the block, 0..1: `0` (default) is a solid collider, a positive value makes it non-blocking for the player and scales their speed while the body overlaps it (`1` = full speed), so leaves at `0.75` are waded through at 75% speed; never set on fluids |
| `friction` | 0.99 | grip of the block's top face, 0..1: `1` is instant response, lower slides more (ground response time grows with `1 - friction`); ice sets `0.95`. Only read from the solid block under the feet |
| `pebbleWeight` | 0 | density multiplier (0..1) for the natural pebble scatter (`PebbleGenerator`) when this block is the exposed surface under the pebble cell: `1` keeps the patch's full density (stone, gravel), a lower value thins the scatter (grass `0.3`), and `0` (the default, e.g. sand and sandstone) means the surface grows no rock piles |
| `occludes` | full cube && !cutout | hides a neighbouring full cube's face (`OccludesNeighbours`) |
| `cullSelf` | false | also cull faces against a neighbour of the same block (glass) |
| `castsAO` | `solid` | darkens neighbouring faces (`CastsAmbientOcclusion`) |
| `cutout` | false | alpha-tested texture; forces `occludes` off whatever the file says |
| `translucent` | false | drawn blended in the sorted translucent pass with the fluids (glass); forces occludes off; never casts a shadow |
| `sway` | false | leaf-wind sway in the vertex shader |
| `material` | none | reflective surface tuning for the ores: `metallic` (0..1, default 0 - an ordinary diffuse block), `roughness` (0..1, default 1, lower being a tighter highlight) and `normalStrength` (0..4, default 1, scaling the tile's normal map). Omitting the object costs nothing at render time. Keyed by *texture*, not by block - see the material atlas below |
| `light.emission` | 0 | 0..15 (`LightEmission`) |
| `light.passes` | `!fullCube` | whether voxel light travels through the cell (`PassesLight`) |
| `renderer` | `"model"` | `model` (baked quads), `tapered_wood`, `pebbles`, `branch_pile`, `snow_layers`, `fluid` or `quern` (procedural emitters), `multiblock` (the anchor cell of a structure, carrying the whole volume's pre-rotated model) or `multiblock_part` (one of its other cells, solid and occluding but never meshed); a fluid's or layer block's model only supplies its tile |
| `multiblock` | none | required on, and only legal with, `renderer: "multiblock"`: `{"part": "block/<name>", "source": "block/<name>"}` — the block filling the other seven cells (which must use `multiblock_part`) and the plain block the volume is built from and reverts to. The anchor and its part must both set `hasItem: false` (a structure is built, never held), and `cutout`/`translucent`/`fluid` are rejected, since both halves are ordinary opaque solids to everything but the mesher. Both default `occludes` and `light.passes` to **true**: the structure's surface really does cover its neighbours, and its quads carry no cullface and are lit from the anchor's own cell, so light has to reach that cell or the whole thing renders black. The model is authored in 0..32 model units (the 2x2x2 volume) with the opening on **north**, no `cullface` on any face and no face covering a whole cell plane — see `### Multiblocks` below |
| `kiln` | false | marks a multiblock anchor as a kiln, which is what gives it `KilnManager`'s fuel, ignition, crucible slot and integrity sweep. Only legal on the `multiblock` renderer |
| `wood.taperLevel` | none | 0 = the family's base, each step up narrows toward its tip; each family's levels must be covered contiguously from 0 (tree 0..5, cactus 0..3, spruce 0) |
| `wood.halfWidth` | 0.5 | half edge length of the tapered box, in blocks (`HalfWidth`) |
| `wood.family` | `"tree"` | which taper family this level belongs to; wood of two families never welds. `"cactus"` is the 4-level saguaro set, `"spruce"` the taiga conifer's single full-cube trunk |
| `stackMax` | 0 | units that stack in one cell (pebble piles 3, branch piles 4, snow layers 16): the count lives in the chunk state byte, 0 = untouched worldgen (the generator's own default), 1..`stackMax` an explicit stack. 2..16 |
| `stackSolidAt` | 0 | stack count at which a stack block turns solid, for a block whose partial stacks are walked through (layered snow at 16). Only legal from 1..`stackMax` on a `solid: false` stack block |
| `hasItem` | true | enters the blocks pool and can be hotbarred; false for taper wood and pebbles |
| `needsSupport` | false | pops, rolling its loot, when the block below is removed; also makes the block replaceable, so a flow washes it away |
| `replaceableByPlacement` | false | a block placement aimed at this block fills the block's own cell and discards it, instead of filling the cell across the hit face, so a block placed against a partial snow drift rests on the ground instead of hovering a layer above it. Only fires while the cell is not solid, so a full snow stack stays an ordinary block, and the displaced block drops nothing. Independent of `needsSupport`, which is what a flow or a bucket may overwrite. Illegal on an always-solid block, which could never honour it |
| `falls` | false | gravity-affected (sand, gravel): topples into a `FallingBlock` entity when the cell below stops being solid, sinks through water, slides off lone supports; never combines with `fluid`/`needsSupport` |
| `fluid` | false | the block is a flowing fluid (water, lava): forces `solid`, `castsAO`, `hasItem`, `needsSupport` off, `occludes` off and `light.passes` on, and drops nothing whatever the file says |
| `flow` | water behaviour | per-fluid tuning, only legal with `fluid: true`: `tickDivisor` (ticks between updates, lava `2` = half water speed), `range` (sideways spread 1..7, lava `3`), `infinite` (adjacent sources birth new ones, lava `false`), and the `hardensTo` + `hardensTouching` pair (lava hardens to `cobblestone` on touching `block/water`) |
| `onBreak` | `"drop"` | `drop` (roll loot) or `prime_tnt` (no drop, spawns a `PrimedTnt`) |
| `loot` | the table sharing this block's name, else drop self | loot table name; `"none"` drops nothing |
| `sfx` | `hit_default` | sound group played while the block is mined, e.g. `hit_rock` (stone family) or `hit_wood` (wood family). A group is every `<group>.wav` / `<group>_N.wav` directly under `assets/sfx`, and one variant is picked at random per hit (`SfxManager`). An unknown group warns yellow and uses the default |
| `hitbox` | full cube | selection boxes in cell-local 0..1 space: one `{"min": [x,y,z], "max": [x,y,z]}` object or an array of them; the outline draws all, the raycast only targets through one |
| `collidesByHitbox` | false | the body collides with the declared `hitbox` boxes instead of the full cell, like tapered wood (a 0.25 slab is stepped onto and stood on; `Camera.IsColliding` -> `WoodShape.OverlapsShape`); requires `hitbox`, `solid: true`, no `stackMax`, not a fluid. Player body only: drops, slimes and falling blocks still see the full cell |

Two traps worth repeating: a **model that is not a full cube needs `light.passes: true`** (which
is its default) or its own cell stays dark and the interior quads render black; and a **cutout
block must not occlude**, which the registry enforces rather than trusting the file.

Partial-block textures are **cropped, never stretched**: the chunk mesher samples `FaceTiles`
crops (`Chunk.TiledUv`/`CropUv`), and the mining pop draws the hitboxes the same way
(`PopBoxEmitter`), so a new partial block needs no pop work as long as its `FaceTiles` are
sensible (the baker falls back to the first quad's tile).

### Multiblocks

A multiblock is a **2x2x2 volume of ordinary blocks** (`Multiblock.Size`, defined once by
`MultiblockShape.Size`), not an entity: the **anchor** — always the volume's minimum corner on
every axis — holds the `multiblock` block and draws the whole structure, and the other seven
cells hold its `multiblock_part`, which collides, occludes and raycasts but is never meshed.
Everything else in the engine keeps seeing eight plain solid cells.

The structure lives in the **per-block state byte**: `Multiblock.EncodeState(facing, dx, dy, dz)`
packs `facing | dx<<2 | dy<<3 | dz<<4`, so the facing sits in the low two bits of *every* cell
(the anchor's offset is 0,0,0, leaving just the facing) and one state read resolves any cell to
its anchor — `Multiblock.TryResolveAnchor` subtracts the offset and then checks the candidate
really is an anchor that owns this part id and agrees on the facing, so an orphaned part left by
a blast reports false instead of pointing at a stranger. `facing` is the engine face index of the
opening's outward normal (north -Z = 0, south +Z = 1, west -X = 2, east +X = 3), and the anchor's
model is baked **four times** at startup into `BlockType.RotatedModels` (`MultiblockShape.Rotate`,
a quarter turn about the vertical axis through `(1, y, 1)`, which maps the volume onto itself),
so the mesher picks a pre-rotated model instead of rotating anything per frame. That rotation is
also why a multiblock face may carry no `cullface` and may not cover a whole cell plane
(`ContentRegistry.ValidateMultiblockBake`): its quads are culled against nothing and the
per-corner AO path indexes neighbour deltas by `Chunk.CubeFaces` corner order, which rotation
breaks, so a multiblock shades flat on every facing.

**Conversion** is a right-click with an item whose `use.action` is `convert_multiblock`
(`ConvertMultiblockItem` -> `World.TryConvertMultiblock` -> `MultiblockPlacement.TryFindVolume`).
The clicked cell is one corner and the volume always extends **into** the face that was hit, away
from the player; which of the four volumes containing that corner is taken is decided in screen
terms in the order **right-down, right-up, left-down, left-up** (`CandidateOrder`), where
`right = Cross(screenUp, hitNormal)`. The first candidate whose eight cells are all the source
block wins, so building a neat cube and clicking any of its front cells converts that cube. The
opening ends up facing the player: on a side face that is the hit normal itself, and on a top or
bottom face (where the normal says nothing about where the player stands) it is the player's own
horizontal view direction, reversed.

**Mining any of the eight cells** reverts the rest (`World.RemoveBlock` -> `RevertMultiblock` ->
`Multiblock.Revert`): the mined cell is cleared and rolls the structure's own loot table (the
kiln's yields the one `mud_bricks` that cell was built from), and every other cell still holding
the anchor or part id becomes the source block again with a **zeroed** state byte.
Both `Convert` and `Revert` write through `Chunk.SetBlockRaw` and only collect the chunk
positions they touched, because the remesh follows the `FellWood` batch idiom — one
`ChunkManager.ImmediateRemeshWithBorders` on the clicked or mined cell's chunk so the click frame
already shows the change, and `Chunk.RemeshAfterBatchEdit` (the asynchronous `MarkAreaDirty`
rebuild a felled tree and a TNT crater already use) on the rest — never a loop of eight immediate
remeshes.

Nothing but mining goes through `RemoveBlock`, so a **blast** would otherwise leave the rest of a
structure standing as invisible solid blocks. `KilnManager.Update` doubles as the integrity sweep
for that: it re-checks `Multiblock.IsIntact` every frame, freezes while any cell is in an unloaded
chunk (`anyUnloaded`, so a half-loaded structure is never mangled) and otherwise reverts,
including when the *anchor* cell itself is gone — which is what `KilnState.AnchorId` remembers.
**Only kilns get that sweep**: a future multiblock that is not a kiln needs its own registry, or a
generic one.

### Item schema (`assets/data/items/<name>.json`)

```json
{ "name": "Flint", "texture": "assets/textures/items/flint.png" }
```

`name` and `texture` are all a plain pickup needs. An optional `use` object gives the item a
right-click behaviour (`World.TryUseSelected` -> `TryUseItem`):

```json
{ "name": "Bucket", "texture": "assets/textures/items/bucket.png",
  "use": { "action": "pickup_fluid", "fluid": "block/water", "result": "item/water_bucket" } }
```

| field | meaning |
|---|---|
| `use.action` | `pickup_fluid` (scoop the targeted fluid source into the item), `place_fluid` (pour one into the targeted cell), `place_stack` (build a stack block up), `ignite_campfire` (strike a branch pile, or a kiln that has fuel) or `convert_multiblock` (turn a 2x2x2 volume into a structure) |
| `use.block` | for `convert_multiblock`, the **source** block the volume must be built from (`block/mud_bricks`); for `place_stack`, the stack block it builds |
| `use.result` (`convert_multiblock`) | the multiblock **anchor** block the volume becomes (`block/mud_brick_kiln`). The registry checks at startup that the anchor really is a `multiblock` renderer built from that very source, so a mismatched pair names its file instead of failing in play. The converter itself is spent by nothing and never morphs |
| `use.fluid` | the fluid block the action works on; must be a block with `fluid: true` || `use.result` | the item this one becomes on success; must be an item, and the two normally point at each other |
| `display` | how the item sits in the right hand: `rotation` as `[pitch, yaw, roll]` degrees (composed yaw, then pitch, then roll), `translation` in blocks of hand space, `scale` as a multiplier; omitted means `{"rotation": [-30, 10, 0]}`, i.e. upright over the hand with a slight sideways cant — the pickaxe yaws to 60 so a tip faces forward, the compass uses rotation `[-60, 0, 0]`, scale `0.67` and a lift to face back up at the holder |
| `blockDamage` | damage one left-click hit removes from a block's `hardness`; default 0.5 (the bare-hand baseline). Independent of `tool`, which only gates *whether* the block may be mined |
| `entityDamage` | damage one punch deals to an enemy (a slime); default 1 (the bare-hand baseline). A weapon raises it without needing a `tool` block (the obsidian blade is `6`) |
| `attack` | left-click attack behaviour, an `AttackPattern`: `punch` (default; forward cone, hits the nearest one) or `slice` (forward hemisphere, hits the nearest two, two-phase swing — the obsidian blade). See `Combat`/`SliceCombo` |
| `fuel.burnSeconds` | seconds one unit of this item keeps a kiln lit (coal 60; 1..3600). Writing the object is what makes an item kiln fuel — a kiln takes up to `KilnManager.MaxFuel` units of **one** kind at a time |
| `crucible.capacityMl` | marks the item a **crucible**: a vessel holding this many millilitres of metal, melted and pending together (100..10000). A crucible stores in the **Tools** pool, one instance at a time, because its contents ride on that instance's `ItemStack` as a `CrucibleComponent(MaterialId, MoltenMl, PendingMl, PendingInputId)` — the last field is display-only, the last melt input charged in, so the kiln can draw what is waiting to melt; while the vessel stands in a kiln the same struct lives in `KilnState` instead, and taking it out writes it back onto a fresh instance (`CrucibleInventory.Add`). The stack carries no melt timer, so putting a crucible back into a kiln restarts the partial chunk it was melting |
| `model` | optional `block/<name>` model under `assets/models/`, baked into `Item.BakedModel` so the item can be drawn as real 3D geometry while it sits inside a station (the crucible standing in a kiln's hearth). The sprite stays the inventory icon; this only adds a mesh |
| `tool` | optional mining numbers: `power` (the item's mining *tier* — the number a block's `tools` requirement is compared against, default 0.5, the bare-hand baseline; it is **not** a damage number, damage is `blockDamage`) and `hitDelayMs` (milliseconds between hits). Writing the object marks the item as a tool; which block `tools` tags it satisfies comes from the tag files, not the item — `assets/data/tags/pickaxe.json` listing `item/primitive_pickaxe` is what makes a block gating on `"pickaxe"` accept it (`Mining.CanMine`). Bare hand and non-tool items mine at the defaults, and creative ignores the gate |

Both names resolve like a loot entry (`block/` / `item/` prefixes pin the namespace).
`ContentRegistry.ValidateItemUses` runs after the blocks are loaded and throws, naming the file,
if the fluid is not a fluid block or the result is not an item. A successful use goes through
`Inventory.MorphTool`, which turns the tool instance into its result in place, so every hotbar
slot holding that bucket flips with it and scooping and pouring alternate on the same button.
A `pickup_fluid` item scoops the fluid it names into its `result`, but an empty bucket also
scoops any other fluid that has its own full bucket (lava becomes `item/lava_bucket`) — the
pour side is what decides the pairing, so new fluids only need a `place_fluid` item to be scoopable.
`ItemType.TargetsFluids` makes the selection
raycast include fluid sources only while a `pickup_fluid` item is held — otherwise water is
aimed straight through, and mining refuses fluid cells regardless.

### Loot schema (`assets/data/loot/<name>.json`)

```jsonc
{
  "pools": [
    {
      "rolls": 1,                    // int, {"min":1,"max":3}, or {"provider":"pebble_count"}
      "chance": 0.15,                // the whole pool is skipped otherwise; default 1
      "entries": [
        { "item": "cobblestone", "weight": 1, "count": 1 },  // count: int or {min,max}; weight default 1
        { "empty": true, "weight": 3 }                       // weighted nothing
      ]
    }
  ]
}
```

An `item` name resolves against blocks first, then items; a `block/` or `item/` prefix pins it to
one of the two. `leaves.json` therefore has to say `"item/branch"`, because a *block* named
`branch` (the thickest taper step) exists as well. The only roll-count provider is `pebble_count`
(`PebbleGenerator.CountAt(x, z)` at the loot context's cell).

Every destruction path rolls through one place: `DropManager.RollLoot` (a block with no table
drops itself, `LootTable.None` drops nothing) and its spawning wrapper `SpawnLoot`. Mining
(`World.RemoveBlock`), blasts (`TntManager.Detonate`) and felled trees (`TopplingTree.TakeDrops`)
all go through it, so a block's yield never depends on how it came down.

### Model schema (`assets/models/block/<name>.json`)

Minecraft Java block-model format, i.e. what BlockBench exports: `parent` (chained, cycles
rejected), `textures` (`#var` refs, child entries overriding the parent's) and `elements[]` with
`from`/`to` in 0..16, optional `rotation {origin, axis, angle, rescale}`, `shade`, and `faces`
carrying `uv`, `texture`, `cullface` and `rotation`. `tintindex`, `display`, `ambientocclusion`
and `gui_light` are ignored. Texture refs are normalized by stripping `minecraft:` and any
leading `block/` or `blocks/`, then looked up as an atlas tile.

Minecraft's face names map onto the engine's face indices (`Chunk.CubeFaces`) as
`north` = -Z = 0, `south` = +Z = 1, `west` = -X = 2, `east` = +X = 3, `up` = +Y = 4,
`down` = -Y = 5.

Four built-in parents mirror Minecraft's byte for byte, so a BlockBench export referencing them
works unchanged: `cube` (six faces, `#north #south #east #west #up #down`), `cube_all` (all six
`#all`), `cube_column` (`#end` on up/down, `#side` around) and `cube_bottom_top` (`#top`,
`#bottom`, `#side`). `ContentRegistry.BakeModels` self-checks at startup that all four still bake
to six full faces.

### Recipes, cast molds and metalworks

Crafting recipes live in `assets/data/recipes/<name>.json` as `{ "result", "count", "ingredients":
[ { "item" | "tag", "count" } ] }` (DTO `RecipeDefinition.cs`, compiled by
`ContentRegistry.CompileRecipe` into a `CraftingRecipe`), campfire recipes in
`recipes/cook/<name>.json` as `{ "input", "result", "count", "cookSeconds" }`
(`CookRecipeDefinition.cs` -> `CookRecipe`, one recipe per input, enforced) and quern recipes in
`recipes/crush/<name>.json` (`CrushRecipe`, likewise one per input). Every name resolves
like a loot entry, with `block/` and `item/` prefixes pinning the namespace (`TryResolve`), so
blocks and items are interchangeable as ingredients, results, inputs and outputs. Craft files are
named by their result, cook files by their input, and a reverse recipe by
`<result>_from_<ingredient>`.

**Cast molds** are the worked example. `mold_pickaxe` / `mold_axe` are pressed from three mudballs
(`recipes/mold_pickaxe.json`, `mold_axe.json`), uncraft back into three
(`recipes/mudball_from_mold_*.json`) and fire into `fired_mold_pickaxe` / `fired_mold_axe` by
right-clicking a lit branch pile with the mold hotbarred (`World.TryCookOrCollect` ->
`CampfireManager.TryPlaceFood`, `recipes/cook/mold_*.json`); a fired mold never uncrafts. They are all
`hasItem` slab blocks whose models under `assets/models/block/` are generated by
`tools/generate-mold-models.ps1` and never hand-edited: the script reads each sprite's alpha
silhouette (`assets/textures/items/mold_*.png`, copies of the copper tools — silhouette input only,
referenced by no JSON, since a block's icon comes from its baked model via `BlockCubeCache`), maps
it onto the inner 12x12 of the 16-cell top, greedy-merges the rim and pocket cells into box elements
and emits `cullface` only on faces that sit on the cell boundary — a cullface culls against the
*world* neighbour, never a sibling element, so the pocket walls carry none and always draw. Its
constants `$SlabHeight` (4), `$PocketDepth`, `$RimCells` and `$AlphaThreshold` sit at the top of the
file, and re-running is byte-identical until the real sprites arrive. The blocks set
`collidesByHitbox` with a `0..0.25` slab hitbox whose height must equal `$SlabHeight/16` (`MoldTests`
pins that tie, and the quad budget at 300); raw molds wear the `mud` tile, fired ones `fired_mud` —
a `stone.png` placeholder that lives in `assets/textures/blocks/`, never `blocks_material/`.
There are **six** of them now — pickaxe, axe and ingot, raw and fired — and the ingot pair is
generated by the same script from the same constants.

**Molten metal is content, not a fluid.** It never enters `FluidManager`, never occupies a cell
and has no block: `assets/data/molten/<name>.json` declares a `MoltenMaterial` as
`{ "name", "texture", "color": [r, g, b] }`, where `texture` is an atlas tile (copper reuses
`lava_still`) and the colour tints it. Ids are handed out **1-based in file-name order** (0 means
"no material"), and the tile is resolved in `BakeModels`, so `MoltenMaterial.Tile` is 0 until
then. `ContentRegistry.MoltenMaterialByName` / `MoltenMaterialById` are the lookups.

Two recipe kinds hang off that. `recipes/melt/<input>.json` is
`{ "input", "fluid", "amountMl" }` — what a block or item melts into and how much, one recipe per
input (`MeltRecipe`, `ContentRegistry.MeltRecipeForInput`). `recipes/cast/<result>.json` is the
metalworks recipe proper, `{ "mold", "fluid", "amountMl", "coolSeconds", "result", "count" }`,
keyed by the **(fired mold block, material)** pair, which must be unique (`CastRecipe`,
`CastRecipeFor(moldId, materialId)`, and `IsCastMold(id)` for "is this cell a mold at all").
Every shipped copper form — the malachite chunk, crushed copper, the ingot and both tool heads —
has a melt recipe, so **a cast melts back into exactly what it took**: `MetalworksRecipeTests`
asserts that conservation as a relation rather than pinning any ml figure.

**The kiln** (`Systems/KilnManager.cs`, keyed by the structure's anchor cell) is the furnace half.
Right-clicking it with an item declaring `fuel.burnSeconds` stores a unit, up to
`KilnManager.MaxFuel` of **one** kind; a firestarter only lights a kiln that has fuel
(`CanIgnite`), and igniting spends the first unit at once, so four coal burn for four minutes and
then go out. A crucible (crafted from mud bricks) right-clicked in stands in the hearth, and
anything with a melt recipe right-clicked after that
charges the vessel with **pending** metal — gated by `KilnManager.CanAccept`,
which needs room and either an empty vessel or the same material, and needs no fire. A lit kiln
then turns `MeltChunkMl` of that pending charge molten every `MeltSeconds`. Melting **pauses**
rather than resets when the fire dies or the vessel is lifted out: `KilnState.MeltLeft` simply
stops ticking, so relighting resumes the partial chunk. `MeltProgress01` and `DisplayMoltenMl` are
what the render pass reads and neither depends on `Lit`, which is what keeps the drawn pool rising
smoothly between chunks and stops it dropping the instant the last unit burns out.
`DisplayPendingMl` is their conservation complement (drawn pending + drawn molten = `TotalMl`),
and `KilnRenderer.PendingScale01` turns it into the lump of the last input
(`CrucibleComponent.PendingInputId`) hovering over the rim, full-size while a whole unit is solid
and shrinking to nothing as the last unit melts — so the charge is never invisible and never cuts
out. Breaking the structure drops the stored fuel and the crucible at the mined cell — the metal
inside is lost.

**Casting** (`Systems/CastingManager.cs`, one `CastState` per mold cell) is the other half.
Right-clicking a fired mold with a crucible binds that tool instance and pours at
`CastingManager.PourMlPerSecond`, draining the stack a little every frame; the pour **pauses**
(losing nothing) when the crucible is deselected, carried out of `World.InteractionReach`, emptied
or found holding the wrong material, and another right-click resumes it. The frame that completes
the fill starts the cooling instead of cooling in the same frame, so the two phases are never
ambiguous; `Cool01` then runs 0..1 over the recipe's `coolSeconds` and the liquid dissolves into
the result through `CastingRenderer.LiquidAlpha` / `ResultAlpha`, a complementary `MathUtils.SmoothStep`
pair that always sums to 1 (no hard cut at the end of the pour). A finished cast is picked up with
another right-click, and breaking the mold — **creative included** — drops a finished cast and only
a finished cast; metal still cooling is lost. Like the campfire, quern and kiln registries, cast
progress is in-memory only: it does not survive a restart, and an unloaded mold cell freezes
rather than being forgotten.

The right-click chain in `World.TryUseSelected` runs `TryCookOrCollect` -> `TryQuernInteract` ->
`TryKilnInteract` -> `TryCastInteract` -> the item's own `use` action or block placement, and the
first one that claims the click wins. On a kiln: an empty hand lifts the crucible out (and puts it
straight back when the tools pool is full), a crucible stands one in, a melt input charges it, fuel
goes into the hearth, a **firestarter falls through** to its own `Item.OnUse` so it can strike and
spend durability, and a **block in hand falls through** to placement against the structure — any
other click on a kiln is spent. On a mold: a crucible click is spent either way, so a refused pour
never places anything, while a block in hand still falls through and molds can be stacked against
each other.

## Architecture: threading model (the most important thing to preserve)

Chunk work is split across threads, and OpenGL calls are only legal on the main thread:

1. `ChunkGenerationWorker` runs dedicated background threads that scan positions around the camera (closest first) and call `ChunkManager.QueueChunk`, which creates the `Chunk` and generates its block data **on that background thread**.
2. Meshing is scheduled by `ChunkManager.ProcessDirtyChunks` (main thread, per-frame budget `MaxDirtyChunksPerFrame`) and runs on the thread pool via `Task.Run`, throttled by a `SemaphoreSlim` (`MaxConcurrentMeshGenerations`). Mesh generation produces CPU-side `ChunkMeshData` only.
3. Completed mesh data lands in `_completedMeshQueue` and is uploaded to the GPU on the **main thread** in `ApplyCompletedMeshes`, sorted closest-first and capped at `MaxMeshUploadsPerFrame` per frame.

Rules that follow from this:

- **Never call GL functions (buffer uploads, `Mesh` creation/disposal, pool uploads/frees through `ChunkGeometryPool`, shader use) off the main thread.** Background work must only produce plain data handed over via the concurrent queues.
- A chunk is only meshed when its block data is ready **and all 6 cardinal neighbors exist with ready data** (`HasAllNeighbors`) — meshing reads neighbor blocks for face culling and cross-chunk AO. Breaking this reintroduces holes/seams at chunk borders (see git history). That scheduling check can go stale (the mesh task waits on the semaphore and an unload may take a neighbor away meanwhile), so `Chunk.GenerateMeshData` re-resolves the whole 3x3x3 neighbourhood once up front and returns `null` when a face neighbor is gone; callers must then re-mark the chunk dirty rather than apply anything. Only the edge/corner neighbors may be absent, and those read as air (they feed light and fluid corner heights only).
- Shared state uses `ConcurrentDictionary`/`ConcurrentQueue`; `Chunk.IsBlockDataReady` is `volatile`. Keep new shared state lock-free in the same style. `ChunkManager.Chunks` is a `ChunkMap` (`Systems/ChunkMap.cs`) wrapping a `ConcurrentDictionary<long, Chunk>` — it is a wrapper, never a `Dictionary` behind a lock, because chunk-gen workers write it and mesh-build pool workers read it.
- Editing a block on a chunk border must also remesh the affected neighbor chunk(s) — block edits use `ChunkManager.ImmediateRemesh` / `MarkDirty`.
- Performance tuning knobs all live in `WorldGenerationConfig`; change constants there rather than hardcoding budgets elsewhere.
- Animation curves (interpolation, easing, one-shot pulses) belong in `MathUtils`, not inline in gameplay or render code — everything time-driven should be lerped through one of those helpers rather than snapped.

Other invariants:

- World/chunk coordinates: `Chunk.Position` is in chunk units; world pos = chunk pos × `Chunk.Size` (16). Chunks are cubic and stacked vertically too (3D chunk grid, not 2D columns).
- Blocks are `byte` IDs; `0` is air. Everything else about a block comes from `assets/data/blocks/<name>.json` through `ContentRegistry` (see "Content" above), never from a new `switch` on the id. Hot paths read the flat mirrors (`BlockType.IsSolid/Occludes/CastsAO`, `BlockType.Emission`, `BlockType.PassesLightById`) rather than `BlockType.ById[...]`, so keep new per-id flags in that shape.
- **Per-block state byte**: alongside `Chunk._blocks` each chunk has a lazily allocated `volatile byte[]? _state` (null = all zero), read through `Chunk.GetLocalState` / `ChunkManager.GetStateGlobal` / `TryGetCell` and written only by `Chunk.SetBlockRaw` (no remesh, no scheduling — the fluid tick batches those). Three features share it, one per block: **fluid levels** (`FluidState` decodes it as source `0`, falling `8` or flowing level `1..7`), **stack counts** (a `stackMax` block's 0 = untouched worldgen, 1..`stackMax` an explicit pile) and a **multiblock's facing and offset** (`Multiblock.EncodeState`, `facing | dx<<2 | dy<<3 | dz<<4`, written by `Multiblock.Convert` through `SetBlockRaw`). Which meaning a byte carries is decided entirely by the block id that owns the cell, so a new user must pick a block kind, never a spare bit. **Every write that is not one of those zeroes it** (`RemoveBlockAt`, `PlaceBlockAt` — which also treats a fluid cell as replaceable — and `ClearBlockAt`), and `Multiblock.Revert` likewise writes its source block with an explicit `0`, so a stale level, count or offset can never outlive the block that owned it. Meshing gets it as a second padded snapshot from `FillPaddedSnapshot(padded, paddedState, neighbors, neighborStates)`, filled from the neighbourhood `TryResolveNeighbors` resolved for the build; the fluid simulation must read cells through `TryGetCell`/`ChunkAt`, never `GetBlockGlobal`, which reports unloaded chunks as air.
- **Station block entities**: `CampfireManager`, `QuernManager`, `KilnManager` and `CastingManager` are all owned by `World`, all **main thread only** and all in-memory only — a lit campfire, a spinning quern, a kiln's fuel and crucible and a mold's cast progress do not survive a restart or a chunk reload. Each is keyed by the cell (the kiln by its structure's anchor), each is ticked from `World.Update` and each has a `NotifyBlockRemoved` that `World.RemoveBlock` calls so a broken station drops what it held. `World.FireCells` gathers every burning cell (campfires and lit kilns) once a frame and feeds both the fire billboards (`FireParticleSystem.SpawnOffset`) and the warm point lights (`RenderManager.FireLightOffset`), so a new fire source only has to appear in that list. Their render passes — `RenderQuerns`, `RenderKilns`, `RenderCastings` — sit inside the existing `entityPass` profiler span rather than adding sections of their own.
- **Fluid tick**: `Systems/FluidManager.cs` (owned by `World`, main thread only) is a *scheduled-cell* simulation, not a scan. `World.Update` calls it once per frame after `UpdateToppling`; it runs whole ticks every `WorldGenerationConfig.FluidTickSeconds` (0.25 s), at most `MaxFluidTicksPerFrame` of them per frame, and drops the leftover time so a hitch never leaves it spiralling. Each tick takes the set of cells scheduled by the previous one (packed as `long`, 21 bits per axis — the same budget `ChunkKey` (`src/ChunkKey.cs`) uses to pack a **chunk** position for `ChunkMap` and for `ChunkManager`'s dirty/queued/generating/placement dictionaries; `FluidManager`'s packer is block-scale, `ChunkKey`'s is chunk-scale, and neither floors nor truncates differently from `VoxelCoords.ChunkPosOf`), recomputes every **non-source** fluid cell from its neighbours (Minecraft's `getNewLiquid`: two adjacent sources over ground that cannot drain become a source themselves, fluid directly above means falling, otherwise one level thinner than the fullest neighbour, and nothing left means the cell drains to air), then spreads it downwards, or sideways toward the nearest hole within `FluidSlopeSearchDistance` (ties spread evenly). The level rules are per-fluid data (`flow.*` on the block: lava updates every other tick, spreads 3 instead of 7 and never forms new sources). Contact hardening runs before all of it: a fluid cell touching its `hardensTouching` fluid becomes `hardensTo` (lava beside water becomes cobblestone), and a flow settling against a fluid that hardens on its touch crusts over the same way, so water reaching lava also yields cobblestone whichever side ticks first. Spreading only ever writes into non-fluid `IsReplaceable` cells, popping the fragile block it washes away through `DropManager.SpawnLoot`; a cell that already holds fluid recomputes itself on its own tick. Writes go through `Chunk.SetBlockRaw` and dirty only the cell's own chunk plus the face-adjacent chunk on each axis where the cell sits on a border, batched into `MarkDirty` at the end of the tick — not `ImmediateRemesh`, and never `MarkAreaDirty`, because a fluid passes light and casts no AO. A cell reschedules itself and its neighbourhood exactly when it changed or wrote into a neighbour, so settled water costs nothing.
- **Waking worldgen water**: the sea is generated as static source cells, so a cave or entrance carved into a land column beside it would leave a wall of water standing against worldgen air that nothing ever schedules. `FluidManager.WakeLoadedChunks(chunks)` — called from `World.Update` each frame, immediately before the fluid tick — fixes that by scheduling the frontier of its water against a carved cavity when it loads, so the sea pours into whatever breached it exactly like Minecraft water does when you dig into an ocean. Only cells at or below their column's natural terrain top count (`FluidManager.IsCarvedFlowTarget`), so open air above the terrain — a ravine, a beach, a hillside — is never woken and generated water stays static across it. It has two halves: the **interior**, collected on the generation worker at the end of `Chunk.GenerateBlockData` into `Chunk.FluidFrontier` (local block indices of every fluid cell with a `FluidManager.IsCarvedFlowTarget` neighbour in -X/+X/-Z/+Z/-Y — never +Y, since water does not flow upward and every sea-surface cell has air above it; only its own `_blocks` are read, so worldgen stays a pure per-chunk function), and the **borders**, which need both chunks loaded and are therefore scanned on the main thread over the shared 16x16 plane of each face neighbour, scheduling whichever side holds the fluid when the other side is a carved cavity (the vertical faces only ever schedule the fluid above the hole). Chunks entirely above every level they could share with a neighbour (`ColumnData.MaxWaterLevel`, never below `SeaLevel`) skip the border scan — generated water cannot reach them, and player-placed water is woken by the edit paths. The queue of newly generated chunks is `ChunkManager.TryDequeueLoaded`; a consumer must re-check that the chunk is still the live one at its position, because an unload can race the queue. Once the frontier has settled, it costs nothing again. Deep lava pockets wake through the same path: the frontier scan is fluid-generic, and chunks at or below `LavaMaxY` report theirs even when they never held sea water.
- **Every block edit path must call `FluidManager.ScheduleAround(cell)`** (or `ScheduleBox(min, max)` for a batch) or the water beside the edit will never notice it: `World.RemoveBlock`, `World.FellWood` (per cleared cell, and in both single-block branches), `World.TryPlaceBlockFromRaycast`, `TntManager.Detonate` (`ScheduleBox` over the blast box grown by one; the blast also **skips fluid cells**, since a dry crater under water would just refill from every side) and bucket edits through `FluidManager.SetCellImmediate`, which is the only fluid write that remeshes synchronously. Mining never takes a fluid out: `RemoveBlock` returns early on `BlockType.IsFluid`, so only a bucket removes fluids.
- **Gravity**: `Systems/FallingBlockManager.cs` (owned by `World`, main thread only) topples `falls` blocks into `FallingBlock` entities and writes rested cubes back as blocks. Every edit path that can unsupport one must call `TryTopple` above it (`World.RemoveBlock`, `World.FellWood` per cleared cell, `World.TryPlaceBlockFromRaycast` at the placed cell, `TntManager.Detonate` via `CheckColumn` bottom-up over the blast box); fluids never count as support, so fluid edits need no hook. A landing cube slides with chance (8 - solid neighbours of its support) / 8 toward a random clear side, corners included (a corner needs its two flanking sides and the three cells above it clear), committing to its direction across landings, and drops straight once past the edge. Sand stacks on sand: a falls-block support never slides, so towers rebuild. A calm landing converts at once so a collapsing tower settles bottom-up instead of interpenetrating, and a cube that would rest over air pops out as a drop instead of floating. A live cube owns its cell: `TryPlaceBlockFromRaycast` refuses placements into one, cubes shove the player aside one-way (`Camera.PushOutOf`, never through walls, never while flying), and a cube motionless-but-unsettled for 5 s breaks into its usual loot instead of hovering. Shorelines generate from sand (`TerrainGenerator.IsBeachHeight`, gravel-capped where `IsGravelPatch`); the far LOD wears sand and gravel there too, the gravel read with one noise eval per cell.
- Worldgen must stay deterministic and thread-safe (`FastNoiseLite.GetNoise` is read-only after setup; noise instances are configured once in static constructors and never re-tuned per call). All noise seeds derive from `WorldGenerationConfig.Seed` plus fixed per-channel offsets (`TerrainGenerator.MakeNoise`). Before player edits, terrain obeys:
  - **Nothing generated is ever solid above `height(x,z)`** — heights come from `Chunk.GetColumnData(...).Heights` (or the `GetColumnHeights` wrapper) and already include ravines, which are cut into the heightmap so the far LOD sees them too. The ravine cut fades out across the shore band (`OceanShoreWeight`..0.5), so it never slices the ocean's edge; the block at `height` is solid unless a cave or entrance carved it.
  - Both carvers only ever **remove** blocks: `Chunk.GenerateBlockData` fills the columns (grass at depth 0, dirt down to `DirtDepth`, stone below), then `NoiseCaves.Carve` and `WormCarver.Carve` clear air out of the result and `_solidCount` is recounted.
  - **Water** fills every air cell up to the column's own level in a **wet** column, in `Chunk.FillWater` after both carvers and before trees and pebbles: an ocean floor, or a lake or river bed, where a hashed lake bowl or a warped river ridge channel carves the ground below it. There is **no single water plane**: `ColumnData.WaterLevel` (`TerrainGenerator.WaterLevelAt`) is sea level for the ocean and for rivers, but a lake's **own** surface, so a pond rests in the ground it was dug into instead of at the bottom of a crater dug to sea level. A lake takes that surface from its own rim (`TerrainGenerator.TryLakeSite`): the lowest ground on the rim minus `LakeBankDepth`, and a candidate whose rim climbs more than `LakeSiteFlatness` is dropped rather than dug into a hillside, which is what keeps every bank it floods against above the water. Its radius (skewed small by `LakeRadiusSkew`), depth and outline (wobbled by `LakeShapeNoise`, so a shoreline is ragged rather than a circle) all vary per lake. `TerrainGenerator.IsWaterColumn` is now just `height < WaterLevelAt(x, z)`, so the carve in `Compute` and the fill agree by construction, and the far LOD draws its sheet at the same per-cell level. Wetness reads the column's `ColumnData.Bed` — the profile before a ravine cut it — not the ravine-cut `Heights`, so a ravine that merely deepens an otherwise dry column holds no water instead of flooding as a narrow spike, while a ravine under a real lake or river just deepens that body (and the cut fades out across the shore band, so no ravine ever cuts the ocean floor). Every other below-level basin is left **dry** — the forest is not flooded wholesale now that ocean carries the bulk of the water — and a desert stays dry; sand and gravel ring actual water only (`TerrainGenerator.SurfaceWaterWeight` gates the height band), never a dry ravine floor in the band. Both the carve and the flood test are scaled by `TerrainGenerator.SurfaceWaterGate` (`WaterDesertFadeStart`/`WaterDesertFadeEnd`), which fades the body out over the last stretch before the desert, so a pond closes on its own forest rim instead of ending in a vertical wall where the hard forest/desert classification flips, while lakes away from a desert keep their full depth. Sea-floor caves still flood, nothing grows in water (wet columns top out in dirt instead of grass), and `IsSubmerged(height)` is only the waterline height test, since wetness is a per-column level now. Generated water is always a **source** (state `0`). A cold column's top water cell is then replaced by `ice` in `SnowGenerator.Stamp` (see Biomes), so a cold ocean or lake freezes into a walkable sheet. `WaterEnabled = false` turns the water off; `LakesAndRiversEnabled = false` leaves oceans only.
  - **Lava pockets** pond over dry cave floors at/below `LavaMaxY` (`LavaGenerator.Stamp`, after the sea so flooded caves stay water): a patch mask plus a per-cell roll, buried under the terrain so ravines and entrances stay dry. Generated lava is likewise all sources; set `LavaEnabled = false` to turn it off.
  - Caves fade out within ~`CaveSurfaceFadeEnd` blocks of the surface (`WormSurfaceMargin` for worms), except inside entrance zones — sparse patches where `ColumnData.EntranceWeight` lifts the fade and caves reach the sky. The entrance weight fades out before the shore band (`EntranceShoreFade`, see `OceanShoreWeight`), so a cave never breaches a beach or the sea floor into a water-filled hole.
  - Worldgen is a **pure function of world position**: each chunk generates its own blocks with no cross-chunk writes, so neighbours agree at their borders without shared state. Worm paths are cached per 64-block XZ cell in `WormCarver` exactly the way column data is cached per chunk column in `Chunk.GetColumnData`, and so are the three worldgen shape caches: tree shapes (`TreeGenerator.ShapeCache`, shared between `Stamp` on the generation workers and the far-LOD canopy sweep), canopy tops (`TreeGenerator.CanopyCache`, whose one engine caller is `LodRegion.BuildMeshData`, once per *fine* column of a region) and cactus stems (`CactusGenerator.ShapeCache`, whose one caller is `CactusGenerator.Stamp`). All five go through `GenerationalCache<TKey, TValue>` (`WorldGen/GenerationalCache.cs`): the cache is split into G generation dictionaries published as one immutable node swapped by `Interlocked.CompareExchange`, a read probes the newest first, a miss computes into the newest, and when the newest fills its `capacity / G` insert budget the node rotates and the *oldest* generation is dropped — so eviction retains the most recently used generations instead of clearing wholesale, and an entry read while it sits in the oldest generation is promoted forward (a second chance). Entries are deterministic, so a race, an eviction or a duplicate yields identical values, and the worst an evicted entry can ever cost is a recompute. Capacities and generation counts all live in `WorldGenerationConfig`, one pair per site: `ColumnCacheCapacity`/`ColumnCacheGenerations`, `CellCacheCapacity`/`CellCacheGenerations`, `TreeShapeCacheCapacity`/`TreeShapeCacheGenerations`, `TreeCanopyCacheCapacity`/`TreeCanopyCacheGenerations` and `CactusShapeCacheCapacity`/`CactusShapeCacheGenerations` — every site ships at 4 generations, and that count is a re-pricing lever that may only be moved against a real capture, never blind. The factory a site passes to `GetOrAdd` must be a `static`, non-capturing delegate *field* (`Chunk.ComputeColumn`, `WormCarver.BuildCellFor`, `TreeGenerator.BuildShapeFor`), because the factory takes the key alone: a lambda written at the call site that captured locals would allocate a closure on every call, hits included, on the generation workers — recomputing those values from the key on a miss is the cheaper trade. **Standing rule: only values that are a pure function of the key may live in that type.** State carrying *player edits* must never be moved into it, because there eviction is data loss rather than a recompute — `LodRegion.ColumnCaps` (mined-column LOD caps) and `TreeGenerator.CanopyDisturbed` (felled-canopy markers) are the two current examples, and both are tracked as their own items rather than converted.
  - 3D cave noise is sampled on a `CaveNoiseStride` lattice (aligned to world coordinates so adjacent chunks share boundary samples) and trilinearly interpolated per block before thresholding.
- **Far-terrain LOD**: full-detail chunks load to `ChunkRadius` (16 chunks = 256 blocks); a cascade of LOD rings defined in `LodRegion.Levels` extends the render distance (level 1: 2-block cells to 512, level 2: 4-block cells to 1024, level 3: 8-block cells to 2048 blocks). Regions are columns meshed at reduced resolution straight from the heightmap cache (no block data). Each level's heights are eroded (min over a widened fine-column window) and biased downward so its mesh sits strictly inside real terrain *and* strictly below the next finer level — finer geometry always buries coarser via the depth buffer, so no masking or camera-driven remeshing exists. Because those shells are stacked, only one level may actually be drawn per area — otherwise they are all visible once the camera is inside the terrain — so `LodManager.UpdateBuriedFlags` marks each region whose footprint is fully covered by *loaded* finer-level regions (region grids nest exactly: one region = ratio×ratio regions of the finer level) and `RenderLod` skips those. Testing loaded regions rather than each level's radius means an unbuilt finer region can never open a hole in the horizon. The finest level (0) has no finer level to hide it, so it is additionally marked buried wherever the full-detail chunks its shell could show through are loaded *and* meshed (`LodManager.IsCoveredByChunks`, using the per-column inclusive chunk-Y ranges recorded at region build in `LodRegion.ColumnCyMin/ColumnCyMax` and `Chunk.IsMeshed`, which unlike `HasMesh` is true for all-air and fully enclosed chunks too). This is needed because caves cut into the band the shell occupies and would otherwise show it from inside; presence is tested per chunk rather than by horizontal distance because the load volume is a sphere. LOD side faces use grass at the top cell, dirt above `NominalDirtDepth` and the stone tile below it, so far cliffs match near ones; a desert cell wears sand over sandstone and a sea-floor cell the sand/gravel/stone band its depth picks (`LodRegion.BuildMeshData` reads `Chunk.GetColumnBiomes`). The nesting invariants (`SampleMargin(L+1) >= SampleMargin(L) + CellSize(L) - 1`, `HeightBias(L+1) >= HeightBias(L) + CellSize(L)`) must hold when adding levels, and `Camera.NearPlane`/`FarPlane` are tuned to keep those separations from z-fighting at the outermost ring. LOD draws before chunks, coarsest level first (`RenderManager.RenderLod`). Destroying a block would expose the buried LOD, so removals cap that column's LOD height below the removed block (`LodRegion.LowerColumnBelow`) and affected regions on every level rebuild (`LodManager.MarkEditedColumn`); placements need nothing. Canopy bumps wear the owning trunk's leaf block (`TreeGenerator.TryGetCanopy`/`CanopyLeafBlock`, resolved generically via `BlockType.ById` in `LodRegion.BuildMeshData`), so a new tree species only needs its `LeafBlock` stored in the canopy cache — never a new LOD branch.

## Biomes

Five biomes. **Ocean** is a flooded basin ramping from a sandy shore down to bare stone.
The four land biomes sit on a temperature x humidity grid: **forest** (warm+wet; smooth rolling
grass hills with flat valleys), **desert** (warm+dry; dunes of sand over sandstone), **taiga**
(cold+wet; a snowy spruce wood) and **frozen** (cold+dry; a barren snow plain of bare rock and
occasional ice). Selection is a pure function of (x, z) from low-frequency noise — continentalness
(low = ocean) plus temperature and humidity, which are read **only** while choosing a biome and the
worldgen features that follow from it — in `TerrainGenerator.BiomeWeights`; `TerrainGenerator.BiomeAt`
classifies the result. Desert needs firmly warm air (`DesertMaxCold`) and taiga/frozen firmly cold
air (`ColdBiomeMinCold`), so the band between them is always temperate forest and dunes never spawn
against snow. The weights are smooth 0..1 and `TerrainGenerator.Compute` lerps the height
between the forest, desert-dune and ocean-ramp profiles by them, so a border is a slope, never a
cliff; the cold biomes ride the forest profile (only their surface differs), and a coherent
`BiomeDitherNoise` field (`DitherBiome`) breaks the grass/sand material line into ragged patches
rather than a per-column checkerboard. The ocean biome begins at `OceanShoreWeight`, and `Compute`
fades the land's above-the-waterline clamp out across `[OceanShoreWeight, 0.5]` — clamping only
while the biome was land put a hard step where the coast met the water, so the blended band is
ocean biome and slopes below the waterline into the sea. The ocean ramp falls to
`OceanFloorHeight` with rare warped **trenches** cut in, and its floor wears sand, gravel or stone by
depth (`OceanSandDepth`/`OceanGravelDepth`). The forest floor is a broad swell plus a positive-only
hill field gated by a hill-region mask (`Hill*`/`ForestBaseHeight`), so hills rise in patches with
flats between them. A rare warped **mountain** ridge (`Mountain*`, gated by a low-frequency mask and
faded out near the origin) lifts an occasional range well above the hills; a crest above
`MountainRockHeight` is bare stone (`Chunk.GenerateColumn` reads the stripped soil, the far LOD a
matching rock cap), so it reads as rock rather than another wooded hill. A low-frequency
**openness** mask (`Openness*`) flattens the hills a little and thins the trees toward none, so the
forest alternates between rolling woodland and open grass instead of one uniform canopy
(`TreeGenerator.TryGetTrunk` reads `TerrainGenerator.ForestOpenness`, and a taiga keeps more of its
spruce via `SpruceOpennessRelief`). On land a hashed lake bowl and a warped river ridge carve below
their own water level and flood only those (every land biome but the desert; both fade out toward a
desert so a pond closes on its own rim) — see the Water invariant below.
A desert column is sand over `SandstoneDepth` sandstone blocks, then stone; a frozen column is bare
rock (the snow pass covers it); a ravined (bare-stone) column keeps those rules.

**Cold weather** (`WorldGen/SnowGenerator.cs`) runs last of the surface features and reads
`TerrainGenerator.ColdWeight` (a biome-generation input) so the snow and freeze lines fade in as a
column cools rather than snapping at a biome border. It freezes a cold column's surface water into a
walkable ice sheet (the sea-level cell becomes `ice`) and lays one **layered snow** cell above the
solid ground (or the ice). A snow cell's depth is drawn from `SnowGenerator.DepthAt` when its state
byte is 0 and from the state byte when a player stacks it (`SnowShape`); each layer is 1/16 of a
block, so a full stack is solid and partial layers are walked through (`BlockType.IsSolidCell`).
`WorldGen/IceSpikeGenerator.cs` grows occasional single-column ice spikes on the frozen plains.

`ColumnData.Biomes` carries the per-column classification for the chunk mesher and the far LOD
(`Chunk.GetColumnBiomes`); the LOD samples it per cell and wears the matching tile (snow for a cold
cell, snow-covered ice for a frozen sea sheet — snow tile when snow-covered, else ice, and never
flagged water, since the flag alone selects the fragment-shader water model whatever the tile), so a
distant desert and sea floor agree with the near chunks.
Vegetation is biome-gated: broadleaf trees (`TreeGenerator`, forest), **spruce** conifers
(`TreeGenerator.BuildSpruceShape`, taiga) and flowers (`FlowerGenerator`, forest), and the desert
grows **cacti** (`CactusGenerator`) — a tapered stem that throws a few arms which bend upward into
prongs, no leaves; the frozen plains grow nothing. Slime spawning already asks
`TerrainGenerator.IsForest(x, z)`, which is now a real test.

Spruce is a third **wood family**: a block's JSON may set `wood.family` (default `"tree"`), and
`ContentRegistry.BuildWoodLevels` validates each family's levels contiguously from 0 (tree 6,
cactus 4, spruce 1 — the full-cube trunk). Wood of two families never welds (`WoodShape` checks the
family), which is what lets a cactus or spruce reuse the tapered-wood mesh, its shape-accurate
hitboxes and tree felling (`TopplingTree` floods any wood family). The cactus blocks are `cactus`
(stem, level 0), `cactus_arm` (the base branch-off, 1), `cactus_prong` (2) and `cactus_tip` (3),
all strictly one level thinner than the cell below so only the base arm welds to the stem; they
wear generated `cactus_side`/`cactus_top` tiles and `sandstone`; see
`tools/generate-biome-textures.ps1` for the placeholder art.

## Entities

Entities (mobs) are data-driven like blocks: `assets/data/entities/<name>.json` gives a display
`name`, an `hp` pool, an optional `loot` table rolled on death, and an optional `model` Blockbench
project (default `assets/models/entity/<name>.bbmodel`). `ContentRegistry.Load` parses them into
`EntityType`s published on `EntityRegistry` (`RequireEntity`/`TryGet`). Entity models reuse
`BbmodelLoader` (which takes an optional required-group list: the player rig passes `RequiredParts`,
entities pass none) and `EntityModel`, which bakes the whole project into one mesh plus its skin.

The first entity is the **slime** (`Slime` + `Systems/SlimeManager.cs`), owned by `World`. It hops
toward the player, damages on contact only while airborne (a slime merely bobbing in water is
harmless), is shoved (harmlessly) by the player when grounded, floats to the surface of water like an
item drop (its water hops keep a land hop's total speed but halve the vertical and move it into the
horizontal, so it skims forward as a low jumping slide), and rolls its `loot` table on death. Natural
spawns appear on dry forest surface in a 20-40 block ring around the player; `SlimeManager` holds the
cap, cadence, damage and shove numbers via `WorldGenerationConfig.Slime*`. The player fights back with
the left-click punch (`World.UpdatePunch` + `Combat`): a slime inside the forward 3-block, 30°-half-angle
cone with line of sight takes the held item's `entityDamage` and a knockback shove, and while one is in
the cone mining stands down (attack-first). `Mining.EntityDamage` is the bare-hand baseline (1) an item
overrides with its `entityDamage` (primitive tools 3, copper tools 5, obsidian blade 6). The obsidian
blade (`attack: "slice"`) instead swings a two-phase sweep (`World.UpdateSlice` + `SliceCombo`): each
swing hits the nearest two slimes in a forward hemisphere of the same reach with line of sight, the
buffered return swing is at half power, and the damage lands at the swing's impact frame rather than on
the click, with the arm and blade carried across the front by `PlayerModel`.

## Conventions

- C# with file-scoped namespaces, one type per file, `_camelCase` private fields, PascalCase public members. Primary constructors are used where natural (`ChunkManager(GL gl)`).
- XML doc comments (`/// <summary>`) on classes and non-obvious public methods; brief `//` comments explaining *why* for tricky logic. Match the existing density — the code is moderately commented.
- **New blocks go in JSON, not C#.** When a block has to behave differently, prefer a new data flag on `BlockDefinition`/`BlockType` that every call site reads over another per-id branch in gameplay code — the point of the content registry is that `World`, `TntManager` and the mesher ask "what does this block do?" instead of "which block is this?".
- `FastNoiseLite.cs` is vendored third-party code — do not reformat or refactor it.
- xUnit tests live in `OpenGLTest.Tests/` — run `dotnet test OpenGLTest.Tests/silkgltriangle.Tests.csproj -c Release`. They cover headless logic and simulated gameplay without a window (fixture: `TestContent.cs`); add a test with every feature instead of a throwaway script — but only when there is a *rule* to lock down. A test must assert behavior (a gate, a formula, a state transition, an invariant that survives content tuning), never a constant, a layout number, a content literal or a starting-stock/seed count (`Assert.Equal(1, inventory holds one converter)` is exactly as pointless as pinning a block's hardness) — see the `functional-tests` skill's ban list for the full rule and rewrite table. Verify feel-in-game by running the game and using the `X` debug overlay (FPS, position, grounded state). Watch the console for chunk generation errors — worker threads catch and log exceptions rather than crashing.
- Branch naming follows feature work off `master` (current work is on `worldgen`). Commit messages are short imperative sentences ("Add block placement", "Fix performance").
