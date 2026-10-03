# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project overview

A Minecraft-style voxel engine written in C# on .NET 9, using **Silk.NET** (windowing, input, OpenGL bindings) and StbImageSharp for texture loading. It renders an infinite, procedurally generated world (FastNoiseLite OpenSimplex2 noise) with chunk streaming, an underground of 3D-noise caves, worm-carved tunnels and heightmap ravines, binary greedy chunk meshing with ambient occlusion (plain cubes are culled by bitmask and merged into larger quads wherever the result is pixel-identical; procedural blocks and fluids keep per-quad emitters — see `MESHER_PLAN.md`), frustum culling, block breaking/placement via raycast, and simple player physics (gravity, jumping, collision). The player spawns standing on the terrain at the origin (`Camera.SpawnPosition`), or where they left a saved world; worlds are created, listed and opened from a main menu and saved to disk (see "Launch flow" and "Saves").

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

**Launch flow.** The game boots to the **main menu** with the cursor free (`Program` owns an
explicit `AppScreen` of Menu / Loading / Playing / Saving; every page rule, key rule and store
call lives in the GL-free, tested `MainMenuController`, drawn by `UIRenderer.DrawMainMenu`
through `RenderManager.RenderMenuFrame`, which writes framebuffer 0 directly with no HDR pass,
lighting or GPU timer). START opens the worlds page, a scrollable list of `WorldStore.List()`
(most recently played first) with PLAY / EDIT (rename) / DELETE (a confirm popup, DELETE /
CANCEL) on every row, plus CREATE and BACK; an empty list says so. CREATE opens a name field
(placeholder `Unnamed World`, which is what an empty field becomes; the store makes a taken name
unique with the smallest free numeric suffix, `WorldNames.Unique`) and a seed field (empty =
random, an integer is used verbatim, any other text is FNV-1a-hashed by `WorldSeedParser`).
Enter confirms the create and edit pages (never the delete popup), Tab switches field, and ESC
steps back one page and does nothing on the main page. SETTINGS and EXIT are the pause menu's
(the settings page is shared: the one `Program.SettingsPageOpen` predicate drives the slider drag
and its save on both menus). PLAY / CREATE never block inside the click: the click records the
pending work, `OnRender` presents one loading-screen frame, and the next update runs
`WorldSeed.Apply(seed)` -> `new ChunkSaveStore(dir)` -> `new World(..., store)`. The world then
streams in `WorldPhase.Loading` behind an animated loading screen (`LoadingScreenView` /
`UIRenderer.DrawLoadingScreen`: a progress bar eased monotonically toward `World.LoadProgress`, a
wall-clock spinner, the world name and `Generating terrain...` for a new world vs `Loading
world...` for a saved one) and is **never rendered** until `IsLoaded`; after that the overlay
fades out over `WorldGenerationConfig.LoadingFadeSeconds` (`RenderManager.RenderLoadingOverlay`).
While `!World.IsPlaying` Program runs no gameplay input, no pause menu and no inventory, and the
cursor stays captured from the World constructor on, so the Loading -> Playing flip never jumps
the look. A menu-launched world's lighting clock is rebased at construction, so a new world
starts at the mid-morning default (+ `VOXEL_TIME`) however long the menu stayed open; a saved
clock overrides that. The pause menu's third button is SAVE & QUIT: it presents a `Saving...`
frame, calls `World.SaveAndDispose(renderManager)` on the next update and returns to the worlds
page, and a second PLAY with another seed works in the same process; closing the window while in
a store-backed world saves it the same way (`OnClosing`). The dev overrides `VOXEL_SCRIPT`,
`VOXEL_SPAWN` and `VOXEL_LOOK` (`LaunchMode.BypassesMenu`) skip the menu entirely and build a
**storeless** World exactly as before — no persistence, no seed change, no loading phase — which
is why a dev override can never reach a saved world; `VOXEL_TIME` alone does not bypass.

Saves live in `%APPDATA%/CSVoxelEngine/saves/<slug>/` (`WorldStore.DefaultRoot`; the slug is
fixed at creation and never follows a rename): `world.json` (name, seed, created and last-played
timestamps, format version), `chunks/<cx>_<cy>_<cz>.bin` (one Deflate-compressed,
block-name-paletted snapshot per **modified** chunk, `ChunkSaveCodec`; an untouched chunk is
never written and simply regenerates), `lod-edits.bin` (`LodEditState`: column caps,
felled-canopy markers, surface overrides), `player.json` (`PlayerSaveCodec`: position and look,
creative/flight, health, respawn eye, lighting clock, the full inventory and hotbar; no position
while the spawn has not settled) and `entities.json` (`EntitySaveCodec`: the live slimes and
goblins with health, facing, velocity and ammo plus still-pending entries, and up to
`MaxSavedDrops` item drops, nearest first). See "Saves" below for the rules.

Setting the `VOXEL_SPAWN` environment variable to `x,y,z` (three comma-separated floats, e.g. `-40,-107,-75`) starts the camera eye at exactly that world position instead of on the surface at the origin, which is handy for inspecting a specific cave or feature without flying to it; an unparseable value is ignored with a console warning. `VOXEL_LOOK` takes `yaw,pitch` in degrees (two floats, e.g. `-90,-20`; yaw 0 faces +X so the default `-90,0` faces -Z, and pitch is clamped to ±89 exactly as the mouse look is) and pins the initial facing, so a scripted verification run frames the same thing every time. `VOXEL_TIME` takes a number of seconds and is added to the window clock before the lighting pass reads it (`Program.timeOffset`), which jumps the day/night cycle straight to noon, dusk or night for a screenshot instead of waiting out the orbit (one full cycle is `WorldGenerationConfig.DayLengthSeconds`, 1200 s like Minecraft's 20 minutes, with the sun starting mid-morning, so noon is about 147 and sunset about 447). All three parse once at startup and ignore garbage with a console warning. `VOXEL_SPAWN` and `VOXEL_LOOK` (like `VOXEL_SCRIPT` below) skip the main menu and play a storeless, unsaved world; `VOXEL_TIME` alone still boots to the menu and offsets every world opened from it (see "Launch flow" above). Setting `VOXEL_SCRIPT` to a scenario JSON path (see `OpenGLTest/visual-examples/basic.json`) runs a scripted visual test instead of playing: `teleport`/`look`/`wait`/`walk`/`mine`/`place`/`fly`/`give`/`spawn`/`grow`/`screenshot` steps drive the real update functions through a scripted keyboard and save numbered PNGs to `visual/<name>/` (`screenshot` takes an optional `hideHud`), then the window closes. `fly` turns creative flight on (or off with `"on": false`, `Camera.TestSetFlight`) so a scripted camera can hover over a spot instead of falling. `spawn` places a mob through `World.SpawnEntity`: `{ "op": "spawn", "entity": "goblin", "pos": [x, z] }` snaps the feet onto the terrain (`World.TryGroundHeight` + 1), while `"pos": [x, y, z]` is an exact feet position; the entity name (`slime` or `goblin`) is checked only when the step runs, so parsing stays registry-free. `visual-examples/goblins.json` (run it with `VOXEL_TIME=147`) spawns two goblins in a flat forest clearing under a flying — and therefore immune — camera and shoots `windup`, `stones` and `charge`. `grow` grows a tree on the live world: `{ "op": "grow", "species": "oak", "pos": [x, z], "steps": n }` snaps the column onto the terrain (`World.TryGroundHeight` + 1) and calls `World.TryGrowTree`, which hands it to `TreeGrowthWriter`: the writer rebuilds the tree rooted in that column from its wood state bytes (`TreeGraph.FromWorld`), or plants a sapling on the ground when the column holds none of that species, runs `n` (> 0) pulses of `TreeGrower.GrowFurther` and writes the difference back (see `### Trees`). Like `spawn`, the species name is checked against `TreeSpeciesRegistry` only when the step runs. `visual-examples/trees.json` (run it with `VOXEL_TIME=147`) frames the oak, spruce and cactus, a cut limb and the leaves decaying after it, a felled trunk, grown oaks, saplings, a baobab and a stone pine, the baobab's wide-trunk cuts and fall, and the stone pine's bare limbs under their leaf pads. Scenario parsing is covered by `VisualScenarioTests`; prefer a scenario over ad-hoc scripts for screenshot verification. `VOXEL_DEBUG=1` starts with the X debug line already on, so such a run logs FPS, position and the profiler breakdown once a second.

The **item debugger** (`dotnet run --project OpenGLTest.ItemDebugger -c Release`, same working-directory
rule) previews held items, entities and trees. Its left panel has three tabs. **ITEMS** is the item grid with the
display sliders. **ENTITIES** lists every `EntityRegistry` entry and draws the selected one part by part
through its `EntityAnimator` (`DebugSceneRenderer.RenderEntity`). The mode buttons are data-driven: one
per clip in the selected model's `.bbmodel`, in file order, plus HURT (the hit-flash tint). For the
goblin they read IDLE WALK THROW MELEE HURT. A clipless model such as the slime shows its rest pose and
only HURT. One-shot clips replay after a short rest, and the preview consumes their events itself. The
GL-free, tested state behind the tab is `OpenGLTest.ItemDebugger/EntityPreviewState.cs`.

**TREES** lists every `TreeSpeciesRegistry` species and grows the selected one pulse by pulse over a
flat grass plane, at a fixed seed (`WorldSeed.Default` at first; the debugger never calls
`WorldSeed.Apply`). The Step slider runs 0..the species' `MaturityStepsMax` and scrubs the growth
deterministically: each step is regrown from scratch in a fresh `VirtualTreeSpace` through
`TreeGrower.GrowSteps`, so dragging back and forth shows the same tree at the same step. STEP - and
STEP + move one pulse, MATURE jumps to the step this seed rolls for maturity
(`TreeGrower.MaturityStepsAt`, which is also where selecting a species lands), and REGROW (NEW SEED)
bumps the seed and keeps the step. Features (the buttress fins) apply only from that maturity step
on, so the mature step is exactly the worldgen tree (`GrowToMaturity`). Flat ground has no carved
cells, so the roots feature never shows here. The tree is rasterised with `TreeRasterizer` and meshed
by `CellBatchMesher` (`OpenGLTest/src/CellBatchMesher.cs`): the real chunk mesher run over scratch
chunks at the cells' real chunk positions, padded by a ring of empty chunks, so thin tree-wood tubes,
wide-trunk shells, the pixel-rim bevel, leaves and the biome tint look as in game, with no clipping at
16-block seams. `DebugSceneRenderer.RenderTree` rebuilds the meshes only when the state's `Version`
moves, and `ToDisplaySpaceTint` rebakes the biome-tinted vertex colours for the debugger, which
samples its atlas without the sRGB decode. The camera frames the union of the current and the
fully grown tree's bounds (`TreePreviewState.FitDistance`), so it stays steady while you scrub, and
the wheel off the grid zooms that framing. The GL-free, tested state is
`OpenGLTest.ItemDebugger/TreePreviewState.cs`.

Environment variables for scripted captures:
- `ITEM_DEBUGGER_SELECT=<item|entity|species>` tries the item grid first, then an entity by name or
  display name (switching to ENTITIES), then a tree species by file name (switching to TREES). A
  `tree/` prefix (`TreePreviewState.NamePrefix`) skips the first two, which is how `tree/cactus`
  reaches the species past the `cactus` block. An unknown name logs a warning.
- `ITEM_DEBUGGER_TREE_STEP=<n|mature>` pins the TREES step (an integer, clamped to the slider range,
  or `mature`). Without it a selected species shows its mature tree.
- `ITEM_DEBUGGER_ANIM=<clip|hurt>` picks the entity mode. An unknown mode logs a warning listing the
  valid ones.
- `ITEM_DEBUGGER_SHOT=<png path>` saves one screenshot and closes. It fires on an early frame, so a
  one-shot clip is caught near its start.
- `ITEM_DEBUGGER_VIEW=first` starts the ITEMS view in first person.
- `ITEM_DEBUGGER_LOOK=yaw,pitch` pins the first-person look angles.

The app opens a window and grabs the mouse. Controls: WASD to move, mouse to look, Space to jump, `C` to toggle creative mode (placing costs nothing, breaking drops nothing; double-tap Space toggles flight, but only while creative — flight is noclip and Space/Left Ctrl move up/down while flying), Left Shift to sprint (1.5x on foot, 10x while flying), hold left-click to punch first, then mine: a mob (a slime or a goblin) inside the forward punch cone (3 blocks, 30° half-angle, line of sight required; only the nearest is hit) takes the held item's `entityDamage` (1 bare-hand, 3 for the primitive tools, 5 for the copper tools, 6 for the obsidian blade) and a knockback shove instead of being mined through (an item declaring `attack: "slice"` — the obsidian blade — instead swings a two-phase blade sweep: each click hits the nearest two mobs in the forward hemisphere of the same reach, the buffered return swing lands at half power, and the arm and blade are carried across the front), while a clear cone mines the targeted block (hit-based: one hit per the held item's `tool.hitDelayMs`, deals the item's `blockDamage`, 0.5 bare-hand, against the block's float `hardness`, declared in its JSON - 5 for most blocks, 8 for malachite ore, 24 for a full-cube trunk of the `tree_wood` logs and 1 for leaves; a tree-wood cell scales its block's `hardness` by the thickness in its `WoodState` (`Mining.HardnessOf`: `hardness * (tipHardness + (1 - tipHardness) * thickness / WoodState.FullCube)`, so the thinnest wood keeps `wood.tipHardness` of it and placed, detached wood is as hard as a trunk), and `0` means it pops on first contact like a pebble; a block with `requiresTool` and a `tools` dict needs a listed tool tag at or above its required tier, and the wrong tool whiffs for 0 damage and no progress; damage resets if you release, switch target or switch tool; instant while creative, which ignores the gate; a click with no block under the crosshair still swings the fist); a stepped crack-sprite overlay drawn on the block's faces via `break-*.glsl` from `assets/textures/environment/break_progress.png` (8 stacked 32x32 frames, the top one blank); each hit pops the block out and back via `RenderManager.RenderBlockPop`), right-click to place the selected block or, when the selected slot holds an item with a `use` action, to use it (the bucket scoops a water source and pours it back out — see the item schema below), 1-10 to pick one of the 10 hotbar slots, `E` for the list inventory (blocks/tools/misc tabs with shared caps of 2048/8/2048; click a row to preview, click a hotbar slot to equip, same item again to unequip; the world keeps running and `E`/Escape closes it), `X` to toggle debug logging (FPS/position/water state to console), Escape to open the pause menu (RESUME / SETTINGS / SAVE & QUIT; Escape on its settings page steps back to it, and on the main page resumes). SAVE & QUIT presents a `Saving...` frame, calls `World.SaveAndDispose` on the next update and returns to the main menu's worlds page. None of these keys act while a world is still loading.

`X` also raises the **frame profiler overlay** under the FPS counter, and prints the same
snapshot to the console once a second so a headless run's log carries it. Console writes go
through the async `Log` queue (`OpenGLTest/src/Log.cs`), so a slow terminal never stalls the
frame that logged; `Log.Shutdown` drains it on exit. Each row is a section
of the frame — `camera`, the three `chunk*` rows from `ChunkManager.Update`, `lod`, `entities`,
`fluidWake`, `fluidTick`, `raycast`, `breaking`, plus the `progUpdate` input shell and the
`player` spawn/animation shell on the update side (right-click use/place work and console
logging are split out as `usePlace` and `debugLog`, and the autosave of a store-backed world is
the one `save` row, see "Saves" below), then `lighting`, `shadows`,
`lodPass`, `worldPass`, `entityPass`, `aoPass` (contact AO, see below), `waterPass`, `selectPass`, `postPass` (the tonemap resolve, see
"Linear lighting" below), `uiPass` on the render side,
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

