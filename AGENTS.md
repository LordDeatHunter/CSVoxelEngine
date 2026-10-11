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

The game is one project under `OpenGLTest/`; the item debugger (`OpenGLTest.ItemDebugger/`) and the dedicated server (`OpenGLTest.Server/`) are separate executables that reference it. Historical naming quirks to be aware of:

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
timestamps, format version, and an optional `source` only the dedicated server reads — see "World
sources"), `chunks/<cx>_<cy>_<cz>.bin` (one Deflate-compressed,
block-name-paletted snapshot per **modified** chunk, `ChunkSaveCodec`; an untouched chunk is
never written and simply regenerates), `lod-edits.bin` (`LodEditState`: column caps,
felled-canopy markers, surface overrides), `player.json` (`PlayerSaveCodec`: position and look,
creative/flight, health, respawn eye, lighting clock, the full inventory and hotbar; no position
while the spawn has not settled) and `entities.json` (`EntitySaveCodec`: the live slimes and
goblins with health, facing, velocity, ammo and a slime's size plus still-pending entries, and up to
`MaxSavedDrops` item drops, nearest first). See "Saves" below for the rules.

Setting the `VOXEL_SPAWN` environment variable to `x,y,z` (three comma-separated floats, e.g. `-40,-107,-75`) starts the camera eye at exactly that world position instead of on the surface at the origin, which is handy for inspecting a specific cave or feature without flying to it; an unparseable value is ignored with a console warning. `VOXEL_LOOK` takes `yaw,pitch` in degrees (two floats, e.g. `-90,-20`; yaw 0 faces +X so the default `-90,0` faces -Z, and pitch is clamped to ±89 exactly as the mouse look is) and pins the initial facing, so a scripted verification run frames the same thing every time. `VOXEL_TIME` takes a number of seconds and is added to the window clock before the lighting pass reads it (`Program.timeOffset`), which jumps the day/night cycle straight to noon, dusk or night for a screenshot instead of waiting out the orbit (one full cycle is `WorldGenerationConfig.DayLengthSeconds`, 1200 s like Minecraft's 20 minutes, with the sun starting mid-morning, so noon is about 147 and sunset about 447). All three parse once at startup and ignore garbage with a console warning. `VOXEL_SPAWN` and `VOXEL_LOOK` (like `VOXEL_SCRIPT` below) skip the main menu and play a storeless, unsaved world; `VOXEL_TIME` alone still boots to the menu and offsets every world opened from it (see "Launch flow" above). Setting `VOXEL_SCRIPT` to a scenario JSON path (see `OpenGLTest/visual-examples/basic.json`) runs a scripted visual test instead of playing: `teleport`/`look`/`wait`/`walk`/`mine`/`place`/`fly`/`give`/`spawn`/`grow`/`select`/`screenshot` steps drive the real update functions through a scripted keyboard and save numbered PNGs to `visual/<name>/` (`screenshot` takes an optional `hideHud`); `select` picks a 1-based hotbar slot like the number keys; `damage` hurts every live mob, or the player's own pool with `"target": "player"`, then the window closes. `fly` turns creative flight on (or off with `"on": false`, `Camera.TestSetFlight`) so a scripted camera can hover over a spot instead of falling. `spawn` places a mob through `World.SpawnEntity`: `{ "op": "spawn", "entity": "goblin", "pos": [x, z] }` snaps the feet onto the terrain (`World.TryGroundHeight` + 1), while `"pos": [x, y, z]` is an exact feet position, and an optional `"size"` (an integer from 1, default 1) is a slime's size; the entity name (`slime` or `goblin`) is checked only when the step runs, so parsing stays registry-free. `visual-examples/slime-sizes.json` (`VOXEL_TIME=147`) films the three slime sizes side by side and mid-hop. `visual-examples/goblins.json` (run it with `VOXEL_TIME=147`) spawns two goblins in a flat forest clearing under a flying — and therefore immune — camera and shoots `windup`, `stones` and `charge`. `grow` grows a tree on the live world: `{ "op": "grow", "species": "oak", "pos": [x, z], "steps": n }` snaps the column onto the terrain (`World.TryGroundHeight` + 1) and calls `World.TryGrowTree`, which hands it to `TreeGrowthWriter`: the writer rebuilds the tree rooted in that column from its wood state bytes (`TreeGraph.FromWorld`), or plants a sapling on the ground when the column holds none of that species, runs `n` (> 0) pulses of `TreeGrower.GrowFurther` and writes the difference back (see `### Trees`). Like `spawn`, the species name is checked against `TreeSpeciesRegistry` only when the step runs. `visual-examples/trees.json` (run it with `VOXEL_TIME=147`) frames the oak, spruce and cactus, a cut limb and the leaves decaying after it, a felled trunk, grown oaks, saplings, a baobab and a stone pine, the baobab's wide-trunk cuts and fall, and the stone pine's bare limbs under their leaf pads. Scenario parsing is covered by `VisualScenarioTests`; prefer a scenario over ad-hoc scripts for screenshot verification. `VOXEL_DEBUG=1` starts with the X debug line already on, so such a run logs FPS, position and the profiler breakdown once a second.

The **item debugger** (`dotnet run --project OpenGLTest.ItemDebugger -c Release`, same working-directory
rule) previews held items, entities and trees. Its left panel has three tabs. **ITEMS** is the item grid with the
display sliders, and the player body: its mode buttons are **every player animation**
(`OpenGLTest.ItemDebugger/PlayerPreviewState.cs`, GL-free and tested): the poses IDLE WALK RUN SNEAK JUMP
S-JUMP LAND SWIM FLOAT, and the swings and reactions PUNCH HEAVY SLICE BACK THROW HURT EQUIP DEATH,
which replay on their own after a short rest. Each only feeds the real `PlayerModel` the inputs and
triggers the game does, so what shows is exactly what the game poses (a jump loop with the landing, a
swim that strokes as long as forward is held, a throw that charges, holds and releases, a death that
falls over and gets back up). **Use this tab first for any player animation change**: it is faster
than a scenario and shows the body from any orbit angle (the VIEW button switches to first person).
Both animated tabs (ITEMS and ENTITIES) share a **timeline** along the bottom of the viewport
(`AnimationClock`, GL-free and tested): a **Progress** slider (seconds into the current play; dragging it
scrubs, and the clock stops while it is held), a **Speed** slider (x0 to x4 in steps of 0.1, 0 pauses) and a
**LOOP** toggle (on: a one-shot starts over and a continuous pose wraps; off: the play runs once and holds its
last frame). The clock steps the state in fixed 1/60 s slices, and a scrub restarts the state
(`IPreviewDriver.Restart`: a fresh `PlayerModel` / mob, already settled unless the mode shows the spawn) and
steps it up to the time, so scrubbing lands on exactly the frame playing would (a test asserts it for every
mode). Both preview states are drivers: each mode has a `Duration`, the clock turns their own replay off
(`Replay`) and owns it. `ITEM_DEBUGGER_SPEED` / `ITEM_DEBUGGER_LOOP` set the playback options at startup.
**ENTITIES** lists every `EntityRegistry` entry and draws the selected one part by part
through its `EntityAnimator` (`DebugSceneRenderer.RenderEntity`). The mode buttons are data-driven: one
per clip in the selected model's `.bbmodel`, in file order, plus HURT (hurts the mob: the hit flash,
its recoil and the `hurt` clip as an overlay, listed once when the model has that clip). For the goblin
they read IDLE WALK THROW MELEE HURT DEATH SPAWN. A goblin or slime is previewed as the real `Goblin` /
`Slime` object driven like a client drives a mob it does not simulate (`Mob.AnimateMirror`, and
`NetworkSetState` for the walk velocity), so what the game layers on the clips shows too: the spawn pop,
the hit recoil, the death, the goblin's body pose (`Goblin.BodyEffect`, applied to every part and the
held flint) and the slime's spring squash. The clipless slime offers IDLE HOP HURT SPAWN DEATH (it hops on
a hidden floor toward the camera and restarts). One-shot clips replay after a short rest, and the
preview consumes their events itself. The GL-free, tested state behind the tab is
`OpenGLTest.ItemDebugger/EntityPreviewState.cs`.

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
- `ITEM_DEBUGGER_ANIM=<clip|hurt|hop|spawn|death|...>` picks the entity mode. An unknown mode logs a
  warning listing the valid ones.
- `ITEM_DEBUGGER_PLAYER_ANIM=<mode>` picks the player animation (`idle`, `walk`, `run`, `sneak`, `jump`,
  `sprint jump`, `land`, `swim`, `swim idle`, `punch`, `heavy punch`, `slice`, `slice back`, `throw`,
  `hurt`, `equip`, `death`) and selects the ITEMS tab. An unknown mode logs a warning listing them.
- `ITEM_DEBUGGER_SPEED=<0..4>` and `ITEM_DEBUGGER_LOOP=<0|1>` set the timeline's speed and loop at startup.
- `ITEM_DEBUGGER_SHOT=<png path>` saves one screenshot and closes. Without `ITEM_DEBUGGER_SHOT_AT` it
  fires on an early frame, so a one-shot is caught near its start.
- `ITEM_DEBUGGER_SHOT_AT=<seconds>` (with `ITEM_DEBUGGER_SHOT`) steps the simulation a fixed 1/60 s per
  frame and captures once that much animation has run, so a given instant of a swing is the same every
  run (a slice's strike is at about 0.4 s into a mode, its first play starting at once). Run it from
  `OpenGLTest/` like the game: `ITEM_DEBUGGER_PLAYER_ANIM=slice ITEM_DEBUGGER_SHOT_AT=0.3
  ITEM_DEBUGGER_SHOT=visual/debugger/slice.png dotnet run --project ../OpenGLTest.ItemDebugger -c Release`.
- `ITEM_DEBUGGER_VIEW=first` starts the ITEMS view in first person.
- `ITEM_DEBUGGER_ORBIT=yaw,pitch` pins the orbit camera in degrees (the default is 30,12): yaw 0 sees the
  player from behind (the model faces -Z), 90 from the side, 180 from the front, which is where a limb coming
  off the body shows.
- `ITEM_DEBUGGER_LOOK=yaw,pitch` pins the first-person look angles.

The app opens a window and grabs the mouse. Controls: WASD to move, mouse to look, Space to jump, `C` to toggle creative mode (placing costs nothing, breaking drops nothing; double-tap Space toggles flight, but only while creative — flight is noclip and Space/Left Ctrl move up/down while flying), Left Shift to sprint (1.5x on foot, 10x while flying), hold left-click to punch first, then mine: a mob (a slime or a goblin) inside the forward punch cone (3 blocks, 30° half-angle, line of sight required; only the nearest is hit) takes the held item's `entityDamage` (1 bare-hand, 3 for the primitive tools, 5 for the copper tools, 6 for the obsidian blade) and a knockback shove instead of being mined through (an item declaring `attack: "slice"` — the obsidian blade — instead swings a two-phase blade sweep: each click hits the nearest two mobs in the forward hemisphere of the same reach, the buffered return swing lands at half power, and the arm and blade are carried across the front), while a clear cone mines the targeted block (hit-based: one hit per the held item's `tool.hitDelayMs`, deals the item's `blockDamage`, 0.5 bare-hand, against the block's float `hardness`, declared in its JSON - 5 for most blocks, 8 for malachite ore, 24 for a full-cube trunk of the `tree_wood` logs and 1 for leaves; a tree-wood cell scales its block's `hardness` by the thickness in its `WoodState` (`Mining.HardnessOf`: `hardness * (tipHardness + (1 - tipHardness) * thickness / WoodState.FullCube)`, so the thinnest wood keeps `wood.tipHardness` of it and placed, detached wood is as hard as a trunk), and `0` means it pops on first contact like a pebble; a block with `requiresTool` and a `tools` dict needs a listed tool tag at or above its required tier, and the wrong tool whiffs for 0 damage and no progress; damage resets if you release, switch target or switch tool; instant while creative, which ignores the gate; a click with no block under the crosshair still swings the fist); a stepped crack-sprite overlay drawn on the block's faces via `break-*.glsl` from `assets/textures/environment/break_progress.png` (8 stacked 32x32 frames, the top one blank); each hit pops the block out and back via `RenderManager.RenderBlockPop`), right-click to place the selected block or, when the selected slot holds an item with a `use` action, to use it (the bucket scoops a water source and pours it back out — see the item schema below), 1-10 to pick one of the 10 hotbar slots, `E` for the list inventory (a search field sits over the tabs: typing names the owned items and the recipes that make or consume a matching thing, ranked with prefix hits and owned items first, and clicking a result picks the item or opens its recipe; blocks/tools/misc tabs with shared caps of 2048/8/2048; click a row to preview, click a hotbar slot to equip, same item again to unequip; the world keeps running and `E`/Escape closes it, and a focused field takes the keyboard so typing never moves the player). A new survival world starts with **nothing** (`Inventory.Empty`); `C` creative mode turns the same Blocks/Tools/Misc tabs into the **whole item catalog** (`CreativeCatalog`) - clicking a row grants one, each row's `[+1]` / `[+10]` / `[+100]` button grants that many, and a red `[X]` clears the id's counts (`Inventory.RemoveAll`), all through the ordinary            `Inventory.Add` - while the Craft tab keeps listing every recipe. `T` opens the chat (type, Enter sends, Escape closes, the arrow keys walk sent messages; while it is open the keyboard is captured so the player never moves while typing) and works in singleplayer and on a server. `X` to toggle debug logging               
           (FPS/position/water state to console), Escape to open the pause menu (RESUME / SETTINGS / SAVE & QUIT;        Escape on its settings page steps back to it, and on the main page resumes). SAVE & QUIT presents a `Saving...` frame, calls `World.SaveAndDispose` on the next update and returns to the main menu's worlds page. None of these keys act while a world is still loading.

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

Standing in water switches the player to **swimming** (`Camera.IsInWater`, recomputed every frame by `PlayerPhysics.UpdateWaterState` from the real fluid surface heights under the body): movement is a series of **burst strokes** rather than a steady paddle: a stroke (ready at once on entering the water, then every `SwimStrokeSeconds`) *sets* the horizontal speed to `SwimBurstSpeed` along the WASD direction (0.6x backing up), which then glides off under `SwimGlideDrag` until the next one; Space and Ctrl stroke straight up (`SwimBurstUp`) or down (`SwimBurstDown`), the vertical speed gliding toward the gentle `SinkSpeed` through `MathUtils.Damp`, and no input means no stroke (`Camera.SwimStrokeCount` moves on each, which is how the body starts its stroke animation on the same frame; see "Animation juice"; with the eye under water, not in lava, each one also lets out a burst of `SwimStrokeBubble*` bubbles just in front of the head through `BubbleParticleSystem.TrackSwimStrokes`, run by `World.UpdateBubbles` in singleplayer and on a client, never on a server). Swimming into a bank while holding Space adds `SwimExitBoost` so the player can climb out without a ramp. Flight ignores water entirely, and a player standing on the bottom of a shallow puddle still jumps normally. Flowing water also pushes the player and floating item drops along its flow direction (`ChunkManager.GetFluidFlowGlobal`, Minecraft's `FlowingFluid.getFlow`: a horizontal vector summed from how much lower each neighbouring cell's surface sits, zero in a still pool). `Camera.IsEyeInWater` is the separate eye-only test and is the source of truth for the underwater tint.

**Water depth states.** Beside `IsInWater` (which keeps its meaning) the camera reports how deep the
body stands: `Camera.ImmersionShare`, the share of `PlayerPhysics.PlayerHeight` under the highest fluid
surface over the cells the body overlaps (gathered in the same `UpdateWaterState` pass), and
`Camera.WaterPhase` from it and the head's eye (`WaterImmersion.PhaseOf`, GL-free): `Dry`, `Wade` (under
`WaterFloatShare` of the body, e.g. a one-block source pool whose surface sits 8/9 up), `Float` (half or
more under with the eye above the surface) and `Swim` (the eye under). `WaterImmersionTests` locks the
bands down. The phase drives the movement (`PlayerPhysics.Update`, `WaterFloatTests`): `Wade` *walks*
(ground acceleration, gravity, auto-step and the land jump) at `WadeSpeedFactor` of the walk, still
carried by a flow, so the burst strokes above apply only from `Float` up. A swimmer is latched onto the
swim physics until it lands on ground, so a stroke that bobs it out above the float band is caught on
the way back down instead of falling through the surface. In the water the vertical speed no longer
glides toward `SinkSpeed` for everyone: it eases at `FloatSettleRate` toward
`WaterImmersion.BuoyancySpeed` (`FloatBuoyancy` times the height error, capped by the sink and rise
speeds), which holds a floating body where `WaterFloatRestShare` (about 70 %) of it is under with the
eye just out, and floats a body that went under without diving (a fall in) back up. A down stroke
(Ctrl) *dives*: the diver keeps the old slow glide toward `SinkSpeed` until its eye surfaces again, and
Space strokes up as before. The server needs nothing new: a body resting at the float is
`PlayerBodyProbe.InWater`, so the hover rule never trips (`MovementRulesTests` feeds it the client's own
simulated float and stroke hop).