**Water rendering** lives in the shared world shader plus two extra passes in `RenderManager`. Just before `RenderWorldTranslucent` draws, `CopyScene` snapshots the HDR scene target's colour and depth (see "Linear lighting" below; the colour copy is `Rgba16f`, like the reflection mirror) into two textures on units 3 and 4 (`CopyTexSubImage2D` from the bound HDR FBO — the water pass depth-tests while sampling, so it cannot read the target directly); the water fragment then composes its own look out of them: a refracted lookup offset along the analytic wave normal, Beer-Lambert absorption over the water's measured thickness (`WaterAbsorb`) plus in-scattered `WaterScatter`, and a Schlick fresnel mix into the planar reflection. Flowing water carries its baked flow into that normal (`FlowWaveNormal`: a bounded two-phase flow map that scrolls the ripples downstream and stretches them along the flow, identical to `WaveNormal` when the flow is zero), and falling side faces add fast vertical streaks; generated rivers and lakes are all source cells, so they bake no flow and stay still until the simulation makes them flow (`visual-examples/flow-water.json`, `VOXEL_TIME=147`, pours some). The mirror plane itself is the single `RenderManager.WaterPlaneY` at sea level, so a lake whose surface stands above it reflects against that plane rather than its own - an accepted approximation, since only one plane can be mirrored per frame. Seen from below — decided per fragment from the face normal (`fromBehind` in `test-fragment.glsl`), never from the sea plane, since a pond stands above it — the surface is a Snell window: the same scene copy shows through it distorted by the wave normal, the inside fresnel goes to total internal reflection past the critical angle and that band fades to the underwater fog colour, and the mirror is never sampled from that side; `ReflectionReuse.MirrorApplies` also skips rendering it whenever `Camera.IsEyeInWater`, and the sky dome (`SkyRenderer`) and the sun disc stay dry in water (lava still fades the dome via `_skyTintAmount`, eased through `_lavaT`) so the window shows real sky. World, cloud and sky fog share `SkySample`, duplicated verbatim in `sky-fragment.glsl`, `test-fragment.glsl` and `cloud-fragment.glsl`, with `SkyPalette.Sample` as the tested C# reference, so change all four together. The dome currently samples the flat 28198cc sky colour: `SkyPalette.Compute` returns the old night/day mix, pulled toward the dusk orange by `SkyDuskMix`, as both zenith and horizon with zero glow, and `SkyHazeStrength` is 0, so every direction (and every fully fogged fragment) is that one colour. The reflection pass now also draws the sun disc and the cloud layer into the mirror, and both it and the scene copies are skipped whenever `CullWater` (one frustum pass a frame, feeding the water pass its draw lists) found no water on screen — glass shares the translucent mesh, so `Chunk.HasWaterMesh` is the flag that decides, not `HasTranslucentMesh`. Opaque faces that look into water are flagged by the mesher in bit 3 of `Vertex.Normal` (bits 0-2 stay the face index, bit 5 is the leaf-sway flag, bits 6-7 the low bits of the block-light tint code) and get the animated Voronoi caustic net; bit 4 is the biome-tint flag (see "Biome-tinted grass and leaves" below). The world fragment darkens a submerged face's albedo by `WetDarken` once, before the light multiply, and only at or below that face's own baked surface (`step(fWorldPos.y, fWaterSurfaceY)`; the caustics stop at the same line), so a dry face beside water — a beach top, a bank wall over the waterline — is never darkened or tinted; ore tiles (a non-zero `material.metallic`) are excluded. `SurfaceFresnel` is the GL-free twin (`Schlick`, `WetWeight`). While the eye is submerged, `UnderwaterRaysRenderer` adds one full-screen ray-march for sun shafts, composited in display space (the upsample reads the HDR target it draws into, adds the shafts to the encoded scene with the pre-SH-10 sRGB sun colour and strength, clips at 1 and decodes), since the same add in linear light lost the glare toward the sun. Both measure depth from the **local** water surface, never the sea plane: the caustics from the surface baked into each submerged face's vertex (`Vertex.WaterSurface`, read as `fWaterSurfaceY`), the rays from `Camera.EyeWaterSurfaceY` (`ChunkManager.GetFluidColumnTopGlobal` over the eye's column), so a pond above sea level gets both — only the planar mirror still uses the sea plane. The rays are why the shadow map keeps rendering underwater (`_shadowStrengthDry`, the strength before the underwater fade) even though the world diffuse no longer uses it. **Depth convention**: `Camera.GetProjectionMatrix` comes from `System.Numerics`, which is Direct3D-style (clip depth 0..1), so window depth is *not* the GL -1..1 mapping — never write a near/far linearization. Reconstruct a world position by inverting the very matrix that wrote the depth (`uInvViewProj`), and compare raw depth values directly for "which is nearer". `visual-examples/water.json` (run it with `VOXEL_TIME=360`) frames the shallows, the grazing surface, deep water from height and the underwater shots, including the surface seen from below in the sea and in a pond above sea level (a liquid one — a frozen pond has an ice lid, not a water top face).