**Water rendering** lives in the shared world shader plus two extra passes in `RenderManager`. Just before `RenderWorldTranslucent` draws, `CopyScene` snapshots the HDR scene target's colour and depth (see "Linear lighting" below; the colour copy is `Rgba16f`, like the reflection mirror) into two textures on units 3 and 4 (`CopyTexSubImage2D` from the bound HDR FBO — the water pass depth-tests while sampling, so it cannot read the target directly); the water fragment then composes its own look out of them: a refracted lookup offset along the analytic wave normal, Beer-Lambert absorption over the water's measured thickness (`WaterAbsorb`) plus in-scattered `WaterScatter`, and a Schlick fresnel mix into the planar reflection. Where that view-ray depth thins below `WaterFoamWidthBlocks` (a wall, a bank, a thin trickle) a pale **shore foam** band is mixed over the composed surface in linear light, lit by the shared opaque `light` term and fogged with it, broken up by a value-noise field the ripple phases jiggle and `FlowFoam` (the same two-phase flow map) carries downstream; top faces only, never from below or with the eye submerged, faded out by 160 blocks. The width must stay under a full cell's surface less the swell (`WaterFoamTests`), so open water over a block floor never foams; being screen-space, only contacts the depth copy sees foam (the camera-facing side of a block; a wall seen straight down shows none). `visual-examples/water-foam.json` frames liquid shores at `VOXEL_TIME` 360 and 147. Flowing water carries its baked flow into that normal (`FlowWaveNormal`: a bounded two-phase flow map that scrolls the ripples downstream and stretches them along the flow, identical to `WaveNormal` when the flow is zero), and falling side faces add fast vertical streaks; generated rivers and lakes are all source cells, so they bake no flow and stay still until the simulation makes them flow (`visual-examples/flow-water.json`, `VOXEL_TIME=147`, pours some). The mirror plane itself is the single `RenderManager.WaterPlaneY` at sea level, so a lake whose surface stands above it reflects against that plane rather than its own - an accepted approximation, since only one plane can be mirrored per frame. Seen from below — decided per fragment from the face normal (`fromBehind` in `test-fragment.glsl`), never from the sea plane, since a pond stands above it — the surface is a Snell window: the same scene copy shows through it distorted by the wave normal, the inside fresnel goes to total internal reflection past the critical angle and that band fades to the underwater fog colour, and the mirror is never sampled from that side; `ReflectionReuse.MirrorApplies` also skips rendering it whenever `Camera.IsEyeInWater`, and the sky dome (`SkyRenderer`) and the sun disc stay dry in water (lava still fades the dome via `_skyTintAmount`, eased through `_lavaT`) so the window shows real sky. World, cloud and sky fog share `SkySample`, duplicated verbatim in `sky-fragment.glsl`, `test-fragment.glsl` and `cloud-fragment.glsl`, with `SkyPalette.Sample` as the tested C# reference, so change all four together. The dome currently samples the flat 28198cc sky colour: `SkyPalette.Compute` returns the old night/day mix, pulled toward the dusk orange by `SkyDuskMix`, as both zenith and horizon with zero glow, and `SkyHazeStrength` is 0, so every direction (and every fully fogged fragment) is that one colour. The reflection pass now also draws the sun disc and the cloud layer into the mirror, and both it and the scene copies are skipped whenever `CullWater` (one frustum pass a frame, feeding the water pass its draw lists) found no water on screen — glass shares the translucent mesh, so `Chunk.HasWaterMesh` is the flag that decides, not `HasTranslucentMesh`. Opaque faces that look into water are flagged by the mesher in bit 3 of `Vertex.Normal` (bits 0-2 stay the face index, bit 5 is the leaf-sway flag, bits 6-7 the low bits of the block-light tint code) and get the animated Voronoi caustic net; bit 4 is the biome-tint flag (see "Biome-tinted grass and leaves" below). The world fragment darkens a submerged face's albedo by `WetDarken` once, before the light multiply, and only at or below that face's own baked surface (`step(fWorldPos.y, fWaterSurfaceY)`; the caustics stop at the same line), so a dry face beside water — a beach top, a bank wall over the waterline — is never darkened or tinted; ore tiles (a non-zero `material.metallic`) are excluded. `SurfaceFresnel` is the GL-free twin (`Schlick`, `WetWeight`). While the eye is submerged, `UnderwaterRaysRenderer` adds one full-screen ray-march for sun shafts, composited in display space (the upsample reads the HDR target it draws into, adds the shafts to the encoded scene with the pre-SH-10 sRGB sun colour and strength, clips at 1 and decodes), since the same add in linear light lost the glare toward the sun. Both measure depth from the **local** water surface, never the sea plane: the caustics from the surface baked into each submerged face's vertex (`Vertex.WaterSurface`, read as `fWaterSurfaceY`), the rays from `Camera.EyeWaterSurfaceY` (`ChunkManager.GetFluidColumnTopGlobal` over the eye's column), so a pond above sea level gets both — only the planar mirror still uses the sea plane. The rays are why the shadow map keeps rendering underwater (`_shadowStrengthDry`, the strength before the underwater fade) even though the world diffuse no longer uses it. **Depth convention**: `Camera.GetProjectionMatrix` comes from `System.Numerics`, which is Direct3D-style (clip depth 0..1), so window depth is *not* the GL -1..1 mapping — never write a near/far linearization. Reconstruct a world position by inverting the very matrix that wrote the depth (`uInvViewProj`), and compare raw depth values directly for "which is nearer". `visual-examples/water.json` (run it with `VOXEL_TIME=360`) frames the shallows, the grazing surface, deep water from height and the underwater shots, including the surface seen from below in the sea and in a pond above sea level (a liquid one — a frozen pond has an ice lid, not a water top face).

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

**Stars.** The night dome adds a star field in `sky-fragment.glsl`'s `main` only, after
`SkySample`, so the fog and `SkySample`'s three twins never see it. Stars live on a grid over the
sphere of directions (cube face, equi-angular warp, `StarCellsPerFace`² cells, each cell's chance
scaled by its solid angle), one star per cell kept `StarField.CellMargin` clear of the edges, drawn
as a pixel-sized gaussian (`fwidth` of the view ray), so they never swim as the view turns. Each
star twinkles smoothly on its own rate and phase hashed from its cell (`StarField.Twinkle`, the
`uStarTime` clock wrapped hourly). Star space
turns with the sun (`StarField.SpinOf`, the sun direction's own angle about Z), visibility fades over dusk
and dawn by the sun's elevation (`StarField.Visibility`), out into the horizon haze and around the
sun. Clouds hide them: the dome samples the cloud-shadow map (unit 8) cell by cell along each view
ray (`CloudCoverAlongRay`), since the drawn clouds alone cover at their opacity. The mirror pass
redraws the dome, so the stars reflect, and the Snell window shows them. GL-free twins:
`StarField` (cells, hash, star placement) and `CloudCover` (the cover along a ray); keep them in step
with the shader (`StarFieldTests`, `CloudCoverTests`). `visual-examples/night-sky.json` (`VOXEL_TIME`
700, 450, 147, and 350 for the glare) frames them.

**Sun glare.** A wide, faint glow around the sun, added in the same dome `main` (`uGlareColor` times
`cos(angle to the sun)^SunGlareExponent`, `SunGlare.Radial`). Its strength is computed once a frame
on the CPU (`SunGlare.Intensity`, capped at `SunGlareStrength`): how directly the camera faces the
sun, the sun's height (zero at and below the horizon) and the cloud over its disc
(`CloudCover.AcrossDisc` over `CloudShadowTexture.Coverage`, eased). It is a camera effect: zero
underwater and in lava, and the mirror pass draws its dome with `GlareColor` zero, so no reflection
ever carries it.

**Wind (SH-27).** `Wind.cs` is the one wind: a GL-free pure function of the lighting clock whose
heading turns slowly (`WorldGenerationConfig.Wind*`), with `Wind.Offset` its bounded closed-form
integral. Leaf sway reads it in both vertex shaders (`uWind`, the direction scaled by `WindLean`,
leans the canopy downwind; `uWindOffset` advects the gust pattern, so the gust phase rate is the
same anywhere in the world — keep the two sway blocks byte-identical), fire smoke and embers drift
along it (`FireParticle.Update`'s `wind`), falling leaves (ambient and burst) ease their sideways
motion into it, pushed by the canopy's own gusts (`LeafParticleSystem.Update`'s `windTime`,
`Wind.Gust`, the C# twin of the sway's `0.6 + 0.4 sin(phase)`; a landed leaf stays put;
`visual-examples/leaf-wind.json` at `VOXEL_TIME` 147 and 1347), and the cloud layer and its shadows are carried by it
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
(plus the four 1-pixel neighbours) per pixel, `ContactAoRadiusBlocks` wide in the world (clamped to `ContactAoMaxPx`): for each opposite
pair, the midpoint of their raw depths is reconstructed on the centre ray and compared with the
centre. Raw depth is affine in screen space over a plane, so planes never darken at any angle;
a concave corner creases positive, convex edges and far silhouettes are rejected by the
`ContactAoMinBlocks..RangeBlocks` window, and it fades out by `ContactAoFadeFar`. A thin object
standing in front of the ground (a torch, a drop, a mob) would crease the ground a tap radius beside
it exactly like a corner and leave displaced copies of its silhouette there, so each pair's crease is
split between its two taps along the centre surface's own slope (read from the four 1-pixel
neighbours; of the two one-sided slopes the one that carries the surface out to its own tap wins, the
stronger crease on a tie), and a tap in front of that surface only counts while what it sees lies
within `ContactAoReachNear..ReachFar` tap radii of the centre point (`ContactAo.Reach`). A wall or
pebble beside the point is that close, the upper part of a post is not; with every occluder in reach
the crease is exactly the plain midpoint crease. The cost is that a corner seen at a grazing angle
keeps a band about that reach wide in the world rather than a tap radius wide on screen.
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
`IsSwaying`; attribute 6 is gone; on a sway vertex the **alpha byte is its sway weight**,
`Vertex.SwayWeight`/`EncodeSwayWeight`: 255 on canopy leaves, eased from 0 at a rooted plant's base
to 255 at its tip, never 253/254, and the vertex shader hands the fragment an alpha of 1, so
the exact-byte water/LOD-foliage flags only ever see non-swaying vertices), the `ushort Layer` at 28 (attribute location 7, an integer attribute) and the `ushort
WaterSurface` at 30 (attribute location 8, also integer): the baked water surface over a
submerged face in quarter blocks (`Vertex.EncodeWaterSurface`, 0 on every dry face). The mesher
gets it from `ChunkMesher.SubmergedSurfaceCode`, which walks the fluid column in the padded snapshot and
resolves a column that leaves the snapshot from the build's resolved `ColumnTerrain.WaterLevel`
(`MeshScratch.Columns`, worldgen or streamed, see "Chunk meshing"); it rides the greedy key
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
rim was removed because it tinted grazing foliage teal. The one exception is the **snow sparkle**,
not part of the material system: the snow block's tile (`uSnowLayer`, its `Top`, set in
`InitWorldProgram`; `uSnowSparkle` 0 switches it off, as in the ItemDebugger) gets a separate term
(`SnowGlint`): a `SnowSparkleDensity` share of texels, hashed (`Pcg3d`) from the texel's place on
the face's own plane in texture-pixel space (`SnowTexels` must equal the tile size,
`SnowSparkleTests`), carries a random cosine-weighted facet, and its whole texel flashes with
`SnowSparkleStrength` of the sun while the eye/sun half vector lines up with it, so glints move with
the camera. Gated by skylight, a thresholded shadow and cloud shadow (no dimmed glints in a shadow
the low sun has faded), the sun on the face's side, distance (gone by 36 blocks) and a grazing view.
Sunlit snow sits ~2x past the tonemap clip for most of a day, so the glints only read when it is
under the clip, i.e. a low sun: `visual-examples/snow-sparkle.json` at `VOXEL_TIME=405`.

**Chunk meshing.** The column terrain a build reads (heights for the sun seeding, water levels,
the tint climate) comes from the 3x3 chunk columns around it, resolved once:
`Chunk.GenerateMeshData` (the one caller of `GenerateMeshDataFromSnapshot`), right after the neighbour resolve and the empty/enclosed early
return, fills `MeshScratch.Columns` (`dx * 3 + dz` order) through
`ChunkLighting.FillColumns(chunkManager.ColumnSource, ...)`, and the sun seeding, the water-surface
codes and the tint fields all read that array (only the pebble, branch-pile and untouched-snow
emitters still read seed-hashed worldgen per cell, see the multiplayer "Known gaps"); a missing
streamed column makes the build return `null`, which the caller treats like a vanished neighbour
(see the threading rules).
`Chunk.GenerateMeshDataFromSnapshot` runs two passes over the padded 18³
block snapshot. The binary greedy pass (`EmitGreedyFaces`, scratch in the `[ThreadStatic]`
`MeshScratch`) takes every block flagged `BlockType.IsGreedy`: a `Model`-renderer block whose
baked model is exactly the six untransformed cube faces (`ContentRegistry.BakesGreedy`; all four
built-in parents qualify, so stone, dirt, grass, logs, TNT, lamps, leaves and glass all do). It
builds per-axis bit columns of greedy and occluding cells, finds visible faces with one
shift-and-mask per column (the halo bits cull border faces exactly like the neighbour read),
keys each face by block id, the four corner AO values, the submerged flag and the four corner
**smooth light** sums (Minecraft smooth lighting, `ChunkLighting.SmoothCornerLight`): each corner's sun
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
emits everything else as before (tree wood — `ChunkMesher.EmitTreeWood`'s node box and connecting tubes
for a thin cell, `ChunkMesher.EmitRimWood`'s stair-stepped `TreeWoodShape.RimBoxes` for a bevelled
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
chunk-column corners: `TintsAt` reads the climate once and returns both palettes (through
`TintsFromClimate`), `FieldsFor(cx, cz)` gives a chunk column's `BiomeTintFields` (a grass and a
foliage `BiomeTintField`, four corners each) and `BiomeTintField.Sample` interpolates bilinearly in
weighted-sum form, which makes two columns sharing lattice points agree bit for bit, so the colour
is continuous across chunk borders. The lattice climate is stored per column:
`TerrainGenerator.Compute` evaluates `Climate` once at the column's minimum corner into
`ColumnData.CornerWarmth/CornerWetness`, and the chunk mesher reads that through
`ChunkManager.ColumnSource` (`BiomeTint.FieldsFromNeighbourhood` over the build's resolved 3x3
`ColumnTerrain`; `TryFieldsFor`/`TryCellTint` for any `IColumnSource`, false when a streamed corner
column is missing; `FieldsFor`/`CellTint` are those over `WorldgenColumnSource`), so a build
evaluates no noise and a multiplayer client tints from the server's streamed climate.
`FieldsAt`/`LatticeTints` still evaluate `Climate` directly (the felled tree's arbitrary origin, the
far LOD); the stored value is the same call at the same int-to-float lattice arguments, so both are
the same bits, which `BiomeTintTests.TheStoredCornerClimateReproducesTheDirectLatticeEvaluation`
pins. Because
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
never rewrites the oak and spruce tiles. Both chunk mesh paths go through the one `ChunkMesher.EmitTintedCubeFace`, which draws the
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
the plain program's per-draw `uBiomeTint` uniform (`BiomeTint.TryCellTint` at the mined cell over the
world's `ColumnSource` — the pop reads `RenderManager.LightSource.ColumnSource`, the debris gets
`BlockDebrisSystem.Burst`'s `columns:` argument — carried by `BlockDebris.Tint` for each cube, and
untinted white when a streamed column is missing), multiplied into bit-4 vertices in the vertex
shader. Its white
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
`TO_WORLD(p)` macro is what lets them be, and `ChunkDrawShaderTests` guards the sway block and
its weight decode (both read `vColor.a` at location 1).

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
    Chunk.cs                   # 16^3 block storage (byte IDs), state/snapshot save-restore and mesh upload; worldgen
                               #   column fill and mesh/light generation live in Systems/Chunks/ (ColumnCache/ChunkMesher/ChunkLighting)
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
    ChunkGenerationWorker.cs   # background threads creating chunks around every streaming focus (one
                               #   ChunkScanFrontier per focus, served round-robin)
    Camera.cs                  # FPS camera pose, view/proj cache, shoulder/orbit modes; movement+physics and the DDA
                               #   raycast live in Player/PlayerPhysics.cs and Systems/Interaction/BlockRaycaster.cs
    LodRegion.cs               # far-terrain LOD: region columns meshed at reduced resolution from heightmaps; per-column chunk-Y ranges for burial
    LodSkyExposure.cs          # GL-free coarse sky exposure the LOD mesher bakes into Vertex.Sun from its column-top grids
    LodSurface.cs              # per-column far-LOD surface overrides (edited top height + tile), beside LodRegion's caps
    FluidState.cs              # encoding of the per-block state byte for fluid cells (source / falling / level 1-7)
    BlockType.cs               # runtime block record built from JSON, plus the per-id lookup tables
                               #   the mesher and gameplay read (ById, Emission, IsSolid/Occludes/...)
    Mining.cs                  # GL-free mining rules: the tools/requiresTool gate, block damage per
                               #   hit and hit pace shared by the break loop and the tests
    PlayerContext.cs           # who an interaction runs for: eye, aim, inventory, creative (local camera or a
                               #   server's connected player), threaded through every World interaction path
    RightClickOutcome.cs       # what claimed a right-click: a station, the item's use, or a placement
    Combat.cs                  # GL-free punch rules: the forward cone (3 blocks, 30° half-angle)
                               #   and entity damage, shared by the punch loop and the tests
    SliceCombo.cs              # the blade's two-phase slice combo: press/queue/advance state
    FallTracker.cs             # GL-free fall-damage rule: peak/grounded chain fed one feet height at a time
                               #   (singleplayer's UpdateHealth and each ServerPlayer's accepted states)
    ThrowZoom.cs               # GL-free throw charge zoom: the FOV target from the charge and its spring step
    DamageDirection.cs         # GL-free damage direction arc: the last blow's world direction, its fade and
                               #   its screen direction from the view yaw (the vignette pass's third term)
    SurfacingDroplets.cs       # GL-free timing of the droplets streaking down after the eye surfaces from water
    WaterImmersion.cs, WaterPhase.cs  # GL-free water depth states: the submerged share of the body and
                               #   its phase (Dry / Wade / Float / Swim)
    Player/                    # the player's rig shape and movement simulation
      PlayerRig.cs             # the posed shape: pivots, cuboids, skin palette, mesh building (Blockbench or Steve)
      PlayerPhysics.cs         # walk/fly/swim input -> body position, voxel collision (static BodyCollides),
                               #   auto-step, knockback, water state
      PlayerBodyProbe.cs       # GL-free body probes over the live chunk map: known (loaded), grounded,
                               #   in water, in lava; what the server judges a remote player's body by
    Mob.cs                     # abstract base of every hostile (Slime, Goblin): position, health,
                               #   facing, hit flash, body box, TakeDamage/Shove
    IMobTarget.cs              # what a mob chases and hurts: eye, immunity, TakeHit (Camera, ServerPlayer)
    MobTargets.cs              # GL-free target rules: body contact, nearest non-immune target, min distance
    MobCaps.cs, IMobPopulation.cs  # the natural and hard mob caps every kind shares, over the
                               #   living count each registered manager reports
    SlimeSize.cs               # GL-free per-size slime stats: body scale, health, contact damage,
                               #   loot rolls, each 1 + (size - 1) * its config step; the spawn size roll
    DayCycle.cs                # GL-free sun direction, dayness and night factor of the lighting clock
                               #   (RenderManager.SetLighting and the slime size roll both read it)
    Goblin.cs                  # the goblin: body physics, throw-then-melee AI, clip/event contract
                               #   (ValidateContent), animation-driven release/impact events
    GoblinAim.cs               # GL-free low-arc ballistic solver for the thrown stones
    AnimationClip.cs           # GL-free Blockbench keyframe clip: per-bone rotation/position/scale
                               #   tracks, linear/catmull-rom/step, loop/once/hold, timeline events
    EntityAnimator.cs          # GL-free per-entity animator: idle/walk locomotion + one-shot action
                               #   layer, events, parent-chain part matrices, HandAnchor
    Placement.cs               # GL-free placement rules: which cell a right-click fills (a stack
                               #   flagged replaceableByPlacement, layered snow, is taken over in
                               #   place), what a placement may write over, and TryMount (a torch
                               #   on a block's side becomes its wallBlock, facing the hit normal)
    BlockSupport.cs            # GL-free needsSupport rule: the cell holding a block up (below, or
                               #   behind the front of a support "attached" block)
    DoorState.cs               # GL-free state byte of an openable block: facing in bits 0-1, the
                               #   open bit 2, the index into its eight pre-turned shapes
    Multiblock.cs              # GL-free multiblock structure rules: the eight cells of the 2x2x2
                               #   volume, the state byte (facing + offset from the anchor),
                               #   anchor resolution, the integrity check, Convert and Revert
    MultiblockPlacement.cs     # which 2x2x2 volume a right-click meant: the right-down/right-up/
                               #   left-down/left-up search and the facing the opening takes
    WorldGenerationConfig.cs   # all tuning constants (view radius, per-frame budgets, thread counts) and every worldgen knob (seed, ravine, cave, carver)
    MathUtils.cs               # lerp/easing/pulse helpers for animation curves + shared constants
    SkyPalette.cs              # GL-free zenith/horizon/glow sky palette and the C# reference of SkySample
    StarField.cs               # GL-free twin of the night dome's star field (cells, hash, visibility)
    SunGlare.cs                # GL-free sun lens glare: per-frame intensity and its sky falloff
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
      BlockShape.cs                         # the one live-world box resolver (raycast, outline, pop,
                                            #   shape collision): tree wood, snow, pane, facing, plain
      PaneShape.cs                          # the pane renderer's shape: which neighbours get an arm,
                                            #   post/arm boxes, the cached hitboxes per arm mask
      PaneProfile.cs                        # one pane block's dimensions (its "pane" JSON): post and
                                            #   arm widths, the rails an arm fills, interned; its boxes
      HorizontalFacing.cs                   # quarter turns about a footprint's centre: points, boxes,
                                            #   baked models (multiblocks at size 2, facing blocks at 1)
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
      WorldInfo.cs             # one listed world: world.json metadata + its folder (slug fixed at creation);
                               #   SourceName = the raw world.json `source` (null when absent), Source = it
                               #   resolved (`generated` when unset), read by the dedicated server only
      WorldStore.cs            # the saves root: list (most recent first), create, rename, delete, touch;
                               #   writes `source` back only when set, so an unset one never appears
      WorldSources.cs          # the server's named world sources (generated, imported): Create(name, worldDir)
      ColumnFileCodec.cs       # an imported column file: magic CVXL, version, Deflate body of ColumnData
                               #   (heights, bed, water levels, biomes, dirt depth, corner climate)
      ImportedMapWriter.cs     # writes an imported map (columns/*.col + chunks/*.bin); ExportGenerated seeds
                               #   one from any IWorldSource (tool/test use)
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
      MobSaveCodec.cs          # a live Mob -> its EntitySaveData form (per-kind ammo)
      ServerPlayerSaves.cs     # a hosted world's per-player files: players/<name slug>.json via the store
      ServerInfo.cs            # one saved server: display name, host, port, last connected (servers.json)
      ServerStore.cs           # servers.json: list, add, delete, touch (unique names, most recent first)
    Networking/                # GL-free multiplayer protocol and client (see "Multiple players and chat")
      NetProtocol.cs           # NetMessage ids, length-prefixed framing, PacketWriter/PacketReader
      ServerAddress.cs         # host[:port] parsing and formatting (the default port)
      NetworkClient.cs         # the client: connect + handshake, reader thread, inbound queue, sends
      RemotePlayer.cs          # another player as the client sees them: pose smoothing + a PlayerModel
      ChunkNetCodec.cs         # chunk blocks/state and column terrain (heights, water levels, corner climate),
                               #   Deflate-compressed
      ClientChunkStreamer.cs   # requests streamed chunks/columns and applies them, no worldgen; the
                               #   client's IColumnSource
      LodNetCodec.cs           # a far-LOD region's mesh on the wire
      ClientLodStreamer.cs     # requests and applies server-built far regions
      NetEntity.cs             # NetEntityKind/NetEntity and NetCellChange
      InventoryNetCodec.cs     # the server-owned inventory on the wire (the save JSON shape)
      InventoryIntent.cs       # one client inventory intent (select, drop, equip, craft, creative grant/clear, pick)
      InventoryIntentCodec.cs  # its bounds-checked wire form
      InventoryIntentRules.cs  # GL-free: what the server accepts of an intent, against that player's inventory
      ServerPlayer.cs          # a connected player on the server: own Inventory, creative/flight flags, pose,
                               #   its PlayerContext, newest sequence id, mining progress, edit rate, attack pace,
                               #   its PlayerHealth/fall tracking/respawn eye, mob-hit sink, correction numbers
      NetPlayerFlags.cs        # the PlayerState flags byte (creative, flying), a request to the server
      PlayerStatusCodec.cs     # PlayerStatusData (modes, health, dead, knockback) and its bounds-checked wire form
      NetValidation.cs         # server movement, edit-reach, intent-eye and visible-face sanity rules
      MovementRules.cs         # GL-free movement rubberband: speed clamp, then collision, rise and hover checks
      MoveVerdict.cs           # Accepted / Clamped / Rejected
      MovementState.cs         # a player's per-connection movement memory (airborne-still time)
      OperatorList.cs          # who may be creative: ops.json in the world folder plus --op, matched by save slug
      WorldIntent.cs           # one client world intent (break start/complete, place, use, throw, attack)
      WorldIntentCodec.cs      # its bounds-checked wire form, with the sequence id
      BreakProgress.cs         # GL-free: a player's mining on the server, judged against the held item's hits
      RateLimiter.cs           # GL-free token bucket (a player's edit rate)
      AttackPacing.cs          # GL-free: the shortest gap between two punches or slice swings
      ClientPrediction.cs      # GL-free: a client's pending predictions vs the server's answers (sequence ids)
      StationNetCodec.cs       # the StationState snapshot: campfires, querns, kilns, cast molds
    UI/                        # UI state, layout and rules, mostly GL-free (UIRenderer draws them)
      PauseMenu.cs             # pause menu pages, hit kinds, view and PauseMenuLayout (RESUME/SETTINGS/SAVE & QUIT)
      SettingsMenu.cs          # settings page rows, tabs, sliders and SettingsLayout (shared by both menus)
      MainMenu.cs              # main-menu pages, hit kinds, fields, keys, actions and MainMenuView
      MainMenuLayout.cs        # main-menu geometry + HitAt: buttons, scrolled world rows, fields, delete popup
      MainMenuController.cs    # every main-menu page transition, key rule and WorldStore call (tested)
      TextFieldState.cs        # one single-line text field: printable-ASCII typing rules and length cap
      SearchFieldState.cs      # a dropdown search field's query, suggestion cursor and scroll
      InventorySearch.cs       # GL-free matching of owned items and recipes behind the search field
      LoadingScreen.cs         # LoadingScreenView (eased monotone progress, fade-out) + LoadingScreenLayout (spinner)
      ScrollList.cs            # scrolled equal-height row math: clamp, visible range, row at a point
      UiCanvas.cs              # the virtual UI canvas: reference size, uniform scale, scissor rects
      InventoryPanelLayout.cs  # [E] panel geometry, tabs, the search field, the catalog buttons, rows and scrollbar hit-testing
      InventoryView.cs         # [E] panel selection/scroll/focus state; ResetForNewWorld between worlds
      CreativeCatalog.cs       # GL-free browsable catalog of every item, grouped by pool and name-sorted
      HotbarLayout.cs          # hotbar strip geometry shared by the draw, its hit-test and the panel
      CraftCardLayout.cs, CraftCardLayoutCache.cs  # the crafting card's layout and its memo
      CraftingSession.cs       # the crafting tab's open recipe, copies and fed reservations
      Inventory.cs, ItemStack.cs  # the three pools, the hotbar, stacks and their components
      BitmapFont.cs            # TrueType font baked into a GL atlas; CPU glyph layout (printable ASCII)
      TextWrap.cs              # GL-free word wrap against a measure delegate
      GameSettings.cs          # player settings with JSON persistence (settings.json)
      Compass.cs               # HUD compass heading math
      Chat.cs                  # ChatKind/ChatMessage, ChatView (log/input/history) and ChatLayout
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
      ChunkManager.cs          # chunk lifecycle: load/unload, dirty tracking, mesh scheduling, all against
                               #   a focus list (unload only outside every focus's radius)
      StreamingFocus.cs        # the published focus chunks (camera, or every connected player on a server):
                               #   an immutable array swapped only when a focus changes chunk
      Interaction/             # GL-free interaction logic
        BlockRaycaster.cs      # the DDA block raycast + line-of-sight segment test, split out of Camera.cs
      Chunks/                  # GL-free chunk mesh/light/column generation, split out of Chunk.cs
        ChunkMesher.cs         # the greedy pass + per-voxel procedural emitters + water-surface codes
        PaneEmitter.cs         # a pane cell from the padded snapshot: post + arms, no face between
                               #   connected panes, caps culled against a same-shaped pane above/below
        ChunkLighting.cs       # the wide light-volume flood (sun + coloured lamp) + corner light queries
        ColumnCache.cs         # the process-global column-data cache, its accessors and the column fill
        IColumnSource.cs       # the seam a mesh build, the fluid-surface query and the pop/debris tint read
                               #   chunk-column terrain through (ChunkManager.ColumnSource): IsReady + TryGet
        ColumnTerrain.cs       # one chunk column's terrain: heights, water levels, corner climate (warmth, wetness)
        WorldgenColumnSource.cs # the singleplayer/generated-server IColumnSource over ColumnCache; a server
                               #   with a custom world source uses that source, the client's is the ClientChunkStreamer
        IWorldSource.cs        # where a world's terrain comes from (extends IColumnSource): GetColumn (a complete
                               #   ColumnData), Supplies, Fill(chunk); thread-safe, deterministic (see "World sources")
        GeneratedWorldSource.cs # the default IWorldSource: ColumnCache.Get + Chunk.GenerateBlockData, byte-identical
                               #   to the path before world sources existed
        ImportedMapSource.cs   # an IWorldSource over a pre-built map folder (<world>/import/), worldgen outside it
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
                               #   CaveIntensity, Biomes, Bed (pre-ravine profile: decides wetness), WaterLevel,
                               #   CornerWarmth/CornerWetness (the climate at the column's tint lattice point)
      WorldgenReads.cs         # test hook: a flag-gated counter of worldgen entries, noted on a ColumnCache
                               #   miss and in TerrainGenerator.Climate (off in production)
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
      PlantClumps.cs           # GL-free clumped-plant rule over a PlantClumpSpec (cell, jitter, radius, core, falloff)
      CottonGenerator.cs       # wild cotton clumps on the dry forest fringe (PlantClumps + a climate gate)
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
OpenGLTest.Server/                    # the dedicated CLI server (see "Multiple players and chat")
  ServerProgram.cs                    # content load, world/port prompts, headless World + tick loop
  GameServer.cs                       # TCP listener, per-connection reader threads, main-thread Pump
  AssetRoot.cs                        # locate assets/ walking up from the server's working directory
tools/generate-goblin-skin.ps1        # deterministic generator of goblin.png; its header pins the UV layout
tools/generate-leaf-textureless.ps1   # deterministic grey leaf tiles for the biome tint (scaled to BiomeTintTextureGrey)
tools/generate-plank-texture.py       # deterministic oak_planks tile (stdlib PNG writer)
tools/generate-lantern-texture.py     # deterministic lantern tile; its header pins the uv of every model part
tools/generate-door-textures.py       # deterministic oak trapdoor (and door) tiles (stdlib PNG writer)
tools/generate-tree-textures.ps1      # placeholder baobab / stone pine log and leaf tiles tinted from the oak art
tools/generate-cotton-textures.py     # deterministic cotton_plant tile and cotton item sprite (stdlib PNG writer)
tools/generate-torch-texture.py       # deterministic torch tile; its header pins the stick the torch models crop
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
| `footstepDust` | false | loose ground: a player sprinting on foot over it kicks up a tiny puff of its chips at every footfall (`FootstepDust`, `GroundDust`; sand, snow, dirt and gravel set it, grass does not). Illegal on a fluid |
| `occludes` | full cube && !cutout | hides a neighbouring full cube's face (`OccludesNeighbours`) |
| `cullSelf` | false | also cull faces against a neighbour of the same block (glass) |
| `castsAO` | `solid` | darkens neighbouring faces (`CastsAmbientOcclusion`) |
| `cutout` | false | alpha-tested texture; forces `occludes` off whatever the file says |
| `translucent` | false | drawn blended in the sorted translucent pass with the fluids (glass); forces occludes off; never casts a shadow |
| `sway` | false | leaf-wind sway in the vertex shader. A full cube (leaves) sways uniformly; a non-full-cube `model` (a plant) is rooted: its sway eases from 0 at the model's lowest vertex to full at its highest (`BlockType.SwaysFromRoot`/`SwayWeightAt`, carried in the vertex alpha) |
| `biomeTint` | none | colour the block by climate (see "Biome-tinted grass and leaves"): `{"palette": p, "faces": {face: entry}}`, where `palette` is `grass` (default) or `foliage`, a face key is `north`..`down`, `side` or `all`, and an entry is `{"tile": t}` or `{"overlay": t, "base": b}`. `tile` replaces the face with a tinted tile, `overlay` draws a tinted cutout tile over it (`base` optionally swapping the tile beneath). Only on a block that bakes to a plain full cube and is not translucent; `overlay` not on a cutout block. `FaceTiles` stay the default tiles for icons, drops and the held block |
| `modelTint` | none | the sub-cell sibling of `biomeTint`: a palette name (`grass` default, or `foliage`) that tints **every** quad of the block's own baked model (`shade * the field`, bit 4 set) instead of per cube face. Only on a non-full-cube `model`-renderer block that is not translucent (a cross-shaped plant), and the model must wear a grey `_textureless` tile, since the tint multiplies the texel. The `tall_grass` / `dead_shrub` plants use it |
| `material` | none | reflective surface tuning for the ores: `metallic` (0..1, default 0 - an ordinary diffuse block), `roughness` (0..1, default 1, lower being a tighter highlight) and `normalStrength` (0..4, default 1, scaling the tile's normal map). Omitting the object costs nothing at render time. Keyed by *texture*, not by block - see the material atlas below |
| `light.emission` | 0 | 0..15 (`LightEmission`) |
| `light.color` | warm torch colour | sRGB `[r, g, b]`, 0..255 each, of the block's light; only legal with `emission` > 0. Colours within 0.5/255 share a `BlockLightPalette` slot, and at most 8 distinct colours exist (the default included) - a ninth throws naming the file. `blue_lamp` is the example |
| `light.passes` | `!fullCube` | whether voxel light travels through the cell (`PassesLight`) |
| `renderer` | `"model"` | `model` (baked quads), `tree_wood` (a grown tree's wood: its shape, thickness and tree-graph links come from the state byte, see `### Trees`; requires a `wood` object, forces `occludes` and `castsAO` off and `light.passes` on, never meshes greedy, and refuses `cutout`, `translucent`, `fluid`, `falls`, `needsSupport`, `stackMax` and `hitbox`), `pebbles`, `branch_pile`, `snow_layers`, `fluid` or `quern` (procedural emitters), `multiblock` (the anchor cell of a structure, carrying the whole volume's pre-rotated model), `multiblock_part` (one of its other cells, solid and occluding but never meshed) or `pane` (a 2 px post plus an arm toward every horizontal neighbour that is a pane or a solid full cube, derived at mesh time by `PaneEmitter` from `PaneShape`, never stored; a lone pane is just the post, as in Minecraft; the raycast, outline and body collide with the same arms through `BlockShape`; refuses `hitbox`, `fluid`, `falls`, `stackMax` and `needsSupport`, and a solid pane always collides by its arms); a fluid's, layer block's or pane's model only supplies its tile (and the pane's icon) |
| `multiblock` | none | required on, and only legal with, `renderer: "multiblock"`: `{"part": "block/<name>", "source": "block/<name>"}` — the block filling the other seven cells (which must use `multiblock_part`) and the plain block the volume is built from and reverts to. The anchor and its part must both set `hasItem: false` (a structure is built, never held), and `cutout`/`translucent`/`fluid` are rejected, since both halves are ordinary opaque solids to everything but the mesher. Both default `occludes` and `light.passes` to **true**: the structure's surface really does cover its neighbours, and its quads carry no cullface and are lit from the anchor's own cell, so light has to reach that cell or the whole thing renders black. The model is authored in 0..32 model units (the 2x2x2 volume) with the opening on **north**, no `cullface` on any face and no face covering a whole cell plane — see `### Multiblocks` below |
| `kiln` | false | marks a multiblock anchor as a kiln, which is what gives it `KilnManager`'s fuel, ignition, crucible slot and integrity sweep. Only legal on the `multiblock` renderer |
| `pane` | glass pane | only legal with `renderer: "pane"`: the post and arm dimensions in pixels, `{"post": p, "arm": a, "rails": [[bottom, top], ...]}` (`PaneProfile`): a full-height post `p` (1..16) wide, arms `a` (1..`p`, default `p`) wide filling each `rails` height band (ascending, apart, inside 0..16, default `[[0, 16]]`). Omitted is the glass pane's 2 px post and arms over the full height, drawn byte-identically to before. Panes join only panes of the same dimensions (interned, so equal dimensions are one `PaneProfile`) and solid full cubes, so a fence never reaches into a glass pane. `oak_fence` is `{"post": 4, "arm": 2, "rails": [[6, 9], [12, 15]]}` |
| `wood` | none | required on, and only legal with, `renderer: "tree_wood"`: how hardness and loot follow the thickness stored in each cell's `WoodState` (code 0 = 1 px across .. 15 = full cube) |
| `wood.tipHardness` | 0.25 | the share of `hardness` the thinnest (1 px) wood keeps, 0..1, rising linearly to the full `hardness` at a full cube (`BlockType.TipHardness`, read by `Mining.HardnessOf`). Player-placed tree wood is a full detached cube and therefore as hard as a trunk |
| `wood.loot` | always drop self | thickness bands `[{"minThickness": n, "loot": l}, ...]`, strictly ascending by `minThickness` (0..15) and starting at exactly 0, so every thickness has loot; `loot` is `self` (drop the block, the default), `none` or a loot table name. The band with the largest `minThickness` at or below the cell's thickness is rolled (`WoodLootBand.Select`, through `LootContext.State` on every destruction path): `oak_log` gives the `wood` table (a stick half the time) under 8 px and itself from there, `cactus` always itself |
| `stackMax` | 0 | units that stack in one cell (pebble piles 3, branch piles 4, snow layers 16): the count lives in the chunk state byte, 0 = untouched worldgen (the generator's own default), 1..`stackMax` an explicit stack. 2..16 |
| `stackSolidAt` | 0 | stack count at which a stack block turns solid, for a block whose partial stacks are walked through (layered snow at 16). Only legal from 1..`stackMax` on a `solid: false` stack block |
| `hasItem` | true | enters the blocks pool and can be hotbarred; false for pebbles, branch piles, layered snow and the kiln's anchor and part (fluids force it off). Every `tree_wood` log and every leaf block keeps its item: placing one writes a detached full cube (`WoodState.Detached`) or a persistent leaf (`LeafState`), which never fells with a tree or decays |
| `needsSupport` | false | pops, rolling its loot, when the block holding it up is emptied (the block below, or behind it with `support: "attached"`; `BlockSupport`), and is only placed against a solid holder; also makes the block replaceable, so a flow washes it away. Mining, blasts, felling and leaf decay pop it; a toppling falls block does not yet |
| `support` | `"below"` | which neighbour holds a `needsSupport` block up: `below`, `above` (the cell over it: a hanging lantern, so it is placed against a block's underside, or wherever a solid block stands over the target cell; `BlockType.SupportAbove`) or `attached` (the cell behind its front, opposite the `facing` in its state byte: a wall torch). Only with `needsSupport`; `attached` also needs `facing` |
| `facing` | none | `"horizontal"`: the state byte's low two bits are a facing (north -Z 0, south +Z 1, west -X 2, east +X 3); the model (no full-face quads) and `hitbox` are authored facing north and pre-turned to all four (`BlockType.RotatedModels`/`RotatedHitboxes`, `HorizontalFacing`). Only on the `model` renderer, never with `fluid`, `falls` or `stackMax` |
| `facingFromLook` | false | a placement faces the block along the placing player's horizontal look (`Placement.LookFacing`: the dominant horizontal axis of `PlayerContext.ViewDirection`, so a server replays a `Place` intent by that player's own aim), whatever face was hit: stairs placed looking north climb north. Only with `facing`, never with `support: "attached"` |
| `open` | none | makes the block openable: `{"model": m, "hitbox": h}`, both required, authored facing north like the closed model and hitbox. Right-click (`World.TryToggleOpenable`, in the right-click chain after the stations) flips `DoorState.OpenBit` in the state byte whatever is in hand, and the block is then drawn with `m` and hit (and, with `collidesByHitbox`, collided) with `h`; `RotatedModels`/`RotatedHitboxes` hold the four closed facings then the four open ones. Only with `facing`. `oak_trapdoor` |
| `wallBlock` | none | the block a placement against a block's side becomes, facing the hit normal (leaning away from the wall); on top the block places as itself, under a ceiling it is refused (`Placement.TryMount`). The wall block needs `facing` and `support: "attached"`, and is picked as this block (`BlockType.PickId`). `torch` -> `wall_torch` |
| `replaceableByPlacement` | false | a block placement aimed at this block fills the block's own cell and discards it, instead of filling the cell across the hit face, so a block placed against a partial snow drift rests on the ground instead of hovering a layer above it. Only fires while the cell is not solid, so a full snow stack stays an ordinary block, and the displaced block drops nothing. Independent of `needsSupport`, which is what a flow or a bucket may overwrite. Illegal on an always-solid block, which could never honour it |
| `falls` | false | gravity-affected (sand, gravel): topples into a `FallingBlock` entity when the cell below stops being solid, sinks through water, slides off lone supports; never combines with `fluid`/`needsSupport` |
| `fluid` | false | the block is a flowing fluid (water, lava): forces `solid`, `castsAO`, `hasItem`, `needsSupport` off, `occludes` off and `light.passes` on, and drops nothing whatever the file says |
| `flow` | water behaviour | per-fluid tuning, only legal with `fluid: true`: `tickDivisor` (ticks between updates, lava `2` = half water speed), `range` (sideways spread 1..7, lava `3`), `infinite` (adjacent sources birth new ones, lava `false`), and the `hardensTo` + `hardensTouching` pair (lava hardens to `cobblestone` on touching `block/water`) |
| `onBreak` | `"drop"` | `drop` (roll loot) or `prime_tnt` (no drop, spawns a `PrimedTnt`) |
| `loot` | the table sharing this block's name, else drop self | loot table name; `"none"` drops nothing |
| `sfx` | `hit_default` | sound group played while the block is mined, e.g. `hit_rock` (the stone blocks) or `hit_wood` (the logs and cactus). A group is every `<group>.wav` / `<group>_N.wav` directly under `assets/sfx`, and one variant is picked at random per hit (`SfxManager`). An unknown group warns yellow and uses the default |
| `hitbox` | full cube | selection boxes in cell-local 0..1 space: one `{"min": [x,y,z], "max": [x,y,z]}` object or an array of them; the outline draws all, the raycast only targets through one |
| `collidesByHitbox` | false | the body collides with the declared `hitbox` boxes instead of the full cell, the way tree wood collides with its drawn node and tubes (a 0.25 slab is stepped onto and stood on; `PlayerPhysics.IsColliding` -> `TreeWoodShape.OverlapsShape`, which tests the registered hitboxes for every block that is not thin or bevelled tree wood); requires `hitbox`, `solid: true`, no `stackMax`, not a fluid. Player body only: drops, slimes and falling blocks still see the full cell |

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

### Building blocks

Plain construction pieces built from the schema above. `oak_planks` is the first building wood:
four from one `oak_log` (`recipes/oak_planks.json`), a plain `cube_all` block with `sfx: hit_wood`
that is mined by hand. Its tile comes from `tools/generate-plank-texture.py` (stdlib PNG writer,
byte-identical re-runs) and is what the wooden pieces below crop their faces from.
`visual-examples/planks.json` (`VOXEL_TIME=147`) builds and shoots them.

**Slabs** (`cobblestone_slab`, `oak_planks_slab`, six from three of their block) are bottom half
blocks: the `block/slab` parent (one 16x8x16 box, `#bottom`/`#top`/`#side`, default UVs so each
side crops the lower half of its tile) with a `0..0.5` `hitbox` and `collidesByHitbox`, the molds'
idiom, so the body steps onto and stands on them. There is no top-half or double-slab placement.
`BuildingBlockTests` holds the rules every shape-colliding model block obeys: it is drawn exactly
as far as its boxes reach on every facing, and a floor slab is stood on at its top.

**Stairs** (`cobblestone_stairs`, `oak_planks_stairs`, four from six of their block) are a
`facing: "horizontal"` block with `facingFromLook`: `Placement.TryMount`'s look overload writes
`Placement.LookFacing` of the placing `PlayerContext.ViewDirection` into the state byte's low two
bits (`World.PlaceBlockAtFace` takes the view from the placing player, the item-use path from
`ActiveContext`), so the step rises the way the player looked. The `block/stairs` parent is
authored facing north (tall step over the north half) and split into three boxes so no face
covers a whole cell side (`ValidateFacingBake`); the two `hitbox` boxes turn with it
(`RotatedHitboxes`) and the body collides with them, so the walk step climbs them. On a client
in third person the predicted facing reads the camera's view while the server reads the aim,
so a placement near a 45-degree look may be corrected. `StairTests` covers the rules.

The **lantern** hangs under a ceiling: `support: "above"` (`BlockSupport.SupportOffset` returns
+Y for a `BlockType.SupportAbove` block), so `World.PlaceBlockAtFace`'s support check only lets it
in where a solid block stands over the target cell (a placement against a block's underside), and
every pop path that asks `BlockSupport` (mining's `PopUnsupportedAround`, the blast scan, leaf
decay) takes it down with that block. It lights like the torch (emission 15, the default warm
colour), is non-solid, and crafts from a torch and a copper ingot. Its model crops a cage, a lid
and a chain out of one opaque tile from `tools/generate-lantern-texture.py`, whose header notes
the trap: `ModelBaker` mirrors an explicit Minecraft uv on the way in (a side face samples column
`32 - 2u`, an up/down face row `32 - 2v`), so an off-centre uv must be written mirrored and the
art kept symmetric. `HangingBlockTests` covers the rule; `visual-examples/lanterns.json`
(`VOXEL_TIME=700`) hangs one and pops it.

The **fence** (`oak_fence`, three from four planks and two sticks) is the `pane` renderer with
its dimensions as block data: the `pane` object (schema table) becomes the block's `PaneProfile`
(`BlockType.Pane`, flat `BlockType.PaneProfileById`), a 4 px post plus two 2 px rails per arm.
`PaneShape` reads the cell's own profile (its profile-less members describe the default glass
pane, so the pane is byte-identical), `PaneEmitter` draws each arm's rails with their caps (a
rail short of the cell's top or bottom always keeps them) and keeps the post's side wherever an
arm does not cover it whole, and `PaneProfile.Boxes` gives the post plus one box per arm and
rail to the raycast, outline and body. Panes join only their own shape. No gate and no 1.5-high
collision. Its icon model is `block/fence_inventory`. `PaneProfileTests` holds the rules;
`visual-examples/fences.json` (`VOXEL_TIME=147`) shoots fences, a dirt block and glass panes
side by side.

The **cobblestone wall** (six from six cobblestone) is the same renderer with a wall's JSON:
`"pane": {"post": 8, "arm": 6}`, an 8 px pillar with 6 px arms over the full height, so stacked
walls meet seamlessly (`cullSelf` hides the caps between them); its icon is
`block/wall_inventory`. `fences.json` builds a short wall run behind the fences, one wall stacked
and one turning away from the fence in front of it.

The **trapdoor** (`oak_trapdoor`, two from six planks) is a 3 px hatch on the cell floor:
`facing` with `facingFromLook` plus an `open` object (schema table), so it opens by right-click
into a panel standing against the side of its cell nearest whoever placed it. The open bit is the
`DoorState` meaning of the state byte (see the state-byte invariant), the toggle is
`World.TryToggleOpenable`, a raw state write through `Chunk.SetStateAt` (saved, journaled to
clients, remeshed at once) with the fluid and leaf-decay schedules and `World.BlockEdited`; a
client predicts it and the server replays the `Use` intent through the same chain. Both models
(`block/trapdoor_bottom`, `block/trapdoor_open`) are split in half-width boxes so no face spans a
cell side; the tile comes from `tools/generate-door-textures.py`. `OpenableTests` covers the
rules; `visual-examples/doors.json` (`VOXEL_TIME=147`) shoots trapdoors shut and open.

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
occupied set; `FromTerrain` reads `ColumnCache.Get`) for worldgen and the item debugger, and by
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
`ChunkMesher.EmitTreeWood` draws a thin cell as a node box sized `TreeWoodShape.HalfWidth(t)` plus one
capless tube toward every connection at the thinner of the two widths (`TubeHalf`) and a same-width
capless plug toward every adjacent solid occluding non-wood block (`TreeWoodShape.PlugMask`: the
ground, a wall), so a thin trunk or root meets the ground it rests on instead of floating the node
clear of it, with cropped
UVs and smooth light; a full-thickness or detached cell takes the model path and culls against a
same-id full cube. A wood block whose species sets `rim: "pixel"` (`BlockType.PixelRimById`, filled
by `ContentRegistry.LoadTreeSpecies`) gets its outer shells bevelled from their eight horizontal
neighbours: `TreeWoodShape.RimCornerMask` carves a corner when neither of its two edge neighbours
is same-id attached full-cube wood (a cell only ever removes its own material, so diagonals never
matter), `RimBoxes` turns the mask into stair-stepped full-height boxes (`RimInsetPx`, at most
`MaxRimBoxes`, `TreeRimBevelPx` deep), and `ChunkMesher.EmitRimWood` meshes exactly those boxes from the
18³ snapshot with caps that subtract a bevelled cell above or below. The hitboxes match the mesh:
`TreeWoodShape.GetBoxes`/`GetBoxesAt`/`OverlapsShape` (node, tubes and rim boxes, allocation-free)
feed the raycast, the selection outline and `PlayerPhysics.IsColliding`.

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
each other. After the cast step comes `TryToggleOpenable`: a click on an openable block (a
trapdoor) flips its open bit whatever is in hand, so nothing places against it.