**Linear lighting (SH-10).** The world is lit in linear light and rendered into
`RenderManager`'s HDR scene target — an `RGBA16F` colour texture plus a sampleable `DepthComponent24`
depth texture (`CreateHdrTarget`, which also builds `_hdrColorOnlyFbo` over the colour alone for the
contact-AO pass; recreated with the viewport like the reflection, scene-copy and
rays targets). `BeginFrame` binds and clears it, and **every mid-frame restore goes back to it,
never to framebuffer 0** (`BindSceneTarget` at the tails of `RenderShadows` and
`RenderReflection`, the `Create*Target` recreate paths, the `UnderwaterRaysRenderer` composite);
only `CreateHdrTarget`/`DeleteHdrTarget` and `RenderPostProcess` bind 0. Colour textures sampled
before the tonemap are uploaded as `SRGB8_ALPHA8` (the `srgb` flag on `TextureLoader` and
`TextureAtlas.Build`: the block atlas, `ItemMeshCache`, skins, sun, break progress, bubbles, the
world healthbar hearts) so the sampler decodes them; the material/normal atlas is data and stays
plain `Rgba`, and so do UI-only textures (font, crosshair, HUD hearts, `UIRenderer`'s own icons).
CPU colours are authored in sRGB and go through `ColorSpace.SrgbToLinear` where they are computed
or uploaded (the sun, ambient and sky palette in `SetLighting`, torch, point lights, water
scatter, molten tints, selection outline); the selection *face* highlight is the one overlay
mixed in display space instead (`selection-fragment.glsl`'s `uDisplayMix`: it reads the HDR target
it draws into after a `glTextureBarrier`, blend off), since a linear 15% blend turned any dark
targeted face into a flat grey, and the cloud layer does the same (`cloud-fragment.glsl` lights,
fogs and mixes in display space over the target it reads through `RenderManager.SceneReadUnit`,
the HDR target or the mirror; the depth pre-pass lets one surface per pixel through), since a
linear blend left the clouds opaque, cream slabs; the block-light colours are the exception that is
uploaded pre-linearised: `BlockLightPalette.Linear` goes up once per frame as the
`uBlockLightPalette[8]` array, `test-vertex.glsl` decodes each chunk vertex's tint code into the
smooth `fBlockTint` (mirroring `LightTint.Decode`), the plain (entity) path reads
`uBlockLightPalette[uEntityTint]`, and the fragment's torch term is `fBlockTint * pow(...)` in
place of the old single `uTorchColor`; a GLSL constant that stands for one is written as its
decoded value with the sRGB original in a comment. At the end of `World.Render` the `postPass`
section calls `RenderManager.RenderPostProcess`, which binds framebuffer 0 and runs
`PostProcessRenderer` (`post-fragment.glsl`): exposure (`WorldGenerationConfig.TonemapExposure`),
a per-channel clamp to 0..1 (what the old 8-bit target did, so snow and white sand clip to white
rather than staying cream; `Tonemap.cs` is its tested C# twin), the exact sRGB encode, and the frame's one
half-LSB dither. FXAA (SH-9) runs unconditionally inside the same pass, on display-space
(tonemapped, encoded) taps filtered by hand from `texelFetch`, before the dither (knobs
`WorldGenerationConfig.Fxaa*`; its contrast early-out keeps it near 1 ms at 1080p). The UI draws
after it, straight into the gamma default framebuffer, so text and icons stay crisp and HUD
colours stay display-space; the two textures it shares with the world (the atlas and the item
meshes) are re-encoded in `ui-fragment.glsl` behind `uSrgbTex` (`UIRenderer`'s `atlasIsSrgb`
constructor flag, `ItemMeshCache.IsSrgb`), so icons write the same bytes as before.
`visual-examples/linear-lighting.json` (run at `VOXEL_TIME` 147, 440 and 700) frames the surface,
the Snell window, the lava cave with and without the HUD and a high LOD/fog vista for comparing a
lighting change against earlier shots.

**World fog** is the squared linear ramp `f*f` over `FogNear..FogFar` toward `SkySample` in the
view direction, reaching exactly the sky at FogFar so the LOD edge stays seamless. In dry air it is
mixed in **display space** (`test-fragment.glsl` encodes the fragment, clamped to 1, and the fog
colour, mixes, and decodes), because the same mix in linear light reads visibly hazier than the
pre-SH-10 look; underwater and in lava it stays the linear mix toward `uFogColor`, and the two are
blended by `uFogWet` rather than branched so the transition never pops. Cloud fog is display
space too (see the cloud layer above); particle and healthbar fogs are still linear.

**Wind (SH-27).** `Wind.cs` is the one wind: a GL-free pure function of the lighting clock whose
heading turns slowly (`WorldGenerationConfig.Wind*`), with `Wind.Offset` its bounded closed-form
integral. Leaf sway reads it in both vertex shaders (`uWind`, the direction scaled by `WindLean`,
leans the canopy downwind; `uWindOffset` advects the gust pattern, so the gust phase rate is the
same anywhere in the world — keep the two sway blocks byte-identical), fire smoke and embers drift
along it (`FireParticle.Update`'s `wind`), and the cloud layer and its shadows are carried by it
(`CloudCopies.Drift`, wrapped modulo the field on both axes; `uCloudDrift` is a `vec2`). `World`
gets the same lighting clock through `World.TimeOffset`, which `Program` sets from `VOXEL_TIME`, so
leaves, smoke and clouds agree; `RenderManager.WindDirection` exposes the unit direction for future
rain and snow.

**Contact AO (SH-28).** A cheap screen-space contact AO darkens the crevices the baked vertex AO
cannot see — where pebbles, branch piles and other per-quad emitters meet the ground, and inner
corners between greedy quads. `RenderManager.RenderContactAo` (`ContactAoRenderer`,
`contact-ao-fragment.glsl`, GL-free twin `ContactAo`) runs mid entity pass as its own `aoPass`:
after everything that writes honest depth (terrain, drops, stations, TNT, falling blocks, slimes, goblins)
and before the depth-less or blended draws (healthbars, the first-person head, debris, toppling
trees, player, held item), the selection outline, water (whose refraction copy then carries the AO),
clouds and particles. It samples the HDR depth texture through the colour-only
`_hdrColorOnlyFbo` (no feedback loop) with multiplicative blending (`Zero, SrcColor`). Four taps
per pixel, `ContactAoRadiusBlocks` wide in the world (clamped to `ContactAoMaxPx`): for each opposite
pair, the midpoint of their raw depths is reconstructed on the centre ray and compared with the
centre. Raw depth is affine in screen space over a plane, so planes never darken at any angle;
a concave corner creases positive, convex edges and far silhouettes are rejected by the
`ContactAoMinBlocks..RangeBlocks` window, and it fades out by `ContactAoFadeFar`.
`ContactAoEnabled = false` skips the pass entirely.

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
sampler's REPEAT tiles it, so UVs are never atlas-absolute), sun/block bytes at 24-25, bytes
26-27 read by GL as **one integer ushort** at attribute location 5 (`Normal` + `LightTint`: bits
0-2 face, 3 submerged, 4 biome tint (`IsBiomeTinted`), 5 sway, 6-15 the 10-bit block-light tint code, `Vertex.BlockTint`,
`IsSwaying`; attribute 6 is gone), the `ushort Layer` at 28 (attribute location 7, an integer attribute) and the `ushort
WaterSurface` at 30 (attribute location 8, also integer): the baked water surface over a
submerged face in quarter blocks (`Vertex.EncodeWaterSurface`, 0 on every dry face). The mesher
gets it from `Chunk.SubmergedSurfaceCode`, which walks the fluid column in the padded snapshot and
resolves a column that leaves the snapshot from the generated `WaterLevel`; it rides the greedy key
as a 7-bit code in bits 25-31 (bits 16-23 are unused), so a merge never crosses two water surfaces. On a **fluid** quad
(alpha 254) the same ushort means something else: the per-corner flow code
(`Vertex.EncodeWaterFlow` of `FluidFlow.Corner`, signed 7-bit X and Z plus a falling bit, 0 =
still), one per vertex so neighbouring cells agree at shared corners. That is safe only because
the shader reads the surface meaning exclusively on non-water submerged faces. Baked model
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
tracks the sun. The ore body darkens by `metallic * 0.6` and the sun highlight, added in linear light,
is scaled by `WorldGenerationConfig.OreSpecLinearScale` (`uOreSpecScale`), because the sRGB encode
lifts an add on the near-black body far more than it did in display space and smeared coal's
specks into grey. Every other (non-ore, non-water) tile is plain diffuse: the SH-23 grazing sky
rim was removed because it tinted grazing foliage teal.

**Chunk meshing.** `Chunk.GenerateMeshDataFromSnapshot` runs two passes over the padded 18³
block snapshot. The binary greedy pass (`EmitGreedyFaces`, scratch in the `[ThreadStatic]`
`MeshScratch`) takes every block flagged `BlockType.IsGreedy`: a `Model`-renderer block whose
baked model is exactly the six untransformed cube faces (`ContentRegistry.BakesGreedy`; all four
built-in parents qualify, so stone, dirt, grass, logs, TNT, lamps, leaves and glass all do). It
builds per-axis bit columns of greedy and occluding cells, finds visible faces with one
shift-and-mask per column (the halo bits cull border faces exactly like the neighbour read),
keys each face by block id, the four corner AO values, the submerged flag and the four corner
**smooth light** sums (Minecraft smooth lighting, `Chunk.SmoothCornerLight`): each corner's sun
and lamp light is summed over the front cell plus the same side/side/corner cells AO reads (a
side that blocks light, or a corner behind two blocked sides, is replaced by the front cell, so
an occluder darkens only through AO and light never leaks round a sealed diagonal), one exact
0..60 sum per channel, 12 bits a corner, travelling in a parallel 48-bit key
(`MeshScratch.PlaneLightKeys`) beside the int key, plus a 10-bit per-corner **tint code** in a
third, 40-bit key (`MeshScratch.PlaneTintKeys`). **Coloured block light** is a dominant-colour
palette tint, not RGB propagation: each block-light cell stores `level | slot << 4` (level bits
0-3, `BlockLightPalette` slot bits 4-6, so every level read masks `& 15`), seeded from
`BlockType.LightSlot`; a chunk with no tinted seed runs the unchanged `SpreadLight`, one with any
runs its twin `SpreadBlockLight`, where the brighter source wins and a tie at equal level goes to
the **lower slot** (re-queued so it propagates), which makes the result depend only on the seed
set, never on BFS order. `SmoothCornerLight` resolves the corner's four (level, slot) samples
through `LightTint.FromSamples` into `slotA | slotB << 3 | q << 6`, B's share being `q / 30`
(`LightTint.WeightDivisor`, t ≤ 0.5, canonical so equal mixes give equal codes), returned in
bits 12-21 of the corner code; the merge never crosses a tint change either, and the shader
lerps the decoded colours linearly, so greedy output stays pixel-identical. A red and a blue
pool therefore meet at a border softened over about a cell rather than adding up to purple.
`FaceLight` and the fluid corner light carry the tint into the procedural emitters (a blocked
fluid sample takes the lowest slot among the brightest open ones), the published light
snapshot gains a slot half (length `2 * BlockCount`) only in chunks that had a tinted seed, so
`EntityLight` hands entities an `EntityLightSample.Tint`, and the LOD bakes code 0. Uniform light gives `4L/60 == L/15`, so a
uniformly lit face is byte-identical to the old one-sample-per-face light. It gathers each key's
faces per depth slice into a 16×16 bit plane and merges rectangles — but only along an axis
where the key's AO and both corner lights are constant, so the merged quad's interpolated shade
and light equal the per-cell values exactly. Merged quads carry UVs 0..W / 0..H in tile units
(`TiledUv`) and the sampler's REPEAT tiles them. The per-voxel loop then skips greedy cells and
emits everything else as before (tree wood — `Chunk.EmitTreeWood`'s node box and connecting tubes
for a thin cell, `Chunk.EmitRimWood`'s stair-stepped `TreeWoodShape.RimBoxes` for a bevelled
pixel-rim trunk shell — pebbles, fluids, any model with cropped or rotated UVs); its full, shaded,
culled cube faces use the same corner rule, and the procedural emitters (tree wood, pebbles, branch
piles, snow layers) bilinearly interpolate the smooth corner light
of the cell face they sample (`FaceLight`, flat where that face's front neighbour occludes).
Fluids average each corner over the same own/dx/dz/diagonal cells `CornerHeight` reads, with a
blocked cell replaced by the brightest open one, an order-independent rule so neighbouring fluid
cells agree at every shared corner. `GenerateMeshData(manager, greedy: false)` is the test seam;
`GreedyMeshingTests` rasterises both outputs into unit cell faces and requires them to be equal
(`SmoothLightTests` locks down the light rules themselves). Because a merge never crosses a
lighting or AO difference, this world's rough noise terrain and leaf canopies only give ~15-20 %
fewer vertices (`PERF_TODO.md` item 11), not the 5-10× of flat worlds; smooth light left that
almost untouched (+0.2 % vertices over eight sampled surface and cave chunks, mesh-build time
within noise), since generated terrain is mostly uniformly lit and only lamp and shadow
gradients split merges.

**Biome-tinted grass and leaves.** Grass and every species' leaves (`oak_leaves`, `spruce_leaves`,
`baobab_leaves`, `stone_pine_leaves`, all on the `foliage` palette) take their colour from the
climate at their position, Minecraft style, while `FaceTiles` keep the default coloured tiles, so
inventory icons, item drops and the held block never change. It is block data: a `biomeTint`
object on the block JSON (`BlockDefinition.BiomeTintDefinition`) names a `palette` (`grass`, the
default, or `foliage`) and a `faces` map keyed by face name (`north`..`down`, or the shorthands
`side` and `all`). A face entry is either `{ "tile" }`, a tinted tile drawn *instead of* the
model's face (one quad; the leaves use it on every face), or `{ "overlay", "base"? }`, a tinted
cutout tile drawn *over* the face with `base` optionally replacing the untinted tile beneath (the
grass side: a `dirt` base, then `grass_side_textureless`). `ContentRegistry.ResolveBiomeTints`
resolves it in `BakeModels` into `BlockType.BiomeTint` (`Content/BiomeTintFaces.cs`: `Palette` plus
per-face `Base`/`Tinted` tiles, `NoTile` where a face has none) and throws naming the file unless
the block `BakesGreedy` and is not translucent, so both mesh paths can treat every tinted face as a
plain cube face; an `overlay` is further refused on a cutout block, which has no opaque base quad.
The alpha-tested tint tiles (every overlay, and a cutout block's replacement tile) join
`BlockType.CutoutTiles`, so they get the coverage-preserving mips a leaf tile gets. The colour is
`TerrainGenerator.Climate`, the temperature and humidity noises normalised through the biome ramps
to warmth 0..1 (exactly 0.5 across the temperate band) and wetness 0..1, so the palette's four
sRGB corners (`WorldGenerationConfig.GrassColorColdDry/ColdWet/WarmDry/WarmWet` and the slightly
darker `FoliageColor*`) sit on the biome lines. `BiomeTint` samples it on a 16-block lattice of
chunk-column corners: `TintsAt` reads the climate once and returns both palettes, `FieldsFor(cx, cz)`
gives a chunk column's `BiomeTintFields` (a grass and a foliage `BiomeTintField`, four corners each)
and `BiomeTintField.Sample` interpolates bilinearly in weighted-sum form, which makes two columns
sharing lattice points agree bit for bit, so the colour is continuous across chunk borders. Because
it is a function of the vertex *position*, not of the cell, the greedy key is unchanged and a
merge never splits (a merged quad interpolates per triangle on the GPU where the field is bilinear;
over 16 blocks of a field with a wavelength of thousands that cross term is far below a byte step,
and it is accepted). The tint rides the vertex rgb as `shade * tint` (`BiomeTint.Shade`), flagged by
`Vertex.Normal` bit 4 (`IsBiomeTinted`), and the world fragment multiplies a flagged face by
`BiomeTint.Gain` (4, mirrored by the literal `4.0` behind `fTintGain` in `test-vertex.glsl` — keep
the two together): the textureless tiles are grey around `WorldGenerationConfig.BiomeTintTextureGrey`
(sRGB 98), so a byte rgb capped at 1 needs that headroom to reach grass green. The two grass tiles
are supplied art; `tools/generate-leaf-textureless.ps1` writes each leaf one,
`<name>_leaves_textureless.png`, from its `<name>_leaves.png` (Rec. 601 luminance, alpha copied,
scaled to the same mean grey, byte-identical re-runs). Its `-Sources` list defaults to all four leaf
tiles; `tools/generate-tree-textures.ps1`, which tints the placeholder baobab and stone pine log and
leaf tiles from the oak art, calls it with `-Sources baobab_leaves, stone_pine_leaves` so a run of it
never rewrites the oak and spruce tiles. Both chunk mesh paths go through the one `Chunk.EmitTintedCubeFace`, which draws the
base quad first and then the coplanar tinted quad, the overlay winning by draw order under
`Lequal`; the reflection and shadow passes reuse the same lists and inherit the order, and the
shadow pass's alpha test punches the overlay's holes over the opaque base. The far LOD reads the
same lattice from the region's own origin (`rx * regionChunkSpan`, never the prefetch origin
`baseChunkX`) through `AddBlockFace`, for grass tops and sides and for the canopy bumps, and a
tinted canopy's transparent texels fall back to the textureless grey (`vec3(0.1221)`, the decoded
98) instead of `uLodLeafColor`, so the tint still applies there; chunks, LOD and the felled tree
all bake the tint into their rgb. The felled tree is meshed by the real chunk mesher in a scratch
chunk at (256, 0, 256), so `TopplingTree.BuildMeshData` passes `BiomeTint.FieldsAt(min)` (a field
whose origin is the tree's own min cell) through the `tintFields` override of the internal
`Chunk.GenerateMeshData(manager, greedy, tintFields)`, or a falling canopy would snap to the climate
of world (4096, 4096). The mining pop and the break debris are standalone copies drawn at many
cells from streamed or cached geometry, so they wear the `BiomeTint` tiles (`PopBoxEmitter.EmitBox`'s
`biomeTint` argument; `BlockCubeCache.GetTinted`/`BuildTintedCube`, one cached debris mesh per
block, which only the debris asks for) with grey shade-only rgb, and the cell's colour comes from
the plain program's per-draw `uBiomeTint` uniform (`BiomeTint.CellTint` at the mined cell, carried by
`BlockDebris.Tint` for each cube), multiplied into bit-4 vertices in the vertex shader. Its white
default is written in `RenderManager.InitWorldProgram`, which binds the program first;
`Shader.SetUniform` writes to whatever program is bound, so every per-draw set sits after
`worldShader.Use()` and is reset to white straight after the pop or debris draw for the plain
draws that follow (the chunk program has no such uniform). `PopBoxEmitter.MaxQuadsPerBox` (12: six
faces, each at most a base and a tinted quad) sizes the pop's streaming buffers and its static
index pattern. `visual-examples/grass-tint.json` (run with `VOXEL_TIME=147`) frames the forest
toward the desert edge, a grass rise, a broadleaf crown, the LOD meet, the taiga spruce, a mining
pop next to the default hotbar icon and a felled tree. Tests: `BiomeTintTests` (field continuity,
the palette range Gain can reach, climate vs biome classification), `BiomeTintMeshTests` (both mesh
paths, overlay order, no merge fragmentation, the field override, leaves keep sway),
`BiomeTintLodTests` (the LOD matches the chunk mesher's field, canopies carry the foliage field),
`BiomeTintTextureTests` (the tiles are grey at the Gain's grey and keep their source holes),
`BiomeTintPopTests`, `TopplingTreeTintTests` and the `biomeTint` rules in `ContentTests`.

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
keeps `uModel`/`uMVP`/`uAlpha` for drops, TNT, falling blocks, toppling trees, block pops and debris
(`SetDrawUniforms`; the far LOD draws through the `CHUNK_DRAW` program too, `RenderLod` -> `chunkShader`), and takes its light per draw as `uEntitySun`/`uEntityBlock`, sampled from the
world at the entity's cell by `EntityLight.Sample` (each chunk keeps its mesh-time light snapshot
for that) and passed through `SetDrawUniforms`'s required `EntityLightSample` argument. Per-draw fade alpha reaches the fragment stage as the `flat` varying
`fAlpha`, because a uniform cannot vary between the sub-draws of one multi-draw. The wave and
sway blocks of `test-vertex.glsl` and `shadow-vertex.glsl` must stay byte-identical — a
`TO_WORLD(p)` macro is what lets them be, and `ChunkDrawShaderTests` guards it.

## Layout

```
OpenGLTest/
  src/
    Program.cs                 # entry point, wires window callbacks, input polling; the AppScreen
                               #   flow (Menu/Loading/Playing/Saving) and the deferred open/save
    LaunchMode.cs              # GL-free rule: VOXEL_SCRIPT/SPAWN/LOOK bypass the menu (storeless world)
    World.cs                   # owns Camera, ChunkManager, ChunkGenerationWorker, LodManager; raycast block edit API;
                               #   optional save store, Loading phase, FlushAll/autosave, SaveAndDispose
    WorldSeed.cs               # the runtime world seed: Apply reseeds worldgen + resets caches/LOD edits; BeginWorld/EndWorld
    WorldPhase.cs              # Loading / Playing / Disposed
    WorldLoadGate.cs           # GL-free: when a loading world may play (ready sphere meshed, or timeout); load progress
    Chunk.cs                   # 16^3 block storage (byte IDs), block/column fill + cave carving via WorldGen/, mesh data generation (greedy pass + per-quad emitters) + AO
    MeshScratch.cs             # per-thread scratch buffers of the binary greedy mesh pass
    CellBatchMesher.cs         # GL-free: meshes any TreeCell set with the real chunk mesher over scratch chunks
                               #   at their real positions inside an empty ring (the item debugger's TREES tab)
    TopplingTree.cs            # a felled tree: the wood TreeSupport finds cut off plus its canopy, meshed once in
                               #   a 16^3 scratch chunk, swung flat around the cut, then popped out as drops
    LeafParticle.cs            # one falling leaf billboard (sway, tumble, rest, fade); LeafParticleSystem owns them
    FaceLight.cs               # a cell face's four smooth corner-light codes, bilinearly sampled by the procedural emitters
    CutoutMipmap.cs            # GL-free coverage-preserving mip chain for cutout tiles (alpha rescaled per level)
    LightFlicker.cs            # GL-free per-cell flicker phase + curve for the campfire point lights
    SelectionOutlineMesh.cs    # GL-free selection outline: the 12 box edges as thin beams (no glLineWidth)
    ChunkGenerationWorker.cs   # background threads creating chunks around the camera
    Camera.cs                  # FPS camera, physics (gravity/jump/collision), DDA raycast
    LodRegion.cs               # far-terrain LOD: region columns meshed at reduced resolution from heightmaps; per-column chunk-Y ranges for burial
    LodSkyExposure.cs          # GL-free coarse sky exposure the LOD mesher bakes into Vertex.Sun from its column-top grids
    LodSurface.cs              # per-column far-LOD surface overrides (edited top height + tile), beside LodRegion's caps
    FluidState.cs              # encoding of the per-block state byte for fluid cells (source / falling / level 1-7)
    BlockType.cs               # runtime block record built from JSON, plus the per-id lookup tables
                               #   the mesher and gameplay read (ById, Emission, IsSolid/Occludes/...)
    Mining.cs                  # GL-free mining rules: the tools/requiresTool gate, block damage per
                               #   hit and hit pace shared by the break loop and the tests
    Combat.cs                  # GL-free punch rules: the forward cone (3 blocks, 30° half-angle)
                               #   and entity damage, shared by the punch loop and the tests
    SliceCombo.cs              # the blade's two-phase slice combo: press/queue/advance state
    Mob.cs                     # abstract base of every hostile (Slime, Goblin): position, health,
                               #   facing, hit flash, body box, TakeDamage/Shove
    Goblin.cs                  # the goblin: body physics, throw-then-melee AI, clip/event contract
                               #   (ValidateContent), animation-driven release/impact events
    GoblinAim.cs               # GL-free low-arc ballistic solver for the thrown stones
    AnimationClip.cs           # GL-free Blockbench keyframe clip: per-bone rotation/position/scale
                               #   tracks, linear/catmull-rom/step, loop/once/hold, timeline events
    EntityAnimator.cs          # GL-free per-entity animator: idle/walk locomotion + one-shot action
                               #   layer, events, parent-chain part matrices, HandAnchor
    Placement.cs               # GL-free placement rules: which cell a right-click fills (a stack
                               #   flagged replaceableByPlacement, layered snow, is taken over in
                               #   place) and what a placement may write over
    Multiblock.cs              # GL-free multiblock structure rules: the eight cells of the 2x2x2
                               #   volume, the state byte (facing + offset from the anchor),
                               #   anchor resolution, the integrity check, Convert and Revert
    MultiblockPlacement.cs     # which 2x2x2 volume a right-click meant: the right-down/right-up/
                               #   left-down/left-up search and the facing the opening takes
    WorldGenerationConfig.cs   # all tuning constants (view radius, per-frame budgets, thread counts) and every worldgen knob (seed, ravine, cave, carver)
    MathUtils.cs               # lerp/easing/pulse helpers for animation curves + shared constants
    SkyPalette.cs              # GL-free zenith/horizon/glow sky palette and the C# reference of SkySample
    BlockLightPalette.cs       # GL-free 8-slot block-light colour palette (slot 0 = the default warm torch colour)
    LightTint.cs               # GL-free 10-bit two-slot tint code: FromSamples/FromWeights/Decode, the C# twin of the shader decode
    BiomeTint.cs               # GL-free biome tint: Climate -> palette -> vertex tint on a 16-block lattice, Gain, Shade, CellTint
    BiomeTintPalette.cs        # Grass / Foliage: which palette a biomeTint block reads
    BiomeTintField.cs          # one palette's four lattice corners over a 16-block field, sampled bilinearly
    BiomeTintFields.cs         # a mesh build's grass and foliage fields, both from one climate evaluation per corner
    Mesh.cs, Shader.cs, TextureLoader.cs, FileUtils.cs, FastNoiseLite.cs (vendored, don't reformat)
    Data/                      # Vertex struct (32 B: float pos, rgba, float tile-local UV, sun/block bytes, a normal/sway/tint
                               #   ushort, ushort array-texture layer), cube geometry, frustum/plane math, direction tables
    Content/                   # the data-driven content pipeline (see "Content" below)
      ContentRegistry.cs       # loads assets/data + assets/models, assigns byte ids, fills the tables
      BlockDefinition.cs, ItemDefinition.cs, LootTableDefinition.cs, ModelDefinition.cs  # JSON DTOs
      BlockModel.cs, ModelLoader.cs         # Minecraft model format: parent chains, #texture refs
      ModelBaker.cs, BakedModel.cs, BakedQuad.cs   # elements -> atlas-ready quads + full-cube flags
      LootTable.cs, LootContext.cs          # compiled loot tables and the world cell they roll at
      BbmodelLoader.cs                      # Blockbench .bbmodel: groups (nested, with Parent),
                                            #   cubes, per-face UVs, the skin texture and the
                                            #   keyframe `animations`; rest rotations rejected
                                            #   (the player's posing is procedural, entities
                                            #   animate from their clips)
      BlockRenderer.cs, BlockBreakAction.cs, AttackPattern.cs, ItemUseAction.cs  # geometry/break/attack/use enums
      BiomeTintFaces.cs                     # a block's resolved biomeTint: palette + per-face base/tinted tiles
      SnowShape.cs                          # state-aware hitbox shape of layered snow
      TreeWoodShape.cs                      # the one tree_wood shape rule: which cells connect (parent
                                            #   edges, never detached wood), node and tube hitboxes,
                                            #   body overlap and the bevelled pixel-rim boxes (RimBoxes)
      TreeSpeciesDefinition.cs              # JSON DTO of assets/data/trees/<species>.json
      WoodLootBand.cs                       # one thickness band of a tree_wood block's loot (self or a table)
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
    Saves/                     # GL-free world saves (see "Saves")
      WorldInfo.cs             # one listed world: world.json metadata + its folder (slug fixed at creation)
      WorldStore.cs            # the saves root: list (most recent first), create, rename, delete, touch
      WorldNames.cs            # name normalising, the "Unnamed World" default, unique numeric suffix, folder slug
      WorldSeedParser.cs       # seed field -> seed: empty = random, integer verbatim, else FNV-1a hash
      SaveNames.cs             # block/ and item/ namespaced content names, so saves never store raw ids
      ChunkSnapshot.cs         # immutable copy of a chunk's blocks, state bytes and player-placed cells
      ChunkSaveCodec.cs        # the chunk file format: block-name palette + Deflate body
      ChunkSaveStore.cs        # one world's chunk files + side cars: pending map, index, async writer thread
      LodEditState.cs          # lod-edits.bin: export/import of the LOD caps, surface overrides, canopy markers
      PlayerSaveData.cs        # the player.json shape: pose, modes, health, clock, inventory + hotbar
      PlayerSaveCodec.cs       # reads/writes player.json
      EntitySaveData.cs        # the entities.json shape: mobs (health/facing/velocity/ammo) and drops
      EntitySaveCodec.cs       # reads/writes entities.json
    UI/                        # UI state, layout and rules, mostly GL-free (UIRenderer draws them)
      PauseMenu.cs             # pause menu pages, hit kinds, view and PauseMenuLayout (RESUME/SETTINGS/SAVE & QUIT)
      SettingsMenu.cs          # settings page rows, tabs, sliders and SettingsLayout (shared by both menus)
      MainMenu.cs              # main-menu pages, hit kinds, fields, keys, actions and MainMenuView
      MainMenuLayout.cs        # main-menu geometry + HitAt: buttons, scrolled world rows, fields, delete popup
      MainMenuController.cs    # every main-menu page transition, key rule and WorldStore call (tested)
      TextFieldState.cs        # one single-line text field: printable-ASCII typing rules and length cap
      LoadingScreen.cs         # LoadingScreenView (eased monotone progress, fade-out) + LoadingScreenLayout (spinner)
      ScrollList.cs            # scrolled equal-height row math: clamp, visible range, row at a point
      UiCanvas.cs              # the virtual UI canvas: reference size, uniform scale, scissor rects
      InventoryPanelLayout.cs  # [E] panel geometry, tabs, rows and scrollbar hit-testing
      InventoryView.cs         # [E] panel selection/scroll state; ResetForNewWorld between worlds
      HotbarLayout.cs          # hotbar strip geometry shared by the draw, its hit-test and the panel
      CraftCardLayout.cs, CraftCardLayoutCache.cs  # the crafting card's layout and its memo
      CraftingSession.cs       # the crafting tab's open recipe, copies and fed reservations
      Inventory.cs, ItemStack.cs  # the three pools, the hotbar, stacks and their components
      BitmapFont.cs            # TrueType font baked into a GL atlas; CPU glyph layout (printable ASCII)
      TextWrap.cs              # GL-free word wrap against a measure delegate
      GameSettings.cs          # player settings with JSON persistence (settings.json)
      Compass.cs               # HUD compass heading math
      Primitives.cs            # Vertex-layout mesh factories for simple shapes
    Items/                     # the Item class hierarchy ItemFactory hands out per use action
      Item.cs, ItemFactory.cs  # the abstract item; the per-use.action factory building the singletons
      ItemRegistry.cs          # every item by shared byte id: pure items (vine, branch, flint, ...) plus
                               #   one BlockItem per placeable block
      CrucibleItem.cs          # a vessel whose molten contents ride on its own ItemStack
      CrucibleInventory.cs     # GL-free helper that adds a crucible and hands back its stack
      ConvertMultiblockItem.cs # the converter's right-click: find the volume, convert in place
    Systems/
      WindowManager.cs         # Silk.NET window setup
      InputManager.cs          # keyboard/mouse context
      RenderManager.cs         # GL state, world/selection/UI passes, viewport
                               #   RenderMenuFrame: a world-less frame (main menu or the opaque
                               #   loading/saving screen) straight into framebuffer 0, no HDR,
                               #   lighting or GPU timer; RenderLoadingOverlay: the fade over the
                               #   first playing frames; ForgetWorld drops a disposed world's refs
                               #   depth-only directional shadow passes (RenderShadows) run first
                               #   each frame into two cascades: the near one (90 blocks, unit 1)
                               #   with chunk and entity casters, then a coarse far one (352
                               #   blocks, unit 7) with chunk casters plus the non-buried LOD
                               #   regions (foliage cast opaque via uOpaqueCasters, no entities);
                               #   SampleShadow in test-fragment.glsl reads the near map and
                               #   blends in one far tap where the near box fades out, and LOD
                               #   fragments receive the far cascade through the same path
                               #   the sky dome is drawn first, right after the clear (RenderSky:
                               #   SkyRenderer, then the additive sun disc from
                               #   assets/textures/environment/sun.png via RenderSun), depth off,
                               #   so the terrain that follows occludes it
                               #   CopyScene snapshots the frame's colour/depth to units 3/4
                               #   for the water pass's refraction (see Water rendering above)
                               #   cloud shadows: CloudShadowTexture bakes CloudField's level
                               #   grid into an R8 map bound ONCE on unit 8 (nothing else may
                               #   bind that unit); CloudShadow in test-fragment.glsl projects
                               #   each fragment up the sun ray to CloudHeight and dims the sun
                               #   term there, off while uCloudShadowStrength is 0 (ItemDebugger,
                               #   clouds turned off); visual-examples/cloud-shadows.json
                               #   owns the two ChunkGeometryPools, the ChunkDrawBatch and the
                               #   CHUNK_DRAW variants of the world/shadow programs; the four
                               #   chunk passes are one MultiDrawElementsIndirect each
                               #   the station passes ride inside entityPass: RenderQuerns,
                               #   RenderKilns (crucible mesh, molten pool, the last charged
                               #   input hovering over the rim and shrinking into it, stored
                               #   fuel lying flat) and RenderCastings (the pouring liquid
                               #   dissolving into the cast, drawn at CastingRenderer.ResultSize
                               #   — the pocket's width — not drop size)
                               #   RenderContactAo: the multiplicative screen-space contact AO
                               #   (ContactAoRenderer) drawn through the colour-only HDR FBO while
                               #   sampling the HDR depth texture, placed mid entity pass
      UIRenderer.cs            # every UI draw: HUD, hotbar, inventory, pause/settings menus,
                               #   DrawMainMenu (all main-menu pages) and DrawLoadingScreen
      UITextCache.cs           # memoised per-frame HUD strings (FPS, compass, coordinates, counts)
      ShadowCascade.cs         # one sun shadow cascade: radius, map size, depth FBO/texture, texel-stepped centre (StepCentre) and light view-proj
      UnderwaterRaysRenderer.cs # additive full-screen sun shafts while the eye is submerged
      SkyRenderer.cs           # the full-screen sky dome pass (sky-fragment.glsl)
      ChunkGeometryPool.cs     # the two shared chunk vertex/index buffers (opaque, translucent):
                               #   BufferStorage VBO+EBO+VAO, NamedBufferSubData uploads, grow by copy
      GeometryAllocator.cs     # GL-free first-fit free list with coalescing, in units of
                               #   64 vertices / 96 indices; hands out the pools' GeometrySlices
      ChunkDrawBatch.cs        # persistent-mapped 3-slot ring of DrawElementsIndirectCommands +
                               #   the vec4(origin, alpha) ChunkInfo SSBO read via gl_DrawID
      ChunkManager.cs          # chunk lifecycle: load/unload, dirty tracking, mesh scheduling
      LodManager.cs            # LOD region lifecycle: worker thread builds, main thread uploads/unloads, burial flags
      FluidManager.cs          # the fluid tick: scheduled cells, level recompute + spread, batched remeshes
      LeafDecayManager.cs      # leaf decay + orphaned-wood crumble: a fixed tick over scheduled cells,
                               #   hydration from live tips (ScheduleAround/ScheduleBox from every edit path)
      TreeGrowthWriter.cs      # reactive growth on the live world: rebuild a column's tree from its wood
                               #   state bytes (or plant a sapling), GrowFurther in a WorldTreeSpace, write
                               #   the difference back; TreeGrowthResult.cs / TreeGrowthStatus.cs are its result
      LeafParticleSystem.cs    # falling leaves: ambient fall off canopy near the camera + bursts where it was disturbed
      LeafParticleRenderer.cs  # the falling leaves as one back-to-front billboard draw
      KilnManager.cs           # kiln block entities keyed by anchor: fuel, ignition, the crucible
                               #   slot and its melt loop, plus the multiblock integrity sweep
      CastingManager.cs        # one cast per mold cell: pour from a held crucible, cool, collect
      SlimeManager.cs          # slimes: hop AI tick, contact damage, loot, ring spawning
      GoblinManager.cs         # goblins: AI tick, their thrown stones, melee damage at the impact
                               #   event, loot + carried flint on death, ring spawning
      MobSpawner.cs            # the shared dry-forest-surface ring spawn search
    WorldGen/
      TerrainGenerator.cs      # the 2D half: owns every 2D noise channel and the biome split; Compute -> ColumnData
      Biome.cs                 # the Forest / Desert / Ocean / Taiga / Frozen enum
      ColumnData.cs            # per-chunk-column arrays: Heights (ravines cut in), DirtDepth, EntranceWeight,
                               #   CaveIntensity, Biomes, Bed (pre-ravine profile: decides wetness)
      TreeGenerator.cs         # the tree worldgen front end: which columns root which species (worldgen
                               #   rules), stamping the rasterised cells into chunks, the far-LOD canopy
                               #   lookups (TryGetCanopy, the crown proxy) and the felled-canopy markers
      TreeBatch.cs             # every tree of one 64x64 XZ cell grown together in one VirtualTreeSpace
      Trees/                   # the tree growth engine (see "Trees")
        TreeGraph.cs, TreeGrower.cs        # the node graph; Pulse / GrowToMaturity / GrowSteps / GrowFurther
        GrowthSignal.cs, TreeDirections.cs, TreeCellKey.cs  # a pulse step, the six face dirs, cell keys
        IGrowthLogic.cs, GrowthLogicRegistry.cs            # growth.logic: the direction chooser + its table
        OakGrowthLogic.cs, ConiferGrowthLogic.cs, CactusGrowthLogic.cs,
          BaobabGrowthLogic.cs, UmbrellaGrowthLogic.cs     # oak, conifer, cactus (pitchfork arms),
                                                           #   baobab, umbrella (bare limbs) logics
        ITreeFeature.cs, TreeFeatureRegistry.cs, TreeFeatureSpec.cs  # features[]: post-growth stages
        RootsFeature.cs, ButtressFeature.cs                # roots down into carved air; buttress fins
        TreeRasterizer.cs, TreeCell.cs     # graph -> wood/shell/leaf cells with their state bytes
        ShellOffset.cs, TrunkShape.cs, TrunkRim.cs  # wide-trunk shells: offsets, footprint, rim style
        LeafCluster.cs, LeafClusterShape.cs         # leaves around live tips and their hydration
        TreeSpecies.cs, TreeSpeciesCompiler.cs, TreeSpeciesRegistry.cs,
          TreeWorldgenRules.cs             # compiled species, its validation, the registry, worldgen rules
        ITreeSpace.cs, VirtualTreeSpace.cs, WorldTreeSpace.cs, TreeCellReader.cs  # what a tree may grow
                                           #   into: worldgen heightmap view, live-world view, world reads
        TreeSupport.cs, TreeSupportResult.cs  # which wood stands: paths to an anchor (felling, crumbling)
        WoodState.cs, LeafState.cs         # the wood and leaf meanings of the per-block state byte
        LodCrownProxy.cs, LodCrownProfile.cs  # the far-LOD crown silhouette of a species
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
  assets/data/entities/<name>.json    # slime, goblin: display name, hp, loot table, optional model
  assets/data/trees/<species>.json    # tree species (oak, spruce, cactus, baobab, stone_pine): wood and
                                      #   leaves, growth, thickness, leaf cluster, features, LOD crown, worldgen
  assets/data/molten/<name>.json      # molten materials (copper): tile + tint the liquid is drawn with
  assets/data/recipes/melt/<in>.json  # what a block or item melts into, and how many ml
  assets/data/recipes/cast/<out>.json # (fired mold + material) -> result, amount and cooling time
  assets/models/block/<name>.json     # Minecraft Java block models, incl. the built-in parents
                                      #   cube, cube_all, cube_column, cube_bottom_top
  assets/models/entity/<name>.bbmodel # Blockbench entity projects (slime, goblin with its four
                                      #   clips) + <name>.png skins
OpenGLTest.ItemDebugger/              # the item/entity/tree preview tool (ITEMS, ENTITIES and TREES tabs)
  EntityPreviewState.cs               # GL-free state of the ENTITIES tab
  TreePreviewState.cs                 # GL-free state of the TREES tab: species, seed, step, per-step
                                      #   snapshots, ground plane and framing bounds
tools/generate-goblin-skin.ps1        # deterministic generator of goblin.png; its header pins the UV layout
tools/generate-leaf-textureless.ps1   # deterministic grey leaf tiles for the biome tint (scaled to BiomeTintTextureGrey)
tools/generate-tree-textures.ps1      # placeholder baobab / stone pine log and leaf tiles tinted from the oak art
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
(`TerrainGenerator.SurfaceBlock/SoilBlock/RockBlock/SeaBlock`, `TreeGenerator.LeavesBlock` — the
far LOD's fallback canopy leaf —, `PebbleGenerator.PebbleBlock`), a tree species resolves its
blocks once at load (`TreeSpecies.WoodId`/`LeavesId`), and everything else reads those bytes. Best of all is not naming
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
| `hardness` | 5 | float damage the block absorbs before breaking; each hit deals the held tool's power (`Item.ToolPower`, bare hand 1), so 5 with a power-1 tool is five hits; `0` breaks on first contact (pebble). On a `tree_wood` block it is the hardness of a full-thickness cell, and a thinner cell scales down linearly with the thickness in its state byte to `wood.tipHardness` of it (`Mining.HardnessOf(id, state)`: `hardness * (tipHardness + (1 - tipHardness) * thickness / 15)`). See `Mining` |
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
| `biomeTint` | none | colour the block by climate (see "Biome-tinted grass and leaves"): `{"palette": p, "faces": {face: entry}}`, where `palette` is `grass` (default) or `foliage`, a face key is `north`..`down`, `side` or `all`, and an entry is `{"tile": t}` or `{"overlay": t, "base": b}`. `tile` replaces the face with a tinted tile, `overlay` draws a tinted cutout tile over it (`base` optionally swapping the tile beneath). Only on a block that bakes to a plain full cube and is not translucent; `overlay` not on a cutout block. `FaceTiles` stay the default tiles for icons, drops and the held block |
| `material` | none | reflective surface tuning for the ores: `metallic` (0..1, default 0 - an ordinary diffuse block), `roughness` (0..1, default 1, lower being a tighter highlight) and `normalStrength` (0..4, default 1, scaling the tile's normal map). Omitting the object costs nothing at render time. Keyed by *texture*, not by block - see the material atlas below |
| `light.emission` | 0 | 0..15 (`LightEmission`) |
| `light.color` | warm torch colour | sRGB `[r, g, b]`, 0..255 each, of the block's light; only legal with `emission` > 0. Colours within 0.5/255 share a `BlockLightPalette` slot, and at most 8 distinct colours exist (the default included) - a ninth throws naming the file. `blue_lamp` is the example |
| `light.passes` | `!fullCube` | whether voxel light travels through the cell (`PassesLight`) |
| `renderer` | `"model"` | `model` (baked quads), `tree_wood` (a grown tree's wood: its shape, thickness and tree-graph links come from the state byte, see `### Trees`; requires a `wood` object, forces `occludes` and `castsAO` off and `light.passes` on, never meshes greedy, and refuses `cutout`, `translucent`, `fluid`, `falls`, `needsSupport`, `stackMax` and `hitbox`), `pebbles`, `branch_pile`, `snow_layers`, `fluid` or `quern` (procedural emitters), `multiblock` (the anchor cell of a structure, carrying the whole volume's pre-rotated model) or `multiblock_part` (one of its other cells, solid and occluding but never meshed); a fluid's or layer block's model only supplies its tile |
| `multiblock` | none | required on, and only legal with, `renderer: "multiblock"`: `{"part": "block/<name>", "source": "block/<name>"}` — the block filling the other seven cells (which must use `multiblock_part`) and the plain block the volume is built from and reverts to. The anchor and its part must both set `hasItem: false` (a structure is built, never held), and `cutout`/`translucent`/`fluid` are rejected, since both halves are ordinary opaque solids to everything but the mesher. Both default `occludes` and `light.passes` to **true**: the structure's surface really does cover its neighbours, and its quads carry no cullface and are lit from the anchor's own cell, so light has to reach that cell or the whole thing renders black. The model is authored in 0..32 model units (the 2x2x2 volume) with the opening on **north**, no `cullface` on any face and no face covering a whole cell plane — see `### Multiblocks` below |
| `kiln` | false | marks a multiblock anchor as a kiln, which is what gives it `KilnManager`'s fuel, ignition, crucible slot and integrity sweep. Only legal on the `multiblock` renderer |
| `wood` | none | required on, and only legal with, `renderer: "tree_wood"`: how hardness and loot follow the thickness stored in each cell's `WoodState` (code 0 = 1 px across .. 15 = full cube) |
| `wood.tipHardness` | 0.25 | the share of `hardness` the thinnest (1 px) wood keeps, 0..1, rising linearly to the full `hardness` at a full cube (`BlockType.TipHardness`, read by `Mining.HardnessOf`). Player-placed tree wood is a full detached cube and therefore as hard as a trunk |
| `wood.loot` | always drop self | thickness bands `[{"minThickness": n, "loot": l}, ...]`, strictly ascending by `minThickness` (0..15) and starting at exactly 0, so every thickness has loot; `loot` is `self` (drop the block, the default), `none` or a loot table name. The band with the largest `minThickness` at or below the cell's thickness is rolled (`WoodLootBand.Select`, through `LootContext.State` on every destruction path): `oak_log` gives the `wood` table (a stick half the time) under 8 px and itself from there, `cactus` always itself |
| `stackMax` | 0 | units that stack in one cell (pebble piles 3, branch piles 4, snow layers 16): the count lives in the chunk state byte, 0 = untouched worldgen (the generator's own default), 1..`stackMax` an explicit stack. 2..16 |
| `stackSolidAt` | 0 | stack count at which a stack block turns solid, for a block whose partial stacks are walked through (layered snow at 16). Only legal from 1..`stackMax` on a `solid: false` stack block |
| `hasItem` | true | enters the blocks pool and can be hotbarred; false for pebbles, branch piles, layered snow and the kiln's anchor and part (fluids force it off). Every `tree_wood` log and every leaf block keeps its item: placing one writes a detached full cube (`WoodState.Detached`) or a persistent leaf (`LeafState`), which never fells with a tree or decays |
| `needsSupport` | false | pops, rolling its loot, when the block below is removed; also makes the block replaceable, so a flow washes it away |
| `replaceableByPlacement` | false | a block placement aimed at this block fills the block's own cell and discards it, instead of filling the cell across the hit face, so a block placed against a partial snow drift rests on the ground instead of hovering a layer above it. Only fires while the cell is not solid, so a full snow stack stays an ordinary block, and the displaced block drops nothing. Independent of `needsSupport`, which is what a flow or a bucket may overwrite. Illegal on an always-solid block, which could never honour it |
| `falls` | false | gravity-affected (sand, gravel): topples into a `FallingBlock` entity when the cell below stops being solid, sinks through water, slides off lone supports; never combines with `fluid`/`needsSupport` |
| `fluid` | false | the block is a flowing fluid (water, lava): forces `solid`, `castsAO`, `hasItem`, `needsSupport` off, `occludes` off and `light.passes` on, and drops nothing whatever the file says |
| `flow` | water behaviour | per-fluid tuning, only legal with `fluid: true`: `tickDivisor` (ticks between updates, lava `2` = half water speed), `range` (sideways spread 1..7, lava `3`), `infinite` (adjacent sources birth new ones, lava `false`), and the `hardensTo` + `hardensTouching` pair (lava hardens to `cobblestone` on touching `block/water`) |
| `onBreak` | `"drop"` | `drop` (roll loot) or `prime_tnt` (no drop, spawns a `PrimedTnt`) |
| `loot` | the table sharing this block's name, else drop self | loot table name; `"none"` drops nothing |
| `sfx` | `hit_default` | sound group played while the block is mined, e.g. `hit_rock` (the stone blocks) or `hit_wood` (the logs and cactus). A group is every `<group>.wav` / `<group>_N.wav` directly under `assets/sfx`, and one variant is picked at random per hit (`SfxManager`). An unknown group warns yellow and uses the default |
| `hitbox` | full cube | selection boxes in cell-local 0..1 space: one `{"min": [x,y,z], "max": [x,y,z]}` object or an array of them; the outline draws all, the raycast only targets through one |
| `collidesByHitbox` | false | the body collides with the declared `hitbox` boxes instead of the full cell, the way tree wood collides with its drawn node and tubes (a 0.25 slab is stepped onto and stood on; `Camera.IsColliding` -> `TreeWoodShape.OverlapsShape`, which tests the registered hitboxes for every block that is not thin or bevelled tree wood); requires `hitbox`, `solid: true`, no `stackMax`, not a fluid. Player body only: drops, slimes and falling blocks still see the full cell |

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
| `entityDamage` | damage one punch or slice deals to an enemy (any `Mob`: a slime or a goblin); default 1 (the bare-hand baseline). A weapon raises it without needing a `tool` block (the obsidian blade is `6`) |
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
`Item.TargetsFluids` (true on `BucketPickupItem`) makes the selection
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
one of the two, so a table that means the item says so whenever a block of the same name could
exist: `leaves.json` (shared by every `*_leaves` block) and `wood.json` (the thin-wood band of the
`tree_wood` logs) both write `"item/branch"`, which keeps the stick an item even beside the
`branch_pile` block. The only roll-count provider is `pebble_count`
(`PebbleGenerator.CountAt(x, z)` at the loot context's cell).

Every destruction path rolls through one place: `DropManager.RollLoot` (a block with no table
drops itself, `LootTable.None` drops nothing) and its spawning wrapper `SpawnLoot`. Mining
(`World.RemoveBlock`), blasts (`TntManager.Detonate`), felled trees (`TopplingTree.TakeDrops`) and
decaying leaves and crumbling orphaned wood (`LeafDecayManager`) all go through it, with the
cell's state byte in `LootContext.State` so tree wood picks its thickness band, so a block's
yield never depends on how it came down.

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

### Tree species schema (`assets/data/trees/<species>.json`)

One file per species; the file name is the species name (`TreeSpeciesRegistry.ByName`, which
`ITEM_DEBUGGER_SELECT` and the `grow` op match). `ContentRegistry.Load` reads the folder **after
the blocks** (`LoadTreeSpecies`; a missing folder is an empty registry), compiles each file through
the pure `TreeSpeciesCompiler.Compile` (DTO `TreeSpeciesDefinition.cs`) into an immutable
`TreeSpecies`, and every error starts `Tree species '<path>'`. Block references take an optional
`block/` prefix and never resolve to an item. Five species ship: `oak`, `spruce` and `cactus`
generate in the world, while `baobab` and `stone_pine` have **no `worldgen` block** and are only
ever grown (`TreeGrowthWriter`, the `grow` op, the item debugger). See `### Trees` for what the
fields drive.

| field | default | meaning |
|---|---|---|
| `wood` | required | the wood block; must use `renderer: "tree_wood"` and must not be a fluid. Two species may share one wood only if they agree on `thickness.rim` |
| `leaves` | none | the leaf block (not the wood, not a fluid). Omitted means a leafless species (cactus), and then `leafCluster` must be omitted too; present means `leafCluster` is required. A leaves block is what `TreeGenerator.IsLeaf` answers for, and so the block whose state byte is a `LeafState` |
| `growth.logic` | required | the registered `IGrowthLogic` direction chooser (`GrowthLogicRegistry`): `oak`, `conifer`, `cactus` (a saguaro pitchfork: each arm leaves the stem level for `CactusGrowthLogic.ArmRun` cells — a pure hash of position, height and side, no RNG draw — then climbs straight up as a prong parallel to the stem and below its top, on distinct sides at least `ArmSpacing` apart), `umbrella` (stone pine: inside `UmbrellaGrowthLogic.BareReach` of the trunk column a limb never forks and climbs as a bare staircase, at most `LimbsPerTrunkNode` limbs a trunk node on distinct sides, so the `pad` leaves sit only at the outer limb ends) or `baobab` |
| `growth.signalEnergy` | required, >= 1 | the energy a growth signal starts with: one per step out from the root, so it bounds the tree's reach. Each tree also rolls its own energy, clamped to 1..this (`IGrowthLogic.TreeEnergy`) |
| `growth.upProbability` | required, >= 0 | weight of growing straight up against the four sideways choices (weight 1 each) |
| `growth.lowestBranchHeight` | required, >= 0 | height above the root below which nothing forks; a `buttress` species needs >= 1 |
| `growth.maturitySteps` | required | `[min, max]`, `1 <= min <= max`: the pulse count a tree grows to maturity, rolled per tree |
| `thickness.tipPx` | required, 1..16 | diameter of a branch tip in pixels |
| `thickness.pipeExponent` | required, finite > 0 | exponent of the pipe model that sums child diameters into the parent's |
| `thickness.maxPx` | required, `tipPx`..128 | largest diameter in pixels; past 16 a node is a full cube, and from 48 a trunk node grows shells (wide trunk) |
| `thickness.girthProfile` | `[[0, 1]]` | `[[height01, multiplier], ...]` along the tree's height: heights start at exactly 0, strictly increase within 0..1, multipliers > 0 (`TreeGrower.Girth`) |
| `thickness.trunkShape` | `circle` | `circle` or `square` (`TrunkShape`): a wide trunk's block footprint |
| `thickness.rim` | `block` | `block` or `pixel` (`TrunkRim`): `pixel` bevels the outer shells of a wide trunk with pixel-stepped quarter-rounds |
| `leafCluster.shape` | `blob` | `blob`, `cone` or `pad` (`LeafClusterShape`; a pad is a flat cap on the tip: a disc of radius `max(1, r - 1)` at the tip's level and one of radius `r` above it, nothing below, so the limb under it stays bare) |
| `leafCluster.radius` | required, 1..8 | the full cluster radius in blocks around each live tip, reached once the trunk is thick enough (see `minRadius`) |
| `leafCluster.minRadius` | `radius` | 1..`radius`: the cluster radius of a tree whose root is 1 px. The tree's one radius (`LeafCluster.ClusterRadius`) runs from this up to `radius` with the root cell's diameter, so a young tree wears a small crown; the default means no scaling |
| `leafCluster.fullRadiusPx` | 16 | 1..`min(maxPx, 16)`: the root diameter in pixels at which the cluster reaches `radius`. Capped at one block because that is the most the wood state byte stores, so a wide-trunk species (baobab) only scales as a seedling |
| `leafCluster.hydration` | required, 1..7 | hydration a leaf right at a tip gets (`LeafState.MaxHydration` is 7); it falls by one per Manhattan step, and a leaf needs at least 1 to exist |
| `leafCluster.tipMaxPx` | required, 1..`maxPx` | the thickest childless wood (in pixels) that still counts as a live tip and grows a cluster |
| `features[].type` | none | a registered post-growth `ITreeFeature` (`TreeFeatureRegistry`): `roots` (feature nodes walked down through free cells) or `buttress` (full-cube fins along the four axes at the root's level); each at most once per species |
| `features[].depth` | 0 | >= 0; `roots` needs >= 1 (its depth in blocks), `buttress` takes exactly 0 |
| `lod.crownHeight` | required | `[min, max]`, `0 <= min <= max`: the far rings' proxy crown height, rolled per tree from its placement hash |
| `lod.crownRadius` | required, >= 0 | the proxy crown radius in blocks |
| `lod.profile` | `dome` | `dome`, `cone` or `column` (`LodCrownProfile`, the silhouette `LodCrownProxy.TopOffset` draws) |
| `worldgen.biomes` | required | non-empty, duplicate-free list of `Biome` names (`forest`, `desert`, `ocean`, `taiga`, `frozen`) the species is planted in |
| `worldgen.density` | required, 0..1 | chance per candidate column |
| `worldgen.spacing` | required, >= 0 | minimum gap to another trunk in blocks (the lowest placement hash wins) |
| `worldgen.maxSlope` | required, >= 0 | largest ground height step across the footprint |
| `worldgen.minSoil` | 0 | soil depth the column needs, >= 0 (oak and spruce 2, so they never root on bare rock) |
| `worldgen.minGround` / `maxGround` | required | the ground height band the species grows in, `minGround <= maxGround` |
| `worldgen.openness` | 1 | 0..1, the share of the forest-openness thinning (`TerrainGenerator.ForestOpenness`) applied to this species (spruce 0.5, cactus 0) |
| `worldgen.spawnClear` | true | whether the species keeps out of the spawn clearing (`TreeSpawnClearRadius`) |

`worldgen` as a whole is optional: leaving it out keeps the species out of worldgen entirely
(`TreeSpecies.Worldgen` is null and `TreeGenerator.TryGetTree` never picks it).

### Trees

Trees are grown, not drawn from templates: one generic, deterministic, step-based engine under
`src/WorldGen/Trees/` grows every species from its JSON (`### Tree species schema` above), and the
world stores the result as **plain blocks plus the state byte** — one `tree_wood` block and one
leaves block per species, no per-tree registry or entity.

- **Wood state** (`WoodState`): bits 0-3 the thickness code `n` (diameter `n + 1` px, 15 =
  `FullCube`), bits 4-6 the parent direction in `Chunk.CubeFaces` order (0-5, 6 = `ParentRoot`,
  7 = `ParentDetached`) and bit 7 `ShellBit` (a girth cell of a wide trunk). The one connection
  rule is `TreeWoodShape.Connects`: same id, neither side detached, and one cell's parent points at
  the other — so siblings and two neighbouring trees never weld, and the tree graph can be read back
  out of the blocks (`TreeGraph.FromWorld`). Placed wood is `WoodState.Detached`, a full cube that
  never welds or fells.
- **Leaf state** (`LeafState`): bits 0-2 hydration, bit 3 `PersistentBit` (player-placed leaves,
  which never decay). Hydration is `leafCluster.hydration` minus the Manhattan distance to the
  nearest **live tip** whose cluster shape holds the cell (`LeafCluster.HydrationFromTip`, the one
  per-tip formula, behind `HydrationAt`, `Collect` and decay alike). The cluster radius is one per
  tree and follows the trunk: `LeafCluster.ClusterRadius` scales `minRadius..radius` by the
  tree's **support**, the root cell's stored diameter (`LeafCluster.SupportPx`, the quantised
  root `ThicknessPx` = `WoodState.DiameterPx` of the root cell, capped at 16 px), up to
  `fullRadiusPx`. It changes shape membership only (hydration stays `hydration` minus the
  distance), and every shape is nested — a smaller radius is a subset of a larger one — which is
  what keeps decay's full-radius, best-first tip table exact; a live tip is a non-detached,
  non-shell cell of the species' wood at or under `tipMaxPx` with no child, so a thick stub left by
  a cut is not a tip and its leaves decay. The mesher never reads `LeafState`.

**Growth engine** (GL-free, headless). `TreeGraph` holds int-indexed nodes (cell, parent and
parent direction, children, the girth-free `PipePx`, the displayed `ThicknessPx`, `IsFeature`, a
`FloorPx` that a tree rebuilt from the world never thins below). `ITreeSpace.IsFree` is the obstacle
view, implemented by `VirtualTreeSpace` (a ground-height delegate, optional carved cells and an
occupied set; `FromTerrain` reads `Chunk.GetColumnData`) for worldgen and the item debugger, and by
`WorldTreeSpace` for live chunks. `TreeGrower.Pulse` sends one Dynamic-Trees style signal up from
the root: each step it weighs the six directions (sideways 1, up `upProbability`, existing limbs
followed by their `PipePx`, no forks under `lowestBranchHeight`, never down, blocked cells masked)
and lets the species' `IGrowthLogic.AdjustWeights` lower, zero or scale them, then either follows a
child or grows exactly one new face-adjacent node and stops, thickening the path on the way back
down. Thickness follows the pipe model on `PipePx` (`pipeExponent`), and the displayed
`ThicknessPx = min(maxPx, max(tipPx, PipePx * Girth))` is raised to the thickest child, so the
girth profile applies once and a parent is never thinner than a child. Everything is deterministic
from `TreeGrower.SeedFor(x, y, z, worldSeed, species)` (position, world seed and an FNV-1a hash of
the species name) through one `SplitMix64` stream:
- `GrowToMaturity` draws the maturity step count and the per-tree energy first, runs that many
  pulses, one final bottom-up `Thicken` (so the girth profile sees the final height), then the
  species' `features` on the same stream;
- `GrowSteps(..., steps, applyFeatures)` replays exactly that stream for any pulse count (the item
  debugger's scrub path); `GrowSteps(MaturityStepsAt(...), applyFeatures: true)` *is*
  `GrowToMaturity`, and without features a shorter grow's nodes are a prefix of a longer one's.
  `MaturityStepsAt` and `TreeEnergyAt` replay the two rolls on their own;
- `GrowFurther` is the reactive path: `Thicken`, N pulses under a node cap, `Thicken`, and the
  features only when asked (a freshly planted sapling), since a feature is not idempotent.

**Rasteriser.** `TreeRasterizer.Rasterize(graph, species, space, out woodCount)` turns a graph into
`TreeCell`s (cell, block, state) in a load-bearing order: the graph nodes in index order (growth
nodes, then feature nodes), then the shells, which end the wood at `woodCount`, then the leaves
(`LeafCluster.Collect`, which never lands a leaf on a shell). The thickness code is
`ThicknessCode(px)` = `ceil(px) - 1` clamped at the full cube. Worldgen stamping, the first far-LOD
ring, reactive growth, the felling and decay tests and the item debugger all share it.
`ReachOf(species)` is the tree's (horizontal, up, down) bound from its root — the energy plus the
leaf radius, widened by the shell radius and the buttress reach — which sizes chunk-border margins
and the decay schedule.

**Wide trunks** are a core plus shells, and only the rasteriser makes them. A node on the
**trunk chain** (`TrunkChain`: the root, then repeated straight-up children) whose diameter `d`
gives `BlockRadius(d)` = `floor((d/16 - 1) / 2)` (capped at `MaxShellRadius`) above 0 stays the
graph node as the **core**, and shell cells fill its footprint at the same Y — `InFootprint`:
Chebyshev `<= r` for a `square` trunk, `dx² + dz² <= r² + r` for a `circle` — so the `2r + 1`
footprint never exceeds the diameter and shells first appear at 48 px. A shell is a full cube with
the shell bit and its parent one step toward the core (`ShellParentDir`, along X when
`|dx| >= |dz|`), so a diagonal shell chains through an edge shell; the `ShellOffsets` tables are
sorted parent-first, and a shell is only emitted when its parent was (or is a full-cube feature
node at the core's Y, a buttress fin) and the space `AcceptsShell`. Because only the trunk chain
grows shells, every core is alone at its Y and the shells are a function of the node set, never of
index order; a wide limb elsewhere renders as a 16 px full cube, and a species that never passes
16 px rasterises exactly as before shells existed.

**Mesh, collision and the pixel rim.** `tree_wood` meshes on the per-voxel path, never greedy:
`Chunk.EmitTreeWood` draws a thin cell as a node box sized `TreeWoodShape.HalfWidth(t)` plus one
capless tube toward every connection at the thinner of the two widths (`TubeHalf`), with cropped
UVs and smooth light; a full-thickness or detached cell takes the model path and culls against a
same-id full cube. A wood block whose species sets `rim: "pixel"` (`BlockType.PixelRimById`, filled
by `ContentRegistry.LoadTreeSpecies`) gets its outer shells bevelled from their eight horizontal
neighbours: `TreeWoodShape.RimCornerMask` carves a corner when neither of its two edge neighbours
is same-id attached full-cube wood (a cell only ever removes its own material, so diagonals never
matter), `RimBoxes` turns the mask into stair-stepped full-height boxes (`RimInsetPx`, at most
`MaxRimBoxes`, `TreeRimBevelPx` deep), and `Chunk.EmitRimWood` meshes exactly those boxes from the
18³ snapshot with caps that subtract a bevelled cell above or below. The hitboxes match the mesh:
`TreeWoodShape.GetBoxes`/`GetBoxesAt`/`OverlapsShape` (node, tubes and rim boxes, allocation-free)
feed the raycast, the selection outline and `Camera.IsColliding`.

**Worldgen** (`TreeGenerator`, a pure function of position like the rest of worldgen).
`TreeGenerator.TryGetTree(x, z, ...)` keeps the placement gates per species from its `worldgen`
block (one hash stream, biome -> species in ordinal name order, density, lowest-hash spacing,
slope, soil, ground band, water, openness, the spawn clearing). Every tree of a 64x64 XZ cell
(`TreeCellShift`/`TreeCellBlocks`, `TreeGenerator.CellOf`) then grows together as one `TreeBatch`
in placement-hash order inside one `VirtualTreeSpace`, each grown tree's cells occupied before the
next grows, so neighbouring crowns grow around each other; the trees of the eight neighbouring
cells count as obstacles only as their cached **solo** shapes (`ShapeOf`), which keeps a batch a
pure function of (cell, seed) and independent of chunk order. `Stamp` writes wood with its state
into air or leaves, then leaves into air, in a fixed scan order (the root walk extends the trunk
down through cave air only inside the chunk holding the base). The far LOD's first rings read the
stamped canopy (`TreeGenerator.TryGetCanopy`), while the coarse rings (`LodLevelSpec.ProxyCanopy`)
read a proxy rolled from the same placement: the species' `lod` crown height and radius through
`LodCrownProxy.TopOffset`'s dome/cone/column silhouette, so the far sweep never grows a tree. The
solo shapes, the batches and both canopy tables are `GenerationalCache` sites (`TreeShapeCache*`,
`TreeBatchCache*`, `TreeCanopyCache*`, `TreeProxyCanopyCache*` in `WorldGenerationConfig`).

**Leaf decay** (`Systems/LeafDecayManager.cs`, owned by `World`, main thread only, never while
loading, the `leafDecay` profiler row) is a `FluidManager`-style scheduled-cell simulation. An edit
schedules the cells around it (`ScheduleAround`/`ScheduleBox`, which grow the box by
`ScheduleReach` so callers never know a radius — see the edit-path invariant below); a tick
re-derives each scheduled non-persistent leaf's hydration as the best `HydrationFromTip` over the
live tips in its species' cluster box (a full-radius offset table, best first, with each candidate
tip's own tree radius re-checked: `TryTipSupport` walks the tip's parent chain under the
`WalkParentChain` link rules to the root cell and reads its diameter, memoised per `Update` in
`_tipSupport`; an unloaded read freezes the leaf), so a settled canopy reads back bit for bit and costs nothing,
and a leaf left at 0 decays to air (at most `LeafDecayMaxRemovalsPerTick` a tick, in a scattered
order, so a crown dissolves speckled), rolling its loot through `DropManager.SpawnLoot` with a few
leaf particles. The same manager crumbles **orphaned wood**: a scheduled non-root, non-detached
`tree_wood` cell that `TreeSupport` finds no longer anchored (`TickWood`, a memoised parent-chain
fast path plus the full search on a broken chain) drops with its thickness-band loot and schedules
its reach box, so a blasted subtree crumbles outward a layer per tick. Any read in an unloaded
chunk drops the cell from the schedule (it freezes, never decays), and a chunk load schedules
nothing. Tuning: `LeafDecayTickSeconds`, `MaxLeafDecayTicksPerFrame`, `MaxLeafDecayCellsPerFrame`,
`MaxLeafDecayBacklogCells`.

**Felling** (`World.FellWood` -> `TopplingTree.Collect`). Mining tree wood fells the cells that
lose every path to an anchor once the cut cell is gone, through the GL-free bounded search
`TreeSupport` (`TreeSupportResult` Supported / Unsupported / Unknown): links are parent edges
(`TreeSupport.Links`) plus vertical shell-to-shell adjacency, anchors are root cells and shells
standing on real ground (`TreeSupport.IsAnchor`: solid, neither tree wood nor leaves), and an
unloaded read is no link. So a cut limb drops only the limb, a cut 1-wide trunk drops the crown,
and a wide trunk falls only once a whole horizontal slice of core and shells is cut. Canopy leaves
within Chebyshev 2 of the falling wood come along. The search is capped at `TreeFellWoodCap` wood cells and the whole fell at
`TreeFellTotalCap` (leaves past it are left standing and decay). The falling tree is meshed by the
real chunk mesher in one 16³ scratch chunk (`TopplingTree.BuildMeshData`) and drops through
`TopplingTree.TakeDrops`.

**Reactive growth** (`World.TryGrowTree(species, x, yHint, z, steps)`, refused while loading or
for an unknown species; the `grow` scenario op calls it). `TreeGrowthWriter` (`Systems/`, GL-free,
main thread; `TreeGrowthResult`/`TreeGrowthStatus` Grew / Planted / NoGround / NotGrowable) scans
the target column for the species' root and rebuilds the tree with `TreeGraph.FromWorld` (a
breadth-first walk over child edges only, so detached and cut-off wood is never reached; every node
floored at its stored diameter, a wide core's floor recovered from its shell run, nodes below the
root loaded as feature nodes; null on an unloaded read or past `TreeGrowNodeCap`), or plants a
sapling on solid ground when there is none. It then runs `GrowFurther` (at most
`TreeGrowMaxPulsesPerCall` pulses) in a `WorldTreeSpace` — a cell is free when it is loaded, not
reserved by a falling block, air or a non-persistent leaf of the species, and not the missing parent
of an orphan, which is what keeps a cut branch cut — rasterises once and writes back only the
difference: existing nodes are only re-coded thicker, new wood goes only into free cells, leaves
only around changed tips — or, when the thicker trunk changed the tree's `ClusterRadius`, around
every original tip still in the world (never one whose wood the write skipped, whose leaves would
hang off wood that never landed) — and shells follow the **new-ring-only** rule (inside the footprint a core
already had, existing shells are only re-coded, so a mined shell or a partial slice cut stays
mined). All writes are `Chunk.SetBlockRaw`, followed by `FluidManager.ScheduleAround` per changed
id, one `LeafDecayManager.ScheduleBox` over the written box (pruning is left to decay), a
`ChunkManager.MarkDirty` per touched chunk and `TreeGenerator.PoisonCanopy(x, z, 0)` plus
`LodManager.MarkEditedColumn` per changed column.

**The item debugger's TREES tab** (`OpenGLTest.ItemDebugger/TreePreviewState.cs`, see "Build and
run") grows any species pulse by pulse over flat grass at a fixed seed through `GrowSteps`, and
meshes it with `CellBatchMesher` (`src/CellBatchMesher.cs`): the real chunk mesher over as many
scratch chunks as the cells span, each at its real position inside an empty one-chunk ring, so a
wide or tall tree is never clipped at a chunk seam and wears the real biome tint.

Known gaps, all open: `TreeGenerator.CanopyDisturbed` still clears wholesale at its cap
(`TODO.md` `M-10`); a grown tree shows no far-LOD canopy until reload, since `PoisonCanopy` only
removes canopy (`M-13`); and the wide-trunk residue of `M-14` — the felled mesh is drawn only
inside its 16³ scratch window (the drops are intact), a wide core cut while its shells stand
leaves the crown unreachable for reactive growth, and a bevelled shell's cap can go stale when an
edit lands in an edge-diagonal chunk that face-adjacent dirtying does not remesh.

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
progress is in-memory only: it does not survive a restart or a save & quit (see "Saves"), and an unloaded mold cell freezes
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
- **Per-block state byte**: alongside `Chunk._blocks` each chunk has a lazily allocated `volatile byte[]? _state` (null = all zero), read through `Chunk.GetLocalState` / `ChunkManager.GetStateGlobal` / `TryGetCell` and written only by `Chunk.SetBlockRaw` (no remesh, no scheduling — the fluid tick batches those). Five features share it, one per block: **fluid levels** (`FluidState` decodes it as source `0`, falling `8` or flowing level `1..7`), **stack counts** (a `stackMax` block's 0 = untouched worldgen, 1..`stackMax` an explicit pile), a **multiblock's facing and offset** (`Multiblock.EncodeState`, `facing | dx<<2 | dy<<3 | dz<<4`, written by `Multiblock.Convert` through `SetBlockRaw`), **tree wood** (`WoodState` on a `tree_wood` block: bits 0-3 the thickness code, `FullCube` = 15 being a whole 16 px cell, bits 4-6 the parent direction in `Chunk.CubeFaces` order or `ParentRoot`/`ParentDetached`, bit 7 the shell flag) and **leaves** (`LeafState` on a leaf block: bits 0-2 the hydration, bit 3 the persistent flag) — see `### Trees`. The tree states are written by `TreeGenerator.Stamp` into the generated state array, by `TreeGrowthWriter` and `LeafDecayManager` through `SetBlockRaw`, and by placement, which passes an explicit state to `PlaceBlockAt` (`World.PlaceBlockAtFace` gives a placed log `WoodState.Detached` and a placed leaf a persistent `LeafState`). Which meaning a byte carries is decided entirely by the block id that owns the cell, so a new user must pick a block kind, never a spare bit. **Every write that is not one of those zeroes it** (`RemoveBlockAt`, `ClearBlockAt` and `PlaceBlockAt` with its default state `0` — which also treats a fluid cell as replaceable), and `Multiblock.Revert` likewise writes its source block with an explicit `0`, so a stale level, count, offset, thickness or hydration can never outlive the block that owned it. Meshing gets it as a second padded snapshot from `FillPaddedSnapshot(padded, paddedState, neighbors, neighborStates)`, filled from the neighbourhood `TryResolveNeighbors` resolved for the build; the fluid simulation must read cells through `TryGetCell`/`ChunkAt`, never `GetBlockGlobal`, which reports unloaded chunks as air.
- **Station block entities**: `CampfireManager`, `QuernManager`, `KilnManager` and `CastingManager` are all owned by `World`, all **main thread only** and all in-memory only — a lit campfire, a spinning quern, a kiln's fuel and crucible and a mold's cast progress are lost on a restart, a save & quit or a chunk reload, even though the chunk's blocks and state bytes around them are saved (see "Saves"). Each is keyed by the cell (the kiln by its structure's anchor), each is ticked from `World.Update` and each has a `NotifyBlockRemoved` that `World.RemoveBlock` calls so a broken station drops what it held. `World.FireCells` gathers every burning cell (campfires and lit kilns) once a frame and feeds both the fire billboards (`FireParticleSystem.SpawnOffset`) and the warm point lights (`RenderManager.FireLightOffset`), so a new fire source only has to appear in that list. Their render passes — `RenderQuerns`, `RenderKilns`, `RenderCastings` — sit inside the existing `entityPass` profiler span rather than adding sections of their own.
- **Fluid tick**: `Systems/FluidManager.cs` (owned by `World`, main thread only) is a *scheduled-cell* simulation, not a scan. `World.Update` calls it once per frame after `UpdateToppling`; it runs whole ticks every `WorldGenerationConfig.FluidTickSeconds` (0.25 s), at most `MaxFluidTicksPerFrame` of them per frame, and drops the leftover time so a hitch never leaves it spiralling. Each tick takes the set of cells scheduled by the previous one (packed as `long`, 21 bits per axis — the same budget `ChunkKey` (`src/ChunkKey.cs`) uses to pack a **chunk** position for `ChunkMap` and for `ChunkManager`'s dirty/queued/generating/placement dictionaries; `FluidManager`'s packer is block-scale, `ChunkKey`'s is chunk-scale, and neither floors nor truncates differently from `VoxelCoords.ChunkPosOf`), recomputes every **non-source** fluid cell from its neighbours (Minecraft's `getNewLiquid`: two adjacent sources over ground that cannot drain become a source themselves, fluid directly above means falling, otherwise one level thinner than the fullest neighbour, and nothing left means the cell drains to air), then spreads it downwards, or sideways toward the nearest hole within `FluidSlopeSearchDistance` (ties spread evenly). The level rules are per-fluid data (`flow.*` on the block: lava updates every other tick, spreads 3 instead of 7 and never forms new sources). Contact hardening runs before all of it: a fluid cell touching its `hardensTouching` fluid becomes `hardensTo` (lava beside water becomes cobblestone), and a flow settling against a fluid that hardens on its touch crusts over the same way, so water reaching lava also yields cobblestone whichever side ticks first. Spreading only ever writes into non-fluid `IsReplaceable` cells, popping the fragile block it washes away through `DropManager.SpawnLoot`; a cell that already holds fluid recomputes itself on its own tick. Writes go through `Chunk.SetBlockRaw` and dirty only the cell's own chunk plus the face-adjacent chunk on each axis where the cell sits on a border, batched into `MarkDirty` at the end of the tick — not `ImmediateRemesh`, and never `MarkAreaDirty`, because a fluid passes light and casts no AO. A cell reschedules itself and its neighbourhood exactly when it changed or wrote into a neighbour, so settled water costs nothing.
- **Waking worldgen water**: the sea is generated as static source cells, so a cave or entrance carved into a land column beside it would leave a wall of water standing against worldgen air that nothing ever schedules. `FluidManager.WakeLoadedChunks(chunks)` — called from `World.Update` each frame, immediately before the fluid tick — fixes that by scheduling the frontier of its water against a carved cavity when it loads, so the sea pours into whatever breached it exactly like Minecraft water does when you dig into an ocean. Only cells at or below their column's natural terrain top count (`FluidManager.IsCarvedFlowTarget`), so open air above the terrain — a ravine, a beach, a hillside — is never woken and generated water stays static across it. It has two halves: the **interior**, collected on the generation worker at the end of `Chunk.GenerateBlockData` into `Chunk.FluidFrontier` (local block indices of every fluid cell with a `FluidManager.IsCarvedFlowTarget` neighbour in -X/+X/-Z/+Z/-Y — never +Y, since water does not flow upward and every sea-surface cell has air above it; only its own `_blocks` are read, so worldgen stays a pure per-chunk function), and the **borders**, which need both chunks loaded and are therefore scanned on the main thread over the shared 16x16 plane of each face neighbour, scheduling whichever side holds the fluid when the other side is a carved cavity (the vertical faces only ever schedule the fluid above the hole). Chunks entirely above every level they could share with a neighbour (`ColumnData.MaxWaterLevel`, never below `SeaLevel`) skip the border scan — generated water cannot reach them, and player-placed water is woken by the edit paths. The queue of newly generated chunks is `ChunkManager.TryDequeueLoaded`; a consumer must re-check that the chunk is still the live one at its position, because an unload can race the queue. Once the frontier has settled, it costs nothing again. Deep lava pockets wake through the same path: the frontier scan is fluid-generic, and chunks at or below `LavaMaxY` report theirs even when they never held sea water.
- **Every block edit path must call `FluidManager.ScheduleAround(cell)`** (or `ScheduleBox(min, max)` for a batch) or the water beside the edit will never notice it: `World.RemoveBlock`, `World.FellWood` (per cleared cell, and in both single-block branches), `World.TryPlaceBlockFromRaycast`, `TntManager.Detonate` (`ScheduleBox` over the blast box grown by one; the blast also **skips fluid cells**, since a dry crater under water would just refill from every side) and bucket edits through `FluidManager.SetCellImmediate`, which is the only fluid write that remeshes synchronously. Mining never takes a fluid out: `RemoveBlock` returns early on `BlockType.IsFluid`, so only a bucket removes fluids.
- **Every block edit path must also call `LeafDecayManager.ScheduleAround(cell)`** (or `ScheduleBox(min, max)` for a batch) or the leaves near the edit never re-derive their hydration: a cut limb's canopy would hang forever, and an orphaned wood cell would never crumble. `Systems/LeafDecayManager.cs` (owned by `World` as `_leafDecay`, main thread only) grows each scheduled box by its `ScheduleReach` itself, so a caller passes only the edited cells. The current call sites: `World.RemoveBlock`; `World.FellWood` (`ScheduleAround` in both single-block branches, one `ScheduleBox` over the whole felled batch, never one per cell); `World.PlaceBlockAtFace`, which every placement goes through (`TryPlaceBlockFromRaycast` and the item-use `IItemUseWorld.PlaceBlockAtFace`); the `IItemUseWorld` bucket and TNT-prime wrappers (`ScoopFluidCell`, `PourFluidCell`, `RemoveCellForPrime`), scheduled for uniformity; `TntManager.Detonate` (`ScheduleBox` over the blast box); and `TreeGrowthWriter` (one `ScheduleBox` over the box it wrote). A new edit path adds its call next to its `FluidManager.ScheduleAround`.
- **Gravity**: `Systems/FallingBlockManager.cs` (owned by `World`, main thread only) topples `falls` blocks into `FallingBlock` entities and writes rested cubes back as blocks. Every edit path that can unsupport one must call `TryTopple` above it (`World.RemoveBlock`, `World.FellWood` per cleared cell, `World.TryPlaceBlockFromRaycast` at the placed cell, `TntManager.Detonate` via `CheckColumn` bottom-up over the blast box); fluids never count as support, so fluid edits need no hook. A landing cube slides with chance (8 - solid neighbours of its support) / 8 toward a random clear side, corners included (a corner needs its two flanking sides and the three cells above it clear), committing to its direction across landings, and drops straight once past the edge. Sand stacks on sand: a falls-block support never slides, so towers rebuild. A calm landing converts at once so a collapsing tower settles bottom-up instead of interpenetrating, and a cube that would rest over air pops out as a drop instead of floating. A live cube owns its cell: `TryPlaceBlockFromRaycast` refuses placements into one, cubes shove the player aside one-way (`Camera.PushOutOf`, never through walls, never while flying), and a cube motionless-but-unsettled for 5 s breaks into its usual loot instead of hovering. Shorelines generate from sand (`TerrainGenerator.IsBeachHeight`, gravel-capped where `IsGravelPatch`); the far LOD wears sand and gravel there too, the gravel read with one noise eval per cell.
- Worldgen must stay deterministic and thread-safe (`FastNoiseLite.GetNoise` is read-only after setup; noise instances are configured once in static constructors and never re-tuned per call). All noise seeds derive from `WorldGenerationConfig.Seed` plus fixed per-channel offsets (`TerrainGenerator.MakeNoise`); `Seed` reads `WorldSeed.Value`, which `WorldSeed.Apply` sets (rebuilding every noise from it) before a World is built, never while one is live (see "Saves"). Before player edits, terrain obeys:
  - **Nothing generated is ever solid above `height(x,z)`** — heights come from `Chunk.GetColumnData(...).Heights` (or the `GetColumnHeights` wrapper) and already include ravines, which are cut into the heightmap so the far LOD sees them too. The ravine cut fades out across the shore band (`OceanShoreWeight`..0.5), so it never slices the ocean's edge; the block at `height` is solid unless a cave or entrance carved it.
  - Both carvers only ever **remove** blocks: `Chunk.GenerateBlockData` fills the columns (grass at depth 0, dirt down to `DirtDepth`, stone below), then `NoiseCaves.Carve` and `WormCarver.Carve` clear air out of the result and `_solidCount` is recounted.
  - **Water** fills every air cell up to the column's own level in a **wet** column, in `Chunk.FillWater` after both carvers and before trees and pebbles: an ocean floor, or a lake or river bed, where a hashed lake bowl or a warped river ridge channel carves the ground below it. There is **no single water plane**: `ColumnData.WaterLevel` (`TerrainGenerator.WaterLevelAt`) is sea level for the ocean and for rivers, but a lake's **own** surface, so a pond rests in the ground it was dug into instead of at the bottom of a crater dug to sea level. A lake takes that surface from its own rim (`TerrainGenerator.TryLakeSite`): the lowest ground on the rim minus `LakeBankDepth`, and a candidate whose rim climbs more than `LakeSiteFlatness` is dropped rather than dug into a hillside, which is what keeps every bank it floods against above the water. A candidate standing above `SeaLevel` is also dropped when a river channel crosses its footprint (`TerrainGenerator.RiverCrossesSite`, sampled every few blocks across the disc), because the rim is read from `LandHeight` before any river cut and the channel would carve a bank below the pond's surface (rule test `LakeTests.NoRiverChannelCrossesAnElevatedPond`). Its radius (skewed small by `LakeRadiusSkew`), depth and outline (wobbled by `LakeShapeNoise`, so a shoreline is ragged rather than a circle) all vary per lake. `TerrainGenerator.IsWaterColumn` is now just `height < WaterLevelAt(x, z)`, so the carve in `Compute` and the fill agree by construction, and the far LOD draws its sheet at the same per-cell level. Wetness reads the column's `ColumnData.Bed` — the profile before a ravine cut it — not the ravine-cut `Heights`, so a ravine that merely deepens an otherwise dry column holds no water instead of flooding as a narrow spike, while a ravine under a real lake or river just deepens that body (and the cut fades out across the shore band, so no ravine ever cuts the ocean floor). Every other below-level basin is left **dry** — the forest is not flooded wholesale now that ocean carries the bulk of the water — and a desert stays dry; sand and gravel ring actual water only (`TerrainGenerator.SurfaceWaterWeight` gates the height band), never a dry ravine floor in the band. Both the carve and the flood test are scaled by `TerrainGenerator.SurfaceWaterGate` (`WaterDesertFadeStart`/`WaterDesertFadeEnd`), which fades the body out over the last stretch before the desert, so a pond closes on its own forest rim instead of ending in a vertical wall where the hard forest/desert classification flips, while lakes away from a desert keep their full depth. Sea-floor caves still flood, nothing grows in water (wet columns top out in dirt instead of grass), and `IsSubmerged(height)` is only the waterline height test, since wetness is a per-column level now. Generated water is always a **source** (state `0`). A cold column's top water cell is then replaced by `ice` in `SnowGenerator.Stamp` (see Biomes), so a cold ocean or lake freezes into a walkable sheet. `WaterEnabled = false` turns the water off; `LakesAndRiversEnabled = false` leaves oceans only.
  - **Lava pockets** pond over dry cave floors at/below `LavaMaxY` (`LavaGenerator.Stamp`, after the sea so flooded caves stay water): a patch mask plus a per-cell roll, buried under the terrain so ravines and entrances stay dry. Generated lava is likewise all sources; set `LavaEnabled = false` to turn it off.
  - Caves fade out within ~`CaveSurfaceFadeEnd` blocks of the surface (`WormSurfaceMargin` for worms), except inside entrance zones — sparse patches where `ColumnData.EntranceWeight` lifts the fade and caves reach the sky. The entrance weight fades out before the shore band (`EntranceShoreFade`, see `OceanShoreWeight`), so a cave never breaches a beach or the sea floor into a water-filled hole.
  - Worldgen is a **pure function of world position**: each chunk generates its own blocks with no cross-chunk writes, so neighbours agree at their borders without shared state. Worm paths are cached per 64-block XZ cell in `WormCarver` exactly the way column data is cached per chunk column in `Chunk.GetColumnData`, and so are the four tree caches in `TreeGenerator` (see `### Trees`): the cell batches (`BatchCache`, every tree rooted in one 64x64 XZ cell grown together, shared by `Stamp` on the generation workers and the first LOD ring's canopy on the LOD builder), the solo tree shapes (`ShapeCache`, which `BuildBatch` reads as the neighbouring cells' obstacles), the grown canopy tops (`CanopyCache`) and the far rings' crown-proxy canopy (`ProxyCanopyCache`), the last two read by their one engine caller `LodRegion.BuildMeshData` once per *fine* column of a region. All six go through `GenerationalCache<TKey, TValue>` (`WorldGen/GenerationalCache.cs`): the cache is split into G generation dictionaries published as one immutable node swapped by `Interlocked.CompareExchange`, a read probes the newest first, a miss computes into the newest, and when the newest fills its `capacity / G` insert budget the node rotates and the *oldest* generation is dropped — so eviction retains the most recently used generations instead of clearing wholesale, and an entry read while it sits in the oldest generation is promoted forward (a second chance). Entries are deterministic, so a race, an eviction or a duplicate yields identical values, and the worst an evicted entry can ever cost is a recompute. Capacities and generation counts all live in `WorldGenerationConfig`, one pair per site: `ColumnCacheCapacity`/`ColumnCacheGenerations`, `CellCacheCapacity`/`CellCacheGenerations`, `TreeBatchCacheCapacity`/`TreeBatchCacheGenerations`, `TreeShapeCacheCapacity`/`TreeShapeCacheGenerations`, `TreeCanopyCacheCapacity`/`TreeCanopyCacheGenerations` and `TreeProxyCanopyCacheCapacity`/`TreeProxyCanopyCacheGenerations` — every site ships at 4 generations, and that count is a re-pricing lever that may only be moved against a real capture, never blind. The factory a site passes to `GetOrAdd` must be a `static`, non-capturing delegate *field* (`Chunk.ComputeColumn`, `WormCarver.BuildCellFor`, `TreeGenerator.BuildShapeFor`, `BuildBatchFor`, `BuildCanopyFor`, `BuildProxyCanopyFor`), because the factory takes the key alone: a lambda written at the call site that captured locals would allocate a closure on every call, hits included, on the generation workers — recomputing those values from the key on a miss is the cheaper trade. **Standing rule: only values that are a pure function of the key may live in that type.** State carrying *player edits* must never be moved into it, because there eviction is data loss rather than a recompute — `LodRegion.ColumnCaps` (mined-column LOD caps) and `TreeGenerator.CanopyDisturbed` (felled-canopy markers) are the two current examples, and both are tracked as their own items rather than converted. They are still plain process-global tables; `LodEditState` exports and imports them per world (with the `LodSurface` overrides) and `WorldSeed.Apply` resets them between worlds.
  - 3D cave noise is sampled on a `CaveNoiseStride` lattice (aligned to world coordinates so adjacent chunks share boundary samples) and trilinearly interpolated per block before thresholding.
- **Far-terrain LOD**: full-detail chunks load to `ChunkRadius` (16 chunks = 256 blocks); a cascade of LOD rings defined in `LodRegion.Levels` extends the render distance (level 1: 2-block cells to 512, level 2: 4-block cells to 1024, level 3: 8-block cells to 2048 blocks). Regions are columns meshed at reduced resolution straight from the heightmap cache (no block data). Each level's heights are eroded (min over a widened fine-column window) and biased downward so its mesh sits strictly inside real terrain *and* strictly below the next finer level — finer geometry always buries coarser via the depth buffer, so no masking or camera-driven remeshing exists. Because those shells are stacked, only one level may actually be drawn per area — otherwise they are all visible once the camera is inside the terrain — so `LodManager.UpdateBuriedFlags` marks each region whose footprint is fully covered by *loaded* finer-level regions (region grids nest exactly: one region = ratio×ratio regions of the finer level) and `RenderLod` skips those. Testing loaded regions rather than each level's radius means an unbuilt finer region can never open a hole in the horizon. The finest level (0) has no finer level to hide it, so it is additionally marked buried wherever the full-detail chunks its shell could show through are loaded *and* meshed (`LodManager.IsCoveredByChunks`, using the per-column inclusive chunk-Y ranges recorded at region build in `LodRegion.ColumnCyMin/ColumnCyMax` and `Chunk.IsMeshed`, which unlike `HasMesh` is true for all-air and fully enclosed chunks too). This is needed because caves cut into the band the shell occupies and would otherwise show it from inside; presence is tested per chunk rather than by horizontal distance because the load volume is a sphere. LOD side faces use grass at the top cell, dirt above `NominalDirtDepth` and the stone tile below it, so far cliffs match near ones; a desert cell wears sand over sandstone and a sea-floor cell the sand/gravel/stone band its depth picks (`LodRegion.BuildMeshData` reads `Chunk.GetColumnBiomes`). The nesting invariants (`SampleMargin(L+1) >= SampleMargin(L) + CellSize(L) - 1`, `HeightBias(L+1) >= HeightBias(L) + CellSize(L)`) must hold when adding levels, and `Camera.NearPlane`/`FarPlane` are tuned to keep those separations from z-fighting at the outermost ring. LOD draws before chunks, coarsest level first (`RenderManager.RenderLod`). Destroying a block would expose the buried LOD, so removals cap that column's LOD height below the removed block (`LodRegion.LowerColumnBelow`) and affected regions on every level rebuild (`LodManager.MarkEditedColumn`); placements need nothing. Canopy bumps come from `TreeGenerator.TryGetCanopy(x, z, proxy, out top, out leaf)`, read once per fine column with the level's `LodLevelSpec.ProxyCanopy`: the first ring reads the grown canopy of the same cell batches the chunks stamp, and the farther rings read each species' `lod` crown proxy (`LodCrownProxy.TopOffset` over its `LodCrownProfile`), which never grows a tree. Either way a bump wears the leaf block that owns its top cell (the species' `LeavesId`, resolved generically via `BlockType.ById` in `LodRegion.BuildMeshData`, falling back to `TreeGenerator.LeavesBlock`), so a new tree species only needs its species file — never a new LOD branch. An edited canopy reads as open sky: mining or blasting a wood or leaf cell, every fell, every column `LeafDecayManager` clears or `TreeGrowthWriter` writes call `TreeGenerator.PoisonCanopy`, whose `CanopyDisturbed` markers `TryGetCanopy` and `CanopyLeafBlock` consult before either cache. LOD vertices bake a coarse sky exposure from those same column heights into `Vertex.Sun` (`LodSkyExposure`, tuned by the `LodSky*`/`LodValleyShade`/`LodCanopyShade` constants): a vertex darkens by how far the columns in front of or around it rise above it, and a canopy bump darkens toward its base, while open ground, straight walls under open sky and the water sheet keep full sun so the ring matches the near chunks.

## Saves

A world opened from the main menu owns a `ChunkSaveStore` over its save folder (layout under
"Launch flow" above); a World built without one (the tests, and every `VOXEL_SCRIPT` /
`VOXEL_SPAWN` / `VOXEL_LOOK` run) regenerates and forgets exactly as before. Worldgen stays a
pure function of the seed: only **deltas** and the seed are written.

- **Chunk persistence ordering** (`ChunkSaveStore`'s class doc is the reference). Writes are
  asynchronous: the main thread only copies arrays (`Chunk.TakeSnapshot`) and one background
  thread (`ChunkSaveWriter`) builds the palette, deflates and writes. `Enqueue` publishes the
  snapshot in the pending map **before** the job is queued, and `ChunkManager` enqueues
  **before** it forgets the unloading chunk, so a chunk that streams straight back in is
  restored from memory. The writer writes `.bin.tmp`, moves it over the final file (retried
  `SaveMoveRetries` times), adds the key to the on-disk index and only **then** removes the
  pending entry — and only if it is still the snapshot it wrote, so a newer enqueue survives. A
  lookup therefore always finds the chunk in one of the two, and a move that finally fails keeps
  its pending entry, so the chunk stays readable for the session and the next flush retries.
  The index is built once at open; leftover temp files from a crash are deleted then.
- **What marks a chunk dirty.** `Chunk.ModifiedSinceSave` is set by the three raw writers every
  edit goes through (`SetBlockRaw` — which placement, state writes and the fluid tick use —
  `RemoveBlockAt` and `ClearBlockAt`) and never by generation, so a fluid settling after an edit
  is saved too, while an untouched chunk is never written.
- **Restored chunks** (`Chunk.RestoreFrom`, on the generation worker) skip worldgen: blocks,
  state bytes and the player-placed cells come from the snapshot, the solid count and surface
  floor are recomputed, the fluid frontier is always rescanned, and `FluidManager` wakes the
  borders of a restored chunk (and of a generated chunk beside one), since a save can hold
  fluid anywhere.
- **The far LOD always reflects the current terrain.** `LodSurface` holds per-column surface
  overrides (the real top height and the block wearing it, REPLACE semantics), beside
  `LodRegion.ColumnCaps` (MIN semantics, only ever falls, so a dug hole never exposes the buried
  shell) and `TreeGenerator.CanopyDisturbed` (the edited-canopy markers `PoisonCanopy` writes;
  its wholesale clear at `CanopyDisturbedLimit` makes them lossy in a very long session, TODO
  `M-10`); a fine column's LOD height is
  `min(override ?? generated, cap)`, fed in before the erosion window and bias so the nesting and
  burial invariants hold by construction. `LodEditState` exports all three to `lod-edits.bin`
  (only when their combined version moved) and imports them **before** `LodManager.Start`, so
  the first region build already sees them.
- **`WorldSeed.Apply(seed)`** is how a world gets its seed (`WorldGenerationConfig.Seed` reads
  `WorldSeed.Value`; `WorldSeed.Default` is what tests and scripted runs use). It is legal only
  while no World is live (`WorldSeed.BeginWorld` in the World constructor, `EndWorld` at the very
  end of a clean teardown; `WorldIsLive` reports it), and it rebuilds every worldgen noise, clears
  the six `GenerationalCache` sites and resets the process-global LOD edit state — always, even
  for the same seed, since another world sharing it must not inherit the previous one's edits.
- **The loading phase.** A World built with a store starts in `WorldPhase.Loading`. The saved
  player is applied before the workers start, so they stream around the restored position;
  `WorldLoadGate` flips the world to Playing once every chunk within `LoadReadyRadiusChunks` of
  the camera is generated and meshed (`Chunk.IsMeshed`), or after `LoadTimeoutSeconds` of update
  time (which still reports `IsLoaded`). While loading, the raised `Loading*PerFrame` budgets
  apply (still bounded, so no frame spikes), every input entry point (throw charge and its
  cancel, use/place, mining, ...) is refused through `IsPlaying`, and World holds
  `Camera.SuppressLook` until the flip — which is why Program opens no menu and runs no input
  while `!IsPlaying`. The far LOD never gates loading.
- **`World.SaveAndDispose(renderManager)`** is the teardown, main thread only and blocking: the
  camera detaches, the generation and LOD workers are stopped and joined
  (`WorkerStopJoinTimeoutMs` each) and the mesh runners drained (`MeshDrainTimeoutMs`) first, then
  `FlushAll` hands every modified chunk, the LOD edit state, the player and the entities to the
  store, then the store shuts down (`SaveWriterShutdownTimeoutSeconds`). Only when every join and
  the drain succeeded are the chunk and LOD slices released to the shared pools, the toppling-tree
  meshes disposed and `WorldSeed.EndWorld` run; `RenderManager.ForgetWorld` drops its references
  (and the eased underwater/lava eye state). A timed-out join leaves `WorldSeed.WorldIsLive` true,
  so the seed stays locked for the rest of the process and the menu refuses PLAY/CREATE with a
  notice; a false return with the seed free (a writer timeout) is only a warning. The World owns
  the store's `Shutdown`: never shut it down again, and give the next World a **new**
  `ChunkSaveStore` over the same folder.
- **Autosave** runs every `AutosaveIntervalSeconds` (at most `MaxAutosaveSnapshotsPerCall`
  snapshots a call, the rest on following frames) after the fluid tick, under the one `save`
  profiler row, which is entered nowhere else.
- **Player and entities.** `player.json` never stores a position before the spawn settled, so a
  world closed mid-load searches the surface again next time. Saved mobs and drops are spawned
  once loading finishes, each only when its chunk is loaded and a mob only while its kind is
  below its alive cap (`SlimeMaxAlive` / `GoblinMaxAlive`, whose sum sizes the healthbar
  buffers); an entry that waits stays pending and is saved again, and is discarded only after
  `EntityRestoreTimeoutSeconds` of play.
- **Not persisted** (tracked as `M-12` in TODO.md): the station block entities (campfire, quern,
  kiln, casting — a reloaded kiln is plain blocks plus its multiblock state bytes until a
  right-click re-registers it through `KilnManager.Ensure`, with no fuel or crucible), primed
  TNT, falling blocks, projectiles in flight and toppling trees.

## Biomes

Five biomes. **Ocean** is a flooded basin ramping from a sandy shore down to bare stone.
The four land biomes sit on a temperature x humidity grid: **forest** (warm+wet; smooth rolling
grass hills with flat valleys), **desert** (warm+dry; dunes of sand over sandstone), **taiga**
(cold+wet; a snowy spruce wood) and **frozen** (cold+dry; a barren snow plain of bare rock and
occasional ice). Selection is a pure function of (x, z) from low-frequency noise — continentalness
(low = ocean) plus temperature and humidity, which are read **only** while choosing a biome, the
worldgen features that follow from it and the grass/foliage tint (`TerrainGenerator.Climate`, see
"Biome-tinted grass and leaves") — in `TerrainGenerator.BiomeWeights`; `TerrainGenerator.BiomeAt`
classifies the result. Temperature is a **three-band axis**: the cold ramp
(`TemperatureColdStart/End`, read by `ColdWeight`: snow, ice, and taiga/frozen at
`ColdBiomeMinCold`), a temperate band, and the warm ramp (`TemperatureWarmStart/End`, read by
`WarmWeight`; desert begins at its midpoint). `desertWeight = min(warm, dry) * (1 - ocean)` is
exactly 0.5 on the desert line on both borders, the humid one and the temperate one, which is the
one line the dune height blend, the surface-water fade (`WaterDesertFade*`) and the border dither
are keyed to, and `DitherBiome` never pushes sand below the warm line. The raw-noise gap between
`ColdBiomeMinCold` on the cold ramp and the warm midpoint always classifies forest and is what keeps
hundreds of blocks of it between sand and snow: widen it by raising the warm ramp, never by moving
the cold one, and the warm ramp must start at or after the cold ramp ends (a test rule,
`TemperatureRampsAreExclusiveWithATemperateBandBetween`). The rules sit in the pure seams
`TerrainGenerator.Classify` / `ColdOf` / `WarmOf`, and `HotAirKeepsAFullLoadRadiusFromSnow` pins
the band to at least the full-detail load radius (`ChunkRadius * Chunk.Size`, Chebyshev distance)
from any column that would be desert if fully dry to any snow. Reaching that bar took a lower
`TemperatureFrequency` (1/4500, was 1/2300) and a **single-octave** temperature noise (was two
octaves), both of which re-laid every temperature value; single-octave output spans the raw
[-1, 1] where two-octave FBm was scaled by 1/1.5, so it also rescaled the cold ramp's share
(taiga, frozen, snow). The weights are smooth 0..1 and `TerrainGenerator.Compute` lerps the height
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
(`TreeGenerator.TryGetTree` reads `TerrainGenerator.ForestOpenness`, scaled per species by its
`worldgen.openness`, so a taiga keeps more of its spruce and the desert's cactus ignores it). On land a hashed lake bowl and a warped river ridge carve below
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
Vegetation is biome-gated. Trees come from the species files (`assets/data/trees`), each planted
only in its `worldgen.biomes`: `oak` in the forest, `spruce` in the taiga and the leafless `cactus`
(a thick stem whose few arms bend upward, wearing the generated `cactus_side`/`cactus_top` tiles;
see `tools/generate-biome-textures.ps1`) in the desert, while `baobab` and `stone_pine` never
generate; the forest also grows flowers (`FlowerGenerator`), and the frozen plains grow nothing.
How a tree is grown, stamped, felled and decays is in `### Trees`. Mob spawning (`MobSpawner`,
shared by slimes and goblins) asks `TerrainGenerator.IsForest(x, z)`, which is now a real test.

## Entities

Entities (mobs) are data-driven like blocks: `assets/data/entities/<name>.json` gives a display
`name`, an `hp` pool, an optional `loot` table rolled on death, and an optional `model` Blockbench
project (default `assets/models/entity/<name>.bbmodel`). `ContentRegistry.Load` parses them into
`EntityType`s published on `EntityRegistry` (`RequireEntity`/`TryGet`), and parses each entity's
`.bbmodel` once, GL-free, into `EntityType.Model` (a `BbmodelData`: parts, skin pixels and animation
clips). The entity, the renderer and the item debugger all share that one parse. A broken entity model
therefore fails content load, and with it every test through `TestContent`. Entity models reuse
`BbmodelLoader`, which takes an optional required-group list: the player rig passes `RequiredParts`,
entities pass none. The slime draws through `EntityModel`, which bakes the whole project into one mesh
plus its skin. The goblin draws per part: `PlayerModel.PlayerRig.FromBbmodel(type.Model)` +
`PlayerModel.BuildMeshes`, one mesh per group, each placed by its `EntityAnimator`.

Every hostile derives from the abstract **`Mob`** (`Mob.cs`). It exposes `Type`, `Position` (the body
centre), `Health`/`IsDead`, `Facing` (yaw, the model faces -Z at 0), `HitFlash`, and the body box as
`BodyHalfExtent`/`BodyHalfHeight`. `HeadAboveCentre` is where the healthbar sits. It also has
`TakeDamage` and `Shove`. `World` rebuilds one `_mobs` list each frame (`RefreshMobs`), after every mob
manager has run and before `UpdateThrown`, so a mob killed or despawned this frame is never hit or given
a healthbar. The punch, the slice, the player's thrown items (`ThrownProjectile.Update`, an AABB overlap
against each mob's body box, so a tall goblin is hit through its head and shins) and the healthbars
(`EntityHealthbarRenderer`, buffers sized by `WorldGenerationConfig.MobsMaxAlive`, the sum of every
kind's cap) all read that one list. A new mob kind must be added to `MobsMaxAlive`, or its hearts vanish
past the cap. The render passes keep one list per kind, because the meshes differ; `ReflectionEntities`
carries both. Ring spawning on dry, loaded forest surface goes through `Systems/MobSpawner.cs`
(`TryFindSurfaceSpawn`), and each manager wraps it with its own radii and body-centre offset.

The first entity is the **slime** (`Slime` + `Systems/SlimeManager.cs`), owned by `World`. It hops
toward the player, damages on contact only while airborne (a slime merely bobbing in water is
harmless), is shoved (harmlessly) by the player when grounded, floats to the surface of water like an
item drop (its water hops keep a land hop's total speed but halve the vertical and move it into the
horizontal, so it skims forward as a low jumping slide), and rolls its `loot` table on death. Natural
spawns appear on dry forest surface in a 20-40 block ring around the player; `SlimeManager` holds the
cap, cadence, damage and shove numbers via `WorldGenerationConfig.Slime*`. The player fights back with
the left-click punch (`World.UpdatePunch` + `Combat`): a mob inside the forward 3-block, 30°-half-angle
cone with line of sight takes the held item's `entityDamage` and a knockback shove, and while one is in
the cone mining stands down (attack-first). `Mining.EntityDamage` is the bare-hand baseline (1) an item
overrides with its `entityDamage` (primitive tools 3, copper tools 5, obsidian blade 6). The obsidian
blade (`attack: "slice"`) instead swings a two-phase sweep (`World.UpdateSlice` + `SliceCombo`): each
swing hits the nearest two mobs in a forward hemisphere of the same reach with line of sight, the
buffered return swing is at half power, and the damage lands at the swing's impact frame rather than on
the click, with the arm and blade carried across the front by `PlayerModel`.

The second entity is the **goblin** (`Goblin` + `Systems/GoblinManager.cs`, owned by `World`), a short
forest raider that throws the flint it carries and then closes for a club swing. It spawns like the
slime: dry forest surface, a 20-40 block ring, a cap of `GoblinMaxAlive` and despawn beyond
`GoblinDespawnRadiusBlocks`. It has no light or time keying. Its **ammo** is a count of the ordinary
`flint` item, rolled at spawn (`GoblinStoneMin..Max`). The stones still carried drop as flint on death,
on top of the goblin's own loot table. A stone that misses **shatters**: it leaves no drop, so thrown
stones never pile up as pickups. The rules:
- **Stance.** With ammo, the goblin walks in beyond `GoblinThrowMaxRange`, backpedals inside
  `GoblinThrowMinRange` and holds position inside the band.
- **When it throws.** Only while grounded, so the auto-jump can never lift the throw origin over a
  wall, and only when the throw cooldown has run out. A fresh goblin waits an unjittered
  `GoblinFirstThrowDelaySeconds`, so it visibly notices the player first; later throws use the
  jittered cooldown.
- **Line of sight.** A throw needs a clear segment from `ThrowOrigin` to the player's chest
  (`Camera.SegmentHitsBlock`). A blocked check is retried only every `GoblinLosRetrySeconds`, and
  while the line stays blocked the goblin keeps walking at the player.
- **The throw.** It aims at the chest seen when the throw starts, plays the `throw` clip, and spawns
  the stone only at that clip's `release` event (`Goblin.TryTakeThrowRelease`, one stone per release).
- **Stones.** A stone is a plain `ThrownProjectile` owned by the manager (`GoblinManager.Stones`).
  `World.Projectiles` merges the stones with the player's throws, so they render and mirror with no
  new plumbing. A stone deals `GoblinStoneDamage` when it touches the player's body.
- **Melee.** Out of ammo, the goblin closes to `GoblinStandoffBlocks` and plays the `melee` clip when
  in reach and off `GoblinMeleeCooldownSeconds`. The damage and knockback land at that clip's `impact`
  event, and only if the player is still in reach (`TryTakeMeleeImpact` + `Goblin.InMeleeReach`).
- **Immunity.** A creative or flying player takes no damage from stones or swings, which is what lets
  `visual-examples/goblins.json` film them from a hovering camera.
- **Movement.** Goblins auto-jump a one-block step they walked into, float and paddle in water like
  the slime, and are shoved away when they overlap the player on the ground.
The launch velocity comes from **`GoblinAim.TryLaunchVelocity`**, the low arc of the exact ballistic
solve under `ThrownProjectile.Gravity`. Always pass that constant, never a second gravity. The level
range of a stone is `v²/g` (`GoblinStoneSpeed` 20 against gravity 28 gives about 14.3 blocks). The
throw band must stay inside it, with margin for a chest-height target and the semi-implicit Euler drop.
`GoblinAimTests` locks that relation down, so retuning the speed or the band cannot silently make edge
throws fall short. An out-of-range target still gets a 45-degree maximum-range toss toward it. Every
goblin number lives under `WorldGenerationConfig.Goblin*`.

### Entity animation

Entity rigs come from Blockbench. **Groups may nest**: `BbmodelPart.Parent` names the enclosing group
(null at the top level), and the goblin keeps `Head`, `ArmL` and `ArmR` under `Torso`. Group origins
stay absolute, as Blockbench stores them. Rest rotations on groups and cubes are still rejected. The
project's top-level `animations` array is parsed by `BbmodelLoader.ReadAnimations` into GL-free
`AnimationClip`s (`BbmodelData.Animations`, in file order):
- **Loop modes:** `loop`, `once` and `hold`.
- **Tracks:** per-bone `rotation`/`position`/`scale` tracks keyed by group uuid, or by the animator's
  `name`.
- **Interpolation:** `linear`, `catmullrom` and `step`, using Blockbench's segment rule: catmull-rom
  when either end key is catmull-rom, step when the earlier key is step, otherwise linear. Components
  are interpolated as raw Euler values, as Blockbench does.
- **Events:** the `effects` animator's `timeline` keyframes become the clip's sorted events. The name
  is the trimmed `script`; sound and particle keys are ignored.
- **Values:** a data point may be a number or a numeric string. A Molang expression, an unknown
  channel, `bezier` or any other interpolation, and an animator naming a group the outliner lacks all
  throw, naming the file and the animation.
- **Unanimated files:** a file without `animations` loads exactly as before.
The **sign and order convention** lives in one place, the comment above `BbmodelLoader.ReadAnimations`,
taken from Blockbench's `BoneAnimator.displayRotation`/`displayPosition`:
- A rotation `(x, y, z)` in degrees becomes engine Euler `(-x, -y, z)`, applied X first, then Y, then
  Z (Blockbench's 'ZYX' order, `AnimationClip.RotationFromDegrees`).
- A position in pixels becomes `(-x, y, z) / 16` blocks, in the parent bone's space.
- Scale is unchanged.
- +X is the model's right (`ArmR`) and -Z its front, so a Blockbench author types a **negative** x to
  raise a hanging arm forward.
If a clip ever plays mirrored, fix the loader, never the clips.

**`EntityAnimator`** (one per entity, GL-free, no per-frame allocation) has two layers:
- **Locomotion** crossfades the `idle` and `walk` clips by horizontal speed through `MathUtils.Damp`.
  The walk plays back at a rate that scales with speed against the animator's reference speed.
- **Action** runs a one-shot clip (`Play("throw")`). Replaying the clip that is already running is
  ignored, and a different one replaces it. The action overrides only the bones its clip keys, with an
  `ActionBlendSeconds` smoothstep in and out, so it returns exactly to the locomotion pose at its end.
`ConsumeEvent(name)` returns true exactly once per play, when playback crosses the event time.
`GetPartMatrix(part, rig, feet, facingYaw)` composes the parent chain in System.Numerics row-vector
order: scale, rotation, then pivot delta plus animated translation, then the parent, down to the root
`RotY(facing) * T(feet)`. `HandAnchor(part, ...)` is that matrix dropped to the part's lowest cube; it
is where the goblin's held flint is drawn (`Goblin.HandPart` = `ArmR`, `Goblin.HeldStoneScale`). The
player's `PlayerModel` still poses procedurally and ignores `Parent`.

The goblin's **content contract** is enforced at startup by `Goblin.ValidateContent`, which the
`GoblinManager` constructor calls. The model must provide:
- the groups `Torso`, `Head`, `ArmL`, `ArmR`, `LegL` and `LegR` (`Goblin.RequiredParts`);
- the clips `idle`, `walk`, `throw` and `melee`;
- a `release` event on `throw` and an `impact` event on `melee`.
A missing piece throws, naming the model file. The clips' keyframes may be retuned freely in
Blockbench; the clip and event names may not change. Hurt is not a clip: it is the hit-flash tint.
`goblin.png` is generated by `tools/generate-goblin-skin.ps1`, whose UV layout must match the
`.bbmodel`'s face UVs.

## Conventions

- C# with file-scoped namespaces, one type per file, `_camelCase` private fields, PascalCase public members. Primary constructors are used where natural (`ChunkManager(GL gl)`).
- XML doc comments (`/// <summary>`) on classes and non-obvious public methods; brief `//` comments explaining *why* for tricky logic. Match the existing density — the code is moderately commented.
- **New blocks go in JSON, not C#.** When a block has to behave differently, prefer a new data flag on `BlockDefinition`/`BlockType` that every call site reads over another per-id branch in gameplay code — the point of the content registry is that `World`, `TntManager` and the mesher ask "what does this block do?" instead of "which block is this?".
- `FastNoiseLite.cs` is vendored third-party code — do not reformat or refactor it.
- xUnit tests live in `OpenGLTest.Tests/` — run `dotnet test OpenGLTest.Tests/silkgltriangle.Tests.csproj -c Release`. They cover headless logic and simulated gameplay without a window (fixture: `TestContent.cs`); add a test with every feature instead of a throwaway script — but only when there is a *rule* to lock down. A test must assert behavior (a gate, a formula, a state transition, an invariant that survives content tuning), never a constant, a layout number, a content literal or a starting-stock/seed count (`Assert.Equal(1, inventory holds one converter)` is exactly as pointless as pinning a block's hardness) — see the `functional-tests` skill's ban list for the full rule and rewrite table. Verify feel-in-game by running the game and using the `X` debug overlay (FPS, position, grounded state). Watch the console for chunk generation errors — worker threads catch and log exceptions rather than crashing.
- Branch naming follows feature work off `master` (current work is on `worldgen`). Commit messages are short imperative sentences ("Add block placement", "Fix performance").