## Architecture: threading model (the most important thing to preserve)

Chunk work is split across threads, and OpenGL calls are only legal on the main thread:

1. `ChunkGenerationWorker` runs dedicated background threads that scan positions around each streaming focus (closest first; `World.Focus` is the camera, or every connected player on a server, and the workers round-robin over the foci with one `ChunkScanFrontier` each) and call `ChunkManager.QueueChunk`, which creates the `Chunk` and generates its block data **on that background thread**.
2. Meshing is scheduled by `ChunkManager.ProcessDirtyChunks` (main thread, per-frame budget `MaxDirtyChunksPerFrame`) and runs on the thread pool via `Task.Run`, throttled by a `SemaphoreSlim` (`MaxConcurrentMeshGenerations`). Mesh generation produces CPU-side `ChunkMeshData` only.
3. Completed mesh data lands in `_completedMeshQueue` and is uploaded to the GPU on the **main thread** in `ApplyCompletedMeshes`, sorted closest-first and capped at `MaxMeshUploadsPerFrame` per frame.

Rules that follow from this:

- **Never call GL functions (buffer uploads, `Mesh` creation/disposal, pool uploads/frees through `ChunkGeometryPool`, shader use) off the main thread.** Background work must only produce plain data handed over via the concurrent queues.
- A chunk is only meshed when its block data is ready **and all 6 cardinal neighbors exist with ready data** (`HasAllNeighbors`) — meshing reads neighbor blocks for face culling and cross-chunk AO. Breaking this reintroduces holes/seams at chunk borders (see git history). That scheduling check can go stale (the mesh task waits on the semaphore and an unload may take a neighbor away meanwhile), so `Chunk.GenerateMeshData` re-resolves the whole 3x3x3 neighbourhood once up front and returns `null` when a face neighbor is gone; callers must then re-mark the chunk dirty rather than apply anything. Only the edge/corner neighbors may be absent, and those read as air (they feed light and fluid corner heights only). The same holds for the 3x3 chunk **columns** a build reads (`ChunkManager.ColumnSource`, see "Chunk meshing"): `ProcessDirtyChunks` drops, and `ImmediateRemesh` re-adds once, a chunk whose nine columns are not all `IColumnSource.IsReady` (an all-air chunk, whose build reads no column, is exempt; a fully enclosed one cannot be told apart without its columns and waits too), `ClientChunkStreamer.ApplyColumn` re-dirties the 3x3 chunk columns on arrival, which is what wakes it, and a build that still finds a column missing returns `null` like a vanished neighbour. The worldgen source is always ready, so singleplayer and the server never wait. A mesh build reaches worldgen only through `WorldgenColumnSource` and the seed-hashed pebble, branch-pile and snow emitters, which is why a seed swap still waits for `ChunkManager.DrainMeshWork`.
- Shared state uses `ConcurrentDictionary`/`ConcurrentQueue`; `Chunk.IsBlockDataReady` is `volatile`. Keep new shared state lock-free in the same style. `ChunkManager.Chunks` is a `ChunkMap` (`Systems/ChunkMap.cs`) wrapping a `ConcurrentDictionary<long, Chunk>` — it is a wrapper, never a `Dictionary` behind a lock, because chunk-gen workers write it and mesh-build pool workers read it.
- Editing a block on a chunk border must also remesh the affected neighbor chunk(s) — block edits use `ChunkManager.ImmediateRemesh` / `MarkDirty`.
- Performance tuning knobs all live in `WorldGenerationConfig`; change constants there rather than hardcoding budgets elsewhere.
- Animation curves (interpolation, easing, one-shot pulses) belong in `MathUtils`, not inline in gameplay or render code — everything time-driven should be lerped through one of those helpers rather than snapped.

Other invariants:

- World/chunk coordinates: `Chunk.Position` is in chunk units; world pos = chunk pos × `Chunk.Size` (16). Chunks are cubic and stacked vertically too (3D chunk grid, not 2D columns).
- Blocks are `byte` IDs; `0` is air. Everything else about a block comes from `assets/data/blocks/<name>.json` through `ContentRegistry` (see "Content" above), never from a new `switch` on the id. Hot paths read the flat mirrors (`BlockType.IsSolid/Occludes/CastsAO`, `BlockType.Emission`, `BlockType.PassesLightById`) rather than `BlockType.ById[...]`, so keep new per-id flags in that shape.
- **Per-block state byte**: alongside `Chunk._blocks` each chunk has a lazily allocated `volatile byte[]? _state` (null = all zero), read through `Chunk.GetLocalState` / `ChunkManager.GetStateGlobal` / `TryGetCell` and written only by `Chunk.SetBlockRaw` (no remesh, no scheduling — the fluid tick batches those). Six features share it, one per block: a **horizontal facing** (a `facing: "horizontal"` block such as the wall torch: bits 0-1 the facing, rest 0, written by placement through `Placement.TryMount`), **fluid levels** (`FluidState` decodes it as source `0`, falling `8` or flowing level `1..7`), **stack counts** (a `stackMax` block's 0 = untouched worldgen, 1..`stackMax` an explicit pile), a **multiblock's facing and offset** (`Multiblock.EncodeState`, `facing | dx<<2 | dy<<3 | dz<<4`, written by `Multiblock.Convert` through `SetBlockRaw`), **tree wood** (`WoodState` on a `tree_wood` block: bits 0-3 the thickness code, `FullCube` = 15 being a whole 16 px cell, bits 4-6 the parent direction in `Chunk.CubeFaces` order or `ParentRoot`/`ParentDetached`, bit 7 the shell flag) and **leaves** (`LeafState` on a leaf block: bits 0-2 the hydration, bit 3 the persistent flag) — see `### Trees`. The tree states are written by `TreeGenerator.Stamp` into the generated state array, by `TreeGrowthWriter` and `LeafDecayManager` through `SetBlockRaw`, and by placement, which passes an explicit state to `PlaceBlockAt` (`World.PlaceBlockAtFace` gives a placed log `WoodState.Detached` and a placed leaf a persistent `LeafState`). Which meaning a byte carries is decided entirely by the block id that owns the cell, so a new user must pick a block kind, never a spare bit. **Every write that is not one of those zeroes it** (`RemoveBlockAt`, `ClearBlockAt` and `PlaceBlockAt` with its default state `0` — which also treats a fluid cell as replaceable), and `Multiblock.Revert` likewise writes its source block with an explicit `0`, so a stale level, count, offset, thickness or hydration can never outlive the block that owned it. Meshing gets it as a second padded snapshot from `FillPaddedSnapshot(padded, paddedState, neighbors, neighborStates)`, filled from the neighbourhood `TryResolveNeighbors` resolved for the build; the fluid simulation must read cells through `TryGetCell`/`ChunkAt`, never `GetBlockGlobal`, which reports unloaded chunks as air. A seventh meaning belongs to the **openable** blocks (an `open` object in their JSON: the trapdoor): `DoorState`, the facing in bits 0-1 like any facing block plus `DoorState.OpenBit` (bit 2), the rest zero; placement writes it closed, and the right-click toggle (`World.TryToggleOpenable`) flips the bit alone through `Chunk.SetStateAt`. The mesher and `BlockShape` index a facing block's pre-turned models and boxes with `BlockType.ShapeIndex(state, count)` (`state & (count - 1)`), so four-shape blocks keep reading the facing and an openable block's eight read the open bit too.
- **Station block entities**: `CampfireManager`, `QuernManager`, `KilnManager` and `CastingManager` are all owned by `World`, all **main thread only** and all in-memory only — a lit campfire, a spinning quern, a kiln's fuel and crucible and a mold's cast progress are lost on a restart, a save & quit or a chunk reload, even though the chunk's blocks and state bytes around them are saved (see "Saves"). Each is keyed by the cell (the kiln by its structure's anchor), each is ticked from `World.Update` and each has a `NotifyBlockRemoved` that `World.RemoveBlock` calls so a broken station drops what it held. `World.FireCells` gathers every burning cell (campfires and lit kilns) once a frame and feeds both the fire billboards (`FireParticleSystem.SpawnOffset`) and the warm point lights (`RenderManager.FireLightOffset`), so a new fire source only has to appear in that list. Their render passes — `RenderQuerns`, `RenderKilns`, `RenderCastings` — sit inside the existing `entityPass` profiler span rather than adding sections of their own. On a multiplayer server they tick in the server's `World.Update` and a pour is bound to the pouring player (`CastState.PourSource`, its `PlayerContext`, which pauses once the player left); a client never ticks them: its managers are display-only mirrors replaced by the server's `StationState` snapshot (each manager's `WriteNet`/`ReadNet`, `StationNetCodec`), and only a quern top keeps turning between snapshots (`QuernManager.Animate`).
- **Fluid tick**: `Systems/FluidManager.cs` (owned by `World`, main thread only) is a *scheduled-cell* simulation, not a scan. `World.Update` calls it once per frame after `UpdateToppling`; it runs whole ticks every `WorldGenerationConfig.FluidTickSeconds` (0.25 s), at most `MaxFluidTicksPerFrame` of them per frame, and drops the leftover time so a hitch never leaves it spiralling. Each tick takes the set of cells scheduled by the previous one (packed as `long`, 21 bits per axis — the same budget `ChunkKey` (`src/ChunkKey.cs`) uses to pack a **chunk** position for `ChunkMap` and for `ChunkManager`'s dirty/queued/generating/placement dictionaries; `FluidManager`'s packer is block-scale, `ChunkKey`'s is chunk-scale, and neither floors nor truncates differently from `VoxelCoords.ChunkPosOf`), recomputes every **non-source** fluid cell from its neighbours (Minecraft's `getNewLiquid`: two adjacent sources over ground that cannot drain become a source themselves, fluid directly above means falling, otherwise one level thinner than the fullest neighbour, and nothing left means the cell drains to air), then spreads it downwards, or sideways toward the nearest hole within `FluidSlopeSearchDistance` (ties spread evenly). The level rules are per-fluid data (`flow.*` on the block: lava updates every other tick, spreads 3 instead of 7 and never forms new sources). Contact hardening runs before all of it: a fluid cell touching its `hardensTouching` fluid becomes `hardensTo` (lava beside water becomes cobblestone), and a flow settling against a fluid that hardens on its touch crusts over the same way, so water reaching lava also yields cobblestone whichever side ticks first. Spreading only ever writes into non-fluid `IsReplaceable` cells, popping the fragile block it washes away through `DropManager.SpawnLoot`; a cell that already holds fluid recomputes itself on its own tick. Writes go through `Chunk.SetBlockRaw` and dirty only the cell's own chunk plus the face-adjacent chunk on each axis where the cell sits on a border, batched into `MarkDirty` at the end of the tick — not `ImmediateRemesh`, and never `MarkAreaDirty`, because a fluid passes light and casts no AO. A cell reschedules itself and its neighbourhood exactly when it changed or wrote into a neighbour, so settled water costs nothing.
- **Waking worldgen water**: the sea is generated as static source cells, so a cave or entrance carved into a land column beside it would leave a wall of water standing against worldgen air that nothing ever schedules. `FluidManager.WakeLoadedChunks(chunks)` — called from `World.Update` each frame, immediately before the fluid tick — fixes that by scheduling the frontier of its water against a carved cavity when it loads, so the sea pours into whatever breached it exactly like Minecraft water does when you dig into an ocean. Only cells at or below their column's natural terrain top count (`FluidManager.IsCarvedFlowTarget`), so open air above the terrain — a ravine, a beach, a hillside — is never woken and generated water stays static across it. It has two halves: the **interior**, collected on the generation worker at the end of `Chunk.GenerateBlockData` into `Chunk.FluidFrontier` (local block indices of every fluid cell with a `FluidManager.IsCarvedFlowTarget` neighbour in -X/+X/-Z/+Z/-Y — never +Y, since water does not flow upward and every sea-surface cell has air above it; only its own `_blocks` are read, so worldgen stays a pure per-chunk function), and the **borders**, which need both chunks loaded and are therefore scanned on the main thread over the shared 16x16 plane of each face neighbour, scheduling whichever side holds the fluid when the other side is a carved cavity (the vertical faces only ever schedule the fluid above the hole). Chunks entirely above every level they could share with a neighbour (`ColumnData.MaxWaterLevel`, never below `SeaLevel`) skip the border scan — generated water cannot reach them, and player-placed water is woken by the edit paths. The queue of newly generated chunks is `ChunkManager.TryDequeueLoaded`; a consumer must re-check that the chunk is still the live one at its position, because an unload can race the queue. Once the frontier has settled, it costs nothing again. Deep lava pockets wake through the same path: the frontier scan is fluid-generic, and chunks at or below `LavaMaxY` report theirs even when they never held sea water.
- **Every block edit path must call `FluidManager.ScheduleAround(cell)`** (or `ScheduleBox(min, max)` for a batch) or the water beside the edit will never notice it: `World.RemoveBlock`, `World.FellWood` (per cleared cell, and in both single-block branches), `World.TryPlaceBlockFromRaycast`, `TntManager.Detonate` (`ScheduleBox` over the blast box grown by one; the blast also **skips fluid cells**, since a dry crater under water would just refill from every side) and bucket edits through `FluidManager.SetCellImmediate`, which is the only fluid write that remeshes synchronously. Mining never takes a fluid out: `RemoveBlock` returns early on `BlockType.IsFluid`, so only a bucket removes fluids.
- **Every block edit path must also call `LeafDecayManager.ScheduleAround(cell)`** (or `ScheduleBox(min, max)` for a batch) or the leaves near the edit never re-derive their hydration: a cut limb's canopy would hang forever, and an orphaned wood cell would never crumble. `Systems/LeafDecayManager.cs` (owned by `World` as `_leafDecay`, main thread only) grows each scheduled box by its `ScheduleReach` itself, so a caller passes only the edited cells. The current call sites: `World.RemoveBlock`; `World.FellWood` (`ScheduleAround` in both single-block branches, one `ScheduleBox` over the whole felled batch, never one per cell); `World.PlaceBlockAtFace`, which every placement goes through (`TryPlaceBlockFromRaycast` and the item-use `IItemUseWorld.PlaceBlockAtFace`); the `IItemUseWorld` bucket and TNT-prime wrappers (`ScoopFluidCell`, `PourFluidCell`, `RemoveCellForPrime`), scheduled for uniformity; `TntManager.Detonate` (`ScheduleBox` over the blast box); and `TreeGrowthWriter` (one `ScheduleBox` over the box it wrote). A new edit path adds its call next to its `FluidManager.ScheduleAround`.
- **Gravity**: `Systems/FallingBlockManager.cs` (owned by `World`, main thread only) topples `falls` blocks into `FallingBlock` entities and writes rested cubes back as blocks. Every edit path that can unsupport one must call `TryTopple` above it (`World.RemoveBlock`, `World.FellWood` per cleared cell, `World.TryPlaceBlockFromRaycast` at the placed cell, `TntManager.Detonate` via `CheckColumn` bottom-up over the blast box); fluids never count as support, so fluid edits need no hook. A landing cube slides with chance (8 - solid neighbours of its support) / 8 toward a random clear side, corners included (a corner needs its two flanking sides and the three cells above it clear), committing to its direction across landings, and drops straight once past the edge. Sand stacks on sand: a falls-block support never slides, so towers rebuild. A calm landing converts at once so a collapsing tower settles bottom-up instead of interpenetrating, and a cube that would rest over air pops out as a drop instead of floating. A live cube owns its cell: `TryPlaceBlockFromRaycast` refuses placements into one, cubes shove the player aside one-way (`Camera.PushOutOf`, never through walls, never while flying), and a cube motionless-but-unsettled for 5 s breaks into its usual loot instead of hovering. Shorelines generate from sand (`TerrainGenerator.IsBeachHeight`, gravel-capped where `IsGravelPatch`); the far LOD wears sand and gravel there too, the gravel read with one noise eval per cell.
- Worldgen must stay deterministic and thread-safe (`FastNoiseLite.GetNoise` is read-only after setup; noise instances are configured once in static constructors and never re-tuned per call). All noise seeds derive from `WorldGenerationConfig.Seed` plus fixed per-channel offsets (`TerrainGenerator.MakeNoise`); `Seed` reads `WorldSeed.Value`, which `WorldSeed.Apply` sets (rebuilding every noise from it) before a World is built, never while one is live (see "Saves"). Before player edits, terrain obeys:
  - **Nothing generated is ever solid above `height(x,z)`** — heights come from `ColumnCache.Get(...).Heights` (or the `ColumnCache.GetHeights` wrapper) and already include ravines, which are cut into the heightmap so the far LOD sees them too. The ravine cut fades out across the shore band (`OceanShoreWeight`..0.5), so it never slices the ocean's edge; the block at `height` is solid unless a cave or entrance carved it.
  - Both carvers only ever **remove** blocks: `Chunk.GenerateBlockData` fills the columns (grass at depth 0, dirt down to `DirtDepth`, stone below), then `NoiseCaves.Carve` and `WormCarver.Carve` clear air out of the result and `_solidCount` is recounted.
  - **Water** fills every air cell up to the column's own level in a **wet** column, in `Chunk.FillWater` after both carvers and before trees and pebbles: an ocean floor, or a lake or river bed, where a hashed lake bowl or a warped river ridge channel carves the ground below it. There is **no single water plane**: `ColumnData.WaterLevel` (`TerrainGenerator.WaterLevelAt`) is sea level for the ocean and for rivers, but a lake's **own** surface, so a pond rests in the ground it was dug into instead of at the bottom of a crater dug to sea level. A lake takes that surface from its own rim (`TerrainGenerator.TryLakeSite`): the lowest ground on the rim minus `LakeBankDepth`, and a candidate whose rim climbs more than `LakeSiteFlatness` is dropped rather than dug into a hillside, which is what keeps every bank it floods against above the water. A candidate standing above `SeaLevel` is also dropped when a river channel crosses its footprint (`TerrainGenerator.RiverCrossesSite`, sampled every few blocks across the disc), because the rim is read from `LandHeight` before any river cut and the channel would carve a bank below the pond's surface (rule test `LakeTests.NoRiverChannelCrossesAnElevatedPond`). Its radius (skewed small by `LakeRadiusSkew`), depth and outline (wobbled by `LakeShapeNoise`, so a shoreline is ragged rather than a circle) all vary per lake. `TerrainGenerator.IsWaterColumn` is now just `height < WaterLevelAt(x, z)`, so the carve in `Compute` and the fill agree by construction, and the far LOD draws its sheet at the same per-cell level. Wetness reads the column's `ColumnData.Bed` — the profile before a ravine cut it — not the ravine-cut `Heights`, so a ravine that merely deepens an otherwise dry column holds no water instead of flooding as a narrow spike, while a ravine under a real lake or river just deepens that body (and the cut fades out across the shore band, so no ravine ever cuts the ocean floor). Every other below-level basin is left **dry** — the forest is not flooded wholesale now that ocean carries the bulk of the water — and a desert stays dry; sand and gravel ring actual water only (`TerrainGenerator.SurfaceWaterWeight` gates the height band), never a dry ravine floor in the band. Both the carve and the flood test are scaled by `TerrainGenerator.SurfaceWaterGate` (`WaterDesertFadeStart`/`WaterDesertFadeEnd`), which fades the body out over the last stretch before the desert, so a pond closes on its own forest rim instead of ending in a vertical wall where the hard forest/desert classification flips, while lakes away from a desert keep their full depth. Sea-floor caves still flood, nothing grows in water (wet columns top out in dirt instead of grass), and `IsSubmerged(height)` is only the waterline height test, since wetness is a per-column level now. Generated water is always a **source** (state `0`). A cold column's top water cell is then replaced by `ice` in `SnowGenerator.Stamp` (see Biomes), so a cold ocean or lake freezes into a walkable sheet. `WaterEnabled = false` turns the water off; `LakesAndRiversEnabled = false` leaves oceans only.
  - **Lava pockets** pond over dry cave floors at/below `LavaMaxY` (`LavaGenerator.Stamp`, after the sea so flooded caves stay water): a patch mask plus a per-cell roll, buried under the terrain so ravines and entrances stay dry. Generated lava is likewise all sources; set `LavaEnabled = false` to turn it off.
  - Caves fade out within ~`CaveSurfaceFadeEnd` blocks of the surface (`WormSurfaceMargin` for worms), except inside entrance zones — sparse patches where `ColumnData.EntranceWeight` lifts the fade and caves reach the sky. The entrance weight fades out before the shore band (`EntranceShoreFade`, see `OceanShoreWeight`), so a cave never breaches a beach or the sea floor into a water-filled hole.
  - Worldgen is a **pure function of world position**: each chunk generates its own blocks with no cross-chunk writes, so neighbours agree at their borders without shared state. Worm paths are cached per 64-block XZ cell in `WormCarver` exactly the way column data is cached per chunk column in `ColumnCache.Get`, and so are the four tree caches in `TreeGenerator` (see `### Trees`): the cell batches (`BatchCache`, every tree rooted in one 64x64 XZ cell grown together, shared by `Stamp` on the generation workers and the first LOD ring's canopy on the LOD builder), the solo tree shapes (`ShapeCache`, which `BuildBatch` reads as the neighbouring cells' obstacles), the grown canopy tops (`CanopyCache`) and the far rings' crown-proxy canopy (`ProxyCanopyCache`), the last two read by their one engine caller `LodRegion.BuildMeshData` once per *fine* column of a region. All six go through `GenerationalCache<TKey, TValue>` (`WorldGen/GenerationalCache.cs`): the cache is split into G generation dictionaries published as one immutable node swapped by `Interlocked.CompareExchange`, a read probes the newest first, a miss computes into the newest, and when the newest fills its `capacity / G` insert budget the node rotates and the *oldest* generation is dropped — so eviction retains the most recently used generations instead of clearing wholesale, and an entry read while it sits in the oldest generation is promoted forward (a second chance). Entries are deterministic, so a race, an eviction or a duplicate yields identical values, and the worst an evicted entry can ever cost is a recompute. Capacities and generation counts all live in `WorldGenerationConfig`, one pair per site: `ColumnCacheCapacity`/`ColumnCacheGenerations`, `CellCacheCapacity`/`CellCacheGenerations`, `TreeBatchCacheCapacity`/`TreeBatchCacheGenerations`, `TreeShapeCacheCapacity`/`TreeShapeCacheGenerations`, `TreeCanopyCacheCapacity`/`TreeCanopyCacheGenerations` and `TreeProxyCanopyCacheCapacity`/`TreeProxyCanopyCacheGenerations` — every site ships at 4 generations, and that count is a re-pricing lever that may only be moved against a real capture, never blind. The factory a site passes to `GetOrAdd` must be a `static`, non-capturing delegate *field* (`ColumnCache.Compute`, `WormCarver.BuildCellFor`, `TreeGenerator.BuildShapeFor`, `BuildBatchFor`, `BuildCanopyFor`, `BuildProxyCanopyFor`), because the factory takes the key alone: a lambda written at the call site that captured locals would allocate a closure on every call, hits included, on the generation workers — recomputing those values from the key on a miss is the cheaper trade. **Standing rule: only values that are a pure function of the key may live in that type.** State carrying *player edits* must never be moved into it, because there eviction is data loss rather than a recompute — `LodRegion.ColumnCaps` (mined-column LOD caps) and `TreeGenerator.CanopyDisturbed` (felled-canopy markers) are the two current examples, and both are tracked as their own items rather than converted. They are still plain process-global tables; `LodEditState` exports and imports them per world (with the `LodSurface` overrides) and `WorldSeed.Apply` resets them between worlds.
  - 3D cave noise is sampled on a `CaveNoiseStride` lattice (aligned to world coordinates so adjacent chunks share boundary samples) and trilinearly interpolated per block before thresholding.
- **Far-terrain LOD**: full-detail chunks load to `ChunkRadius` (16 chunks = 256 blocks); a cascade of LOD rings defined in `LodRegion.Levels` extends the render distance (level 1: 2-block cells to 512, level 2: 4-block cells to 1024, level 3: 8-block cells to 2048 blocks). Regions are columns meshed at reduced resolution straight from the heightmap cache (no block data; a column a server's world source supplies is read from that source's `ColumnData` instead, see "World sources"). Each level's heights are eroded (min over a widened fine-column window) and biased downward so its mesh sits strictly inside real terrain *and* strictly below the next finer level — finer geometry always buries coarser via the depth buffer, so no masking or camera-driven remeshing exists. Because those shells are stacked, only one level may actually be drawn per area — otherwise they are all visible once the camera is inside the terrain — so `LodManager.UpdateBuriedFlags` marks each region whose footprint is fully covered by *loaded* finer-level regions (region grids nest exactly: one region = ratio×ratio regions of the finer level) and `RenderLod` skips those. Testing loaded regions rather than each level's radius means an unbuilt finer region can never open a hole in the horizon. The finest level (0) has no finer level to hide it, so it is additionally marked buried wherever the full-detail chunks its shell could show through are loaded *and* meshed (`LodManager.IsCoveredByChunks`, using the per-column inclusive chunk-Y ranges recorded at region build in `LodRegion.ColumnCyMin/ColumnCyMax` and `Chunk.IsMeshed`, which unlike `HasMesh` is true for all-air and fully enclosed chunks too). This is needed because caves cut into the band the shell occupies and would otherwise show it from inside; presence is tested per chunk rather than by horizontal distance because the load volume is a sphere. LOD side faces use grass at the top cell, dirt above `NominalDirtDepth` and the stone tile below it, so far cliffs match near ones; a desert cell wears sand over sandstone and a sea-floor cell the sand/gravel/stone band its depth picks (`LodRegion.BuildMeshData` reads `ColumnCache.GetBiomes`). The nesting invariants (`SampleMargin(L+1) >= SampleMargin(L) + CellSize(L) - 1`, `HeightBias(L+1) >= HeightBias(L) + CellSize(L)`) must hold when adding levels, and `Camera.NearPlane`/`FarPlane` are tuned to keep those separations from z-fighting at the outermost ring. LOD draws before chunks, coarsest level first (`RenderManager.RenderLod`). Destroying a block would expose the buried LOD, so removals cap that column's LOD height below the removed block (`LodRegion.LowerColumnBelow`) and affected regions on every level rebuild (`LodManager.MarkEditedColumn`); placements need nothing. All of this runs where the LOD is built (singleplayer and the server): a multiplayer client's far LOD is the server's, streamed as finished vertices, so its edits return before these hooks (see "Multiple players and chat"). Canopy bumps come from `TreeGenerator.TryGetCanopy(x, z, proxy, out top, out leaf)`, read once per fine column with the level's `LodLevelSpec.ProxyCanopy`: the first ring reads the grown canopy of the same cell batches the chunks stamp, and the farther rings read each species' `lod` crown proxy (`LodCrownProxy.TopOffset` over its `LodCrownProfile`), which never grows a tree. Either way a bump wears the leaf block that owns its top cell (the species' `LeavesId`, resolved generically via `BlockType.ById` in `LodRegion.BuildMeshData`, falling back to `TreeGenerator.LeavesBlock`), so a new tree species only needs its species file — never a new LOD branch. An edited canopy reads as open sky: mining or blasting a wood or leaf cell, every fell, every column `LeafDecayManager` clears or `TreeGrowthWriter` writes call `TreeGenerator.PoisonCanopy`, whose `CanopyDisturbed` markers `TryGetCanopy` and `CanopyLeafBlock` consult before either cache. LOD vertices bake a coarse sky exposure from those same column heights into `Vertex.Sun` (`LodSkyExposure`, tuned by the `LodSky*`/`LodValleyShade`/`LodCanopyShade` constants): a vertex darkens by how far the columns in front of or around it rise above it, and a canopy bump darkens toward its base, while open ground, straight walls under open sky and the water sheet keep full sun so the ring matches the near chunks.

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
  its pending entry and is remembered as failed, so the chunk stays readable for the session,
  the next flush of it retries, and `Shutdown` re-queues every failed write once more
  (`ChunkSaveStore.RetryFailed`) before the writer stops. A file a read gave up on (an
  `IOException` after a few retries, or a corrupt file) is moved to `.bad` before the writer
  first replaces it, so what the player had is never overwritten by a regenerated chunk or an
  empty player file, and the temp file is flushed to disk before the rename. The store holds
  `session.lock` (`FileShare.None`) from construction until its writer thread exits, so a second
  store over the same folder — this process after a writer overran its shutdown timeout, or the
  dedicated server beside the game — throws `IOException` from the constructor: `Program` shows
  the could-not-open notice and the server prints a line and exits.
  The index is built once at open; leftover temp files from a crash are deleted then.
- **What marks a chunk dirty.** `Chunk.ModifiedSinceSave` is set by the three raw writers every
  edit goes through (`SetBlockRaw` — which placement, state writes and the fluid tick use —
  `RemoveBlockAt` and `ClearBlockAt`) and never by generation, so a fluid settling after an edit
  is saved too, while an untouched chunk is never written.
- **Restored chunks** (`Chunk.RestoreFrom`, on the generation worker) skip worldgen: blocks,
  state bytes and the player-placed cells come from the snapshot, the solid count and surface
  floor are recomputed, the fluid frontier is always rescanned, and `FluidManager` wakes the
  borders of a restored chunk (and of a generated chunk beside one), since a save can hold
  fluid anywhere. A chunk an imported map supplies goes through the same restore
  (`Chunk.RestoreFrom(snapshot, column)`, with the source's column) and is `IsRestored` too;
  like any restore it is not `ModifiedSinceSave` until edited, so the save only ever holds the
  edited imported chunks, and a saved chunk wins over the map (see "World sources").
- **`world.json`'s `source` and the `import/` folder.** `source` (`WorldInfo.SourceName`, kept
  raw) names the world source a dedicated server builds the world from; `WorldStore` writes it
  back on every rewrite (rename, touch) only when it is set, so a world the game creates never
  gains the field, and `CurrentVersion` did not move (an older file reads as `generated`). The
  map an `imported` world reads lives in the world folder's `import/` (`ImportedMapSource`);
  `ChunkSaveStore` never reads or writes it (it indexes `chunks/*.bin` and clears leftover temp
  files in the world folder, `chunks/` and `players/` only), but deleting the world from the menu
  deletes it with the folder.
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
  notice; a false return with the seed free (a writer timeout) is only a warning, but the folder
  stays locked until that writer finishes, so a PLAY on the same world before then is refused
  with the could-not-open notice. The World owns
  the store's `Shutdown`: never shut it down again, and give the next World a **new**
  `ChunkSaveStore` over the same folder.
- **Autosave** runs every `AutosaveIntervalSeconds` (at most `MaxAutosaveSnapshotsPerCall`
  snapshots a call, the rest on following frames) after the fluid tick, under the one `save`
  profiler row, which is entered nowhere else.
- **Player and entities.** `player.json` never stores a position before the spawn settled, so a
  world closed mid-load searches the surface again next time. Saved mobs and drops are spawned
  once loading finishes, each only when its chunk is loaded; a restore is a forced spawn, so it
  may pass the natural mob cap but a mob waits while the hard cap (`MobHardCap`, which sizes the
  healthbar buffers) is full; an entry that waits stays pending and is saved again, and is
  discarded only after `EntityRestoreTimeoutSeconds` of play.
- **Not persisted** (tracked as `M-12` in TODO.md): the station block entities (campfire, quern,
  kiln, casting — a reloaded kiln is plain blocks plus its multiblock state bytes until a
  right-click re-registers it through `KilnManager.Ensure`, with no fuel or crucible), primed
  TNT, falling blocks, projectiles in flight and toppling trees.

## Multiple players and chat

A dedicated **server** is a fourth project (`OpenGLTest.Server/`, `dotnet run --project
OpenGLTest.Server/Server.csproj -c Release`, working directory with `assets/` up the tree — the
program sets its own current directory to the assets parent so `FileUtils`' relative `assets/...`
paths resolve). It is a **CLI**: it loads content headlessly (`ContentRegistry.Load` +
`TextureAtlas.RegisterTilesWithoutUpload` + `ContentRegistry.BakeModels`, the `TestContent`
recipe), lists `WorldStore.List()` and asks for a world number or `N` for a new one (name and
optional seed), asks for a port (default `ServerAddress.DefaultPort`, 25566; `--allow-creative` on
the command line sets `GameServer.AllowCreative`, default `WorldGenerationConfig.ServerAllowCreative`,
off, and every repeatable `--op <name>` joins `GameServer.Operators` with the names in the world
folder's `ops.json` — see the creative permission below — and the server prints one line saying
who may be creative), resolves the world source (`--source <name>`, else `world.json`'s
`source`; see "World sources" below), then builds a
**server-mode** `World` (`new World(..., store, serverMode: true, worldSource: source)`) over that world's
`ChunkSaveStore` and runs `World.Update(dt, null)` in a ~1 ms loop with `GameServer.Pump(dt)`.
The world list marks a world whose `WorldInfo.Version` is not `WorldStore.CurrentVersion` as
"(old format, cannot be hosted)" and refuses to select it (the menu's PLAY gate), and a world
folder another session still holds (the store's `session.lock`, an `IOException` from the
`ChunkSaveStore` constructor) is refused with a message before any World is built. A port already
in use (`SocketException` from `GameServer.Start`) prints a line, closes the world and exits.
Ctrl+C, a typed `stop`, `AppDomain.ProcessExit` and the `SIGTERM`/`SIGHUP` `PosixSignalRegistration`s
(each in a try/catch for `PlatformNotSupportedException`) all set the stop event; the loop's
`finally` runs `GameServer.Stop`, `SaveAndDispose` and `Log.Shutdown` on a clean stop and on an
unexpected exception alike, and the signal handlers wait for it (bounded by
`ServerShutdownWaitSeconds`) before letting the process end, so a server session persists its
edits however it is stopped. Server mode sets `ChunkManager.Headless` (finished meshes are marked and their light
published by `Chunk.MarkMeshedHeadless`, the geometry dropped — a headless world has no pools),
hovers the camera creative+flying so mobs cannot hurt it (and therefore never saves that camera
as the player: `CapturePlayer` writes back the `player.json` the world was opened with, kept as
`_hostedPlayer`, with only the lighting clock refreshed — its inventory too is written back as it
was opened, since every connected player carries their own — or for a world that had none no pose,
no creative or flight, no respawn eye and no inventory, so a survival world opened after hosting is
not in creative flight at the server camera's hover), and skips the LodGen threads; the
global `WorldSeed` still applies, and the six `GenerationalCache` sites behave exactly as in the
game, which is what lets a headless World run `Update` over real terrain (the LOD builder already
drops real builds when its pool is null). `ServerWorldTests` locks this down.

**World sources.** Where a world's terrain comes from is one seam, `IWorldSource`
(`Systems/Chunks/IWorldSource.cs`). It extends `IColumnSource`, so the mesher's column reads and
the server's column answers read the same object, and adds three members: `GetColumn(cx, cz)`, the
complete `ColumnData` of a chunk column (heights, bed, water levels, biomes, dirt depth, corner
climate; never null); `Supplies(cx, cz)`, whether the source supplies that column and its whole
chunk stack itself or defers both to worldgen (cheap: no computation, no allocation); and
`Fill(chunk)`, which fills a chunk built with `generateBlockData: false` and leaves
`IsBlockDataReady`, the surface floor, the non-air count and the fluid frontier set. A source is
called on the generation workers, the pool mesh builds, the LodGen threads and the main thread, so
it must be thread-safe and deterministic, never touch GL and **never write into a
`GenerationalCache`**: those hold pure functions of their key, which a custom source's terrain is
not. A source is per world, never a static, so `ColumnCache` and `WorldSeed.Apply` keep their
contract and two in-process worlds (the loopback tests) cannot interfere.
`GeneratedWorldSource.Instance` is the default: `GetColumn` is `ColumnCache.Get` (the cached
instance, no copy), `Supplies` is always false and `Fill` is `Chunk.GenerateBlockData`, so
singleplayer and every World built without a source run exactly the old reads in the old order.
The source sits on `ChunkManager.WorldSource` (and `LodManager.WorldSource`, which `World` copies
before `Start`), set from `World`'s optional `worldSource:` constructor argument before any worker
starts (published by `Thread.Start`), which also installs it as `ChunkManager.ColumnSource`; null
leaves both defaults (`GeneratedWorldSource`, `WorldgenColumnSource`) untouched. The readers that
describe the live world go through it:
- `ChunkManager.CreateChunk`, the single seam for where a chunk's blocks come from: the save
  store still wins (its restore reads the source's column), otherwise `WorldSource.Fill`;
- `FluidManager`'s wake scans (`WakeLoadedChunks`, `WakeBorders`) and its generated-water test;
- the LOD edit hooks, `LodSurface.TopOrHeight(source, x, z)` and
  `LodRegion.LowerColumnBelow(source, x, y, z)` (the source-less overload is worldgen's);
- `LodRegion.BuildMeshData(..., source:)`, from `LodManager.TryBuildOneRegion` and the server's
  `LodRegionRequest` (`ChunkManager.WorldSource`). A supplied column's far LOD takes the biome and
  water level at the cell centre from its `ColumnData`, snow from a Taiga or Frozen biome, sea ice
  from Frozen, beaches from `TerrainGenerator.IsBeachHeight` in the Ocean biome, bare rock from a
  zero `DirtDepth` in the Forest biome and the tint lattice from the stored corner climate, and it
  draws no canopy bumps (its trees are in its blocks) and no gravel caps; an unsupplied column keeps
  every worldgen read verbatim, so a generated region is byte-identical to the build without a
  source;
- `World.TryGroundHeight` and the spawn settle (`EnsureSpawnSettled`, whose water test is now the
  column's stored `WaterLevel`, the same value `TerrainGenerator.IsWaterColumn` evaluates for a
  generated column);
- `MobSpawner.TryFindSurfaceSpawn` (heights and water from the column; a supplied column's forest
  test reads its own `Biomes`, a generated one keeps the undithered `TerrainGenerator.IsForest`);
- the server's `ColumnRequest`, answered from `ChunkManager.ColumnSource`, exactly what its own
  mesher reads.

Worldgen's own pure functions deliberately stay static and never see the source:
`Chunk.GenerateBlockData`, the carvers (`NoiseCaves`, `WormCarver`), `TreeGenerator` (placement,
batches, the far-LOD canopy), `VirtualTreeSpace` and `Camera.SpawnPosition` (a server's camera
starts at the generated origin surface, harmless since it is creative and flying, and the spawn
settle then re-settles it on the source's terrain). A generated chunk beside a supplied column
therefore generates as if the source were not there (see the known gaps).

**`ImportedMapSource`** (`Systems/Chunks/ImportedMapSource.cs`) is the non-generating source: a map
folder (`<world>/import/` on a server, `ImportedMapSource.DirectoryName`) holding
`columns/<cx>_<cz>.col` and `chunks/<cx>_<cy>_<cz>.bin`. A column file is `ColumnFileCodec`
(`Saves/ColumnFileCodec.cs`): the magic `CVXL`, a version byte (1), then a Deflate body of 256
int32 heights, 256 int32 bed, 256 int32 water levels, 256 biome bytes, 256 dirt-depth bytes and the
corner warmth and wetness floats, all little-endian; `EntranceWeight` and `CaveIntensity` are
worldgen-only carver inputs, not stored and decoded as zeros. A chunk file is the save store's own
`ChunkSaveCodec` (block-name paletted, so ids never leak into a map; player-placed cells in it are
ignored). A column with a file is **supplied**: its `ColumnData` comes from the file and every chunk
of its stack from its chunk file, or is **all air** when there is none; every other column, and its
chunks, comes from the fallback (worldgen by default). Only canonical file names count. Every
column file is loaded once at construction into a table never modified afterwards (a known limit:
about 3.5 KB of decoded arrays per column, all at once, plus a startup log line with the column and
chunk-file counts) and every chunk read opens its own stream, so the generation workers, the mesh
builds and the LodGen threads read it concurrently and deterministically. A damaged column file
refuses the whole map at construction (`InvalidDataException`/`IOException` naming the file); a
damaged chunk file is retried like a save read and then loads as air with a warning, so a position
is never left stuck in the queued set. Supplied chunks are restored through
`Chunk.RestoreFrom(snapshot, column)`: they are `IsRestored` (so the fluid wake always scans their
borders) and not `ModifiedSinceSave` until edited, so an untouched imported chunk is never saved
and an edited one is saved and wins over the map on reload. `ImportedMapWriter`
(`Saves/ImportedMapWriter.cs`, tool and test use only) writes the format (`WriteColumn`,
`WriteChunk`, each through a temp file) and `ExportGenerated(dir, source, cxMin, cxMax, cyMin,
cyMax, czMin, czMax)` seeds a map from any source (over `GeneratedWorldSource`, worldgen under the
current seed), writing every column in the box and every chunk that is not all air.

**Choosing a source.** The dedicated server reads `world.json`'s `source` (`WorldInfo.Source`:
`generated`, the default when the field is absent, or `imported`; `WorldSources.Names`), and
`--source <name>` on the command line overrides it (case-insensitive, never persisted; a bare
`--source` prints that it needs a name and exits). `WorldSources.Create(name, worldDirectory)`
(`Saves/WorldSources.cs`) builds it after the port prompt and the seed-lock check and **before**
the `ChunkSaveStore` takes `session.lock`, so an unknown name (an `ArgumentException` listing the
names), a missing `import/` or `import/columns/` (`DirectoryNotFoundException`) or a bad column
file prints `Cannot use world source '<name>': ...` and exits without locking the folder. After the
`Serving` line the server prints `World source: generated` or `World source: imported (<map dir>;
generated outside the imported columns)`. `WorldSeed.Apply(world.Seed)` still runs for an imported
world, since its fallback columns depend on it. Clients need nothing new: they never generate, and
the chunks, columns and far LOD they stream are the source's. Tests: `WorldSourceTests` (the
default source fills exactly like worldgen and hands back the cached column; a custom source
supplies its chunks and columns and the fallback the rest; the save store wins over the source; the
LOD edit hooks, the fluid wake and the ground height and spawn read the source; `FlatWorldSource`
is the test double), `WorldSourceLodTests` (a supplied column's far LOD follows its own heights,
biome, stored climate and dirt depth; an unsupplied one stays byte-identical),
`ColumnFileCodecTests`, `ImportedMapSourceTests` (an exported generated region fills exactly like
worldgen, an absent or damaged chunk is air, a corrupt column file or a missing folder is refused),
`WorldSourcesTests` and the `source` rule in `WorldStoreTests`, and `ImportedWorldStreamingTests`
(over a loopback server, an imported world is played and settles its spawn on the map, and a bare
client `ChunkManager` receives its chunk, column and far LOD without generating anything; an
edited imported chunk is saved and wins on reload while unsaved chunks still come from the map).

**The main menu** grew a fourth button, MULTIPLAYER (the main page no longer shares
`PauseMenuLayout`'s three-button stack; `MainMenuLayout.MainButtonCentre`/`MainTitleTop` own the
four-button page). It opens the multiplayer page, which is shaped exactly like the worlds page
(`MainMenuPage.Multiplayer`): a scrolled list of `ServerStore.List()` (name over address) with
CONNECT / DELETE per row, ADD SERVER / DIRECT CONNECT / BACK across the bottom, and an empty
state. ADD SERVER (`MainMenuPage.AddServer`) takes a NAME and an ADDRESS; DIRECT CONNECT
(`MainMenuPage.DirectConnect`) takes just an ADDRESS. `ServerAddress.TryParse` accepts
`host`, `host:port`, `[ipv6]:port` and a bare IPv6, defaulting the port; a bad address keeps the
page up with `MainMenuController.BadAddressNotice`. Servers live in one JSON file,
`%APPDATA%/CSVoxelEngine/servers.json`, owned by `ServerStore` (unique case-insensitive names via
`WorldNames.Unique`, most-recently-connected first, a temp-file write); a row CONNECT touches it
and returns a `ConnectTarget` through `MainMenuAction.Connect`, which Program turns into a real
session.

**The wire protocol** is in `OpenGLTest/src/Networking/NetProtocol.cs`: `NetFraming` writes a
4-byte little-endian length then the payload whose first byte is a `NetMessage`, and
`PacketWriter`/`PacketReader` carry the fields (`BinaryWriter`/`BinaryReader`, strings 7-bit
length prefixed, `Vector3` as three floats). `NetFraming.ProtocolVersion` must match in both
hellos. Messages: `ClientHello` (version, name), `ServerHello` (version, player id, world name,
seed, day clock, spawn), `KeepAlive`, `ChatSend`/`ChatMessage` (kind, sender, text),
`PlayerState` (eye, yaw, pitch, flags, the newest correction it acks), `PlayerStatus` (the
player's server-owned status, `PlayerStatusCodec`: the creative/flight flags the server actually
granted, health, a dead flag, and the knockback impulse and lift of the mob hits since the last
one; sent at join, whenever it changed, and once when a mode request was refused),
`PlayerCorrection` (a numbered eye the client must snap to: an illegal move undone, or a
respawn), `PlayerList` (id, name, eye, yaw, pitch, flags), `WorldIntent` (a client's world action, `WorldIntentCodec`), `IntentResult` (sequence id,
accepted), `BlockCorrection` (sequence id, then the authoritative cells around the intent's target),
`ChunkRequest`/`ChunkData` and `ColumnRequest`/`ColumnTerrain` (the streamed world,
`ChunkNetCodec`; a column answer is the whole `ColumnTerrain`, `EncodeColumn`), `LodRegionRequest`/`LodRegionData` (a streamed far region, `LodNetCodec`),
`EntityList` (the live mobs, drops, primed TNT and falling blocks, `NetEntity`), `StationState` (the campfires, querns, kilns and
cast molds, `StationNetCodec`), `BlockChanges` (a batch of server cell changes, `NetCellChange`)
and `InventoryState`/`InventoryRequest` (a player's server-owned inventory, `InventoryNetCodec`,
and its intents, `InventoryIntentCodec`). The intent protocol came with version 3: a client
never writes a cell (the old raw `BlockEdit` is retired and refused), every intent carries a
per-player sequence id, and `InventoryState` carries the id of the newest intent it reflects.
Version 4 added the status payload of `PlayerStatus`, the
`PlayerCorrection` message (id 25), the correction ack in `PlayerState` and the `Respawn` intent.
Version 5 made a column answer the whole `ColumnTerrain` (heights, water levels and the corner
climate) instead of the heights alone, so a client derives nothing from worldgen.
`ProtocolVersion` 6 is the current one: `EntityList` also carries `NetEntityKind.PrimedTnt` (4;
`ItemId` the block that primed, `Health` the fuse remaining and `Ammo` the whole fuse, both in
milliseconds) and `NetEntityKind.FallingBlock` (5; `ItemId` the block), and an older client is
refused at hello. A slime entry's `Ammo` (a goblin's flint) carries the slime's size; the layout
did not change, so no version bump: an older server's 0 reads as the smallest size, and a mirror
whose reported size differs is replaced (`World.KindMatches`). A
`WorldIntent` is the sequence id, a `NetIntentKind` byte, the eye and the unit aim the action was
made from, then that kind's fields (`WorldIntent`): `BreakStart`/`BreakComplete(cell)`,
`Place(hit cell, face normal)`, `Use(cell under the crosshair, optional)`, `Throw(charge 0..1)`,
`Attack(punch | slice | slice return)` and `Respawn` (nothing more; never rate-limited, edits no
cell). Its reader refuses an unknown kind or attack, a
non-finite or far-out eye, an aim that is not close to a unit vector and a normal component
outside -1..1 as an `InvalidDataException`, renormalises the aim and clamps the charge. An
`InventoryRequest` is the sequence id, then a `NetInventoryAction` byte and only that action's fields
(`InventoryIntent`): `SelectSlot(slot)`, `DropSelected`/`DropAllSelected`, `Equip(slot, isTool,
id)` (a stackable's id or a tool's instance id), `Craft(recipe file name, batch, fed member per
ingredient)`, `CreativeGrant(id, count)`, `CreativeClear(id)` and `PickBlock(id)`. The reader is
bounds-checked: an unknown action, an overlong recipe name or more than
`InventoryIntentCodec.MaxCraftMembers` members is an `InvalidDataException` (the sender is
dropped like any malformed frame), while merely out-of-range values parse and are refused.

**The server** (`OpenGLTest.Server/GameServer.cs`) is a dual-mode `TcpListener.Create(port)`
(IPv6 any with IPv4 mapped, so `[::1]:port` works like `127.0.0.1:port`) with one reader thread per
connection that only frames inbound bytes onto a `ConcurrentQueue`; `Pump` (main thread) drains it
and owns every piece of `World` state, so the simulation stays single-threaded exactly like the
client. A transient `SocketException` from the accept is logged and retried rather than ending the
accept thread. Each `Handle` runs inside a try/catch: a short or malformed frame
(`EndOfStreamException`, `IOException`, `InvalidDataException`, `FormatException`,
`ArgumentException`, `ObjectDisposedException`) logs, closes and removes only that client. Sends
are blocking writes on the main thread, so every socket gets a `NetSendTimeoutMs` write timeout,
and a failed or timed-out send faults the connection (no further sends) and queues its removal
once. The accept thread queues each new connection on `_accepted` before its reader starts; `Pump`
moves the ones that have not said hello into `_pending`, and once a second sweeps every
connection, hello'd or pending, whose reader saw no frame for `NetIdleTimeoutSeconds`
(`ClientConnection.LastInboundAt`, `Volatile` ticks; clients send `KeepAlive` every
`NetKeepAliveSeconds` and their state at 20 Hz). `Stop` closes the pending ones too. Every
connected player is a **`ServerPlayer`** (`OpenGLTest/src/Networking/ServerPlayer.cs`): id, a name
unique among the players online by its save slug (a second "Player" joins as "Player 2"), its
**own** `Inventory` (`Inventory.Empty()` for a newcomer), server-owned `Creative`/`Flying` flags
(survival by default), its own health and respawn eye (see "Player status" below), the accepted
pose and `LastStateAt`. On `ClientHello` it assigns an
id, loads the player's file (`ServerPlayerSaves`: `players/<slug of the name>.json` in the world
folder, the `PlayerSaveCodec` shape — health and respawn eye included — written through the
world's `ChunkSaveStore` — `World.SaveStore` — which accepts that one subfolder), replies
`ServerHello` with the saved position or else the **join spawn** (the server camera's position
captured at the first hello; the camera never follows the players, so a later joiner never spawns
wherever the players online have wandered, possibly inside rock), teleports the player there
(`ServerPlayer.Teleport`, which starts its fall tracking; a player with no saved respawn eye gets
that spot as one), sends that player's inventory and status, and broadcasts a system chat and the
player list; players are saved again on leaving, in `Stop` (before the world's `SaveAndDispose`
shuts the store) and every `AutosaveIntervalSeconds`. Thereafter it relays chat, records
`PlayerState` into the player list (sent every `NetPlayerListSeconds`, 10 Hz) — its flags
(`NetPlayerFlags`, the client's `C` toggle) are only a request: `ServerPlayer.Creative` is set
only while `GameServer.MayBeCreative` holds for that player, and flight only with it, and its
position goes through the movement rubberband (both below) — syncs the day clock
(`NetTimeSyncSeconds`), serves `ChunkRequest`/`ColumnRequest` from the loaded world
(`ChunkManager.TryCaptureChunk`; a column through `WorldgenColumnSource`, exactly what its own
mesher reads, encoded by `ChunkNetCodec.EncodeColumn`), queues `LodRegionRequest`s (deduped
per connection, `ClientConnection.LodQueued`) and builds at most `NetLodBuildsPerPump` of them a
`Pump` (`LodRegion.BuildMeshData`, skipping any whose client has gone), replays each
`InventoryRequest` intent against **the sender's** inventory only — `InventoryIntentRules.Apply`
(a slot must be on the hotbar; equip only what the player owns, or toggle off what the slot
already shows; craft through `Inventory.TryCraft` with exactly the fed members, so a refused craft
spends nothing; creative grants and clears only while `ServerPlayer.Creative`; pick-block through
`Inventory.PickBlock`, which never grants) and a Q-drop through
`World.ThrowSelected(wholeStack, eye, forward, inventory)` from that player's eye and look and out
of that player's inventory, never the server camera — and **always** answers with a full
`InventoryState` snapshot to that player alone, accepted or not, so a refused intent rubberbands
the client's optimistic copy. Drops are picked up per player: a server-mode `World` reads the
connected players from `World.ServerPlayers` (the server's list) and `DropManager.Update`'s
multi-collector overload (`DropCollector`) flies each drop in a loaded chunk to the nearest player
whose inventory has room (a drop in an unloaded chunk is neither collected nor simulated; one
nobody can take lets go and falls), crediting that player's inventory and flagging
`ServerPlayer.InventoryChanged`, which the next `Pump` answers with that player's snapshot; the
server camera and `World.Inventory` never collect.

**World intents.** A client never writes a cell: every world action is a `WorldIntent` the server
replays for the sending player through the singleplayer rules. Each `ServerPlayer` owns a
`PlayerContext` (`OpenGLTest/src/PlayerContext.cs`: eye, aim, view direction, its inventory, its
server creative flag) that `World` runs every interaction path through instead of the camera
(singleplayer builds one from its camera, whose raycasts keep the third-person rules). An intent
is refused (and nothing applied) when its sequence id is not above the player's last
(`ServerPlayer.AcceptSeq`); a **dead** player's intent is judged right after that, before every
rule below — only a `Respawn` is accepted, however far its eye drifted, and anything else is
refused (see "Player status"), while a living player's `Respawn` is refused. Otherwise an intent
is refused when the world is not playing, its eye is more than
`NetMaxIntentEyeDriftBlocks` from the player's accepted position (`NetValidation.EyeWithin`: one
state interval of movement plus slack), or, for an edit (break completion, place, use, throw),
the player's `EditLimiter` (`RateLimiter`, `NetEditIntentsPerSecond`/`NetEditIntentBurst`) is
empty. Then, by kind:
- **Break** — `BreakStart` and `BreakComplete` both require the player's own first-person ray from
  the intent's eye along its aim (`World.RaycastFor`, `InteractionReach`, fluids excluded) to hit
  exactly the named cell, loaded and neither air nor fluid. The start records the target, the held
  item (id and tool instance) and the server time in `ServerPlayer.Breaking` (`BreakProgress`); the
  completion breaks only when the held item's hits since then reach the block's hardness the way
  the client counts them (first hit at the start, then one per `Mining.HitGapSeconds`, each dealing
  `Mining.BlockDamage`, summed in float — `BreakProgress.RequiredSeconds`), less
  `NetBreakSlackSeconds` of jitter, never with a tool `Mining.CanMine` refuses, and never after the
  target or the held item changed; a server-creative player breaks at once. The break itself is
  `World.BreakBlockFor`, i.e. the full `RemoveBlock`: felling (`FellWood`, which now runs on the
  server), multiblock revert, loot through `DropManager.SpawnLoot` (picked up by the nearest player
  with room), a station's hand-back, needs-support pops, toppling and the fluid, leaf-decay, LOD
  and canopy hooks.
- **Place** — the ray must hit the named cell, and the named face must be the ray's own or another
  face of that cell turned toward the eye (`NetValidation.FaceFacesEye`: a third-person aim reports
  the camera ray's face; the far side of a wall never qualifies). `World.TryPlaceSelectedBlockAt`
  then places the player's **selected** block (owned, placeable, one spent unless creative) under
  the placement rules (`Placement`, no live falling cube, fragile blocks on solid ground, and no
  connected player's body in the cell — `World.BodyBlocksPlacement` checks every `ServerPlayer`, the
  server camera is nobody), with the world's own state byte (a placed log `WoodState.Detached`, a
  placed leaf persistent).
- **Use** — `World.TryUseSelected(InteractionReach, context, out outcome, allowPlace: false)`: the
  right-click chain (campfire, quern, kiln, cast mold, then the held item's `OnUse` — bucket, pile,
  firestarter on TNT or a kiln, converter) aimed by the player's ray and spending from its
  inventory, short of the placement fallback. The item-use tail reads the dispatching player
  (`World._activeContext`), and a chance roll goes through `IItemUseWorld.NextUseRoll`
  (`World.UseRoll`, a test seam).
- **Throw** — `World.ThrowFor` launches the selected throwable at the intent's charge from the eye.
- **Attack** — a punch needs a non-slicing hand and a slice a slicing weapon, each at most once per
  its pace (`ServerPlayer.TryAttack`, `AttackPacing`: the held item's hit gap, or
  `PlayerModel.SliceDuration`, less `NetAttackSlackSeconds`); `World.PunchFor`/`SliceFor` find
  the targets among the server's mobs from the intent's eye and aim with the `Combat` rules.

Every intent is answered, in order: an intent that can edit cells gets a `BlockCorrection` with
the authoritative state of the 3x3x3 cells around its named cell (within edit reach), so whatever
the client predicted there snaps to the server's truth whether it was accepted or not; an intent
whose prediction can change the inventory (place, use, throw), and any refused one, gets that
player's `InventoryState`; then its `IntentResult`. Consequences beyond the corrected cells (a
felled crown, a blast, a flow) reach everyone through the journal. A raw `BlockEdit` from an old
client is read, logged and refused, answered with the named cell's state when it is in reach. A
`PlayerState` whose creative or flight request the server does not grant is answered once per
request with a `PlayerStatus` (`ServerPlayer.ShouldReportModes`), and the client takes those modes
(`Camera.SetModes`). The server also broadcasts every station block entity it runs as a
`StationState` on the entity cadence (and one empty snapshot after the last one goes). It **journals every raw cell
write** (`Chunk.CellWritten`, set in `Start`, cleared in `Stop`) and broadcasts the coalesced
batch as `BlockChanges` each `Pump`, so its own simulation — explosions, fluid flow, leaf decay,
felling, tree growth — reaches clients too. It broadcasts the live mobs, drops, primed TNT and
falling blocks every `NetEntitySyncSeconds` (`EntityList`, with stable ids by object identity). Client input is
validated by `NetValidation`: a `PlayerState` farther than `NetMaxMoveBlocksPerSecond` allows is
clamped, the authoritative position moving toward it by exactly the allowed distance (so a burst
of fast states converges at the cap instead of freezing at a stale point that would get every later
state rejected too); the cap is 130 b/s, the sprint-flight diagonal (`BaseSpeed *
FlyBoostMultiplier` = 80 b/s on each of the horizontal and vertical axes, ~113 b/s together) plus
slack. On top of that clamp the movement rubberband rejects an illegal move (below). The severed
player is removed on disconnect (its context marked absent, so a pour bound to it pauses).

**Streaming around every player.** Chunks load around **every** connected player, not around the
server camera: `World.Focus` (`Systems/StreamingFocus.cs`) publishes an immutable array of focus
chunks — every `ServerPlayer`'s chunk on a server with players online (players sharing a chunk
count once), the camera otherwise (singleplayer, a client, and a server while nobody is online) —
swapped with `Volatile.Write` only when a focus changes chunk or the set changes, so a still frame
allocates nothing. `World.UpdateFocus` refreshes it right before each `ChunkManager.Update(foci)`
(and once in the World constructor, before the generation workers start). `ChunkManager` unloads a
chunk only outside **all** foci's radii (`StreamingFocus.WithinRadius`), orders and drops mesh
uploads and dirty rebuilds by the distance to the nearest focus (`MinDistanceSquared`), and keys
its unload gate on the focus array's reference (plus the 120-frame fallback); an empty focus list
unloads, applies and builds nothing. `ChunkManager.Update(Camera)` stays as a thin overload over
its own single-focus `StreamingFocus` for the tests. `ChunkGenerationWorker` keeps one
`ChunkScanFrontier` per focus index (an array grown by compare-and-swap, since every worker
thread calls it) and round-robins the scans over the foci. `GameServer` no longer moves the
camera (`World.SetServerFocus` remains, for tests): the camera stays at the world spawn, is the
focus only while nobody is online and remains the join spawn. Two far-apart players therefore
cost roughly two load spheres of generation, meshing and memory, sharing the same per-frame
budgets (`MaxDirtyScanPerFrame`, `MaxMeshUploadsPerFrame`), which are not retuned for it. The far LOD stays
camera-bound: the server builds far regions per client request.

**Player status** is server-owned. Each `ServerPlayer` owns a `PlayerHealth` (`Status`, with
`Health`/`IsDead`), a `FallTracker` (`FallTracker.cs`, the GL-free peak/grounded chain lifted out
of singleplayer's `World.UpdateHealth`, which still uses it with `DefaultMaxStepBlocks`), a
`RespawnEye` and a mob-hit sink. The server never simulates a player's body; it probes its own
loaded chunks around the reported position (`Player/PlayerBodyProbe.cs`: `IsKnown` — the feet
cell's chunk and the one `NetGroundProbeBlocks` below are loaded —, `Grounded` — the body lowered
by `NetGroundProbeBlocks` collides, through the static `PlayerPhysics.BodyCollides` the client's
own collision now delegates to —, `InWater`, mirroring `UpdateWaterState`'s swim test, and
`InLava`, the one lava rule `UpdateWaterState` also calls). The fall tracker is fed once per
**accepted** `PlayerState` (`ServerPlayer.ObserveAcceptedState`), with the state gap's allowed
distance (`NetValidation.AllowedDistance(gap, NetMaxMoveBlocksPerSecond)`) as its teleport bound —
exactly what the accepted position already obeys, so only the server's own teleports (join,
respawn, a correction: `Teleport`/`ResetFall`) reset it and lag errs toward no damage, never extra
damage; a body over unloaded chunks is not judged. Lava and the damage flash tick once per `Pump`
(`ServerPlayer.TickStatus` from `GameServer.TickPlayers`), creative or flying being immune as in
singleplayer. Mob hits land through `IMobTarget.TakeHit` (below), which bills the player's own
pool and queues the knockback. Whatever changed the status marks `StatusDirty`, and `TickPlayers`
then sends that player a `PlayerStatus` (modes, health, dead, and the queued knockback, handed
over and cleared by `TakePendingKnockback`); one more goes out at join, so a returning player's
health (or death) is mirrored at once. The status is saved with the player (health and respawn
eye in `players/<slug>.json`). A **dead** player stays where it died: its states still record
look, flags and modes but never move it, and every intent but `Respawn` is refused. The client
mirrors all of it: `World.ApplyNetworkHealth` (client mode only; lower health is taken as damage
with the flash and a singleplayer-sized shake, higher is restored outright), `UpdateClient`
fading the flash, `Camera.AddKnockback` for the shove (the client's own physics applies it) and
`SetModes`; a client never bills itself damage. On the first dead status the client sends a
`Respawn` intent at once (`Program.SendRespawn`, from its eye and aim) and re-sends it every
`NetRespawnRetrySeconds` while the status still reads dead; the server judges it before the
eye-drift and rate rules, so a corpse that kept falling on the client can always come back, brings
the player back at full health at its `RespawnEye` (else the join spawn, `ServerPlayer.Respawn`),
snaps the client there with a `PlayerCorrection` and sends the fresh status. There is no death
screen: the respawn is automatic, as in singleplayer.

**The movement rubberband.** Every accepted-for-position `PlayerState` goes through
`MovementRules.Judge` (`Networking/MovementRules.cs`, GL-free, verdict `MoveVerdict` Accepted /
Clamped / Rejected, per-player memory in `ServerPlayer.Movement`, a `MovementState`): first the
converging speed clamp above (`Clamped`, never a correction), then, on the bounded point and only
without the flight flag and over loaded chunks (`PlayerBodyProbe.IsKnown`; unknown means accept):
a body that would collide with a solid cell while the previous position did not is rejected (a
player already inside a solid may move, so it can leave), and, when the state gap is at most
`NetMaxJudgedStateGapSeconds`, a climb faster than `NetMaxRiseBlocksPerSecond * gap +
NetRiseSlackBlocks` is rejected, and so is a hover: airborne, not in water and not sinking for
longer than `NetHoverGraceSeconds` in total. The numbers are generous on purpose — a jump, a sprint
jump, a sprint auto-step of a whole block inside one state, swimming up and out of water, a mob's
knockback lift, a fall and standing idle never trip them (`MovementRulesTests` asserts the
relations against `PlayerPhysics`' own constants). A rejection keeps the player where it stood,
resets its fall tracking and sends a numbered `PlayerCorrection(seq, eye)`
(`ServerPlayer.IssueCorrection`); the client snaps there (`Camera.TestTeleport`, motion reset),
echoes the number in every following `PlayerState`, and the server ignores state positions until
one acks the newest correction (`AcceptsStatePosition`), so the states the client sent before it
snapped can never trip a second correction. A respawn uses the same correction.

**Creative permission** is per player: a client's creative bit is only a request, granted (with
flight only alongside it, and flight is noclip to the movement rules) while
`GameServer.MayBeCreative(name)` holds — everyone when `AllowCreative` (`--allow-creative`) is on,
otherwise only an operator. `OperatorList` (`Networking/OperatorList.cs`, GL-free) holds the
operators by save slug (`WorldNames.Slug`, the same normalisation that names a player's file, so
a name matches exactly when it would load the same save file — case and punctuation fold away —
and a duplicate joiner's "Name 2" is not an operator); `ServerProgram` builds it from every repeatable `--op <name>` plus
`ops.json` in the hosted world's folder (a JSON array of names, read once at start through
`ChunkSaveStore.ReadFile`; no file means nobody, a broken one warns and means nobody), and nobody
is an operator by default. Both enforcement points use it — the restored modes at hello and every
`PlayerState` — creative grants and clears follow `ServerPlayer.Creative`, and a refused request
is answered once with a `PlayerStatus` carrying the modes the player really has.

**Mobs follow real players.** A mob chases and hurts an `IMobTarget` (`IMobTarget.cs`: `Eye`,
`IsImmune`, `TakeHit(damage, away, speed, lift)`), never the camera directly. `World` rebuilds the
target list each frame right before the slime and goblin managers run: singleplayer's is the
`Camera` (immune while creative or flying; its `TakeHit` applies only the knockback, since World
still bills the managers' `DamageDealtThisFrame` to its own pool, so singleplayer is unchanged),
and a server's is every `ServerPlayer` (immune while creative, flying or dead; `TakeHit` bills its
own pool and queues the knockback for its status), so a server with nobody online chases, spawns
and despawns nothing. `MobTargets` (GL-free) holds the rules: each mob chases the horizontally
nearest non-immune target (the nearest immune one only when every target is), contact and stones
hit whichever non-immune target they touch (a stone stops at any target) and a goblin's melee
impact every non-immune target still in reach, a mob despawns only when it is past its despawn radius from **every** target, and the one shared
spawn timer tries its ring around each target in turn (round-robin, `_spawnCursor`) under the
unchanged global caps. `Goblin.Update` takes the target, and a null one means no chase.

`GameServerTests` (loopback) and `ServerWorldTests` lock the frame, spawn, LOD-queue,
hosted-player, per-player inventory (independent inventories, refused equips corrected, creative
gating and its status report, crafting spends only the crafter's), nearest-player pickup and
intent rules down (a tool-less break refused and corrected, a break faster than its hits refused,
an unowned placement refused, a placement spending exactly one under the placement rules, a
forged overwrite refused, the server-computed state byte, a blast one player primed reaching
another client, its primed cube in the other client's `EntityList` until the blast, mined loot
landing with the breaker only, an intent from a far eye or a replayed
id refused, no placement into another connected player's body), the status rules (a joiner told
its health, death held until a respawn intent from a drifted eye brings the player home), the
rubberband (an illegal move corrected and ignored until acked, a legal walk never corrected), the
operator permission, per-player streaming (a moving player becomes a focus and the camera stays
put; two players 500 blocks apart both stream terrain and edit beside themselves, and a departed
player's chunks unload), with `InventoryIntentTests`,
`ServerPlayerTests`, `DropPickupTests`, `WorldIntentRuleTests` (codec, `BreakProgress`, edit rate,
visible face, the respawn intent), `ClientPredictionTests`, `StationNetCodecTests`,
`NetworkProtocolTests` (the status and correction payloads), `FallTrackerTests`,
`ServerPlayerStatusTests`, `MovementRulesTests`, `OperatorListTests`, `MobTargetTests`,
`StreamingFocusTests`, `ChunkManagerTests.UnloadKeepsAChunkInsideAnyFocusRadius` and
`ChunkGenerationWorkerTests.TwoFociAreBothServed` for the GL-free rules.

**The client** (`OpenGLTest/src/Networking/NetworkClient.cs`) connects on a background thread from
`Program.BeginConnect` (the CONNECT click records `PendingWork.Connect` and presents the
"Connecting..." loading screen, like PLAY), handshakes, then a reader thread queues frames. The
handshake read is bounded by `NetConnectTimeoutSeconds` (cleared before play), the socket is
disposed on every failure path, the connect timeout included, and `Status`/`Server`/`Error` are
published through volatile fields; `Handshaken` is set before the reader starts and the reader's
final `Disconnected` is unconditional, so a server that closes at once can never leave
`Handshaken` standing. `Program.UpdateConnecting` aborts to the server list on `Failed` **or**
`Disconnected`, waits for `NetworkStatus.Handshaken`, then builds a **client-mode** `World`
(`clientMode: true`, `spawn: server.Spawn`, so the camera starts at the server's spawn — a client
camera never runs `Camera.SpawnPosition`'s surface search, and stands at
`Camera.PlaceholderSpawn` when no spawn is given) with the renderer's pools and the window — no
worldgen, no LOD threads and no local simulation — and plays. The client derives nothing from the
seed: terrain, water levels and the biome tint all stream from the server, so it never calls
`WorldSeed.Apply` and a connect is never refused by the seed lock (the client world still calls
`WorldSeed.BeginWorld`, so a client teardown that times out keeps the lock like a singleplayer
one). A `ClientChunkStreamer` requests the chunks around the camera and, with every chunk it asks
for, the 3x3 chunk columns around it (`ChunkData`/`ColumnTerrain`; a chunk at the load sphere's rim
needs diagonal columns no chunk inside it would ask for). It is the client's `IColumnSource`
(installed as `ChunkManager.ColumnSource`, a `ConcurrentDictionary` because mesh builds on the
thread pool read it): a column already received is never asked again, an unanswered one is re-sent
after `ResendSeconds` like a chunk (a column is a hard dependency), and an arriving column
re-dirties the loaded chunks of the 3x3 chunk columns around it unless it equals the stored one in
heights, water levels and climate (`ColumnTerrain.ContentEquals`). A client chunk never meshes
until its nine columns are in (the dirty gate in the threading rules; all-air chunks exempt), and
the eye's water surface (`ChunkManager.GetFluidColumnTopGlobal`, `Camera.EyeWaterSurfaceY`) reads
the streamed water level too. The far LOD is the server's: a `ClientLodStreamer` requests far
regions (`LodRegionData`, at most `NetLodInFlightMax` outstanding, an unanswered one re-sent only
after `NetLodResendSeconds`), whose `LodNetCodec` payload is the server's finished vertices with
the tint and the canopy bumps already baked, so the client's `LodManager` never reads `ColumnCache`
or `TreeGenerator`; for the same reason `World.NotifyLodEdit` and the inline
`TreeGenerator.PoisonCanopy`/`LodManager.MarkEditedColumn` sites in `RemoveBlock` and `FellWood`
return early in client mode, so a client edit never touches `LodSurface`, `LodRegion.ColumnCaps`
or `TreeGenerator.CanopyDisturbed`. `WorldgenReads` (off in production) is the counter
`ClientWorldgenIsolationTests` turns on to prove a client world under a poisoned seed streams and
meshes with no worldgen call and edits without moving the far-LOD tables, and
`StreamedTerrainMeshTests` that a mesh built from streamed columns is byte-identical to the
worldgen one (a tinted surface and a submerged floor).
`World.ApplyNetworkEntities`/`ApplyNetworkBlockChanges`/`Inventory.Import` apply the server's
entities, cell changes and inventory mirror. Only the mob kinds (`NetEntity.IsMob`) enter the mob
reconcile; primed TNT and falling blocks become **display-only mirrors**, and only in client mode
(`ApplyNetworkMirrors`, so a singleplayer world can never acquire a cube its `Update` would tick
and detonate): a primed TNT is reconciled by id (`PrimedTnt.Mirror` / `MirrorSetState`, so its
strobe clock survives snapshots, the fuse clamped so a bad pair cannot divide the flash by zero),
falling cubes are rebuilt wholesale (`FallingBlockManager.ReplaceMirrors`), and `World.Render`
draws both through the usual `_tntManager.Primed`/`_falling.Falling` lists. A client never calls
`TntManager.Update` or `FallingBlockManager.Update`, so a mirror never moves, lands or detonates
(the cells it leaves arrive as `BlockChanges`); `UpdateClient` only burns the TNT fuse down
locally (`TntManager.AnimateMirrors`) so the flash keeps its pace between snapshots, and a
mirrored falling cube still blocks the client's placement prediction (`OverlapsAny`).
`NetworkEntitySyncTests.PrimedTntAndFallingBlocksMirrorFromASnapshot` locks the mirrors down and
`GameServerTests.APrimedTntReachesTheClientsEntityList` the broadcast.
`ApplyNetworkBlockChanges` writes raw, then rebuilds
each touched chunk once with the light-radius `Chunk.RemeshAfterBatchEdit` (so a remote lamp or
roof edit relights the edge and corner chunks) and registers an arriving quern
(`QuernManager.Ensure`). `ChunkManager.ApplyStreamedChunk` does not queue streamed chunks on the
worldgen water-wake queue, and a client world disables `FluidManager` and `LeafDecayManager`
scheduling (`Enabled = false`), since it never ticks either. **The client predicts, then the
server answers.** Every action runs locally first as a prediction and raises
`World.IntentRaised` (`Program.SendWorldIntent`): mining raises `BreakStart` when it starts a
target (or the held item changes) and `BreakComplete` when its own hits reach the hardness (a
creative client breaks once per `ClientCreativeBreakSeconds` rather than per frame, to stay
inside the edit rate); a claimed right-click raises `Place` (the hit cell and face its placement
used, `World._lastPlaceHit`) or `Use`; a released throw raises `Throw`; a landed punch or slice
raises `Attack`. Intents carry `Camera.Position` and the head ray the camera aimed along
(`Camera.LastAimDirection`), so the server's first-person ray lands on the same cell. Every cell
the prediction changed is reported first through `World.BlockEdited` — mining (`RemoveBlock`, plus
the seven cells a multiblock revert gives back), placement, the bucket's
`ScoopFluidCell`/`PourFluidCell`, the stack writers `SetPileCount`/`SetPileComposition`, all eight
cells of a `TryConvertMultiblock` and `RemoveCellForPrime` — and staged in `ClientPrediction`,
which tags them (and, for place/use/throw and every inventory intent, the inventory) with the
intent's sequence id. A server cell value for a predicted cell — a `BlockChanges` broadcast, or a
`BlockCorrection` answering an older intent — is held back until that prediction's
`IntentResult`, then applied (the newest one per cell); a correction answering the newest
prediction applies at once; and an `InventoryState` older than the newest inventory-predicting
intent is dropped, since the answer to that intent follows and includes it. A client fells
nothing, topples nothing, primes no TNT, rolls no loot, lets no station hand anything back and
damages no mob itself (`RemoveBlock`, `TryPlaceBlockFromRaycast`, `IItemUseWorld.AfterPlaced`,
`SpawnPrimedTnt`, `UpdatePunch`, `UpdateSlice` and `UpdateThrown` are guarded by `_clientMode`):
the server's broadcasts carry every consequence, its mobs' health arrives in `EntityList`, mined
loot arrives as drops it picks up into the player's server inventory, and a throw flies locally
only as a cosmetic copy. The purely cosmetic leaf particles and mining debris, the player model,
the station mirrors' quern animation and fire particles, the mirrored TNT fuses and the cosmetic throws tick in
`World.UpdateClient` so nothing hangs in mid-air. `UpdateNetwork` sends `PlayerState` every
`NetPlayerStateSeconds`, carrying the newest correction it applied (`netCorrectionAck`); chat
feeds `ChatView`, `TimeSync` calls `World.SetLightingTime`,
`PlayerList` reconciles `World.SyncRemotePlayers`, `StationState` replaces the station mirrors
(`World.ApplyNetworkStations`), `PlayerStatus` sets the camera's modes and mirrors health, death
and knockback (and starts the automatic respawn, see "Player status"), `PlayerCorrection` snaps
the camera and makes the next state go out at once with its ack, and `InventoryState` is
imported as the truth when it is current.
Inventory edits are predicted then sent (`Program.SendInventoryIntent`): the hotbar keys and `Q`
are intents only, while the wheel (`Program.MirrorSelection`), a middle-click pick
(`World.TryPickTargetedBlock(inventory, out id)` -> `PickBlock`), an [E]-panel hotbar equip, a
craft (the fed members read before `CraftingSession.Craft` spends the reservation) and a creative
catalog grant or clear apply locally at once and send their intent, so the server's answer keeps
them or snaps them back. Each server message is handled inside a
try/catch, and a malformed one (`EndOfStreamException`, `IOException`, `InvalidDataException`,
`FormatException`, `ArgumentException`) leaves the server with "The server sent bad data" instead
of crashing the game. Remote players are `RemotePlayer`s whose
`PlayerModel` walk cycle runs off the distance they moved; `World` updates them each frame and
`RenderManager.RenderRemotePlayers` draws each as the player body at its broadcast eye. A lost
connection, or SAVE & QUIT while connected, disposes the client world and returns to the
multiplayer page.

**Chat** is universal: `ChatView`/`ChatLayout` (`OpenGLTest/src/UI/Chat.cs`, GL-free, tested) hold
a 100-line log, a 200-char input, arrow-key history and the open flag. `T` opens it from a clean
play state, `Enter` sends, `Escape` closes; while it is open the keyboard is withheld from the
camera exactly like a focused inventory field, and the log sits bottom-left above the hotbar
(`UIRenderer.DrawChat`, its own `RenderManager.RenderChat` UI pass). In singleplayer `Program.
SendChat` echoes locally; in multiplayer it sends `ChatSend` and the server's broadcast is the
only copy, so a line never appears twice.

**Known gaps** (open): players are keyed by name with no authentication, so whoever joins first
under a name gets that name's inventory, status and operator rights (`ops.json` is read once at
start, and there is no in-game op command); a slice's return-swing flag and a throw's charge are
taken from the client (both are paced, neither verified); a third-person placement may land on a
face other than the one the client predicted (any face of the hit cell turned toward the eye is
accepted) and is then corrected; a correction covers the 3x3x3 cells around the named cell, so a
prediction outside it waits for the journal; projectiles and toppling trees are not in
`EntityList` (their cells still sync via `BlockChanges`, and a client never fakes them locally, so
a felled tree shows as cells changing rather than as a toppling entity; a client's own throw is
drawn as a cosmetic copy only); the primed TNT and falling-block mirrors update at the entity
cadence without interpolation (a falling cube steps at 10 Hz; only the TNT flash animates
locally) and never physically shove the client's player (`Camera.PushOutOf` runs only in the full
`Update`); station mirrors update at the entity cadence (10 Hz); every player is named "Player",
because `ChatView.LocalName` is never set from a setting; and there is no authentication or encryption. From the worldgen-free client: the
pebble, branch-pile and snow-layer emitters still read seed-keyed worldgen per cell for an
untouched cell (state byte 0: the `PebbleGenerator`/`BranchPileGenerator` position hashes and
noise keyed on `WorldGenerationConfig.Seed`, and `SnowShape.CountWithState` -> `SnowGenerator.DepthAt`, which
sets the drawn depth of a snow cell; collision reads the state byte alone), and since a client no longer applies the server's seed
it draws those with its own process seed (`WorldSeed.Default`, or the last singleplayer world's),
so untouched pebbles, branch piles and snow depths can differ from the server's
(`ClientWorldgenIsolationTests` picks chunks without them, and `WorldgenReads` does not count
those reads); and `ServerHello` still carries the seed although no client uses it. From the
server-owned status and movement:
a hovering cheater is frozen in place by repeated corrections rather than dropped to the ground;
collision is judged at a state's endpoint only, so one state may pass through a thin wall within
its speed allowance, a body already inside a solid may move anywhere, and a descent slower than
the hover rule's sink tolerance is never caught; knockback is applied by the client's own physics
(the server only queues it in the status), so a modified client can ignore it; respawn is
automatic with no death screen, and a dead player's model stays where it died for others until
the respawn; a player's lava and fall state is judged from its 20 Hz positions, so a long fall
across a lag gap can go unbilled (lag errs toward no damage, never extra); two far-apart players
cost roughly two load spheres of generation and memory under the one set of per-frame budgets;
and while a store-backed server world is still loading, an early joiner is already the streaming
focus but `WorldLoadGate` still measures around the server camera, so a joiner who walks off
before the spawn settles makes the load fall back to its timeout. From the world sources: the
game's main menu ignores `world.json`'s `source`, so an `imported` world opened in singleplayer
regenerates (its saved chunks are restored into generated terrain); worldgen features in a
generated chunk beside a supplied column (trees, caves, pebbles, flowers) are computed from
worldgen's own heights, never the source's — **including** trees whose placement roots inside the
map's footprint but whose cells land in the generated chunk, which `TreeGenerator.TryGetCanopy`
also draws as far-LOD canopy bumps on the generated cells beside the map — so a map should be
padded by `TreeGenerator.ReachChunkRadius` chunks on every side or cover whole 64x64 tree cells;
an imported pebble or snow cell whose state byte is 0 takes its count (and a pebble its loot)
from worldgen's per-cell hash and noise like any untouched cell, so a map that means a specific
count should store it in the state byte; a supplied column's far LOD approximates snow, sea ice, beaches and bare rock
from its biome and dirt depth and draws no canopy bumps or gravel caps; the fluid wake treats
every imported chunk as restored (its border faces are always scanned at load), so a map whose
water disagrees with its columns costs wake work, never a crash; `ImportedMapSource` loads every
column file at construction, so a map of hundreds of thousands of columns costs hundreds of MB
and a long synchronous start (a lazy per-column load is the follow-up); and the server camera,
which `Camera.SpawnPosition` still places on the generated origin surface, can start inside or
under the map's terrain until the spawn settle lifts it.

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
`MountainRockHeight` is bare stone (`ColumnCache.GenerateColumn` reads the stripped soil, the far LOD a
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
(`ColumnCache.GetBiomes`); the LOD samples it per cell and wears the matching tile (snow for a cold
cell, snow-covered ice for a frozen sea sheet — snow tile when snow-covered, else ice, and never
flagged water, since the flag alone selects the fragment-shader water model whatever the tile), so a
distant desert and sea floor agree with the near chunks.
Vegetation is biome-gated. Trees come from the species files (`assets/data/trees`), each planted
only in its `worldgen.biomes`: `oak` in the forest, `spruce` in the taiga and the leafless `cactus`
(a thick stem whose few arms bend upward, wearing the generated `cactus_side`/`cactus_top` tiles;
see `tools/generate-biome-textures.ps1`) in the desert, while `baobab` and `stone_pine` never
generate; the forest also grows flowers (`FlowerGenerator`), and the frozen plains grow nothing.
Wild cotton grows in **clumps** on the warm, dry forest fringe before the desert
(`CottonGenerator`: a forest centre whose desert weight lies in
`[CottonMinDesertWeight, CottonMaxDesertWeight)`, the top end being where the sand dither starts),
stamped after the pebbles and before the flowers and foliage. Placement is the reusable
`PlantClumps` rule over a `PlantClumpSpec`: one candidate clump per coarse cell, its centre jittered
inside the cell (so centres are at least `MinCentreSpacing` apart), a disc whose `MinMembers` nearest
cells always belong and whose rest thin toward the rim, and a column asks every cell that can reach
it, so a clump is never cut at a cell or chunk border. A pure hash of (cell, seed), no noise, so no
reseed; a later clumped plant needs only its own spec and gate. Like flowers, the far LOD draws none.
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
plus its skin. The goblin draws per part: `PlayerRig.FromBbmodel(type.Model)` +
`PlayerRig.BuildMeshes`, one mesh per group, each placed by its `EntityAnimator`.

Every hostile derives from the abstract **`Mob`** (`Mob.cs`). It exposes `Type`, `Position` (the body
centre), `Health`/`IsDead`, `Facing` (yaw, the model faces -Z at 0), `HitFlash`, and the body box as
`BodyHalfExtent`/`BodyHalfHeight`. `HeadAboveCentre` is where the healthbar sits. It also has
`TakeDamage` and `Shove`. `World` rebuilds one `_mobs` list each frame (`RefreshMobs`), after every mob
manager has run and before `UpdateThrown`, so a mob killed or despawned this frame is never hit or given
a healthbar. The punch, the slice, the player's thrown items (`ThrownProjectile.Update`, an AABB overlap
against each mob's body box, so a tall goblin is hit through its head and shins) and the healthbars
(`EntityHealthbarRenderer`, buffers sized by `WorldGenerationConfig.MobsMaxAlive`, the hard cap
below) all read that one list. The render passes keep one list per kind, because the meshes differ; `ReflectionEntities`
carries both. Ring spawning on dry, loaded forest surface goes through `Systems/MobSpawner.cs`
(`TryFindSurfaceSpawn`), and each manager wraps it with its own radii and body-centre offset.
**Two global caps** bound every kind together (`MobCaps`, owned by `World` and handed to each manager,
which registers itself as an `IMobPopulation`; a manager built alone caps itself): the ring spawners
stop at the **natural cap** (`MobNaturalCap`), and a **forced** spawn — a manager's public `Spawn`
(a slime splitting, a scenario, a save restore, a client mirroring its server) — may pass it but
returns null at the **hard cap** (`MobHardCap`, `MobHardCapFactor` times the natural one, which is
`MobsMaxAlive`). Both count living mobs only (`LivingCount`, dying excluded), so a new kind needs no
cap of its own: it registers with the world's `MobCaps`.
The managers never read the camera: they take a list of `IMobTarget`s (the camera in
singleplayer, every connected player on a server), chase the nearest non-immune one, spawn rings
round-robin over them under the global caps and despawn by the distance to the nearest (see "Mobs
follow real players" under "Multiple players and chat"); the `Update(..., Camera)` overloads wrap
the camera as a one-target list.

The first entity is the **slime** (`Slime` + `Systems/SlimeManager.cs`), owned by `World`. It hops
toward the player, damages on contact only while airborne (a slime merely bobbing in water is
harmless), is shoved (harmlessly) by the player when grounded, floats to the surface of water like an
item drop (its water hops keep a land hop's total speed but halve the vertical and move it into the
horizontal, so it skims forward as a low jumping slide), and rolls its `loot` table on death. Natural
spawns appear on dry forest surface in a 20-40 block ring around the player; `SlimeManager` holds the
cadence, damage and shove numbers via `WorldGenerationConfig.Slime*`. Slimes come in **sizes**
(`Slime.Size`, `SlimeSize.Min`..`Max`, fixed for life): every scaled stat is the size-1 value times
`1 + (size - 1) * step`, one `SlimeSize*Step` per stat — the body scale (`SizeScale`: the drawn model
in `RenderManager.SlimeTransform`, multiplied over the squash spring, the collision box
`BodyHalfExtent`/`BodyHalfHeight`, the healthbar height), the health pool (`Mob.MaxHealth`, virtual,
over the JSON `hp`; the save and net clamps read it), the contact damage and the loot rolls (the table
rolled `LootRolls` times). The size is saved (`EntitySaveData.MobData.Size`, absent = 1) and sent (a
slime entry's `NetEntity.Ammo`). A ring spawn rolls its size (`SlimeSize.Roll`, a pure function of the
night, an underground flag and one uniform roll): each size past 1 is reached with the large-size
chance again, which eases from `SlimeLargeChanceDay` to `SlimeLargeChanceNight` with
`DayCycle.NightFactor(World.LightingTime)` — the same sun `RenderManager.SetLighting` lights the world
by — and is the night value for ground at least `SlimeUndergroundDepthBlocks` under its column's
surface (`SlimeSize.IsUnderground`; the surface-only ring never finds such a spot today). A rolled body
that would overlap a block beside the cleared column steps down a size until it fits. A slime larger
than the smallest **splits** on the transition, never on being low (`SlimeSize.SplitsOnHit`): a hit that
takes it from above half its health to at or below half while it survives marks it in
`Slime.TakeDamage`, and `SlimeManager.Update` then spawns `SlimeSplitChildren` of the next smaller size
inside its body, shoved outward with a small hop, as forced spawns (only as many as the hard cap has
room for). The parent lives on, so a killing blow, a second hit below half and the smallest slime never
split. Only the authoritative simulation deals damage, so a client's mirror never splits.
`visual-examples/slime-split.json` (`VOXEL_TIME=147`) hits a large slime across its half line and films
the children and their own split. The player fights back with
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
slime: dry forest surface, a 20-40 block ring, under the shared natural mob cap and despawn beyond
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
  (`BlockRaycaster.SegmentHitsBlock`). A blocked check is retried only every `GoblinLosRetrySeconds`, and
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

### Animation juice (springs, mob life-cycle, player secondary motion)

`Spring.cs` is the one velocity-carrying damped spring (`Impulse`, `Step(target, dt)`; frequency in
Hz, damping ratio below 1 rings); `MathUtils` also has `EaseOutBack`, `EaseOutElastic`,
`EaseOutBounce` and `EaseInOutCubic`. Use them instead of a linear fade wherever something should
overshoot or jiggle.

- **Mob life-cycle** (`Mob.cs`): every mob has a spawn pop (`SpawnProgress`), a death
  (`BeginDying`/`IsDying`/`DeathProgress`/`IsRemovable`, `DeathSeconds` per kind), a hit recoil spring
  (`HitKick`) and an eased `HitFlash`. A killed mob **rolls its loot at once**, then stays in the
  manager's list (still drawn, no longer a target, a threat, a saved or broadcast mob or part of the
  live count) until its death has played; `World.RefreshMobs` skips dying mobs, the spawn caps use
  `LivingCount`, and a client's `Remove` (the server stopped reporting it) starts the death instead of
  popping it. `AnimateMirror` advances the clocks of mobs a client never simulates.
- **Slime**: squash and stretch ride one under-damped spring (`Slime._squash`): a 0.2 s crouch before
  each hop (the launch waits for it), a stretch kick on take-off, a landing kick in proportion to the
  fall, an idle breath, a ballooning pop on death and an overshooting pop-in.
- **Goblin**: its facing eases (`Mob.TurnToward`); `Goblin.BodyEffect(feet)` is the pose the clips
  cannot give (a lean that follows the walk, the hit flinch, the death fall and shrink, the spawn pop)
  and the renderer multiplies it onto every part and the held flint. The clips are generated by
  `tools/generate-goblin-clips.py` (re-running is byte-identical, `--check` verifies): idle, walk,
  throw, melee as before (the `release` at 0.42 s and `impact` at 0.30 s are pinned by gameplay
  pacing) plus the optional `hurt`, `death` and `spawn`. `EntityAnimator.PlayOverlay` plays `hurt` as
  an **additive overlay** (translation added, rotation composed) so a hit never cancels a throw or
  swing; `death` and `spawn` are ordinary actions.
- **Arm frames** (`PlayerModel.ApplyArmFrames`): the base arm is a forward flex plus a yaw, so both
  bones stay in one plane and it cannot reach out sideways. Poses that need that are given as an upper
  arm *roll* (abduction about the front axis, then flexion about X, then yaw; the left arm mirrors the
  roll and yaw) and a forearm *flexion plus bend*, slerped over the base frames by a weight
  (`UpperFrame`/`ForeFrame`/`Blend`). With the arm held out the elbow's X hinge is the vertical axis, so
  a plain forearm flexion swings it forward in the horizontal plane and a bend about Z lifts it up. In
  use: the **jump** (arms out to the sides, a soft elbow, both knees folded under; a sprint jump trails
  them back), the **punch** (the elbow flares out and forward at the chamber with the forearm folded to
  the front, the weight `Retract * (1 - Extend) * (1 - Recover)` fades it to nothing at full extension
  where the IK frame on the aim line takes over, so the elbow swings in and forward through the thrust),
  the **throw charge** (the upper arm out to the right and a little behind the body, the forearm bent up
  at about a right angle, the shoulders wound back toward the throwing side, then a whip forward and a
  settle) and the **swim stroke**. The hand anchor follows the forearm frame, so the held item does too.
- **Slice** (`PlayerModel.SlicePhase`): a whole-body motion in uneven phases of the same 0.7 s: a slow
  wind-up that coils the body (to 0.26), a strike about twice as fast as before centred on the impact
  (0.26..0.49, a steeper `EaseInOutCubic` through the middle) and a long eased recovery, so the contrast
  reads as a snap. The arm sweep is only part of it: the whole body turns about the feet
  (`SliceBodyYaw`, the legs counter-turning by `SliceLegCounter` so the feet stay planted), the shoulders
  turn further (`SliceTorsoYaw`), the knees and hips sink into the strike and the torso dips. The hand
  also travels a diagonal on every slice, from `SliceStartRaiseBlocks` above the chest line (so the cut
  starts high, not horizontal) down to just `SliceEndDropBlocks` below it (`SliceDiagonal`, through the
  same re-solved arm as the chest-height drop), whichever way the swing goes. The slicing **arm is held out in front,
  nearly straight, at chest height** (`SliceArmShoulder`, `SliceArmElbow`): the carrying pose (elbow bent 75
  degrees, forearm up, which made the hand look held up by the elbow) is eased into it as the stroke ramps in. The
  **blade is carried by the wrist**: its tip points along the forearm (so it follows the arm's sweep and is
  always across the direction the hand travels, never turned up mid-cut), rolled about its length by
  `SliceBladeRoll` (cocked on edge, not laid flat) and cocked up from the forearm by `SliceBladeCant`, and through the main part of the cut (`SliceMainWeight`: eased in at the end of the wind-up,
  full through the strike, out over the start of the recovery) turned about the vertical by `SliceBladeYawOffset`
  (+45 degrees in the engine's yaw convention, so the tip leads ahead of the hand's travel; mirrored for the
  return); in
  `GetHandAnchorMatrix` the wrist orientation takes over from the body-space one by the stroke. The tip therefore
  sweeps out to the wind-up side, forward as the hand crosses the middle and out to the far side (mirrored for the
  return).
  On top of that the pose adds a **display-space offset** to the held blade through the main part of the cut
  (`PlayerModel.HeldItemRotationOffset`, from `SliceBladeDisplayYawDegrees` / `SliceBladeDisplayRollDegrees`):
  degrees in the item JSON's `display.rotation` order `[pitch, yaw, roll]`, which `Item.DisplayMatrix(offset)` and
  the debugger's `ItemDisplayEditor.DisplayMatrix(offset)` add to the item's own rotation, so the blade is drawn
  at yaw -145 / roll 70 mid-cut and at its JSON yaw -90 / roll 45 at rest. **That is how a mid-animation held-item
  pose is tuned**: in the item debugger scrub the timeline to the frame, slide Rot yaw / Rot roll until it reads
  right, and put the difference from the JSON values into those constants; the sliders keep showing the JSON
  values (what SAVE TO JSON writes), since the offset is the pose's, not the item's. The yaw part mirrors for the
  return swing and the roll does not: for a display yaw of -90 that makes the return's blade the exact mirror image
  of the first swing's (`PlayerModelTests.TheDrawnBladeLeadsAheadOfTheForearmThroughTheCutAndTheReturnSwingIsItsMirrorImage`,
  which measures the item as drawn: the blade sprite's tip is its (1, 1, 0) diagonal and its edge the (1, -1, 0) one,
  both through `DisplayMatrix(offset)` and the hand anchor).
  The **shoulder socket** (`ShoulderSocketInward`, 0.09) slides the joint in toward the torso as an arm lifts off
  hanging (`SocketWeight`), so a raised arm stays seated on the body instead of hinging on a point 0.1 outside it
  with a gap; the punch IK includes it. `SliceImpactProgress` (where the damage lands) is the middle of the strike. Filmed by
  `visual-examples/animations-slice.json`.
- **Attachment** (`PlayerModel.GetPartMatrix`): the neck and both shoulders are children of the **torso
  frame** (hip-origin, turning with the torso's lean, twist and roll), so a limb can never come off the body
  or sink into it; only the legs and the pelvis hang off the root. While a tuned pose is showing (the slice
  sweep, the cocked throw, the punch) the torso's twist is taken back out of that arm's yaw so its hand path is
  the one authored in body space; an idle arm just follows the torso round. The punch IK is solved from where
  the torso actually carries the shoulder (`PunchTargetFromShoulder`), not from its rest place. On top of that a
  reaching arm **pulls its shoulder** a little toward where it reaches (`PullTarget`, spring-smoothed so a
  thrust or release trails and settles, at most `MaxShoulderPullBlocks` = 0.08) and the torso leans toward the
  pulled side. `PlayerModelJuiceTests.TheShouldersAndTheNeckStayOnTheTorsoThroughEveryAnimation` runs every
  debugger mode and asserts that no shoulder or neck is ever farther off its place on the torso than the pull bound.
- **Swim stroke animation**: `PlayerModel` runs the physics' stroke clock (`SwimStrokeProgress`, started
  by `TriggerSwimStroke`; a remote model with `AutoSwimStroke` starts its own while it moves): the arms
  reach forward together, thrust out to the sides (slightly behind the shoulders) in the first 20 %,
  hold for a beat, then creep back to the front over the rest, where the cycle rests until the next
  stroke. The legs snap straight with the thrust and draw up during the recovery, and the whole body
  tips about the hips toward the heading (`Update(..., swimForward:, swimSide:)`: forward when
  swimming ahead, back when backing off, rolled into a strafe, plus a lunge at the burst).
- **Surface float** (`PlayerModel.Update(..., floating:)`, `FloatBlend`, `Float*` constants): a body at
  rest in `WaterPhase.Float` is upright and straight (`ComputeMods` drops the swim tilt and roll by the
  float's share), its arms scull slowly beside the hips (upper arms a little out and forward, forearms
  bent forward, the hands sweeping out and in together) and its legs hang with a faint flutter. It eases
  in over the swim pose through `MathUtils.Damp` and back out during a stroke (a floater swimming off
  strokes in the swim pose) or once the body stops floating. `World.UpdatePlayerModel` feeds the swim
  pose only from `Float` up (a wader walks with no water pose) and `floating` in `Float`. The item
  debugger's `SURFACE` mode (`ITEM_DEBUGGER_PLAYER_ANIM=surface`) previews it, `SurfaceFloatPoseTests`
  locks it down and `visual-examples/water-depth.json` (`VOXEL_TIME=147`) films a poured puddle (wade),
  the deep sea's float, a stroke hop and a dive (swim) from the shoulder camera.
- **Player model** (`PlayerModel.cs`): the existing `Pose` is post-processed by `ApplySwim` and
  `ApplyMods` (`ComputeMods`): a landing dip spring (knees, hips, torso, root drop), crouch
  (`Update(..., sneaking:)`), a swim stroke (`swimming:`), the hit flinch (`Hurt(fromFront)`), torso
  twist and sway in the gait, the slice twist and weight shift, the throw lean, the off-hand counter
  on a punch, a heavier swing for a slower tool (`Punch(weight)`), the first-person arm lag behind
  the look (`SetLook`), the equip dip (`NotifyEquip`), the body-yaw lag with a head that leads
  (`BodyYawLags`, third person and remote players only) and the death fall (`DeathFall`). The limbs
  are still not parented to the torso, so torso-only motion stays small.
- **Camera** (`Camera.cs`): the view effects fold into `_bobHeight`, `_roll` and `_currentFov`, which
  the view cache is already keyed on, so none needs a key of its own: a landing dip, a directional
  hit kick (`AddHitKick`), an FOV pinch on a swing (`PunchFov`), a crouch eye drop and the death view
  (`DeathProgress`). The **sprint FOV** widening (`SprintFov`, knobs `SprintFov*` in
  `WorldGenerationConfig`) is its own eased term *added* to the base FOV and the pinch: the sprint key
  with movement on foot or flying, never sneaking or swimming, scaled by the speed and clamped at the
  on-foot sprint, so the 10x flight sprint widens no further. The **strafe tilt** (`StrafeTilt`,
  `StrafeTilt*` knobs) is likewise an eased term *added* to `_roll`: positive roll tips the view's up
  axis toward the camera's right, so a strafe right leans the head right, into the move; off while
  flying or dead. **Sneak** is Left Ctrl on foot (`Camera.IsSneaking`): slower, never sprinting, a
  lowered *view and model only* — the body and its collision keep their size, so the server's
  movement rules (which derive the feet from the eye) are unaffected.
  The **throw charge zoom** (`ThrowZoom`, `ThrowZoom*` knobs) is a third FOV term *added* beside the
  sprint widening and the pinch: `World.UpdatePlayerModel` copies the arm's `PlayerModel.ThrowCharge`
  into `Camera.ThrowCharge` (like `HoldPunchFov`), and the next head-motion update springs the view
  toward `ThrowZoom.TargetDegrees` (narrower with the eased charge, 0 with none) through an
  under-damped `Spring`, so a release or cancel springs back with a small wide overshoot, clamped at
  half the zoom. Singleplayer and a client alike (it reads only the local arm);
  `visual-examples/throw-zoom.json` films it, `ThrowZoomTests` locks it down.
- **Hit-stop** (`SwingHitStop`, `HitStop*` knobs): a punch or slice that connects
  (`World.UpdatePunch`/`UpdateSlice`; on a client the *predicted* connect, a mob in the cone or
  hemisphere with line of sight, since the server's verdict comes too late) calls
  `PlayerModel.HitStop`, which holds that swing at its impact pose (full extension, or
  `SliceImpactProgress`) and then replays the rest faster, so the swing still ends when it would have:
  the slice combo, the jab pace and `AttackPacing` never see it, and the world, mobs and movement
  never stop. `Camera.HoldPunchFov` holds the pinch while `PlayerModel.IsHitStopped`. The item
  debugger never requests one. `visual-examples/hit-stop.json` films it.
- **Death in singleplayer** plays for `World.DeathPlaySeconds` (the view tips over, the body falls)
  before the respawn; a client has no death screen and still snaps back.
- **Ground dust** (`GroundDust`, owned by `World`; cosmetic, so never on a server): a landing kicks
  up chips of the block under the feet (`GroundDust.TryFindSurface`: the feet's own cell if it holds
  a stack cover like a snow drift, else the cell below), sized by `LandingDust` (nothing under
  `LandingDustMinFallBlocks`, then count and launch speed grow with the drop along
  `MathUtils.ImpactStrength`, both capped). They go through `BlockDebrisSystem.Puff` as
  `DebrisLook.Surface` chips, which wear the block's top on every face (`BlockCubeCache.GetSurface`), so
  grass dust is tinted green. The local player's landings come from `GroundDust`'s own
  `FallTracker.LandedDrop` (singleplayer and a client); a mob reports its landing with
  `Mob.NoteLanding` (the goblin's and slime's physics do) and `World` takes them after `RefreshMobs`
  (singleplayer only: a client's mobs are mirrors and never land). A slime splats its own goo instead,
  on every hop (`LandingDust.SplatCount`/`SplatSpeed`, linear in the impact like its squash kick, scaled
  by the slime's body scale `SlimeSize.Scale(size)`): `DebrisLook.Colour` chips with an air id, one grey cube
  (`BlockCubeCache.GetColourChip`, `WorldGenerationConfig.ColourChipTile`) painted through `uBiomeTint`
  with `BiomeTint.VertexTint` of the mean opaque skin colour (`LandingDust.SkinColour`). Sprinting
  on foot over a `footstepDust` block (grounded, not sneaking, not in water, really moving) puffs once
  per foot strike of the gait: `PlayerModel.WalkPhase` crossing π/2 + kπ (`FootstepDust.FootfallsBetween`).
- **Remote players** (protocol 7): the `PlayerState` flags carry cosmetic `Sneaking`, `Swimming`,
  `Sprinting` and `Airborne` bits (rebroadcast untrusted, never acted on) and every `PlayerList`
  entry ends with the player's last `NetPlayerAction` and a counter that moves per swing, so
  `RemotePlayer` poses the model and replays punches, slices and throws.
  A seventh cosmetic bit, `NetPlayerFlags.Floating` (64), rides beside `Swimming` while the sender
  is in `WaterPhase.Float`, so others see the surface float (`PlayerModel.Update`'s `floating`); the
  client now sets `Swimming` only from `Float` up, so a wader walks for everyone. No protocol version
  moved: the server stores the flags byte raw and rebroadcasts it, and no reader rejects an unknown
  bit, so an older client just poses the swim (`GameServerTests.TheCosmeticFloatingFlagReachesTheOtherClientsUntouched`,
  `RemotePlayerAnimationTests`).
- **HUD feedback** (GL-free state fed once a frame from what the HUD already shows, so singleplayer and a
  client's mirrored inventory and health behave alike): `HotbarNameFade` shows the selected item's name
  over the hearts after a slot or item switch, `HotbarPickupPop` bumps a slot whose count rises (never a
  fall or an item change), and `LowHealthVignette.Intensity` is a slow red edge heartbeat below
  `LowHealthThreshold`, off while dead or creative, drawn as a second term (`uLowHealth`) of the damage
  vignette's one full-screen pass. `visual-examples/hotbar.json` and `low-health.json` film them.
  The **damage direction arc** is that pass's third term (`uHitArc`, `uHitArcDir`): a red glow on the
  screen edge toward where the last blow came from, held then faded (`DamageArc*` knobs). The camera
  owns a GL-free `DamageDirection` that keeps the blow's *world* direction and recomputes the screen
  direction from the current view yaw every frame (`Camera.DamageArcScreenDirection`), so the arc keeps
  pointing at the attacker while the player turns. It is fed by `Camera.TakeHit` (the mob's real
  knockback direction, so a goblin stone from 12 blocks points right, which the nearest-mob direction
  `NotifyPlayerHurt` gives `AddHitKick` would not) and, on a client, by `Camera.AddDamageDirection` from
  a `PlayerStatus` knockback that came with lost health; a fall or lava tick has no direction and shows
  none. `visual-examples/damage-arc.json` films it, `DamageDirectionTests` locks it down.
  **Surfacing droplets** are a fourth term of the same pass (`uDroplets`, `uDropletTime`,
  `uDropletSeed`): when the eye comes up out of water (not lava), hashed drops with a dark rim and a
  glint dot the screen, the bigger ones slide down leaving thin trails, and all fade out over
  `SurfacingDropletSeconds`. The camera's GL-free `SurfacingDroplets` carries the timing: it is told
  the frame's final eye state in `Camera.RefreshEyeWaterState` (the one the underwater tint reads, so
  the shoulder camera's eye counts), aged in `UpdateHeadMotion`, cleared the moment the eye is back
  under, and given a new seed per surfacing; the drop positions, sizes and slides are hashed in the
  shader. `visual-examples/surfacing-droplets.json` films it, `SurfacingDropletsTests` locks it down.
- **Floating damage numbers**: a landed hit floats its damage over the mob (`World.DamageNumbers`, a
  fixed `DamageNumberPool` of `DamageNumberCapacity` that replaces its oldest number when full),
  drawn by `DamageNumberRenderer` right after the healthbars with their billboard shader and the HUD's
  font atlas. Singleplayer sees every accepted `Mob.TakeDamage` through `Mob.HitsTaken` /
  `LastHitDamage` (compared around `StrikeMob`, which punch, slice and throws all use); a client,
  which never damages a mob, floats the health drop between two `EntityList` snapshots, plus the health
  left on a mob that leaves the snapshot within `DamageNumberDeathRangeBlocks` of the camera (the
  server stops listing a mob the moment it dies, so that is the killing blow). A headless server keeps
  none. `visual-examples/damage-numbers.json` films them.
- **Verification**: `visual-examples/animations.json` (run with `VOXEL_TIME=147`) films the player's
  poses from the shoulder camera and the mobs' spawn, hit and death; its `damage`, `view` and
  `player` ops (`hurt`, `punch`, `slice`, `equip`, `charge`, `release`, `die`, `revive`) exist for it. For
  iterating on a single animation prefer the **item debugger** (see "Build and run"): its ITEMS tab has a
  button for every player animation and its ENTITIES tab previews the goblin and slime life-cycles,
  including the slime's code-driven squash, with `ITEM_DEBUGGER_PLAYER_ANIM` / `ITEM_DEBUGGER_ANIM` and
  `ITEM_DEBUGGER_SHOT_AT` for scripted captures. A new player animation gets a mode in
  `PlayerPreviewState`, a new mob animation a mode in `EntityPreviewState`.

## Conventions

- C# with file-scoped namespaces, one type per file, `_camelCase` private fields, PascalCase public members. Primary constructors are used where natural (`ChunkManager(GL gl)`).
- XML doc comments (`/// <summary>`) on classes and non-obvious public methods; brief `//` comments explaining *why* for tricky logic. Match the existing density — the code is moderately commented.
- **New blocks go in JSON, not C#.** When a block has to behave differently, prefer a new data flag on `BlockDefinition`/`BlockType` that every call site reads over another per-id branch in gameplay code — the point of the content registry is that `World`, `TntManager` and the mesher ask "what does this block do?" instead of "which block is this?".
- `FastNoiseLite.cs` is vendored third-party code — do not reformat or refactor it.
- xUnit tests live in `OpenGLTest.Tests/` — run `dotnet test OpenGLTest.Tests/silkgltriangle.Tests.csproj -c Release`. They cover headless logic and simulated gameplay without a window (fixture: `TestContent.cs`); add a test with every feature instead of a throwaway script — but only when there is a *rule* to lock down. A test must assert behavior (a gate, a formula, a state transition, an invariant that survives content tuning), never a constant, a layout number, a content literal or a starting-stock/seed count (`Assert.Equal(1, inventory holds one converter)` is exactly as pointless as pinning a block's hardness) — see the `functional-tests` skill's ban list for the full rule and rewrite table. Verify feel-in-game by running the game and using the `X` debug overlay (FPS, position, grounded state). Watch the console for chunk generation errors — worker threads catch and log exceptions rather than crashing.
- Branch naming follows feature work off `master` (current work is on `worldgen`). Commit messages are short imperative sentences ("Add block placement", "Fix performance").
