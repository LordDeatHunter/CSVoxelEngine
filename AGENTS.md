# AGENTS.md

Guidance for AI coding agents working in this repository.

## Project overview

A Minecraft-style voxel engine written in C# on .NET 9, using **Silk.NET** (windowing, input, OpenGL bindings) and StbImageSharp for texture loading. It renders an infinite, procedurally generated world (FastNoiseLite OpenSimplex2 noise) with chunk streaming, an underground of 3D-noise caves, worm-carved tunnels and heightmap ravines, binary greedy chunk meshing with ambient occlusion (plain cubes are culled by bitmask and merged into larger quads wherever the result is pixel-identical; procedural blocks and fluids keep per-quad emitters — see "Chunk meshing"), frustum culling, block breaking/placement via raycast, and simple player physics (gravity, jumping, collision). The player spawns standing on the terrain at the origin (`SpawnOverrides.SpawnPosition`), or where they left a saved world; worlds are created, listed and opened from a main menu and saved to disk (see "Launch flow" and "Saves").

The renderer requires an **OpenGL 4.6 core** context: `WindowManager` requests `APIVersion(4, 6)`
with `ContextProfile.Core`, and every shader under `assets/shaders/` declares `#version 460 core`.
The chunk draw path uses those 4.6 core features directly — `gl_DrawID`, `BufferStorage`,
persistent buffer mapping and `glMultiDrawElementsIndirect` (see "Chunk drawing" below) — so this
is a hard requirement rather than a preference; any 2015+ desktop driver on Windows or Linux
provides it, but **macOS (stuck at GL 4.1) is no longer supported**. The opaque chunk pass also runs compute shaders
(the Hi-Z occlusion cull, which zeroes culled commands in the indirect buffer in place; see "Chunk
drawing"), another 4.3+ core feature.

The game is one project under `OpenGLTest/`; the item debugger (`OpenGLTest.ItemDebugger/`) and the dedicated server (`OpenGLTest.Server/`) are separate executables that reference it. Historical naming quirks to be aware of:

- The solution is `VoxelEngine.sln`, the project folder is `OpenGLTest/`, the csproj is `OpenGLTest/VoxelEngine.csproj`, and all code lives under the `VoxelEngine` root namespace with one sub-namespace per folder (see "Code organization"). Keep a new file in the namespace its folder maps to.
- Verification is by building and running the game **and** by the xUnit tests in `OpenGLTest.Tests/` (headless, no window: `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release`) plus the scripted visual scenarios under `OpenGLTest/visual-examples/` (see `VOXEL_SCRIPT` below). There is no CI.

## Build and run

```powershell
dotnet build VoxelEngine.sln -c Release
dotnet run --project OpenGLTest/VoxelEngine.csproj -c Release
```

Always build and run **Release**. Debug is several times slower in the voxel loops (mesh
builds, light propagation, the per-frame chunk walks), so a framerate or hitch observed in Debug
says little about the game. `Directory.Build.props` at the repo root makes Release the default
when no `-c` is given to a project build or `dotnet run`; a solution build still needs the
explicit `-c Release`, and Rider's run configuration follows the solution configuration
dropdown, which must be set to `Release | Any CPU`. Use `-c Debug` only when you need
breakpoints. The startup line `Build: Release | debugger ...` confirms which one is running.

All csprojs target **net9.0**, but the code uses C# 14 (null-conditional assignment such as
`mouse?.MouseUp += ...`, first-class span conversions), so building needs the **.NET 10 SDK**: an
older SDK (e.g. the 9.0.100 in `~/.dotnet`) fails with CS0079/CS0131/CS0411. Use the SDK 10 launcher
and, on a machine that only has the 10 runtime, prefix every `dotnet` command with
`DOTNET_ROLL_FORWARD=LatestMajor` or the build and the tests refuse to run at all.

**Launch flow.** The game boots to the **main menu** with the cursor free (`Program` owns an
explicit `AppScreen` of Menu / Loading / Playing / Saving; every page rule, key rule and store
call lives in the GL-free, tested `MainMenuController`, drawn by `MenuRenderer.DrawMainMenu`
through `UiPasses.RenderMenuFrame`, which writes framebuffer 0 directly with no HDR pass,
lighting or GPU timer). START opens the worlds page, a scrollable list of `WorldStore.List()`
(most recently played first) with PLAY / EDIT (rename) / DELETE (a confirm popup, DELETE /
CANCEL) on every row, plus CREATE and BACK; an empty list says so. CREATE opens a name field
(placeholder `Unnamed World`, which is what an empty field becomes; the store makes a taken name
unique with the smallest free numeric suffix, `WorldNames.Unique`) and a seed field (empty =
random, an integer is used verbatim, any other text is FNV-1a-hashed by `WorldSeedParser`).
Enter confirms the create and edit pages (never the delete popup), Tab switches field, and ESC
steps back one page and does nothing on the main page. SETTINGS and EXIT are the pause menu's
(the settings page is shared: the one `MenuInput.SettingsPageOpen` predicate drives the slider drag
and its save on both menus). PLAY / CREATE never block inside the click: the click records the
pending work, `OnRender` presents one loading-screen frame, and the next update runs
`WorldSeed.Apply(seed)` -> `new ChunkSaveStore(dir)` -> `new World(..., store)`. The world then
streams in `WorldPhase.Loading` behind an animated loading screen (`LoadingScreenView` /
`LoadingScreenRenderer.DrawLoadingScreen`: a progress bar eased monotonically toward `World.LoadProgress`, a
wall-clock spinner, the world name and `Generating terrain...` for a new world vs `Loading
world...` for a saved one) and is **never rendered** until `IsLoaded`; after that the overlay
fades out over `HudConfig.LoadingFadeSeconds` (`UiPasses.RenderLoadingOverlay`).
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
felled-canopy markers, grown crowns, surface overrides), `wood-damage.bin` (`WoodDamageCodec`: the permanent chop damage of natural trunk cells, singleplayer only), `player.json` (`PlayerSaveCodec`: position and look,
creative/flight, health, mana, respawn eye, lighting clock, the full inventory and hotbar (a staff's bound spells by part key) and the known spell parts by key; no position
while the spawn has not settled) and `entities.json` (`EntitySaveCodec`: the live slimes and
goblins with health, facing, velocity, ammo and a slime's size plus still-pending entries, up to
`MaxSavedDrops` item drops, nearest first, and the drifting icebergs) and `stations.json` (`StationSaveCodec`:
the campfires, querns, kilns and cast molds with their fuel, fire, crucible, cooking and cast progress). See "Saves" below for the rules.

Setting the `VOXEL_SPAWN` environment variable to `x,y,z` (three comma-separated floats, e.g. `-40,-107,-75`) starts the camera eye at exactly that world position instead of on the surface at the origin, which is handy for inspecting a specific cave or feature without flying to it; an unparseable value is ignored with a console warning. `VOXEL_LOOK` takes `yaw,pitch` in degrees (two floats, e.g. `-90,-20`; yaw 0 faces +X so the default `-90,0` faces -Z, and pitch is clamped to ±89 exactly as the mouse look is) and pins the initial facing, so a scripted verification run frames the same thing every time. `VOXEL_TIME` takes a number of seconds and is added to the window clock before the lighting pass reads it (`Program.timeOffset`), which jumps the day/night cycle straight to noon, dusk or night for a screenshot instead of waiting out the orbit (one full cycle is `SkyConfig.DayLengthSeconds`, 1200 s like Minecraft's 20 minutes, with the sun starting mid-morning, so noon is about 147 and sunset about 447). All three parse once at startup and ignore garbage with a console warning. `VOXEL_SPAWN` and `VOXEL_LOOK` (like `VOXEL_SCRIPT` below) skip the main menu and play a storeless, unsaved world; `VOXEL_TIME` alone still boots to the menu and offsets every world opened from it (see "Launch flow" above). Setting `VOXEL_SCRIPT` to a scenario JSON path (see `OpenGLTest/visual-examples/basic.json`) runs a scripted visual test instead of playing: `teleport`/`look`/`wait`/`walk`/`mine`/`place`/`fly`/`creative`/`give`/`spawn`/`grow`/`select`/`screenshot` steps drive the real update functions through a scripted keyboard and save numbered PNGs to `visual/<name>/` (`screenshot` takes an optional `hideHud`); `select` picks a 1-based hotbar slot like the number keys; `damage` hurts every live mob, or the player's own pool with `"target": "player"`; `effect` (`{ "op": "effect", "effect": "burning", "target": "mobs"|"player", "scale": 1 }`, target `mobs` by default) applies a status effect through `StatusEffects.Apply`, the name checked only when the step runs (an unknown one is warned and skipped); `cast` (`{ "op": "cast", "parts": ["element/fire", "shape/projectile"] }`) fires a spell of those part keys from the camera's eye and aim with no staff or mana (`SpellEffects.TestCast`; an unknown key or an invalid recipe stops the run), and `visual-examples/spells.json` (`VOXEL_TIME=147`) films a fire, a frost and a nature bolt; then the window closes. `fly` turns creative flight on (or off with `"on": false`, `Camera.TestSetFlight`) so a scripted camera can hover over a spot instead of falling. `creative` turns creative mode on
**without** flight (or off with `"on": false`, which ends flight too, as the C key does): the
player still falls and lands, but a drop from a mid-air teleport costs no health. Put it first in
any scenario that teleports a walking player above the ground, and turn it off again before a
`mine` step that should take real hits (creative breaks on the first hit). `spawn` places a mob through `World.SpawnEntity`: `{ "op": "spawn", "entity": "goblin", "pos": [x, z] }` snaps the feet onto the terrain (`World.TryGroundHeight` + 1), while `"pos": [x, y, z]` is an exact feet position, and an optional `"size"` (an integer from 1, default 1) is a slime's size; the entity name (`slime`, `goblin`, `hellbrute` or `cinderwitch`) is checked only when the step runs, so parsing stays registry-free. `visual-examples/slime-sizes.json` (`VOXEL_TIME=147`) films the three slime sizes side by side and mid-hop. `visual-examples/hell-mobs.json` (`VOXEL_TIME=147`) spawns a hellbrute and a cinderwitch in a lamp-lit hell cavern and films them idle, casting and swinging. `visual-examples/goblins.json` (run it with `VOXEL_TIME=147`) spawns two goblins in a flat forest clearing under a flying — and therefore immune — camera and shoots `windup`, `stones` and `charge`. `grow` grows a tree on the live world: `{ "op": "grow", "species": "oak", "pos": [x, z], "steps": n }` snaps the column onto the terrain (`World.TryGroundHeight` + 1) and calls `World.TryGrowTree`, which hands it to `TreeGrowthWriter`: the writer rebuilds the tree rooted in that column from its wood state bytes (`TreeGraph.FromWorld`), or plants a sapling on the ground when the column holds none of that species, runs `n` (> 0) pulses of `TreeGrower.GrowFurther` and writes the difference back (see `### Trees`). Like `spawn`, the species name is checked against `TreeSpeciesRegistry` only when the step runs. `visual-examples/trees.json` (run it with `VOXEL_TIME=147`) frames the oak, spruce and cactus, a cut limb and the leaves decaying after it, a felled trunk, grown oaks, saplings, a baobab and a stone pine, the baobab's wide-trunk cuts and fall, and the stone pine's bare limbs under their leaf pads. Scenario parsing is covered by `VisualScenarioTests`; prefer a scenario over ad-hoc scripts for screenshot verification. `VOXEL_DEBUG=1` starts with the X debug line already on, so such a run logs FPS, position and the profiler breakdown once a second.

The **item debugger** (`dotnet run --project OpenGLTest.ItemDebugger -c Release`, same working-directory
rule) previews held items, entities and trees. Its left panel has three tabs. **ITEMS** is the item grid with the
display sliders, and the player body: its mode buttons are **every player animation**
(`OpenGLTest.ItemDebugger/PlayerPreviewState.cs`, GL-free and tested): the poses IDLE WALK RUN SNEAK JUMP
S-JUMP LAND SWIM FLOAT SURFACE FLY, and the swings and reactions PUNCH HEAVY SLICE BACK CUT B-CUT CHOP THROW HURT EQUIP DEATH and CAST (the staff gesture),
which replay on their own after a short rest. Each only feeds the real `PlayerModel` the inputs and
triggers the game does, so what shows is exactly what the game poses (a jump loop with the landing, a
swim that strokes as long as forward is held, a throw that charges, holds and releases, a death that
falls over and gets back up). **Use this tab first for any player animation change**: it is faster
than a scenario and shows the body from any orbit angle (the VIEW button switches to first person).
The blade modes (SLICE, BACK, CUT, B-CUT, CHOP) with a slice or sword item selected also draw the **slice
trail**: `PlayerPreviewState.Trail` is fed once per fixed step on the preview's own clock through the
`SliceTrailSampler` the program installs (posed with the display sliders' live values), so a scrub rebuilds
exactly the trail playing would have, and a paused or finished clock holds it as it stands.
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
they read IDLE WALK THROW MELEE HURT DEATH SPAWN, for the hellbrute IDLE WALK MELEE HURT DEATH SPAWN and for
the cinderwitch IDLE WALK THROW HURT DEATH SPAWN (its cast is named `throw`). A goblin, a hell mob or a
slime is previewed as the real `Goblin` / `Hellbrute` / `Cinderwitch` / `Slime` object driven like a client drives a mob it does not simulate (`Mob.AnimateMirror`, and
`NetworkSetState` for the walk velocity), so what the game layers on the clips shows too: the spawn pop,
the hit recoil, the death, the rigged mob's body pose (`RiggedMob.BodyEffect`, applied to every part and the
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
cells, so the roots feature never shows here. The tree is rasterized with `TreeRasterizer` and meshed
by `CellBatchMesher` (`OpenGLTest/src/Chunks/Meshing/CellBatchMesher.cs`): the real chunk mesher run over scratch
chunks at the cells' real chunk positions, padded by a ring of empty chunks, so thin tree-wood tubes,
wide-trunk shells, the pixel-rim bevel, leaves and the biome tint look as in game, with no clipping at
16-block seams. `DebugSceneRenderer.RenderTree` rebuilds the meshes only when the state's `Version`
moves, and `ToDisplaySpaceTint` rebakes the biome-tinted vertex colors for the debugger, which
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
  `sprint jump`, `land`, `swim`, `swim idle`, `surface`, `fly`, `punch`, `heavy punch`, `slice`, `slice back`, `cut`, `back cut`, `chop`, `throw`,
  `hurt`, `equip`, `death`, `cast`) and selects the ITEMS tab. An unknown mode logs a warning listing them.
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

The app opens a window and grabs the mouse. Controls: WASD to move, mouse to look, Space to jump, `C` to toggle creative mode (placing costs nothing, breaking drops nothing; double-tap Space toggles flight, but only while creative — flight is noclip and Space/Left Ctrl move up/down while flying), Left Shift to sprint (1.5x on foot, 10x while flying), hold left-click to punch first, then mine: a mob (a slime or a goblin) inside the forward punch cone (3 blocks, 30° half-angle, line of sight required; only the nearest is hit) takes the held item's `entityDamage` (1 bare-hand, 3 for the primitive tools, 5 for the copper tools, 6 for the obsidian blade) and a knockback shove instead of being mined through (an item declaring `attack: "slice"` — the obsidian blade — instead swings a two-phase blade sweep: each click hits the nearest two mobs in the forward hemisphere of the same reach, the buffered return swing lands at half power, and the arm and blade are carried across the front; an item declaring `attack: "sword"` — the iron sword — swings a three-swing chain instead: a diagonal cut and a rising back cut over the same hemisphere, hitting the nearest three, then an overhead chop on the one mob under the aim at the highest power and a heavier shove, each swing buffered by a click or chained by a press shortly after the last one ended, see `SwordCombo`/`SwordSwings`), while a clear cone mines the targeted block (hit-based: one hit per the held item's `tool.hitDelayMs`, deals the item's `blockDamage`, 0.5 bare-hand, against the block's float `hardness`, declared in its JSON - 5 for most blocks, 8 for malachite ore, 24 for a full-cube trunk of the `tree_wood` logs and 1 for leaves; a tree-wood cell scales its block's `hardness` by the thickness in its `WoodState` (`Mining.HardnessOf`: `hardness * (tipHardness + (1 - tipHardness) * thickness / WoodState.FullCube)`, so the thinnest wood keeps `wood.tipHardness` of it and placed, detached wood is as hard as a trunk), and `0` means it pops on first contact like a pebble; a block with `requiresTool` and a `tools` dict needs a listed tool tag at or above its required tier, and the wrong tool whiffs for 0 damage and no progress; damage resets if you release, switch target or switch tool; instant while creative, which ignores the gate; a click with no block under the crosshair still swings the fist); a stepped crack-sprite overlay drawn on the block's faces via `break-*.glsl` from `assets/textures/environment/break_progress.png` (8 stacked 32x32 frames, the top one blank); each hit pops the block out and back via `SelectionPasses.RenderBlockPop`), right-click to place the selected block or, when the selected slot holds an item with a `use` action, to use it (the bucket scoops a water source and pours it back out — see the item schema below; with a spell staff held, right-click casts its selected spell slot instead and never places, and Left Ctrl + mouse wheel cycles that slot while the plain wheel still moves the hotbar; right-clicking a spell table with a staff in hand opens the spell builder instead, where clicks pick known parts and a slot and BIND/CLEAR write it, E or Escape closes it, the wheel and world clicks are held off while it is open, and without a staff a toast says to hold one), 1-10 to pick one of the 10 hotbar slots, `E` for the list inventory (a search field sits over the tabs: typing names the owned items and the recipes that make or consume a matching thing, ranked with prefix hits and owned items first, and clicking a result picks the item or opens its recipe; blocks/tools/misc tabs with shared caps of 2048/8/2048; click a row to preview, click a hotbar slot to equip, same item again to unequip; the world keeps running and `E`/Escape closes it, and a focused field takes the keyboard so typing never moves the player). A new survival world starts with just the **hint scroll** (`Items/StarterKit`: `scroll_fire_projectile`, which teaches fire + projectile; a server newcomer and a player saved before known spell parts get it too, never twice); `C` creative mode turns the same Blocks/Tools/Misc tabs into the **whole item catalog** (`CreativeCatalog`) - clicking a row grants one, each row's `[+1]` / `[+10]` / `[+100]` button grants that many, and a red `[X]` clears the id's counts (`Inventory.RemoveAll`), all through the ordinary            `Inventory.Add` - while the Craft tab keeps listing every recipe. `T` opens the chat (type, Enter sends, Escape closes, the arrow keys walk sent messages; while it is open the keyboard is captured so the player never moves while typing) and works in singleplayer and on a server. `X` to toggle debug logging
           (FPS/position/water state to console), Escape to open the pause menu (RESUME / SETTINGS / SAVE & QUIT;        Escape on its settings page steps back to it, and on the main page resumes). SAVE & QUIT presents a `Saving...` frame, calls `World.SaveAndDispose` on the next update and returns to the main menu's worlds page. None of these keys act while a world is still loading.

**HUD and feedback.** The crosshair is drawn as five pieces cropped out of its 16x16 texture (a
center dot and four pills): the arms slide outward with the eased `Camera.ThrowCharge` while a throw
is charged, a ring of dots around it fills with `LeftClickAction.Crosshair.BreakProgress` (0 in creative or
with no target), and the hit marker flashes an X at the connecting-hit sites the hit-stop uses (a
landed punch, slice or sword swing), fading over `HitMarker.Seconds` (`HitMarkerTests`). At the top of the
screen the **look-at label** shows the targeted block: its name and a small spinning 3D render of
it, pushed below the compass or clock strip while one is equipped (`TopStripRenderer.DrawLookLabel` and
its `LookLabelTop` over `BlockCubeCache`), the name in red while the held item cannot mine
it, and "Unknown" with a flat dark silhouette until its item has been obtained; creative mode and a
block with no item at all always know, and `X` adds id, state, sun/block light and cell debug lines
(`LookLabel`, `LeftClickAction.LookAt`, `LookLabelTests`, `look-label.json`). "Obtained" is the inventory's own
set (`Inventory.Knows`, marked in `Add`, saved as names in `player.json` and riding the
`InventoryNetCodec` snapshot, so a server's player and a client agree), which also drives the
"New: <name>" toast on a first pickup or `/give`. `HudToast` draws one line above the item-name row
for the day toast ("Day N" as the sun rises through dawn), the inventory-full warning, the
first-find toast and the spell table's hold-a-staff hint; the spell builder panel hides the crosshair
and the hearts row like the [E] panel does; `HotbarNameFade` shows the selected item's name after a slot or item switch and
`HotbarPickupPop` bumps a slot whose count rises, and a collected drop's icon arcs from where it lay into its hotbar slot (`PickupFlyIcons`, owned by the pop, spawned by `DropPickup` for local pickups only, drawn by `HudRenderer.DrawFlyingPickups`; a client sees none since its drops arrive as snapshots). The [E] panel fades and slides up on open and fades on close (`InventoryView.Transition`, `PanelTransition`; `UIRenderer.SetOverlay` shifts the pass's projection and sets `uFade` in `ui-fragment.glsl`, and panel scissors add `OverlayOffset`). The player's active status effects show as a row of icons left of the hearts (`HudRenderer.DrawEffects`, see "Status effects"). `ItemTooltip.Lines` builds the hover panel over the
[E] panel's rows and the hotbar, and holding Tab draws the player list (a server session only). F2
saves the finished frame through `VisualCapture`; F1 toggles `GameplayInput`'s `hudVisible`, the gate the scenario
screenshot's `hideHud` drives. The chat commands are `/help`, `/tp x y z`, `/give <item> [count]`
(the last two needing rights) and `/time day|night|<seconds>`; the death line is
`DeathMessages.For` over the blow `PlayerHealth` recorded, drained into the chat (`LocalPlayer.TakeDeathMessage`).

`X` also raises the **frame profiler overlay** under the FPS counter, and prints the same
snapshot to the console once a second so a headless run's log carries it. Console writes go
through the async `Log` queue (`OpenGLTest/src/Core/Log.cs`), so a slow terminal never stalls the
frame that logged; `Log.Shutdown` drains it on exit. Each row is a section
of the frame — `camera`, the three `chunk*` rows from `ChunkManager.Update`, `lod`, `entities`,
`fluidWake`, `fluidTick`, `raycast`, `breaking`, plus the `progUpdate` input shell (`GameplayInput.Update`) and the
`player` spawn/animation shell on the update side (right-click use/place work and console
logging (`DevDiagnostics.LogDebugLine`) are split out as `usePlace` and `debugLog`, and the autosave of a store-backed world is
the one `save` row, see "Saves" below), then `lighting`, `shadowNear`/`shadowFar` (the two
cascades: `ShadowPass.RenderShadows` times itself, split at the far cascade's FBO bind because
`TIME_ELAPSED` ranges cannot nest, so their sum is the whole shadow pass; `shadowFar` is
amortized: about 0 on the frames that reuse the cached far map, the full far-box cost on the frames
`FarShadowReuse` re-renders it, and the 3-slot GPU query ring drops slow results, so its GPU figure
leans toward the cheap frames — see PERF_TODO F3), `reflect`,
`lodPass`, `worldPass`, `entityPass`, `aoPass` (contact AO, see below), `waterPass`, `selectPass`, `postPass` (the tonemap resolve, see
"Linear lighting" below), `uiPass` on the render side,
and the `rendShell`/`screenshot`/`publish` frame shell —
with its average and worst millisecond cost over the last second; `other` is the wall time no
section claimed (buffer swap, window pump, OS/GC jitter). The header carries the frame time and FPS, the CPU
sum against the GPU's own `TIME_ELAPSED` measurement of the whole frame (`--` until the first
query comes back), GC collections per second and the loaded/visible chunk counts. CPU timings are
collected every frame; per-pass GPU timing is gated (`GpuTimingGate.ShouldTime`, latched once per
frame in `GpuPassTimer.BeginGpuFrame`) — every frame while the overlay is up, and one frame in
`PostProcessConfig.GpuTimingIdleInterval` while it is down, which keeps enough samples in each
once-a-second window that the snapshot standing when `X` is pressed already carries GPU figures.
The caveat is that a pass which does not run every frame (`selectPass`) can collect no samples at
all across an overlay-down window and read `--` in that first snapshot. The overlay's own glyph
draws land in `uiPass`, so compare that row with `X` on and off before reading it as a baseline.
`Program.OnLoad` and the `RenderManager` constructor each print one startup line — build
configuration, debugger, GC mode and CPU count, then the GL
renderer, vendor and version (which must report a 4.6 core context) — so a measurement always
records which build and which GPU produced it. Timing lives in `OpenGLTest/src/Core/FrameProfiler.cs`;
wrap new work in `FrameProfiler.Begin`/`End` (main thread only) rather than adding another ad-hoc
stopwatch.

Standing in water switches the player to **swimming** (`Camera.IsInWater`, recomputed every frame by `PlayerPhysics.UpdateWaterState` from the real fluid surface heights under the body): movement is a series of **burst strokes** rather than a steady paddle: a stroke (ready at once on entering the water, then every `SwimStrokeSeconds`) *sets* the horizontal speed to `SwimBurstSpeed` along the WASD direction (0.6x backing up), which then glides off under `SwimGlideDrag` until the next one; Space and Ctrl stroke straight up (`SwimBurstUp`) or down (`SwimBurstDown`), the vertical speed gliding toward the gentle `SinkSpeed` through `MathUtils.Damp`, and no input means no stroke (`Camera.SwimStrokeCount` moves on each, which is how the body starts its stroke animation on the same frame; see "Animation juice"; with the eye under water, not in lava, each one also lets out a burst of `SwimStrokeBubble*` bubbles just in front of the head through `BubbleParticleSystem.TrackSwimStrokes`, run by `AmbientEffects.UpdateBubbles` in singleplayer and on a client, never on a server). Swimming into a bank while holding Space adds `SwimExitBoost` so the player can climb out without a ramp. Flight ignores water entirely, and a player standing on the bottom of a shallow puddle still jumps normally. Flowing water also pushes the player and floating item drops along its flow direction (`ChunkManager.GetFluidFlowGlobal`, Minecraft's `FlowingFluid.getFlow`: a horizontal vector summed from how much lower each neighboring cell's surface sits, zero in a still pool). `Camera.IsEyeInWater` is the separate eye-only test and is the source of truth for the underwater tint.

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

**Drowning.** With the eye under water (`PlayerBodyProbe.EyeInFluid`, the one eye rule the physics, the
shoulder camera and the server share; lava never counts) the player's air (`Breath`, owned by `PlayerHealth`,
`PlayerConfig.AirSeconds` 15) drains; out of the water it refills at `AirRefillPerSecond`, and at zero a
`DamageSource.Drowning` blow of `DrownDamagePerTick` lands every `DrownTickSeconds` (`PlayerHealth.UpdateBreath`;
creative and flight are immune, `DeathMessages` says "drowned"). Singleplayer ticks it in
`LocalPlayer.UpdateHealth`; a server ticks each `ServerPlayer` in `TickStatus` (an unloaded body is not judged) and
sends the air in `PlayerStatus` as a `Breath.NetSteps` byte (protocol 9; the status goes out whenever the step
changes), which a client mirrors (`ClientMirror.ApplyNetworkAir`). `HudRenderer.DrawAir` draws ten bubbles
(`assets/textures/ui/bubble.png`, from the GGJ-2025 project) over the hearts, right-aligned, emptying from the left,
shown only below a full lung. `BreathTests`, `visual-examples/drowning.json` (`VOXEL_TIME=147`).

**Regeneration.** Out of combat the player heals (`PlayerHealth.UpdateRegen`): once
`PlayerConfig.RegenDelaySeconds` have passed with no damage taken (`TakeDamage` restarts the clock)
and no blow dealt (`NoteCombat`, called by `MobStrikes.StrikeMob` through `PlayerContext.Health` for
punches and slices; a thrown item does not count yet) it refills at `RegenPerSecond`, scaled by
`RegenSprintFactor` while sprinting (0 would halt it). Creative and flying players, the dead and a
full pool are left alone. Singleplayer ticks it in `LocalPlayer.UpdateHealth`, a server in
`ServerPlayer.TickStatus` (the sprint bit is the client's claim and only slows its own healing),
and the heal reaches a client through the usual `PlayerStatus` health. `RegenTests`.

**Mana (MG-1).** The magic pool is `PlayerHealth.Mana` (`PlayerMana`, GL-free, tuning in
`ManaConfig`): it regenerates at `Max / ManaConfig.RegenFullSeconds` per second
(`PlayerHealth.UpdateMana`), never while dead, and refills on a respawn. `TrySpend(cost, creative)`
is free in creative and refused, changing nothing, when the pool is short. The max starts at
`ManaConfig.StartMax` and grows through `MaxFor`/`SetKnownParts` with the player's known spell parts
(`KnownSpellParts.SizeMana`, called whenever they are learned or restored; MG-8), up to `ManaConfig.MaxCap`. Singleplayer ticks it in
`LocalPlayer.UpdateHealth`, a server in `ServerPlayer.TickStatus`, which marks the status dirty only
when the whole-unit `NetMana` or the `Max` moves (about one status a second while it refills, none
when full). It rides the end of `PlayerStatus` as two ints (protocol 11, the max clamped to
1..`MaxCap` and the mana into it on read) and a client only mirrors it
(`ClientMirror.ApplyNetworkMana`). It is saved as `Mana` in `player.json` (absent reads as full; the
max is not saved, since it follows progression). `HudRenderer.DrawMana` draws it as a thin blue bar
as wide as the hotbar row in the margin under it, hidden while dead or in creative. The cost a cast
spends comes from its spell parts (`SpellRecipe`, see "Spell part schema"). `ManaTests`.

**Status effects (MG-4b).** The player's effects are `PlayerHealth.Effects` (a `StatusEffectSet`, see
"Status effect schema"), ticked by `PlayerHealth.UpdateEffects` in `LocalPlayer.UpdateHealth`
(singleplayer) and `ServerPlayer.TickStatus` (server); a creative, flying or dead player has them
cleared, and a respawn or a restore clears them too. Their damage is a `DamageSource.Magic` blow whose
death line is the effect's own `deathMessage` phrase ("burned to death"), else "was killed by magic". A
server marks the status dirty whenever the set of active effects changes, and the mask rides the end of
`PlayerStatus` (protocol 13), which a client only mirrors (`ClientMirror.ApplyNetworkEffects`; a mirror
never ticks). `HudRenderer.DrawEffects` draws one icon per active effect that has an `icon`, in a row
ending just left of the hearts, hidden while dead. `StatusEffectSetTests`, `StatusEffectApplyTests`.

**Water rendering** lives in the shared world shader plus two extra passes, the planar mirror (`ReflectionPass.RenderReflection`) and the scene copy. Just before `TerrainPasses.RenderWorldTranslucent` draws, `RenderTargets.CopyScene` snapshots the HDR scene target's color and depth (see "Linear lighting" below; the color copy is `Rgba16f`, like the reflection mirror) into two textures on units 3 and 4 (`CopyTexSubImage2D` from the bound HDR FBO — the water pass depth-tests while sampling, so it cannot read the target directly); the water fragment then composes its own look out of them: a refracted lookup offset along the analytic wave normal, Beer-Lambert absorption over the water's measured thickness (`WaterAbsorb`) plus in-scattered `WaterScatter`, and a Schlick fresnel mix into the planar reflection. Where that view-ray depth thins below `WaterFoamWidthBlocks` (a wall, a bank, a thin trickle) a pale **shore foam** band is mixed over the composed surface in linear light, lit by the shared opaque `light` term and fogged with it, broken up by a value-noise field the ripple phases jiggle and `FlowFoam` (the same two-phase flow map) carries downstream; top faces only, never from below or with the eye submerged, faded out by 160 blocks. The width must stay under a full cell's surface less the swell (`WaterFoamTests`), so open water over a block floor never foams; being screen-space, only contacts the depth copy sees foam (the camera-facing side of a block; a wall seen straight down shows none). `visual-examples/water-foam.json` frames liquid shores at `VOXEL_TIME` 360 and 147. Flowing water carries its baked flow into that normal (`FlowWaveNormal`: a bounded two-phase flow map that scrolls the ripples downstream and stretches them along the flow, identical to `WaveNormal` when the flow is zero), and falling side faces add fast vertical streaks; generated rivers and lakes are all source cells, so they bake no flow and stay still until the simulation makes them flow (`visual-examples/flow-water.json`, `VOXEL_TIME=147`, pours some). The mirror plane itself is the single `RenderManager.WaterPlaneY` at sea level, so a lake whose surface stands above it reflects against that plane rather than its own - an accepted approximation, since only one plane can be mirrored per frame. Seen from below — decided per fragment from the face normal (`fromBehind` in `test-fragment.glsl`), never from the sea plane, since a pond stands above it — the surface is a Snell window: the same scene copy shows through it distorted by the wave normal, the inside fresnel goes to total internal reflection past the critical angle and that band fades to the underwater fog color, and the mirror is never sampled from that side; `ReflectionReuse.MirrorApplies` also skips rendering it whenever `Camera.IsEyeInWater`, and the sky dome (`SkyRenderer`) and the sun disc stay dry in water (lava still fades the dome via `FrameLighting.SkyTintAmount`, eased through its `_lavaT`) so the window shows real sky. World, cloud and sky fog share `SkySample`, duplicated verbatim in `sky-fragment.glsl`, `test-fragment.glsl` and `cloud-fragment.glsl`, with `SkyPalette.Sample` as the tested C# reference, so change all four together. The dome currently samples the flat 28198cc sky color: `SkyPalette.Compute` returns the old night/day mix, pulled toward the dusk orange by `SkyDuskMix`, as both zenith and horizon with zero glow, and `SkyHazeStrength` is 0, so every direction (and every fully fogged fragment) is that one color. The reflection pass now also draws the sun disc and the cloud layer into the mirror, and both it and the scene copies are skipped whenever `TerrainPasses.CullWater` (one frustum pass a frame, feeding the water pass its draw lists) found no water on screen — glass shares the translucent mesh, so `Chunk.HasWaterMesh` is the flag that decides, not `HasTranslucentMesh`. The mirror's opaque chunk and LOD lists skip anything whose top (`Chunk.WorldMax.Y`, `LodRegion.YMax`) plus `ReflectionCull.TopMarginBlocks` (a multiblock's overhang, two blocks for the 3x3x3 furnace, plus the shader's sway and wave lift) lies below the plane, since its clip would discard every fragment. Opaque faces that look into water are flagged by the mesher in bit 3 of `Vertex.Normal` (bits 0-2 stay the face index, bit 5 is the leaf-sway flag, bits 6-7 the low bits of the block-light tint code) and get the animated Voronoi caustic net; bit 4 is the biome-tint flag (see "Biome-tinted grass and leaves" below). The world fragment darkens a submerged face's albedo by `WetDarken` once, before the light multiply, and only at or below that face's own baked surface (`step(fWorldPos.y, fWaterSurfaceY)`; the caustics stop at the same line), so a dry face beside water — a beach top, a bank wall over the waterline — is never darkened or tinted; ore tiles (a non-zero `material.metallic`) are excluded. `SurfaceFresnel` is the GL-free twin (`Schlick`, `WetWeight`). While the eye is submerged, `UnderwaterRaysRenderer` adds one full-screen ray-march for sun shafts, composited in display space (the upsample reads the HDR target it draws into, adds the shafts to the encoded scene with the pre-SH-10 sRGB sun color and strength, clips at 1 and decodes), since the same add in linear light lost the glare toward the sun. Both measure depth from the **local** water surface, never the sea plane: the caustics from the surface baked into each submerged face's vertex (`Vertex.WaterSurface`, read as `fWaterSurfaceY`), the rays from `Camera.EyeWaterSurfaceY` (`ChunkManager.GetFluidColumnTopGlobal` over the eye's column), so a pond above sea level gets both — only the planar mirror still uses the sea plane. The rays are why the shadow map keeps rendering underwater (`FrameLighting.ShadowStrengthDry`, the strength before the underwater fade) even though the world diffuse no longer uses it. **Depth convention**: `Camera.GetProjectionMatrix` comes from `System.Numerics`, which is Direct3D-style (clip depth 0..1), so window depth is *not* the GL -1..1 mapping — never write a near/far linearization. Reconstruct a world position by inverting the very matrix that wrote the depth (`uInvViewProj`), and compare raw depth values directly for "which is nearer". `visual-examples/water.json` (run it with `VOXEL_TIME=360`) frames the shallows, the grazing surface, deep water from height and the underwater shots, including the surface seen from below in the sea and in a pond above sea level (a liquid one — a frozen pond has an ice lid, not a water top face).

**Deep-water darkness.** The underwater fog darkens and loses red the deeper the eye is below its
own water surface: `RenderManager.BeginFrame` reads the depth as `Camera.EyeWaterSurfaceY` minus the
eye (`RenderEye`) while the eye is in water and not lava, and `ApplyFrameUniformsTo` uploads
`DeepWaterFog.Dim(depth)` (eased in with the underwater tint, zero in lava) as `uDeepWaterDim`. The
world fragment multiplies the wet fog color by `1 - uDeepWaterDim` in the fog mix, and the Snell
window's total-internal-reflection fallback (which stands in for the water body below) the same way.
The curve is `DeepWaterFogMaxDim * (1 - exp(-loss * depth / DeepWaterFogScaleBlocks))` per channel
with `DeepWaterFogLoss` ordered red first, so it is zero at the surface, never decreasing, takes red
first and never goes pitch black (`DeepWaterFogTests`). `visual-examples/deep-water.json`
(`VOXEL_TIME=147`) frames the warm sea at 1.5, 5.5 and 9 blocks down and a frozen deep basin at 3 and
26.

**Sun glitter.** On top of the water's smooth sun highlight (`waterSpec`) the fancy water path adds
hashed sparkles along the sun's reflection path (`WaterGlitterAt`): the water plane is cut into
`WaterGlitterCellsPerBlock` cells per block, a `WaterGlitterDensity` share of which carry a tiny facet
tilted up to `WaterGlitterTiltRadians` off the wave normal (`Pcg3d`, like the snow glint), and a facet
mirrors the sun through a narrow lobe on the eye/sun half vector, so the sparkles gather where the
waves already face it. The light is a small round point at a hashed spot in its cell, each cell
re-rolls its facet `WaterGlitterRate` times a second on its own phase and fades in and out within each
roll, so they dance without popping; added as `WaterGlitterStrength` times the direct sun, gated by
the shadow and skylight, a risen sun and a fade between `WaterGlitterFadeNearBlocks` and
`WaterGlitterFadeFarBlocks`, top faces only, never from below (`fromBehind`) nor with the eye
underwater. `WaterGlitter` is the GL-free twin (`Sparkle`, `Life`, `Spot`, `Gate`;
`WaterGlitterTests`); `uWaterGlitter` 0 switches it off. `visual-examples/water-glitter.json`
(`VOXEL_TIME=360`) frames the reflection path over the warm sea twice 0.3 s apart, away from the sun
and from below.

**Linear lighting (SH-10).** The world is lit in linear light and rendered into the HDR scene
target `RenderTargets` owns — an `RGBA16F` color texture plus a sampleable `DepthComponent24`
depth texture (`RenderTargets.CreateHdrTarget`, which also builds `HdrColorOnlyFbo` over the color
alone for the contact-AO pass; recreated with the viewport like the reflection, scene-copy and
rays targets). `RenderManager.BeginFrame` binds and clears it, and **every mid-frame restore goes
back to it, never to framebuffer 0** (`RenderTargets.BindSceneTarget` at the tails of
`ShadowPass.RenderShadows` and `ReflectionPass.RenderReflection`, the `Create*Target` recreate
paths, the `UnderwaterRaysRenderer` composite); only `CreateHdrTarget`/`DeleteHdrTarget`,
`ScreenPasses.RenderPostProcess` and the world-less `UiPasses.RenderMenuFrame` bind 0. Color textures sampled
before the tonemap are uploaded as `SRGB8_ALPHA8` (the `srgb` flag on `TextureLoader` and
`TextureAtlas.Build`: the block atlas, `ItemMeshCache`, skins, sun, break progress, bubbles, the
world healthbar hearts) so the sampler decodes them; the material/normal atlas is data and stays
plain `Rgba`, and so do UI-only textures (font, the crosshair and HUD hearts `HudRenderer` loads, `UIRenderer`'s own item icons).
CPU colors are authored in sRGB and go through `ColorSpace.SrgbToLinear` where they are computed
or uploaded (the sun, ambient and sky palette in `FrameLighting.SetLighting`, torch, point lights,
water scatter, molten tints, selection outline); the selection *face* highlight is the one overlay
mixed in display space instead (`selection-fragment.glsl`'s `uDisplayMix`: it reads the HDR target
it draws into after a `glTextureBarrier`, blend off), since a linear 15% blend turned any dark
targeted face into a flat gray, and the cloud layer does the same (`cloud-fragment.glsl` lights,
fogs and mixes in display space over the target it reads through `RenderManager.SceneReadUnit`,
the HDR target or the mirror; the depth pre-pass lets one surface per pixel through), since a
linear blend left the clouds opaque, cream slabs; the block-light colors are the exception that is
uploaded pre-linearized: `BlockLightPalette.Linear` goes up once per frame as the
`uBlockLightPalette[8]` array, `test-vertex.glsl` decodes each chunk vertex's tint code into the
smooth `fBlockTint` (mirroring `LightTint.Decode`), the plain (entity) path reads
`uBlockLightPalette[uEntityTint]`, and the fragment's torch term is `fBlockTint * pow(...)` in
place of the old single `uTorchColor`; a GLSL constant that stands for one is written as its
decoded value with the sRGB original in a comment. At the end of `WorldRenderer.Render` the `postPass`
section calls `ScreenPasses.RenderPostProcess`, which binds framebuffer 0 and runs
`PostProcessRenderer` (`post-fragment.glsl`): exposure (`LightingConfig.TonemapExposure`),
a per-channel clamp to 0..1 (what the old 8-bit target did, so snow and white sand clip to white
rather than staying cream; `Tonemap.cs` is its tested C# twin), the exact sRGB encode, and the frame's one
half-LSB dither. FXAA (SH-9) runs unconditionally inside the same pass, on display-space
(tonemapped, encoded) taps filtered by hand from `texelFetch`, before the dither (knobs
`PostProcessConfig.Fxaa*`; its contrast early-out keeps it near 1 ms at 1080p). The UI draws
after it, straight into the gamma default framebuffer, so text and icons stay crisp and HUD
colors stay display-space; the two textures it shares with the world (the atlas and the item
meshes) are re-encoded in `ui-fragment.glsl` behind `uSrgbTex` (`UIRenderer`'s `atlasIsSrgb`
constructor flag, `ItemMeshCache.IsSrgb`), so icons write the same bytes as before.
`visual-examples/linear-lighting.json` (run at `VOXEL_TIME` 147, 440 and 700) frames the surface,
the Snell window, the lava cave with and without the HUD and a high LOD/fog vista for comparing a
lighting change against earlier shots.

**World fog** is the squared linear ramp `f*f` over `FogNear..FogFar` toward `SkySample` in the
view direction, reaching exactly the sky at FogFar so the LOD edge stays seamless. In dry air it is
mixed in **display space** (`test-fragment.glsl` encodes the fragment, clamped to 1, and the fog
color, mixes, and decodes), because the same mix in linear light reads visibly hazier than the
pre-SH-10 look; underwater and in lava it stays the linear mix toward `uFogColor`, and the two are
blended by `uFogWet` rather than branched so the transition never pops. Cloud fog is display
space too (see the cloud layer above); particle and healthbar fogs are still linear.

**Stars.** The night dome adds a star field in `sky-fragment.glsl`'s `main` only, after
`SkySample`, so the fog and `SkySample`'s three twins never see it. Stars live on a grid over the
sphere of directions (cube face, equi-angular warp, `StarCellsPerFace`² cells, each cell's chance
scaled by its solid angle), one star per cell kept `StarField.CellMargin` clear of the edges, drawn
as a pixel-sized gaussian (`fwidth` of the view ray), so they never swim as the view turns. They turn with the sky
instead: the grid is read in star space, the world direction turned by -`DayCycle.OrbitAngle` about +Z (`uStarTurn`,
twin `StarField.ToStarSpace`), the sun's own orbit run backward, so the sun and the moon opposite it keep their
place among the stars while the whole field rises and sets once a day. Each
star twinkles smoothly on its own rate and phase hashed from its cell (`StarField.Twinkle`, the
`uStarTime` clock wrapped hourly). Visibility fades over dusk
and dawn by the sun's elevation (`StarField.Visibility`), out into the horizon haze and around the
sun. Clouds hide them: the dome samples the cloud-shadow map (unit 8) cell by cell along each view
ray (`CloudCoverAlongRay`), since the drawn clouds alone cover at their opacity. The mirror pass
redraws the dome, so the stars reflect, and the Snell window shows them. GL-free twins:
`StarField` (cells, hash, star placement) and `CloudCover` (the cover along a ray); keep them in step
with the shader (`StarFieldTests`, `CloudCoverTests`). `visual-examples/night-sky.json` (`VOXEL_TIME`
700, 450, 147, and 350 for the glare) frames them.

**Shooting stars.** A rare streak across the night dome, added in the dome `main` right after the stars
(`ShootingStar` in `sky-fragment.glsl`, so the fog never sees it; the same fade, sun and moon masks and
`CloudCoverAlongRay` as the stars). Time is cut into `ShootingStarSlotSeconds` slots of the star clock (the
slot length divides its hourly wrap); a slot holds a streak with probability `ShootingStarChance`, hashed from
its index, which starts at a hashed moment inside the slot and lives `ShootingStarLifeSeconds`, so a pixel
only ever reads one slot, and the chance and life-window tests come before any trigonometry. The head slides
`ShootingStarArcDegrees` along a great circle through a hashed point of the upper sky (`ShootingStarMin/MaxSinElevation`)
in a hashed heading, a tail of `ShootingStarTrailDegrees` fading quadratically behind it, a pixel-relative
gaussian across, eased in and out over its life. It stays fixed in world directions. GL-free twin
`ShootingStar` (`ShootingStarTests`); `visual-examples/shooting-stars.json` (`VOXEL_TIME=600`).

**Sun glare.** A wide, faint glow around the sun, added in the same dome `main` (`uGlareColor` times
`cos(angle to the sun)^SunGlareExponent`, `SunGlare.Radial`). Its strength is computed once a frame
on the CPU (`SunGlare.Intensity`, capped at `SunGlareStrength`): how directly the camera faces the
sun, the sun's height (zero at and below the horizon) and the cloud over its disc
(`CloudCover.AcrossDisc` over `CloudShadowTexture.Coverage`, eased). It is a camera effect: zero
underwater and in lava, and the mirror pass draws its dome with `GlareColor` zero, so no reflection
ever carries it.

**Sunset glow.** The one direction-dependent part of the otherwise flat dome (this supersedes the
"zero glow" above): `SkyPalette.Compute`'s `glowAmount` scales a warm sRGB band
(`SkyDuskGlowStrength`) that every `SkySample` copy already adds low toward the sun's azimuth
(`Glow * lobe`). `SkyPalette.GlowAmount(sunElevation)` gives it a value only while the sun crosses
the horizon, at sunset and sunrise alike: zero at and above `SkyGlowAboveSunElevation` (the flat day
sky) and at and below `SkyGlowBelowSunElevation` (the afterglow gone), peaking at
`SkyGlowPeakSunElevation`. The world and cloud fog sample the same band, so fogged terrain toward
the sun melts into it. `SkyPaletteTests` holds the rules; `visual-examples/sunset-glow.json`
(`VOXEL_TIME` 430, 147, 1030) frames it.

**Cloud silver lining.** Thin cloud edges near the sun light up: `cloud-fragment.glsl` adds the
display-space sun term times `uSilverStrength` times the forward lobe `cos(view, sun)^CloudSilverLiningExponent`
times how thin the cloud is behind the lit surface along the view ray (`CloudThinness`: the share of
`CloudSilverLining.Samples` points `CloudSilverLiningStepBlocks` apart that are out of the cloud
volume, read cell by cell from the cloud-shadow coverage map on unit 8 like the stars' cloud cover,
so `CloudRenderer` now takes that unit). The frame's strength is `CloudSilverLining.Strength` of the
sun's elevation (zero at and below the horizon), set on `CloudRenderer.SilverLining` by
`FrameLighting.SetLighting`; the lobe and thinness are computed only where the lobe is non-zero.
GL-free twin `CloudSilverLining` (`CloudSilverLiningTests`); `visual-examples/cloud-silver-lining.json`
(`VOXEL_TIME` 343, 400, 147) frames it.

**Moon (V-6).** `SkyPasses.RenderMoon`, called by `SkyPasses.RenderSky` right after `RenderSun` (so the
mirror draws it too and the Snell window shows it through the scene copy), draws the sun's quad and
`sun-vertex.glsl` again opposite the sun (`-FrameLighting.SkySunDir`), additively and depth-free,
with the full-disc sprite `assets/textures/environment/moon.png` and `moon-fragment.glsl`, which
shades the phase over it: each disc point gets a sphere normal and is lit where it faces
`MoonPhase.LightDirection`, softened over `MoonTerminatorSoftness`, the unlit part keeping
`MoonEarthshine`; a faint halo grows with the lit share. `MoonPhase` (GL-free) picks the phase:
`DayIndex` counts whole days from the first noon (`DayCycle.NoonTime`), so it steps at noon while
the moon is below the horizon, day 0 (a new world's first night) is full and the cycle is
`MoonCycleDays` long. It fades in and out with the stars (`MoonPhase.Visibility` =
`StarField.Visibility`) and out in lava, and the dome hides the stars behind its disc (`uMoonMask`,
`SkyDomeFrame.MoonVisibility`). `MoonPhaseTests` (the phase never changes in view, the shaded share
matches `IlluminatedFraction`, waxing lit on the right); `visual-examples/moon.json` (`VOXEL_TIME`
700 full, 3100 last quarter, 4300 crescent, 5500 new, 7900 first quarter) frames it.

**Moonlight.** At night the moon is the world's **key light**: `FrameLighting.SunDir` (what
`uSunDir`, the shadow cascades, the clouds, the leaves and the rays read) is `Moonlight.Direction`,
the sun by day and the moon (the sun negated) once the sun is at or below
`MoonlightSwitchSunElevation`, while the sky (the dome, the sun and moon discs, the stars' turn, the
glare) keeps the true sun in `SkySunDir`, and the sun disc its own `SunDiscColor`. So the world
shader's `uSunColor`/`uSunIntensity` are the moonlight's at night: `Moonlight.ColorSrgb` (cool) at
`Moonlight.Intensity` (`MoonlightIntensity`, eased in by `Moonlight.Rise` as the moon climbs and
scaled by the phase, `MoonlightNewMoonShare` at a new moon), and the shadow strength follows the key
light's own elevation, so a night keeps soft shadows (the shadow passes now run at night too). The
handover never pops: the sun's light eases out below the horizon (`Moonlight.SunFade`, which also
removes the old night floor that lit the world from under the ground) and the moon's eases in from
the switch, both zero there. The switch sits at or below `SkyGlowBelowSunElevation`, because the
world and cloud `SkySample` copies read `uSunDir`: with no glow left, the key light's direction
changes nothing in the fog. The night ambient is a touch bluer. `MoonlightTests`;
`visual-examples/moonlight.json` (`VOXEL_TIME` 455 the handover, 950 a low moon, 700, 5450 a new moon).

**Aurora.** Slow green and violet curtains in the night dome, seen only from cold lands:
`AuroraCurtains` in `sky-fragment.glsl`'s `main`, after the stars (so the fog and the `SkySample`
twins never see it), hidden behind the clouds by the same `CloudCoverAlongRay`. Broad sectors along
the horizon come from a periodic value noise of the view azimuth, with fine vertical folds, a wavy
bright lower edge and a glow fading up from green to violet; each noise's drift is wrapped into its
period on the CPU (`Aurora.Drift`, `Aurora.Periods`), so it never jumps. The strength
(`SkyDomeFrame.Aurora`, `Aurora.Visibility`) is the eye's coldness, eased (`AuroraColdEaseRate`) and
ramped over `AuroraColdStart..AuroraColdFull`, times `StarField.Visibility`. The coldness comes from
`EyeClimate.TryAt` over `RenderManager.LightSource.ColumnSource` (the stored corner climate of the
eye's column, bilinear like the biome tint, read back through `EyeClimate.Coldness`), never from
worldgen noise, so a client reads its server's climate; an unknown column eases it out. The mirror
redraws the dome, so it reflects, and the Snell window shows it. `AuroraTests`;
`visual-examples/aurora.json` (`VOXEL_TIME` 700) films it over a frozen plain and back at the
temperate origin, where it has faded out.

**Eye adaptation.** The post pass's exposure (`uExposure`, now set per frame by
`PostProcessRenderer.Render(viewport, exposure)`) is `TonemapExposure` times `FrameLighting.EyeExposure`,
which its `SetLighting` eases each frame (`EyeAdaptation.Step`: `EyeAdaptToDarkRate` while it rises, the
faster `EyeAdaptToLightRate` while it falls) toward `EyeAdaptation.Target` of the voxel light at the
eye's cell (`EntityLight.Sample(LightSource, camera.Position)`, sky and block light, no GPU readback):
exactly 1 once the brighter of the two reaches `EyeAdaptFullLight` (open sky at any hour, a lamp-lit
room), up to `EyeAdaptMaxExposure` in total darkness. An unknown cell reads as open sky.
`EyeAdaptationTests`; `visual-examples/eye-adaptation.json` (`VOXEL_TIME` 147) films a dark tunnel on
arrival and adapted, then the over-bright step back outside settling within a couple of seconds.

**Realm layer fog.** The eye's height colors the sky around it (plans/LAYERS.md): below the barrier
band, in hell, `FrameLighting.SetLighting` eases `_hellT` (`LayerClimate.HellTarget`: exactly 0 at and
above hell's top, ramped to 1 over `SkyConfig.HellFogRampBlocks` below it; `LayerClimate.Ease` at
`LayerFogEaseRate`) and above the solid cloud slab `_altitudeT` (`AltitudeTarget`, full at the sky
islands). `LayerClimate.Apply` shifts the sRGB `SkyPalette` before it is decoded: the altitude multiplies
it by `SkyAltitudeTint` (a slightly deeper, brighter day blue; night stays dark), hell mixes zenith and
horizon toward `HellFogColor` and drops the glow. Every `SkySample` copy reads the shifted palette
through its usual uniforms, so the dome, the dry world fog, the cloud fog and the clear color all turn
red with no shader change. Hell also lerps the dry fog toward `HellFogNearBlocks..FarBlocks` and fades
the sun, moon, stars, aurora, glare and godrays out (`1 - _hellT`); water and lava still override it.
`LayerClimate.HellLayerIndex` counts the hell layers down from the barrier. `LayerClimateTests`;
`visual-examples/layers-fog.json` (`VOXEL_TIME=147`).

**Biome color grade.** The post pass multiplies the finished, anti-aliased display image by
`uGrade` (`PostEffects.Grade`, (1, 1, 1) by default): `BiomeGrade.Tint` of the eye's climate (the same
`EyeClimate.TryAt` sample the aurora reads), warm where it is warm and dry (the desert's own
weighting) and cool where it is cold enough for snow (`BiomeGradeColdStart..ColdFull`), each channel
moving at most `BiomeGradeStrength`; exactly neutral across the temperate band. `FrameLighting.Grade`
eases toward it (`BiomeGradeEaseRate`) and back to neutral over an unknown column, so a client
without its column yet never guesses. `BiomeGradeTests`; `visual-examples/biome-grade.json`
(`VOXEL_TIME` 147) compares the noon sky over the origin, a deep desert and a frozen plain.

**Damage chromatic fringe.** For a moment after a hit the screen edges split red and blue:
post-fragment.glsl reads red and blue from either side of each pixel along the line from the screen
center (`DisplayBilinear`, after FXAA, before the grade), by `uDamageFringe` times
`DamageFringe.EdgeWeight` (nothing in the middle, full at the corners) times `DamageFringeScreenShare`
of the screen height. The strength is `DamageFringe.Strength` of the vignette's own damage flash
(`PlayerHealth.DamageFlash`, which `WorldRenderer.Render` now hands to `ScreenPasses.RenderPostProcess`), so
it lasts only the first `DamageFringeSeconds` of the flash; a client mirrors the flash with its health.
`DamageFringeTests`; `visual-examples/damage-fringe.json` (`VOXEL_TIME` 147).

**Underwater wobble.** With the eye under water (`PostEffects.Underwater`, the eased underwater amount
the world shader's `uUnderwater` also carries, 0 in lava) the post pass moves the point each pixel
shows by `UnderwaterWobble.Offset` (two slow waves across the screen, up to
`UnderwaterWobbleScreenShare` of its height) and hands that point to FXAA, so every tap reads around
the swayed coordinate; a no-edge pixel then returns its bilinear color at the point rather than the
nearest texel, so the sway never steps a whole pixel. The clock is wrapped on the CPU at the waves'
common period (`UnderwaterWobble.WrapTime`), so it never jumps. `UnderwaterWobbleTests`;
`visual-examples/underwater-wobble.json` (`VOXEL_TIME` 147).

**Godrays.** Light shafts through trees, clouds and gaps in dry air when the sun is in view:
`ScreenPasses.RenderGodrays` (`GodraysRenderer`), its own `godPass` profiler row right after
`raysPass`, switched off by `GodraysEnabled`. Two draws. The march (`godrays-fragment.glsl`) is the
underwater shafts' march run over air into their half-resolution target (borrowed: the two never
need it at once): each pixel's ray up to `GodraysMaxDistance` (inside the near cascade) in
`GodraysSteps` jittered samples, summing the sunlit blocks that pass the near shadow map and the
cloud shadow up the sun ray (`GodraysCloudShadow`, unit 8), plus `GodraysSkyBlocks` for a ray that
reaches open sky (depth at the far plane: nothing drawn there, no terrain, tree or cloud), turned
into light by the saturating `Godrays.Scatter` and the forward `Godrays.Phase`. It reads the HDR
scene's own depth texture (no scene copy). The composite (`godrays-composite-fragment.glsl`)
averages that along the line from each pixel toward the sun's place on screen (`GodraysBlur*`), so
the contrast between long sunlit rays and short or shadowed ones streaks outward from the sun as
beams, and adds it in display space through the color-only HDR framebuffer like the underwater
shafts. The frame's strength is `Godrays.Intensity` (set by `FrameLighting.SetLighting`): facing the sun
within `GodraysFacingDegrees`, strongest for a sun near `GodraysPeakElevation` and `GodraysNoonShare` of
that overhead, zero at and below the horizon, dimmed by the eased cloud over the sun's disc and
zero under water (where the underwater shafts take over); the pass is also skipped while the
shadow passes do not run. Cost on the dev GPU at 1080p: about 0.35 ms GPU (under 0.8 ms worst)
and 0.02-0.05 ms CPU while active, nothing otherwise. `GodraysTests`;
`visual-examples/godrays.json` (`VOXEL_TIME` 395, 425, 147, with `VOXEL_DEBUG=1` for the cost).

**Wind (SH-27).** `Wind.cs` is the one wind: a GL-free pure function of the lighting clock whose
heading turns slowly (`SkyConfig.Wind*`), with `Wind.Offset` its bounded closed-form
integral. Leaf sway reads it in both vertex shaders (`uWind`, the direction scaled by `WindLean`,
leans the canopy downwind; `uWindOffset` advects the gust pattern, so the gust phase rate is the
same anywhere in the world — keep the two sway blocks byte-identical), fire smoke and embers drift
along it (`FireParticle.Update`'s `wind`), falling leaves (ambient and burst) ease their sideways
motion into it, pushed by the canopy's own gusts (`LeafParticleSystem.Update`'s `windTime`,
`Wind.Gust`, the C# twin of the sway's `0.6 + 0.4 sin(phase)`; a landed leaf stays put;
`visual-examples/leaf-wind.json` at `VOXEL_TIME` 147 and 1347), and the cloud layer and its shadows are carried by it
(`CloudCopies.Drift`, wrapped modulo the field on both axes; `uCloudDrift` is a `vec2`). `World`
gets the same lighting clock through `World.TimeOffset`, which `Program` sets from `VOXEL_TIME`, so
leaves, smoke and clouds agree; `FrameLighting.WindDirection` exposes the unit direction for future
rain and snow.

**Contact AO (SH-28).** A cheap screen-space contact AO darkens the crevices the baked vertex AO
cannot see — where pebbles, branch piles and other per-quad emitters meet the ground, and inner
corners between greedy quads. `ScreenPasses.RenderContactAo` (`ContactAoRenderer`,
`contact-ao-fragment.glsl`, GL-free twin `ContactAo`) runs mid entity pass as its own `aoPass`:
after everything that writes honest depth (terrain, drops, stations, TNT, falling blocks, slimes, goblins)
and before the depth-less or blended draws (healthbars, the first-person head, debris, toppling
trees, player, held item), the selection outline, water (whose refraction copy then carries the AO),
clouds and particles. It samples the HDR depth texture through the color-only
`RenderTargets.HdrColorOnlyFbo` (no feedback loop) with multiplicative blending (`Zero, SrcColor`). Four taps
(plus the four 1-pixel neighbors) per pixel, `ContactAoRadiusBlocks` wide in the world (clamped to `ContactAoMaxPx`): for each opposite
pair, the midpoint of their raw depths is reconstructed on the center ray and compared with the
center. Raw depth is affine in screen space over a plane, so planes never darken at any angle;
a concave corner creases positive, convex edges and far silhouettes are rejected by the
`ContactAoMinBlocks..RangeBlocks` window, and it fades out by `ContactAoFadeFar`. A thin object
standing in front of the ground (a torch, a drop, a mob) would crease the ground a tap radius beside
it exactly like a corner and leave displaced copies of its silhouette there, so each pair's crease is
split between its two taps along the center surface's own slope (read from the four 1-pixel
neighbors; of the two one-sided slopes the one that carries the surface out to its own tap wins, the
stronger crease on a tie), and a tap in front of that surface only counts while what it sees lies
within `ContactAoReachNear..ReachFar` tap radii of the center point (`ContactAo.Reach`). A wall or
pebble beside the point is that close, the upper part of a post is not; with every occluder in reach
the crease is exactly the plain midpoint crease. The cost is that a corner seen at a grazing angle
keeps a band about that reach wide in the world rather than a tap radius wide on screen.
`ContactAoEnabled = false` skips the pass entirely.

**Selection outline pulse.** The targeted block's outline breathes between
`SelectionPulseMinOpacity` (70 %) and full opacity, one breath every `SelectionPulsePeriodSeconds`:
`selection-fragment.glsl` scales the outline's `uOpacity` by `1 - uPulseDepth * (0.5 + 0.5 sin)` of
`uTime`, and `SelectionPulse` is its GL-free twin (`Depth(mining)` is what
`SelectionPasses.RenderBlockSelection` uploads, `Opacity` the curve). While the block is being mined
(`LeftClickAction.IsBreaking`, passed as `RenderBlockSelection`'s `mining`) the depth is 0, so the
outline holds steady at full and the crack overlay stays readable; the face highlight (`uDisplayMix`
1) never pulses. The outline beams draw with back faces culled, so each pixel blends exactly one
face of a beam: with both faces blended an alpha `a` read as `1-(1-a)^2` and the breath all but
vanished. `SelectionPulseTests`, `visual-examples/selection-pulse.json` (`VOXEL_TIME=147`).

**Leaf backlight.** A leaf-sway face (bit 5: canopy leaves and rooted plants; `test-vertex.glsl`
hands the fragment the flag as the flat `fSwayFlag`, outside the guarded sway block) standing
between the eye and the sun glows warm, as if lit through: the world fragment adds
`albedo * LeafBacklightTint * sun` scaled by `LeafBacklightStrength` and a
`pow(dot(viewRay, sunDir), LeafBacklightPower)` lobe, gated by skylight, the cloud shadow, a risen
sun and dry eyes. Whether sunlight reaches the leaf from behind is the shadow map read
`LeafBacklightReachBlocks` toward the sun from it with the sun as the normal (the faces the eye sees
from the dark side read as self-shadowed at their own position), so a crown's thin, sunlit rim
lights up while leaves deep in its own shadow stay dark; the shadow pass is untouched.
`LeafBacklight.Weight` is the GL-free twin (`LeafBacklightTests`); `uLeafBacklight` 0 (the
ItemDebugger) switches it off. `visual-examples/leaf-backlight.json` (`VOXEL_TIME=1100`, a morning
sun behind an oak crown).

**Grass wind shimmer.** Soft bright bands lie across the wind on the grass tops and roll downwind
with the leaf sway's gusts: on a biome-tinted up face (bit 4, face 4; never a swaying face nor the
far LOD's crowns) the world fragment reads a crest pattern along `uWind` (normalized) at the
position less `uWindOffset` — the very offset the sway samples its gust wave at, which the fragment
stage now declares too — weights it by the sway's own gust (`0.6 + 0.4 sin(...)`) and brightens the
grass by `GrassShimmerStrength` of its own light, so the bands darken with shade and night. Bands
are `GrassShimmerWavelengthBlocks` apart and fade out between `GrassShimmerFadeNearBlocks` and
`GrassShimmerFadeFarBlocks`. `GrassShimmer` is the GL-free twin (its gust is `Wind.Gust`;
`GrassShimmerTests`: banded, carried exactly by the gust offset, faded, grass tops only);
`uGrassShimmer` 0 switches it off. `visual-examples/grass-shimmer.json` (`VOXEL_TIME=147`, three
shots of one meadow 0.7 s apart).

**Lava glow pulse.** Lava is the fluid path's `isLava` (a water-flagged quad on the lava tile,
`uLavaLayer`), whose glow is worked out in display space and clipped there. After that clip the
glow is multiplied by `1 + LavaPulseStrength * (2 * field - 1)`, where the field (`LavaPulseField`)
is two octaves of the shader's `ValueNoise` drifting slowly against each other, so broad patches of
the surface brighten and dim over seconds on top of the existing fast sine pulse; applied in linear
light, the dim phase reaches the clipped molten texels too. `LavaPulse` is the GL-free twin (its
`Hash12`/`ValueNoise` are float ports of the shader's; `LavaPulseTests`: bounded, identity at zero
strength, smooth, slow); `uLavaPulse` 0 (the ItemDebugger) switches it off. `lava.json` ends with
two shots of the pool 2.5 s apart (`pulse_a`, `pulse_b`).

**Glass sheen.** Glass blocks and panes share the glass tile (`uGlassLayer`) and draw blended in the
translucent pass; at a graze they mirror the sky. The weight is `GlassSheenStrength` times the
Schlick fresnel of `|V.N|` (a face seen from inside the cube reads the same), the sky is `SkySample`
of the reflected ray blended to `uFogColor` by `uFogWet` and dimmed by skylight. Tinting the color
alone would vanish under glass's low tile alpha, so the sky is folded into the blend: the fragment
writes alpha `a + F - aF` (`glassAlpha`, which replaces `texAlpha` at the end) and color
`(c a (1 - F) + sky F) / alpha`, which straight-alpha blended over any background is exactly the
plain glass composite mixed toward the sky by F. `GlassSheen` is the GL-free twin (`GlassSheenTests`
proves that identity); `uGlassSheen` 0 (the ItemDebugger) switches it off.
`visual-examples/glass-sheen.json` (`VOXEL_TIME=147`) places glass and panes on the frozen plain and
frames them head-on, grazing and from above.

Shaders (`assets/shaders/**/*.glsl`), textures (`assets/textures/**/*.png`), fonts (`assets/fonts/**/*.ttf`) and all content JSON (`assets/**/*.json` — blocks, items, loot tables, models) are copied to the output directory via `CopyToOutputDirectory` rules in the csproj — **new asset types/extensions need a matching `<None Update>` entry** or they won't be found at runtime. The JSON rule is a wildcard over the whole `assets` tree, so adding a block, item, loot table or model needs no csproj edit.

Assets are opened by **relative** path (`FileUtils.ReadFile("assets/shaders/...")`, `TextureAtlas.Build(gl, "assets/textures/blocks")` in `RenderManager.LoadResources`), so the app must be launched with a working directory that contains `assets/` — either the build output directory or `OpenGLTest/`. `dotnet run --project OpenGLTest/VoxelEngine.csproj` satisfies this (it runs from the project folder); launching the built executable from an arbitrary directory does not.

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
gets it from `WaterSurfaceCodes.SubmergedSurfaceCode`, which walks the fluid column in the padded snapshot and
resolves a column that leaves the snapshot from the build's resolved `ColumnTerrain.WaterLevel`
(`MeshScratch.Columns`, worldgen or streamed, see "Chunk meshing"); it rides the greedy key
as a 7-bit code in bits 25-31 (bits 16-23 are unused), so a merge never crosses two water surfaces. On a **fluid** quad
(alpha 254) the same ushort means something else: the per-corner flow code
(`Vertex.EncodeWaterFlow` of `FluidFlow.Corner`, signed 7-bit X and Z plus a falling bit, 0 =
still), one per vertex so neighboring cells agree at shared corners. That is safe only because
the shader reads the surface meaning exclusively on non-water submerged faces. Baked model
quads (`BakedQuad.Uvs` + `.Tile`) and every procedural emitter go through `ChunkFaceTables.TiledUv(face,
local, extent)`, the single definition of per-face texture orientation; `CropUv` is its
extent-1 case. Bind these textures with `TextureTarget.Texture2DArray`.
The block and material atlases sample their mip chains (`TextureLoader.UploadArray(...,
sampleMips: true)`: min filter `NearestMipmapLinear` plus `AtlasAnisotropy`, mag stays `Nearest`,
so close blocks keep crisp pixels while distant ground stops sparkling); every other texture stays
plain `Nearest`. Right after `ContentRegistry.BakeModels`, `TextureAtlas.ApplyCutoutMips`
overwrites levels 1..n of every tile a cutout block wears (`BlockType.CutoutTiles`) with a
`CutoutMipmap` chain whose alpha is rescaled per level to keep the coverage at
`LightingConfig.AlphaCutoff` that level 0 has, so leaf canopies do not thin with distance.

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
/ `fBitangent` frame the normal map is read in, and it has to agree with `ChunkFaceTables.TiledUv`'s per-face
UV orientation or the relief comes out rotated or mirrored per face. Regenerate the art with
`tools/generate-ore-materials.ps1` (deterministic, byte-identical re-runs);
`visual-examples/ores.json` is the screenshot check, and wants a `VOXEL_TIME` since the highlight
tracks the sun. The ore body darkens by `metallic * 0.6` and the sun highlight, added in linear light,
is scaled by `SurfaceShadingConfig.OreSpecLinearScale` (`uOreSpecScale`), because the sRGB encode
lifts an add on the near-black body far more than it did in display space and smeared coal's
specks into gray. Every other (non-ore, non-water) tile is plain diffuse: the SH-23 grazing sky
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

The **ice glint** is a second such tile term, on the ice block's tile (`uIceLayer`; `uIceGlint` 0
switches it off, as in the ItemDebugger): a fresnel sheen of the sky the face mirrors
(`SkySample` of the reflected view ray, blended to `uFogColor` by `uFogWet`, dimmed by skylight)
mixed over the lit ice by `IceSheenStrength * Schlick`, the way the ore mixes its sky, plus a
Blinn sun highlight (`IceGlintShine`) of `IceGlintStrength` times the direct sun, added in linear
light and gated by skylight, the shadow and cloud shadow and the sun on the face's side. Ice is
translucent but its tile is opaque, so it needs no alpha handling in the translucent pass. Twin
`IceGlint` (`SheenWeight`, `Highlight`; `IceGlintTests`); `visual-examples/ice-glint.json` at
`VOXEL_TIME=405` places a patch of ice on the frozen plain and frames the highlight, the grazing
sheen and an overhead view.

The **ore twinkle** rides the material path itself (`oreTile`, a non-zero `material.metallic`,
inside the near fade): an `OreTwinkleDensity` share of the texels, hashed with `Pcg3d` from the
texel's place on the face's plane exactly like the snow glint (salted so the two never pick the same
texels), flash once per cycle on their own hashed rate (`OreTwinkleRateMin..Max` per second) and
phase, a squared tent of half-width `OreTwinkleWidth` (in cycles) around mid-cycle, added untinted as
`OreTwinkleStrength` times the direct sun. Gated like the snow glint by skylight, a thresholded
shadow and cloud shadow and the sun on the face's side, and faded out between
`OreTwinkleFadeNearBlocks` and `OreTwinkleFadeFarBlocks`, so ore in a cave (no skylight) never
twinkles. Twin `OreTwinkle` (`Cycle`, `Flash`, `Gate`; `OreTwinkleTests`); `uOreTwinkle` 0 switches
it off. `ores.json`'s spawn spot now sits under a canopy, so `visual-examples/ore-twinkle.json`
(`VOXEL_TIME=147`) lays coal, malachite and obsidian on the frozen plain and takes four shots 0.4 s
apart.

**Chunk meshing.** The column terrain a build reads (heights for the sun seeding, water levels,
the tint climate) comes from the 3x3 chunk columns around it, resolved once:
`ChunkMeshBuilder.GenerateMeshData` (behind `Chunk.GenerateMeshData`, the one caller of `GenerateMeshDataFromSnapshot`), right after the neighbor resolve and the empty/enclosed early
return, fills `MeshScratch.Columns` (`dx * 3 + dz` order) through
`ChunkLighting.FillColumns(chunkManager.ColumnSource, ...)`, and the sun seeding, the water-surface
codes and the tint fields all read that array (only the pebble, branch-pile and untouched-snow
emitters still read seed-hashed worldgen per cell, see the multiplayer "Known gaps"); a missing
streamed column makes the build return `null`, which the caller treats like a vanished neighbor
(see the threading rules).
`ChunkMeshBuilder.GenerateMeshDataFromSnapshot` runs two passes over the padded 18³
block snapshot. The binary greedy pass (`EmitGreedyFaces`, scratch in the `[ThreadStatic]`
`MeshScratch`) takes every block flagged `BlockType.IsGreedy`: a `Model`-renderer block whose
baked model is exactly the six untransformed cube faces (`ModelBakeSteps.BakesGreedy`; all four
built-in parents qualify, so stone, dirt, grass, logs, TNT, lamps, leaves and glass all do). It
builds per-axis bit columns of greedy and occluding cells, finds visible faces with one
shift-and-mask per column (the halo bits cull border faces exactly like the neighbor read),
keys each face by block id, the four corner AO values, the submerged flag and the four corner
**smooth light** sums (Minecraft smooth lighting, `ChunkLighting.SmoothCornerLight`): each corner's sun
and lamp light is summed over the front cell plus the same side/side/corner cells AO reads (a
side that blocks light, or a corner behind two blocked sides, is replaced by the front cell, so
an occluder darkens only through AO and light never leaks round a sealed diagonal), one exact
0..60 sum per channel, 12 bits a corner, traveling in a parallel 48-bit key
(`MeshScratch.PlaneLightKeys`) beside the int key, plus a 10-bit per-corner **tint code** in a
third, 40-bit key (`MeshScratch.PlaneTintKeys`). **Colored block light** is a dominant-color
palette tint, not RGB propagation: each block-light cell stores `level | slot << 4` (level bits
0-3, `BlockLightPalette` slot bits 4-6, so every level read masks `& 15`), seeded from
`BlockType.LightSlot`; a chunk with no tinted seed runs the unchanged `SpreadLight`, one with any
runs its twin `SpreadBlockLight`, where the brighter source wins and a tie at equal level goes to
the **lower slot** (re-queued so it propagates), which makes the result depend only on the seed
set, never on BFS order. `SmoothCornerLight` resolves the corner's four (level, slot) samples
through `LightTint.FromSamples` into `slotA | slotB << 3 | q << 6`, B's share being `q / 30`
(`LightTint.WeightDivisor`, t ≤ 0.5, canonical so equal mixes give equal codes), returned in
bits 12-21 of the corner code; the merge never crosses a tint change either, and the shader
lerps the decoded colors linearly, so greedy output stays pixel-identical. A red and a blue
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
emits everything else as before (tree wood — `TreeWoodEmitter.EmitTreeWood`'s node box and connecting tubes
for a thin cell, `TreeWoodEmitter.EmitRimWood`'s stair-stepped `TreeWoodShape.RimBoxes` for a beveled
pixel-rim trunk shell — pebbles, fluids, any model with cropped or rotated UVs); its full, shaded,
culled cube faces use the same corner rule, and the procedural emitters (tree wood, pebbles, branch
piles, snow layers) bilinearly interpolate the smooth corner light
of the cell face they sample (`FaceLight`, flat where that face's front neighbor occludes).
Fluids average each corner over the same own/dx/dz/diagonal cells `CornerHeight` reads, with a
blocked cell replaced by the brightest open one, an order-independent rule so neighboring fluid
cells agree at every shared corner. `GenerateMeshData(manager, greedy: false)` is the test seam;
`GreedyMeshingTests` rasterizes both outputs into unit cell faces and requires them to be equal
(`SmoothLightTests` locks down the light rules themselves). Because a merge never crosses a
lighting or AO difference, this world's rough noise terrain and leaf canopies only give ~15-20 %
fewer vertices (`plans/PERF.md` item 11), not the 5-10× of flat worlds; smooth light left that
almost untouched (+0.2 % vertices over eight sampled surface and cave chunks, mesh-build time
within noise), since generated terrain is mostly uniformly lit and only lamp and shadow
gradients split merges.

**Biome-tinted grass and leaves.** Grass and every species' leaves (`oak_leaves`, `spruce_leaves`,
`baobab_leaves`, `stone_pine_leaves`, all on the `foliage` palette) take their color from the
climate at their position, Minecraft style, while `FaceTiles` keep the default colored tiles, so
inventory icons, item drops and the held block never change. It is block data: a `biomeTint`
object on the block JSON (`BlockDefinition.BiomeTintDefinition`) names a `palette` (`grass`, the
default, or `foliage`) and a `faces` map keyed by face name (`north`..`down`, or the shorthands
`side` and `all`). A face entry is either `{ "tile" }`, a tinted tile drawn *instead of* the
model's face (one quad; the leaves use it on every face), or `{ "overlay", "base"? }`, a tinted
cutout tile drawn *over* the face with `base` optionally replacing the untinted tile beneath (the
grass side: a `dirt` base, then `grass_side_textureless`). `ModelBakeSteps.ResolveBiomeTints`
resolves it in `ContentRegistry.BakeModels` into `BlockType.BiomeTint` (`Content/Blocks/BiomeTintFaces.cs`: `Palette` plus
per-face `Base`/`Tinted` tiles, `NoTile` where a face has none) and throws naming the file unless
the block `BakesGreedy` and is not translucent, so both mesh paths can treat every tinted face as a
plain cube face; an `overlay` is further refused on a cutout block, which has no opaque base quad.
The alpha-tested tint tiles (every overlay, and a cutout block's replacement tile) join
`BlockType.CutoutTiles`, so they get the coverage-preserving mips a leaf tile gets. The color is
`TerrainGenerator.Climate`, the temperature and humidity noises normalized through the biome ramps
to warmth 0..1 (exactly 0.5 across the temperate band) and wetness 0..1 (the two humidity ramps
read the same way, `BiomeClassifier.WetnessOf`: 0 fully dry, exactly 0.5 between the dry and the
semi-dry ramp, 1 wet), so the palette's three rows of a cold and a warm sRGB corner
(`SurfaceShadingConfig.GrassColorColdDry/WarmDry`, `ColdMid/WarmMid`, `ColdWet/WarmWet` and the
slightly darker `FoliageColor*`) sit on the biome lines: Dry on the desert and tundra, Mid on the
steppe, the savanna and the dry taiga edge, Wet on the forest and taiga. `BiomeTint.PaletteSrgb`
interpolates them piecewise (Dry to Mid up to wetness 0.5, Mid to Wet above), and each Mid corner
is yellower than the plain average of its Dry and Wet corners, so the grass dries monotonically
and the dry grasslands read dry (`TheGrassDriesMonotonically`). `BiomeTint` samples it on a 16-block lattice of
chunk-column corners: `TintsAt` reads the climate once and returns both palettes (through
`TintsFromClimate`), `FieldsFor(cx, cz)` gives a chunk column's `BiomeTintFields` (a grass and a
foliage `BiomeTintField`, four corners each) and `BiomeTintField.Sample` interpolates bilinearly in
weighted-sum form, which makes two columns sharing lattice points agree bit for bit, so the color
is continuous across chunk borders. The lattice climate is stored per column:
`TerrainGenerator.Compute` evaluates `Climate` once at the column's minimum corner into
`ColumnData.CornerWarmth/CornerWetness`, and the chunk mesher reads that through
`ChunkManager.ColumnSource` (`BiomeTint.FieldsFromNeighborhood` over the build's resolved 3x3
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
the two together): the textureless tiles are gray around `SurfaceShadingConfig.BiomeTintTextureGray`
(sRGB 98), so a byte rgb capped at 1 needs that headroom to reach grass green. The two grass tiles
are supplied art; `tools/generate-leaf-textureless.ps1` writes each leaf one,
`<name>_leaves_textureless.png`, from its `<name>_leaves.png` (Rec. 601 luminance, alpha copied,
scaled to the same mean gray, byte-identical re-runs). Its `-Sources` list defaults to all four leaf
tiles; `tools/generate-tree-textures.ps1`, which tints the placeholder baobab and stone pine log and
leaf tiles from the oak art, calls it with `-Sources baobab_leaves, stone_pine_leaves` so a run of it
never rewrites the oak and spruce tiles. Both chunk mesh paths go through the one `ChunkMesher.EmitTintedCubeFace`, which draws the
base quad first and then the coplanar tinted quad, the overlay winning by draw order under
`Lequal`; the reflection and shadow passes reuse the same lists and inherit the order, and the
shadow pass's alpha test punches the overlay's holes over the opaque base. The far LOD reads the
same lattice from the region's own origin (`rx * regionChunkSpan`, never the prefetch origin
`baseChunkX`) through `LodFaceEmitter.AddBlockFace`, for grass tops and sides and for the canopy bumps, and a
tinted canopy's transparent texels fall back to the textureless gray (`vec3(0.1221)`, the decoded
98) instead of `uLodLeafColor`, so the tint still applies there; chunks, LOD and the felled tree
all bake the tint into their rgb. The felled tree is meshed by the real chunk mesher through
`CellBatchMesher.Build` (one scratch chunk per chunk its cells span, each at its real position), so
every chunk uses its own `BiomeTint.FieldsFor` and a falling canopy keeps the climate it stood in.
The mining pop and the break debris are standalone copies drawn at many
cells from streamed or cached geometry, so they wear the `BiomeTint` tiles (`PopBoxEmitter.EmitBox`'s
`biomeTint` argument; `BlockCubeCache.GetTinted`/`BuildTintedCube`, one cached debris mesh per
block, which only the debris asks for) with gray shade-only rgb, and the cell's color comes from
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
`BiomeTintTextureTests` (the tiles are gray at the Gain's gray and keep their source holes),
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
(`SetDrawUniforms`; the far LOD draws through the `CHUNK_DRAW` program too, `TerrainPasses.RenderLod` -> `chunkShader`), and takes its light per draw as `uEntitySun`/`uEntityBlock`, sampled from the
world at the entity's cell by `EntityLight.Sample` (each chunk keeps its mesh-time light snapshot
for that) and passed through `SetDrawUniforms`'s required `EntityLightSample` argument. Per-draw fade alpha reaches the fragment stage as the `flat` varying
`fAlpha`, because a uniform cannot vary between the sub-draws of one multi-draw. The wave and
sway blocks of `test-vertex.glsl` and `shadow-vertex.glsl` must stay byte-identical — a
`TO_WORLD(p)` macro is what lets them be, and `ChunkDrawShaderTests` guards the sway block and
its weight decode (both read `vColor.a` at location 1).

**Opaque occlusion cull (PERF_TODO F9).** The camera's opaque pass (`TerrainPasses.RenderWorld`)
adds its whole front-to-back run in one `BeginPass` but draws it as consecutive
`ChunkDrawBatch.DrawRange` segments, split at the shares of the run in
`PostProcessConfig.ChunkOcclusionSplits` (`HizOcclusion.SegmentStarts`). At each split
`ChunkOcclusionCuller.Cull` builds a half-resolution max-depth pyramid (R32F, padded to a power of
two, `hiz-build-compute.glsl`, twin `HizPyramid`) from the HDR depth the LOD and the earlier segments
wrote, then `hiz-cull-compute.glsl` (twin `HizOcclusion`) tests every later command: the chunk box
from its `ChunkInfo` origin grown by `HizOcclusion.ReachBlocks` (`ShadowCasterCull.CasterReachBlocks`:
the multiblock overhang plus the sway; opaque vertices have no waves), projected with the very
`uViewProj` the chunk program draws with, its rect rounded outward plus one texel, and a command whose
nearest corner depth lies strictly behind the pyramid max over the rect plus `DepthMargin` gets its
`InstanceCount` zeroed in place in the ring (a corner at or behind the eye plane always draws). Nothing
is reordered and a culled chunk has no fragment that could pass `LEQUAL`, fading or not, so the frame
is pixel-identical (a temporary occlusion-query and bit-diff check found 0 differences across the
scenarios, see PERF_TODO F9). Barriers: image access plus texture fetch after every pyramid level, the
last included, and `COMMAND` after the cull. Bindings: SSBO 0 ChunkInfo, 1 tile materials, 2 the
indirect ring (rebound every call, since a ring grow renames it); texture units
`RenderManager.HizDepthUnit` (10, unbound again before the next segment draws into that depth) and
`HizPyramidUnit` (11); image units 0 and 1. `ChunkOcclusionCullEnabled` switches it off. The cost sits
inside the `worldPass` row. The shadow cascades, the mirror and the translucent pass are not culled
this way (light-space occlusion measured ~0%).

## Layout

```
OpenGLTest/
  VoxelEngine.csproj           # the game (net9.0); <None Update> rules copy assets/ to the output directory
  GlobalUsings.cs              # project-wide usings: VoxelEngine.Core and the Shader alias (see "Code organization")
  src/                         # the app entry and the core types only (Program, World and its phase/seed/load gate, BlockType)
    Program.cs                 # entry point and composition root: window callbacks, the components, the AppScreen
                               #   flow (Menu/Loading/Playing/Saving), the deferred open/save/connect and the world lifecycle;
                               #   the frame-order OnUpdate/OnRender orchestration
    World.cs                   # the composition root: owns Camera, ChunkManager, ChunkGenerationWorker, LodManager,
                               #   the simulations and the per-job components (BlockEditor, BlockPlacer, LeftClickAction,
                               #   RightClickAction, ThrowAction, SpellCaster, LocalPlayer, ClientMirror, WorldPersistence, MobDirector, WorldRenderer,
                               #   ...); runs Update/UpdateClient/UpdateLoading, the Loading phase and SaveAndDispose,
                               #   and holds the dedicated server's public facade (BreakBlockFor, TryUseSelected, ...)
    WorldPhase.cs              # Loading / Playing / Disposed
    WorldLoadGate.cs           # GL-free: when a loading world may play (ready sphere meshed, or timeout); load progress
    WorldSeed.cs               # the runtime world seed: Apply reseeds worldgen + resets caches/LOD edits; BeginWorld/EndWorld
    BlockType.cs               # runtime block record built from JSON, plus the per-id lookup tables
                               #   the mesher and gameplay read (ById, Emission, IsSolid/Occludes/...)
    App/                       # the app's components, built once by Program.OnLoad; they read the live world and screen through Program
      AppScreen.cs             # what the window is showing: Menu / Loading / Playing / Saving
      ChatInput.cs             # the chat's keys, submitting a line, the local chat commands and the Tab player list's names
      DevDiagnostics.cs        # the startup build line, the VOXEL_TIME read, the live debug flag (X, VOXEL_DEBUG) and the X debug line
      GameplayInput.cs         # a playing world's per-frame polled input: key edges, hotbar, right-click chain, pause-gated world update, mining, middle pick
      InputEvents.cs           # the window's mouse and keyboard events, routed to whatever owns them on the current screen
      InventoryPanelInput.cs   # the [E] panel's input: clicks, scrollbar, search keys, creative buttons, hovered row, tooltip
      MenuInput.cs             # the pause menu's and shared settings page's input, and applying the window-level settings
      SpellBuilderInput.cs     # the spell builder's input: open from the table (or the hold-a-staff toast), close on
                               #   E/Escape or a changed held staff, cursor and look capture, clicks, bind/clear requests
    Audio/                     # sound: the sfx library, mixer and manager, the ambience bed
      AmbienceManager.cs       # looping day/night ambience with a slow crossfade
      SfxLibrary.cs            # maps the loose .wav files under a folder onto sound groups (and names the
                               #   code-played ones: item_pickup, spell_cast, spell_impact, spell_fizzle)
      SfxManager.cs            # one-shot sound effects built on the same NAudio stack as AmbienceManager
      SfxMixer.cs              # mixes any number of overlapping 16-bit stereo 44100 Hz clips over a silent baseline
    Chunks/                    # chunk storage and lifecycle, the column cache and column terrain, the light
                               #   query entities read (EntityLight); meshing, world sources and streaming below
      BoxSweep.cs              # GL-free, state-free solid-box axis sweep every loose body shares (drops, TNT, falling
                               #   blocks, projectiles, goblins, slimes, debris): StepSize, OffsetAxis, IsColliding, MoveOnAxis
      BurningFluid.cs          # GL-free burning-fluid (lava) cell test over a box, behind MobLava.BodyInLava and
                               #   PlayerBodyProbe.InLava
      CellKey.cs               # packs a world block cell into one long (fluid and leaf-decay schedules, tree growth sets),
                               #   forwarding to ChunkKey's one 21-bit bit math
      Chunk.cs                 # 16^3 block storage of one chunk: ids, the per-block state byte, the published light
                               #   snapshot, the raw writers and edit API, save/network snapshots, and its GPU slices
                               #   (UpdateMesh, MarkMeshedHeadless); GenerateBlockData, RestoreFrom and GenerateMeshData
                               #   forward to WorldGen/ChunkTerrainFill, ChunkRestore and Meshing/ChunkMeshBuilder
      ChunkCellCache.cs        # a simulation's per-frame cell access: the 16-slot chunk cache, TryGetCell, the raw Write
                               #   that queues the touched chunks, the MarkDirty batch; one instance per owner
      ChunkFluidQueries.cs     # the fluid surface, column-top and flow queries over the loaded cells (behind ChunkManager.GetFluid*)
      ChunkKey.cs              # packs a chunk position into one long, so the chunk
                               #   dictionaries hash one integer instead of three floats; the one 21-bit bit math, which CellKey shares at block scale
      ChunkManager.cs          # the chunk lifecycle and composition root: load/restore/stream-in/unload against a focus list
                               #   (unload only outside every focus's radius), saves of edited chunks, the world cell queries;
                               #   composes ChunkMeshScheduler, ChunkRenderLists and PlayerPlacedIndex and forwards their public members
      ChunkMap.cs              # the loaded-chunk table (ChunkManager.Chunks): a ConcurrentDictionary keyed on a packed ChunkKey long
      ChunkRenderLists.cs      # Renderable / TranslucentRenderable swap-remove bookkeeping and MeshVersion (behind ChunkManager's forwarders)
      ChunkRestore.cs          # rebuilds a chunk from a ChunkSnapshot (a save, a world source's map, a streamed chunk):
                               #   copies blocks and state, recounts, surface floor, fluid frontier, publishes (behind
                               #   Chunk.RestoreFrom)
      ColumnCache.cs           # the process-global column-data cache, its accessors and the column fill
      ColumnTerrain.cs         # one chunk column's terrain: heights, water levels, corner climate (warmth, wetness)
      EntityLight.cs           # GL-free world light query for entity draws (drops, pops, debris, the player, ...)
      EntityLightSample.cs     # sun and block light (each 0..1) one entity draw is lit with
      IColumnSource.cs         # the seam a mesh build, the fluid-surface query and the pop/debris tint read
                               #   chunk-column terrain through (ChunkManager.ColumnSource): IsReady + TryGet
      PlayerPlacedIndex.cs     # the player-placed cells bucketed by chunk, the restored-placement queue and its adoption, the unload snapshot's placed indices
      Meshing/                 # the mesh build: the greedy pass, the procedural emitters, the light volume and corner light
        CellBatchMesher.cs     # GL-free: meshes any TreeCell set with the real chunk mesher over scratch chunks
                               #   at their real positions inside an empty ring (the item debugger's TREES tab)
        ChunkFaceTables.cs     # the mesher's per-face tables (neighbor/AO deltas, the AO neighbor table AONeighbors,
                               #   ShadeLut, SmoothLightDivisor) and the shared corner/UV helpers (BoxFaceCorners,
                               #   TiledUv, CropUv, VertexAO)
        ChunkLighting.cs       # the wide light-volume snapshot and flood (sun + colored lamp) + corner light queries
        ChunkMeshBuilder.cs    # the per-chunk mesh build: neighborhood resolve, padded and light snapshots, the light flood
                               #   and the per-voxel emitter loop (behind Chunk.GenerateMeshData)
        ChunkMeshData.cs       # CPU-side mesh of one chunk (opaque + translucent lists, light snapshot, vents, flame points)
        ChunkMeshScheduler.cs  # the dirty set, the per-frame dirty scan, the thread-pool mesh runners, the completed queue and budgeted upload,
                               #   the immediate remesh paths (behind ChunkManager.Update, MarkDirty, ImmediateRemesh*)
        WoodDamage.cs          # the permanent chop damage per natural trunk cell (damage + first-hit side face) the mesher reads, and the damage -> slit depth rule
      ChunkMesher.cs         # the binary greedy pass, the biome-tinted cube face and model-tint helpers, and the box-face/quad primitives the emitters share
        FaceLight.cs           # a cell face's four smooth corner-light codes, bilinearly sampled by the procedural emitters
        FluidEmitter.cs        # a fluid cell's surface and side quads: order-independent corner heights, corner light, per-corner flow codes
        MeshScratch.cs         # per-thread scratch buffers of the binary greedy mesh pass
        PaneEmitter.cs         # a pane cell from the padded snapshot: post + arms, no face between
                               #   connected panes, caps culled against a same-shaped pane above/below
        StackEmitter.cs        # the stack-block emitters: pebble piles, branch piles, layered snow
        TreeWoodEmitter.cs     # tree wood: a thin cell's node box, tubes and plugs; the beveled pixel-rim trunk shell (RimBoxes)
        WaterSurfaceCodes.cs   # the 7-bit water-surface code a submerged face keys on (SubmergedSurfaceCode), its decode, BakedWaterSurface
      Sources/                 # where a world's terrain comes from: IWorldSource and its implementations (see "World sources")
        GeneratedWorldSource.cs # the default IWorldSource: ColumnCache.Get + Chunk.GenerateBlockData, byte-identical
                                #   to the path before world sources existed
        ImportedMapSource.cs   # an IWorldSource over a pre-built map folder (<world>/import/), worldgen outside it
        IWorldSource.cs        # where a world's terrain comes from (extends IColumnSource): GetColumn (a complete
                               #   ColumnData), Supplies, Fill(chunk); thread-safe, deterministic (see "World sources")
        WorldgenColumnSource.cs # the singleplayer/generated-server IColumnSource over ColumnCache; a server
                                #   with a custom world source uses that source, the client's is the ClientChunkStreamer
      Streaming/               # the chunk-generation workers, their shared scan frontier and the streaming foci
        ChunkGenerationWorker.cs # background threads creating chunks around every streaming focus (one
                                 #   ChunkScanFrontier per focus, served round-robin)
        ChunkScanFrontier.cs   # the shared resume point for the chunk-generation workers' closest-first offset scan (PERF_TODO C3)
        StreamingFocus.cs      # the published focus chunks (camera, or every connected player on a server):
                               #   an immutable array swapped only when a focus changes chunk
    Config/                    # every tuning constant, one static class per domain (all const or static
                               #   readonly, read concurrently by every thread; namespace VoxelEngine.Config)
      AudioConfig.cs           # ambience bed paths, volumes and fades; the sfx volume
      HudConfig.cs             # selection outline and pulse, hotbar name fade, pop and flash, the loading screen
      IcebergConfig.cs         # icebergs: caps and the spawn ring, the spawn band, size and mass, drift and push,
                               #   wobble, life cycle, the client mirror's lead
      JuiceConfig.cs           # landing and footstep dust, slime splat, hit sparks, death poof, blast shake and
                               #   push, falling-block dust, lava steam, tree thud, splashes, tool shatter, pickup
                               #   sparkle, low-health vignette, damage arc, surfacing droplets
      LightingConfig.cs        # alpha cutoff, atlas anisotropy, light spread, shadow cascades, point and fire
                               #   lights, fog, tonemap exposure, eye adaptation
      ManaConfig.cs            # the mana pool: start max, growth per known part and its cap, the refill time
      SpellConfig.cs           # spell recipe rules: when complexity starts to fizzle, the chance per unit and its cap;
                               #   the staff slot cap and the cast cooldown (CastCooldownSeconds)
      MobConfig.cs             # mob caps and shared mob rules, layer difficulty, hell mobs, slimes, goblins (flee, dodge, pack, torches, notice
                               #   pip), healthbars, damage numbers, the realm layer difficulty step
      NetworkConfig.cs         # multiplayer cadences, movement, edit and intent rules (the attack and cast slack),
                               #   timeouts, LOD streaming, ServerAllowCreative
      ParticleConfig.cs        # falling leaves, mining debris, campfire, lava and torch embers and smoke, bubbles,
                               #   spring steam
      PlayerConfig.cs          # break overlay, camera and swing feel (sprint FOV, strafe tilt, hit-stop, throw
                               #   zoom), water depth states, throws, slime block, sink blocks, bandage
      PostProcessConfig.cs     # biome grade, damage fringe, underwater wobble, FXAA, contact AO, godrays, GPU timer
                               #   gate
      SaveConfig.cs            # autosave, save writer retries and shutdown, teardown joins and drain, entity restore
      SimulationConfig.cs      # fluid tick, tree felling, reactive growth, leaf decay, ash, saplings
      StatusEffectConfig.cs    # status effects: the duration scale cap, the block writer's reach and write caps,
                               #   the mob tint strength, the particle emitter cap and rate
      SkyConfig.cs             # day length, sun and glare, sky dome, stars, moon, moonlight, aurora, clouds, wind,
                               #   the sky-island cloud sea (SkyCloudSea*), the realm layer fog (HellFog*, SkyAltitudeTint)
      StreamingConfig.cs       # view radius, per-frame mesh, dirty and scan budgets, thread counts, LOD build and
                               #   upload budgets, loading budgets and gate
      SurfaceShadingConfig.cs  # per-tile shader terms (ore, snow, leaf backlight, grass shimmer, ice, glass, wet
                               #   albedo, lava), biome tint palette, LOD sky exposure, leaf sway
      WaterRenderConfig.cs     # reflection and its reuse, waves, refraction, absorption, caustics, underwater rays
                               #   and fog, flow, foam, glitter, SSR, deep-water fog, LOD water sheet
      WorldFeatureConfig.cs    # pebbles, flowers, foliage, cotton and the scenery generators
      WorldGenerationConfig.cs # the seed and every worldgen knob: terrain, water levels, biomes, snow and ice, ores,
                               #   lava, ravines, caves, worms, islands and archipelagos (Island*, Archipelago*),
                               #   tree placement, worldgen cache sizes, and every realm layer Y band (Barrier*,
                               #   Hell*, CloudSlab*, SkyIsland*; plans/LAYERS.md, ordered by LayerBandTests) with
                               #   the barrier's patch noise (BarrierEnabled, BarrierPatchFrequency) and the hell
                               #   caverns (HellEnabled, HellNoiseStride, HellCavern*, HellEdge*, HellOpenThreshold*,
                               #   HellLava*) and biomes (HellBiome*, HellAshlands*, HellFungal*, HellPool*,
                               #   Hell*SkinBlocks, HellEmberCrystalRate, HellMagmaFloorRate) and flora (HellVine*,
                               #   Hell*VineRate, HellGlowFernRate, HellEmberBloomRate, HellTree*), and the solid
                               #   cloud patches (CloudPatchesEnabled, CloudPatchFrequency, CloudPatchThreshold,
                               #   CloudPatchCoreValue, CloudPatchDetailFrequency)
    Content/                   # the data-driven content pipeline (ContentRegistry) — see "Content" below
      ContentRegistry.cs       # the static facade: owns the content tables, assigns byte ids, runs Load / BakeModels /
                               #   Reset in order and serves the public lookups (RequireBlock, recipes, tags, ...)
      ContentFiles.cs          # the one JsonSerializerOptions, JSON file listing and reading, name -> id resolution
      EntityLoader.cs          # data/entities -> EntityRegistry (hp, loot table, the parsed .bbmodel)
      ItemLoader.cs            # data/items -> ItemRegistry (use, tool, display, spritesheet); the item-use check
      TagLoader.cs             # data/tags -> the tag sets; warns on a tool tag no item provides
      TreeSpeciesLoader.cs     # data/trees -> TreeSpeciesRegistry (TreeSpeciesCompiler) and the pixel-rim table
      Blocks/                  # block-side enums and resolved tables (renderer, break, attack, use, biome tint faces)
        BlockRenderer.cs, BlockBreakAction.cs,
          AttackPattern.cs, ItemUseAction.cs  # geometry/break/attack/use enums
        BiomeTintFaces.cs      # a block's resolved biomeTint: palette + per-face base/tinted tiles
        BlockFieldRules.cs     # a block JSON's plain per-field rules: renderer, flow, stacking, support, ground traits, tools,
                               #   the status flags (ValidateStatusFlags)
        BlockLinkResolver.cs   # the cross-block references (hardening, freezesTo/meltsTo, multiblock, wall block, upper half,
                               #   pickAs), queued per file and resolved once all load
        BlockLoader.cs         # data/blocks -> BlockType and the per-id tables: LoadBlocks, BuildType and RegisterTables
        BlockSchemaParser.cs   # a block JSON's shape fields: material, flame, facing/support, pane, open, hitboxes, tree wood
      Definitions/             # the JSON DTOs
        BlockDefinition.cs, ItemDefinition.cs,
          LootTableDefinition.cs, ModelDefinition.cs  # JSON DTOs
        AlloyRecipeDefinition.cs # JSON shape of assets/data/recipes/alloy/<name>.json: result + parts
        CastRecipeDefinition.cs # (fired mold, material) -> result + cooling
        CookRecipeDefinition.cs # JSON shape of assets/data/recipes/cook/<name>.json
        CrushRecipeDefinition.cs # JSON shape of assets/data/recipes/crush/<name>.json
        EntityDefinition.cs    # JSON shape of assets/data/entities/<name>.json
        MeltRecipeDefinition.cs # block/item -> (material, ml)
        MoltenMaterialDefinition.cs # a molten metal: id, tile, tint
        RecipeDefinition.cs    # JSON shape of assets/data/recipes/<name>.json
        SpellPartDefinition.cs # JSON shape of assets/data/spell/<kind>/<name>.json
        StatusEffectDefinition.cs # JSON shape of assets/data/effects/<name>.json
        TagDefinition.cs       # JSON shape of assets/data/tags/<name>.json
        TreeSpeciesDefinition.cs # JSON DTO of assets/data/trees/<species>.json
      Loot/                    # compiled loot tables, loot context, wood loot bands
        LootTable.cs, LootContext.cs  # compiled loot tables and the world cell they roll at
        LootTableCompiler.cs   # data/loot -> compiled LootTables (pools, rolls, ranges)
        WoodLootBand.cs        # one thickness band of a tree_wood block's loot (self or a table)
      Models/                  # Minecraft model format and Blockbench projects: parents, baking, quads
        ModelBaker.cs, BakedModel.cs, BakedQuad.cs  # elements -> atlas-ready quads + full-cube flags
        BbmodelLoader.cs       # Blockbench .bbmodel: groups (nested, with Parent),
                               #   cubes, per-face UVs, the skin texture and the
                               #   keyframe `animations`; rest rotations rejected
                               #   (the player's posing is procedural, entities
                               #   animate from their clips)
        BlockModel.cs, ModelLoader.cs  # Minecraft model format: parent chains, #texture refs
        ModelBakeSteps.cs      # BakeModels' steps: facing/multiblock bake checks, BakesGreedy, tint resolution, parent self-check
      Recipes/                 # compiled craft, cook, crush, melt, cast and alloy recipes
        AlloyRecipe.cs         # one alloy: a set of molten parts (material, ml) -> the same ml of the result
        CastRecipe.cs          # (fired mold, material) -> result + cooling
        CookRecipe.cs          # one compiled campfire recipe: raw input, cooked result and seconds on the fire
        CraftingRecipe.cs      # one compiled crafting recipe: what it makes and what one craft consumes
        CrushRecipe.cs         # one compiled quern recipe: crushable input and the crushed result
        MeltRecipe.cs          # block/item -> (material, ml)
        RecipeLoader.cs        # data/recipes (craft, cook, crush, melt, cast, alloy) and data/molten -> the compiled lists
      Shapes/                  # hitboxes and the shape resolvers (pane, multiblock, tree wood, snow, facing, break overlay)
        BlockHitbox.cs         # one axis-aligned selection box in cell-local 0..1 space
        BlockShape.cs          # the one live-world box resolver (raycast, outline, pop,
                               #   shape collision): tree wood, snow, pane, facing, plain
        BreakOverlayUv.cs      # cell-space crop of the block-breaking crack sprite for one hitbox face
        HorizontalFacing.cs    # quarter turns about a footprint's center: points, boxes,
                               #   baked models (multiblocks at their size, facing blocks at 1)
        MultiblockShape.cs     # a multiblock volume's geometry at any size (2..4): rotating a point,
                               #   a direction or a whole baked model to a facing; the legal sizes
        PaneProfile.cs         # one pane block's dimensions (its "pane" JSON): post and
                               #   arm widths, the rails an arm fills, interned; its boxes
        PaneShape.cs           # the pane renderer's shape: which neighbors get an arm,
                               #   post/arm boxes, the cached hitboxes per arm mask
        SnowShape.cs           # state-aware hitbox shape of layered snow
        TreeWoodShape.cs       # the one tree_wood shape rule: which cells connect (parent
                               #   edges, never detached wood), node and tube hitboxes,
                               #   body overlap and the beveled pixel-rim boxes (RimBoxes)
      Spells/                  # spell parts and recipes (MG-2): kinds, the compiled part, its registry and
                               #   loader, the recipe compile
        SpellPartKind.cs, SpellPart.cs  # element/shape/modifier/trigger; one compiled part keyed <kind>/<name>
        SpellDelivery.cs       # how a shape delivers its element: Projectile (default) or Self
        SpellImpact.cs         # [Flags] what an element does where it lands: ignite, freeze, thaw, prime, light, grow
        SpellPartRegistry.cs   # every part by key, published once by ContentRegistry.Load
        SpellPartLoader.cs     # data/spell/<kind>/*.json -> parts; ReadParts and Compile are pure (tests)
        SpellRecipe.cs         # element + shape + modifiers + trigger -> cost, verdict, fizzle, factors
        SpellStats.cs, SpellVerdict.cs  # what a recipe compiles to; the first rule it breaks
      StatusEffects/           # status effects (MG-4b): the compiled effect, its block actions, loader and registry
        StatusEffect.cs        # one compiled effect: duration, tick, damage share, factors, block actions, visuals; Bit
        StatusEffectBlockAction.cs # [Flags] Ignite / Freeze / Thaw, what an effect's tick does to the cells around
        StatusEffectLoader.cs  # data/effects/*.json -> effects (id = file-name order, at most MaxKinds); Compile is pure
        StatusEffectRegistry.cs # every effect by name and id, KnownMask, published once by ContentRegistry.Load
      Stations/                # GL-free render constants of the kiln, quern and casting passes, molten materials
        KilnRenderer.cs, QuernRenderer.cs,
          CastingRenderer.cs   # GL-free placement/animation
                               #   constants tying a station's render pass to its
                               #   authored model (the crucible bowl, fuel lump and
                               #   pending-charge sizes every kiln shares; the mold
                               #   pocket levels and the cooling dissolve curves)
        KilnLayout.cs          # one kiln model's opening and its bowl, fire, fuel and mouth spots, per structure
                               #   size (Kiln 2, Furnace 3, ForSize), and ToWorld onto a facing
        KilnProfile.cs         # a kiln anchor's compiled `kiln` object: fuel cap, melt speed (MeltSeconds), can-alloy,
                               #   and the Layout of its multiblock size
        MoltenMaterial.cs      # a molten metal: id, tile, tint
        SpritePose.cs          # the face-up pose and size-dependent resting lift
                               #   shared by every item sprite lying on a station
                               #   (the cooled cast, kiln fuel)
    Core/                      # GL-free utilities: math, springs, vectors, color space, logging, the frame
                               #   profiler, launch mode, voxel coordinates, file IO, the assets-directory lookup and the
                               #   vendored FastNoiseLite (never reformat)
      AssetRoot.cs             # the assets-directory lookup the server, the item debugger and
                               #   the test fixture share
      BodyMotion.cs            # GL-free, state-free velocity steps every loose body shares (drops, TNT, falling
                               #   blocks, projectiles, goblins, slimes, debris): gravity, sinking, floating,
                               #   water drag, bounce, ground scrub, and their numbers
      ColorSpace.cs            # exact sRGB transfer functions
      FastNoiseLite.cs         # vendored, don't reformat
      FileUtils.cs             # ReadFile: reads a file by path (assets are opened relative to the working directory)
      FrameProfiler.cs         # per-frame CPU timing, collected on every frame, plus
                               #   per-pass GPU timing, which is gated by GpuTimingGate
      LaunchMode.cs            # GL-free rule: VOXEL_SCRIPT/SPAWN/LOOK bypass the menu (storeless world)
      Log.cs                   # async console sink: every diagnostic line is enqueued and written by one background thread in order
      MathUtils.cs             # lerp/easing/pulse helpers for animation curves + shared constants
      Spring.cs                # a damped spring that carries its own velocity
      Vector2Utils.cs          # extension operations on Vector2 mirroring Vector3Utils
      Vector3Utils.cs          # extension operations on Vector3 that are repeated across gameplay, physics and render code
      VoxelCoords.cs           # chunk/world coordinate math shared by generation, physics and scheduling code
    Data/                      # Vertex struct (32 B: float pos, rgba, float tile-local UV, sun/block bytes, a normal/sway/tint
                               #   ushort, ushort array-texture layer), cube geometry, frustum/plane math, direction tables
      CubeVertexPositions.cs   # the eight named corner positions of the unit cube
      Frustum.cs               # the six normalized view-frustum planes of a view-projection
                               #   matrix and an allocation-free AABB visibility test
      Plane.cs                 # one plane (normal + distance): Normalize and the signed distance to a point
      Rgba.cs                  # straight (non-premultiplied) 8-bit RGBA color, matching the packing Vertex expects
      UvRect.cs                # an axis-aligned region of one slice of an array texture, in 0..1 UV space
      Vector3Directions.cs     # integer companion to CardinalDirections, in the SAME
                               #   order (Up, Down, Left, Right, Forward, Backward)
      Vertex.cs                # the one vertex layout every textured mesh shares (chunks, LOD, drops, icons, UI, sun)
    Effects/                   # GL-free twins of shader terms, by sub-domain
      Climate/                 # the biome tint lattice and palettes, the eye's climate
        BiomeTint.cs           # GL-free biome tint: Climate -> palette -> vertex tint on a 16-block lattice, Gain, Shade, CellTint
        BiomeTintField.cs      # one palette's four lattice corners over a 16-block field, sampled bilinearly
        BiomeTintFields.cs     # a mesh build's grass and foliage fields, both from one climate evaluation per corner
        BiomeTintPalette.cs    # Grass / Foliage: which palette a biomeTint block reads
        EyeClimate.cs          # GL-free climate at a world column from an IColumnSource's stored corner climate (no worldgen)
      Lighting/                # the block-light palette, its two-slot tint code and the campfire flicker
        BlockLightPalette.cs   # GL-free 8-slot block-light color palette (slot 0 = the default warm torch color)
        LightFlicker.cs        # GL-free per-cell flicker phase + curve for the campfire point lights
        LightTint.cs           # GL-free 10-bit two-slot tint code: FromSamples/FromWeights/Decode, the C# twin of the shader decode
      Post/                    # the post pass and screen-space effects: tonemap, eye
                               #   adaptation, biome grade, contact AO, godrays, underwater wobble
        BiomeGrade.cs          # GL-free post color grade from the eye's climate: warm over desert, cool over snow, eased
        ContactAo.cs           # GL-free twin of contact-ao-fragment.glsl (SH-28)
        ContactAoTaps.cs       # the nine raw window depths the contact AO pass reads around one pixel (ContactAo.Occlusion)
        EyeAdaptation.cs       # GL-free exposure target from the voxel light at the eye, eased slower into the dark
        Godrays.cs             # GL-free twin of the godrays' strength (facing, sun height, cloud, wet), scatter and phase
        Tonemap.cs             # the C# twin of post-fragment.glsl's tonemap, used only
                               #   by tests: exposure, then a per-channel clamp to 0..1
        UnderwaterWobble.cs    # GL-free twin of the post pass's underwater screen sway: the offset and its wrapped clock
      Sky/                     # sun, moon, stars, aurora, glare, silver lining, wind and the day cycle
        Aurora.cs              # GL-free twin of the aurora's fade (eye coldness x star visibility) and its wrapped noise drift
        CloudSilverLining.cs   # GL-free twin of the clouds' silver lining: sun gate, forward lobe, thinness along the view
        DayCycle.cs            # GL-free sun direction, dayness and night factor of the lighting clock
                               #   (FrameLighting.SetLighting and the slime size roll both read it)
        LayerClimate.cs        # GL-free realm layer sky: hell tint and altitude shift of the eye's height, eased,
                               #   applied to the sRGB SkyPalette; the hell layer index
        Moonlight.cs           # GL-free key-light rule: sun by day, moon at night, handed over with both dark
        MoonPhase.cs           # GL-free moon: the day's phase (steps at noon), its light direction, lit share, visibility
        SkyPalette.cs          # GL-free zenith/horizon/glow sky palette and the C# reference of SkySample
        ShootingStar.cs        # GL-free twin of the night dome's shooting stars: hashed slots, the great-circle streak, its light
        StarField.cs           # GL-free twin of the night dome's star field (cells, hash, visibility)
        SunGlare.cs            # GL-free sun lens glare: per-frame intensity and its sky falloff
        Wind.cs                # the one wind of the world: a horizontal direction that turns slowly
                               #   and steadily with the lighting clock (window time plus VOXEL_TIME)
      Surfaces/                # per-tile terms of test-fragment.glsl: glass, grass, ice, lava, leaves, ore
        GlassSheen.cs          # GL-free twin of the glass sheen: fresnel weight and the sky folded into the blend
        GrassShimmer.cs        # GL-free twin of the grass wind shimmer: gust-carried bands, distance fade, the face gate
        IceGlint.cs            # GL-free twin of the ice tile term: fresnel sky sheen weight, gated sun highlight
        LavaPulse.cs           # GL-free twin of the lava glow pulse: the slow noise field and the brightness factor
        LeafBacklight.cs       # GL-free twin of the leaf backlight: lobe around the sun, sun and light-through gates
        OreTwinkle.cs          # GL-free twin of the ore twinkle: per-texel cycle, the flash tent, sun/distance gate
      Water/                   # deep-water fog, the wet waterline fresnel, sun glitter
        DeepWaterFog.cs        # the deep-water darkness curve: per-channel fog dim from the eye's depth, red first
        SurfaceFresnel.cs      # GL-free twin of the surface fresnel terms in test-fragment.glsl:
                               #   the shared Schlick helper the ore path reads
        WaterGlitter.cs        # GL-free twin of the sun glitter on water: mirror lobe, sparkle life and spot, gate
    Entities/                  # mobs (the Mob base and the shared GL-free mob rules; the goblin, the hell mobs and the slime below), item drops, projectiles,
                               #   primed TNT, falling blocks, damage numbers, the entity animator and clips
      AnimationClip.cs         # GL-free Blockbench keyframe clip: per-bone rotation/position/scale
                               #   tracks, linear/catmull-rom/step, loop/once/hold, timeline events
      ContactDamage.cs         # GL-free contact damage a body takes standing against a cactus
      DamageNumber.cs          # one floating damage number in a DamageNumberPool: where it started, the damage it shows
      DamageNumberPool.cs      # the floating damage numbers over struck mobs, in one fixed array: a hit claims a free slot
      EntityAnimator.cs        # GL-free per-entity animator: idle/walk locomotion + one-shot action
                               #   layer, events, parent-chain part matrices, HandAnchor
      EntityModel.cs           # a runtime entity model: the cubes parsed from a Blockbench project
      EntityType.cs            # one entity kind loaded from assets/data/entities/<name>.json: its display name, health pool
      FallingBlock.cs          # a gravity-affected block (sand, gravel) in flight
      FallingBlockLanding.cs   # a falling cube written back as a block: cell, block, fall, submerged (its dust)
      MobCaps.cs, IMobPopulation.cs  # the natural and hard mob caps every kind shares, over the
                                     #   living count each registered manager reports
      IMobTarget.cs            # what a mob chases and hurts: eye, immunity, TakeHit (Camera, ServerPlayer)
      ItemDrop.cs              # a collectable left behind by mining: usually a block, sometimes a pure item (see Item)
      LedgeProbe.cs            # GL-free ledge caution: the drop to the first ground ahead, safe-step rule
      EntityContract.cs        # the startup checks of a clip-animated kind's model: the humanoid groups, a clip,
                               #   a clip's timeline event (RequireHumanoid), each naming the model file
      Mob.cs                   # abstract base of every hostile (Slime, Goblin, the hell mobs): position, health,
                               #   facing, hit flash, body box, TakeDamage/Shove
      MobFallDamage.cs         # GL-free mob fall damage: a share of the mob's pool per block past its safe height
      MobInterest.cs           # GL-free per-mob interest: sight checks, losing a hidden target, idle
                               #   time, the idle despawn gate and wander goals
      MobLava.cs               # GL-free mob lava rule: body box in a burning fluid (BurningFluid), damage per tick
      MobLoot.cs               # GL-free loot toss: the up-and-out launch a killed mob's drops get
      MobTargets.cs            # GL-free target rules: body contact, nearest non-immune target, min distance
      RiggedMob.cs             # abstract walking mob animated from its clips (Goblin, the hell mobs): the
                               #   EntityAnimator, walk/auto-jump/float physics, facing, death/spawn/hurt clips,
                               #   the mirror's animation and BodyEffect
      PrimedTnt.cs             # a primed TNT block: a full-size cube that falls under gravity and
                               #   collides with blocks axis by axis like the player and the item drops (BoxSweep)
      ThrownProjectile.cs      # a thrown item or a goblin stone in flight: falls under Gravity,
                               #   its damage scaled by the charge, hits a mob or stops on a block
      Goblins/                 # the goblin: the mob, its stone aim and its torch roll
        Goblin.cs              # the goblin: body physics, throw-then-melee AI, clip/event contract
                               #   (ValidateContent), animation-driven release/impact events
        GoblinAim.cs           # GL-free low-arc ballistic solver for the thrown stones
        GoblinTorches.cs       # GL-free torch roll for a goblin spawn (night, depth, one uniform roll)
      HellMobs/                # hell's mobs (see "Hell mobs"): the shared base, the two kinds, the layer difficulty
        HellMob.cs             # abstract hell mob: layer-scaled pool, the chase frame (face, Decide, walk), the
                               #   throttled line of sight
        Hellbrute.cs           # the melee brute: walk, a charge with a clear line, the melee clip's impact event
        Cinderwitch.cs         # the fire caster: the range band, a cast with a clear line, the release event's bolt
        LayerDifficulty.cs     # GL-free: the per-layer multiplier (health, damage, cast cooldown) and pack size
      Icebergs/                # drifting icebergs (not mobs): the berg and its GL-free rules, see "Icebergs"
        Iceberg.cs             # one berg: variant, horizontal scale, quarter turns, seed, waterline center,
                               #   velocity, cached world boxes, emerge/sink, the client mirror's dead reckoning
        IcebergContact.cs      # GL-free box rules: overlap, riding, side contact and its push direction; the one
                               #   player body box (BodyMin/BodyMax) BodyCollision builds from
        IcebergMotion.cs       # GL-free drift along the wind, the push over the mass, the speed cap
        IcebergShape.cs        # a variant's collision boxes from its model cubes; the one turn rule (RotateQuarter),
                               #   ModelMatrix, Place, mass
        IcebergSpawnRule.cs    # GL-free spawn band: open water from SpawnColdMin to the freeze threshold
        IcebergWobble.cs       # GL-free render-only bob, pitch and roll of (seed, lighting clock)
      Slimes/                  # the slime: the mob, its sizes and its merge
        Slime.cs               # a slime: a gel cube that hops toward the player and only hurts on contact while it is off the ground
        SlimeMerge.cs          # GL-free slime merge: who merges (same size, touching, past the cooldown),
                               #   the merged size, center and health
        SlimeSize.cs           # GL-free per-size slime stats: body scale, health, contact damage,
                               #   loot rolls, each 1 + (size - 1) * its config step; the spawn size roll
    Graphics/                  # GL resource wrappers: Mesh, Shader, textures and the atlases,
                               #   the block/item mesh caches, cutout mips, tile materials
      BlockCubeCache.cs        # lazily built cube meshes, one per block type
      CutoutMipmap.cs          # GL-free coverage-preserving mip chain for cutout tiles (alpha rescaled per level)
      ItemMeshCache.cs         # lazily built extruded-sprite meshes plus their GL textures, one per Item
      Mesh.cs                  # an indexed triangle mesh in the shared Vertex layout
      Shader.cs                # a compiled GL program from vertex/fragment sources, with
                               #   an optional defines prefix (the CHUNK_DRAW variants); CreateCompute builds a
                               #   compute program (the Hi-Z build and cull)
      TextureAtlas.cs          # loads the individual block textures in
                               #   assets/textures/blocks/*.png into one TEXTURE_2D_ARRAY at startup
      TextureLoader.cs         # uploads textures for the shared Vertex layout
      TileMaterials.cs         # the per-atlas-layer material table the world fragment shader indexes by fLayer
    Interaction/               # GL-free block interaction rules: raycast, mining, combat, placement, support,
                               #   doors and tall blocks, multiblocks, the selection outline, chat commands;
                               #   and the World components that run them for a player: block edits,
                               #   placement, left and right click, throws, station clicks, mob strikes
      BlockEditor.cs           # World component: the removal edit paths (RemoveBlock, FellWood, tall blocks),
                               #   multiblock convert/revert, toppling-tree landing, the prediction report, LOD notify
      BlockInteractions.cs     # World component: the campfire, quern, kiln and cast mold right-clicks, openables
      BlockPlacer.cs           # World component: placement (PlaceBlockAtFace and its callers), the place pop,
                               #   pick block, the last placement hit
      BlockRaycaster.cs        # the DDA block raycast + line-of-sight segment test, split out of Camera.cs
      BlockSupport.cs          # GL-free needsSupport rule: the cell holding a block up (below, or
                               #   behind the front of a support "attached" block)
      ChatCommands.cs          # the chat command table: /help, /tp, /give, /time and their rights
      Combat.cs                # GL-free punch rules: the forward cone (3 blocks, 30° half-angle)
                               #   and entity damage, shared by the punch loop and the tests
      DoorState.cs             # GL-free state byte of an openable block: facing in bits 0-1, the
                               #   open bit 2, the index into its eight pre-turned shapes
      LeftClickAction.cs       # World component: the crosshair target, mining (UpdateBlockBreaking), punch and
                               #   slice, the look-at label and crosshair state
      Mining.cs                # GL-free mining rules: the tools/requiresTool gate, block damage per
                               #   hit and hit pace shared by the break loop and the tests
      MobStrikes.cs            # World component: a landed hit's damage, shove, sparks and floating number
      Multiblock.cs            # GL-free multiblock structure rules: the cells of a volume of any size
                               #   (Offsets(size)), the state byte (facing + offset from the anchor),
                               #   anchor resolution, the integrity check, Convert and Revert
      MultiblockPlacement.cs   # which volume of a given size a right-click meant: the rightmost, then
                               #   lowest candidate holding the clicked cell, and the facing the opening takes
      Placement.cs             # GL-free placement rules: which cell a right-click fills (a stack
                               #   flagged replaceableByPlacement, layered snow, is taken over in
                               #   place), what a placement may write over, and TryMount (a torch
                               #   on a block's side becomes its wallBlock, facing the hit normal)
      RightClickAction.cs      # World component: the right-click chain (TryUseSelected: stations, the spell table,
                               #   a staff's cast, the item's use, placement; one overload hands back the outcome)
                               #   and the timed use charge
      RightClickOutcome.cs     # what claimed a right-click: a station, the spell table, the item's use, a placement or a cast
      SelectionOutlineMesh.cs  # GL-free selection outline: the 12 box edges as thin beams (no glLineWidth)
      SelectionPulse.cs        # GL-free twin of the outline's breathing opacity (steady while mining)
      SliceCombo.cs            # the blade's two-phase slice combo: press/queue/advance state
      SwordCombo.cs            # the sword's cut / back cut / chop chain: press/queue/advance state and the
                               #   window after a swing in which the next press continues the chain
      SwordSwing.cs            # the three sword swings, in chain order
      SwordSwings.cs           # GL-free per-swing rules: the chain's successor, power, targets, cone vs
                               #   hemisphere, shove and hit-stop weight
      TallBlock.cs             # GL-free two-cell block rules (a door): each half's partner cell
                               #   and block, and the lower half every half drops and picks as
      ThrowAction.cs           # World component: Q drops, the throw charge and release, the projectiles in flight
    Items/                     # the Item class hierarchy ItemFactory hands out per use action, and the registry
      BlockItem.cs             # the Item every placeable block also is, so hotbar,
                               #   drops and recipes resolve both through ItemRegistry
      BucketPickupItem.cs      # scoops a targeted fluid source into the item (empty bucket)
      BucketPlaceItem.cs       # pours a fluid source into the targeted cell (full bucket), becoming the empty item again
      ConvertMultiblockItem.cs # the converter's right-click: try every multiblock with its own source, convert in place
      CrucibleInventory.cs     # GL-free helper that adds a crucible and hands back its stack
      CrucibleItem.cs          # a vessel whose molten contents ride on its own ItemStack
      FirestarterItem.cs       # the firestarter's own code class: the worked example of a per-item override
      GrowTreeItem.cs          # grows the tree the targeted cell belongs to by a few pulses (ash on a trunk or a canopy)
      HealItem.cs              # restores health (a bandage): heals the player the use
                               #   runs for by PlayerConfig.BandageHealAmount
      IgniteItem.cs            # strikes a spark at the aimed block: a dry TNT block primes, a fully built firepit lights
      IItemUseWorld.cs         # what an Item override may query and change during Item.OnUse
      Item.cs, ItemFactory.cs  # the abstract item; the per-use.action factory building the singletons
      ItemRegistry.cs          # every item by shared byte id: pure items (vine, branch, flint, ...) plus
                               #   one BlockItem per placeable block
      LearnPartsItem.cs        # a teaching scroll (learn_parts): teaches its parts, spent once, refused when nothing is new
      RestoreManaItem.cs       # a mana draught (restore_mana): refills a share of the pool, spent once, refused at a full pool
      ItemUseResult.cs         # outcome of Item.OnUse: did the use take effect, what
                               #   the tool morphs into, and whether the player punches
      PlaceStackItem.cs        # builds up a multi-state block (flint or obsidian on a pebble pile)
      PlainItem.cs             # a plain pickup: no right-click behavior (vine, branch, compass)
      PlantTreeItem.cs         # plants a sapling of the item's Item.UseTreeName species
                               #   on the aimed ground (an acorn on dirt or grass)
      StarterKit.cs            # the hint scroll a new survival player (and a server newcomer) starts with
      StaffBinding.cs          # GL-free staff slot rules: bind a recipe valid against the staff's tags, clear,
                               #   cycle (wrapping), select, resolve saved part keys
      StaffItem.cs             # a spell staff: slots, tier and allowed part tags; its spells ride its ItemStack
      WorldItemUse.cs          # World component: the one IItemUseWorld implementation, DispatchUse and the
                               #   dispatching player (bucket, pile, prime, heal, grow and plant wrappers)
    Juice/                     # game-feel rules: camera shake, kick, FOV and tilt terms, hit-stop,
                               #   damage direction and fringe, the low-health vignette, pops and shatters
      BlastImpulse.cs          # GL-free TNT blast shove: away from the center, falling to zero at the radius
      BreakKick.cs             # GL-free camera nudge of a heavy mining hit: strength from the block's hardness, harder on the breaking hit
      DamageDirection.cs       # GL-free damage direction arc: the last blow's world direction, its fade and
                               #   its screen direction from the view yaw (the vignette pass's third term)
      DamageFringe.cs          # GL-free twin of the post pass's damage chromatic fringe: strength from the flash, edge weight
      LowHealthVignette.cs     # the low-health warning: how strongly the screen edge pulses red
      PlacePop.cs              # GL-free twin of the placement pop: the growth a freshly placed
                               #   block's pop box is drawn with (SelectionPasses.RenderBlockPop)
      ShakeFalloff.cs          # GL-free camera-shake trauma of a world event (blast, tree thud) by its distance
      SprintFov.cs             # GL-free rule of the sprint FOV widening: how many
                               #   degrees the view eases out by while the player sprints
      StrafeTilt.cs            # GL-free rule of the strafe camera tilt: the view
                               #   rolls a little toward the side the player strafes to
      SurfacingDroplets.cs     # GL-free timing of the droplets streaking down after the eye surfaces from water
      SwingHitStop.cs          # GL-free hit-stop of one swing clock (PlayerModel's punch or slice)
      ThrowZoom.cs             # GL-free throw charge zoom: the FOV target from the charge and its spring step
      ToolShatter.cs           # GL-free rules of a tool that ran out of durability
    Lod/                       # far-terrain LOD: region builds, the manager, sky exposure, surface overrides
      LodBuildOutcome.cs       # what one LodGen worker spin did (Built / Idle / Throttled), which picks its back-off
      LodColumnCaps.cs         # the process-global mined-column LOD caps (MIN semantics): LowerColumnBelow, export/import for
                               #   lod-edits.bin, the per-world reset
      LodCoverPass.cs          # the buried-flag pass: regions covered by loaded finer regions or meshed chunks, re-run only
                               #   when its inputs move, the level-0 cover tests spread over frames (IsCoveredByChunks)
      LodFaceEmitter.cs        # a region build's face and water emission (ref struct): block palette, tint lattice, the output
                               #   lists, AddQuad/AddBlockFace
      LodLevelSpec.cs          # one LOD ring's parameters (cell size, region size and radius, sample margin, height bias,
                               #   proxy canopy)
      LodManager.cs            # the far-LOD composition root: builds the components, unloads out-of-range regions, uploads
                               #   finished builds (ApplyCompletedMeshes), streamed regions, the camera; forwards the rest
      LodMeshBuilder.cs        # the body of LodRegion.BuildMeshData, one instance per build: prefetch, fine-height grid, per-cell
                               #   grids, burial ranges
      LodMeshData.cs           # one finished region build (vertex/index lists, Y bounds, chunk-Y ranges) on its way to the main thread
      LodRegion.cs             # an uploaded far-LOD region: the Levels table, its pool slices, burial flags and per-column chunk-Y
                               #   ranges, the draw-order comparers; BuildMeshData forwards to LodMeshBuilder
      LodRegionClaims.cs       # the LodGen claim and build protocol (item C5): queued/dirty/building sets, the closest-first
                               #   scan, the in-flight cap, the finished-build queue, MarkEditedColumn
      LodRegionSet.cs          # the main-thread region set: RegionList swap-removes, the pending-cover list, the finer-child
                               #   counts and the region-set version
      LodSkyExposure.cs        # GL-free coarse sky exposure the LOD mesher bakes into Vertex.Sun from its column-top grids
      LodSurface.cs            # per-column far-LOD surface overrides (edited top height + tile), beside LodColumnCaps
      LodWorkerPool.cs         # the LodGen threads: Start (refused while a straggler lives), the worker loop, the joining Stop
    Magic/                     # the cast pipeline: the one SpellCaster seam and its GL-free rules (see the Cast intent)
      CastRisk.cs              # the pure fizzle roll and backfire damage, with the gear resistance hook
      CastPacing.cs            # one caster's cooldown (SpellConfig.CastCooldownSeconds, network slack passed in)
      CastRules.cs             # the pure cast judgement: held staff, selected bound slot, valid recipe, pacing,
                               #   then mana spent (or, for a client's prediction, only checked)
      CastVerdict.cs           # why a cast was refused, or Cast
      HeldStaff.cs             # the held staff's stack, its selected slot, and the select/cycle/bind/clear that write it
                               #   (the spell table's prediction and the server's inventory rules share TryBind/TryClear)
      KnownSpellParts.cs       # the one known-part rule (creative, or Inventory.KnowsPart) the spell builder, the client
                               #   prediction and the server share; Learn (a scroll) and SizeMana (the pool max follows
                               #   the owned known count)
      SpellBoltPreview.cs      # GL-free: a casting client's own cosmetic bolts (SpellProjectile's flight and stop
                               #   test, sparks only; never strikes, applies or writes anything)
      SpellCast.cs             # what the effect hook receives: caster, staff, slot, parts, stats, eye and aim
      SpellBlockImpact.cs      # what an element's impact flags do to a landed-on cell (prime, light, grow, and
                               #   ignite/freeze/thaw through StatusEffectBlockWriter.ApplyAt), every write hooked
      SpellCaster.cs           # World component: the one cast seam (playing gate, CastRules, the Effect hook and the
                               #   cosmetic Feedback hook; never spends or fires on a client), behind World.CastFor/SpellEffect
      SpellEffects.cs          # World component (authoritative only): the Effect hook; launches and flies the
                               #   bolts, lands them on mobs and cells (the impact sound near the camera), applies a
                               #   self cast; the `cast` op's TestCast
      SpellFeedback.cs         # World component (never on a headless server): the Feedback hook's cast/fizzle sound and
                               #   spark burst for the local player's cast, a client's preview bolts, remote cast bursts
      SpellFlight.cs           # what one bolt update ended in: Flying, HitMob, HitCell, Spent
      SpellImpactRules.cs      # GL-free: a bolt's damage and range from the recipe's factors, its element and
                               #   shape, the impacts that are block actions
      SpellProjectile.cs       # GL-free: one bolt in flight (straight, sub-stepped, BoxSweep and first non-air
                               #   cell, mob overlap, spent after its range)
      SpellSparks.cs           # GL-free: the shared spell sparks (the element's color, the trail, the impact, the cast burst)
    Networking/                # the GL-free multiplayer protocol and codecs both sides share
                               #   (see "Multiple players and chat"); the client and the server side below
      ChunkNetCodec.cs         # chunk blocks/state and column terrain (heights, water levels, corner climate),
                               #   Deflate-compressed
      InventoryIntent.cs       # one client inventory intent (select, drop, equip, craft, creative grant/clear, pick)
      InventoryIntentCodec.cs  # its bounds-checked wire form
      InventoryNetCodec.cs     # the server-owned inventory on the wire (the save JSON shape)
      LodNetCodec.cs           # a far-LOD region's mesh on the wire
      NetAttacks.cs            # what each NetAttackKind stands for: the attack pattern it needs in hand, the
                               #   sword swing it names, the NetPlayerAction it is rebroadcast as
      NetEntity.cs             # NetEntityKind/NetEntity and NetCellChange
      NetPlayerAction.cs       # the swing a player just made, as the server rebroadcasts it in each
                               #   player list entry (with a counter that moves on every new one)
      NetPlayerFlags.cs        # the PlayerState flags byte (creative, flying), a request to the server
      NetProtocol.cs           # NetMessage ids, length-prefixed framing, PacketWriter/PacketReader
      PlayerStatusCodec.cs     # PlayerStatusData (modes, health, dead, knockback) and its bounds-checked wire form
      ServerAddress.cs         # host[:port] parsing and formatting (the default port)
      StationNetCodec.cs       # the StationState snapshot: campfires, querns, kilns, cast molds
      WorldIntent.cs           # one client world intent (break start/complete, place, use, throw, attack)
      WorldIntentCodec.cs      # its bounds-checked wire form, with the sequence id
      Client/                  # the client: connection, streamed chunks and far LOD, the World mirror, predictions, remote players
        ClientChunkStreamer.cs # requests streamed chunks/columns and applies them, no worldgen; the
                               #   client's IColumnSource
        ClientLodStreamer.cs   # requests and applies server-built far regions
        ClientSession.cs       # the client's session: connect, the connecting screen, the network pump and cadences,
                               #   message dispatch, every send, the disconnect
        ClientMirror.cs        # World component, client mode: applies the server's cells, entities, stations,
                               #   health and player list (ApplyNetwork*, SyncRemotePlayers)
        ClientPrediction.cs    # GL-free: a client's pending predictions vs the server's answers (sequence ids)
        NetworkClient.cs       # the client: connect + handshake, reader thread, inbound queue, sends
        RemotePlayer.cs        # another player as the client sees them: pose smoothing + a PlayerModel
      ServerSide/              # the server's side of a session: the connected player and the rules its intents and
                               #   moves are judged by (the dedicated server itself is OpenGLTest.Server, VoxelEngine.Server)
        AttackPacing.cs        # GL-free: the shortest gap from one attack to the next (a punch's pace, a
                               #   slice's swing, the sword swing before the one asked for)
        BreakProgress.cs       # GL-free: a player's mining on the server, judged against the held item's hits
        InventoryIntentRules.cs # GL-free: what the server accepts of an intent, against that player's inventory
        MovementRules.cs       # GL-free movement rubberband: speed clamp, then collision, rise and hover checks
        MovementState.cs       # a player's per-connection movement memory (airborne-still time)
        MoveVerdict.cs         # Accepted / Clamped / Rejected
        NetValidation.cs       # server movement, edit-reach, intent-eye and visible-face sanity rules
        OperatorList.cs        # who may be creative: ops.json in the world folder plus --op, matched by save slug
        RateLimiter.cs         # GL-free token bucket (a player's edit rate)
        ServerPlayer.cs        # a connected player on the server: own Inventory, creative/flight flags, pose,
                               #   its PlayerContext, newest sequence id, mining progress, edit rate, attack pace,
                               #   cast pacing (Casting), its PlayerHealth/fall tracking/respawn eye, mob-hit sink,
                               #   correction numbers
    Particles/                 # every particle system (debris, bubbles, fire, leaves,
                               #   splashes, dust) and its renderer; the ParticleRing buffer
      AmbientEffects.cs        # World component: the fire cells (campfires, lit kilns, goblin torches), cosmetic
                               #   world effects (steam puffs, lava vents, spring steam, torch flames, bubbles,
                               #   water splashes, blast shake)
      BlockDebris.cs           # one chunk of mining debris: a small cube textured as the block it broke off from
      BlockDebrisSystem.cs     # owns the live mining debris: small textured cubes chipped off
                               #   on every mining hit plus a bigger burst when the block breaks
      BubbleParticle.cs        # one exhaled bubble: a small billboard that rises off the player's mouth while the eye is submerged
      BubbleParticleRenderer.cs # draws every live BubbleParticle as a camera-facing billboard in one dynamic draw
      BubbleParticleSystem.cs  # live exhaled bubbles: spawns them at the player's mouth while the eye is submerged
      DebrisLook.cs            # what a BlockDebris chip looks like, i.e. which cached cube BlockCubeCache draws it with
      FireParticle.cs          # one fire particle: an ember, a smoke puff or a steam puff
      FireParticleRenderer.cs  # draws every live FireParticle as a camera-facing billboard
      FireParticleSystem.cs    # live fire particles: embers and smoke rising off every lit cell the caller feeds in
      FootstepDust.cs          # the sprint footstep dust rule, GL-free: when a footfall happens
                               #   (FootfallsBetween) and whether it kicks up a puff (Kicks)
      GroundDust.cs            # the cosmetic ground dust: a landing from a real fall kicks up a puff of chips of the block landed on
      LandingDust.cs           # the landing-dust rule, GL-free: how many chips a landing kicks up and how fast they fly
      LeafParticle.cs          # one falling leaf billboard (sway, tumble, rest, fade); LeafParticleSystem owns them
      LeafParticleRenderer.cs  # the falling leaves as one back-to-front billboard draw
      LeafParticleSystem.cs    # falling leaves: ambient fall off canopy near the camera + bursts where it was disturbed
      ParticleRing.cs          # a fixed-capacity, order-preserving particle store
      PopBoxEmitter.cs         # the mining pop's geometry: each hitbox drawn as a box sampling its crop of the block's face tiles
      SliceTrail.cs            # GL-free ring of blade-tip samples in the swinging player's owner frame, plus a live head
      SliceTrailGlow.cs        # GL-free twin of slice-trail-fragment.glsl: the soft body, the white core, the streak lanes
      SliceTrailRenderer.cs    # the additive ribbon draw of one SliceTrail (slice-trail-*.glsl)
      SliceTrailRibbon.cs      # GL-free builder of the tapered, age-faded Catmull-Rom ribbon quads of a SliceTrail
      SliceTrailSystem.cs      # World component: feeds the local and remote players' slice trails; AssumedBlade
      TreeShudder.cs           # GL-free: the leaf cells above and around a chopped trunk cell, thinned to a few, that an axe hit shakes loose
      WaterSplash.cs           # GL-free splash rule (droplets by crossing speed) + Track: a body's droplets off the water top
      WaterSplashTracker.cs    # GL-free per-body water crossing memory: in water last frame, speed from its own height
    Platform/                  # Silk.NET window and input setup
      InputManager.cs          # keyboard/mouse context
      WindowManager.cs         # Silk.NET window setup
    Player/                    # the player: camera, physics, water phases, body probes, health,
                               #   fall tracking, the rig and model (its posing components live in Posing/),
                               #   sprint toggle, death messages
      Breath.cs                # GL-free air under water: drain, refill, drowning ticks, the wire step (see "Drowning")
      BodyCollision.cs         # GL-free, state-free player-body collision rule (BodyCollides, which the server's
                               #   MovementRules and PlayerBodyProbe judge by), the stepped axis moves and auto-step, stepping by BoxSweep.StepSize,
                               #   and the falling-cube shove behind Camera.PushOutOf
      Camera.cs                # the camera's pose, mouse look, eye water state and IMobTarget side, and the facade over
                               #   its components (ViewProjectionCache, ModeToggles, HeadMotion, ShoulderRig); the view and
                               #   projection matrices and the cache key; movement lives in PlayerPhysics.cs, the body
                               #   collision and the falling-cube shove in BodyCollision.cs, the DDA walk in
                               #   Interaction/BlockRaycaster.cs
      CameraMode.cs            # F5 camera modes, FirstPerson -> Shoulder -> Orbit
      DeathMessages.cs         # the death line from the blow PlayerHealth recorded
      FallTracker.cs           # GL-free fall-damage rule: peak/grounded chain fed one feet height at a time
                               #   (singleplayer's LocalPlayer.UpdateHealth and each ServerPlayer's accepted states)
      IBodySolids.cs           # non-block solids a player body collides with (the iceberg boxes), read by
                               #   BodyCollision, PlayerPhysics (Camera.BodySolids) and the server's grounded probe
      HeldItemPose.cs          # GL-free held-item sizing and the drawn sprite matrix (SpriteMatrix, BackOffset), shared
                               #   by PlayerPasses, the slice trail and the item debugger
      HeadMotion.cs            # the per-frame view juice (bob, roll, landing dip, hit kick, FOV punch, sprint FOV, throw
                               #   zoom, strafe tilt, crouch drop, death view, damage arc and droplet aging) folded into
                               #   Roll/BobHeight/BobStrafe/CurrentFov
      LocalPlayer.cs           # World component: the local player's spawn settle, health, death and respawn,
                               #   body model, hurt and punch feedback, the saved-player apply, its PlayerContext
      ModeToggles.cs           # the creative (C) and double-tap flight toggles and the sprint latch
      PlayerBodyProbe.cs       # GL-free body probes over the live chunk map: known (loaded), grounded (a sink
                               #   layer counts), in water, in lava (BurningFluid), the sink layer top, a cushioned
                               #   landing (bounce or softLanding); what the server judges a remote player's body by
      PlayerContext.cs         # who an interaction runs for: eye, aim, inventory, creative (local camera or a
                               #   server's connected player), threaded through every World interaction path
      PlayerHealth.cs          # the player's survival pool, 0-100 HP drawn as 10 hearts (1 heart = 10 HP)
      PlayerMana.cs            # GL-free mana pool: slow regen, spend (creative free, short refused), max from known
                               #   parts (SetKnownParts), wire values (see "Mana")
      SavedPlayerPools.cs      # restores a saved player's inventory, then sizes the mana for its known parts, then
                               #   health and mana (singleplayer and a server's players share the order)
      PlayerModel.cs           # blocky Minecraft-like player model with two-segment limbs: the state owner and facade
                               #   (Update, triggers, GetPose/GetPartMatrix/GetHandAnchorMatrix) over Posing/
      Posing/                  # the posing components PlayerModel owns (see PlayerModel.cs), namespace VoxelEngine.Player.Posing
        ArmOverlays.cs         # lays the pose-specific arm frames (jump, swim stroke and float, punch chamber, cocked throw) over the base frames
        ArmSolver.cs           # the two-bone arm math: hand offset for a shoulder pitch and elbow, and the joints that reach an offset
        BowPose.cs             # a bow's two-handed draw: item arm out, string arm to the cheek, side-on torso, the release snap (UsePose.Bow)
        CastMotion.cs          # the staff cast's gesture as pure functions of the cast clock: raise, thrust, recoil, return
      BladeSwingFrame.cs     # what the right arm, the body and the held blade do on one frame of a blade swing (a slice
                               #   or a sword swing): the one struct the body code reads of either
        BodyPartMatrices.cs    # the world matrices of the body parts and the held item's hand anchor (GetPartMatrix/GetHandAnchorMatrix bodies)
        FlightPose.cs          # the creative-flight pose: legs sway and trail bent back, the torso leans, scaling with flight speed to the PlayerConfig.Flight* caps
        LimbFrames.cs          # the arm-frame builders (FrameOf, UpperFrame, ForeFrame, Blend) and the shoulder socket
        PoseBuilder.cs         # assembles the frame's joint pose: gait and jump blend, then swim, mods, jab, throw and use in order
        PoseMods.cs            # the secondary-motion values ComputeMods hands to ApplyMods
        PunchPose.cs           # the jab: phase weights and the two-bone IK that drives the fist along the aim
        SecondaryMotion.cs     # the body-wide mods (landing dip, crouch, flinch, sway, twist, swim tilt, death fall) and ApplyMods
        ShoulderReach.cs       # where each shoulder is pulled toward its reaching arm (the target of the owner's pull springs)
        SliceMotion.cs         # the slice swing's phase shapes as pure functions of the slice clock, and its BladeSwingFrame
        SwordMotion.cs         # the sword's cut, back cut and chop as pure functions of their clocks (one Shape
                               #   each), and their BladeSwingFrames
        SwimPose.cs            # the swim stroke shape and the swim and surface-float leg pose
        ThrowPose.cs           # the right arm's throw and timed-use overrides
      PlayerPhysics.cs         # walk/fly/swim input -> body position (moved through BodyCollision),
                               #   knockback, water state, pass-through and ground-friction probes; owns and ticks the ViewShake
      PlayerRig.cs             # the posed shape: pivots, cuboids, skin palette, mesh building (Blockbench or Steve)
      ShoulderRig.cs           # the F5 rig (mode cycle, shoulder offset march, tilted view direction, orbit body facing)
                               #   and the crosshair raycast along it
      SinkLayer.cs             # GL-free sink-block rule: the solid part of a sink block, feet in its soft layer,
                               #   the slow hold (SinkBlockSpeed) the physics, the collision and the server share
      SpawnOverrides.cs        # VOXEL_SPAWN / VOXEL_LOOK parsing and the origin surface spawn search
      SprintToggle.cs          # the sprint latch, GL-free: flipped on each fresh press
                               #   with the sprint-toggle setting on, else it follows the key
      ViewProjectionCache.cs   # the bit-compared view*projection and frustum cache over Camera.ViewProjKey (PERF_TODO R7/R11)
      ViewShake.cs             # the damage view shake: trauma fed by Camera.AddShake, decayed each frame by
                               #   PlayerPhysics.Update into the view offset and roll the view matrix and its cache key read
      WaterImmersion.cs, WaterPhase.cs  # GL-free water depth states: the submerged share of the body and
                                        #   its phase (Dry / Wade / Float / Swim)
    Rendering/                 # the GL render passes and their GL-free gates: RenderManager plus its pass components,
                               #   WorldRenderer, GPU timing, the texture-unit cache, shadow cascades, reflection reuse
      ChunkOcclusionCuller.cs  # the opaque pass's Hi-Z occlusion cull: the pyramid texture, the build and cull compute
                               #   programs (hiz-build-compute.glsl, hiz-cull-compute.glsl), Cull between segments
      FarShadowReuse.cs        # the GL-free gate behind the far cascade's reuse: re-render on no map, the moonlight
                               #   handover, a key-light turn, a re-center, eye depth along the light, age or a caster
                               #   geometry change (LightingConfig.FarShadowReuse*); visual-examples/far-shadow.json
                               #   (VOXEL_TIME=400, shots near 430) is the far-shadow pop check
      FrameLighting.cs         # the frame's light, sky, fog and eye state, computed once a frame by SetLighting (the
                               #   sun/moon key light, eye adaptation, biome grade, glare, godrays strength, point lights,
                               #   WindDirection) and read live by every pass; FireLightOffset
      GpuPassTimer.cs          # the per-pass TIME_ELAPSED query ring and its once-a-frame GpuTimingGate latch
                               #   (BeginGpuFrame, BeginGpuPass/EndGpuPass, EndGpuFrame, LogQueryCounts)
      GpuTimingGate.cs         # the GL-free decision behind the per-pass GPU timer queries
      HizOcclusion.cs          # GL-free twin of hiz-cull-compute.glsl: grown chunk box, outward screen rect and nearest
                               #   depth, the strict occlusion test, SegmentStarts; its exactness proof
      HizPyramid.cs            # GL-free twin of hiz-build-compute.glsl: the padded max-depth pyramid, LevelFor, MaxOver
      ReflectionEntities.cs    # the live entities ReflectionPass.RenderReflection may draw into the water mirror
      ReflectionPass.cs        # the planar water mirror: its target (CreateReflectionTarget), the temporal reuse and
                               #   RenderReflection (the mirrored chunks, LOD, sky, clouds and entities)
      ReflectionCull.cs        # GL-free: which opaque chunks and LOD regions can reach the mirror's clip plane
                               #   (top + TopMarginBlocks at or above WaterPlaneY); the rest draw no pixel;
                               #   MirrorView is the one mirror pose and MirrorMayRender the conservative
                               #   could-render gate the shadow pass reads before the mirror decides
      ReflectionReuse.cs       # the GL-free decision behind the reflection pass's temporal reuse
      RenderManager.cs         # the composition root of the frame's GL state: owns every GL resource and fixes
                               #   their creation and disposal order (LoadResources, Dispose); the world programs,
                               #   their one-time and per-frame uniforms (InitWorldProgram, ApplyFrameUniforms,
                               #   SetDrawUniforms) and the CHUNK_DRAW variants of the world/shadow programs;
                               #   BeginFrame (binds and clears the HDR target, draws the sky), BindWorldTextures,
                               #   SetViewport; ForgetWorld drops a disposed world's refs
                               #   cloud shadows: CloudShadowTexture bakes CloudField's level
                               #   grid into an R8 map bound ONCE on unit 8 (nothing else may
                               #   bind that unit); CloudShadow in test-fragment.glsl projects
                               #   each fragment up the sun ray to CloudHeight and dims the sun
                               #   term there, off while uCloudShadowStrength is 0 (ItemDebugger,
                               #   clouds turned off); visual-examples/cloud-shadows.json
                               #   owns the two ChunkGeometryPools and the ChunkDrawBatch every chunk
                               #   pass draws through; builds the pass components below and carries
                               #   the static facade the item debugger and tests read (PlayerModelPath,
                               #   the held-item sizing, PoseHeldItem, FireLightOffset, GroundAmbientOf)
      RenderTargets.cs         # the HDR scene target (RGBA16F color, sampleable depth, the color-only FBO the
                               #   contact AO draws through), the scene copies and the half-res rays target, all
                               #   recreated with the viewport; BindSceneTarget (every mid-frame restore);
                               #   CopyScene snapshots the frame's color/depth to units 3/4
                               #   for the water pass's refraction (see Water rendering above)
      ShadowCascade.cs         # one sun shadow cascade: radius, map size, depth FBO/texture, texel-stepped center (StepCenter) and light view-proj;
                               #   LightAxes, TargetOf; the far one is cached: UpdateReused holds its center and view-proj
                               #   until FarShadowReuse asks for a render, MarkRendered confirms it, Invalidate drops it
      ShadowCasterCull.cs      # GL-free: which chunk casters can reach a near shadow texel any consumer samples
                               #   this frame (camera and mirror pyramids in light space, PCF/normal-offset reach,
                               #   sway and overhang growth); the near cascade only (the cached far map draws its
                               #   whole box); its class doc lists every consumer of units 1 and 7
      ShadowPass.cs            # depth-only directional shadow passes (RenderShadows, which times
                               #   itself as shadowNear/shadowFar) run first
                               #   each frame into two cascades: the near one (90 blocks, unit 1)
                               #   with chunk and entity casters, then a coarse far one (352
                               #   blocks, unit 7) with chunk casters plus the non-buried LOD
                               #   regions (foliage cast opaque via uOpaqueCasters, no entities);
                               #   SampleShadow in test-fragment.glsl reads the near map and
                               #   blends in one far tap where the near box fades out, and LOD
                               #   fragments receive the far cascade through the same path
                               #   (the two ShadowCascades are RenderManager's, built by CreateShadowMap);
                               #   the far map is cached: it is re-rendered only on frames FarShadowReuse
                               #   asks for (decided in FrameLighting.UpdateShadowCamera, since BeginFrame
                               #   uploads uLightViewProjFar first) and skipped otherwise, and a re-render
                               #   draws every chunk and LOD caster in its cascade box with no receiver cull;
                               #   near chunk casters are culled to the box and then to the receiver hull
                               #   (ShadowCasterCull, pixel-identical), so a new shadow-map consumer or a
                               #   new world-program view must add its pyramid there or call KeepAll;
                               #   entity casters are culled to the box only
      TerrainPasses.cs         # the chunk and far-LOD draws: RenderLod (coarsest level first, buried regions
                               #   skipped), RenderWorld (opaque, front to back), CullWater (the translucent and
                               #   water draw lists) and RenderWorldTranslucent (back to front); the four chunk
                               #   passes are one MultiDrawElementsIndirect each, and the mirror shares its scratch lists
      TextureUnitCache.cs      # the GL-free bookkeeping behind the render-state rebind cache (PERF_TODO R5)
      TextureUnits.cs          # the one texture-unit binder every pass binds through (ActiveUnit, BindUnit,
                               #   BindLazy, InvalidateTextureUnits) over the one TextureUnitCache
      WorldRenderer.cs         # World component: the world's frame (Render, every pass in order) and its UI (RenderUI)
      Actors/                  # the entity and station draws: mobs, loose objects, the player, the stations
        IcebergPasses.cs       # the iceberg draws: one mesh and skin per variant, ModelMatrix (wobble and rise/sink
                               #   over IcebergShape.ModelMatrix), the entity, near-shadow and mirror passes
        MobPasses.cs           # the slime, goblin and hell mob draws (RenderSlimes, RenderGoblins, RenderHellMobs),
                               #   the slime mesh and skin, one RigMeshes per clip-animated kind, SlimeTransform
        RigMeshes.cs           # one clip-animated kind's rig, part meshes (ordinal-sorted) and skin, PartMatrix
        ObjectPasses.cs        # drops, thrown items, primed TNT, falling blocks, mining debris and toppling trees
                               #   (Render*/Draw* pairs; the mirror reuses the Draw* cores)
        PlayerPasses.cs        # the player body, held item and remote players (RenderPlayer, RenderHeldItem, RenderRemotePlayers),
                               #   the player rig and its meshes; the held sprite through HeldItemPose and DrawItemSprite,
                               #   and the assumed blade (SliceTrailSystem.AssumedBlade) while a remote player swings
        StationPasses.cs       # the station passes ride inside entityPass: RenderCampfireCooking,
                               #   RenderQuerns, RenderKilns (crucible mesh, molten pool, the last charged
                               #   input hovering over the rim and shrinking into it, stored
                               #   fuel lying flat) and RenderCastings (the pouring liquid
                               #   dissolving into the cast, drawn at CastingRenderer.ResultSize
                               #   — the pocket's width — not drop size)
      Atmosphere/              # the sky dome and the cloud layer: their passes, GL-free twins and the cloud-shadow map
        CloudCopies.cs         # the GL-free arithmetic behind the cloud layer's 3x3
                               #   sweep: where each copy of the field sits this frame
        CloudCover.cs          # whether the cloud layer, as CloudRenderer draws it, stands between an eye and the sky along a ray
        CloudTiles.cs          # GL-free twin of the cloud cull: per-tile vertex bounds of the field mesh and the
                               #   frustum-culled indirect commands both cloud sweeps draw (an order-preserving
                               #   subsequence of the full instanced draw; the margin covers
                               #   GL's -w near clip, which Frustum's z >= 0 near plane does not)
        CloudRenderer.cs       # draws the cloud layer: one static mesh built once from CloudField, drawn as
                               #   frustum-culled tile runs (CloudTiles), one MultiDrawElementsIndirect per
                               #   sweep (depth pre-pass, then color), main view and mirror each culled with
                               #   its own viewProj; WarmUp, called at the end of RenderManager.LoadResources,
                               #   pays the first-use draw cost at load (one triangle into a private 1x1
                               #   RGBA16F/Depth24 target under both cull faces, every state restored)
        CloudShadowTexture.cs  # the cloud-shadow coverage map: one R8 GL_TEXTURE_2D of CloudField.Cells texels per side
        SkyDomeFrame.cs        # what the sky dome needs each frame beyond its SkyPalette: the night sky's stars
        SkyPasses.cs           # the sky dome is drawn first, right after the clear (RenderSky,
                               #   called by RenderManager.BeginFrame: SkyRenderer, then the additive sun
                               #   disc from assets/textures/environment/sun.png via RenderSun and the
                               #   moon via RenderMoon), depth off, so the terrain that follows occludes
                               #   it; RenderClouds draws the cloud layer
        SkyRenderer.cs         # the full-screen sky dome pass (sky-fragment.glsl)
      Geometry/                # chunk draw plumbing: the geometry pools and their allocator, the indirect draw batch, the mesh list pool
        ChunkDrawBatch.cs      # persistent-mapped 3-slot ring of DrawElementsIndirectCommands +
                               #   the vec4(origin, alpha) ChunkInfo SSBO read via gl_DrawID; DrawRange
                               #   issues a pass as consecutive segments (the opaque occlusion cull)
        DrawElementsIndirectCommand.cs # one glMultiDrawElementsIndirect command (GL's layout), shared by the chunk
                               #   batch and the cloud tile runs
        ChunkGeometryPool.cs   # the shared chunk vertex/index buffers (opaque, translucent, LOD):
                               #   BufferStorage VBO+EBO+VAO, NamedBufferSubData uploads, grow by copy;
                               #   ContentVersion (moves on every upload, free and NoteDrawSetChange, a LOD
                               #   burial flip), summed by RenderManager.FarCasterVersion for the far shadow
        GeometryAllocator.cs   # GL-free first-fit free list with coalescing, in units of
                               #   64 vertices / 96 indices; hands out the pools' GeometrySlices;
                               #   ContentVersion moves on every successful allocate and free, never on Grow
        MeshBufferPool.cs      # recycles the vertex and index lists a mesh build fills
      Overlays/                # drawn over the scene: the UI pass (UiPasses, UIRenderer and its page renderers, the text cache), the damage vignette, the
                               #   selection outline and mining pop, and the world-space healthbars, damage numbers, notice pips and particles
        BillboardPasses.cs     # the world-space billboard draws: healthbars, damage numbers, notice pips and the
                               #   leaf, fire and bubble particles and the slice trails (RenderSliceTrails)
        ChatRenderer.cs        # the chat HUD: the word-wrapped log and the input line
        CraftCardRenderer.cs   # the [E] panel's crafting card and its hit-tests over the last drawn
                               #   CraftCardLayout
        DamageNumberRenderer.cs # draws the floating damage numbers (DamageNumberPool)
                                #   as world-space text that always faces the camera
        DamageVignetteRenderer.cs # full-screen damage vignette: one oversized triangle with
                                  #   a radial fragment gradient, drawn inside the UI pass
        EntityHealthbarRenderer.cs # five hearts floating above every live mob, always facing the camera
        HudRenderer.cs         # crosshair, hearts, hotbar and its feedback, toasts, FPS and profiler, tooltip,
                               #   player list, the status effect icons (DrawEffects); owns the crosshair, heart and
                               #   effect icon sprites
        InventoryPanelRenderer.cs # the [E] panel: tabs, search field, rows, creative catalog buttons,
                                  #   preview, and its hit-tests
        LoadingScreenRenderer.cs # the loading/saving screen and the fade over the first playing frames
        MenuRenderer.cs        # the pause and settings menus and every main-menu page, with their hit-tests
        NoticePipRenderer.cs   # draws the "!" a goblin pops over its head the moment it first notices a player
        SelectionPasses.cs     # the mining and placement pop (RenderBlockPop), the selection outline and face
                               #   highlight (RenderBlockSelection) and the crack overlay (RenderBlockBreaking)
        SpellBuilderRenderer.cs # the spell table's builder page: staff slots, the known parts by kind, the live
                                #   cost/verdict/fizzle/factors, BIND and CLEAR (or the hold-a-staff hint)
        TopStripRenderer.cs    # the top-center HUD strip (compass or clock) and the look-at label below it
                               #   (LookLabelTop)
        UiPasses.cs            # the UI hand-off: RenderUI, RenderChat, RenderTooltip, RenderPlayerList;
                               #   RenderMenuFrame: a world-less frame (main menu or the opaque
                               #   loading/saving screen) straight into framebuffer 0, no HDR,
                               #   lighting or GPU timer; RenderLoadingOverlay: the fade over the
                               #   first playing frames
        UIRenderer.cs          # the UI's GL resources (ui program, count font, text batch, item icons) and shared
                               #   primitives (rects, text, icons, input field); builds the page renderers as internal properties
        UITextCache.cs         # memoized per-frame HUD strings (FPS, compass, coordinates, counts)
      ScreenSpace/             # full-screen and screen-space passes: post (tonemap, FXAA), contact AO, godrays, underwater rays
        ContactAoRenderer.cs   # the screen-space contact AO pass (SH-28, contact-ao-fragment.glsl, twin ContactAo)
        GodraysRenderer.cs     # the godrays: a half-res shadow-map march over dry air, then a radial composite toward the sun
        HalfResTarget.cs       # the GL-free sizing arithmetic behind the half-resolution underwater-rays render target
        PostEffects.cs         # the post pass's per-frame camera effects (exposure, grade, ...) handed to PostProcessRenderer
        PostProcessRenderer.cs # the frame's final full-screen pass: one oversized triangle
                               #   (the shape SkyRenderer draws) shaded by post-fragment.glsl
        ScreenPasses.cs        # the full-screen passes: RenderPostProcess (tonemap, FXAA, the post
                               #   effects), RenderUnderwaterRays, RenderGodrays and
                               #   RenderContactAo: the multiplicative screen-space contact AO
                               #   (ContactAoRenderer) drawn through the color-only HDR FBO while
                               #   sampling the HDR depth texture, placed mid entity pass
        UnderwaterRaysRenderer.cs # additive full-screen sun shafts while the eye is submerged
    Saves/                     # GL-free world saves: stores, codecs, names, world sources (see "Saves")
      ChunkSaveCodec.cs        # the chunk file format: block-name palette + Deflate body
      CrucibleSaveCodec.cs     # a crucible's metals by name (CrucibleData), shared by player.json, stations.json
                               #   and the inventory snapshot; Import clamps to the capacity
      ChunkSaveStore.cs        # one world's chunk files + side cars: pending map, index, async writer thread
      ChunkSnapshot.cs         # immutable copy of a chunk's blocks, state bytes and player-placed cells
      ColumnFileCodec.cs       # an imported column file: magic CVXL, version, Deflate body of ColumnData
                               #   (heights, bed, water levels, biomes, dirt depth, corner climate)
      EntitySaveCodec.cs       # reads/writes entities.json
      EntitySaveData.cs        # the entities.json shape: mobs (health/facing/velocity/ammo) and drops
      ImportedMapWriter.cs     # writes an imported map (columns/*.col + chunks/*.bin); ExportGenerated seeds
                               #   one from any IWorldSource (tool/test use)
      LodEditState.cs          # lod-edits.bin: export/import of the LOD caps, surface overrides, canopy markers
      MobSaveCodec.cs          # a live Mob -> its EntitySaveData form (per-kind ammo)
      PlayerSaveCodec.cs       # reads/writes player.json
      PlayerSaveData.cs        # the player.json shape: pose, modes, health, clock, inventory + hotbar
      SaveNames.cs             # block/ and item/ namespaced content names, so saves never store raw ids
      StationSaveCodec.cs      # reads/writes stations.json
      StationSaveData.cs       # the stations.json shape: campfires, querns, kilns, cast molds (content by name)
      ServerInfo.cs            # one saved server: display name, host, port, last connected (servers.json)
      ServerPlayerSaves.cs     # a hosted world's per-player files: players/<name slug>.json via the store
      ServerStore.cs           # servers.json: list, add, delete, touch (unique names, most recent first)
      WorldInfo.cs             # one listed world: world.json metadata + its folder (slug fixed at creation);
                               #   SourceName = the raw world.json `source` (null when absent), Source = it
                               #   resolved (`generated` when unset), read by the dedicated server only
      WoodDamageCodec.cs       # reads/writes wood-damage.bin: the permanent chop damage of natural trunk cells
      WorldNames.cs            # name normalizing, the "Unnamed World" default, unique numeric suffix, folder slug
      WorldPersistence.cs      # World component: autosave, FlushAll, the player and entity capture and the
                               #   pending entity restore and the station restore (player.json, entities.json, stations.json, lod-edits.bin)
      WorldSeedParser.cs       # seed field -> seed: empty = random, integer verbatim, else FNV-1a hash
      WorldSources.cs          # the server's named world sources (generated, imported): Create(name, worldDir)
      WorldStore.cs            # the saves root: list (most recent first), create, rename, delete, touch;
                               #   writes `source` back only when set, so an unset one never appears
    Simulation/                # the main-thread simulations World owns: gravity, TNT, tree growth,
                               #   saplings, the stations (campfire, quern, kiln, casting), drops, the mob managers
                               #   and spawner; the fluids are in Fluids/ and leaf decay in LeafDecay/ below
      AlloyRules.cs            # GL-free crucible rules: which metals may share a vessel (CanMix), which melts next
                               #   (NextToMelt; alloy parts wait for their set), full sets -> the alloy (Resolve)
      CampfireManager.cs       # block-entity registry for campfires: lit branch piles with a per-branch burn timer
      CastingManager.cs        # one cast per mold cell: pour from a held crucible (the metal PourRecipe picks), cool, collect
      DropCollector.cs         # one player a drop may fly to on a multiplayer server
      DropManager.cs           # owns the live item drops: spawns them where blocks break, pulls nearby ones toward the player
      DropPickup.cs            # World component: the per-frame drop collection (the local player, or every
                               #   connected player on a server, plus goblin scavenging)
      FallingBlockManager.cs   # owns gravity-affected blocks: topples a falls block
                               #   into a FallingBlock entity when its support goes away
      Fluids/                  # the fluid simulation, namespace VoxelEngine.Simulation.Fluids
        FluidCells.cs          # the tick's cell access: its own ChunkCellCache plus the LodManager binding and the far-LOD
                               #   hook in Write
        FluidFlow.cs           # the one definition of which way a fluid flows
        IFluidCellReader.cs    # the cell read seam FluidFlow is generic over (the live world, the mesher's padded snapshot)
        FluidManager.cs        # the fluid tick's composition root and facade: the tick and its slicing, SetCellImmediate,
                               #   TickCell; the schedule, wake and Update entry points
        FluidMud.cs            # dirt beside settled water turning to mud
        FluidNeighborhood.cs   # the neighbor reads of the level rules: ComputeState, contact hardening, source count,
                               #   the Horizontals table
        FluidSchedule.cs       # the one set of cells waiting for the next tick and the Enabled gate on the edit paths
        FluidSpread.cs         # downward and sideways spread, the slope search, Flow (washing blocks away, arriving hardened)
        FluidState.cs          # encoding of the per-block state byte for fluid cells (source / falling / level 1-7)
        FluidWakeRules.cs      # pure, worker-thread-safe IsFlowTarget / IsCarvedFlowTarget / IsWakeTarget
        FluidWakeScanner.cs    # waking loaded chunks' water: the worker-found frontier and the border scan
      GoblinManager.cs         # goblins: AI tick, their thrown stones, melee damage at the impact
                               #   event, loot + carried flint on death, ring spawning
      HellMobManager.cs        # the hell mobs: AI tick, the brute's blow at its impact, the casters' fire bolts
                               #   (damage + burning), loot, despawn, the hell-only ring spawn (packs by layer)
      Icebergs/                # the iceberg simulation, namespace VoxelEngine.Simulation.Icebergs
        IcebergManager.cs      # the live bergs: forced and ring spawns, drift and push, the terrain/berg/body
                               #   collision, the life cycle, IBodySolids, the rider carry and push-out, client mirrors
        IcebergSpawner.cs      # where a berg may spawn: ocean at sea level, the band (injectable IcebergClimate),
                               #   the footprint clear and loaded, no target near
      KilnManager.cs           # kiln block entities keyed by anchor: fuel, ignition, the crucible
                               #   slot and its melt loop, plus the multiblock integrity sweep
      LeafDecay/               # leaf decay and the orphaned-wood crumble, namespace VoxelEngine.Simulation.LeafDecay
        LeafDecayManager.cs    # the composition root and facade: the scheduled cells, the tick and its slicing,
                               #   ScheduleAround/ScheduleBox from every edit path, TickLeaf/TickWood and the removal
        OrphanWoodCheck.cs     # the orphaned-wood test: the parent-chain fast path, the full TreeSupport search, frame memos
        TipHydration.cs        # the leaf hydration rule on world cells: best-first tip offsets, live tips, tip support
      MobDirector.cs           # World component: the per-frame mob job (the targets, the slime and goblin updates and
                               #   their damage, the blast shove, the mob list and its scenario spawn/damage seams,
                               #   the drain of status-effect block actions into the StatusEffectBlockWriter)
      MobSpawner.cs            # the shared dry-forest-surface ring spawn search
      QuernManager.cs          # block-entity registry for querns: one crushable unit per cell plus a one-rotation spin timer
      SaplingManager.cs        # the saplings planted in the world (an acorn, or any
                               #   ItemUseAction.PlantTree use), GL-free and main thread only
      SlimeManager.cs          # slimes: hop AI tick, contact damage, loot, ring spawning
      TntManager.cs            # owns the live primed TNT: spawns cubes where firestarter
                               #   strikes and blasts light TNT, ticks their fuses
      TopplingTree.cs          # a felled tree: the wood TreeSupport finds cut off plus its canopy, meshed once by
                               #   CellBatchMesher (one mesh per chunk it spans), swung flat around the cut, then popped out as drops
      TreeGrowthResult.cs      # the outcome of one TreeGrowthWriter call: its status,
                               #   the root cell, the added nodes and the written cells
      TreeGrowthStatus.cs      # what one TreeGrowthWriter.Grow call did
      TreeGrowthWriter.cs      # reactive growth on the live world: rebuild a column's tree from its wood
                               #   state bytes (or plant a sapling), GrowFurther in a WorldTreeSpace, write
                               #   the difference back; TreeGrowthResult.cs / TreeGrowthStatus.cs are its result
    Status/                    # status effects at run time (MG-4b), see "Status effect schema"
      StatusEffectSet.cs       # GL-free per-target set: apply (refresh, never stack), tick (DoT, block actions), the
                               #   lowest move/attack factor, the client mirror mask, the tint
      StatusEffects.cs         # the one Apply API (a Mob, an IMobTarget, a PlayerHealth), authoritative only
      StatusEffectBlockRule.cs # GL-free: which cell an ignite/freeze/thaw converts, and whether it holds something up
      StatusEffectBlockWriter.cs # the authoritative block writes with every edit hook, capped per pulse and per frame
    UI/                        # UI state, layout and rules, mostly GL-free (UIRenderer's page renderers in Rendering/Overlays draw them): canvas, font, settings
      BitmapFont.cs            # TrueType font baked into a GL atlas; CPU glyph layout (printable ASCII)
      GameSettings.cs          # player settings with JSON persistence (settings.json)
      Primitives.cs            # Vertex-layout mesh factories for simple shapes
      ScrollList.cs            # scrolled equal-height row math: clamp, visible range, row at a point
      TextWrap.cs              # GL-free word wrap against a measure delegate
      UiCanvas.cs              # the virtual UI canvas: reference size, uniform scale, scissor rects
      Hud/                     # HUD widgets: chat, clock, compass, crosshair state,
                               #   toasts, hotbar feedback, look label, tooltips, screenshots
        Chat.cs                # ChatKind/ChatMessage, ChatView (log/input/history) and ChatLayout
        Clock.cs               # pure clock math behind the HUD's held-clock readout (see TopStripRenderer.DrawClock)
        Compass.cs             # HUD compass heading math
        CrosshairState.cs      # throw charge, break progress and hit marker, handed to the UI pass
        DayToast.cs            # the dawn watch behind "Day N"
        HitMarker.cs           # the crosshair's connecting-hit X: its flash clock and fade
        HotbarNameFade.cs      # the selected item's name over the hearts after a switch
        HotbarPickupPop.cs     # the bump a hotbar slot gets when its count rises
        PickupFlyIcons.cs      # GL-free ring of collected-drop icons arcing into their hotbar slots
        HudToast.cs            # one hold-then-fade HUD line (the day toast, a first find, packs full)
        ItemTooltip.cs         # the hover panel's lines, built from the item's own fields
        LookLabel.cs           # the look-at label's rule: name, "Unknown", the mineable flag
        LookLabelState.cs      # what one frame's label draws: text, flags, cell, state byte, light
        ScreenshotPaths.cs     # where F2's capture lands and what it is called
        StaffSlotIndicator.cs  # GL-free, cached label of the held staff's selected spell slot and its parts (or
                               #   empty), drawn by HudRenderer.DrawStaffSlot right of the hotbar
      InventoryPanel/          # inventory, item stacks, hotbar, the [E] panel, crafting cards and session, creative catalog, search
        CraftCardLayout.cs, CraftCardLayoutCache.cs  # the crafting card's layout and its memo
        CrucibleComponent.cs   # what a crucible holds: one CrucibleMetal per molten material (molten + pending ml),
                               #   immutable, sorted, empty metals dropped; the last charged input
        CrucibleMetal.cs       # one metal in a crucible: material id, molten ml, pending ml
        CraftingSession.cs     # the crafting tab's open recipe, copies and fed reservations
        CreativeCatalog.cs     # GL-free browsable catalog of every item, grouped by pool and name-sorted
        HotbarLayout.cs        # hotbar strip geometry shared by the draw, its hit-test and the panel
        HotbarSlot.cs          # one hotbar slot: a stackable id or a tool instance (the default is empty)
        Inventory.cs, ItemStack.cs  # the three pools, the hotbar, stacks and their components
        InventoryCategory.cs   # which storage pool an id belongs to (Blocks, Tools, Misc, Crafting)
        InventoryCrafting.cs   # GL-free crafting rules behind Inventory's Craftable/CanCraftBatch/TryCraft forwarders
        InventoryPanelLayout.cs # [E] panel geometry, tabs, the search field, the catalog buttons, rows and scrollbar hit-testing
        InventorySaveCodec.cs  # Inventory's player.json mapping (Export/Import forwarders), HandledComponentTypes
        InventorySearch.cs     # GL-free matching of owned items and recipes behind the search field
        InventoryView.cs       # [E] panel selection/scroll/focus state; ResetForNewWorld between worlds
        StaffComponent.cs      # a staff's bound spells (part keys per slot) and selected slot, immutable
        PanelTransition.cs     # the [E] panel's open/close easing: eased alpha and slide from a self-clocked progress
        SearchFieldState.cs    # a dropdown search field's query, suggestion cursor and scroll
      Menus/                   # main, pause and settings menus, the loading screen, text fields
        LoadingScreen.cs       # LoadingScreenView (eased monotone progress, fade-out) + LoadingScreenLayout (spinner)
        MainMenu.cs            # main-menu pages, hit kinds, fields, keys, actions and MainMenuView
        MainMenuController.cs  # every main-menu page transition, key rule and WorldStore call (tested)
        MainMenuLayout.cs      # main-menu geometry + HitAt: buttons, scrolled world rows, fields, delete popup
        PauseMenu.cs           # pause menu pages, hit kinds, view and PauseMenuLayout (RESUME/SETTINGS/SAVE & QUIT)
        RespawnPrompt.cs       # GL-free death screen rule (shows after the death plays, one respawn request per death) + DeathScreenLayout
        SettingsMenu.cs        # settings page rows, tabs, sliders and SettingsLayout (shared by both menus)
        TextFieldState.cs      # one single-line text field: printable-ASCII typing rules and length cap
      SpellBuilder/            # the spell table's builder: GL-free state, catalog, layout and text (SpellBuilderRenderer draws it)
        SpellBuilderCatalog.cs # the four part columns (element, shape, modifier, trigger) and the known parts of a kind
        SpellBuilderHit.cs     # one hit-test result: kind, column, row or slot
        SpellBuilderHitKind.cs # None / Panel / Part / Slot / Bind / Clear
        SpellBuilderLayout.cs  # virtual-canvas geometry shared by the draw and HitAt: panel, slots, part rows, stats, buttons
        SpellBuilderText.cs    # the title, the hold-a-staff hint and a phrase per SpellVerdict
        SpellBuilderView.cs    # open flag, transition, chosen slot, the selection (one element/shape/trigger, any
                               #   modifiers), live stats cached per Version; Refresh rebuilds only on a knowledge change
    VisualTest/                # the VOXEL_SCRIPT scenario runner: parsed scenario, director, scripted keyboard, PNG capture
      ScriptedKeyboard.cs      # an IKeyboard whose pressed keys are set by code instead of the OS
      VisualCapture.cs         # reads the finished frame back from the GPU and writes it as a PNG
      VisualDirector.cs        # runs a VisualScenario inside the live game
      VisualScenario.cs        # a scripted in-game run:
                               #   teleport/look/wait/walk/mine/place/fly/give/spawn/grow/damage/effect/screenshot
                               #   steps loaded from JSON (VOXEL_SCRIPT)
    WorldGen/                  # procedural worldgen: terrain, biomes, caves and worms, the
                               #   tree front end and batches, clouds, the generational cache
      Biome.cs                 # the Forest / Desert / Ocean / Taiga / Frozen / Steppe / Savanna enum (appended, never reordered)
      BiomeClassifier.cs       # the biome climate split: SampleClimate (the one temperature/humidity reader, at the
                               #   ClimateWarp-displaced position), BiomeWeights
                               #   over the Classify/ColdOf/WarmOf/DryOf/SemiDryOf seams, WetnessOf, ColdWeight, WarmWeight
                               #   and the desert border dither (DitherBiome)
      BiomeTraits.cs           # shared biome yes/no rules the near fill and both far-LOD paths ask (ShowsBareRock,
                               #   RimsWaterWithSand)
      CaveNode.cs, Worm.cs     # one tunnel sphere (center + radius); a worm's node array plus its AABB
      ChunkTerrainFill.cs      # the worldgen fill of a fresh chunk (column fill, carvers, water, mud, lava, every feature
                               #   stamp, each gated by its StampBand, the fluid frontier scan; behind Chunk.GenerateBlockData)
      CloudField.cs            # the cloud layer as a 2D grid of cells, each holding 0..CloudMaxLevels stacked slabs;
                               #   BuildMesh also returns each cell's index-run start (cells are emitted x-major)
      ColumnData.cs            # per-chunk-column arrays: Heights (ravines cut in), DirtDepth, EntranceWeight,
                               #   CaveIntensity, Biomes, Bed (pre-ravine profile: decides wetness), WaterLevel,
                               #   CornerWarmth/CornerWetness (the climate at the column's tint lattice point)
      GenerationalCache.cs     # the shared eviction policy behind the deterministic worldgen caches (PERF_TODO D2)
      GreatLakeLobe.cs         # one disc of a great lake's outline, or its island (Radius 0 = none)
      GreatLakeSite.cs         # one great-lake cell's cached verdict: surface, floor, shore share, river-fed, river
                               #   ramp length, outflow channel, up to three lobes and an island (default = no lake)
      GreatLakes.cs            # the second, rare lake tier: hashed lobed sites on TerrainSites, the cached verdict
                               #   (SiteAt/BuildSite: rim walk, interior pass, relief overlap, river ramp and outflow,
                               #   sea-level fallback, island), the per-column Sample (weight, surface, floor, ravine
                               #   guard, river level, bank, outflow), RiverLevelAt, RampRiverBeside, RiverShare,
                               #   NearAnyFootprint
      NoiseCaves.cs            # 3D-noise caves (cheese rooms, wide/narrow spaghetti, deep caverns) on a strided lattice
      SplitMix64.cs            # deterministic integer-hash PRNG so worms don't depend on System.Random internals
      TerrainGenerator.cs      # the 2D column build: the palette, LandHeight (with the relief layer, trenches and ravines), Compute ->
                               #   ColumnData, Climate, and the column queries (forwarding to BiomeClassifier, WaterBodies, TerrainNoise);
                               #   ReliefLandHeight (no hashed peaks) and PreFeatureLandHeight (massifs, no lone peaks) for the site tests
      TerrainNoise.cs          # every 2D terrain noise channel, built only by Reseed (static ctor, WorldSeed.Apply); MakeNoise
      TerrainIslands.cs        # the ocean-gated island term folded into BiomeWeights (LandWeight: large and small
                               #   island noise) and the stateless hashed archipelago footprints
      TerrainPeaks.cs          # massifs and lone peaks: cached hashed sites, a pointed spurred massif, flatness-tested
                               #   lone peaks, the soft relief ceiling (SoftCeiling) under the clouds; sites accepted
                               #   all-or-nothing on the pre-island ocean weight, island sites scaled by island land
      TerrainRelief.cs         # the relief layer: highland weight (ReliefNoise), its lift and hill gain, the linear
                               #   mountain ranges (hashed per-cell direction, spine), the spawn clear
      TerrainSite.cs           # one accepted hashed site (center, radius, height, OnIsland; default = no site)
      TerrainSites.cs          # the shared hashed-site helper used by lakes, massifs and peaks: cell range, roll,
                               #   wobbles, rim samples, lowest-hash spacing
      TreeBatch.cs             # every tree of one 64x64 XZ cell grown together in one VirtualTreeSpace
      TreeBatches.cs           # the grown tree caches: solo shapes (one tree alone, per trunk) and the
                               #   64x64-cell batches grown together (BuildBatch), their profiler counters
      TreeCanopy.cs            # the far-LOD canopy: grown and crown-proxy canopy tops per chunk column, the
                               #   felled-canopy markers (CanopyDisturbed, PoisonCanopy), the crowns a reactive growth
                               #   added back (CanopyAdded, SetCanopyAdded) and their save export
      TreeGenerator.cs         # the tree worldgen front end: which columns root which species (worldgen
                               #   rules, TryGetTree), the reach and leaf tables, the cache reset; forwards
                               #   Stamp and the far-LOD canopy lookups (TryGetCanopy, PoisonCanopy)
      TreeStamper.cs           # stamping the batch shapes that cross a chunk into its blocks and state bytes
      WaterBodies.cs           # the surface-water model: WaterLevelAt (ocean, rivers at their own level, lakes on their own rim), lake bowls,
                               #   river channels, IsWaterColumn and the desert gate SurfaceWaterGate; lake candidates
                               #   roll through TerrainSites, their verdict held in the per-cell pond verdict cache
                               #   (PondSiteAt); SampleSurfaceWater composes ponds, rivers and great lakes
      WorldgenReads.cs         # test hook: a flag-gated counter of worldgen entries, noted on a ColumnCache
                               #   miss and in TerrainGenerator.Climate (off in production)
      WorldgenStageTimer.cs    # test/probe seam: per-stage wall time of ChunkTerrainFill.GenerateBlockData,
                               #   passed explicitly (null in production)
      Hell/                    # the realm under the barrier band (plans/LAYERS.md), namespace VoxelEngine.WorldGen.Hell
        HellGenerator.cs       # hell's endless layers: cavern pockets over hellrock slabs on a 3D lattice noise (seed
                               #   offset 163), pools ponding the floors' dips, the biome skins (Classify over the
                               #   open field); BlockAt equals the stamp, which owns every cell at or below HellTopY
        HellBiome.cs           # Ashlands / MagmaCaverns / FungalGrove, apart from the surface Biome enum
        HellBiomePalette.cs    # one hell biome's blocks (surface, soil, wall, ceiling, pool bed, lights), lava share
                               #   and flora (ground plant, vine rate, tree species and chance)
        HellBiomes.cs          # the hell biome of a cell (3D noise, seed 164, on a per-layer 2D lattice, plus a layer
                               #   lean), the pool lava pick (seed 165), the palettes, the salted cell hash
        HellFlora.cs           # the vines and ground plants of a pocket's air cell, part of HellGenerator's one rule
        HellTrees.cs           # hell trees: one per 8x8 cell and layer, a pure function grown in a HellTreeSpace,
                               #   cached, stamped into every chunk it crosses
        HellTreeSpace.cs       # the ITreeSpace a hell tree grows in: its own cell's columns, pocket air only
        IHellField.cs          # the open field, row rules and biome lookups the hell rules read (point or stamp)
      Sky/                     # the realm above the peaks (plans/LAYERS.md), namespace VoxelEngine.WorldGen.Sky
        CloudPatchGenerator.cs # the solid cloud patches in the CloudSlab band: a 2D patch noise (seed 166) outlines
                               #   them, its height over the threshold and a detail noise (seed 167) set each column's
                               #   cells (TryGetColumn, the one rule IsCloud and the stamp share); into air only
      WorldgenStats.cs         # lock-free worldgen timing from the generation workers and
                               #   the LOD builder, drained once a second by FrameProfiler
      WormCarver.cs            # worm-carved tunnels: per-XZ-cell deterministic paths, cached, carved sphere by sphere
      Features/                # every surface feature generator (see "Biomes"); the underground ones below
        BoulderGenerator.cs    # a rare 2-4 cell stone or cobblestone blob on the forest/taiga floor
        BranchPileGenerator.cs # crossed-stick branch piles: up to 4 long thin logs sharing one air cell above the ground
        CairnGenerator.cs      # a 3-cell cobblestone stack with a pebble hat on a local summit
        CottonGenerator.cs     # wild cotton clumps in the savanna (PlantClumps + a climate gate)
        DesertWellGenerator.cs # the desert's ring-and-roof well, dug into sand over sandstone
        FallenLogGenerator.cs  # a rare 3-5 cell hollow log of the biome's tree species lying on the floor
        FlowerGenerator.cs     # meadow flowers on warm, open grass
        FoliageGenerator.cs    # ground cover: a grass tuft on dry forest grass and a dead shrub on desert sand or tundra permafrost
        IceSpikeGenerator.cs   # rare single-column ice spikes on the frozen plains
        LilyPadGenerator.cs    # lily pads floating on ponds (PlantClumps)
        MeteorGenerator.cs     # a blast crater with scattered meteor rock
        OasisGenerator.cs      # a desert oasis: a water disc, a grass ring and palms (worldgen.oasis)
        PebbleGenerator.cs     # ankle-high stones in rare patches: 1-3 small boxes sharing one air cell above the surface
        PlantClumps.cs         # GL-free clumped-plant rule over a PlantClumpSpec (cell, jitter, radius, core, falloff)
        PlantClumpSpec.cs      # the shape of one clumped plant scatter for PlantClumps:
                               #   at most one clump per square cell of CellSize blocks
        ReedGenerator.cs       # shore reeds on the shallow water edge (PlantClumps)
        RuinedPillarGenerator.cs # broken stone columns standing on the surface
        SeaStackGenerator.cs   # stone stacks rising out of the sea near a shore
        SnowGenerator.cs       # cold weather: freezes surface water to ice (IsFrozenWater, the pack-ice band
                               #   the far LOD shares), lays one layered-snow cell per cold column (StampGround)
                               #   and dusts cold canopy tops (StampCanopy)
        StampBand.cs           # the world-y band a feature stamp can write (WriteBand); ChunkTerrainFill skips a
                               #   stamp whose band misses the chunk
        Underground/           # below the surface: ores, geodes, gravel pockets, lava, cave formations and mushrooms, hot springs, pools,
                               #   the barrier band
          BarrierGenerator.cs  # the barrier band over the overworld/hell border (BarrierBottomY..BarrierTopY): a gapless
                               #   core, patchy ramps on a 3D noise (seed offset 162); IsBarrier equals the stamp, stamped last
          CaveFormationGenerator.cs # stalactites and stalagmites in cave rooms
          CoalGenerator.cs     # coal ore veins: the same deposits as MalachiteGenerator, thresholding the
                               #   OreNoiseLattice field of its own noise
          GeodeGenerator.cs    # a hollow, crystal-lined sphere deep in solid stone
          GlowMushroomGenerator.cs # lit mushrooms on dry, dark cave floors
          GravelPocketGenerator.cs # gravel and sand pockets buried in stone (falls blocks, see "Gravel pockets"),
                                   #   thresholding the OreNoiseLattice field inside the burial band
          TinGenerator.cs      # tin ore veins: commoner than malachite, in the TinMinY..TinMaxY world-Y band, on the OreNoiseLattice field of its own noise (seed offset 144)
        HematiteGenerator.cs # hematite (iron) ore veins: deep stone only, at or below HematiteMaxY (the lava
                               #   band), rarer than malachite, on the OreNoiseLattice field of its own noise
          HotSpringGenerator.cs # steaming cave water bodies
          IOreLatticeRule.cs   # an OreNoiseLattice stamp's host test and replacement block (a struct generic argument)
          LavaGenerator.cs     # deep lava pockets: cave floors at/below LavaMaxY pond over inside patch mask + pool roll
          MalachiteGenerator.cs # malachite ore veins: sparse large blobs where the OreNoiseLattice field of a
                                #   low-frequency 3D OpenSimplex2 noise exceeds its threshold
          OreNoiseLattice.cs   # the shared world-aligned ore and pocket noise lattice (OreNoiseStride): trilinear,
                               #   a cell whose corners are all under the threshold holds none, Stamp equals the
                               #   Exceeds point query bit for bit
          UndergroundPoolGenerator.cs # still cave pools on carved floors
      Trees/                   # the tree growth engine (see "Trees")
        GrowthSignal.cs, TreeDirections.cs  # a pulse step, the six face dirs
        ITreeSpace.cs, VirtualTreeSpace.cs,
          WorldTreeSpace.cs, TreeCellReader.cs  # what a tree may grow
                                                #   into: worldgen heightmap view, live-world view, world reads
        WoodState.cs, LeafState.cs  # the wood and leaf meanings of the per-block state byte
        TreeGraph.cs, TreeGrower.cs  # the node graph; Pulse / GrowToMaturity / GrowSteps / GrowFurther
        TreeSpecies.cs, TreeSpeciesCompiler.cs,
          TreeSpeciesRegistry.cs, TreeWorldgenRules.cs  # compiled species, its validation, the registry, worldgen rules
        TreeSupport.cs, TreeSupportResult.cs  # which wood stands: paths to an anchor (felling, crumbling)
        Features/              # features[]: post-growth stages (roots, buttress) and their registry
          RootsFeature.cs, ButtressFeature.cs  # roots down into carved air; buttress fins
          ITreeFeature.cs, TreeFeatureRegistry.cs,
            TreeFeatureSpec.cs # features[]: post-growth stages
        Logics/                # growth.logic direction choosers and their registry
          OakGrowthLogic.cs, ConiferGrowthLogic.cs,
            CactusGrowthLogic.cs, BaobabGrowthLogic.cs,
            UmbrellaGrowthLogic.cs  # oak, conifer, cactus (pitchfork arms),
                                    #   baobab, umbrella (bare limbs) logics
          IGrowthLogic.cs, GrowthLogicRegistry.cs  # growth.logic: the direction chooser + its table
        Shape/                 # rasterizing a graph: cells, leaf clusters, trunk shapes and rims, the far-LOD crown proxy
          LeafCluster.cs, LeafClusterShape.cs  # leaves around live tips and their hydration
          LodCrownProxy.cs, LodCrownProfile.cs  # the far-LOD crown silhouette of a species
          ShellOffset.cs, TrunkShape.cs, TrunkRim.cs  # wide-trunk shells: offsets, footprint, rim style
          TreeRasterizer.cs, TreeCell.cs  # graph -> wood/shell/leaf cells with their state bytes
  assets/shaders/, assets/textures/   # GLSL shaders; block tiles (loaded into a TEXTURE_2D_ARRAY,
                                      #   one slice per file, at startup), item sprites, crosshair
  assets/textures/blocks/             # block tiles, one TEXTURE_2D_ARRAY slice per file in sorted name order
  assets/textures/blocks_material/    # index-parallel normal/metallic tiles (the material atlas); only the ores
  assets/textures/items/              # item sprites
  assets/textures/environment/        # sun, moon and the break-progress frames
  assets/textures/particles/          # particle sprites (bubble)
  assets/textures/ui/                 # HUD icons (hearts); crosshair.png sits in textures/ itself
  assets/fonts/                       # TTF for the UI text
  assets/sfx/                         # mined-block hit groups (<group>.wav / <group>_N.wav, see `sfx`), item pickups and
                                      #   the spell sounds (spell_cast, spell_impact, spell_fizzle; played by code, never a block)
  assets/sfx/ambience/                # the forest and night ambience beds
  assets/data/blocks/<name>.json      # one file per block; the file name is its string id
  assets/data/items/<name>.json       # vine, branch, flint, bucket, water_bucket, lava_bucket
  assets/data/loot/<name>.json        # stone, wood, leaves, pebble
  assets/data/tags/<tag>.json         # tool tags (axe, pickaxe) a block's `tools` requirement names
  assets/data/entities/<name>.json    # slime, goblin: display name, hp, loot table, optional model
  assets/data/trees/<species>.json    # tree species (oak, spruce, cactus, baobab, stone_pine): wood and
                                      #   leaves, growth, thickness, leaf cluster, features, LOD crown, worldgen
  assets/data/molten/<name>.json      # molten materials (bronze, copper, glass, tin): tile + tint the liquid is drawn with
  assets/data/recipes/<out>.json      # crafting recipes: result, count, ingredients
  assets/data/recipes/cook/<in>.json  # campfire recipes: input -> result after cookSeconds
  assets/data/recipes/crush/<in>.json # quern recipes: input -> crushed result
  assets/data/recipes/melt/<in>.json  # what a block or item melts into, and how many ml
  assets/data/recipes/cast/<out>.json # (fired mold + material) -> result, amount and cooling time
  assets/data/recipes/alloy/<name>.json # an alloy: result + parts (material, ml); a full set -> the summed ml of the result
  assets/data/spell/<kind>/<name>.json # spell parts by kind (element, shape, modifier, trigger): cost share,
                                      #   complexity, tags, modifier factors, an element's status `effect`
  assets/data/effects/<name>.json     # status effects (burning, frostburn): duration, damage over time, speed
                                      #   factors, block actions, tint, particle, HUD icon, death phrase
  assets/models/block/<name>.json     # Minecraft Java block models, incl. the built-in parents
                                      #   cube, cube_all, cube_column, cube_bottom_top
  assets/models/entity/<name>.bbmodel # Blockbench entity projects (slime, goblin with its four
                                      #   clips) + <name>.png skins
  assets/models/player/               # the player rig (player.bbmodel) and its skin
  visual-examples/                    # the VOXEL_SCRIPT scenario JSONs (see `VOXEL_SCRIPT`)
OpenGLTest.ItemDebugger/              # the item/entity/tree preview tool (ITEMS, ENTITIES and TREES tabs)
  VoxelEngine.ItemDebugger.csproj     # the project file (references the game project)
  GlobalUsings.cs                     # VoxelEngine.Core and the Shader alias
  AnimationClock.cs                   # the playback controls of the item debugger's timeline
  DebuggerTab.cs                      # which grid the item debugger's left panel shows, and
                                      #   so what the scene previews: the player holding an item
  DebugSceneRenderer.cs               # a minimal standalone renderer for the item debugger: content registry, block atlas
  EntityPreviewState.cs               # GL-free state of the ENTITIES tab
  ItemDebuggerProgram.cs              # standalone item-debugger entry point: a window showing the player model holding the selected item
  ItemDebuggerUi.cs                   # immediate-mode UI for the item debugger: an item grid on the left
  ItemDisplayEditor.cs                # editable copy of the selected item's display block plus the debugger-only renderer tweaks
  ItemJsonDisplayWriter.cs            # writes an item's display block back into its JSON file
  PlayerPreviewState.cs               # GL-free state behind the item debugger's player animations (the ITEMS tab)
  SliceTrailSampler.cs                # the delegate that poses the previewed blade for PlayerPreviewState's slice trail
  TreePreviewState.cs                 # GL-free state of the TREES tab: species, seed, step, per-step
                                      #   snapshots, ground plane and framing bounds
OpenGLTest.Server/                    # the dedicated CLI server (see "Multiple players and chat")
  VoxelEngine.Server.csproj           # the project file (references the game project)
  GlobalUsings.cs                     # VoxelEngine.Core and VoxelEngine.Config
  ClientConnection.cs                 # one connected socket: id, name, lock-guarded framed send, idle
                                      #   clock (LastInboundAt), the far-LOD regions queued for it
  GameServer.cs                       # the composition root: builds the components below, owns the
                                      #   public surface, Start/Stop and the main-thread Pump, and
                                      #   dispatches each inbound message (Handle)
  Inbound.cs                          # one frame a reader thread queued (a null payload: remove it)
  IntentReplay.cs                     # world and inventory intents replayed for their player and
                                      #   answered (BlockCorrection, InventoryState, IntentResult)
  PlayerSessions.cs                   # join (hello), PlayerState judging, status ticks, saves, unique
                                      #   names, the player list, leave and the idle sweep
  ServerBroadcasts.cs                 # the cell journal (BlockChanges), entities under stable ids,
                                      #   stations and the day clock
  ServerChat.cs                       # chat relay and the server's chat commands (over ChatCommands)
  ServerProgram.cs                    # content load, world/port prompts, headless World + tick loop
  ServerState.cs                      # what the components share: world, client and player tables,
                                      #   pending connections, clock, join spawn, creative rights
  ServerTransport.cs                  # TCP listener, accept thread, per-connection reader threads
  StreamRequests.cs                   # chunk, column and far-LOD region requests and the LOD queue
OpenGLTest.Tests/                     # xUnit tests, headless (no window); a test sits beside the area
                                      #   of the code it tests (by the type under test), a probe beside
                                      #   the area it probes, a helper of several areas in Support/;
                                      #   namespaces mirror the test folders (VoxelEngine.Tests.<folder>)
  VoxelEngine.Tests.csproj            # the test project (references the game, the server and the item debugger)
  GlobalUsings.cs                     # VoxelEngine.Core, VoxelEngine.Chunks, VoxelEngine.Content and VoxelEngine.Tests.Support
  Support/                     # cross-area fixtures: TestContent (the headless content load),
                               #   MeshTestWorld, MobTestWorld, FlatWorldSource, StampedTree,
                               #   WideTreeSpecies, TreeWorldgenFixture, IcebergTestWorld (a hand-built sea with a
                               #   pack-ice strip), StubTarget, BoxSolids and the three [CollectionDefinition]s
  Audio/                       # the sfx library and the ambience bed
  Chunks/                      # chunk storage, keys and map, the chunk manager and its mesh scheduling, the
                               #   column cache, fluid surfaces, lava vents, EntityLight; the replay probes
    Meshing/                   # greedy and per-voxel meshing, smooth light, the light volume, plant sway,
                               #   cell batches; the mesh-cost probe
    Sources/                   # the generated and imported world sources
    Streaming/                 # the generation workers, the scan frontier, the streaming foci
  Content/                     # the registry and its baked tables: blocks, models, shapes (panes,
                               #   tree wood, multiblocks), loot, recipes, kiln and molds, spell parts and
                               #   recipes, status effects and the status block flags (flat)
  Core/                        # the Core utilities and the src-root types (World lifecycle, WorldSeed, WorldLoadGate)
  Effects/                     # mirrors src/Effects: one folder per sub-domain of shader twins
    Climate/                   # the biome tint field and its chunk, LOD, pop and texture consumers
    Lighting/                  # the campfire flicker and the block-light tint code
    Post/                      # biome grade, contact AO, eye adaptation, godrays, underwater wobble
    Sky/                       # aurora, silver lining, moon, moonlight, sky palette, stars, glare, wind
    Surfaces/                  # glass, grass, ice, lava, leaf and ore twins, the snow sparkle shader term
    Water/                     # deep-water fog, the waterline fresnel, sun glitter, the shore foam shader term
  Entities/                    # the Mob base and the shared mob rules (caps, targets, notice, interest, fall,
                               #   lava, ledge, loot), clips and the animator, drops, projectiles, damage numbers
    Goblins/                   # goblin AI, aim and stones, flee, dodge, pack alert,
                               #   torches, the content contract (mirrors src/Entities/Goblins)
    HellMobs/                  # hell mob contracts, layer difficulty, brute blow, caster cast and bolt burn
    Icebergs/                  # iceberg shape, motion, wobble, spawn band, contact, the berg (mirrors src/Entities/Icebergs)
    Slimes/                    # slime sizes, hops, knockback, split and merge (mirrors src/Entities/Slimes)
  Graphics/                    # shader defines and the chunk-draw shader guard, cutout mips, tile materials
  Interaction/                 # raycast and targeting, mining, combat and the slice combo,
                               #   placement, support, doors and stairs, multiblocks, the selection
                               #   outline, chat commands; its own helper SupportTestWorld
  ItemDebugger/                # the OpenGLTest.ItemDebugger project: the timeline, the
                               #   preview states, the display editor and its JSON writer
  Items/                       # item uses (acorn, converter, firestarter, heal, grow-tree,
                               #   timed use), the staff slot binding and the hierarchy; its own helper StubItemUseWorld
  Juice/                       # game-feel rules: blast shake, damage direction and fringe,
                               #   hit-stop, the vignette, pops, FOV and tilt, droplets, tool shatter
  Lod/                         # far-LOD regions, the manager, build pool and buffers, column caps, surface
                               #   overrides and their save state, sky exposure; the build-cost probe
  Magic/                       # the cast rules, pacing and held-staff lookup, the spell effects (bolt flight,
                               #   impact rules, the element impacts on a server world), the sparks, the cast
                               #   feedback and a client's preview bolt; its own helper StaffFixture
  Networking/                  # protocol and codecs, end-to-end chunk and imported-world streaming
    Client/                    # client streaming, the mesh gate, predictions and intent flow, the entity
                               #   mirror, remote players, worldgen isolation
    ServerSide/                # the server player and its status, movement and intent rules, operators,
                               #   the loopback GameServer and the server-mode World
  Particles/                   # every particle system and the dust, splash and pop emitters
  Player/                      # camera, physics, water phases and swimming, health and fall tracking,
                               #   the player model, creative, sprint, the saved-player restore
  Rendering/                   # render-pass gates: GPU timing, reflection reuse, shadow cascades, the texture-unit cache,
                               #   the Hi-Z occlusion twins (HizOcclusionTests)
    Atmosphere/                # cloud copies, cover and shadow
    Geometry/                  # the geometry allocator and the LOD pool; the geometry-layout probe
    Overlays/                  # healthbars, text batching, the HUD text cache
    ScreenSpace/               # the half-res target
  Saves/                       # codecs and stores of world, chunk, player, entity
                               #   and server saves, names, seed parsing, world sources
  Simulation/                  # the main-thread simulations: fluids, falling blocks, leaf decay, tree growth
                               #   and felling, saplings, the stations, drops, the mob managers and spawning
    Icebergs/                  # the iceberg manager, its spawner and icebergs in a headless World
  Status/                      # the status effect set, the Apply API, the speed factors and the block rule and writer
  UI/                          # the canvas, settings, scroll lists, text wrap
    Hud/                       # chat, clock, compass, hit marker, hotbar feedback,
                               #   toasts, tooltips, the look label, screenshot paths
    InventoryPanel/            # the inventory and its save shape, the [E] panel layout, crafting cards, the creative catalog, search
    Menus/                     # main and pause menus, the loading screen, text fields
    SpellBuilder/              # the builder's selection rules, live stats and layout hit-tests
  VisualTest/                  # scenario parsing and ops, PNG capture
  WorldGen/                    # terrain, biomes, lakes, ravines, clouds, the generational
                               #   cache, the tree front end and batches, worldgen stats,
                               #   the worldgen cost probe and the stage-timer tests
    Features/                  # every feature generator (mirrors src/WorldGen/Features)
      Underground/             # coal, malachite, hematite, gravel pockets, cave life (mirrors src/WorldGen/Features/Underground)
    Hell/                      # the hell caverns, biomes, palette and flora (mirrors src/WorldGen/Hell)
    Sky/                       # the solid cloud patches (mirrors src/WorldGen/Sky)
    Trees/                     # the tree growth engine, flat over src's Trees/Shape, Logics and Features
tools/                                # asset generators: tiles, sprites, models and clips, each re-run byte-identical
  generate-acorn-texture.py           # deterministic generator of the acorn sprite (acorn.png)
  generate-bandage-texture.py         # deterministic generator of the bandage sprite (bandage.png)
  generate-cave-plants.py             # deterministic generator of the glow mushroom tile (glow_mushroom.png)
  generate-clock-texture.py           # deterministic generator of the clock sprite (clock.png)
  generate-cotton-textures.py         # deterministic cotton_plant tile and cotton item sprite (stdlib PNG writer)
  generate-crystal-texture.py         # deterministic generator of the crystal tile (crystal_block.png)
  generate-crushed-iron-texture.py    # deterministic generator of the crushed iron sprite (crushed_iron.png)
  generate-door-textures.py           # deterministic oak trapdoor (and door) tiles (stdlib PNG writer)
  generate-effect-icons.py            # deterministic 16x16 HUD icons of the status effects (effect_*.png), --check
  generate-goblin-clips.py            # deterministic generator of the goblin's animation clips (goblin.bbmodel)
  generate-goblin-skin.ps1            # deterministic generator of goblin.png; its header pins the UV layout
  generate-hell-mob-models.py         # deterministic hellbrute/cinderwitch .bbmodel (rig + clips) and skins, and the
                                      #   ember_shard sprite; its header pins the rig and clip contract, --check
  generate-hollow-log-textures.py     # deterministic placeholder inner/end tiles of the hollow logs
  generate-iceberg-models.py          # deterministic iceberg_{small,medium,large}.bbmodel and iceberg.png; its header pins
                                      #   the geometry rules (waterline, shelf, 8 px inset steps) and the UV layout
  generate-lantern-texture.py         # deterministic lantern tile; its header pins the uv of every model part
  generate-metal-textures.py          # deterministic tin/copper-sword/bronze/iron-pickaxe item art and the tin ore tiles, recolored from the copper tools, the iron sword and malachite
  generate-leaf-textureless.ps1       # deterministic gray leaf tiles for the biome tint (scaled to BiomeTintTextureGray)
  generate-permafrost-texture.py      # deterministic generator of the permafrost tile (permafrost.png)
  generate-plank-texture.py           # deterministic oak_planks tile (stdlib PNG writer)
  generate-pond-plants.py             # deterministic generator of the pond plants' art
  remove-copper-head-handles.py       # strips the wooden handle from copper_pickaxe_head.png / copper_axe_head.png (re-runs are no-ops)
  generate-slime-block-texture.py     # deterministic generator of the slime block tile (slime_block.png)
  generate-staff-textures.py          # deterministic placeholder sprites of the wooden and crystal staffs
  generate-spell-sfx.py               # deterministic placeholder spell sounds (spell_cast/impact/fizzle.wav), --check
  generate-mana-draught-texture.py    # deterministic placeholder sprite of the mana draught (mana_draught.png), --check
  generate-stone-brick-texture.py     # deterministic generator of the stone brick tile (stone_bricks.png)
  generate-barrier-texture.py         # deterministic generator of the dark barrier tile (barrier.png)
  generate-hell-textures.py           # deterministic hell block tiles from a list of tile specs (hellrock, ash,
                                      #   cinder_soil, scorched_hellrock, basalt, magma_block, ember_crystal, fungal_*,
                                      #   the hell logs, the fungal cap and the cutout plants and vine;
                                      #   Pillow, plain `python`), --check
  generate-sky-textures.py            # deterministic sky block tiles from a list of tile specs (cloud_block: a soft
                                      #   white mottle with mottled alpha; Pillow, plain `python`), --check
  generate-alloy-bricks-texture.py    # deterministic alloy brick tile (alloy_bricks.png), mud_bricks.png recolored bronze and gray
  generate-tree-textures.ps1          # placeholder baobab / stone pine log and leaf tiles tinted from the oak art
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
| `softLanding` | false | a body landing on the block takes no fall damage (no spring back, unlike `bounce`); read over the footprint under a landing player's feet (`PlayerBodyProbe.LandingCushioned`), in singleplayer and on the server. Illegal on a fluid |
| `sink` | 0 | the top share of the cell, 0..0.5, a player body sinks into slowly (`PlayerConfig.SinkBlockSpeed`) until it stands on the solid part below (`SinkLayer`); the body counts as grounded in the layer. Only on a plain solid full-cell collider: refused on a fluid, a non-solid or pass-through (`movementSpeed`) block, a stack, tree wood, a `collidesByHitbox` block and a `bounce` block. Player body only |
| `footstepDust` | false | loose ground: a player sprinting on foot over it kicks up a tiny puff of its chips at every footfall (`FootstepDust`, `GroundDust`; sand, snow, dirt and gravel set it, grass does not). Illegal on a fluid |
| `occludes` | full cube && !cutout | hides a neighboring full cube's face (`OccludesNeighbors`) |
| `cullSelf` | false | also cull faces against a neighbor of the same block (glass) |
| `castsAO` | `solid` | darkens neighboring faces (`CastsAmbientOcclusion`) |
| `cutout` | false | alpha-tested texture; forces `occludes` off whatever the file says |
| `translucent` | false | drawn blended in the sorted translucent pass with the fluids (glass); forces occludes off; never casts a shadow |
| `sway` | false | leaf-wind sway in the vertex shader. A full cube (leaves) sways uniformly; a non-full-cube `model` (a plant) is rooted: its sway eases from 0 at the model's lowest vertex to full at its highest (`BlockType.SwaysFromRoot`/`SwayWeightAt`, carried in the vertex alpha) |
| `biomeTint` | none | color the block by climate (see "Biome-tinted grass and leaves"): `{"palette": p, "faces": {face: entry}}`, where `palette` is `grass` (default) or `foliage`, a face key is `north`..`down`, `side` or `all`, and an entry is `{"tile": t}` or `{"overlay": t, "base": b}`. `tile` replaces the face with a tinted tile, `overlay` draws a tinted cutout tile over it (`base` optionally swapping the tile beneath). Only on a block that bakes to a plain full cube and is not translucent; `overlay` not on a cutout block. `FaceTiles` stay the default tiles for icons, drops and the held block |
| `modelTint` | none | the sub-cell sibling of `biomeTint`: a palette name (`grass` default, or `foliage`) that tints **every** quad of the block's own baked model (`shade * the field`, bit 4 set) instead of per cube face. Only on a non-full-cube `model`-renderer block that is not translucent (a cross-shaped plant), and the model must wear a gray `_textureless` tile, since the tint multiplies the texel. The `tall_grass` / `dead_shrub` plants use it |
| `material` | none | reflective surface tuning for the ores: `metallic` (0..1, default 0 - an ordinary diffuse block), `roughness` (0..1, default 1, lower being a tighter highlight) and `normalStrength` (0..4, default 1, scaling the tile's normal map). Omitting the object costs nothing at render time. Keyed by *texture*, not by block - see the material atlas below |
| `light.emission` | 0 | 0..15 (`LightEmission`) |
| `light.color` | warm torch color | sRGB `[r, g, b]`, 0..255 each, of the block's light; only legal with `emission` > 0. Colors within 0.5/255 share a `BlockLightPalette` slot, and at most 8 distinct colors exist (the default included) - a ninth throws naming the file. `blue_lamp` is the example; five are in use (default, blue lamp, glow mushroom, the hell ember and fungal teal) |
| `light.passes` | `!fullCube` | whether voxel light travels through the cell (`PassesLight`) |
| `flame` | none | `[x, y, z]` in 0..16 model units: where a small flame burns (a torch's head, half a pixel above its stick). Authored facing north and pre-turned with a `facing` block (`BlockType.FlamePoints`, `FlamePointAt(state)`); the mesher records each cell's point in `Chunk.FlamePoints` and the world burns a flicker of embers and a wisp of smoke there (see "Animation juice"). Only on the `model` renderer |
| `renderer` | `"model"` | `model` (baked quads), `tree_wood` (a grown tree's wood: its shape, thickness and tree-graph links come from the state byte, see `### Trees`; requires a `wood` object, forces `occludes` and `castsAO` off and `light.passes` on, never meshes greedy, and refuses `cutout`, `translucent`, `fluid`, `falls`, `needsSupport`, `stackMax` and `hitbox`), `pebbles`, `branch_pile`, `snow_layers`, `fluid` or `quern` (procedural emitters), `multiblock` (the anchor cell of a structure, carrying the whole volume's pre-rotated model), `multiblock_part` (one of its other cells, solid and occluding but never meshed) or `pane` (a 2 px post plus an arm toward every horizontal neighbor that is a pane or a solid full cube, derived at mesh time by `PaneEmitter` from `PaneShape`, never stored; a lone pane is just the post, as in Minecraft; the raycast, outline and body collide with the same arms through `BlockShape`; refuses `hitbox`, `fluid`, `falls`, `stackMax` and `needsSupport`, and a solid pane always collides by its arms); a fluid's, layer block's or pane's model only supplies its tile (and the pane's icon) |
| `multiblock` | none | required on, and only legal with, `renderer: "multiblock"`: `{"part": "block/<name>", "source": "block/<name>", "size": n}` — the block filling the other cells (which must use `multiblock_part`), the plain block the volume is built from and reverts to, and the volume's edge length in cells, `size` 2..4 (default 2, the kiln; `BlockType.MultiblockSize`, `BlockSchemaParser.ParseMultiblockSize`; the state byte holds each offset in two bits, so no more). The anchor and its part must both set `hasItem: false` (a structure is built, never held), and `cutout`/`translucent`/`fluid` are rejected, since both halves are ordinary opaque solids to everything but the mesher. Both default `occludes` and `light.passes` to **true**: the structure's surface really does cover its neighbors, and its quads carry no cullface and are lit from the anchor's own cell, so light has to reach that cell or the whole thing renders black. The model is authored in 0..16 * size model units (0..32 for a 2x2x2) with the opening on **north**, no `cullface` on any face and no face covering a whole cell plane — see `### Multiblocks` below |
| `kiln` | none | marks a multiblock anchor as a kiln, which is what gives it `KilnManager`'s fuel, ignition, crucible slot and integrity sweep, and tunes it: `{"fuelCap": n, "meltSpeed": s, "canAlloy": b}`, every field optional (`{}` is the plain kiln). `fuelCap` 1..64 (default 4) is how many fuel units the hearth holds, `meltSpeed` above 0 and at most 8 (default 1) divides the melt interval (`KilnProfile.MeltSeconds` = `BaseMeltSeconds / meltSpeed`), `canAlloy` (default false) lets the crucible hold two metals and alloy them (see "Recipes, cast molds and metalworks"). Compiled into `BlockType.Kiln` (a `KilnProfile`, null on a non-kiln) together with the `KilnLayout` of the anchor's `multiblock.size` (`KilnLayout.ForSize`: 2 is the mud brick kiln's model, 3 the alloy furnace's), so a kiln's model must follow the layout of its size and a size with no kiln layout is refused; an out-of-range value throws naming the file. Only legal on the `multiblock` renderer |
| `spellBuilder` | false | the block is a spell table: a right-click with a staff in hand opens the spell builder (`BlockType.SpellBuilder`, `BlockInteractions.TryOpenSpellTable`, after the other stations and before the staff cast; a block in hand places against it instead). No block entity, no state; legal on any block. `spell_table` |
| `pane` | glass pane | only legal with `renderer: "pane"`: the post and arm dimensions in pixels, `{"post": p, "arm": a, "rails": [[bottom, top], ...]}` (`PaneProfile`): a full-height post `p` (1..16) wide, arms `a` (1..`p`, default `p`) wide filling each `rails` height band (ascending, apart, inside 0..16, default `[[0, 16]]`). Omitted is the glass pane's 2 px post and arms over the full height, drawn byte-identically to before. Panes join only panes of the same dimensions (interned, so equal dimensions are one `PaneProfile`) and solid full cubes, so a fence never reaches into a glass pane. `oak_fence` is `{"post": 4, "arm": 2, "rails": [[6, 9], [12, 15]]}` |
| `wood` | none | required on, and only legal with, `renderer: "tree_wood"`: how hardness and loot follow the thickness stored in each cell's `WoodState` (code 0 = 1 px across .. 15 = full cube) |
| `wood.tipHardness` | 0.25 | the share of `hardness` the thinnest (1 px) wood keeps, 0..1, rising linearly to the full `hardness` at a full cube (`BlockType.TipHardness`, read by `Mining.HardnessOf`). Player-placed tree wood is a full detached cube and therefore as hard as a trunk |
| `wood.loot` | always drop self | thickness bands `[{"minThickness": n, "loot": l}, ...]`, strictly ascending by `minThickness` (0..15) and starting at exactly 0, so every thickness has loot; `loot` is `self` (drop the block, the default), `none` or a loot table name. The band with the largest `minThickness` at or below the cell's thickness is rolled (`WoodLootBand.Select`, through `LootContext.State` on every destruction path): `oak_log` gives the `wood` table (a stick half the time) under 8 px and itself from there, `cactus` always itself |
| `stackMax` | 0 | units that stack in one cell (pebble piles 3, branch piles 4, snow layers 16): the count lives in the chunk state byte, 0 = untouched worldgen (the generator's own default), 1..`stackMax` an explicit stack. 2..16 |
| `stackSolidAt` | 0 | stack count at which a stack block turns solid, for a block whose partial stacks are walked through (layered snow at 16). Only legal from 1..`stackMax` on a `solid: false` stack block |
| `hasItem` | true | enters the blocks pool and can be hotbarred; false for pebbles, branch piles, layered snow and the kiln's anchor and part (fluids force it off). Every `tree_wood` log and every leaf block keeps its item: placing one writes a detached full cube (`WoodState.Detached`) or a persistent leaf (`LeafState`), which never fells with a tree or decays |
| `needsSupport` | false | pops, rolling its loot, when the block holding it up is emptied (the block below, or behind it with `support: "attached"`; `BlockSupport`), and is only placed against a solid holder; also makes the block replaceable, so a flow washes it away. Mining, blasts, felling and leaf decay pop it; a toppling falls block does not yet |
| `flowResistant` | false | a `needsSupport` block a flow may not wash away (a cave formation is rock, not vegetation): the fluid tick treats the cell as a wall and only mining the block under it pops it. Only legal with `needsSupport` |
| `support` | `"below"` | which neighbor holds a `needsSupport` block up: `below`, `above` (the cell over it: a hanging lantern, so it is placed against a block's underside, or wherever a solid block stands over the target cell; `BlockType.SupportAbove`; any non-air holder holds it, so a generated `hell_vine` hangs cell under cell, and emptying any cell pops everything hanging below it, by mining, a blast or leaf decay) or `attached` (the cell behind its front, opposite the `facing` in its state byte: a wall torch). Only with `needsSupport`; `attached` also needs `facing` |
| `facing` | none | `"horizontal"`: the state byte's low two bits are a facing (north -Z 0, south +Z 1, west -X 2, east +X 3); the model (no full-face quads) and `hitbox` are authored facing north and pre-turned to all four (`BlockType.RotatedModels`/`RotatedHitboxes`, `HorizontalFacing`). Only on the `model` renderer, never with `fluid`, `falls` or `stackMax` |
| `facingFromLook` | false | a placement faces the block along the placing player's horizontal look (`Placement.LookFacing`: the dominant horizontal axis of `PlayerContext.ViewDirection`, so a server replays a `Place` intent by that player's own aim), whatever face was hit: stairs placed looking north climb north. Only with `facing`, never with `support: "attached"` |
| `open` | none | makes the block openable: `{"model": m, "hitbox": h}`, both required, authored facing north like the closed model and hitbox. Right-click (`BlockInteractions.TryToggleOpenable`, in the right-click chain after the stations) flips `DoorState.OpenBit` in the state byte whatever is in hand, and the block is then drawn with `m` and hit (and, with `collidesByHitbox`, collided) with `h`; `RotatedModels`/`RotatedHitboxes` hold the four closed facings then the four open ones. Only with `facing`. `oak_trapdoor` |
| `upperHalf` | none | makes this block the lower half of a block two cells tall (`TallBlock`): the named block fills the cell above it. Placement writes both halves with one state byte and is refused, spending nothing, unless the upper cell is free too; breaking either half takes the other down through the hooks mining runs and drops this block once; an openable pair opens and shuts together. The upper half needs `hasItem: false` (it is picked as this block, `BlockType.PickId`), the same `facing` and `open` as this block, and neither half may be a fluid, fall, need support or stack. `oak_door` -> `oak_door_upper` |
| `wallBlock` | none | the block a placement against a block's side becomes, facing the hit normal (leaning away from the wall); on top the block places as itself, under a ceiling it is refused (`Placement.TryMount`). The wall block needs `facing` and `support: "attached"`, and is picked as this block (`BlockType.PickId`). `torch` -> `wall_torch` |
| `pickAs` | none | the block a middle-click picks instead of this one (`BlockType.PickId`, through the same `ItemBlockId` a `wallBlock` and an `upperHalf` set): a worldgen-only block picked as its plain counterpart. Requires `hasItem: false` on this block and names another registered block that has an item; a block that is already some block's `wallBlock` or an `upperHalf` may not set it (`BlockLoader.ResolvePickAs` refuses each case naming the file). A misspelled field name is ignored silently, like every unknown field. `hollow_oak_log` -> `oak_log` |
| `replaceableByPlacement` | false | a block placement aimed at this block fills the block's own cell and discards it, instead of filling the cell across the hit face, so a block placed against a partial snow drift rests on the ground instead of hovering a layer above it. Only fires while the cell is not solid, so a full snow stack stays an ordinary block, and the displaced block drops nothing. Independent of `needsSupport`, which is what a flow or a bucket may overwrite. Illegal on an always-solid block, which could never honor it |
| `falls` | false | gravity-affected (sand, gravel): topples into a `FallingBlock` entity when the cell below stops being solid, sinks through water, slides off lone supports; never combines with `fluid`/`needsSupport` |
| `fluid` | false | the block is a flowing fluid (water, lava): forces `solid`, `castsAO`, `hasItem`, `needsSupport` off, `occludes` off and `light.passes` on, and drops nothing whatever the file says |
| `flow` | water behavior | per-fluid tuning, only legal with `fluid: true`: `tickDivisor` (ticks between updates, lava `2` = half water speed), `range` (sideways spread 1..7, lava `3`), `infinite` (adjacent sources birth new ones, lava `false`), and the `hardensTo` + `hardensTouching` pair (lava hardens to `cobblestone` on touching `block/water`) |
| `flammable` | false | a status effect with `blocks.ignite` burns the block away to air (no loot, no fire block) when it ticks beside it (`BlockType.FlammableById`). Illegal on a fluid and on a multiblock anchor or part. The leaves and the ground plants set it; planks, logs, TNT and stations do not |
| `freezesTo` | none | the solid block a fluid **source** freezes into under `blocks.freeze` and a spell's `freeze` impact (`BlockType.FreezesToById`; water -> `ice`, lava -> `cobblestone`). Only legal on a fluid, and the target must be a registered non-fluid block |
| `meltsTo` | none | the fluid a block thaws into, as a source, under `blocks.thaw` (`BlockType.MeltsToById`; ice -> `water`). Only legal on a non-fluid, and the target must be a fluid other than the block itself |
| `onBreak` | `"drop"` | `drop` (roll loot) or `prime_tnt` (no drop, spawns a `PrimedTnt`) |
| `loot` | the table sharing this block's name, else drop self | loot table name; `"none"` drops nothing |
| `sfx` | `hit_default` | sound group played while the block is mined, e.g. `hit_rock` (the stone blocks) or `hit_wood` (the logs and cactus). A group is every `<group>.wav` / `<group>_N.wav` directly under `assets/sfx`, and one variant is picked at random per hit (`SfxManager`). An unknown group warns yellow and uses the default |
| `hitbox` | full cube | selection boxes in cell-local 0..1 space: one `{"min": [x,y,z], "max": [x,y,z]}` object or an array of them; the outline draws all, the raycast only targets through one |
| `collidesByHitbox` | false | the body collides with the declared `hitbox` boxes instead of the full cell, the way tree wood collides with its drawn node and tubes (a 0.25 slab is stepped onto and stood on; `BodyCollision.BodyCollides` -> `TreeWoodShape.OverlapsShape`, which tests the registered hitboxes for every block that is not thin or beveled tree wood); requires `hitbox`, `solid: true`, no `stackMax`, not a fluid. Player body only: drops, slimes and falling blocks still see the full cell |

Two traps worth repeating: a **model that is not a full cube needs `light.passes: true`** (which
is its default) or its own cell stays dark and the interior quads render black; and a **cutout
block must not occlude**, which the registry enforces rather than trusting the file.

Partial-block textures are **cropped, never stretched**: the chunk mesher samples `FaceTiles`
crops (`ChunkFaceTables.TiledUv`/`CropUv`), and the mining pop draws the hitboxes the same way
(`PopBoxEmitter`), so a new partial block needs no pop work as long as its `FaceTiles` are
sensible (the baker falls back to the first quad's tile).

### Multiblocks

A multiblock is a **cubic volume of ordinary blocks**, `size` cells along every edge (the anchor
block's `multiblock.size`, 2..4, `BlockType.MultiblockSize`; the kiln is 2x2x2, the alloy furnace
3x3x3), not an entity:
the **anchor** — always the volume's minimum corner on every axis — holds the `multiblock` block
and draws the whole structure, and every other cell holds its `multiblock_part`, which collides,
occludes and raycasts but is never meshed. Everything else in the engine keeps seeing plain solid
cells. Every structure rule takes the size from the anchor's type: `Multiblock.Offsets(size)` (the
cells as offsets, the anchor first) drives `Convert`, `Revert`, `IsIntact` and the editor's and
kiln sweep's per-cell hooks, and `MultiblockPlacement.TryFindVolume` and `MultiblockShape.Rotate`
take it as an argument.

The structure lives in the **per-block state byte**: `Multiblock.EncodeState(facing, dx, dy, dz)`
packs the facing into bits 0-1 and each offset (0..3) into two bits, the low bits of dx, dy, dz
in bits 2, 3, 4 and their high bits in bits 5, 6, 7 — so a 2x2x2 cell reads exactly
`facing | dx<<2 | dy<<3 | dz<<4`, the kiln's bytes from before sizes existed. The facing sits in
the low two bits of *every* cell (the anchor's offset is 0,0,0, leaving just the facing) and one
state read resolves any cell to its anchor — `Multiblock.TryResolveAnchor` subtracts the offset
and then checks the candidate really is an anchor that owns this part id, agrees on the facing
and is big enough to hold that offset, so an orphaned part left by a blast reports false instead
of pointing at a stranger. `facing` is the engine face index of the
opening's outward normal (north -Z = 0, south +Z = 1, west -X = 2, east +X = 3), and the anchor's
model is baked **four times** at startup into `BlockType.RotatedModels` (`MultiblockShape.Rotate`,
a quarter turn about the vertical axis through `(size / 2, y, size / 2)`, which maps the volume
onto itself),
so the mesher picks a pre-rotated model instead of rotating anything per frame. That rotation is
also why a multiblock face may carry no `cullface` and may not cover a whole cell plane
(`ModelBakeSteps.ValidateMultiblockBake`): its quads are culled against nothing and the
per-corner AO path indexes neighbor deltas by `Chunk.CubeFaces` corner order, which rotation
breaks, so a multiblock shades flat on every facing.

**Conversion** is a right-click with an item whose `use.action` is `convert_multiblock`
(`ConvertMultiblockItem` -> `BlockEditor.TryConvertMultiblock` -> `MultiblockPlacement.TryFindVolume`).
The converter names no structure: it tries every multiblock anchor in id order with that anchor's
own source block and size, and the first whose volume the clicked cell belongs to is converted
(2x2x2 `mud_bricks` into the kiln, 3x3x3 `alloy_bricks` into the alloy furnace), so a new structure
needs no converter edit, and no two structures may share a source block. The clicked cell lies
on the volume's front face and the volume always extends **into** the face that was hit, away
from the player; which of the volumes whose front face holds that cell is taken is decided in
screen terms, where `right = Cross(screenUp, hitNormal)`: the one reaching farthest right wins,
then the one reaching farthest down — for a 2x2x2 the order **right-down, right-up, left-down,
left-up** over the four volumes the clicked corner belongs to. The first candidate whose cells are
all the source block wins, so building a neat cube and clicking any of its front cells (a corner,
an edge or the center) converts that cube. A cell **inside** the volume, touching none of its faces
(`Multiblock.IsInterior`: the 3x3x3's center, nothing in a 2x2x2), may be air or the source block,
so a hollow shell converts as well as a solid cube; it becomes a part cell either way, a source
block built there is handed back as a drop in front of the clicked face (never on a client, whose
convert is a prediction), and a revert leaves the interior empty, so a convert and a revert make
and lose nothing. The
opening ends up facing the player: on a side face that is the hit normal itself, and on a top or
bottom face (where the normal says nothing about where the player stands) it is the player's own
horizontal view direction, reversed.

**Mining any cell** reverts the rest (`BlockEditor.RemoveBlock` -> `RevertMultiblock` ->
`Multiblock.Revert`): the mined cell is cleared and rolls the structure's own loot table (the
kiln's yields the one `mud_bricks` that cell was built from, the furnace's one `alloy_bricks`), and
every other shell cell still holding the anchor or part id becomes the source block again with a
**zeroed** state byte, the interior air.
Both `Convert` and `Revert` write through `Chunk.SetBlockRaw` and only collect the chunk
positions they touched, because the remesh follows the `BlockEditor.FellWood` batch idiom — one
`ChunkManager.ImmediateRemeshWithBorders` on the clicked or mined cell's chunk so the click frame
already shows the change, and `Chunk.RemeshAfterBatchEdit` (the asynchronous `MarkAreaDirty`
rebuild a felled tree and a TNT crater already use) on the rest — never a loop of immediate
remeshes.

Nothing but mining goes through `BlockEditor.RemoveBlock`, so a **blast** would otherwise leave the rest of a
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
bits (`BlockPlacer.PlaceBlockAtFace` takes the view from the placing player, the item-use path from
`WorldItemUse.ActiveContext`), so the step rises the way the player looked. The `block/stairs` parent is
authored facing north (tall step over the north half) and split into three boxes so no face
covers a whole cell side (`ModelBakeSteps.ValidateFacingBake`); the two `hitbox` boxes turn with it
(`RotatedHitboxes`) and the body collides with them, so the walk step climbs them. On a client
in third person the predicted facing reads the camera's view while the server reads the aim,
so a placement near a 45-degree look may be corrected. `StairTests` covers the rules.

The **lantern** hangs under a ceiling: `support: "above"` (`BlockSupport.SupportOffset` returns
+Y for a `BlockType.SupportAbove` block), so `BlockPlacer.PlaceBlockAtFace`'s support check only lets it
in where a solid block stands over the target cell (a placement against a block's underside), and
every pop path that asks `BlockSupport` (mining's `BlockEditor.PopUnsupportedAround`, the blast scan, leaf
decay) takes it down with that block. It lights like the torch (emission 15, the default warm
color), is non-solid, and crafts from a torch and a copper ingot. Its model crops a cage, a lid
and a chain out of one opaque tile from `tools/generate-lantern-texture.py`, whose header notes
the trap: `ModelBaker` mirrors an explicit Minecraft uv on the way in (a side face samples column
`32 - 2u`, an up/down face row `32 - 2v`), so an off-center uv must be written mirrored and the
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
`BlockInteractions.TryToggleOpenable`, a raw state write through `Chunk.SetStateAt` (saved, journaled to
clients, remeshed at once) with the fluid and leaf-decay schedules and `World.BlockEdited`; a
client predicts it and the server replays the `Use` intent through the same chain. Both models
(`block/trapdoor_bottom`, `block/trapdoor_open`) are split in half-width boxes so no face spans a
cell side; the tile comes from `tools/generate-door-textures.py`. `OpenableTests` covers the
rules; `visual-examples/doors.json` (`VOXEL_TIME=147`) shoots trapdoors shut and open.

The **door** (`oak_door`, three from six planks) is two blocks, `oak_door` (the lower half, the
item and the drop) naming `oak_door_upper` as its `upperHalf` (schema table, `TallBlock`), both
openable facing blocks on the `block/door_closed` / `block/door_open` parents: shut, a 3 px panel
across the side of the cell nearest the placer; open, swung a quarter turn about its left-hand
hinge into the cell. `BlockPlacer.PlaceBlockAtFace` checks the upper cell (`CanPlaceUpperHalf`: loaded,
free for a placement, no falling cube or player body) before writing the lower half, then writes
the upper half with the same state (`PlaceUpperHalf`, with the fluid, leaf-decay and
`BlockEdited` hooks); `BlockEditor.RemoveBlock` hands either half to `RemoveTallBlock`, which drops the
lower half once and clears the partner through the hooks mining runs (fluid, leaf decay, far LOD,
`PopUnsupportedAround`, a falls block above it, `BlockEdited`), the multiblock revert in
miniature; `BlockInteractions.TryToggleOpenable` writes the clicked half's new state to its partner too. A blast
clears cells without these hooks, so it can leave one half standing (like a multiblock without
a sweep); breaking that orphan drops the door as usual. The door tiles are from
`tools/generate-door-textures.py`, the pull on the center line because of the mirrored uv.
`DoorTests` covers placement, the two-cell refusal, breaking either half and the shared toggle;
`doors.json` shoots two doors, one clicked open.

**Stone bricks** (`stone_bricks`, four from four stone) are a plain `cube_all` building block on
`stone_bricks`' own tile.

**Slime block** (nine slimeballs) carries the `bounce` flag: a body that lands on it is sprung back
up at `SlimeBlockBounceRestitution` of its impact and bills no fall damage (`FallTracker`'s cushioned
landing); the tile comes from `tools/generate-slime-block-texture.py` (`SlimeBlockTests`).

**Sink and soft-landing flags** (the solid `cloud_block` of the sky's cloud slab uses both, see "Solid clouds").
`softLanding` makes a landing on the block free, like the bounce but with no spring. `sink` (0..0.5)
cuts a soft layer off the top of a plain solid cell: the player body collides only with the part
below it (`SinkLayer.SolidHeight`, inside `BodyCollision.BodyCollides`, so the server's endpoint rule
accepts a sunk body), and a body whose feet stand in the layer (`PlayerBodyProbe.SinkLayerTop`) is
grounded while `PlayerPhysics.HoldInSinkLayer` holds its descent to `PlayerConfig.SinkBlockSpeed`
(`SinkLayer.HoldFeet`: a fall is caught at the top) until it rests on the solid part; it jumps, steps
and walks out as from any ground. `PlayerBodyProbe.Grounded` counts the layer too, so the server's
hover rule never corrects a sinking body, and the one cushion rule (`PlayerBodyProbe.LandingCushioned`:
a bounce or soft-landing block under the footprint) feeds the fall tracker in singleplayer and on the
server alike, so a server no longer bills a slime-block landing either. Player body only: mobs, drops
and falling blocks see the whole cell (`SinkBlockTests`, `MovementRulesTests.ABodySinkingIntoASinkBlockIsNeverCorrectedNorAHoverer`,
both driven through the real `cloud_block`).

**Solid clouds** (`cloud_block`, `WorldGen/Sky/CloudPatchGenerator.cs`, plans/LAYERS.md) are real blocks
in the sky band `CloudSlabBottomY..CloudSlabTopY` (198..203), above every peak (`ReliefCeilingHeight`
148) and the drawn cloud layer (160). The block is a `translucent`, `cullSelf` glass-like cube (drawn in
the sorted translucent pass, no shadow, no AO, light passes) with `softLanding` and `sink: 0.5`, so a
fall onto it is free and the player sinks half a block into its top cell and stands there; hardness 1,
it breaks by hand and drops itself, so mining through a patch drops the player out of it. Its tile
comes from `tools/generate-sky-textures.py` (alpha about 0.7). Worldgen lays patches where a 2D
OpenSimplex noise (seed offset 166, two octaves, `CloudPatchFrequency`) is above `CloudPatchThreshold`
(about a quarter of the sky, patches tens of blocks across): the further above, the thicker the column
(one cell at a rim, the whole band from `CloudPatchCoreValue`), roughened by a detail noise (offset 167)
that also lifts the top, so a patch is a flat-ish slab with an uneven domed top.
`CloudPatchGenerator.TryGetColumn` is the one rule `IsCloud` and the stamp share, and cloud only
replaces air. A sky chunk in the band (above the terrain and any water) takes a short path that runs
only the cloud stamp, the solid recount and the frontier scan (`Stage.Clouds`); a chunk the full
pipeline reaches stamps it after the foliage, before the barrier. The far LOD draws no clouds (the
realms exist only in full-detail chunks), so they show within the load sphere (256 blocks from the
eye). `CloudPatchTests`, `StampGateTests`; `visual-examples/clouds-solid.json` (`VOXEL_TIME=147`).

**Bandage** is the first `use.action: "heal"` item (`HealItem` -> `PlayerHealth.Heal`, refused at
full health, `BandageHealAmount`), its sprite from `tools/generate-bandage-texture.py`
(`HealItemTests`).

**Ash fertilizer** (`use.action: "grow_tree"`) grows the tree of the column it is used on, resolving
the species from the targeted block (`TreeSpeciesRegistry.ForBlock`, which resolves a wood two
species share to the leafy owner) and refusing, spending nothing, where the cell holds no tree
(`GrowTreeItemTests`). The **acorn** plants an oak sapling the same way (`AcornTests`), and
`World.TryGrowTree` is the reactive growth path behind both (`### Trees`).

**Spell staffs** (`staff` in the item schema): the **wooden staff** (branches and a crystal block) and
the **crystal staff** it is upgraded into (the wooden staff, crystal blocks and a copper ingot), a
higher tier with more slots and the `advanced` part tag beside `basic`. Their sprites come from
`tools/generate-staff-textures.py` (`StaffContentTests`, `StaffBindingTests`).

**Spell table** (`spell_table`, four oak planks and a crystal block) is a plain `cube_bottom_top` block
wearing the crystal block's tile on top and planks elsewhere, made a station by the `spellBuilder`
flag alone (no block entity). A right-click with a staff in hand opens the spell builder
(`App/SpellBuilderInput`, `UI/SpellBuilder/`, `SpellBuilderRenderer`), client-side only; without a
staff a toast asks for one. The builder lists the parts the player knows by kind (`KnownSpellParts`:
the parts its scrolls taught, creative always all), keeps one element, one shape and one trigger with any
modifiers, shows the live cost, verdict, fizzle chance and damage and range factors (`SpellRecipe.Compile`
against the staff's tags), and binds or clears the chosen slot through the `BindStaffSlot` /
`ClearStaffSlot` inventory actions, which the client predicts and the server's `InventoryState` keeps or
reverts. E or Escape closes it, and so do the pause menu, the death screen and a change of the held
staff (`SpellTableTests`, `SpellBuilderViewTests`, `SpellBuilderLayoutTests`).

**Flint from gravel**: the gravel loot table yields the flint inside it sometimes, so a gravel patch
is worth digging (`GravelPocketTests` covers the pocket side).

Two small interaction rules round the list out: **a cactus prick** hurts any body standing against
the cactus' side (a `contact` block flag, billed like a mob's contact damage; `CactusPrickTests`),
and **attacks pass through plants**: the punch and slice cones ignore non-solid plants (tall grass,
flowers), so a mob behind one is still reachable. **A placement takes the plant's own cell** when
the targeted block is `replaceableByPlacement` (the layered snow idiom), so building against a plant
fills where it stands instead of hovering beside it (`PlacementTests`).

### Item schema (`assets/data/items/<name>.json`)

```json
{ "name": "Flint", "texture": "assets/textures/items/flint.png" }
```

`name` and `texture` are all a plain pickup needs. An optional `use` object gives the item a
right-click behavior (`RightClickAction.TryUseSelected` -> `WorldItemUse.DispatchUse`):

```json
{ "name": "Bucket", "texture": "assets/textures/items/bucket.png",
  "use": { "action": "pickup_fluid", "fluid": "block/water", "result": "item/water_bucket" } }
```

| field | meaning |
|---|---|
| `use.action` | `pickup_fluid` (scoop the targeted fluid source into the item), `place_fluid` (pour one into the targeted cell), `place_stack` (build a stack block up), `ignite_campfire` (strike a branch pile, or a kiln that has fuel), `convert_multiblock` (turn a volume of the anchor's size into a structure), `heal` (restore health to the user, refused at full health), `grow_tree` (grow the tree the aimed cell belongs to, `AshGrowthPulses` pulses), `restore_mana` (refill the `restoresMana` share of the mana pool, one unit spent, refused at a full pool) or `learn_parts` (a teaching scroll: learn the spell parts in `teaches`, spending one; creative spends none, and a scroll that teaches nothing new is refused and kept) |
| `use.pose` | the pose held while a `use.seconds` charge runs: `raise` (default, the hand presenting the item) or `bow` (`UsePose`, `BowPose`): the item arm out forward, the other arm drawn to the cheek as the charge rises, the shoulders turned side-on, holding at full draw until the button is let go (the use never runs by itself; `RightClickAction.UpdateUseCharge`), then the string arm snaps back and the body settles. Needs `use.seconds`. The `bow` item has no shot yet (`F-4`), and a remote player is not shown drawing one (the draw is not on the wire) |
| `use.seconds` | seconds holding right-click takes before the use runs: the world charges a use pose (`PlayerModel.StartUseCharge`/`ThrowPose.WithUse`) and only runs the use at completion, so letting go early cancels it and spends nothing (`RightClickAction.TryBeginUseCharge`/`UpdateUseCharge`/`CancelUseCharge`, the twin of `ThrowAction`'s throw charge). 0 (omitted) keeps the old instant use. The firestarter's `useDelay` stays the cooldown after a use |
| `use.block` | for `place_stack`, the stack block it builds; for `ignite_campfire`, the firepit block |
| `convert_multiblock` | takes no `use.block`, `use.result` or `use.fluid` (refused naming the file): the converter presses whichever multiblock the clicked cube is the source of (each anchor's `multiblock.source`). It is spent by nothing and never morphs |
| `use.fluid` | the fluid block the action works on; must be a block with `fluid: true` || `use.result` | the item this one becomes on success; must be an item, and the two normally point at each other |
| `display` | how the item sits in the right hand: `rotation` as `[pitch, yaw, roll]` degrees (composed yaw, then pitch, then roll), `translation` in blocks of hand space, `scale` as a multiplier; omitted means `{"rotation": [-30, 10, 0]}`, i.e. upright over the hand with a slight sideways cant — the pickaxe yaws to 60 so a tip faces forward, the compass uses rotation `[-60, 0, 0]`, scale `0.67` and a lift to face back up at the holder |
| `blockDamage` | damage one left-click hit removes from a block's `hardness`; default 0.5 (the bare-hand baseline). Independent of `tool`, which only gates *whether* the block may be mined |
| `throwDamage` / `throwAligned` | `throwDamage` makes the item throwable (a charged right-click throw, damage at full charge, 0 for a harmless snowball); `throwAligned: true` (needs `throwDamage`) makes the thrown copy fly point first along its velocity (`ThrownProjectile.AlignedRotation`, the sprite's tip on its (1, 1) diagonal along the heading) instead of tumbling about the vertical, which is how the `spear` flies. Its sprite comes from `tools/generate-spear-texture.py`; `visual-examples/spear.json` films it (the `throw` player action launches the selected throwable at full charge). Gameplay beyond the throw (pickup, durability, a recipe) is `F-4` |
| `entityDamage` | damage one punch or slice deals to an enemy (any `Mob`: a slime or a goblin); default 1 (the bare-hand baseline). A weapon raises it without needing a `tool` block (the obsidian blade is `6`) |
| `attack` | left-click attack behavior, an `AttackPattern`: `punch` (default; forward cone, hits the nearest one), `slice` (forward hemisphere, hits the nearest two, two-phase swing — the obsidian blade) or `sword` (a three-swing chain: a diagonal cut and a rising back cut over the hemisphere, hitting the nearest three, then an overhead chop on the one target in the cone at the highest power with a heavier shove — the iron sword). See `Combat`/`SliceCombo`/`SwordCombo`/`SwordSwings` |
| `trail` | optional `{"color": [r, g, b]}`, sRGB 0..255: the color of the glowing slice trail the item's swings leave (`Item.TrailColor`; omitted, `TrailColorOrDefault` falls back to `ParticleConfig.SliceTrailDefaultColor`). Legal only on a `slice` or `sword` item; a trail on a punch item, or a color that is not three ints in 0..255, throws naming the file (`ItemLoader.ParseTrail`). The obsidian blade is violet, the iron sword pale blue-white |
| `fuel.burnSeconds` | seconds one unit of this item keeps a kiln lit (coal 60; 1..3600). Writing the object is what makes an item kiln fuel — a kiln takes up to its `kiln.fuelCap` units of **one** kind at a time |
| `crucible.capacityMl` | marks the item a **crucible**: a vessel holding this many milliliters of metal, melted and pending together (100..10000). A crucible stores in the **Tools** pool, one instance at a time, because its contents ride on that instance's `ItemStack` as a `CrucibleComponent`: one `CrucibleMetal(MaterialId, MoltenMl, PendingMl)` per molten material it holds (copper and tin side by side in an alloy kiln, see "The kiln"), the capacity counted over all of them, plus `PendingInputId`, display-only, the last melt input charged in, so the kiln can draw what is waiting to melt; while the vessel stands in a kiln the same struct lives in `KilnState` instead, and taking it out writes it back onto a fresh instance (`CrucibleInventory.Add`). The stack carries no melt timer, so putting a crucible back into a kiln restarts the partial chunk it was melting |
| `staff` | marks the item a **spell staff**: `{"slots": n, "tier": t, "tags": [..]}`, slots 1..`SpellConfig.StaffMaxSlots`, tier >= 1, non-blank distinct spell part tags (`ItemLoader.ParseStaff`, shape only: spell parts load after items). Builds a `StaffItem`, stored in the **Tools** pool one instance at a time, because each slot's bound spell recipe rides that instance's `ItemStack` as a `StaffComponent` (the part keys `<kind>/<name>` per slot plus the selected slot, immutable). `StaffBinding.TryBind` binds a recipe only when MG-2's `SpellRecipe.Compile` finds it valid against the staff's tags (a part passes when any of its tags is allowed), so a tier gates its parts by tag; `Clear`, `Cycle` (wrapping) and `Select` exist; Left Ctrl + wheel cycles the held staff's slot through `HeldStaff` and the server-owned `SelectStaffSlot` inventory action, and the spell table binds and clears a slot (`HeldStaff.TryBind`/`TryClear`, through the server-owned `BindStaffSlot`/`ClearStaffSlot` inventory actions, see "Spell table"). Right-click casts the selected slot (see the **Cast** intent). Saved in `player.json` and carried in the inventory snapshot as `ToolData.Staff` by part key, never id, and re-bound on load against this run's parts (what no longer binds is dropped with a warning). Crafting consumes the least-bound instance first. A Q-dropped staff loses its spells like a crucible loses its metal (an `ItemDrop` carries only an id). May not declare `use` or `crucible`; the tier is shown in the tooltip |
| `teaches` | the spell part keys (`<kind>/<name>`) a scroll teaches, a non-empty list of distinct keys; required with, and only legal with, `use.action: "learn_parts"` (`ItemLoader.ParseTeaches` checks the shape, and `ValidateLearnParts` checks every key against `SpellPartRegistry` once the spell parts load, naming the file). Builds a `LearnPartsItem`, a plain Misc stackable; a scroll may not declare `use.result`, `use.fluid` or `use.block`. Its tooltip lists the parts it teaches |
| `restoresMana` | the share of the mana pool one use restores, in (0, 1]; required with, and only legal with, `use.action: "restore_mana"` (`ItemLoader.ParseRestoresMana`; `RestoreManaItem`, `PlayerMana.TryRestore`; the `mana_draught`) |
| `model` | optional `block/<name>` model under `assets/models/`, baked into `Item.BakedModel` so the item can be drawn as real 3D geometry while it sits inside a station (the crucible standing in a kiln's hearth). The sprite stays the inventory icon; this only adds a mesh |
| `tool` | optional mining numbers: `power` (the item's mining *tier* — the number a block's `tools` requirement is compared against, default 0.5, the bare-hand baseline; it is **not** a damage number, damage is `blockDamage`) and `hitDelayMs` (milliseconds between hits). Writing the object marks the item as a tool; which block `tools` tags it satisfies comes from the tag files, not the item — `assets/data/tags/pickaxe.json` listing `item/primitive_pickaxe` is what makes a block gating on `"pickaxe"` accept it (`Mining.CanMine`). Bare hand and non-tool items mine at the defaults, and creative ignores the gate |

Both names resolve like a loot entry (`block/` / `item/` prefixes pin the namespace).
`ItemLoader.ValidateItemUses` runs after the blocks are loaded and throws, naming the file,
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
(`BlockEditor.RemoveBlock`), blasts (`TntManager.Detonate`), felled trees (`TopplingTree.TakeDrops`) and
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
the blocks** (`TreeSpeciesLoader.LoadTreeSpecies`; a missing folder is an empty registry), compiles each file through
the pure `TreeSpeciesCompiler.Compile` (DTO `TreeSpeciesDefinition.cs`) into an immutable
`TreeSpecies`, and every error starts `Tree species '<path>'`. Block references take an optional
`block/` prefix and never resolve to an item. Nine species ship: `oak` (the forest), `spruce` (the
taiga), the leafless `cactus` (the desert), the leafless `dead_tree` (the desert, the frozen
plains and the steppe, which borrows the oak's wood — `TreeSpeciesRegistry.ForBlock` resolves a
shared wood to the leafy owner, so the growth item never turns an oak into a snag) and the sparse
`stone_pine` (the savanna, its umbrella crown an acacia stand-in) generate in the world; the `palm`
generates only through its `worldgen.oasis` disc; `baobab` has **no `worldgen` block** and is
only ever grown (`TreeGrowthWriter`, the `grow` op, the item debugger); and the hell's `fungal_tree`
and leafless `charred_tree` have no `worldgen` block either: `HellTrees` plants them from the hell
biome palettes (see "Hell flora"). See
`### Trees` for what the fields drive.

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
| `worldgen.fallenLog` | none | the block worldgen lays as this species' fallen hollow log on the floor of the biomes it is the tree of (`TreeWorldgenRules.FallenLogId`, read by `FallenLogGenerator.HollowLogFor`); it must be a `facing: "horizontal"` model block, not tree wood or a fluid, or the species is refused at load, since worldgen writes its facing into the state byte. Omitted means that biome gets no fallen logs (oak `hollow_oak_log`, spruce `hollow_spruce_log`, stone pine `hollow_stone_pine_log`) |

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
  the other — so siblings and two neighboring trees never weld, and the tree graph can be read back
  out of the blocks (`TreeGraph.FromWorld`). Placed wood is `WoodState.Detached`, a full cube that
  never welds or fells.
- **Leaf state** (`LeafState`): bits 0-2 hydration, bit 3 `PersistentBit` (player-placed leaves,
  which never decay). Hydration is `leafCluster.hydration` minus the Manhattan distance to the
  nearest **live tip** whose cluster shape holds the cell (`LeafCluster.HydrationFromTip`, the one
  per-tip formula, behind `HydrationAt`, `Collect` and decay alike). The cluster radius is one per
  tree and follows the trunk: `LeafCluster.ClusterRadius` scales `minRadius..radius` by the
  tree's **support**, the root cell's stored diameter (`LeafCluster.SupportPx`, the quantized
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

**Rasterizer.** `TreeRasterizer.Rasterize(graph, species, space, out woodCount)` turns a graph into
`TreeCell`s (cell, block, state) in a load-bearing order: the graph nodes in index order (growth
nodes, then feature nodes), then the shells, which end the wood at `woodCount`, then the leaves
(`LeafCluster.Collect`, which never lands a leaf on a shell). The thickness code is
`ThicknessCode(px)` = `ceil(px) - 1` clamped at the full cube. Worldgen stamping, the first far-LOD
ring, reactive growth, the felling and decay tests and the item debugger all share it.
`ReachOf(species)` is the tree's (horizontal, up, down) bound from its root — the energy plus the
leaf radius, widened by the shell radius and the buttress reach — which sizes chunk-border margins
and the decay schedule.

**Wide trunks** are a core plus shells, and only the rasterizer makes them. A node on the
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
16 px rasterizes exactly as before shells existed.

**Mesh, collision and the pixel rim.** `tree_wood` meshes on the per-voxel path, never greedy:
`TreeWoodEmitter.EmitTreeWood` draws a thin cell as a node box sized `TreeWoodShape.HalfWidth(t)` plus one
capless tube toward every connection at the thinner of the two widths (`TubeHalf`) and a same-width
capless plug toward every adjacent solid occluding non-wood block (`TreeWoodShape.PlugMask`: the
ground, a wall), so a thin trunk or root meets the ground it rests on instead of floating the node
clear of it, with cropped
UVs and smooth light; a full-thickness or detached cell takes the model path and culls against a
same-id full cube. A wood block whose species sets `rim: "pixel"` (`BlockType.PixelRimById`, filled
by `TreeSpeciesLoader.LoadTreeSpecies`) gets its outer shells beveled from their eight horizontal
neighbors: `TreeWoodShape.RimCornerMask` carves a corner when neither of its two edge neighbors
is same-id attached full-cube wood (a cell only ever removes its own material, so diagonals never
matter), `RimBoxes` turns the mask into stair-stepped full-height boxes (`RimInsetPx`, at most
`MaxRimBoxes`, `TreeRimBevelPx` deep), and `TreeWoodEmitter.EmitRimWood` meshes exactly those boxes from the
18³ snapshot with caps that subtract a beveled cell above or below. The hitboxes match the mesh:
`TreeWoodShape.GetBoxes`/`GetBoxesAt`/`OverlapsShape` (node, tubes and rim boxes, allocation-free)
feed the raycast, the selection outline and `BodyCollision.BodyCollides`.

**Worldgen** (`TreeGenerator`, a pure function of position like the rest of worldgen).
`TreeGenerator.TryGetTree(x, z, ...)` keeps the placement gates per species from its `worldgen`
block (one hash stream, biome -> species in ordinal name order, density, lowest-hash spacing,
slope, soil, ground band, water, openness, the spawn clearing). Every tree of a 64x64 XZ cell
(`TreeCellShift`/`TreeCellBlocks`, `TreeGenerator.CellOf`) then grows together as one `TreeBatch`
in placement-hash order inside one `VirtualTreeSpace`, each grown tree's cells occupied before the
next grows, so neighboring crowns grow around each other; the trees of the eight neighboring
cells count as obstacles only as their cached **solo** shapes (`TreeBatches.ShapeOf`), which keeps a batch a
pure function of (cell, seed) and independent of chunk order. `Stamp` writes wood with its state
into air or leaves, then leaves into air, in a fixed scan order (the root walk extends the trunk
down through cave air only inside the chunk holding the base). The far LOD's first rings read the
stamped canopy (`TreeGenerator.TryGetCanopy`), while the coarse rings (`LodLevelSpec.ProxyCanopy`)
read a proxy rolled from the same placement: the species' `lod` crown height and radius through
`LodCrownProxy.TopOffset`'s dome/cone/column silhouette, so the far sweep never grows a tree. The
solo shapes, the batches and both canopy tables are `GenerationalCache` sites (`TreeShapeCache*`,
`TreeBatchCache*`, `TreeCanopyCache*`, `TreeProxyCanopyCache*` in `WorldGenerationConfig`).

**Leaf decay** (`Simulation/LeafDecay/LeafDecayManager.cs`, owned by `World`, main thread only, never while
loading, the `leafDecay` profiler row) is a `FluidManager`-style scheduled-cell simulation. An edit
schedules the cells around it (`ScheduleAround`/`ScheduleBox`, which grow the box by
`ScheduleReach` so callers never know a radius — see the edit-path invariant below); a tick
re-derives each scheduled non-persistent leaf's hydration as the best `HydrationFromTip` over the
live tips in its species' cluster box (a full-radius offset table, best first, with each candidate
tip's own tree radius re-checked: `TipHydration.TryTipSupport` walks the tip's parent chain under the
`OrphanWoodCheck.WalkParentChain` link rules to the root cell and reads its diameter, memoized per `Update` in
its `_tipSupport`; an unloaded read freezes the leaf), so a settled canopy reads back bit for bit and costs nothing,
and a leaf left at 0 decays to air (at most `LeafDecayMaxRemovalsPerTick` a tick, in a scattered
order, so a crown dissolves speckled), rolling its loot through `DropManager.SpawnLoot` with a few
leaf particles. The same manager crumbles **orphaned wood**: a scheduled non-root, non-detached
`tree_wood` cell that `TreeSupport` finds no longer anchored (`TickWood` through `OrphanWoodCheck`, a memoized parent-chain
fast path plus the full search on a broken chain) drops with its thickness-band loot and schedules
its reach box, so a blasted subtree crumbles outward a layer per tick. Any read in an unloaded
chunk drops the cell from the schedule (it freezes, never decays), and a chunk load schedules
nothing. Tuning: `LeafDecayTickSeconds`, `MaxLeafDecayTicksPerFrame`, `MaxLeafDecayCellsPerFrame`,
`MaxLeafDecayBacklogCells`.

**Felling** (`BlockEditor.FellWood` -> `TopplingTree.Collect`). Mining tree wood fells the cells that
lose every path to an anchor once the cut cell is gone, through the GL-free bounded search
`TreeSupport` (`TreeSupportResult` Supported / Unsupported / Unknown): links are parent edges
(`TreeSupport.Links`) plus vertical shell-to-shell adjacency, anchors are root cells and shells
standing on real ground (`TreeSupport.IsAnchor`: solid, neither tree wood nor leaves), and an
unloaded read is no link. So a cut limb drops only the limb, a cut 1-wide trunk drops the crown,
and a wide trunk falls only once a whole horizontal slice of core and shells is cut. Canopy leaves
within Chebyshev 2 of the falling wood come along. The search is capped at `TreeFellWoodCap` wood cells and the whole fell at
`TreeFellTotalCap` (leaves past it are left standing and decay). The falling tree is meshed by the
real chunk mesher through `CellBatchMesher.Build` (`TopplingTree.BuildMeshData`: one mesh per chunk
the felled cells span, so a crown of any size is drawn whole) and drops through
`TopplingTree.TakeDrops`.

**Reactive growth** (`World.TryGrowTree(species, x, yHint, z, steps)`, refused while loading or
for an unknown species; the `grow` scenario op calls it). `TreeGrowthWriter` (`Simulation/`, GL-free,
main thread; `TreeGrowthResult`/`TreeGrowthStatus` Grew / Planted / NoGround / NotGrowable) scans
the target column for the species' root and rebuilds the tree with `TreeGraph.FromWorld` (a
breadth-first walk over child edges only, so detached and cut-off wood is never reached; every node
floored at its stored diameter, a wide core's floor recovered from its shell run, nodes below the
root loaded as feature nodes; null on an unloaded read or past `TreeGrowNodeCap`), or plants a
sapling on solid ground when there is none. It then runs `GrowFurther` (at most
`TreeGrowMaxPulsesPerCall` pulses) in a `WorldTreeSpace` — a cell is free when it is loaded, not
reserved by a falling block, air or a non-persistent leaf of the species, and not the missing parent
of an orphan, which is what keeps a cut branch cut — rasterizes once and writes back only the
difference: existing nodes are only re-coded thicker, new wood goes only into free cells, leaves
only around changed tips — or, when the thicker trunk changed the tree's `ClusterRadius`, around
every original tip still in the world (never one whose wood the write skipped, whose leaves would
hang off wood that never landed) — and shells follow the **new-ring-only** rule (inside the footprint a core
already had, existing shells are only re-coded, so a mined shell or a partial slice cut stays
mined). All writes are `Chunk.SetBlockRaw`, followed by `FluidManager.ScheduleAround` per changed
id, one `LeafDecayManager.ScheduleBox` over the written box (pruning is left to decay), a
`ChunkManager.MarkDirty` per touched chunk and `TreeGenerator.PoisonCanopy(x, z, 0)` plus
`LodManager.MarkEditedColumn` per changed column.

**The sapling clock** (`Simulation/SaplingManager.cs`, owned by `World`, main thread only, skipped on
a client): a `plant_tree` use (an acorn on dirt or grass, `WorldItemUse.PlantTree`, which maps the hit
cell and face to the ground cell) plants through the writer's Planted branch and schedules the root
cell; every `SaplingPulseSeconds` (jittered by `SaplingPulseJitter`) the manager grows it one pulse
through the same `TryGrowTree` path, up to the species' `MaturityStepsAt` for its own root cell and
seed, then forgets it — so a sapling becomes a tree over a few minutes. A cell in an unloaded chunk
waits, and a cell whose wood is gone (mined, or grown over) is forgotten. In memory only: a reload
leaves a half-grown sapling standing until it is grown again by hand (see the known gaps).

**The item debugger's TREES tab** (`OpenGLTest.ItemDebugger/TreePreviewState.cs`, see "Build and
run") grows any species pulse by pulse over flat grass at a fixed seed through `GrowSteps`, and
meshes it with `CellBatchMesher` (`src/Chunks/Meshing/CellBatchMesher.cs`): the real chunk mesher over as many
scratch chunks as the cells span, each at its real position inside an empty one-chunk ring, so a
wide or tall tree is never clipped at a chunk seam and wears the real biome tint.

A grown tree shows in the far LOD at once: after poisoning each written column, `TreeGrowthWriter`
scans the loaded column for its highest leaf and records it through `TreeGenerator.SetCanopyAdded`
(`TreeCanopy.CanopyAdded`: packed top and leaf block per fine column, read before the disturbed
marker and the generated canopy on every ring, cleared by any later `PoisonCanopy`, saved in
`lod-edits.bin` format version 2 with the leaf as a block name, reset by `WorldSeed.Apply`; never
in a `GenerationalCache`). `CanopyAddedTests`, `AGrownCrownGoesBackToTheFarLod`.

Known gap, open: the wide-trunk residue of `M-14` — a wide core cut while its shells stand leaves
the crown unreachable for reactive growth.

### Recipes, cast molds and metalworks

Crafting recipes live in `assets/data/recipes/<name>.json` as `{ "result", "count", "ingredients":
[ { "item" | "tag", "count" } ] }` (DTO `RecipeDefinition.cs`, compiled by
`RecipeLoader.CompileRecipe` into a `CraftingRecipe`), campfire recipes in
`recipes/cook/<name>.json` as `{ "input", "result", "count", "cookSeconds" }`
(`CookRecipeDefinition.cs` -> `CookRecipe`, one recipe per input, enforced) and quern recipes in
`recipes/crush/<name>.json` (`CrushRecipe`, likewise one per input). Every name resolves
like a loot entry, with `block/` and `item/` prefixes pinning the namespace (`ContentFiles.TryResolve`), so
blocks and items are interchangeable as ingredients, results, inputs and outputs. Craft files are
named by their result, cook files by their input, and a reverse recipe by
`<result>_from_<ingredient>`.

**Cast molds** are the worked example. `mold_pickaxe` / `mold_axe` are pressed from three mudballs
(`recipes/mold_pickaxe.json`, `mold_axe.json`), uncraft back into three
(`recipes/mudball_from_mold_*.json`) and fire into `fired_mold_pickaxe` / `fired_mold_axe` by
right-clicking a lit branch pile with the mold hotbarred (`BlockInteractions.TryCookOrCollect` ->
`CampfireManager.TryPlaceFood`, `recipes/cook/mold_*.json`); a fired mold never uncrafts. They are all
`hasItem` slab blocks whose models under `assets/models/block/` are generated by
`tools/generate-mold-models.ps1` and never hand-edited: the script reads each sprite's alpha
silhouette (`assets/textures/items/mold_*.png`, copies of the copper tools — silhouette input only,
referenced by no JSON, since a block's icon comes from its baked model via `BlockCubeCache`), maps
it onto the inner 12x12 of the 16-cell top, greedy-merges the rim and pocket cells into box elements
and emits `cullface` only on faces that sit on the cell boundary — a cullface culls against the
*world* neighbor, never a sibling element, so the pocket walls carry none and always draw. Its
constants `$SlabHeight` (4), `$PocketDepth`, `$RimCells` and `$AlphaThreshold` sit at the top of the
file, and re-running is byte-identical until the real sprites arrive. The blocks set
`collidesByHitbox` with a `0..0.25` slab hitbox whose height must equal `$SlabHeight/16` (`MoldTests`
pins that tie, and the quad budget at 300); raw molds wear the `mud` tile, fired ones `fired_mud` —
a `stone.png` placeholder that lives in `assets/textures/blocks/`, never `blocks_material/`.
There are **eight** of them now — pickaxe, axe, ingot and sword, raw and fired — all generated by the same
script from the same constants (`mold_sword.png` comes from `tools/generate-metal-textures.py`, which also
recolors the tin and copper-sword art).

**Tin** (`tin_ore`, `TinGenerator`) is the second, weaker metal: commoner than malachite, in the
`TinMinY..TinMaxY` band, mined with any pickaxe; it crushes in the quern into `crushed_tin`, melts as `tin`
(ingot, axe, pickaxe and sword heads 200 ml, ore and crushed 100 ml) and casts into the four molds. Tin and
copper each have an axe, pickaxe and sword (`attack: "sword"`), the tool ladder runs primitive < tin < copper
< bronze.

**Bronze** is the alloy (3 copper + 1 tin by ml, made only in the alloy furnace, see the recipe kind
below) and the top of that ladder: an ingot, axe, pickaxe and sword heads (each 200 ml, melting back
to `bronze`, cast in the same four molds) and the three tools crafted like the others (head + branch + 2
vines), in the `axe` and `pickaxe` tags. It has no ore, no blocks and no armor. Its art is
`tools/generate-metal-textures.py bronze`; `MiningTests.BronzeOutranksCopperAndMinesWhatCopperMines`
holds the ladder as relations and `visual-examples/bronze.json` (`VOXEL_TIME=147`) shows the tin,
copper and bronze tools side by side.

**The casting basin** (`casting_basin`, fired on a campfire from `casting_basin_raw`, which is
pressed from four mudballs) is the mold that makes **blocks**: a hand-authored 8 px bowl (a 2 px
floor, a 2 px rim) whose model `assets/models/block/casting_basin.json` is the source of truth for
`CastingRenderer.Basin`, the `Pocket` the render pass uses for any cast whose result is a block
(`CastingRenderer.PocketFor`; item casts keep the mold slab's `Mold` pocket, and
`CastingTests.BasinPocketMatchesItsBakedModelAndBlockCastsUseIt` pins the tie). Nothing else
changed: `CastingManager` keys on the mold block id, so `recipes/cast/glass.json` (mold
`block/casting_basin`, fluid `glass`, 100 ml, result `block/glass`) is all it takes. **Molten
glass** (`data/molten/glass.json`) is the first non-copper material: `recipes/melt/sand.json` melts
a sand block into 100 ml (one kiln melt chunk, so an 800 ml crucible makes eight glass) and
`recipes/melt/glass.json` melts a glass block back into the same 100 ml.

**Molten metal is content, not a fluid.** It never enters `FluidManager`, never occupies a cell
and has no block: `assets/data/molten/<name>.json` declares a `MoltenMaterial` as
`{ "name", "texture", "color": [r, g, b] }`, where `texture` is an atlas tile (copper reuses
`lava_still`) and the color tints it. Ids are handed out **1-based in file-name order** (0 means
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

A third kind, `recipes/alloy/<name>.json`, is `{ "result", "parts": [ { "fluid", "ml" }, ... ] }`
(`AlloyRecipe`, `ContentRegistry.AlloyRecipes`, compiled by `RecipeLoader.CompileAlloyRecipe`): one
full set of the parts becomes exactly their summed ml of the result, so alloying conserves metal by
construction. A recipe needs at least two distinct parts, none of them the result, and a metal may
be a part of one alloy only. `bronze.json` ships: 600 ml copper + 200 ml tin = 800 ml bronze
(`data/molten/bronze.json`), which is why the
crucible holds 800 ml: one vessel takes exactly one set (`AlloyKilnTests.OneCrucibleHoldsAFullSetOfEveryAlloy`).

**Iron** has an ore and a quern path but no smelting yet. `hematite_ore` needs a tier-2 (copper)
pickaxe (`tools: {"pickaxe": 2}`, so the primitive and tin pickaxes whiff and copper and bronze mine it) and drops a hematite chunk (the
`hematite_ore` item), which the quern crushes into `crushed_iron` (`recipes/crush/hematite_ore.json`).
There is no melt recipe and no molten iron, so a kiln refuses
every iron form (`MetalworksRecipeTests.IronCrushesInTheQuernButNeverMelts`). `iron_sword` (a
`slice` weapon) and `iron_sword_head` exist as items with no recipe until an iron kiln change. The
`iron_pickaxe` is the top pickaxe tier (power 3, above bronze's 2.5; in the `pickaxe` tag), the tool
the hell barrier needs (plans/LAYERS.md). It has no recipe either: for now it only comes from the
creative catalog (`MiningTests.IronOutranksBronzeAndMinesEverythingBronzeMines`; sprite from
`tools/generate-metal-textures.py iron`).

**The barrier** (`barrier`, `BarrierGenerator`) seals the overworld from hell: `requiresTool` with
`tools: {"pickaxe": 3}`, so only the iron pickaxe mines it, hardness 40, and it drops itself (no loot
table, so a player may place it back). Worldgen lays it over `BarrierBottomY..BarrierTopY`: a gapless
core (`BarrierFullBottomY..BarrierFullTopY`) where every cell becomes barrier, carved air, water and
lava included, so no cave lets a player through; over the two ramps its share rises linearly from 0 at
the band's edge to 1 at the core, a ramp cell is barrier where a 3D OpenSimplex noise (seed offset 162,
`BarrierPatchFrequency`) mapped to 0..1 is under that share, and only solid, occluding ground is
replaced there, so caves stay open and the edges break into patches. The stamp runs last of all the
features (`Stage.Barrier`), so nothing writes over it; `BarrierGenerator.IsBarrier` is the field it
writes, bit for bit across chunk seams. Hell starts right below the band (see "Hell" next).
`BarrierTests`; its tile comes from `tools/generate-barrier-texture.py`;
`visual-examples/barrier.json` (`VOXEL_TIME=147`) frames a ramp cave and the core's faces.

**Hell** (`WorldGen/Hell/HellGenerator.cs`, plans/LAYERS.md) is every cell at or below `HellTopY`, and
it has no bottom. It is a stack of layers `HellLayerPeriodBlocks` tall repeating downward forever
(`HellGenerator.LayerIndex`, 0 right under the barrier, the cell twin of `LayerClimate.HellLayerIndex`):
the top `PocketHeight` rows of a layer are its cavern pocket, the bottom `HellSlabThicknessBlocks` an
unbroken slab of `hellrock` (hardness 24, `tools: {"pickaxe": 3}`, so only the iron pickaxe digs down;
drops itself). A pocket cell opens where a two-octave 3D OpenSimplex noise (seed offset 163,
`HellCavernFrequency`, y scaled by `HellCavernYScale`), sampled on a world-aligned `HellNoiseStride`
lattice and blended trilinearly at the cell's center (`OreNoiseLattice.Trilinear`), exceeds the row's
threshold (`RowThreshold`): the layer's `OpenThreshold`, lower (more open) each layer down to
`HellOpenThresholdMin`, plus an edge bias (`HellEdgeStrength`, quadratic over `HellEdgeBlocks`) that
closes the cells toward the pocket's ceiling and floor, so both follow the noise. A row whose threshold
reaches `RowSolidLimit` is hellrock by definition and never samples. An open cell fewer than
`LavaDepth(layer)` cells above its pocket's floor (`HellLavaBaseDepth`, rising `HellLavaDepthPerLayer`
a layer to `HellLavaMaxDepth`) is a pool cell (`HellGenerator.InPool`), so the floor's dips pond and
deeper layers pond more: lava where the column's pool holds lava, else the biome's soil topped by its
surface (a dry, flat floor). The level is flat across a layer, the biome and pool are constant down a
column of one layer and a dry pool is solid, so every lava cell's sides and bottom are lava or solid and
nothing ever needs waking (a generated hell chunk's fluid frontier is empty).

**Hell biomes** (`WorldGen/Hell/HellBiomes.cs`, `HellBiome`, `HellBiomePalette`; L-7/L-8) are their own
enum, never the surface `Biome` (whose ordinals ride the column codecs): `Ashlands`, `MagmaCaverns` and
`FungalGrove`. `HellBiomes.At(x, y, z)` reads a two-octave 3D noise (seed offset 164,
`HellBiomeFrequency`) at the layer's pocket middle row, on a chunk-aligned 2D lattice per layer
(`HellBiomeLatticeStride`) blended bilinearly, plus a hashed lean per layer (`HellBiomeLayerBias`), cut at
`HellAshlandsMaxValue` / `HellFungalMinValue`: regions a few hundred blocks across, mixed within a layer,
laid out anew in the next, constant down a column of one layer (floor and ceiling agree). Each biome's
palette (blocks resolved once in `HellBiomes`' static constructor) names its floor `Surface` and `Soil`,
`Wall`, `Ceiling`, lava `PoolBed`, a `CeilingLight` (one ceiling cell right over open cavern in
`CeilingLightRate`, by a cell hash, `HellBiomes.Picks`), an optional `FloorLight` and the `LavaShare` of
its pool columns: 1 for the Magma Caverns (basalt, magma floors and beds), `HellAshlandsLavaShare` (ash
over cinder soil, scorched hellrock walls) and `HellFungalLavaShare` (fungal moss over fungal soil); a
partial share picks lava columns by a single-octave pool noise (seed offset 165, `HellPoolFrequency`, on
an `HellPoolLatticeStride` lattice). The skins (`HellGenerator.Classify`, one rule over two fields): a
solid pocket cell with open cavern within `HellFloorSkinBlocks` above is floor (surface right under air,
soil deeper, the pool bed under lava), else with open cavern within `HellCeilingSkinBlocks` below ceiling,
else (above the lava level) beside open cavern wall; the slab stays hellrock. The stamp computes the open
field once over the chunk plus a one-cell ring and the skin reach (`FillOpenMask`) from the same lattice,
rows and fraction expressions as `HellGenerator.IsOpen`, and caches the row rules and each column's biome
and pool per layer, so `HellGenerator.BlockAt` (the point field) equals the stamp bit for bit across
seams. Every palette block is solid and steady, so lava stays put. The lights glow in two palette colors
of their own: ember (`ember_crystal` 15, `magma_block` 9) and fungal teal (`fungal_glow` 14), five of the
eight `BlockLightPalette` slots now used. The hell blocks (`hellrock`, `ash`, `cinder_soil`,
`scorched_hellrock`, `basalt`, `magma_block`, `ember_crystal`, `fungal_moss`, `fungal_soil`,
`fungal_glow`) are plain JSON cubes whose tiles come from `tools/generate-hell-textures.py`; the soils and
ash mine by hand, the rocks need any pickaxe, hellrock the iron one. Costs about 0.26 ms a warm hell
chunk with the flora below (0.15 before it), plus the hell trees grown on a cache miss, about 0.6 ms a
chunk on the probe's cold samples, which regrow every tree (`WorldgenCostProbe`, plans/PERF.md). Layer index will also scale the hell mobs (`L-10`).
`HellTests`, `HellBiomeTests`; `visual-examples/hell.json` (`VOXEL_TIME=147`) frames layers 0, 1 and 4
and a spot in each biome.

**Hell flora** (`L-9`; `WorldGen/Hell/HellFlora.cs`, `HellTrees.cs`, `HellTreeSpace.cs`) is per biome,
named in its palette: `GroundPlant` and `GroundPlantRate`, `VineRate` (0 for none) and the `Tree`
species with its `TreeChance` (the Fungal Grove: `glow_fern`, thick vines, `fungal_tree`; the Ashlands:
`ember_bloom`, sparse vines, `charred_tree`; the Magma Caverns none). The small flora is part of hell's
one rule: `HellGenerator`'s open cell above the lava level asks `HellFlora.AirCell` over the same
`IHellField` (point or stamp), so `BlockAt` still equals the stamp bit for bit and a chunk reads only
itself (the stamp's open mask reaches `HellFlora.ReachUp` = `HellVineMaxLength` rows above the chunk for
the vines). An air cell is a **vine** (`hell_vine`) when the first closed cell over it, at most
`HellVineMaxLength` up, is a ceiling cell whose salted cell hash (`HellBiomes.Hash`) hangs a vine at least
that long (1..`HellVineMaxLength`, stopping at the floor), so every vine cell hangs from a solid cell or
the vine over it; else an air cell over its biome's `Surface` (a floor's top or a dry pool's, never the
floor light) is now and then its `GroundPlant`. Flora only ever takes pocket air. **Trees** grow over
that and do not go through `TreeGenerator` (hell has no heightmap): hell is cut into
`HellTreeCellBlocks` squares (8, dividing the chunk), and each cell of each layer roots at most one
tree, a pure function of (cell, layer, seed): a hash picks the root column at least `HellTreeCellMargin`
inside the cell, the column's biome the species and chance, a hash one of the column's floors in the
layer (`HellGenerator.IsCavernAir` scans, its lattice samples memoized), and the root must stand on the
biome's `Surface`. It grows to maturity in a `HellTreeSpace`: free only in its own cell's columns and
in pocket air before the flora, so trees never meet, never enter rock, and the tree's cells all lie in
its chunk column. The shape is cached per key (`HellTrees`' `GenerationalCache`,
`HellTreeCacheCapacity`, cleared by `WorldSeed.Apply`) and `HellTrees.Stamp` (`Stage.HellTrees`, after
the hell stamp on both the wholly-hell and the mixed path) writes each tree's cells with their state
bytes over whatever flora stood there, so a tree is the same whichever chunk stamps it, and a vine cut by
a tree cell stays held by it. The species are ordinary tree JSON with no `worldgen` block (so
`TreeGenerator` never plants them): `fungal_tree` (oak logic, a pad cap of `fungal_cap`, an opaque-tiled
leaves block that, like every leaf block, passes light, casts no AO and hides no face, glowing teal at 9
in the fungal glow's palette slot, and decays like leaves) on
`fungal_log`, and the leafless `charred_tree` on `charred_log`. The plants are cross models
(`glow_fern` teal light 6, `ember_bloom` ember light 5, both `needsSupport`), and `hell_vine` is
`support: "above"`: each cell is held by the one over it, a solid ceiling or a vine, so a vine cut
anywhere drops what hangs below, through mining's recursive pop and, with this change, the blast scan
(`TntManager.PopIfUnsupported` follows a popped block's dependents past the blast box) and leaf decay
(`LeafDecayManager.PopUnsupportedAround` recurses), with no new block flag. Every tile comes from
`tools/generate-hell-textures.py` (the `bark`, `rings`, `fern`, `bloom` and `vine` painters).
`HellFloraTests`; `visual-examples/hell-flora.json` (`VOXEL_TIME=147`) frames the Fungal Grove and the
Ashlands.

**The kiln** (`Simulation/KilnManager.cs`, keyed by the structure's anchor cell) is the furnace half.
How a kiln behaves is its anchor block's `KilnProfile` (the `kiln` object, `BlockType.Kiln`), which
`KilnState.Profile` caches from `KilnState.AnchorId` whenever a kiln is registered, restored or
mirrored (`KilnManager.ProfileOf`; never saved or sent, so the state, `stations.json` and
`StationNetCodec` are shared by every kiln size). Right-clicking it with an item declaring
`fuel.burnSeconds` stores a unit, up to the profile's `FuelCap` of **one** kind; a firestarter only lights a kiln that has fuel
(`CanIgnite`), and igniting spends the first unit at once, so four coal burn for four minutes and
then go out. A crucible (crafted from mud bricks) right-clicked in stands in the hearth, and
anything with a melt recipe right-clicked after that
charges the vessel with **pending** metal — gated by `KilnManager.CanAccept`, which needs room
(every metal, molten and pending, counted together) and a metal that may join the vessel
(`AlloyRules.CanMix`): a **plain kiln** (`canAlloy` false) keeps a vessel to **one metal** and refuses
another while any of the first is in it, molten or pending; an **alloy kiln** (`canAlloy` true) also
lets the parts and the result of one alloy share it, charged in any order. Charging needs no fire. A
lit kiln then turns `KilnManager.MeltChunkMl` (the same for every kiln) of one metal's pending
charge molten every `KilnProfile.MeltSeconds` of its profile (the base 2 s over its `meltSpeed`): the
lowest-id metal that may melt (`AlloyRules.NextToMelt`, `KilnManager.MeltingMaterial`). In an alloy
kiln a part **waits**, solid, while another part of its alloy is in the vessel, and each lit tick
first turns every full set of parts, molten or pending, into the same ml of the result as pending
charge (`AlloyRules.Resolve`), which then melts at the kiln's pace: so 600 copper + 200 tin become
800 molten bronze in eight chunks, the surplus of an unfinished set waits for the rest, and the ml in
always equals the ml out. A lone metal melts in an alloy kiln as in a plain one; a vessel mixed in an
alloy kiln and carried to a plain one melts each metal on its own there. Melting **pauses**
rather than resets when the fire dies or the vessel is lifted out: `KilnState.MeltLeft` simply
stops ticking, so relighting resumes the partial chunk. `MeltProgress01` and `DisplayMoltenMl` are
what the render pass reads and neither depends on `Lit`, which is what keeps the drawn pool rising
smoothly between chunks and stops it dropping the instant the last unit burns out.
`DisplayPendingMl` is their conservation complement (drawn pending + drawn molten = `TotalMl`),
and `KilnRenderer.PendingScale01` turns it into the lump of the last input
(`CrucibleComponent.PendingInputId`) hovering over the rim, full-size while a whole unit is solid
and shrinking to nothing as the last unit melts — so the charge is never invisible and never cuts
out. The pool is every molten metal together, tinted as `KilnManager.PoolMaterial` (the metal with the
most molten ml), and the tooltip of a carried crucible lists each metal's molten and unmelted ml.
Breaking the structure drops the stored fuel and the crucible at the mined cell — the metal
inside is lost.

**The alloy furnace** (`alloy_furnace` / `alloy_furnace_part`, `multiblock.size` 3) is the second
kiln: a 3x3x3 hollow shell of `alloy_bricks` (one mud brick block, a copper and a tin ingot each,
`recipes/alloy_bricks.json`; the tile from `tools/generate-alloy-bricks-texture.py`) pressed by the
same converter, with `{"fuelCap": 8, "meltSpeed": 1.5, "canAlloy": true}`. It is the same
`KilnManager` state, save and wire shape; only its profile and its model differ. Where the render
pass (`StationPasses.RenderKilns`) draws the crucible, the pending lump and the fuel row, where the
fire burns (`KilnManager.LitCells`, the flame and point light `AmbientEffects` gathers) and where
the hearth's light is sampled are its `KilnLayout` (`KilnProfile.Layout`), which mirrors its model
`assets/models/block/alloy_furnace.json` (a deep chamber, x 8..40, y 4..30, z 0..36 in 0..48 model
units, wide enough for eight fuel units in one row) the way `KilnLayout.Kiln` mirrors the mud brick
kiln's; the crucible bowl's own sizes stay on `KilnRenderer`. `KilnRendererTests` and
`KilnContentTests` hold both kilns to their layouts; `AlloyFurnaceTests` converts and reverts one in
a live world; `visual-examples/alloy-furnace.json` (`VOXEL_TIME=147`) builds, converts, loads, lights
and alloys one.

**Casting** (`Simulation/CastingManager.cs`, one `CastState` per mold cell) is the other half.
Right-clicking a fired mold with a crucible binds that tool instance and pours at
`CastingManager.PourMlPerSecond`, draining the stack a little every frame. An empty mold takes, of
the vessel's molten metals it has a cast recipe for, the one with the most ml (`CastingManager.PourRecipe`,
the lowest material id on a tie), and a half-filled one only more of the metal it was started with,
so a mixed crucible pours one metal and keeps the rest. The pour **pauses**
(losing nothing) when the crucible is deselected, carried out of `World.InteractionReach` or emptied
of that metal, and another right-click resumes it. The frame that completes
the fill starts the cooling instead of cooling in the same frame, so the two phases are never
ambiguous; `Cool01` then runs 0..1 over the recipe's `coolSeconds` and the liquid dissolves into
the result through `CastingRenderer.LiquidAlpha` / `ResultAlpha`, a complementary `MathUtils.SmoothStep`
pair that always sums to 1 (no hard cut at the end of the pour). A finished cast is picked up with
another right-click, and breaking the mold — **creative included** — drops a finished cast and only
a finished cast; metal still cooling is lost. Like the campfire, quern and kiln registries, cast
progress is saved with the world (`stations.json`, see "Saves"; a pour comes back paused and a right-click resumes it), and an unloaded mold cell freezes
rather than being forgotten.

The right-click chain in `RightClickAction.TryUseSelected` runs `TryCookOrCollect` -> `TryQuernInteract` ->
`TryKilnInteract` -> `TryCastInteract` -> `TryToggleOpenable` -> `TryOpenSpellTable` (all on `BlockInteractions`) -> a held spell staff's cast (which never places) -> the item's own `use` action or block placement, and the
first one that claims the click wins (`TryUseSelected(maxDistance, out RightClickOutcome)` hands back which). On a kiln: an empty hand lifts the crucible out (and puts it
straight back when the tools pool is full), a crucible stands one in, a melt input charges it, fuel
goes into the hearth, a **firestarter falls through** to its own `Item.OnUse` so it can strike and
spend durability, and a **block in hand falls through** to placement against the structure — any
other click on a kiln is spent. On a mold: a crucible click is spent either way, so a refused pour
never places anything, while a block in hand still falls through and molds can be stacked against
each other. After the mold step comes `TryToggleOpenable`: a click on an openable block (a
trapdoor, either half of a door, which swings both) flips its open bit whatever is in hand, so
nothing places against it. Then `TryOpenSpellTable`: a click on a `spellBuilder` block with anything
but a block in hand is claimed as `RightClickOutcome.SpellTable` with no side effect and no swing (the
local player's `GameplayInput` opens the builder from that outcome; a server's replay changes nothing),
so a staff in hand never casts at the table.

### Spell part schema (`assets/data/spell/<kind>/<name>.json`)

The folder is the part's kind (`element`, `shape`, `modifier`, `trigger`; `SpellPartKind`, folder name
`SpellPart.FolderOf`), never a field. Parts are keyed `<kind>/<name>` (`SpellPart.Key`), so an element
and a modifier may share a file name, and they take no byte ids. A missing `spell/` folder or kind folder
is no parts (no trigger ships); an unknown kind folder or a `.json` lying directly in `spell/` is an error
naming it, and every part error names its file. `SpellPartLoader` (`Content/Spells/`) runs in
`ContentRegistry.Load` right after the status effects (which follow the tree species) and publishes `SpellPartRegistry` (`TryGet`,
`TryGetByKey`, `Require`, `All`), which `ContentRegistry.Reset` clears. Tests read temp folders through the pure
`SpellPartLoader.ReadParts`/`Compile`, never by replacing the published registry.

| field | default | meaning |
|---|---|---|
| `name` | file name | display name |
| `costShare` | required | the share of `ManaConfig.StartMax` the part adds to a spell's cost, in 0..1, exclusive of 0 |
| `complexity` | required | 0 or more; summed over a recipe it feeds the fizzle chance |
| `tags` | required | at least one non-blank, distinct, lowercase tag (compared ordinally): what a staff tier allows (MG-3). Their own namespace, unrelated to the item tool tags in `data/tags` |
| `damageFactor` | 1 | modifier only, finite and > 0: multiplies the spell's damage |
| `rangeFactor` | 1 | modifier only, finite and > 0: multiplies the spell's range (projectile flight) |
| `effect` | none | element only: the status effect the element applies on hit (`SpellPart.Effect`, read by `SpellEffects`), the name of a loaded `assets/data/effects` file; an unknown or blank name, or an `effect` on any other kind, is refused naming the file. `fire` -> `burning`, `frost` -> `frostburn`, `nature` none |
| `impact` | none | element only: the list of things the element does to the cell it lands on (`SpellPart.Impact`, a `SpellImpact` flag set): `ignite` burns `flammable` blocks to air, `freeze` sets fluid sources at or below the impact to their `freezesTo`, `thaw` melts `meltsTo` blocks to a source (these three within `SpellConfig.ImpactReachBlocks` and, for a self cast, around the caster's feet), `prime` primes a dry primable block (TNT), `light` lights a fueled kiln or a full, unlit branch-pile campfire, `grow` grows the aimed tree or plants `plant`. An unknown or repeated name, or an `impact` on any other kind, is refused naming the file. `fire` ignite, thaw, prime, light; `frost` freeze; `nature` grow |
| `plant` | none | element only, and only with `grow`: the tree species (a `data/trees` file name) a `grow` impact plants where it lands on ground holding no tree; an unknown or blank species is refused. `nature` -> `oak` |
| `delivery` | `projectile` | shape only: how the shape delivers its element (`SpellPart.Delivery`), `projectile` (a straight bolt, `SpellProjectile`) or `self` (the caster); anything else, or a `delivery` on another kind, is refused naming the file. `self` -> `self` |

`SpellRecipe` (a flat list of parts) compiles through `Compile(allowedTags)` to a `SpellStats`, GL-free
and pure (no roll, no registry read). The verdict (`SpellVerdict`) checks, in order: exactly one
element, exactly one shape, at most one trigger, no modifier twice, and, when a tag set is given (the
staff's), every part allowed by it — a part passes when **any** of its tags is in the set. `Cost` is
`ManaConfig.StartMax` times the summed `costShare` (the unit `PlayerMana.TrySpend` takes), so every
added part raises it; `FizzleChance` is `SpellRecipe.FizzleChanceFor` of the summed complexity: 0 up to
`SpellConfig.FizzleFreeComplexity`, then `FizzleChancePerComplexity` a unit, capped at
`FizzleMaxChance` — a chance only, the roll belongs to the cast (`SpellCaster`, rules in `Magic/CastRisk`: a fizzle fires no effect and backfires on the caster). `DamageFactor` and
`RangeFactor` are the products of the parts' factors. Every number is computed even for an invalid
recipe, so the spell builder shows it live; a cast must check `IsValid` before spending. A new part
is a JSON file, never a `switch` on its name. Slice 1 ships the elements `fire`, `frost` and `nature`,
the shapes `projectile` and `self` and the modifiers `power` (raises damage and cost) and `range`
(lengthens flight and raises cost), all tagged `basic`; every valid combination costs 20-40% of the
starting pool (the `ManaConfig` design rule). `SpellRecipeTests`, `SpellPartLoaderTests`.

**What a cast does (MG-5).** `Magic/SpellEffects` (owned by `World`, built only where the world is
authoritative, so never on a client) is the `World.SpellEffect` hook. A `projectile` shape launches a
`SpellProjectile` from just in front of the eye: straight at `SpellConfig.ProjectileSpeed` with no gravity,
sub-stepped (`ProjectileMaxStepBlocks`), stopped by a solid cell through `BoxSweep`, by the first non-air
cell its center enters (water, lava, leaves, a plant) or by a mob (`ThrownProjectile.FindMobHit`), and
spent after `SpellImpactRules.RangeFor` (`ProjectileRangeBlocks` times the `RangeFactor`). Bolts tick in
`World.Update` right after the thrown items and never hit players. A mob hit deals
`SpellImpactRules.DamageFor` (`BaseDamage` times the `DamageFactor`, at least 1) through
`MobStrikes.StrikeMob` and applies the element's `effect`; a cell hit runs `SpellBlockImpact` (the
`impact` table above). A `self` shape applies the effect to the caster (the camera or the server's
player, so a creative or flying caster is immune) and the element's ignite/freeze/thaw around its feet;
prime, light and grow are aimed only. A non-server world shows a bolt as sparks of its effect's tint
(`SpellSparks`). Known gaps: bolts are not sent to clients (the caster's own client flies a cosmetic copy,
`SpellBoltPreview`, see "Cast feedback" below) and not saved; a frostburned body over lava
crusts it. `SpellImpactRulesTests`, `SpellProjectileTests`, `SpellEffectsTests`.

### Status effect schema (`assets/data/effects/<name>.json`)

One file per effect; the file name is its name (`StatusEffectRegistry.Require(name)`), and an effect's
byte id is its index in file-name order (`StatusEffect.Bit` = `1 << Id`). At most
`StatusEffect.MaxKinds` (8) may exist, because every wire form is a one-byte mask; a ninth file is
refused naming the folder. `StatusEffectLoader` (`Content/StatusEffects/`) runs in
`ContentRegistry.Load` right **before** the spell parts, so an element's `effect` validates against it,
and every error names its file. Tests compile in-memory effects through the pure
`StatusEffectLoader.Compile`/`ReadEffects`.

| field | default | meaning |
|---|---|---|
| `name` | file name | display name |
| `durationSeconds` | required | finite and > 0: how long one application lasts (times the caller's scale, clamped to 0..`StatusEffectConfig.MaxDurationScale`) |
| `tickSeconds` | none | finite and > 0, required exactly when the effect deals damage or has a block flag, refused otherwise: the pulse interval |
| `damageShare` | none | in (0, 1]: damage per pulse as a share of the target's max health, at least 1 (`StatusEffectSet.DamagePerTick`) |
| `moveFactor` | 1 | in (0, 1]: scales the walk and swim speed (the player's `PlayerPhysics`, a goblin's walk, a slime's hop clock) |
| `attackFactor` | 1 | in (0, 1]: slows the local player's hit gap (`Mining.HitGapSeconds(held, factor)`: punch cadence, mining hits, the axe chop), a goblin's throw and melee cooldowns and a slime's contact cooldown |
| `blocks.ignite` / `blocks.freeze` / `blocks.thaw` | false | what each pulse does to the cells around the body (`StatusEffectBlockAction`): burn `flammable` blocks to air, freeze a fluid source under the feet into its `freezesTo`, thaw a block into its `meltsTo` fluid source |
| `tint` | none | sRGB `[r, g, b]`, 0..255 each: the mob tint (`uStatusTint`, decoded to linear) |
| `particle` | none | a `FireParticleKind` name (`ember`, `smoke`, `steam`, ...), matched case-insensitively: what an affected mob sheds |
| `icon` | none | a `.png` path under `assets/`: the player's HUD icon |
| `deathMessage` | none | the phrase after the player's name in a death line ("burned to death") |

An effect must do something: damage, a factor below 1 or a block flag. Two ship: `burning` (fire; 6 s,
5% a second, ignite and thaw, orange, embers) and `frostburn` (frost; 8 s, 2% every 2 s, half speed, 0.6
attack, freeze, pale blue, steam), first guesses.

**Rules.** A target's effects are one `StatusEffectSet` (`Status/`, GL-free, allocation-free after the
first apply): re-applying an active effect refreshes its remaining time to the larger of the two and
never stacks its damage; damage lands once per `tickSeconds` and never after the duration; the movement
and attack factors are the **lowest** among the active effects. Effects tick **only in the
authoritative simulation** (singleplayer and the server): a mob's in `Mob.UpdateHazards` (cleared once it
is dying), the player's as under "Status effects" above; a client's sets are mirrors
(`SetMirrorMask`, unknown bits dropped) that never tick. **The one API** MG-5 calls is
`StatusEffects.Apply(target, effectName, durationScale)`, with overloads for a `Mob` (refused while
dying or dead), an `IMobTarget` (its default `Effects` member is null, and an immune, dead or effect-less
target refuses) and a `PlayerHealth` (refused while immune or dead); never call it on a client. The
visual-scenario `effect` op (`visual-examples/status-effects.json`, `VOXEL_TIME=147`) applies one to every
mob or to the player.

**Block interaction.** Each pulse ORs the effect's block flags into the set's pending actions, which
`MobDirector` drains after the mob managers run (mobs at their feet, targets at the eye less
`Camera.EyeHeight`) into `StatusEffectBlockWriter`, built only when not a client. The writer scans the
cells within `StatusEffectConfig.BlockReachBlocks` horizontally of the feet, from the cell under them to
the one above, asks the pure `StatusEffectBlockRule` (freeze only a fluid source whose top is at or below
the feet, so a body never freezes itself in), skips an unloaded cell, a cell that holds a
support-fragile neighbor up (`HoldsSomethingUp`, so it never needs a pop path) and a fluid cell a
falling cube overlaps, and stops at `MaxBlockWritesPerPulse` per body and `MaxBlockWritesPerFrame` in
all. Each write is `Chunk.SetBlockRaw` (saved, journaled to clients on a server) followed by every
edit-path hook: `UnmarkPlayerPlaced`, an asynchronous `MarkAreaDirty`, `FluidManager.ScheduleAround`,
`LeafDecayManager.ScheduleAround`, `FallingBlockManager.TryTopple` above, the LOD edit hook, and for a
tree block `TreeGenerator.PoisonCanopy` plus `LodManager.MarkEditedColumn`. No loot, no
`World.BlockEdited`. `StatusEffectBlockTests`.

**Visuals.** An affected mob is tinted by `uStatusTint` (a `vec4` in `test-fragment.glsl` mixed into
the albedo before the hit flash at `StatusEffectConfig.MobTintStrength`, zero by default and reset every
frame on the plain program, so everything else is byte-identical; `MobPasses` sets it from
`StatusEffectSet.TryGetTint`, the lowest-id tinted effect) and sheds its effects' particles through the
campfire `FireParticleSystem.Puff` (`AmbientEffects.UpdateStatusParticles`, nearest first, at most
`MaxParticleEmitters` mobs, `ParticlesPerSecond` each, never on a server). The HUD icons come from
`tools/generate-effect-icons.py`. Tests: `StatusEffectLoaderTests`, `StatusBlockFlagTests`,
`StatusEffectSetTests`, `StatusEffectApplyTests`, `StatusEffectFactorTests`, `StatusEffectBlockTests`.

Known gaps: effects are not saved (`player.json`, `entities.json`); other players' effects are not shown
(the `PlayerList` carries none); the player's slowdown is client-trusted (the server rubberband only
caps speed from above); sword and slice swing durations are not scaled by the attack factor; a blast
does not consult effects; a slime's split children do not inherit its effects; and icebergs are not
consulted, so a frostburned rider may freeze water inside a berg's submerged boxes.

## Architecture: threading model (the most important thing to preserve)

Chunk work is split across threads, and OpenGL calls are only legal on the main thread:

1. `ChunkGenerationWorker` runs dedicated background threads that scan positions around each streaming focus (closest first; `World.Focus` is the camera, or every connected player on a server, and the workers round-robin over the foci with one `ChunkScanFrontier` each) and call `ChunkManager.QueueChunk`, which creates the `Chunk` and generates its block data **on that background thread**.
2. Meshing is scheduled by `ChunkMeshScheduler.ProcessDirtyChunks` (behind `ChunkManager.Update`; main thread, per-frame budget `DirtyStartBudget`, default `MaxDirtyChunksPerFrame`) and runs as plain thread-pool work items (`ThreadPool.UnsafeQueueUserWorkItem`): at most `StreamingConfig.MaxConcurrentMeshGenerations` runners are in flight, each draining a shared queue of pending builds. Mesh generation produces CPU-side `ChunkMeshData` only.
3. Completed mesh data lands in `ChunkMeshScheduler`'s `_completedMeshQueue` and is uploaded to the GPU on the **main thread** in `ChunkMeshScheduler.ApplyCompletedMeshes`, sorted closest-first and capped at `MeshUploadBudget` (default `MaxMeshUploadsPerFrame`) per frame.

Rules that follow from this:

- **Never call GL functions (buffer uploads, `Mesh` creation/disposal, pool uploads/frees through `ChunkGeometryPool`, shader use) off the main thread.** Background work must only produce plain data handed over via the concurrent queues.
- A chunk is only meshed when its block data is ready **and all 6 cardinal neighbors exist with ready data** (`ChunkMeshScheduler.HasAllNeighbors`) — meshing reads neighbor blocks for face culling and cross-chunk AO. Breaking this reintroduces holes/seams at chunk borders (see git history). That scheduling check can go stale (the build waits in the pending queue for a free runner and an unload may take a neighbor away meanwhile), so `Chunk.GenerateMeshData` (`ChunkMeshBuilder.GenerateMeshData`) re-resolves the whole 3x3x3 neighborhood once up front and returns `null` when a face neighbor is gone; callers must then re-mark the chunk dirty rather than apply anything. Only the edge/corner neighbors may be absent, and those read as air (they feed light and fluid corner heights only). The same holds for the 3x3 chunk **columns** a build reads (`ChunkManager.ColumnSource`, see "Chunk meshing"): `ChunkMeshScheduler.ProcessDirtyChunks` drops, and `ImmediateRemesh` re-adds once, a chunk whose nine columns are not all `IColumnSource.IsReady` (an all-air chunk, whose build reads no column, is exempt; a fully enclosed one cannot be told apart without its columns and waits too), `ClientChunkStreamer.ApplyColumn` re-dirties the 3x3 chunk columns on arrival, which is what wakes it, and a build that still finds a column missing returns `null` like a vanished neighbor. The worldgen source is always ready, so singleplayer and the server never wait. A mesh build reaches worldgen only through `WorldgenColumnSource` and the seed-hashed pebble, branch-pile and snow emitters, which is why a seed swap still waits for `ChunkManager.DrainMeshWork`.
- Shared state uses `ConcurrentDictionary`/`ConcurrentQueue`; `Chunk.IsBlockDataReady` is `volatile`. Keep new shared state lock-free in the same style. `ChunkManager.Chunks` is a `ChunkMap` (`Chunks/ChunkMap.cs`) wrapping a `ConcurrentDictionary<long, Chunk>` — it is a wrapper, never a `Dictionary` behind a lock, because chunk-gen workers write it and mesh-build pool workers read it.
- Editing a block on a chunk border must also remesh the affected neighbor chunk(s) — block edits use `ChunkManager.ImmediateRemesh` / `MarkDirty`.
- Performance tuning knobs all live in the per-domain config classes under `src/Config/` (per-frame budgets and thread counts in `StreamingConfig`, worldgen knobs in `WorldGenerationConfig`, and so on); change constants there rather than hardcoding budgets elsewhere.
- Animation curves (interpolation, easing, one-shot pulses) belong in `MathUtils`, not inline in gameplay or render code — everything time-driven should be lerped through one of those helpers rather than snapped.

Other invariants:

- World/chunk coordinates: `Chunk.Position` is in chunk units; world pos = chunk pos × `Chunk.Size` (16). Chunks are cubic and stacked vertically too (3D chunk grid, not 2D columns).
- Blocks are `byte` IDs; `0` is air. Everything else about a block comes from `assets/data/blocks/<name>.json` through `ContentRegistry` (see "Content" above), never from a new `switch` on the id. Hot paths read the flat mirrors (`BlockType.IsSolid/Occludes/CastsAO`, `BlockType.Emission`, `BlockType.PassesLightById`) rather than `BlockType.ById[...]`, so keep new per-id flags in that shape.
- **Per-block state byte**: alongside `Chunk._blocks` each chunk has a lazily allocated `volatile byte[]? _state` (null = all zero), read through `Chunk.GetLocalState` / `ChunkManager.GetStateGlobal` / `TryGetCell` and written only by `Chunk.SetBlockRaw` (no remesh, no scheduling — the fluid tick batches those). Six features share it, one per block: a **horizontal facing** (a `facing: "horizontal"` block such as the wall torch or a hollow fallen log: bits 0-1 the facing, rest 0, written by placement through `Placement.TryMount`, or by `FallenLogGenerator.Stamp` into the generated state array), **fluid levels** (`FluidState` decodes it as source `0`, falling `8` or flowing level `1..7`), **stack counts** (a `stackMax` block's 0 = untouched worldgen, 1..`stackMax` an explicit pile), a **multiblock's facing and offset** (`Multiblock.EncodeState`: the facing in bits 0-1, each 0..3 offset's low bit in bits 2-4 and high bit in bits 5-7, so a 2x2x2 reads `facing | dx<<2 | dy<<3 | dz<<4`; written by `Multiblock.Convert` through `SetBlockRaw`), **tree wood** (`WoodState` on a `tree_wood` block: bits 0-3 the thickness code, `FullCube` = 15 being a whole 16 px cell, bits 4-6 the parent direction in `Chunk.CubeFaces` order or `ParentRoot`/`ParentDetached`, bit 7 the shell flag) and **leaves** (`LeafState` on a leaf block: bits 0-2 the hydration, bit 3 the persistent flag) — see `### Trees`. The tree states are written by `TreeGenerator.Stamp` into the generated state array, by `TreeGrowthWriter` and `LeafDecayManager` through `SetBlockRaw`, and by placement, which passes an explicit state to `PlaceBlockAt` (`BlockPlacer.PlaceBlockAtFace` gives a placed log `WoodState.Detached` and a placed leaf a persistent `LeafState`). Which meaning a byte carries is decided entirely by the block id that owns the cell, so a new user must pick a block kind, never a spare bit. **Every write that is not one of those zeroes it** (`RemoveBlockAt`, `ClearBlockAt` and `PlaceBlockAt` with its default state `0` — which also treats a fluid cell as replaceable), and `Multiblock.Revert` likewise writes its source block with an explicit `0`, so a stale level, count, offset, thickness or hydration can never outlive the block that owned it. Meshing gets it as a second padded snapshot from `ChunkMeshBuilder.FillPaddedSnapshot(padded, paddedState, neighbors, neighborStates)`, filled from the neighborhood `ChunkMeshBuilder.TryResolveNeighbors` resolved for the build; the fluid simulation must read cells through `TryGetCell`/`ChunkAt`, never `GetBlockGlobal`, which reports unloaded chunks as air. A seventh meaning belongs to the **openable** blocks (an `open` object in their JSON: the trapdoor, and both halves of a door, which always carry the same byte): `DoorState`, the facing in bits 0-1 like any facing block plus `DoorState.OpenBit` (bit 2), the rest zero; placement writes it closed, and the right-click toggle (`BlockInteractions.TryToggleOpenable`, through `ToggleOpenCell`) flips the bit alone through `Chunk.SetStateAt`. The mesher and `BlockShape` index a facing block's pre-turned models and boxes with `BlockType.ShapeIndex(state, count)` (`state & (count - 1)`), so four-shape blocks keep reading the facing and an openable block's eight read the open bit too.
- **Station block entities**: `CampfireManager`, `QuernManager`, `KilnManager` and `CastingManager` are all owned by `World`, all **main thread only**. A lit campfire, a spinning quern, a kiln's fuel and crucible and a mold's cast progress are saved in `stations.json` (see "Saves"): each manager has a `Capture()` and a `Restore(...)` over `StationSaveData` (content by `SaveNames` name, never byte id), `WorldPersistence` restores them at construction and writes them on every autosave and flush, and **a new station registry must add its own pair and list**. Each is keyed by the cell (the kiln by its structure's anchor), each is ticked from `World.Update` and each has a `NotifyBlockRemoved` that `BlockEditor.RemoveBlock` calls so a broken station drops what it held. `AmbientEffects` gathers every burning cell once a frame (campfires and lit kilns in every update, plus goblin torches in the full `World.Update`; a client's `UpdateClient` gathers the stations only) and `World.FireCells` exposes the list, which feeds both the fire billboards (`FireParticleSystem.SpawnOffset`) and the warm point lights (`FrameLighting.FireLightOffset`, which `RenderManager` still forwards for the tests), so a new fire source only has to appear in that list. Their render passes — `StationPasses.RenderQuerns`, `RenderKilns`, `RenderCastings` — sit inside the existing `entityPass` profiler span rather than adding sections of their own. On a multiplayer server they tick in the server's `World.Update` and a pour is bound to the pouring player (`CastState.PourSource`, its `PlayerContext`, which pauses once the player left); a client never ticks them: its managers are display-only mirrors replaced by the server's `StationState` snapshot (each manager's `WriteNet`/`ReadNet`, `StationNetCodec`), and only a quern top keeps turning between snapshots (`QuernManager.Animate`).
- **Fluid tick**: `Simulation/Fluids/FluidManager.cs` (owned by `World`, main thread only) is a *scheduled-cell* simulation, not a scan. `World.Update` calls it once per frame after `BlockEditor.UpdateToppling`; it runs whole ticks every `SimulationConfig.FluidTickSeconds` (0.25 s), at most `MaxFluidTicksPerFrame` of them per frame, and drops the leftover time so a hitch never leaves it spiraling. Each tick takes the set of cells scheduled by the previous one (packed as `long`, 21 bits per axis, through `CellKey` (`src/Chunks/CellKey.cs`), which forwards to the one bit math in `ChunkKey` (`src/Chunks/ChunkKey.cs`), the packer of a **chunk** position for `ChunkMap` and for `ChunkManager`'s dirty/queued/generating/placement dictionaries; `CellKey`'s keys are block-scale, `ChunkKey`'s are chunk-scale, and neither floors nor truncates differently from `VoxelCoords.ChunkPosOf`), recomputes every **non-source** fluid cell from its neighbors (Minecraft's `getNewLiquid`: two adjacent sources over ground that cannot drain become a source themselves, fluid directly above means falling, otherwise one level thinner than the fullest neighbor, and nothing left means the cell drains to air), then spreads it downwards, or sideways toward the nearest hole within `FluidSlopeSearchDistance` (ties spread evenly). The level rules are per-fluid data (`flow.*` on the block: lava updates every other tick, spreads 3 instead of 7 and never forms new sources). Contact hardening runs before all of it: a fluid cell touching its `hardensTouching` fluid becomes `hardensTo` (lava beside water becomes cobblestone), and a flow settling against a fluid that hardens on its touch crusts over the same way, so water reaching lava also yields cobblestone whichever side ticks first. Spreading only ever writes into non-fluid `IsReplaceable` cells, popping the fragile block it washes away through `DropManager.SpawnLoot`; a cell that already holds fluid recomputes itself on its own tick. Writes go through `Chunk.SetBlockRaw` and dirty only the cell's own chunk plus the face-adjacent chunk on each axis where the cell sits on a border, batched into `MarkDirty` (`FluidCells` over its own `ChunkCellCache`) at the end of the tick — not `ImmediateRemesh`, and never `MarkAreaDirty`, because a fluid passes light and casts no AO. A cell reschedules itself and its neighborhood exactly when it changed or wrote into a neighbor, so settled water costs nothing.
- **Waking worldgen water**: the sea is generated as static source cells, so a cave or entrance carved into a land column beside it would leave a wall of water standing against worldgen air that nothing ever schedules. `FluidManager.WakeLoadedChunks(chunks)` — called from `World.Update` each frame, immediately before the fluid tick — fixes that by scheduling the frontier of its water against a carved cavity when it loads, so the sea pours into whatever breached it exactly like Minecraft water does when you dig into an ocean. Only cells at or below their column's natural terrain top count (`FluidWakeRules.IsCarvedFlowTarget`), so open air above the terrain — a ravine, a beach, a hillside — is never woken and generated water stays static across it. It has two halves: the **interior**, collected on the generation worker at the end of `ChunkTerrainFill.GenerateBlockData` into `Chunk.FluidFrontier` through `FluidWakeRules.IsWakeTarget` (local block indices of every fluid cell with a `FluidWakeRules.IsCarvedFlowTarget` neighbor in -X/+X/-Z/+Z/-Y — never +Y, since water does not flow upward and every sea-surface cell has air above it; only its own `_blocks` are read, so worldgen stays a pure per-chunk function), and the **borders**, which need both chunks loaded and are therefore scanned on the main thread (`FluidWakeScanner.WakeBorders`) over the shared 16x16 plane of each face neighbor, scheduling whichever side holds the fluid when the other side is a carved cavity (the vertical faces only ever schedule the fluid above the hole). Chunks entirely above every level they could share with a neighbor (`ColumnData.MaxWaterLevel`, never below `SeaLevel`) skip the border scan — generated water cannot reach them, and player-placed water is woken by the edit paths. The queue of newly generated chunks is `ChunkManager.TryDequeueLoaded`; a consumer must re-check that the chunk is still the live one at its position, because an unload can race the queue. Once the frontier has settled, it costs nothing again. Deep lava pockets wake through the same path: the frontier scan is fluid-generic, and chunks at or below `LavaMaxY` report theirs even when they never held sea water.
- **Every block edit path must call `FluidManager.ScheduleAround(cell)`** (or `ScheduleBox(min, max)` for a batch) or the water beside the edit will never notice it: `BlockEditor.RemoveBlock`, `BlockEditor.FellWood` (per cleared cell, and in both single-block branches), `BlockPlacer.PlaceBlockAtFace` (which every placement ends in: `BlockPlacer.TryPlaceBlockFromRaycast` through `PlaceAgainstFace`, and the item-use `IItemUseWorld.PlaceBlockAtFace`), `BlockInteractions.ToggleOpenCell`, `TntManager.Detonate` (`ScheduleBox` over the blast box grown by one; the blast also **skips fluid cells**, since a dry crater under water would just refill from every side) and bucket edits through `FluidManager.SetCellImmediate` (`WorldItemUse`'s `ScoopFluidCell`/`PourFluidCell`), which is the only fluid write that remeshes synchronously. Mining never takes a fluid out: `BlockEditor.RemoveBlock` returns early on `BlockType.IsFluid`, so only a bucket removes fluids.
- **Every block edit path must also call `LeafDecayManager.ScheduleAround(cell)`** (or `ScheduleBox(min, max)` for a batch) or the leaves near the edit never re-derive their hydration: a cut limb's canopy would hang forever, and an orphaned wood cell would never crumble. `Simulation/LeafDecay/LeafDecayManager.cs` (owned by `World` as `_leafDecay`, main thread only) grows each scheduled box by its `ScheduleReach` itself, so a caller passes only the edited cells. The current call sites: `BlockEditor.RemoveBlock`; `BlockEditor.FellWood` (`ScheduleAround` in both single-block branches, one `ScheduleBox` over the whole felled batch, never one per cell); `BlockPlacer.PlaceBlockAtFace`, which every placement goes through (`BlockPlacer.TryPlaceBlockFromRaycast` and the item-use `IItemUseWorld.PlaceBlockAtFace`); the `IItemUseWorld` bucket and TNT-prime wrappers on `WorldItemUse` (`ScoopFluidCell`, `PourFluidCell`, `RemoveCellForPrime`), scheduled for uniformity; `BlockInteractions.ToggleOpenCell`; `TntManager.Detonate` (`ScheduleBox` over the blast box); and `TreeGrowthWriter` (one `ScheduleBox` over the box it wrote). A new edit path adds its call next to its `FluidManager.ScheduleAround`.
- **Gravity**: `Simulation/FallingBlockManager.cs` (owned by `World`, main thread only) topples `falls` blocks into `FallingBlock` entities and writes rested cubes back as blocks. Every edit path that can unsupport one must call `TryTopple` above it (`BlockEditor.RemoveBlock`, `BlockEditor.FellWood` per cleared cell, `BlockPlacer.PlaceAgainstFace` at the placed cell (the path of `BlockPlacer.TryPlaceBlockFromRaycast`) and `WorldItemUse`'s `IItemUseWorld.AfterPlaced` after an item's own placement, `TntManager.Detonate` via `CheckColumn` bottom-up over the blast box); fluids never count as support, so fluid edits need no hook. A landing cube slides with chance (8 - solid neighbors of its support) / 8 toward a random clear side, corners included (a corner needs its two flanking sides and the three cells above it clear), committing to its direction across landings, and drops straight once past the edge. Sand stacks on sand: a falls-block support never slides, so towers rebuild. A calm landing converts at once so a collapsing tower settles bottom-up instead of interpenetrating, and a cube that would rest over air pops out as a drop instead of floating. A live cube owns its cell: `BlockPlacer.PlaceBlockAtFace` refuses placements into one, cubes shove the player aside one-way (`Camera.PushOutOf`, never through walls, never while flying), and a cube motionless-but-unsettled for 5 s breaks into its usual loot instead of hovering. Shorelines generate from sand (`TerrainGenerator.IsBeachHeight`, gravel-capped where `IsGravelPatch`); the far LOD wears sand and gravel there too, the gravel read with one noise eval per cell.
- **TNT**: `Simulation/TntManager.cs` (owned by `World`, main thread only, never ticked on a client) runs the primed cubes (`PrimedTnt`). A cube lit by hand burns `TntManager.PlayerFuseSeconds` (1 s) and one lit by another blast `ChainFuseSeconds` (0.5 s), so a chain stays prompt; `PrimedTnt.WhiteFlash` ramps its strobe from 4 to 16 Hz over whatever fuse it was given (`FirestarterTests`). A blast shoves everything loose within `BlastRadius` away from its center (`BlastImpulse.At`: `BlastPushMaxSpeed` at the center falling linearly to zero at the radius, the direction tipped up by `BlastPushUpBias`, never growing with distance): at the end of `Detonate`'s clearing, before the falling-block sweep (so sand the blast undermines falls straight), the other primed TNT (`PrimedTnt.Push`, chained cubes at spawn), the drops (`DropManager.ApplyBlastImpulse`, the blast's own loot included) and the falling cubes in the air (`FallingBlock.Push`); then `MobDirector.PushFromBlast` (World's mob component, called once per blast from `World.Update`) takes each center `TntManager.Blasts` reports and shoves the living mobs (`Mob.Shove`, sideways) and the players through `IMobTarget.TakeHit` with no damage: the local camera's knockback in singleplayer, each `ServerPlayer`'s queued knockback on a server (its `PlayerStatus` carries it to the client); a creative, flying or dead player is immune. Gameplay, so a client, which never ticks TNT, pushes nothing (`BlastEffectTests`).
- Worldgen must stay deterministic and thread-safe (`FastNoiseLite.GetNoise` is read-only after setup; noise instances are configured once in static constructors and never re-tuned per call). All noise seeds derive from `WorldGenerationConfig.Seed` plus fixed per-channel offsets (`TerrainNoise.MakeNoise`, forwarded as `TerrainGenerator.MakeNoise`; every 2D channel is built by `TerrainNoise.Reseed`); `Seed` reads `WorldSeed.Value`, which `WorldSeed.Apply` sets (rebuilding every noise from it) before a World is built, never while one is live (see "Saves"). Before player edits, terrain obeys:
  - **Nothing generated is ever solid above `height(x,z)`** — heights come from `ColumnCache.Get(...).Heights` (or the `ColumnCache.GetHeights` wrapper) and already include ravines, which are cut into the heightmap so the far LOD sees them too. The ravine cut fades out across the shore band (`OceanShoreWeight`..0.5), so it never slices the ocean's edge; the block at `height` is solid unless a cave or entrance carved it.
  - Both carvers only ever **remove** blocks: `ChunkTerrainFill.GenerateBlockData` fills the columns (grass at depth 0, dirt down to `DirtDepth`, stone below), then `NoiseCaves.Carve` and `WormCarver.Carve` clear air out of the result and `_solidCount` is recounted.
  - **Water** fills every air cell up to the column's own level in a **wet** column, in `ChunkTerrainFill.FillWater` after both carvers and before trees and pebbles: an ocean floor, or a lake or river bed, where a hashed lake bowl or a warped river ridge channel carves the ground below it. There is **no single water plane**: `ColumnData.WaterLevel` (`TerrainGenerator.WaterLevelAt`) is sea level for the ocean, a river's own level for a river (sea level, except on the ramp of an elevated great lake, below), but a lake's **own** surface, so a pond rests in the ground it was dug into instead of at the bottom of a crater dug to sea level. A lake takes that surface from its own rim (`WaterBodies.TryLakeSite`, whose verdict is cached per lake cell, `WaterBodies.PondSiteAt`): the lowest ground on the rim minus `LakeBankDepth`, and a candidate whose rim climbs more than `LakeSiteFlatness` is dropped rather than dug into a hillside, which is what keeps every bank it floods against above the water. A candidate standing above `SeaLevel` is also dropped when a river channel crosses its footprint (`WaterBodies.RiverCrossesSite`, sampled every few blocks across the disc), because the rim is read from `LandHeight` before any river cut and the channel would carve a bank below the pond's surface (rule test `LakeTests.NoRiverChannelCrossesAnElevatedPond`). Its radius (skewed small by `LakeRadiusSkew`), depth and outline (wobbled by `TerrainNoise.LakeShapeNoise`, so a shoreline is ragged rather than a circle) all vary per lake. A second, much rarer **great lake** tier (`WorldGen/GreatLakes.cs`, BIOMEGEN §13 phase D) sits beside the ponds: one hashed candidate per `GreatLakeCellBlocks` cell, a union of one to three lobe discs of radius `GreatLakeMinRadius..MaxRadius` wobbled inward by `TerrainNoise.GreatLakeShapeNoise`, optionally with an island in its main lobe. Its verdict is cached per cell (`GreatLakes.SiteAt`/`BuildSite`): the surface is the lowest ground of a dense walk over each lobe's dry shore annulus minus `GreatLakeBankDepth`, a rim spread over `GreatLakeSiteFlatness`, any ocean or desert weight or a biome that holds no water on the rim or in a coarse interior pass, interior ground rising more than `GreatLakeInteriorRiseMax` over the rim, or any massif or lone-peak disc meeting a lobe refuses the site, and the shore ramp widens with the rim spread. A great lake never refuses a river: a cutting river anywhere in a lobe's shore annulus makes it **river-fed**. A lake standing above `SeaLevel` may carry a **river ramp** (`GreatLakeSite.RampLength`, `(Surface - SeaLevel) * GreatLakeRiverRunBlocks`): past its lobe edges the per-column river level (`GreatLakes.RiverLevelAt`) steps down one block every `GreatLakeRiverRunBlocks` from the lake's surface to `SeaLevel`, never rising away from the lake and never more than a block between neighbors, so a river reaching it meets it at its own surface; the ramp is kept only when it fits (at most `GreatLakeRampMaxLength`, its footprint within the lowest-hash spacing, out of the spawn clearing) and every river sample on it stands in lake-holding ground `GreatLakeRiverBankRise` over its level. A ramped lake may also get an **outflow**: a straight channel `GreatLakeOutflowHalfWidth` wide leaving radially from the lowest eligible point of its rim, running down the same ramp and tapering shut (`GreatLakeOutflowTaperBlocks`) inside it, kept only when its corridor holds for at least `GreatLakeOutflowMinLength`. A river-fed lake keeps its ramp and its own surface; a rim-fed one keeps a ramp only to carry an outflow; a river-fed lake whose ramp cannot be kept falls back to resting at `SeaLevel`, refused when its rim-chosen surface lies below `SeaLevel` or more than `GreatLakeRiverFedMaxDrop` above it. `WaterBodies.SampleSurfaceWater` is the one place the tiers compose, read by `WaterLevelAt`, `SurfaceWaterWeight` and `Compute`: it hands out the river level (the river carve targets `riverLevel - RiverBedDepth`, its lowland fade measured above that level, and `WaterLevelFor` stores it), folds the outflow into the river weight, and wherever the great-lake weight is above 0 it replaces the pond fields, and the river is scaled by `GreatLakes.RiverShare`, exactly 0 wherever lake water stands. `Compute` raises a column left dry under the highest level a ramp neighbor can hold to that level when `GreatLakes.RampRiverBeside` finds ramp river or outflow beside it (it only raises dry ground, so it never opens a leak), and its ravine guard stays at 1 to `RampLength + 1` past a lobe. A pond yields to a great lake at site level (`TryLakeSite` refuses a candidate `GreatLakes.NearAnyFootprint` finds within `GreatLakePondClearance` of a lobe grown by the guard, or by `GreatLakes.RiverReach` for a ramped lake), so no column belongs to both; and `Compute` feeds the great lake's ravine guard (1 inside a lobe, easing to 0 `GreatLakeRavineGuardBlocks` past it) times `WaterBodyThreshold` into `RavineCutFade`, so a ravine tapers in across the guard band and never cuts a bank or an island. Every great-lake output is exactly 0 (the river level `SeaLevel`) outside the accepted lobe discs grown by the guard (by `RiverReach` for a ramped lake) and within `GreatLakeSpawnClearRadius` (= `ReliefSpawnClearStart`), so land elsewhere is bit-identical but for ponds that yield within pond reach of a lake (`GreatLakeSiteTests`, `GreatLakeTests`, `GreatLakeRiverTests`, `LodReliefTests.TheFarLodSitsInsideTheChunkTerrainOverAGreatLake`). `TerrainGenerator.IsWaterColumn` is now just `height < WaterLevelAt(x, z)`, so the carve in `Compute` and the fill agree by construction, and the far LOD draws its sheet at the same per-cell level. Wetness reads the column's `ColumnData.Bed` — the profile before a ravine cut it — not the ravine-cut `Heights`, so a ravine that merely deepens an otherwise dry column holds no water instead of flooding as a narrow spike, while a ravine under a real lake or river just deepens that body (and the cut fades out across the shore band, so no ravine ever cuts the ocean floor). Every other below-level basin is left **dry** — the forest is not flooded wholesale now that ocean carries the bulk of the water — and a desert stays dry; sand and gravel ring actual water only (`TerrainGenerator.SurfaceWaterWeight` gates the height band), never a dry ravine floor in the band. Both the carve and the flood test are scaled by `TerrainGenerator.SurfaceWaterGate` (`WaterDesertFadeStart`/`WaterDesertFadeEnd`), which fades the body out over the last stretch before the desert, so a pond closes on its own forest rim instead of ending in a vertical wall where the hard forest/desert classification flips, while lakes away from a desert keep their full depth. Sea-floor caves still flood, nothing grows in water (wet columns top out in dirt instead of grass), and `IsSubmerged(height)` is only the waterline height test, since wetness is a per-column level now. Generated water is always a **source** (state `0`). A cold column's top water cell is then replaced by `ice` in `SnowGenerator.StampGround` wherever `SnowGenerator.IsFrozenWater` says so (see Biomes), so a cold ocean or lake freezes into a walkable sheet whose edge breaks into floes and open leads over the pack-ice band. `WaterEnabled = false` turns the water off; `LakesAndRiversEnabled = false` leaves oceans only.
  - **Lava pockets** pond over dry cave floors at/below `LavaMaxY` (`LavaGenerator.Stamp`, after the sea so flooded caves stay water): a patch mask plus a per-cell roll, buried under the terrain so ravines and entrances stay dry. Generated lava is likewise all sources; set `LavaEnabled = false` to turn it off.
  - **The barrier band** (`BarrierGenerator`, see "The barrier") is the one stamp that writes into carved air and fluids: inside its core every cell becomes barrier. It runs after every other stamp, so a later feature can never reopen it; its ramps replace solid, occluding ground only. `BarrierEnabled = false` turns it off.
  - **Solid clouds** (`CloudPatchGenerator`, see "Solid clouds") only replace air inside `CloudSlabBottomY..CloudSlabTopY`. A chunk entirely above the terrain and any water normally takes the empty early-out; one whose floor lies in the slab's `WriteBand` instead takes a short path (the cloud stamp, then the solid recount, so it is non-empty and meshes), which `StampGateTests` proves identical to the whole pipeline (`stampGates: false`). `CloudPatchesEnabled = false` turns it off.
  - **Hell** (`HellGenerator`, see "Hell") owns every cell at or below `HellTopY`: its stamp, run after the barrier (`Stage.Hell`), overwrites them all whatever the overworld pipeline put there, and its `WriteBand` is open-ended downward (`int.MinValue..HellTopY`). A chunk wholly in hell (`HellGenerator.Covers`) therefore skips the neighborhood scan, the carvers and every other stamp and only runs the hell stamp, the hell trees (`HellTrees.Stamp`, also run after the hell stamp on the straddling chunk), the solid recount and the frontier scan, which is bit-identical (`StampGateTests` proves it: the ungated `stampGates: false` path runs the whole pipeline there) and about 0.26 ms a chunk warm. Chunks load to any depth (the streaming sphere has no vertical cap); overworld cave slimes never spawn in hell (`MobSpawner.TryFindCaveSpawn`). `HellEnabled = false` turns it off and leaves stone.
  - Caves fade out within ~`CaveSurfaceFadeEnd` blocks of the surface (`WormSurfaceMargin` for worms), except inside entrance zones — sparse patches where `ColumnData.EntranceWeight` lifts the fade and caves reach the sky. The entrance weight fades out before the shore band (`EntranceShoreFade`, see `OceanShoreWeight`), so a cave never breaches a beach or the sea floor into a water-filled hole.
  - Worldgen is a **pure function of world position**: each chunk generates its own blocks with no cross-chunk writes, so neighbors agree at their borders without shared state. Worm paths are cached per 64-block XZ cell in `WormCarver` exactly the way column data is cached per chunk column in `ColumnCache.Get`, and so are the four tree caches in `TreeBatches` and `TreeCanopy` (see `### Trees`): the cell batches (`TreeBatches.BatchCache`, every tree rooted in one 64x64 XZ cell grown together, shared by `Stamp` on the generation workers and the first LOD ring's canopy on the LOD builder), the solo tree shapes (`TreeBatches.ShapeCache`, which `BuildBatch` reads as the neighboring cells' obstacles), the grown canopy tops (`TreeCanopy.CanopyCache`) and the far rings' crown-proxy canopy (`TreeCanopy.ProxyCanopyCache`), the last two read by their one engine caller `LodRegion.BuildMeshData` once per *fine* column of a region; and so are the massif and lone-peak site verdicts (`TerrainPeaks.MassifSiteAt`/`PeakSiteAt`, one accepted-or-rejected `TerrainSite` per site cell) and the great-lake verdicts (`GreatLakes.SiteAt`, one `GreatLakeSite` per great-lake cell, whose factory reads the massif and lone-peak caches one way: a lake reads the peaks, the peaks never read lakes, so nothing recurses); and so are the pond verdicts (`WaterBodies.PondSiteAt`, one (accepted, surface, floor) per lake cell, whose factory reads only heights, river weights and the great-lake footprints, never a water level, so it cannot recurse either); and so are the hell trees (`HellTrees`, one grown tree or none per hell tree cell and layer, whose factory reads only hell's own pure field). All eleven go through `GenerationalCache<TKey, TValue>` (`WorldGen/GenerationalCache.cs`): the cache is split into G generation dictionaries published as one immutable node swapped by `Interlocked.CompareExchange`, a read probes the newest first, a miss computes into the newest, and when the newest fills its `capacity / G` insert budget the node rotates and the *oldest* generation is dropped — so eviction retains the most recently used generations instead of clearing wholesale, and an entry read while it sits in the oldest generation is promoted forward (a second chance). Entries are deterministic, so a race, an eviction or a duplicate yields identical values, and the worst an evicted entry can ever cost is a recompute. Capacities and generation counts all live in `WorldGenerationConfig`, one pair per site: `ColumnCacheCapacity`/`ColumnCacheGenerations`, `CellCacheCapacity`/`CellCacheGenerations`, `TreeBatchCacheCapacity`/`TreeBatchCacheGenerations`, `TreeShapeCacheCapacity`/`TreeShapeCacheGenerations`, `TreeCanopyCacheCapacity`/`TreeCanopyCacheGenerations`, `TreeProxyCanopyCacheCapacity`/`TreeProxyCanopyCacheGenerations`, `MassifSiteCacheCapacity`/`MassifSiteCacheGenerations`, `PeakSiteCacheCapacity`/`PeakSiteCacheGenerations`, `GreatLakeSiteCacheCapacity`/`GreatLakeSiteCacheGenerations`, `PondSiteCacheCapacity`/`PondSiteCacheGenerations` and `HellTreeCacheCapacity`/`HellTreeCacheGenerations` — every site ships at 4 generations, and that count is a re-pricing lever that may only be moved against a real capture, never blind. The factory a site passes to `GetOrAdd` must be a `static`, non-capturing delegate *field* (`ColumnCache.Compute`, `WormCarver.BuildCellFor`, `TreeBatches.BuildShapeFor`, `BuildBatchFor`, `TreeCanopy.BuildCanopyFor`, `BuildProxyCanopyFor`, `TerrainPeaks.BuildMassifSiteFor`, `BuildPeakSiteFor`, `GreatLakes.BuildSiteFor`, `WaterBodies.BuildPondSiteFor`, `HellTrees.BuildFor`), because the factory takes the key alone: a lambda written at the call site that captured locals would allocate a closure on every call, hits included, on the generation workers — recomputing those values from the key on a miss is the cheaper trade. **Standing rule: only values that are a pure function of the key may live in that type.** State carrying *player edits* must never be moved into it, because there eviction is data loss rather than a recompute — `LodColumnCaps` (mined-column LOD caps) and `TreeCanopy.CanopyDisturbed` (felled-canopy markers) are the two current examples, and both are tracked as their own items rather than converted. They are still plain process-global tables; `LodEditState` exports and imports them per world (with the `LodSurface` overrides) and `WorldSeed.Apply` resets them between worlds.
  - 3D cave noise is sampled on a `CaveNoiseStride` lattice (aligned to world coordinates so adjacent chunks share boundary samples) and trilinearly interpolated per block before thresholding. Ore veins (`MalachiteGenerator`, `CoalGenerator`, `HematiteGenerator`) and gravel/sand pockets (`GravelPocketGenerator`) are sampled the same way on the `OreNoiseStride` lattice through `OreNoiseLattice` (PERF_TODO G3): a lattice cell whose 8 corners are all at or below the threshold holds none (a trilinear blend never exceeds its largest corner, so this is part of the field, not an approximation), and `HasOre`/`HasPocket` evaluate the very same field (`OreNoiseLattice.Exceeds`), so a point query equals what the chunk stamp writes, bit for bit and across chunk seams (`OreNoiseLatticeTests`). `HematiteGenerator.HasOre` applies the same world-Y ceiling (`HematiteMaxY`) as its stamp's host rule, which is what keeps that equality for an ore confined to a depth band (`ChunkTerrainFill` also skips the call for a chunk whose floor is above the ceiling, which is bit-identical).
- **Far-terrain LOD**: full-detail chunks load to `ChunkRadius` (16 chunks = 256 blocks); a cascade of LOD rings defined in `LodRegion.Levels` extends the render distance (level 1: 2-block cells to 512, level 2: 4-block cells to 1024, level 3: 8-block cells to 2048 blocks). Regions are columns meshed at reduced resolution straight from the heightmap cache (`LodRegion.BuildMeshData`, built by `LodMeshBuilder` and `LodFaceEmitter`) (no block data; a column a server's world source supplies is read from that source's `ColumnData` instead, see "World sources"). Each level's heights are eroded (min over a widened fine-column window) and biased downward so its mesh sits strictly inside real terrain *and* strictly below the next finer level — finer geometry always buries coarser via the depth buffer, so no masking or camera-driven remeshing exists. Because those shells are stacked, only one level may actually be drawn per area — otherwise they are all visible once the camera is inside the terrain — so `LodCoverPass.UpdateBuriedFlags` marks each region whose footprint is fully covered by *loaded* finer-level regions (region grids nest exactly: one region = ratio×ratio regions of the finer level) and `TerrainPasses.RenderLod` skips those. Testing loaded regions rather than each level's radius means an unbuilt finer region can never open a hole in the horizon. The finest level (0) has no finer level to hide it, so it is additionally marked buried wherever the full-detail chunks its shell could show through are loaded *and* meshed (`LodCoverPass.IsCoveredByChunks`, using the per-column inclusive chunk-Y ranges recorded at region build in `LodRegion.ColumnCyMin/ColumnCyMax` and `Chunk.IsMeshed`, which unlike `HasMesh` is true for all-air and fully enclosed chunks too). This is needed because caves cut into the band the shell occupies and would otherwise show it from inside; presence is tested per chunk rather than by horizontal distance because the load volume is a sphere. LOD side faces use grass at the top cell, dirt above `NominalDirtDepth` and the stone tile below it, so far cliffs match near ones; a desert cell wears sand over sandstone and a sea-floor cell the sand/gravel/stone band its depth picks (`LodRegion.BuildMeshData` reads `ColumnCache.GetBiomes`). The nesting invariants (`SampleMargin(L+1) >= SampleMargin(L) + CellSize(L) - 1`, `HeightBias(L+1) >= HeightBias(L) + CellSize(L)`) must hold when adding levels, and `Camera.NearPlane`/`FarPlane` are tuned to keep those separations from z-fighting at the outermost ring. LOD draws before chunks, coarsest level first (`TerrainPasses.RenderLod`). Destroying a block would expose the buried LOD, so removals cap that column's LOD height below the removed block (`LodColumnCaps.LowerColumnBelow`) and affected regions on every level rebuild (`LodManager.MarkEditedColumn`); placements need nothing. All of this runs where the LOD is built (singleplayer and the server): a multiplayer client's far LOD is the server's, streamed as finished vertices, so its edits return before these hooks (see "Multiple players and chat"). Canopy bumps come from `TreeGenerator.TryGetCanopy(x, z, proxy, out top, out leaf)`, read once per fine column with the level's `LodLevelSpec.ProxyCanopy`: the first ring reads the grown canopy of the same cell batches the chunks stamp, and the farther rings read each species' `lod` crown proxy (`LodCrownProxy.TopOffset` over its `LodCrownProfile`), which never grows a tree. Either way a bump wears the leaf block that owns its top cell (the species' `LeavesId`, resolved generically via `BlockType.ById` in `LodRegion.BuildMeshData`, falling back to `TreeGenerator.LeavesBlock`), so a new tree species only needs its species file — never a new LOD branch. An edited canopy reads as open sky: mining or blasting a wood or leaf cell, every fell, every column `LeafDecayManager` clears or `TreeGrowthWriter` writes call `TreeGenerator.PoisonCanopy`, whose `CanopyDisturbed` markers `TryGetCanopy` and `CanopyLeafBlock` consult before either cache. LOD vertices bake a coarse sky exposure from those same column heights into `Vertex.Sun` (`LodSkyExposure`, tuned by the `LodSky*`/`LodValleyShade`/`LodCanopyShade` constants): a vertex darkens by how far the columns in front of or around it rise above it, and a canopy bump darkens toward its base, while open ground, straight walls under open sky and the water sheet keep full sun so the ring matches the near chunks.

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
  `LodColumnCaps` (MIN semantics, only ever falls, so a dug hole never exposes the buried
  shell) and `TreeCanopy.CanopyDisturbed` (the edited-canopy markers `PoisonCanopy` writes: one bit per
  fine column, kept as four 64-bit words per chunk column and never evicted or cleared; the file still
  lists them column by column, TODO `M-10`) and `TreeCanopy.CanopyAdded` (the grown crowns a
  reactive growth added back over those markers); a fine column's LOD height is
  `min(override ?? generated, cap)`, fed in before the erosion window and bias so the nesting and
  burial invariants hold by construction. `LodEditState` exports all four to `lod-edits.bin`
  (only when their combined version moved) and imports them **before** `LodManager.Start`, so
  the first region build already sees them.
- **`WorldSeed.Apply(seed)`** is how a world gets its seed (`WorldGenerationConfig.Seed` reads
  `WorldSeed.Value`; `WorldSeed.Default` is what tests and scripted runs use). It is legal only
  while no World is live (`WorldSeed.BeginWorld` in the World constructor, `EndWorld` at the very
  end of a clean teardown; `WorldIsLive` reports it), and it rebuilds every worldgen noise, clears
  the eleven `GenerationalCache` sites and resets the process-global LOD edit state — always, even
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
  `WorldPersistence.FlushAll` hands every modified chunk, the LOD edit state, the player and the entities to the
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
- **Autosave** (`WorldPersistence.Autosave`) runs every `AutosaveIntervalSeconds` (at most `MaxAutosaveSnapshotsPerCall`
  snapshots a call, the rest on following frames) after the fluid tick, under the one `save`
  profiler row, which is entered nowhere else.
- **Player and entities.** `player.json` never stores a position before the spawn settled, so a
  world closed mid-load searches the surface again next time. Saved mobs and drops are spawned
  once loading finishes, each only when its chunk is loaded; a restore is a forced spawn, so it
  may pass the natural mob cap but a mob waits while the hard cap (`MobHardCap`, which sizes the
  healthbar buffers) is full; an entry that waits stays pending and is saved again, and is
  discarded only after `EntityRestoreTimeoutSeconds` of play. A saved iceberg (`Icebergs` in
  `entities.json`) waits until every chunk under its placed bounds is loaded, and a sinking berg
  is not saved.
- **Stations** (`stations.json`, `StationSaveCodec`/`StationSaveData`): restored into the empty
  managers when the world is built, with no wait for chunks, because every manager already freezes a
  cell in an unloaded chunk and sweeps an entry whose block is gone (dropping what it held) once the
  chunk loads. An entry whose block, item or molten material name no longer resolves is dropped with
  one aggregated warning, and a cast pour is restored paused (its crucible binding is transient). A
  corrupt file parses to null and is replaced at the next autosave, like `entities.json`.
- **Not persisted** (tracked as `M-12` in `plans/TODO.md`): primed TNT, falling blocks, projectiles
  in flight, spell bolts in flight, toppling trees and the sapling clock (a half-grown sapling stands where it is until it
  is grown again).

## Multiple players and chat

A dedicated **server** is a fourth project (`OpenGLTest.Server/`, `dotnet run --project
OpenGLTest.Server/VoxelEngine.Server.csproj -c Release`, working directory with `assets/` up the tree — the
program sets its own current directory to the assets parent so `FileUtils`' relative `assets/...`
paths resolve). It is a **CLI**: it loads content headlessly (`ContentRegistry.Load` +
`TextureAtlas.RegisterTilesWithoutUpload` + `ContentRegistry.BakeModels`, the `TestContent`
recipe), lists `WorldStore.List()` and asks for a world number or `N` for a new one (name and
optional seed), asks for a port (default `ServerAddress.DefaultPort`, 25566; `--allow-creative` on
the command line sets `GameServer.AllowCreative`, default `NetworkConfig.ServerAllowCreative`,
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
as the player: `WorldPersistence.CapturePlayer` writes back the `player.json` the world was opened with, kept as
`_hostedPlayer`, with only the lighting clock refreshed — its inventory too is written back as it
was opened, since every connected player carries their own — or for a world that had none no pose,
no creative or flight, no respawn eye and no inventory, so a survival world opened after hosting is
not in creative flight at the server camera's hover), and skips the LodGen threads; the
global `WorldSeed` still applies, and the eleven `GenerationalCache` sites behave exactly as in the
game, which is what lets a headless World run `Update` over real terrain (the LOD builder already
drops real builds when its pool is null). `ServerWorldTests` locks this down.

**World sources.** Where a world's terrain comes from is one seam, `IWorldSource`
(`Chunks/Sources/IWorldSource.cs`). It extends `IColumnSource`, so the mesher's column reads and
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
- `FluidWakeScanner`'s wake scans (`WakeLoadedChunks`, `WakeBorders`) and its generated-water test;
- the LOD edit hooks, `LodSurface.TopOrHeight(source, x, z)` and
  `LodColumnCaps.LowerColumnBelow(source, x, y, z)` (the source-less overload is worldgen's);
- `LodRegion.BuildMeshData(..., source:)` (its body is `LodMeshBuilder`), from `LodRegionClaims.TryBuildOneRegion` and the server's
  `LodRegionRequest` (`ChunkManager.WorldSource`). A supplied column's far LOD takes the biome and
  water level at the cell center from its `ColumnData`, snow from a Taiga or Frozen biome, sea ice
  from Frozen (it carries no cold weight, so this approximates the generated pack-ice band of
  `SnowGenerator.IsFrozenWater`), beaches from `TerrainGenerator.IsBeachHeight` in the Ocean biome, bare rock from a
  zero `DirtDepth` in a snowless grass biome (`BiomeTraits.ShowsBareRock`), a permafrost soil band
  and lake bed in the Frozen biome (stone at a zero `DirtDepth`) and the tint lattice from the
  stored corner climate, and it
  draws no canopy bumps (its trees are in its blocks) and no gravel caps; an unsupplied column keeps
  every worldgen read verbatim, so a generated region is byte-identical to the build without a
  source;
- `World.TryGroundHeight` and the spawn settle (`LocalPlayer.EnsureSpawnSettled`, whose water test is now the
  column's stored `WaterLevel`, the same value `TerrainGenerator.IsWaterColumn` evaluates for a
  generated column);
- `MobSpawner.TryFindSurfaceSpawn` (heights and water from the column; a supplied column's
  mob-biome test, `MobSpawner.SpawnsMobs`, reads its own `Biomes`, a generated one keeps the
  undithered `TerrainGenerator.BiomeAt`);
- the server's `ColumnRequest`, answered from `ChunkManager.ColumnSource`, exactly what its own
  mesher reads.

Worldgen's own pure functions deliberately stay static and never see the source:
`ChunkTerrainFill.GenerateBlockData`, the carvers (`NoiseCaves`, `WormCarver`), `TreeGenerator` (placement,
batches, the far-LOD canopy), `VirtualTreeSpace` and `SpawnOverrides.SpawnPosition` (a server's camera
starts at the generated origin surface, harmless since it is creative and flying, and the spawn
settle then re-settles it on the source's terrain). A generated chunk beside a supplied column
therefore generates as if the source were not there (see the known gaps).

**`ImportedMapSource`** (`Chunks/Sources/ImportedMapSource.cs`) is the non-generating source: a map
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
`PauseMenuLayout`'s three-button stack; `MainMenuLayout.MainButtonCenter`/`MainTitleTop` own the
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
granted, health, a dead flag, the knockback impulse and lift of the mob hits since the last
one, the air and the mana; sent at join, whenever it changed, and once when a mode request was refused),
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
`ProtocolVersion` 6: `EntityList` also carries `NetEntityKind.PrimedTnt` (4;
`ItemId` the block that primed, `Health` the fuse remaining and `Ammo` the whole fuse, both in
milliseconds) and `NetEntityKind.FallingBlock` (5; `ItemId` the block), and an older client is
refused at hello. A slime entry's `Ammo` (a goblin's flint) carries the slime's size; the layout
did not change, so no version bump: an older server's 0 reads as the smallest size, and a mirror
whose reported size differs is replaced (`ClientMirror.KindMatches`). Version 9 ends `PlayerStatus` with the player's air byte. Version 10 lets an `Attack` intent carry the sword's swings (`NetAttackKind.SwordCut`/`SwordBackCut`/`SwordChop`, mapped by `NetAttacks`) and the player list their `NetPlayerAction`s; an older peer would refuse the intent as malformed and drop the connection. Version 11 ends `PlayerStatus` with the player's mana and its max (two ints, clamped on read). Version 12 adds the `Cast` world intent (its slot byte) and the `SelectStaffSlot` inventory action; an older peer would refuse either as malformed. Version 14 adds the `BindStaffSlot` and `ClearStaffSlot` inventory actions (the spell table); an older peer would refuse either as malformed. Version 15 adds the known spell parts (`KnownParts`, by key) to the `InventoryState` snapshot. Version 17 (current) lets `EntityList` carry the hell mobs (`NetEntityKind.Hellbrute` 7 and `Cinderwitch` 8, the
mob fields only: a mirror's layer, and so its pool, follows from where it is first seen); an older client would not
draw them. Version 16 makes a crucible carry one entry per metal (the alloy kiln): a `StationState` kiln entry writes a metal count (at most `CrucibleComponent.MaxMetals`; `StationNetCodec.ReadCrucible` refuses more, a material 0, a repeat or a negative amount) and each metal's id, molten and pending ml, and the `InventoryState` crucible a `Metals` list; an older peer would misread the station frames. Version 13 ends every `EntityList` entry, of every kind, with a status-effect byte mask (`NetEntity.Effects`: a mob's `StatusEffect.Bit`s, 0 for a drop, primed TNT, falling block or iceberg) and ends `PlayerStatus` with the player's own effect mask (`PlayerStatusData.EffectMask`); both codecs carry the mask raw and the client drops unknown bits when it mirrors them, and an older peer would misread every frame after the first entry. Version 8 added
`NetEntityKind.Iceberg` (6): `Position` the waterline center, `Yaw` the quarter turns times pi/2
(`NetEntity.YawFromTurns` / `TurnsFromYaw`), `Health` the seed, `Ammo` the scale in thousandths and
the sinking in its next byte (`NetEntity.PackIcebergAmmo` / `UnpackIcebergAmmo`), `ItemId` the
variant and `Velocity` the drift the client dead-reckons along; an older client is refused, since
it would neither draw nor collide with bergs while the server judges a rider's hover against
them. A
`WorldIntent` is the sequence id, a `NetIntentKind` byte, the eye and the unit aim the action was
made from, then that kind's fields (`WorldIntent`): `BreakStart`/`BreakComplete(cell)`,
`Place(hit cell, face normal)`, `Use(cell under the crosshair, optional)`, `Throw(charge 0..1)`,
`Attack(punch | slice | slice return)`, `Respawn` (nothing more; never rate-limited, edits no
cell) and `Cast(slot)` (a held staff's spell slot as one byte; never edits a cell, rate-limited). Its
reader refuses an unknown kind or attack, a cast slot at or past `SpellConfig.StaffMaxSlots`, a
non-finite or far-out eye, an aim that is not close to a unit vector and a normal component
outside -1..1 as an `InvalidDataException`, renormalizes the aim and clamps the charge. An
`InventoryRequest` is the sequence id, then a `NetInventoryAction` byte and only that action's fields
(`InventoryIntent`): `SelectSlot(slot)`, `DropSelected`/`DropAllSelected`, `Equip(slot, isTool,
id)` (a stackable's id or a tool's instance id), `Craft(recipe file name, batch, fed member per
ingredient)`, `CreativeGrant(id, count)`, `CreativeClear(id)`, `PickBlock(id)`, `SelectStaffSlot(slot)` (the held staff's selected spell
slot, refused without a staff or for a slot it lacks), `BindStaffSlot(slot, part keys)` (binds a
recipe to the held staff's slot through `HeldStaff.TryBind`: refused without a staff, for keys that do
not resolve, for a part the player does not know (`KnownSpellParts`; creative knows all) or for a
recipe `StaffBinding.TryBind` refuses against the staff's tags) and `ClearStaffSlot(slot)` (refused
without a staff or for an unbound slot). The reader is
bounds-checked: an unknown action, an overlong recipe name, more than
`InventoryIntentCodec.MaxCraftMembers` members, more than `MaxSpellParts` part keys or a key
longer than `MaxPartKeyLength` is an `InvalidDataException` (the sender is
dropped like any malformed frame), while merely out-of-range values parse and are refused.

**The server** (`OpenGLTest.Server/GameServer.cs`, the composition root over `ServerTransport`, `PlayerSessions`, `IntentReplay`, `StreamRequests`, `ServerBroadcasts` and `ServerChat`, which share a `ServerState`) is a dual-mode `TcpListener.Create(port)`
(IPv6 any with IPv4 mapped, so `[::1]:port` works like `127.0.0.1:port`) with one reader thread per
connection that only frames inbound bytes onto a `ConcurrentQueue`; `Pump` (main thread) drains it
and owns every piece of `World` state, so the simulation stays single-threaded exactly like the
client. A transient `SocketException` from the accept is logged and retried rather than ending the
accept thread. Each `Handle` call in `Pump` runs inside a try/catch: a short or malformed frame
(`EndOfStreamException`, `IOException`, `InvalidDataException`, `FormatException`,
`ArgumentException`, `ObjectDisposedException`) logs, closes and removes only that client. Sends
are blocking writes on the main thread, so every socket gets a `NetSendTimeoutMs` write timeout,
and a failed or timed-out send faults the connection (no further sends) and queues its removal
once. The accept thread queues each new connection on `ServerTransport.Accepted` before its reader starts; `Pump`
moves the ones that have not said hello into `ServerState.Pending`, and once a second sweeps (`PlayerSessions.SweepIdleConnections`) every
connection, hello'd or pending, whose reader saw no frame for `NetIdleTimeoutSeconds`
(`ClientConnection.LastInboundAt`, `Volatile` ticks; clients send `KeepAlive` every
`NetKeepAliveSeconds` and their state at 20 Hz). `Stop` closes the pending ones too. Every
connected player is a **`ServerPlayer`** (`OpenGLTest/src/Networking/ServerSide/ServerPlayer.cs`): id, a name
unique among the players online by its save slug (a second "Player" joins as "Player 2"), its
**own** `Inventory` (`Inventory.Empty()` for a newcomer), server-owned `Creative`/`Flying` flags
(survival by default), its own health and respawn eye (see "Player status" below), the accepted
pose and `LastStateAt`. On `ClientHello` (`PlayerSessions.Join`) it assigns an
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
mesher reads, encoded by `ChunkNetCodec.EncodeColumn`), queues `LodRegionRequest`s (`StreamRequests`, deduped
per connection, `ClientConnection.LodQueued`) and builds at most `NetLodBuildsPerPump` of them a
`Pump` (`LodRegion.BuildMeshData`, skipping any whose client has gone), replays each
`InventoryRequest` intent (`IntentReplay.OnInventoryRequest`) against **the sender's** inventory only — `InventoryIntentRules.Apply`
(a slot must be on the hotbar; equip only what the player owns, or toggle off what the slot
already shows; craft through `Inventory.TryCraft` with exactly the fed members, so a refused craft
spends nothing; creative grants and clears only while `ServerPlayer.Creative`; pick-block through
`Inventory.PickBlock`, which never grants; a staff bind only with a staff in hand, keys that resolve,
parts the player knows and a recipe valid against the staff's tags, and a clear only on a bound slot,
both through `HeldStaff`) and a Q-drop through
`World.ThrowSelected(wholeStack, eye, forward, inventory)` from that player's eye and look and out
of that player's inventory, never the server camera — and **always** answers with a full
`InventoryState` snapshot to that player alone, accepted or not, so a refused intent rubberbands
the client's optimistic copy. Drops are picked up per player: a server-mode world's `DropPickup.Update` reads the
connected players from `World.ServerPlayers` (the server's list) and `DropManager.Update`'s
multi-collector overload (`DropCollector`) flies each drop in a loaded chunk to the nearest player
whose inventory has room (a drop in an unloaded chunk is neither collected nor simulated; one
nobody can take lets go and falls), crediting that player's inventory and flagging
`ServerPlayer.InventoryChanged`, which the next `Pump` answers with that player's snapshot; the
server camera and `World.Inventory` never collect.

**World intents.** A client never writes a cell: every world action is a `WorldIntent` the server
replays for the sending player through the singleplayer rules. Each `ServerPlayer` owns a
`PlayerContext` (`OpenGLTest/src/Player/PlayerContext.cs`: eye, aim, view direction, its inventory, its
server creative flag) that every interaction path (`BlockEditor`, `BlockPlacer`, `LeftClickAction`,
`RightClickAction`, `ThrowAction`, `WorldItemUse`) runs through instead of the camera
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
  `World.BreakBlockFor` (forwarding to `BlockEditor.BreakBlockFor`), i.e. the full
  `BlockEditor.RemoveBlock`: felling (`FellWood`, which now runs on the
  server), multiblock revert, loot through `DropManager.SpawnLoot` (picked up by the nearest player
  with room), a station's hand-back, needs-support pops, toppling and the fluid, leaf-decay, LOD
  and canopy hooks.
- **Place** — the ray must hit the named cell, and the named face must be the ray's own or another
  face of that cell turned toward the eye (`NetValidation.FaceFacesEye`: a third-person aim reports
  the camera ray's face; the far side of a wall never qualifies). `World.TryPlaceSelectedBlockAt`
  then places the player's **selected** block (owned, placeable, one spent unless creative) under
  the placement rules (`Placement`, no live falling cube, fragile blocks on solid ground, and no
  connected player's body in the cell — `BlockPlacer.BodyBlocksPlacement` checks every `ServerPlayer`, the
  server camera is nobody), with the world's own state byte (a placed log `WoodState.Detached`, a
  placed leaf persistent).
- **Use** — `World.TryUseSelected(InteractionReach, context, out outcome, allowPlace: false)`: the
  right-click chain (campfire, quern, kiln, cast mold, then the held item's `OnUse` — bucket, pile,
  firestarter on TNT or a kiln, converter) aimed by the player's ray and spending from its
  inventory, short of the placement fallback. The item-use tail reads the dispatching player
  (`WorldItemUse._activeContext`), and a chance roll goes through `IItemUseWorld.NextUseRoll`
  (`World.UseRoll`, a test seam).
- **Throw** — `World.ThrowFor` launches the selected throwable at the intent's charge from the eye.
- **Attack** — a swing needs the weapon that makes it in hand (`NetAttacks.PatternOf`: a slice a
  slicing blade, a sword swing a sword, a punch anything else), each at most once per its pace
  (`ServerPlayer.TryAttack`, `AttackPacing`: the held item's hit gap, `PlayerModel.SliceDuration`,
  or for a sword swing the length of the swing before it, `PlayerModel.SwordDuration` of the
  player's last attack, less `NetAttackSlackSeconds`); `World.PunchFor`/`SliceFor`/`SwordFor` find
  the targets among the server's mobs from the intent's eye and aim with the `Combat` rules.
- **Cast** — `World.CastFor` (the `Magic/SpellCaster` seam, rules in `CastRules`) with the player's
  own `ServerPlayer.Casting` pacing: a spell staff in hand (`HeldStaff`), the intent's slot equal to
  the staff's selected one and bound, its parts resolving to a valid recipe with a finite cost, the
  cooldown (`SpellConfig.CastCooldownSeconds` less `NetCastSlackSeconds`) run out, and the mana billed
  last (`PlayerContext.Health.Mana.TrySpend`, free in creative), so every refusal spends nothing. A
  spend or a refusal for mana sets `StatusDirty`, so the pool reaches the client in the same `Pump`.
  An accepted cast fires `World.SpellEffect`, by default `SpellEffects.Cast`, which launches the bolt
  or applies the self cast on the server (bolts are not in `EntityList`, so other clients see only their
  consequences, while the caster's own client flies a cosmetic preview) and is rebroadcast as
  `NetPlayerAction.Cast` (8, no protocol bump: the player-list action byte is read unchecked, so an older
  client just shows no gesture). A client runs the same seam to predict only: it
  checks and paces, never spends and never fires the effect, then raises the intent.

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
write** (`Chunk.CellWritten` -> `ServerBroadcasts.OnCellWritten`, set in `Start`, cleared in `Stop`) and broadcasts the coalesced
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
server camera: `World.Focus` (`Chunks/Streaming/StreamingFocus.cs`) publishes an immutable array of focus
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
of singleplayer's `LocalPlayer.UpdateHealth`, which still uses it with `DefaultMaxStepBlocks`), a
`RespawnEye` and a mob-hit sink. The server never simulates a player's body; it probes its own
loaded chunks around the reported position (`Player/PlayerBodyProbe.cs`: `IsKnown` — the feet
cell's chunk and the one `NetGroundProbeBlocks` below are loaded —, `Grounded` — the body lowered
by `NetGroundProbeBlocks` collides, through the static `BodyCollision.BodyCollides` the client's
own collision now delegates to —, `InWater`, mirroring `UpdateWaterState`'s swim test, and
`InLava`, the one lava rule `UpdateWaterState` also calls; its cell test, `BurningFluid.AnyInBox`, is the mobs' too). The fall tracker is fed once per
**accepted** `PlayerState` (`ServerPlayer.ObserveAcceptedState`), with the state gap's allowed
distance (`NetValidation.AllowedDistance(gap, NetMaxMoveBlocksPerSecond)`) as its teleport bound —
exactly what the accepted position already obeys, so only the server's own teleports (join,
respawn, a correction: `Teleport`/`ResetFall`) reset it and lag errs toward no damage, never extra
damage; a body over unloaded chunks is not judged. Lava and the damage flash tick once per `Pump`
(`ServerPlayer.TickStatus` from `PlayerSessions.TickPlayers`), creative or flying being immune as in
singleplayer. Mob hits land through `IMobTarget.TakeHit` (below), which bills the player's own
pool and queues the knockback. Whatever changed the status marks `StatusDirty`, and `TickPlayers`
then sends that player a `PlayerStatus` (modes, health, dead, the queued knockback, handed
over and cleared by `TakePendingKnockback`, the air and the mana); one more goes out at join, so a returning player's
health (or death) is mirrored at once. The status is saved with the player (health and respawn
eye in `players/<slug>.json`). A **dead** player stays where it died: its states still record
look, flags and modes but never move it, and every intent but `Respawn` is refused. The client
mirrors all of it: `ClientMirror.ApplyNetworkHealth` (client mode only; lower health is taken as damage
with the flash and a singleplayer-sized shake, higher is restored outright), `UpdateClient`
fading the flash, `Camera.AddKnockback` for the shove (the client's own physics applies it) and
`SetModes`; a client never bills itself damage. Once the status has read dead for
`RespawnPrompt.DelaySeconds` the client's **death screen** comes up (`ClientSession`'s
`RespawnPrompt`, the GL-free rule behind the screen: YOU DIED and a RESPAWN button, Enter works
too), and only when the player chooses RESPAWN does the client send a `Respawn` intent
(`ClientSession.RequestRespawn` -> `SendRespawn`, from its eye and aim), re-sent every
`NetRespawnRetrySeconds` while the status still reads dead; the server judges it before the
eye-drift and rate rules, so a corpse that kept falling on the client can always come back, brings
the player back at full health at its `RespawnEye` (else the join spawn, `ServerPlayer.Respawn`),
snaps the client there with a `PlayerCorrection` and sends the fresh status. Nothing server-side
changed: a dead player simply waits for the intent. Singleplayer works the same way
(`LocalPlayer.RespawnPrompt`: the death plays, then the player stays down until they respawn).

**The movement rubberband.** Every accepted-for-position `PlayerState` goes through
`MovementRules.Judge` (`Networking/ServerSide/MovementRules.cs`, GL-free, verdict `MoveVerdict` Accepted /
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
snapped can never trip a second correction. A respawn uses the same correction. The hover rule
(and the fall tracker's grounded probe) also counts the server's iceberg boxes as ground
(`World.BodySolids`, an `IBodySolids` passed to `MovementRules.Judge` and
`ServerPlayer.ObserveAcceptedState`), so a rider is never a hoverer; the endpoint-collision rule
stays block-only, since a client stands on a mirror up to a snapshot behind the server's berg.

**Creative permission** is per player: a client's creative bit is only a request, granted (with
flight only alongside it, and flight is noclip to the movement rules) while
`GameServer.MayBeCreative(name)` holds — everyone when `AllowCreative` (`--allow-creative`) is on,
otherwise only an operator. `OperatorList` (`Networking/ServerSide/OperatorList.cs`, GL-free) holds the
operators by save slug (`WorldNames.Slug`, the same normalization that names a player's file, so
a name matches exactly when it would load the same save file — case and punctuation fold away —
and a duplicate joiner's "Name 2" is not an operator); `ServerProgram` builds it from every repeatable `--op <name>` plus
`ops.json` in the hosted world's folder (a JSON array of names, read once at start through
`ChunkSaveStore.ReadFile`; no file means nobody, a broken one warns and means nobody), and nobody
is an operator by default. Both enforcement points use it — the restored modes at hello and every
`PlayerState` — creative grants and clears follow `ServerPlayer.Creative`, and a refused request
is answered once with a `PlayerStatus` carrying the modes the player really has.

**Mobs follow real players.** A mob chases and hurts an `IMobTarget` (`IMobTarget.cs`: `Eye`,
`IsImmune`, `TakeHit(damage, away, speed, lift)`), never the camera directly. `MobDirector.Update`
(World's mob component) rebuilds the target list each frame right before it runs the slime and goblin managers: singleplayer's is the
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

**The client** (`OpenGLTest/src/Networking/Client/NetworkClient.cs`) connects on a background thread from
`ClientSession.BeginConnect` (the CONNECT click records `PendingWork.Connect` and presents the
"Connecting..." loading screen, like PLAY), handshakes, then a reader thread queues frames. The
handshake read is bounded by `NetConnectTimeoutSeconds` (cleared before play), the socket is
disposed on every failure path, the connect timeout included, and `Status`/`Server`/`Error` are
published through volatile fields; `Handshaken` is set before the reader starts and the reader's
final `Disconnected` is unconditional, so a server that closes at once can never leave
`Handshaken` standing. `ClientSession.UpdateConnecting` aborts to the server list on `Failed` **or**
`Disconnected`, waits for `NetworkStatus.Handshaken`, then builds a **client-mode** `World`
(`clientMode: true`, `spawn: server.Spawn`, so the camera starts at the server's spawn — a client
camera never runs `SpawnOverrides.SpawnPosition`'s surface search, and stands at
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
or `TreeGenerator`; for the same reason `BlockEditor.NotifyLodEdit` and the inline
`TreeGenerator.PoisonCanopy`/`LodManager.MarkEditedColumn` sites in `RemoveBlock` and `FellWood`
return early in client mode, so a client edit never touches `LodSurface`, `LodColumnCaps`
or `TreeCanopy.CanopyDisturbed`. `WorldgenReads` (off in production) is the counter
`ClientWorldgenIsolationTests` turns on to prove a client world under a poisoned seed streams and
meshes with no worldgen call and edits without moving the far-LOD tables, and
`StreamedTerrainMeshTests` that a mesh built from streamed columns is byte-identical to the
worldgen one (a tinted surface and a submerged floor).
`ClientMirror.ApplyNetworkEntities`/`ApplyNetworkBlockChanges`/`Inventory.Import` apply the server's
entities, cell changes and inventory mirror. Only the mob kinds (`NetEntity.IsMob`) enter the mob
reconcile; primed TNT and falling blocks become **display-only mirrors**, and only in client mode
(`ApplyNetworkMirrors`, so a singleplayer world can never acquire a cube its `Update` would tick
and detonate): a primed TNT is reconciled by id (`PrimedTnt.Mirror` / `MirrorSetState`, so its
strobe clock survives snapshots, the fuse clamped so a bad pair cannot divide the flash by zero),
falling cubes are rebuilt wholesale (`FallingBlockManager.ReplaceMirrors`), and `WorldRenderer.Render`
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
`World.IntentRaised` (`ClientSession.SendWorldIntent`): mining raises `BreakStart` when it starts a
target (or the held item changes) and `BreakComplete` when its own hits reach the hardness (a
creative client breaks once per `ClientCreativeBreakSeconds` rather than per frame, to stay
inside the edit rate); a claimed right-click raises `Place` (the hit cell and face its placement
used, `BlockPlacer.LastPlaceHit`) or `Use`; a released throw raises `Throw`; a landed punch or slice
raises `Attack`. Intents carry `Camera.Position` and the head ray the camera aimed along
(`Camera.LastAimDirection`), so the server's first-person ray lands on the same cell. Every cell
the prediction changed is reported first through `World.BlockEdited` — mining (`BlockEditor.RemoveBlock`, plus
the cells a multiblock revert gives back), placement, the `WorldItemUse` bucket wrappers
`ScoopFluidCell`/`PourFluidCell`, the stack writers `SetPileCount`/`SetPileComposition`, every
cell of a `BlockEditor.TryConvertMultiblock` and `RemoveCellForPrime` — and staged in `ClientPrediction`,
which tags them (and, for place/use/throw and every inventory intent, the inventory) with the
intent's sequence id. A server cell value for a predicted cell — a `BlockChanges` broadcast, or a
`BlockCorrection` answering an older intent — is held back until that prediction's
`IntentResult`, then applied (the newest one per cell); a correction answering the newest
prediction applies at once; and an `InventoryState` older than the newest inventory-predicting
intent is dropped, since the answer to that intent follows and includes it. A client fells
nothing, topples nothing, primes no TNT, rolls no loot, lets no station hand anything back and
damages no mob itself (`BlockEditor.RemoveBlock`, `BlockPlacer.PlaceAgainstFace`, `WorldItemUse`'s
`AfterPlaced` and `SpawnPrimedTnt`, `LeftClickAction.UpdatePunch`/`UpdateSlice` and
`ThrowAction.UpdateThrown` are guarded by `_clientMode`):
the server's broadcasts carry every consequence, its mobs' health arrives in `EntityList`, mined
loot arrives as drops it picks up into the player's server inventory, and a throw flies locally
only as a cosmetic copy. The purely cosmetic leaf particles and mining debris, the player model,
the station mirrors' quern animation and fire particles, the mirrored TNT fuses and the cosmetic throws tick in
`World.UpdateClient` so nothing hangs in mid-air. `UpdateNetwork` sends `PlayerState` every
`NetPlayerStateSeconds`, carrying the newest correction it applied (`netCorrectionAck`); chat
feeds `ChatView`, `TimeSync` calls `World.SetLightingTime`,
`PlayerList` reconciles `ClientMirror.SyncRemotePlayers`, `StationState` replaces the station mirrors
(`ClientMirror.ApplyNetworkStations`), `PlayerStatus` sets the camera's modes and mirrors health, death
and knockback (and starts the death screen's clock, see "Player status"), `PlayerCorrection` snaps
the camera and makes the next state go out at once with its ack, and `InventoryState` is
imported as the truth when it is current.
Inventory edits are predicted then sent (`ClientSession.SendInventoryIntent`): the hotbar keys and `Q`
are intents only, while the wheel (`GameplayInput.MirrorSelection`), a middle-click pick
(`BlockPlacer.TryPickTargetedBlock(inventory, out id)` -> `PickBlock`), an [E]-panel hotbar equip, a
craft (the fed members read before `CraftingSession.Craft` spends the reservation) and a creative
catalog grant or clear apply locally at once and send their intent, so the server's answer keeps
them or snaps them back. Each server message is handled inside a
try/catch, and a malformed one (`EndOfStreamException`, `IOException`, `InvalidDataException`,
`FormatException`, `ArgumentException`) leaves the server with "The server sent bad data" instead
of crashing the game. Remote players are `RemotePlayer`s whose
`PlayerModel` walk cycle runs off the distance they moved; `World` updates them each frame and
`PlayerPasses.RenderRemotePlayers` draws each as the player body at its broadcast eye. A lost
connection, or SAVE & QUIT while connected, disposes the client world and returns to the
multiplayer page.

**Chat** is universal: `ChatView`/`ChatLayout` (`OpenGLTest/src/UI/Hud/Chat.cs`, GL-free, tested) hold
a 100-line log, a 200-char input, arrow-key history and the open flag. `T` opens it from a clean
play state, `Enter` sends, `Escape` closes; while it is open the keyboard is withheld from the
camera exactly like a focused inventory field, and the log sits bottom-left above the hotbar
(`ChatRenderer.DrawChat`, its own `UiPasses.RenderChat` UI pass). In singleplayer `ClientSession.
SendChat` echoes locally; in multiplayer it sends `ChatSend` and the server's broadcast is the
only copy, so a line never appears twice.

**Known gaps** (open): players are keyed by name with no authentication, so whoever joins first
under a name gets that name's inventory, status and operator rights (`ops.json` is read once at
start, and there is no in-game op command); a slice's return-swing flag and a throw's charge are
taken from the client (both are paced, neither verified); a timed use (`use.seconds`) is held
locally and the completed `Use` intent is taken on trust, so a modified client can fire one
instantly; a third-person placement may land on a
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
(the server only queues it in the status), so a modified client can ignore it; a dead player
waits on the death screen and its model stays where it died for others until the respawn; a player's lava and fall state is judged from its 20 Hz positions, so a long fall
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
from its biome and dirt depth (its sea ice follows the stored Frozen biome, so it shows no ragged
pack-ice band) and draws no canopy bumps or gravel caps; the fluid wake treats
every imported chunk as restored (its border faces are always scanned at load), so a map whose
water disagrees with its columns costs wake work, never a crash; `ImportedMapSource` loads every
column file at construction, so a map of hundreds of thousands of columns costs hundreds of MB
and a long synchronous start (a lazy per-column load is the follow-up); and the server camera,
which `SpawnOverrides.SpawnPosition` still places on the generated origin surface, can start inside or
under the map's terrain until the spawn settle lifts it. From the status effects (MG-4b): other
players' effects are not shown (the `PlayerList` carries no mask), a client's slowdown is applied by
its own physics and is not judged (the rubberband only caps speed from above), and the server's attack
pacing checks a minimum gap only, so a slowed client stays legal but an unslowed one is not caught.

## Biomes

Seven biomes (`WorldGen/Biome.cs`; new values are appended, never inserted, since the ordinals ride
`ColumnFileCodec` and the network column and index `TreeGenerator.SpeciesByBiome`). **Ocean** is a
flooded basin ramping from a sandy shore down to bare stone. The six land biomes sit on a **3x3
climate grid** of temperature (cold / temperate / warm) and humidity (dry / semi-dry / wet),
`BiomeClassifier.Classify`:

|               | wet    | semi-dry | dry    |
|---------------|--------|----------|--------|
| **cold**      | taiga  | taiga    | frozen |
| **temperate** | forest | steppe   | steppe |
| **warm**      | forest | savanna  | desert |

**forest** (smooth rolling grass hills with flat valleys), **desert** (dunes of sand over
sandstone), **taiga** (a snowy spruce wood), **frozen** (a tundra: permafrost under the snow, over
stone, with sparse dead shrubs, leafless snags and occasional ice), **steppe** (an open, dry-tinted grass plain with scattered dead trees) and
**savanna** (a dry grassland of umbrella-crowned stone pines and cotton). Forest needs wet air, so
the dry axis reads desert -> steppe -> tundra and the warm row forest -> savanna -> desert: no strip
of forest ever stands between sand and snow. Selection is a pure function of (x, z) from
low-frequency noise — continentalness (low = ocean) plus temperature and humidity, which are read
**only** through `BiomeClassifier.SampleClimate` (the one reader of the two noises) while choosing a
biome, the worldgen features that follow from it and the grass/foliage tint
(`TerrainGenerator.Climate`, see "Biome-tinted grass and leaves") — in `BiomeClassifier.BiomeWeights`
(forwarded by `TerrainGenerator.BiomeWeights`); `TerrainGenerator.BiomeAt` classifies the result.
`SampleClimate` **domain-warps** the sample position before reading either noise, with one shared
OpenSimplex2 warp (`TerrainNoise.ClimateWarp`, seed offset 151, `WorldGenerationConfig.ClimateWarp*`;
the amplitude is a FastNoiseLite `SetDomainWarpAmp` value, not blocks), so every climate border bends
and wanders instead of tracing the raw noises' smooth blobs, and the biome, the cold and warm weights,
snow and ice, the tint, the grade and the aurora all read the same warped sample
(`BiomeClimateTests.EveryClimateReaderSeesTheOneSample`); never warp anywhere else. The warp is exactly
zero at the origin, and its stretch budget is a Lipschitz bound of 2: a unit step in the world moves
the warped sample by at most 2 (`BiomeClimateTests.TheWarpStretchesTheClimateBoundedly`), while the
desert/snow separation below is the tighter limit on how strong it may be. The warp (with
`TemperatureFrequency` lowered to make room for it) re-laid every seed except the origin: existing
saves regenerate their unmodified chunks under the new climate, so old worlds show seams at the edge
of their edited chunks.
Temperature is a **three-band axis**: the cold ramp (`TemperatureColdStart/End`, read by
`ColdWeight`: snow, ice, and taiga/frozen at `ColdBiomeMinCold`), a temperate band, and the warm
ramp (`TemperatureWarmStart/End`, read by `WarmWeight`; desert and savanna begin at its midpoint).
Humidity is a three-band axis too: the dry ramp (`HumidityDryStart/End`, `BiomeClassifier.DryOf`)
and the **semi-dry ramp** (`HumiditySemiDryStart/End`, `SemiDryOf`), which starts at or after the
dry ramp ends, so the two are exclusive and `semiDry` is 1 wherever any dryness is (a test rule,
`HumidityRampsAreExclusive`). `desertWeight = min(warm, dry) * (1 - ocean)` is exactly 0.5 on the
desert line on both borders, the humid one and the temperate one, which is the one line the dune
height blend, the surface-water fade (`WaterDesertFade*`) and the border dither are keyed to. A
third smooth weight, `aridWeight = semiDry * (1 - ocean)`, flattens `DryHillReduction` of the
forest-profile hills on dry and semi-dry land, so the steppe, the savanna and the tundra are plains
while every border stays a slope. The raw-noise gap between `ColdBiomeMinCold` on the cold ramp
and the warm midpoint classifies forest (wet) or steppe (semi-dry, dry) and keeps hundreds of
blocks of it between sand and snow: widen it by raising the warm ramp, never by moving the cold
one, and the warm ramp must start at or after the cold ramp ends (a test rule,
`TemperatureRampsAreExclusiveWithATemperateBandBetween`). The rules sit in the pure seams
`BiomeClassifier.Classify` / `ColdOf` / `WarmOf` / `DryOf` / `SemiDryOf`: `TheDryAxisNeverClassifiesForest`
locks the grid, and `DesertAndSnowAreNeverNearAndTheDryCorridorIsSteppeOrTundra` pins the band to
at least the full-detail load radius (`ChunkRadius * Chunk.Size`, Chebyshev distance) from any
column that would be desert if fully dry to any snow, and every dry land column to desert, steppe
or tundra. The origin's raw humidity is exactly 0 for every seed, so the semi-dry ramp's midpoint
sits just below 0 to keep the spawn a forest (`BiomeTests.OriginColumnIsForest`). Reaching that bar took a lower
`TemperatureFrequency` (1/5500, was 1/2300, then 1/4500 before the climate warp) and a
**single-octave** temperature noise (was two octaves), both of which re-laid every temperature value; single-octave output spans the raw
[-1, 1] where two-octave FBm was scaled by 1/1.5, so it also rescaled the cold ramp's share
(taiga, frozen, snow). The weights are smooth 0..1 and `TerrainGenerator.Compute` lerps the height
between the forest, desert-dune and ocean-ramp profiles by them, so a border is a slope, never a
cliff; the cold biomes ride the forest profile (only their surface differs), and a coherent
`TerrainNoise.BiomeDitherNoise` field (`BiomeClassifier.DitherBiome`) breaks the grass/sand material line into ragged patches
rather than a per-column checkerboard: inside the band around the desert line a savanna column may
take sand (it is always on the warm ramp, so sand never enters the temperate band), a steppe column
never does, and a desert column takes the grass biome across the line it actually borders — of the
two ramps that bind `min(warm, dry)`, a binding warmth gives steppe and a binding dryness savanna
(the label check in `BiomeBorderDitherFlipsCoherentPatchesNotSingleColumns`). The forest, steppe
and savanna borders share one grass fill and the taiga/frozen line lies under snow, so neither is
dithered. Shared yes/no biome rules live in `WorldGen/BiomeTraits.cs` (`ShowsBareRock`: the
snowless grass biomes whose stripped columns show stone; `RimsWaterWithSand`: the grass-fill
biomes whose lake and river rims wear sand), so the near fill and both far-LOD column paths ask one
rule. The ocean biome begins at `OceanShoreWeight`, and `Compute`
fades the land's above-the-waterline clamp out across `[OceanShoreWeight, 0.5]` — clamping only
while the biome was land put a hard step where the coast met the water, so the blended band is
ocean biome and slopes below the waterline into the sea. The ocean ramp falls to
`OceanFloorHeight` with rare warped **trenches** cut in, and its floor wears sand, gravel or stone by
depth (`OceanSandDepth`/`OceanGravelDepth`). The forest floor is a broad swell plus a positive-only
hill field gated by a hill-region mask (`Hill*`/`ForestBaseHeight`), so hills rise in patches with
flats between them. A **relief layer** (`WorldGen/TerrainRelief.cs`, BIOMEGEN §6) rides
on top, orthogonal to the climate: a continent-scale **highland weight**
(`TerrainRelief.HighlandWeight`, a smoothstep of `ReliefNoise`, seed offset 153, about 5000 blocks,
two octaves, over `HighlandMaskStart/End`) lifts the base of the forest and dune profiles
(`HighlandBaseLift`, added right after the swell) and multiplies the hill amplitude
(`HighlandHillGain`); `DryHillReduction` still applies after it, so an arid highland is a lifted
plateau rather than broken country. **Mountain ranges** rise only inside highlands: the rarity mask
(`MountainMask*`) is multiplied by the highland weight, and the warped ridge (`MountainSharpness`)
rides a lower-frequency **spine** (`MountainSpineNoise`, offset 154) and is sampled stretched
(`MountainRangeStretch`) along a hashed direction per `MountainRangeCellBlocks` cell, so a range is a
chain hundreds of blocks long; neighboring cells crossfade only in a narrow band at their borders
(`MountainRangeBlend`), and the cell lookup alone is jittered by `MountainRangeCellWarp` (offset 155)
so no straight north-south or east-west seam shows. The whole layer fades out within
`ReliefSpawnClearStart/End` (1536..2560 blocks) of the origin, so the spawn region and nearly every
scenario's ground stay as they were; highlands begin on the horizon the far LOD shows from the
spawn. A highland's lift raises the forest-profile height past `RiverLowEnd`, so its rivers dry
into washes, and its high ground reaches the rock and snow caps through the same altitude path as
before: a crest above `MountainRockHeight` in a snowless grass biome (`BiomeTraits.ShowsBareRock`)
is bare stone (`ColumnCache.GenerateColumn` reads the stripped soil, the far LOD a matching rock
cap), so it reads as rock rather than another wooded hill, and `SnowGenerator.ColdWeightAt` caps a
peak with snow. Every relief term is exactly neutral where the highland weight is 0 (no lift, a hill
gain of exactly 1, no range), so land with zero weight is bit-identical to the terrain before the
layer, except where an old out-of-highland mountain stood (those mountains are gone, so unmodified
chunks of an old save re-lay there). `TerrainReliefTests` (neutral at zero, ranges only inside
highlands, below the cloud layer, ranges run along their cell's direction, a continuous crossfade),
`TerrainReliefSeedTests` (a pure function of position and seed) and `LodReliefTests` (the LOD
nesting invariants, and the far LOD inside the chunk terrain over a steep highland range at every
level) lock it down. **Massifs and lone peaks** (`WorldGen/TerrainPeaks.cs`, phase B) are hashed
sites rolled through the shared `TerrainSites` helper (the one lake candidates use too) and summed
into the forest profile after the ranges (`WithMassif`, then `WithLonePeak`): a rare massif rolls an
absolute summit (`MassifMinSummit..MaxSummit`) and rises from `ReliefLandHeight` to it with a pointed,
spurred body, and a lone peak stands only where the pre-feature profile (`PreFeatureLandHeight`) is
flat over its rim (`PeakSiteFlatness`), never on a massif; a site's acceptance never reads `LandHeight`,
so nothing recurses, and its cached verdict (`MassifSiteAt`/`PeakSiteAt`) is reset by `WorldSeed.Apply`.
Both terms are exactly zero outside their discs and inside the spawn clear, so land there is
bit-identical, and the soft `SoftCeiling` (`ReliefCeilingHeight`, knee `ReliefCeilingKnee`) keeps any
spur under the clouds (`TerrainPeaksTests`, `TerrainSitesTests`, the site facts in
`TerrainReliefSeedTests` and `LodReliefTests`). **Islands and archipelagos**
(`WorldGen/TerrainIslands.cs`, phase C) rise out of deep water by lowering the ocean weight itself:
`BiomeClassifier.BiomeWeights` takes the pre-island weight (`PreIslandOceanWeight`, the old ocean
expression), asks `TerrainIslands.LandWeight` for an island land weight (the max of a large-island
channel, `IslandNoise` offset 159, and a small-island one, `IslandSmallNoise` offset 160, scaled by how
far past the shore band the water is), folds it as `pre * (1 - island)` only when that weight is above
0 and hands the result to `WeightsForOcean`, so every consumer of the ocean weight (the
classification, the height lerps and shore clamp, water bodies, trees, mobs, the far LOD) sees island
land as ordinary land of its climate biome, sand-rimmed through the Ocean-classified 0.35..0.5 rim.
A stateless hashed **archipelago** footprint (one elongated ellipse per `ArchipelagoCellBlocks` cell,
no cache) lowers the small channel's threshold and raises its gain, so islets string out in chains.
`LandWeight` is exactly 0 wherever the pre-island weight is at or below `OceanShoreWeight` (checked
before any read) and inside `ReliefSpawnClear*`; `ContinentNoise` and `SampleClimate` are untouched. A
massif or lone-peak site is accepted all-or-nothing on the pre-island weight (wholly continental, or
wholly on saturated island land), and an island site (`TerrainSite.OnIsland`) is scaled by the island
land weight at the column, so `BiomeWeights`, the peak terms and `LandHeight` are bit-identical on the
continent and the coast band by construction (`TerrainIslandsTests`, the island facts of
`TerrainPeaksTests` (`ASiteNeverStraddlesTheIslandFold`, `AnIslandSiteNeverRaisesTheShoreBandOrTheContinent`),
`TerrainReliefSeedTests.TheIslandTermIsAPureFunctionOfPositionAndSeed` and the island, archipelago and
shore-pit facts of `LodReliefTests`). `visual-examples/terrain-shape.json` (`VOXEL_TIME=1090`,
so the long run stays in daylight) films the origin valley, a highland interior, a range along and
across, an arid plateau, a highland border, a river wash, a range crossing a cell border, a far-LOD
vista, a massif from afar, its flank and its crown, a lone peak and a massif in the far LOD, and an
island from offshore, its beach, a sand knoll, an archipelago chain and islands in the far LOD
(`island_far`, `island_beach`, `island_knoll`, `archipelago_chain`, `island_lod_vista`). A low-frequency
**openness** mask (`Openness*`) flattens the hills a little and thins the trees toward none, so the
forest alternates between rolling woodland and open grass instead of one uniform canopy
(`TreeGenerator.TryGetTree` reads `TerrainGenerator.ForestOpenness`, scaled per species by its
`worldgen.openness`, so a taiga keeps more of its spruce and the desert's cactus ignores it). On land a hashed lake bowl and a warped river ridge carve below
their own water level and flood only those (every land biome but the desert; both fade out toward a
desert so a pond closes on its own rim) — see the Water invariant below. Far from the spawn (past
`GreatLakeSpawnClearRadius`, 1536 blocks) rare **great lakes** (`WorldGen/GreatLakes.cs`, phase D)
spread over lobed bowls a few hundred blocks across: their surface is chosen from their rim like a
pond's, they stand only on lake-holding ground with no ocean, desert, massif or lone peak inside and no
interior rise over the rim, ponds give way to them and a ravine guard keeps ravines out of their banks.
A river reaching one never refuses it (phase E): past an elevated lake's shore the river level steps
down a block at a time over a ramp from the lake's own surface to `SeaLevel`, so the river meets the
lake at its surface, and an outflow channel may leave the lake's low side down the same ramp; a lake
whose rivers cannot step down from it rests at `SeaLevel` instead. A cold great lake freezes into the
pack ice like any water. `visual-examples/terrain-shape.json` shoots them (`great_lake_shore`,
`great_lake_height`, `great_lake_bay`, `great_lake_island`, `great_lake_river`, `great_lake_elevated`,
`great_lake_outflow`, `great_lake_inflow`, `great_lake_lod_vista`).
A desert column is sand over `SandstoneDepth` sandstone blocks, then stone; a frozen column is a
`permafrost` top and two cells of permafrost soil over stone, under the snow layer the snow pass
lays (a ravine strips it down to bare stone, like a stripped desert column); forest, taiga, steppe and savanna share the grass-over-dirt fill
(sand or gravel only on the rims of real water); a ravined (bare-stone) column keeps those rules.
Existing saves regenerate their unmodified chunks under the current rules, so a biome change shows
as seams at the edge of the edited (saved) chunks of an old world.

**Cold weather** (`WorldGen/Features/SnowGenerator.cs`) runs last of the surface features and reads
`SnowGenerator.ColdWeightAt` — the climate's `TerrainGenerator.ColdWeight` (a biome-generation input)
so the snow line and the pack-ice band fade in as a column cools rather than snapping at a biome border,
**raised by altitude** above `MountainRockHeight` and full at `MountainSnowFullHeight`, so a warm
biome's peak wears a snow cap and the near stamp, the drawn depth and the far LOD all read the one
function. It freezes a cold column's surface water into a
walkable ice sheet (the top water cell becomes `ice`), lays one **layered snow** cell above the
solid ground (or the ice) and dusts the canopy tops of a cold column's trees, so a taiga or frozen
forest reads white from above (`SnowCanopyTests`). Freezing is not one line but a **pack-ice band**:
at or below `PackIceColdStart` water stays open, past `FreezeColdThreshold` it is a solid sheet, and
in between a coherent floe noise (`TerrainNoise.PackIceNoise`, seed offset 152, `PackIceFrequency`,
a wavelength of tens of blocks) decides ice or open water per column against a smoothstep of the
cold, so the edge breaks into floes and open leads whose ice share rises with the cold until they
close into the sheet. `SnowGenerator.IsFrozenWater(x, z, cold)` is that one rule, a pure function of
(x, z, seed) that the near stamp and the generated far LOD's `_iceTop` both call, for the sea, lakes
and rivers alike; hot springs never freeze (tested last, so the LodGen threads pay
`HotSpringGenerator.IsSite` only for water that would otherwise freeze), and open water gets no
snow. The far LOD's `_snowTop` stays the plain snow line (`SnowColdThreshold`), since it also
decides snow on land; because `PackIceColdStart >= SnowColdThreshold`, every frozen sheet cell is
snow-covered there exactly as the near stamp covers every ice cell it writes (`PackIceTests`). A snow cell's depth is drawn from `SnowGenerator.DepthAt` when its state
byte is 0 and from the state byte when a player stacks it (`SnowShape`); each layer is 1/16 of a
block, so a full stack is solid and partial layers are walked through (`BlockType.IsSolidCell`).
`WorldGen/Features/IceSpikeGenerator.cs` grows occasional single-column ice spikes on the frozen plains.

`ColumnData.Biomes` carries the per-column classification for the chunk mesher and the far LOD
(`ColumnCache.GetBiomes`); the LOD samples it per cell and wears the matching tile (snow for a cold
cell, snow-covered ice for a frozen sea sheet — snow tile when snow-covered, else ice, and never
flagged water, since the flag alone selects the fragment-shader water model whatever the tile; on
the generated path a sheet cell is frozen exactly when `SnowGenerator.IsFrozenWater` says so at its
center, so the far pack-ice edge carries the same floes and leads as the near one, while a supplied
column's sea ice still follows its stored Frozen biome), so a
distant desert and sea floor agree with the near chunks. Its beach and bare-rock tops ask the same
`BiomeTraits` rules as the near fill (`RimsWaterWithSand`, `ShowsBareRock`; a supplied column's rock
rule is `ShowsBareRock` with a zero `DirtDepth`, its beaches stay ocean-only), and steppe and
savanna fall through to the tinted grass top and the soil side band, exactly like the forest. A
frozen cell's soil side band and lake bed wear permafrost on both column paths (stone where the
cell-center column's `DirtDepth` is zero: `LodMeshBuilder`'s stripped-soil grid, read from the
prefetched `ColumnData` on the generated path too), while its top stays snow.
Vegetation is biome-gated. Trees come from the species files (`assets/data/trees`), each planted
only in its `worldgen.biomes`: `oak` in the forest, `spruce` in the taiga, the leafless `cactus`
(a thick stem whose few arms bend upward, wearing the generated `cactus_side`/`cactus_top` tiles;
see `tools/generate-biome-textures.ps1`) in the desert (where it wins over the dead tree, since
`TreeGenerator.SpeciesByBiome` takes the first species in name order), the leafless `dead_tree` in
the desert, the frozen tundra and the steppe, and `stone_pine` in the savanna, whose umbrella crown
stands in for an acacia (its reach equals the palm's, so `ReachChunkRadius` did not grow), while
`baobab` never generates (`EveryTreeGrowsInItsSpeciesBiome` checks every land biome a species
names); the forest and the steppe grow flowers in open clumps (`FlowerGenerator`), grass tufts
grow in the forest, steppe and savanna, and dead shrubs grow sparsely on desert sand and on the
frozen plains' permafrost (`FoliageGenerator.TryGetDeadShrub`). A tundra shrub replaces the snow cell
the snow pass already laid over permafrost: the one exception to the into-air stamp rule, confined
to permafrost ground (snow over any other ground is never replaced).
Wild cotton grows in **clumps** in the savanna (`CottonGenerator`: a savanna center whose desert
weight stays under `CottonMaxDesertWeight`, where the savanna/sand dither starts, so a clump never
lands on dithered sand), stamped after the pebbles and before the flowers and foliage. Placement is the reusable
`PlantClumps` rule over a `PlantClumpSpec`: one candidate clump per coarse cell, its center jittered
inside the cell (so centers are at least `MinCenterSpacing` apart), a disc whose `MinMembers` nearest
cells always belong and whose rest thin toward the rim, and a column asks every cell that can reach
it, so a clump is never cut at a cell or chunk border. A pure hash of (cell, seed), no noise, so no
reseed; a later clumped plant needs only its own spec and gate. Like flowers, the far LOD draws none.
How a tree is grown, stamped, felled and decays is in `### Trees`. Mob spawning (`MobSpawner`,
shared by slimes and goblins) takes a column whose biome spawns mobs (`MobSpawner.SpawnsMobs`:
forest, steppe or savanna; a supplied column's stored biome, a generated column's undithered
`TerrainGenerator.BiomeAt`). Steppe and savanna hold lakes and rivers like the forest
(`WaterBodies.CanHoldSurfaceWater`: every land biome but the desert).

**Scenery and landmarks** are pure per-cell functions like the rest of worldgen — each decided
from a hash of (cell, seed) or a `PlantClumps` clump, stamped per chunk and clipped at its border,
into air or stone only, and never drawn by the far LOD (`SceneryTests`, `PillarWellTests`,
`GeodeMeteorTests`, `OasisTests`, `PoolCairnTests`, `PondPlantTests`, `CaveLifeTests`,
`SpringStackTests`, `GravelPocketTests`, `SnowCanopyTests`). Each stamp also declares a conservative
`WriteBand` (a `StampBand`: the world-y range its `Stamp` can write, derived from its own code, never
from tuning), and `ChunkTerrainFill.GenerateBlockData` skips a stamp whose band misses the chunk, so the
output is bit-identical; a new feature stamp must add its band and its row in `StampGateTests`' feature
table (`stampGates: false` is the test-only ungated path, PERF_TODO G6). A stamp whose site can sit in
a neighbor chunk column (fallen logs, boulders, wells) gets only an upper bound from
`ChunkTerrainFill.NeighborhoodMaxHeight`, and the snow canopy pass (`SnowGenerator.StampCanopy`, beside
the gated `StampGround`) is never gated, since its leaves may come from neighbor trees:
- **Fallen logs** (`FallenLogGenerator`): a rare hollow log of 3-5 cells lying along X or Z on the
  floor, one per 32-block cell (a log never leaves its cell, so two never cross). Its block is the
  `hollow_<species>_log` of the **start column's** biome tree species (`worldgen.fallenLog`,
  `FallenLogGenerator.HollowLogFor`; `TryGetPlacedLog` is the one placement rule the stamp and the
  tests share): oak in the forest, spruce in the taiga, stone pine in the savanna. A biome whose
  species declares none gets no fallen logs (the desert's cactus, the dead tree of the tundra and
  steppe, the oasis palm), and neither does a start column whose stored, dithered surface biome
  differs from `TerrainGenerator.BiomeAt` (so no stone pine log lies on dithered desert sand) or a
  water column. The hollow log is an ordinary `facing: "horizontal"` model block, not tree wood, so
  it never welds, fells or decays: the shared `block/hollow_log` model is a square tube authored
  along Z (3 px walls round a 10 px hole, open at both ends, each wall split at the cell's middle so
  no quad covers a whole side), its outside the species log's own side tile and its inside and end
  rings the placeholder `hollow_<species>_log_inner`/`_end` tiles of
  `tools/generate-hollow-log-textures.py`. The state byte holds a facing (`FacingAlongZ` = north
  or `FacingAlongX` = west, picking the pre-turned model and boxes), never a `WoodState`. Its four
  wall `hitbox`es with `collidesByHitbox` leave the hole open to the raycast and the player body
  (only the player: mobs, drops and projectiles see a full cell). Its `hardness` is half the
  species log's full-cube one with the same axe gate, so it mines in half the time; it is
  worldgen-only (`hasItem: false`), drops the plain log (its own loot table) and is picked as it
  (`pickAs`). The far LOD draws none. Known gap: snow does not settle on a taiga log, since the log
  takes the cell the snow pass would cover. `HollowLogTests` (half hardness and the same gate, the
  bark outside, the plain-log drop, the pick, the open shell that can be stood on, the stamped
  facings) and `SceneryTests` (a whole stamped log in every biome that has one, turned along its
  axis); `visual-examples/hollow-logs.json` (`VOXEL_TIME=147`) frames one in each biome.
- **Boulders** (`BoulderGenerator`): a rare 2-4 cell blob of stone or cobblestone on the forest,
  taiga, steppe and savanna floor.
- **Cairns** (`CairnGenerator`): a three-cell cobblestone stack with a one-stone pebble hat on a
  local summit.
- **Ruined pillars** (`RuinedPillarGenerator`) and the **desert well** (`DesertWellGenerator`):
  broken stone columns on the surface, and the desert's ring-and-roof well dug into sand over
  sandstone.
- **Sea stacks** (`SeaStackGenerator`) and **hot springs** (`HotSpringGenerator`): stone stacks
  rising out of the sea near a shore, and steaming water bodies in caves.
- **Oases** (`OasisGenerator`): a desert water disc with a grass ring and palms — the one place the
  `palm` species (a worldgen species with `worldgen.oasis`) is planted, since the ordinary desert
  placement never picks it.
- **Reeds** (`ReedGenerator`) and **lily pads** (`LilyPadGenerator`): shore reeds on the shallow
  water edge and pads floating on ponds, both over `PlantClumps`.
- **Glow mushrooms** (`GlowMushroomGenerator`) on dry, dark cave floors, and **cave formations**
  (`CaveFormationGenerator`): stalactites hanging from ceilings and stalagmites standing on floors,
  both `needsSupport` and `flowResistant` (rock: a flow leaves them standing).
- **Geodes** (`GeodeGenerator`) and **meteor craters** (`MeteorGenerator`): a hollow, crystal-lined
  sphere deep in solid stone (its `crystal_block` is the one non-ore block on the reflective
  material path), and a blast crater with scattered meteor rock.
- **Gravel and sand pockets** (`GravelPocketGenerator`): blobs of loose rock buried in stone, both
  `falls` blocks, so mining into one can bring a small collapse down.
- **Underground pools** (`UndergroundPoolGenerator`): still water on carved cave floors.

The tuft density of the `tall_grass`/`dead_shrub` plants runs with the climate's wetness
(`FoliageGenerator.GrassDensityAt`, `GrassDensityDry`..`GrassDensityWet`) and every flower stands in
an open-forest clump (`FlowerGenerator`, the `PlantClumps` rule) (`MeadowTests`).

## Entities

Entities (mobs) are data-driven like blocks: `assets/data/entities/<name>.json` gives a display
`name`, an `hp` pool (optional: an entity with none has no health pool, `EntityType.MaxHealth` 0,
and the `Mob` constructor refuses it), an optional `loot` table rolled on death, and an optional
`model` Blockbench project (default `assets/models/entity/<name>.bbmodel`), or instead of `model`
a non-empty `variants` list of projects, one per size class or look (`EntityType.Variants` /
`VariantPaths`, whose first entry is always `Model`; the icebergs use it). `ContentRegistry.Load` parses them into
`EntityType`s published on `EntityRegistry` (`RequireEntity`/`TryGet`), and parses each entity's
`.bbmodel` once, GL-free, into `EntityType.Model` (a `BbmodelData`: parts, skin pixels and animation
clips). The entity, the renderer and the item debugger all share that one parse. A broken entity model
therefore fails content load, and with it every test through `TestContent`. Entity models reuse
`BbmodelLoader`, which takes an optional required-group list: the player rig passes `RequiredParts`,
entities pass none. The slime draws through `EntityModel`, which bakes the whole project into one mesh
plus its skin. The goblin draws per part: `PlayerRig.FromBbmodel(type.Model)` +
`PlayerRig.BuildMeshes`, one mesh per group, each placed by its `EntityAnimator`.

Every hostile derives from the abstract **`Mob`** (`Mob.cs`). It exposes `Type`, `Position` (the body
center), `Health`/`IsDead`, `Facing` (yaw, the model faces -Z at 0), `HitFlash`, and the body box as
`BodyHalfExtent`/`BodyHalfHeight`. `HeadAboveCenter` is where the healthbar sits. It also has
`TakeDamage` and `Shove`. `World`'s `MobDirector` rebuilds one `_mobs` list each frame (`MobDirector.RefreshMobs`), after every mob
manager has run and before `ThrowAction.UpdateThrown`, so a mob killed or despawned this frame is never hit or given
a healthbar. The punch, the slice, the player's thrown items (`ThrownProjectile.Update`, an AABB overlap
against each mob's body box, so a tall goblin is hit through its head and shins) and the healthbars
(`EntityHealthbarRenderer`, buffers sized by `MobConfig.MobsMaxAlive`, the hard cap
below) all read that one list. The render passes keep one list per kind, because the meshes differ; `ReflectionEntities`
carries both. Ring spawning on dry, loaded forest surface goes through `Simulation/MobSpawner.cs`
(`TryFindSurfaceSpawn`), and each manager wraps it with its own radii and body-center offset.
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

**Shared mob rules** live once in `Mob` (or a GL-free helper beside it), so each manager applies them
with one call, and they run only in the authoritative simulation (singleplayer and the server; a
client's mirrors are advanced by `Mob.AnimateMirror` alone). **Fall damage**: every landing a mob's
physics reports (`Mob.NoteLanding`, which also feeds the landing dust) bills `MobFallDamage.Damage`
over the drop its impact speed implies: free up to `Mob.SafeFallBlocks` (`MobSafeFallBlocks`; a slime
adds its own hop's apex, so its hops never hurt it), then `MobFallDamagePerBlockShare` of the mob's
full health (at least 1) per whole block past it, and nothing when the feet were in water
(`MobFallDamageTests`). **Lava**: each manager calls `Mob.UpdateHazards` right after the mob's own
update; a body box touching a burning fluid (`MobLava.BodyInLava`, sharing the cell test
`BurningFluid.AnyInBox` with `PlayerBodyProbe.InLava`) takes `MobLava.DamagePerTick` (`MobLavaDamageShare` of its full health, at
least 1) every `MobLavaTickSeconds` with the hit flash, leaving it clears the partial tick, and a
death it causes rolls the loot as usual (`MobLavaTests`). `visual-examples/mob-hazards.json`
(`VOXEL_TIME=147`) films the falls and the lava bath. **Despawn fade**: a mob past every target's
despawn radius is not removed outright; the manager calls `Mob.BeginDying` without rolling any loot,
so it plays its death pose and is dropped once that has run, and like any dying mob it leaves
`LivingCount`, the saves and the server's broadcast at once (`SlimeTests`, `GoblinManagerTests`,
`MobTargetTests`). **Loot toss**: every item a killed mob drops (its table's rolls, a goblin's carried
flint) is spawned with `DropManager.SpawnAt`'s toss set to `MobLoot.RandomToss()`, a random outward
heading at `MobLootTossMinSpeed..MaxSpeed` plus `MobLootTossLift` on top of the drop's own pop, so
the loot arcs out of the body (`MobLootTests`). **Notice range**: `MobTargets.Nearest` only picks a
target within its `MobTargets.NoticeRadius`, `MobNoticeRadiusBlocks` horizontally, times
`MobSneakNoticeFactor` while the target sneaks (`IMobTarget.IsSneaking`: the camera's own crouch, a
`ServerPlayer`'s cosmetic `NetPlayerFlags.Sneaking`, untrusted but only ever helping the one who
claims it); a target past it is neither chased nor faced, though despawning still measures every
target (`MobNoticeTests`). **Lose interest**: each manager picks the chased target through
`Mob.PickTarget`, the notice rule plus the mob's `MobInterest`, which checks the line of sight from
`Mob.SightOrigin` to the target's eye every `MobSightCheckSeconds` (`BlockRaycaster.SegmentHitsBlock`)
and gives the target up once the line has stayed blocked for `MobLoseInterestSeconds`; a clear check
wins it back and a different target starts the count over. A mob chasing nobody (none noticed, or
interest lost) **wanders**: `Mob.WanderGoal` picks a point `MobWanderMin..MaxRadiusBlocks` away, anew
every `MobWanderRepickSeconds` or on arrival, which a slime hops toward at its usual cadence and a
goblin strolls to at `MobWanderSpeedFactor` of its walk (`Goblin.Update`'s `wander`); with nobody in
play at all it stands still as before. Idle for `MobIdleDespawnSeconds` and farther than
`MobIdleDespawnMinDistanceBlocks` from every target, it despawns through the same fade
(`Mob.ShouldIdleDespawn`; `MobInterestTests`). **Torch-safe ground**: the ring spawn search refuses a
column whose standing cell holds `MobSpawnBlockLightLimit` or more block light
(`MobSpawner.IsLitByBlocks`, read through `EntityLight.Sample` from the light each chunk publishes
with its mesh; a headless server publishes it too, and a cell without a snapshot reads as unlit), so
torches and lamps keep a base clear of new mobs (`TorchSafeSpawnTests`). **Ledge caution**: a mob
will not step (a goblin's next step, `MobLedgeProbeBlocks` past its body, zeroing its move in
`Goblin.Update`) or hop (where a slime's flat-ground hop would land, sitting that hop out) onto ground
more than `MobLedgeMaxDropBlocks` under its feet unless its target stands lower than that
(`Mob.IsLedgeAhead` over `LedgeProbe`, which reads the live cells rather than the generated heightmap
`World.TryGroundHeight` returns, so caves and dug pits count; an unloaded cell or lava below is a
bottomless drop and water a soft landing), so a cautious mob never takes the fall damage above
(`LedgeCautionTests`).

The first entity is the **slime** (`Slime` + `Simulation/SlimeManager.cs`), owned by `World`. It hops
toward the player, damages on contact only while airborne (a slime merely bobbing in water is
harmless), is shoved (harmlessly) by the player when grounded, floats to the surface of water like an
item drop (its water hops keep a land hop's total speed but halve the vertical and move it into the
horizontal, so it skims forward as a low jumping slide), and rolls its `loot` table on death. Natural
spawns appear on dry forest surface in a 20-40 block ring around the player; `SlimeManager` holds the
cadence, damage and shove numbers via `MobConfig.Slime*`. Slimes come in **sizes**
(`Slime.Size`, `SlimeSize.Min`..`Max`, fixed for life): every scaled stat is the size-1 value times
`1 + (size - 1) * step`, one `SlimeSize*Step` per stat — the body scale (`SizeScale`: the drawn model
in `MobPasses.SlimeTransform`, multiplied over the squash spring, the collision box
`BodyHalfExtent`/`BodyHalfHeight`, the healthbar height), the health pool (`Mob.MaxHealth`, virtual,
over the JSON `hp`; the save and net clamps read it), the contact damage and the loot rolls (the table
rolled `LootRolls` times). The size is saved (`EntitySaveData.MobData.Size`, absent = 1) and sent (a
slime entry's `NetEntity.Ammo`). A ring spawn rolls its size (`SlimeSize.Roll`, a pure function of the
night, an underground flag and one uniform roll): each size past 1 is reached with the large-size
chance again, which eases from `SlimeLargeChanceDay` to `SlimeLargeChanceNight` with
`DayCycle.NightFactor(World.LightingTime)` — the same sun `FrameLighting.SetLighting` lights the world
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
the left-click punch (`LeftClickAction.UpdatePunch` + `Combat`): a mob inside the forward 3-block, 30°-half-angle
cone with line of sight takes the held item's `entityDamage` and a knockback shove, and while one is in
the cone mining stands down (attack-first). `Mining.EntityDamage` is the bare-hand baseline (1) an item
overrides with its `entityDamage` (primitive tools 3, copper tools 5, obsidian blade 6). The obsidian
blade (`attack: "slice"`) instead swings a two-phase sweep (`LeftClickAction.UpdateSlice` + `SliceCombo`): each
swing hits the nearest two mobs in a forward hemisphere of the same reach with line of sight, the
buffered return swing is at half power, and the damage lands at the swing's impact frame rather than on
the click, with the arm and blade carried across the front by `PlayerModel`. The iron sword
(`attack: "sword"`) is the long blade: `LeftClickAction.UpdateSword` runs `SwordCombo`, a chain of
three swings (`SwordSwing`: the cut, the back cut, the chop) that a click buffers while one plays and
a press within `SwordCombo.ChainWindowSeconds` of the last swing's end continues, a later press
starting over at the cut. Each swing has its own rules in `SwordSwings`: the cut and the back cut
sweep the forward hemisphere for the nearest `SweepTargets` mobs (the back cut at reduced power), the
chop takes only the one mob in the punch cone at the highest power (`Combat.SwordDamage`) with a
double shove and the heaviest hit-stop, and each has its own length and impact frame
(`PlayerModel.SwordDuration`/`SwordImpactProgress`), so the chain paces itself. All three blade
paths land through one `LeftClickAction.StrikeNearest` (nearest first, line of sight required) and
`MobStrikes.StrikeMob`, whose shove speed a chop doubles.

A larger slime is pushed less: every shove (a punch, a slice, a grounded player walking into it) is
divided by its body scale (`SlimeSize.ShoveSpeed`, applied in `Slime.Shove`), while a split child's
launch (`Slime.Pop`) is not (`SlimeKnockbackTests`). A larger slime also hops higher and farther after
a longer crouch: the hop's vertical and horizontal launch speeds and the crouch time are the size-1
ones times the body scale to `SlimeHopLiftExponent`, `SlimeHopReachExponent` and
`SlimeHopWindupExponent` (`SlimeSize.HopLift`/`HopReach`/`WindupScale`, read by `Slime.HopVelocity`
and the squash spring's windup; `SlimeHopTests`). The largest size (`SlimeMaxSize`, 4) is the rare
**giant**: `SlimeSize.Roll` takes the last step into it with only `SlimeMaxSizeChanceFactor` of the
usual threshold, so it is rarer than the size below it in every light (`GiantSlimeTests`), and it
splits into that size like any other; `visual-examples/slime-sizes.json` stands it beside the other
three. **Cave slimes**: slimes also spawn on dark cave floors in the same ring
(`MobSpawner.TryFindCaveSpawn`, any biome): each try scans a column down from a random height within
`SlimeCaveSearchHalfHeight` of the player's feet for at most `SlimeCaveScanDepth` cells and takes the
first solid floor under two clear, dry cells, if it lies at least `SlimeUndergroundDepthBlocks` under
its column's surface, its standing cell has at most `SlimeCaveMaxSkyLight` sky light (a cell without a
published light snapshot reads as full sun) and it is not torch-lit. `SlimeManager.TryFindSpawn`
searches caves first for a player standing underground and the surface first otherwise, each falling
back to the other, and the size roll treats a cave spawn as underground (night chance); this makes the
"surface-only ring never finds such a spot" note above out of date (`CaveSlimeTests`). **Slime
merge**, the split's inverse: after its main loop `SlimeManager.Update` merges every pair of living,
same-sized slimes below the largest whose bodies touch and which have both lived
`SlimeMergeCooldownSeconds` (`SlimeMerge.CanMerge` over `Mob.AgeSeconds`, so a split's children never
merge straight back): both vanish without loot and one slime of the next size pops in midway on the
lower feet (only where its body fits), a forced spawn the hard cap always has room for since the pair
leaves first, carrying the pair's share of their pools (`SlimeMerge.MergedHealth`). Each slime merges
at most once an update; only the authoritative simulation merges, and a client sees the pair leave
its snapshot (playing their death pop) and the merged slime arrive (`SlimeMergeTests`).

The second entity is the **goblin** (`Goblin` + `Simulation/GoblinManager.cs`, owned by `World`), a short
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
  `ThrowAction.RefreshProjectiles` merges the stones with the player's throws into one projectile list,
  so they render and mirror with no
  new plumbing. A stone deals `GoblinStoneDamage` when it touches the player's body.
- **Melee.** Out of ammo, the goblin closes to `GoblinStandoffBlocks` and plays the `melee` clip when
  in reach and off `GoblinMeleeCooldownSeconds`. The damage and knockback land at that clip's `impact`
  event, and only if the player is still in reach (`TryTakeMeleeImpact` + `Goblin.InMeleeReach`).
- **Immunity.** A creative or flying player takes no damage from stones or swings, which is what lets
  `visual-examples/goblins.json` film them from a hovering camera.
- **Movement.** Goblins auto-jump a one-block step they walked into, float and paddle in water like
  the slime, and are shoved away when they overlap the player on the ground.
The launch velocity comes from **`GoblinAim.TryLaunchVelocity`**, the low arc of the exact ballistic
solve under `ThrownProjectile.Gravity`. Always pass that constant, never a second gravity. It is an alias of `BodyMotion.Gravity`, the one gravity every loose body (drops, primed TNT, falling blocks, debris, goblins, slimes) falls under. The level
range of a stone is `v²/g` (`GoblinStoneSpeed` 20 against gravity 28 gives about 14.3 blocks). The
throw band must stay inside it, with margin for a chest-height target and the semi-implicit Euler drop.
`GoblinAimTests` locks that relation down, so retuning the speed or the band cannot silently make edge
throws fall short. An out-of-range target still gets a 45-degree maximum-range toss toward it. Every
goblin number lives under `MobConfig.Goblin*`.

A goblin's life has more rules than its AI. **Flee**: below `GoblinFleeHealthShare` (a quarter) of
its pool it turns and runs from the player at `GoblinFleeSpeedFactor` of its walk, never throwing or
swinging (`Goblin.IsFleeing`, `GoblinFleeTests`). **Scavenge**: a live goblin with ammo room joins
the world's drop pass as a `DropCollector` that takes only flint (`GoblinManager.AppendCollectors`,
`AwardScavenged`, capped at `GoblinStoneMax`; a client's mirrors never scavenge)
(`GoblinScavengeTests`). **Dodge**: a charged throw (at least `GoblinDodgeChargeThreshold`) aimed at
a goblin inside `GoblinDodgeConeDegrees` and within `GoblinDodgeRangeBlocks` makes it sidestep out of
the line while the aim holds, its side fixed at spawn (`GoblinManager.IsAimedAt`, `Goblin.Decide`;
`GoblinDodgeTests`). **Pack alert**: a goblin that notices a player alerts every other goblin within
`GoblinPackAlertRadiusBlocks`, which skip their first-throw delay so the pack throws together
(`Goblin.TakeJustNoticed`/`AlertPack`, `GoblinPackAlertAround`; `GoblinPackAlertTests`). **Pack
spawning**: a ring spawn rolls one to `GoblinPackMaxSize` goblins, the rest landing within
`GoblinPackSpawnRadiusBlocks` of the found cell, each re-checked against the shared natural cap
(`GoblinManager.TryFindPackMate`; `GoblinPackSpawnTests`). **Torches**: a spawn may carry up to
`GoblinTorchMaxCount` torches (a roll whose chance is small by day, larger at night and highest
underground), equips one while it is dark (drawn at the off hand's `ArmL` anchor through
`BodyEffect`, its flame point joining `World.FireCells` for light and embers), and drops the carried
torches on death with the flint; the count is saved (`MobData.Torches`) and sent (the mob
`NetEntity.ItemId` byte) (`GoblinTorches`, `GoblinTorchTests`). **A notice pip**: a "!" billboard
pops over a goblin's head the moment it first spots a player (`GoblinNoticePipTests`).

Every mob shares three more hazards. **Fall damage**: `Mob.NoteLanding` bills `MobFallDamage.Damage`
over the drop its impact speed implies — free up to `Mob.SafeFallBlocks` (plus a slime's own hop
peak), then `MobFallDamagePerBlockShare` of the pool per whole block, nothing when the feet were in
water (`MobFallDamageTests`). **Lava**: `Mob.UpdateHazards` with `MobLava.BodyInLava` deals a pool
share every `MobLavaTickSeconds` with the hit flash, a death it causes rolling loot as usual
(`MobLavaTests`). **Ledge caution**: `Mob.IsLedgeAhead` over `LedgeProbe` (live cells, not the
heightmap) stops a step or a hop that would land more than `MobLedgeMaxDropBlocks` down unless the
target stands lower (`LedgeCautionTests`).

### Hell mobs

Hell's cavern pockets hold two harder kinds (plans/LAYERS.md `L-10`), both `HellMob`s
(`src/Entities/HellMobs/`), which like the goblin are `RiggedMob`s: a walking body animated from its
`.bbmodel` clips through an `EntityAnimator` (`RiggedMob` owns the walk, auto-jump and float physics, the
facing, the death, spawn and hurt clips, a mirror's animation and `BodyEffect`; `Goblin` derives from it
too). Both models, their skins and the `ember_shard` sprite come from `tools/generate-hell-mob-models.py`
(deterministic, `--check`); both are humanoid rigs (`EntityContract.HumanoidParts`, head and arms under the
torso) checked at startup (`Hellbrute.ValidateContent`, `Cinderwitch.ValidateContent` over
`EntityContract.RequireHumanoid`).
- **The hellbrute** (`hellbrute.json`, hp 60, loot ember shards and coal) is slow and tanky: it walks at
  its target, charges at `HellbruteChargeSpeedFactor` once it sees it (a clear line from its eye to the
  chest, `HellMob.SeesTarget`, throttled) within `HellbruteChargeRangeBlocks`, and in reach plays its
  `melee` clip, whose `impact` event bills `MeleeDamage` with a heavy knockback to every non-immune target
  still in reach (`HellMobManager`). A player walking into it barely moves it (`HellbruteShoveSpeed`).
- **The cinderwitch** (`cinderwitch.json`, hp 24, loot ember shards) keeps
  `HellCasterMinRange..HellCasterMaxRange` and, grounded off its cooldown with a clear line, plays its cast
  (the `throw` clip); at its `release` event the manager launches a **fire bolt**: a `ThrownProjectile`
  of the `ember_shard` item on `GoblinAim`'s low arc (the band stays inside `v^2/g`), merged into the
  projectile list like the goblin stones (`HellMobManager.Bolts`), shedding embers in flight. A bolt that
  touches a non-immune target deals the caster's `BoltDamage` and applies `burning` through
  `StatusEffects.Apply(IMobTarget, "burning", HellBoltBurnScale)`; any touched target or block stops it.
- **Layer difficulty** (`LayerDifficulty`, GL-free): `1 + layer * MobConfig.LayerDifficultyPerLayer`,
  capped at `LayerDifficultyMaxMultiplier`, scales a mob's pool (`HellMob.MaxHealth`), its damage and a
  caster's cooldown, from the hell layer of its spawn height (`HellMob.Layer`, fixed for life, so a save
  restore and a client mirror at the same spot agree); a ring spawn rolls a pack of up to
  `LayerDifficulty.PackSize(layer)`.
- **Spawning** (`HellMobManager`, one manager for both kinds, joined to the world's `MobCaps`): every
  `HellSpawnIntervalSeconds`, under the natural cap, around a target standing in hell only,
  `MobSpawner.TryFindHellSpawn` scans ring columns for an open cavern floor whose two standing cells are in
  hell and whose block light is at most `HellSpawnMaxBlockLight` (no skylight reaches hell); the overworld
  cave slimes stop above `HellTopY`. `HellCasterShare` of the spawns are casters. Despawn and the shared
  hazards work as for the goblin.
- **Plumbing**: `World.HellMobs`, `MobDirector` (update, damage billing, `RefreshMobs`, the `spawn` op names),
  `MobPasses.RenderHellMobs` and the near shadow cascade (no mirror: hell lies far under the water plane),
  `entities.json` by kind name, the `EntityList` kinds above and `ClientMirror`'s reconcile, and the item
  debugger's ENTITIES tab. Tests: `HellMobTests`, `HellMobManagerTests`, the hell round trip in
  `WorldLifecycleTests.EntitiesRoundTripThroughSaveAndReload` and `NetworkEntitySyncTests`.
- Known gaps: the bolts, like the goblin stones, are not on the wire (a client sees only their damage and
  the burning); a client's mirror shows no attack clip; the casters walk (flight is `L-14`).

### Icebergs

Drifting icebergs float on the open sea at the pack-ice edge. They are entities, never blocks, and
**not mobs**: an `Iceberg` (`src/Entities/Icebergs/`) has no health, is never a punch, slice or
projectile target, draws no healthbar and counts toward none of the mob caps (`MobCaps`); the
`IcebergManager` (`src/Simulation/Icebergs/`, owned by `World`, main thread only) keeps its own
`IcebergConfig.NaturalCap` (the ring spawner) and `MaxAlive` (every forced spawn: a scenario, a
save restore). All tuning lives in `Config/IcebergConfig.cs`.

- **Content.** `assets/data/entities/iceberg.json` names no `hp` and lists its size classes as
  `variants` (`iceberg_small`, `_medium`, `_large`.bbmodel, generated with their shared skin by
  `tools/generate-iceberg-models.py`, byte-identical re-runs, `--check`). Model y = 0 is the
  **waterline**; every cube is axis-aligned and snapped to 8 px vertically, the one cube crossing
  the waterline is a **shelf** whose top is 8 px up (a swimmer can climb onto it), and every cube
  above it is an 8 px **step** inset from the one below on all four sides, so walking auto-step
  (`PlayerPhysics.WalkStepHeight`, half a block) climbs to the top from any side;
  `BodySolidsTests.ASwimmerCanBoardEveryIcebergVariant` enforces it.
- **Shape and placement.** `IcebergShape` reads a variant's collision boxes from the model's own
  cubes through `EntityModel.CubeBoxes` (the frame the baked mesh uses) and is the one definition
  of a berg's placement: `RotateQuarter` turns a box (each quarter turn maps (x, z) to (z, -x),
  exactly `Matrix4x4.CreateRotationY(pi/2)` on a row vector, so the boxes stay axis-aligned) and
  `ModelMatrix` is the matrix the renderer draws with (`IcebergShapeTests` checks they agree for
  every turn). A berg rolls a variant, a **horizontal-only** scale (`ScaleMin..ScaleMax`, so step
  heights and freeboard stay exactly as authored), a quarter-turn yaw and a seed, all fixed for
  life; its center's Y is fixed at the waterline. Its mass is `Density * Volume * scale^2`.
- **Motion.** `IcebergMotion`: the velocity eases (`WaterDragRate`) toward a drift along the world
  wind (`Wind.Direction`, turned by a seed-hashed angle within `DriftSpreadRadians`, at
  `DriftSpeed`), a push adds `PlayerPushForce` along the summed contact normals over the mass (the
  same push moves a big berg less), capped at `MaxSpeed`. A berg moves along X then Z; a move
  whose leading face enters a cell layer holding a solid or unloaded cell anywhere across the
  berg's bounds (the pack ice, shallows, land), or whose bounds would overlap another berg, or
  whose boxes would push into a standing (not riding, flying or dead) target's body, stops that
  axis. A berg only moves while a target is within `SimulateRadiusBlocks` and every chunk under it
  is loaded.
- **Pushing** is derived from positions, with nothing on the wire: a target in side contact with a
  berg box (`IcebergContact.TrySideContact`, within `ContactSlackBlocks`) pushes it along the
  contact normal, unless it rides the berg, flies or is dead (`IMobTarget.IsFlying` / `IsDead`,
  default members the camera and `ServerPlayer` override).
- **Standing on a berg.** The player body collides with berg boxes through one seam,
  `Player/IBodySolids.cs`: `BodyCollision.BodyCollides` / `MoveOnAxis` / `TryStepUp` /
  `PushOutOfBox` take an optional `IBodySolids`, `PlayerPhysics` passes `Camera.BodySolids` (the
  world's `IcebergManager`), so walking, auto-step, the swim exit and landing all treat the boxes
  as solid. The rider is carried client-side (`IcebergManager.CarryAndPushOut`, after the bergs
  moved): a body riding a berg's boxes where they stood before the move is moved by the berg's
  displacement through the body collision with its own berg ignored (terrain and other bergs still
  stop it), then a body left inside any box is pushed out. Singleplayer runs it after
  `IcebergManager.Update` (`MobDirector.UpdateIcebergs`), a client after its mirrors dead-reckoned
  (`MobDirector.AnimateIcebergMirrors`). A placement into a cell a berg box overlaps is refused
  (`IcebergManager.OverlapsCell`, beside the falling-cube check); the air above a low ledge is free.
- **Wobble** (`IcebergWobble`) is render only: bob, pitch and roll as two sines each of hashed
  periods and phases, a pure function of (seed, lighting clock), smaller for a bigger berg. The
  collision boxes never bob, so a rider's feet may visually bob by its small amplitude.
- **Spawning and life.** Every `SpawnIntervalSeconds`, below the natural cap, the spawner tries
  ring points around a target in turn (`IcebergSpawner.TryFindSite`): the stored column must be
  `Biome.Ocean` at `WorldGenerationConfig.SeaLevel` (no river or lake), its climate in the band
  (`IcebergSpawnRule.InBand`: cold from `SpawnColdMin`, a little below `PackIceColdStart`, up to
  `FreezeColdThreshold`, and the top water not frozen by `SnowGenerator.IsFrozenWater`), the berg's
  whole placed bounds loaded and free of solids (deep and wide enough) and no target within
  `SpawnClearRadiusBlocks`. The climate read is an injectable `IcebergClimate`; the default reads
  worldgen and none for a column a world source supplies. A fresh berg rises out of the sea over
  `EmergeSeconds` (render only); past `DespawnRadiusBlocks` from every target it is removed; when
  its rolled life runs out and nobody rides it or stands within the clear radius it sinks over
  `SinkSeconds` (no longer colliding) and is removed. With nobody online nothing spawns, moves or
  despawns.
- **Saves and the wire.** `entities.json` carries an optional `Icebergs` list (variant, scale,
  turns, seed, waterline center, velocity, life left); a saved berg is restored once every chunk
  under its placed bounds is loaded. On a server each berg rides `EntityList` as
  `NetEntityKind.Iceberg` (see "Multiple players and chat"); a client mirrors it without
  simulation (`Iceberg.Mirror`, fully emerged, dead-reckoned along its velocity for at most
  `MirrorMaxLeadSeconds` past a snapshot, `IcebergManager.ReplaceMirrors` / `AnimateMirrors`).
- **Rendering.** `Rendering/Actors/IcebergPasses.cs` builds one mesh and skin per variant
  (`EntityModel.FromData`) and draws each berg with `IcebergPasses.ModelMatrix` (the wobble's tilt
  about the waterline center, then `IcebergShape.ModelMatrix` at the center raised by the bob and
  lowered by the rise or sink) in the entity pass (before contact AO), the near shadow cascade and
  the water mirror (whose reuse motion signal reads each berg's center). The wobble clock
  (`IcebergPasses.Time`) is set once a frame before the shadows, so the three agree.
- **Debugger.** The item debugger's ENTITIES tab offers one mode per variant for the iceberg
  (`ITEM_DEBUGGER_SELECT=iceberg ITEM_DEBUGGER_ANIM=iceberg_large`).

Tests: `Entities/Icebergs/` (shape, motion, wobble, spawn band, contact, the berg),
`Simulation/Icebergs/` (`IcebergManagerTests`: drift stopped by the ice and by other bergs, frozen
over unloaded chunks, a small berg pushed farther than a large one, carry, push-out, mirrors,
despawn and sinking; `IcebergSpawnerTests`; `IcebergWorldTests`: the spawn op, standing on a berg,
the placement refusal and the save restore), `Player/BodySolidsTests`, the hover rule in
`MovementRulesTests`, the codec and protocol tests and `GameServerTests.AnIcebergReachesTheClientsEntityList`.
`visual-examples/icebergs.json` (`VOXEL_TIME=147`) films the three sizes, the wobble, the drift,
standing on a berg, a push and the reflection.

Known gaps (TODO `M-27`): no natural bergs on a column a world source supplies (no worldgen climate there);
item drops, mobs, projectiles, TNT blasts, the punch and slice cones and the block raycast ignore
bergs; the server's endpoint-collision rule is not judged against bergs (only the hover rule is);
a body merely resting against a berg nudges it a hair until contact breaks; mirrors dead-reckon
without collision; a rider's feet visually bob by the wobble; and a block that lands or hardens
inside a berg's bounds (a falling block, a fluid crust, a felled tree) is not refused and is
carried inside it.

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
  (`HitKick`) and an eased `HitFlash`; an active status effect tints it through `uStatusTint`, mixed
  under the flash (see "Status effect schema"). A killed mob **rolls its loot at once**, then stays in the
  manager's list (still drawn, no longer a target, a threat, a saved or broadcast mob or part of the
  live count) until its death has played; `MobDirector.RefreshMobs` skips dying mobs, the spawn caps use
  `LivingCount`, and a client's `Remove` (the server stopped reporting it) starts the death instead of
  popping it. `AnimateMirror` advances the clocks of mobs a client never simulates.
- **Slime**: squash and stretch ride one under-damped spring (`Slime._squash`): a 0.2 s crouch before
  each hop (the launch waits for it), a stretch kick on take-off, a landing kick in proportion to the
  fall, an idle breath, a ballooning pop on death and an overshooting pop-in.
- **Goblin**: its facing eases (`Mob.TurnToward`); `RiggedMob.BodyEffect(feet)` (the goblin's and the hell mobs') is the pose the clips
  cannot give (a lean that follows the walk, the hit flinch, the death fall and shrink, the spawn pop)
  and the renderer multiplies it onto every part and the held flint. The clips are generated by
  `tools/generate-goblin-clips.py` (re-running is byte-identical, `--check` verifies): idle, walk,
  throw, melee as before (the `release` at 0.42 s and `impact` at 0.30 s are pinned by gameplay
  pacing) plus the optional `hurt`, `death` and `spawn`. `EntityAnimator.PlayOverlay` plays `hurt` as
  an **additive overlay** (translation added, rotation composed) so a hit never cancels a throw or
  swing; `death` and `spawn` are ordinary actions.
- **Arm frames** (`ArmOverlays.ApplyArmFrames`): the base arm is a forward flex plus a yaw, so both
  bones stay in one plane and it cannot reach out sideways. Poses that need that are given as an upper
  arm *roll* (abduction about the front axis, then flexion about X, then yaw; the left arm mirrors the
  roll and yaw) and a forearm *flexion plus bend*, slerped over the base frames by a weight
  (`LimbFrames.UpperFrame`/`ForeFrame`/`Blend`). With the arm held out the elbow's X hinge is the vertical axis, so
  a plain forearm flexion swings it forward in the horizontal plane and a bend about Z lifts it up. In
  use: the **jump** (arms out to the sides, a soft elbow, both knees folded under; a sprint jump trails
  them back), the **punch** (the elbow flares out and forward at the chamber with the forearm folded to
  the front, the weight `Retract * (1 - Extend) * (1 - Recover)` fades it to nothing at full extension
  where the IK frame on the aim line takes over, so the elbow swings in and forward through the thrust),
  the **throw charge** (the upper arm out to the right and a little behind the body, the forearm bent up
  at about a right angle, the shoulders wound back toward the throwing side, then a whip forward and a
  settle) and the **swim stroke**. The hand anchor follows the forearm frame, so the held item does too.
- **Bow draw** (`UsePose.Bow`, `BowPose`): an item whose `use.pose` is `bow` (the `bow`) holds a two-handed draw while the timed use charges: the right (item) arm goes out forward through `ArmOverlays`, the left arm is folded back to the cheek, and the torso twists side-on and leans back a little (`SecondaryMotion`), all weighted by the use charge (`BowPose.BodyWeight`/`DrawWeight`). It holds at full draw and, when the button is let go (`PlayerModel.CancelUseCharge` from a real draw), the string arm snaps back within `BowPose.SnapShare` of `ReleaseSeconds` and the rest follows; the camera narrows with the draw like a throw's charge (`Camera.ThrowCharge` is fed `Player.BowCharge`). The item debugger's `BOW` mode and the `draw`/`loose` player ops of `animations.json` show it.
- **Cast feedback** (MG-10, cosmetic only): a staff cast plays `PlayerModel.Cast`, `PlayerModel.CastDuration` (0.45 s) of `CastMotion` (GL-free pure functions of the cast clock): the right arm raises the staff over the head-front, thrusts it straight forward with a snap (an ease-out) and a small forward lean (`CastMotion.Lean` into `SecondaryMotion`'s torso pitch), recoils a little and eases back, its weight 0 at both ends so the arm leaves and returns to the base pose without a jump; `PoseBuilder` lays it after the throw, and `BodyPartMatrices` counts it as an authored right arm. `LocalPlayer.CastSwing` (the right-click chain's staff branch, and the `player` op's `cast` action, gesture only) adds a light view pinch (`SpellConfig.CastFovKickDegrees`). The server rebroadcasts an accepted cast as `NetPlayerAction.Cast`, which `RemotePlayer` replays. The rest is `Magic/SpellFeedback`, the `SpellCaster.Feedback` hook (never built on a headless server): for the local player's own accepted cast the `spell_cast` sound (`spell_fizzle` on a fizzle, singleplayer only, since a client never learns of one) and a burst of `SpellSparks` at the bolt's launch point in the element's status tint (`SpellSparks.ColorOf`, else `BoltDefaultColor`; gray on a fizzle); on a client a cosmetic preview of the bolt (`SpellBoltPreview`: `SpellProjectile`'s flight and stop test, trail and impact sparks and the impact sound, never a strike, effect or cell write) and a default-colored burst where a remote player cast. `SpellEffects` plays `spell_impact` where a real bolt lands within `ImpactSoundRangeBlocks` of the camera, at most once an update (the sounds are not positional). The wavs come from `tools/generate-spell-sfx.py`. The item debugger's `CAST` mode (`ITEM_DEBUGGER_PLAYER_ANIM=cast`, with `ITEM_DEBUGGER_SELECT=wooden_staff`) and `visual-examples/animations-cast.json` (`VOXEL_TIME=147`) show it. Remote players' bolts stay invisible.
- **Axe chop** (`SwordSwing.AxeChop`, a fourth swing that is not in the sword's chain): an item in the `axe`
  tag (`Mining.IsAxe`) swings a chop from the side (a flat sweep across the front from the right, the elbow straightening into the blow) instead of a punch whenever `LocalPlayer.PunchSwing` runs, through
  `PlayerModel.StartAxeChop(seconds)` over the same `SwordMotion` shape machinery and `BladeSwingFrame`. Its length
  is the axe's hit gap (`LocalPlayer.AxeChopSeconds`, held within `PlayerConfig.AxeChopMin/MaxSeconds`), and while
  mining `LeftClickAction.UpdateBlockBreaking` starts the swing ahead of each hit by the shape's impact share of that
  gap, so the blade comes down as the hit lands (the first hit of a break lands at once, so that one chops a beat
  late). A hit on tree wood also shakes leaves loose from the canopy above it (`TreeShudder` into
  `LeafParticleSystem.BurstCells`, `ParticleConfig.TreeShudder*`), and a heavy hit nudges the camera (`BreakKick`).
  Chopping a **natural** tree's wood (tree wood whose state is not `WoodState.Detached`; a placed log never) keeps its
  damage **for good**, in singleplayer: `LeftClickAction.RecordChop` stores the damage and the side face of the first
  hit in the world's `ChunkManager.ChopDamage` (`WoodDamage`), a break of that cell seeds its damage from it, so
  releasing, retargeting or switching tools never heals it, and it is saved with the world (`wood-damage.bin`,
  `WoodDamageCodec`, written by `WorldPersistence` when its version moved, read in the `World` constructor before any
  chunk meshes). The cell is cleared when it is mined (`Chunk.RemoveBlockAt`) or felled (`BlockEditor.FellWood`); a
  client and a server keep none (the server counts a break from zero). A full-cube trunk cell shows that damage as its
  hit face cut by a **slit**: the whole width of the face from the first hit, a few pixels high (it never grows
  taller), and deeper with every hit (`WoodDamage.BiteDepthPx`, at least 1 px after any damage and `MaxDepthPx` only
  at the break): `RecordChop` remeshes the chunk when the slit gets deeper (`ImmediateRemesh` takes a chunk position) and
  `TreeWoodEmitter.EmitEaten` draws the cube minus that box (each cell face as the bars around the hole, the box's
  walls in the log's end-grain tile, a cap where the neighbor hides a face, so it is never see-through; a cut that
  reaches the cell's side also opens the neighboring face). It is cosmetic (the hitbox stays the whole cube), it
  replaces the crack overlay and the mining pop cube for that cell (`LeftClickAction.ShowsEatenFace`; the pop box is
  a whole cube and would cover the bite), and a thin branch cell or a hit on a top or bottom face still keeps the
  damage but shows the usual crack overlay.
  The item debugger's `AXE` mode and `visual-examples/animations-axe.json` (`VOXEL_TIME=147`) show it.
- **Slice** (`SliceMotion.SlicePhase`): a whole-body motion in uneven phases of the same 0.7 s: a slow
  wind-up that coils the body (to 0.26), a strike about twice as fast as before centered on the impact
  (0.26..0.49, a steeper `EaseInOutCubic` through the middle) and a long eased recovery, so the contrast
  reads as a snap. The arm sweep is only part of it: the whole body turns about the feet
  (`SliceMotion.SliceBodyYaw`, the legs counter-turning by `SliceLegCounter` so the feet stay planted), the shoulders
  turn further (`SliceMotion.SliceTorsoYaw`), the knees and hips sink into the strike and the torso dips. The hand
  also travels a diagonal on every slice, from `SliceStartRaiseBlocks` above the chest line (so the cut
  starts high, not horizontal) down to just `SliceEndDropBlocks` below it (`SliceMotion.SliceDiagonal`, through the
  same re-solved arm as the chest-height drop), whichever way the swing goes. The slicing **arm is held out in front,
  nearly straight, at chest height** (`SliceMotion.SliceArmShoulder`, `SliceArmElbow`): the carrying pose (elbow bent 75
  degrees, forearm up, which made the hand look held up by the elbow) is eased into it as the stroke ramps in. The
  **blade is carried by the wrist**: its tip points along the forearm (so it follows the arm's sweep and is
  always across the direction the hand travels, never turned up mid-cut), rolled about its length by
  `SliceBladeRoll` (cocked on edge, not laid flat) and cocked up from the forearm by `SliceBladeCant`, and through the main part of the cut (`SliceMotion.SliceMainWeight`: eased in at the end of the wind-up,
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
  The **shoulder socket** (`LimbFrames.ShoulderSocketInward`, 0.09) slides the joint in toward the torso as an arm lifts off
  hanging (`LimbFrames.SocketWeight`), so a raised arm stays seated on the body instead of hinging on a point 0.1 outside it
  with a gap; the punch IK includes it. `SliceImpactProgress` (where the damage lands) is the middle of the strike. Filmed by
  `visual-examples/animations-slice.json`.
- **Blade swing frames** (`Posing/BladeSwingFrame`): everything the body reads of a blade swing in progress (the
  arm's sweep yaw, the shoulder and elbow it is eased to by the stroke, a hand lift re-solved through the arm, the
  body's turn, twist, roll, dip and lean, the shoulder pull, the wrist carry's roll, cant and yaw and the display
  offset) is one struct, `PlayerModel.Blade`, built by `SliceMotion.FrameAt` for a slice and `SwordMotion.FrameAt`
  for a sword swing. `BodyPartMatrices`, `SecondaryMotion`, `ShoulderReach` and `HeldItemRotationOffset` read only
  that, so a new swing is a new shape, never a new branch in the body code, and only one blade swing plays at a
  time (`StartSlice` cuts a sword swing short and `StartSword` a slice).
- **Sword** (`SwordMotion`, one `Shape` record per `SwordSwing`): a long blade is swung from the shoulder, not
  carried across at chest height, so each swing drives the shoulder flexion and the elbow themselves (a wound
  value through the wind-up, carried to a far value through the strike) instead of an extended arm plus a hand
  drop. The **cut** (0.85 s) winds the arm high over the right shoulder, the elbow folded so the blade lies back,
  the body coiled toward it, then sweeps across and down to low on the left with the body turning hard (a
  `BodyYaw` wider than the slice's) and dipping at the impact; the **back cut** (0.75 s) is its mirror in side and
  height, low on the left rising to high on the right, a little quicker; the **chop** (1.1 s) raises the arm
  straight overhead with the blade cocked behind the head, reaching the top three quarters through its wind-up
  and holding there (`WindupReach`, the anticipation), then whips the arm straight down the front while the torso
  folds forward (`Lean`) and the weight drops to full depth by the impact (`HeldDip`, deeper than any cut), rests
  at the bottom (`HoldEnd`) and unwinds slowly. The wrist carries the blade as in the slice: the cuts keep the
  slice's edge roll, cant, leading yaw and display offset (mirrored for the back cut), the chop carries the blade
  straight along the forearm, so it points down and forward at the bottom of the blow. The far poses are placed
  for the first-person view as much as the third (the comment above the shapes has the two rules: a forward fold
  tips the arm down with the torso, a turn toward the sword side swings that shoulder behind the eye), so every
  impact is on screen while a wind-up may rise out of the frame and a follow-through may leave it. Each
  swing's damage lands at its own `Impact`, which the hit-stop holds at. The item debugger's CUT, B-CUT and CHOP
  modes and `visual-examples/animations-sword.json` (`VOXEL_TIME=147`, the three swings from the shoulder camera,
  then the cut and the chop in first person) show them; `PlayerModelTests` holds the shapes (the cut comes down
  from high right to low left, the back cut rises the other way, the chop goes over the head and straight down
  the middle with the blade pointed down at the end, the chop sinks and folds the body further than a cut, no
  swing snaps at either end, every shoulder stays on the torso) and `HitStopTests` the hold.
- **Attachment** (`PlayerModel.GetPartMatrix`, its body in `Posing/BodyPartMatrices`): the neck and both shoulders are children of the **torso
  frame** (hip-origin, turning with the torso's lean, twist and roll), so a limb can never come off the body
  or sink into it; only the legs and the pelvis hang off the root. While a tuned pose is showing (the slice
  sweep, the cocked throw, the punch) the torso's twist is taken back out of that arm's yaw so its hand path is
  the one authored in body space; an idle arm just follows the torso round. The punch IK is solved from where
  the torso actually carries the shoulder (`PunchPose.PunchTargetFromShoulder`), not from its rest place. On top of that a
  reaching arm **pulls its shoulder** a little toward where it reaches (`ShoulderReach.PullTarget`, spring-smoothed so a
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
- **Surface float** (`PlayerModel.Update(..., floating:)`, `FloatBlend`, the `SwimPose.Float*` constants): a body at
  rest in `WaterPhase.Float` is upright and straight (`SecondaryMotion.ComputeMods` drops the swim tilt and roll by the
  float's share), its arms scull slowly beside the hips (upper arms a little out and forward, forearms
  bent forward, the hands sweeping out and in together) and its legs hang with a faint flutter. It eases
  in over the swim pose through `MathUtils.Damp` and back out during a stroke (a floater swimming off
  strokes in the swim pose) or once the body stops floating. `LocalPlayer.UpdatePlayerModel` feeds the swim
  pose only from `Float` up (a wader walks with no water pose) and `floating` in `Float`. The item
  debugger's `SURFACE` mode (`ITEM_DEBUGGER_PLAYER_ANIM=surface`) previews it, `SurfaceFloatPoseTests`
  locks it down and `visual-examples/water-depth.json` (`VOXEL_TIME=147`) films a poured puddle (wade),
  the deep sea's float, a stroke hop and a dive (swim) from the shoulder camera.
- **Player model** (`PlayerModel.cs`): the existing `Pose` is post-processed by `SwimPose.ApplySwim` and
  `SecondaryMotion.ApplyMods` (`ComputeMods`): a landing dip spring (knees, hips, torso, root drop), crouch
  (`Update(..., sneaking:)`), a swim stroke (`swimming:`), the hit flinch (`Hurt(fromFront)`), torso
  twist and sway in the gait, the slice twist and weight shift, the throw lean, the off-hand counter
  on a punch, a heavier swing for a slower tool (`Punch(weight)`), the first-person arm lag behind
  the look (`SetLook`), the equip dip (`NotifyEquip`), the body-yaw lag with a head that leads
  (`BodyYawLags`, third person and remote players only) and the death fall (`DeathFall`). The limbs
  are still not parented to the torso, so torso-only motion stays small.
- **Camera** (`Camera.cs`, its view juice in `HeadMotion.cs`): the view effects fold into `HeadMotion`'s `_bobHeight`, `_roll` and `CurrentFov`, which
  the view cache is already keyed on, so none needs a key of its own: a landing dip, a directional
  hit kick (`AddHitKick`), an FOV pinch on a swing (`PunchFov`), a crouch eye drop and the death view
  (`DeathProgress`). The **sprint FOV** widening (`SprintFov`, knobs `SprintFov*` in
  `PlayerConfig`) is its own eased term *added* to the base FOV and the pinch: the sprint key
  with movement on foot or flying, never sneaking or swimming, scaled by the speed and clamped at the
  on-foot sprint, so the 10x flight sprint widens no further. The **strafe tilt** (`StrafeTilt`,
  `StrafeTilt*` knobs) is likewise an eased term *added* to `_roll`: positive roll tips the view's up
  axis toward the camera's right, so a strafe right leans the head right, into the move; off while
  flying or dead. **Sneak** is Left Ctrl on foot (`Camera.IsSneaking`): slower, never sprinting, a
  lowered *view and model only* — the body and its collision keep their size, so the server's
  movement rules (which derive the feet from the eye) are unaffected.
  The **throw charge zoom** (`ThrowZoom`, `ThrowZoom*` knobs) is a third FOV term *added* beside the
  sprint widening and the pinch: `LocalPlayer.UpdatePlayerModel` copies the arm's `PlayerModel.ThrowCharge`
  into `Camera.ThrowCharge` (like `HoldPunchFov`), and the next head-motion update springs the view
  toward `ThrowZoom.TargetDegrees` (narrower with the eased charge, 0 with none) through an
  under-damped `Spring`, so a release or cancel springs back with a small wide overshoot, clamped at
  half the zoom. Singleplayer and a client alike (it reads only the local arm);
  `visual-examples/throw-zoom.json` films it, `ThrowZoomTests` locks it down.
- **Hit-stop** (`SwingHitStop`, `HitStop*` knobs): a punch or slice that connects
  (`LeftClickAction.UpdatePunch`/`UpdateSlice`; on a client the *predicted* connect, a mob in the cone or
  hemisphere with line of sight, since the server's verdict comes too late) calls
  `PlayerModel.HitStop`, which holds that swing at its impact pose (full extension, or
  `SliceImpactProgress`) and then replays the rest faster, so the swing still ends when it would have:
  the slice combo, the jab pace and `AttackPacing` never see it, and the world, mobs and movement
  never stop. `Camera.HoldPunchFov` holds the pinch while `PlayerModel.IsHitStopped`. The item
  debugger never requests one. `visual-examples/hit-stop.json` films it.
- **Death** plays for `RespawnPrompt.DelaySeconds` (the view tips over, the body falls), then the
  **death screen** (`MenuRenderer.DrawDeathScreen`, `DeathScreenLayout`, drawn by
  `UiPasses.RenderDeathScreen`) covers the world with the death line and a RESPAWN button, in
  singleplayer and on a client alike. While it is up `GameplayInput` stands every game input down
  (keys, mining, use, the wheel, ESC, the chat and the [E] panel; `SyncDeathScreen` frees the cursor
  and the look) and a click on RESPAWN or Enter asks for the respawn (`ClientSession.RequestRespawn`).
  There are no death drops and no bed: the respawn point stays where the world first settled.
- **Mob death poof.** A mob bursts into skin-colored chips when its death animation ends
  (`BlockDebrisSystem` + `DebrisLook.Color`, `LandingDust.SkinColor`), the same pool the slime
  splat uses.
- **Placement pop.** A placed block pops in a little too big and settles with one small overshoot
  (`SelectionPasses.RenderBlockPop`, `PopBoxEmitter`).
- **Mob hit sparks.** A punch, slice or throw that connects throws pale chips from the wound point
  on top of the floating damage number.
- **Drop landing bounce.** A drop that lands hops back up once or twice, damped by `BounceDamping`,
  so a pickup reads as physical.
- **Tool break shatter.** A tool whose durability runs out shatters into chips of its own sprite
  colors instead of just vanishing.
- **Pickup sparkle.** A few bright `FireParticleKind.Spark` motes pop at the chest where a drop is
  collected.
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
  by the slime's body scale `SlimeSize.Scale(size)`): `DebrisLook.Color` chips with an air id, one gray cube
  (`BlockCubeCache.GetColorChip`, `JuiceConfig.ColorChipTile`) painted through `uBiomeTint`
  with `BiomeTint.VertexTint` of the mean opaque skin color (`LandingDust.SkinColor`). Sprinting
  on foot over a `footstepDust` block (grounded, not sneaking, not in water, really moving) puffs once
  per foot strike of the gait: `PlayerModel.WalkPhase` crossing π/2 + kπ (`FootstepDust.FootfallsBetween`).
- **World-event effects** (cosmetic: singleplayer and a client, never a headless server). A TNT blast
  shakes the local camera (`AmbientEffects.ShakeForBlast`): `ShakeFalloff.Trauma` is `BlastShakeStrength` inside
  `TntManager.BlastRadius`, falls off as radius / distance and eases to zero at `BlastShakeRangeBlocks`,
  never growing with distance. Singleplayer reads the centers `TntManager.Blasts` reports for the frame;
  a client, which never ticks TNT, takes a primed-TNT mirror leaving the server's snapshot with its
  locally burnt fuse within `BlastMirrorFuseSlackSeconds` as the blast (the server drops a cube only when
  it detonates). Tests: `BlastEffectTests`. A sand or gravel cube that settles throws a skirt of its
  own chips (`GroundDust.LandBlock` -> `BlockDebrisSystem.Skirt`, which starts each chip just outside
  the cell's sides so none spawns inside the solid block), sized by `LandingDust.FallingBlockCount` /
  `FallingBlockSpeed` from the fall (`FallingBlock.FallBlocks`, from the highest point the cube
  reached; none under `FallingBlockDustMinFallBlocks`, none under water): singleplayer reads
  `FallingBlockManager.Landings` (each cube written back as a block that frame), and a client puffs
  where a `BlockChanges` entry turns a cell into a `falls` block beside a falling-cube mirror of that
  block in the newest or previous snapshot, as if it fell `FallingBlockDustClientFallBlocks`
  (`FallingBlockDustTests`). Lava hardening gives off steam: `FluidManager.HardenedCells` lists every
  cell contact hardening turned solid in the frame (the touching rule and a flow arriving hardened),
  and `AmbientEffects.PuffSteam` puffs `SteamPuffSmoke` pale `FireParticleKind.Steam` particles (drawn blended
  like smoke, lighter, quicker and briefer) off the top of each through `FireParticleSystem.Puff` on
  the campfire instance, at most `SteamPuffMaxCellsPerFrame` cells within `FireParticleRadius` a frame,
  since a waking cave pocket hardens dozens a frame far below; a client puffs where a `BlockChanges`
  entry turns a fluid into its own `hardensTo` block (a flow crusting against lava is missed there)
  (`SteamPuffTests`). A felled tree hitting the ground thuds: `TopplingTree.TryTakeLanding` is true
  exactly once, on the update its swing reaches flat, and `BlockEditor.ThudTree` then takes the blocks'
  rest cells (`TopplingTree.GatherLanded`, the cells `TakeDrops` drops at), bursts
  `LeafParticleBurstThud` leaves off the landed canopy, kicks the ground's dust up under at most
  `TreeThudDustPoints` points of the landed trunk (`GroundDust.TreeThud`) and shakes the camera by its
  distance to the trunk (`ShakeFalloff`, `TreeThudShakeStrength` scaled by the wood that fell over
  `TreeThudFullWoodCells`). Singleplayer only: a server skips it, and a client sees no toppling tree
  at all (`TreeThudTests`). Anything that enters or leaves water splashes: every body keeps a
  `WaterSplashTracker` (the player's in `AmbientEffects`, a field on `Mob`, `ItemDrop`, `ThrownProjectile`,
  `FallingBlock` and `PrimedTnt`), which `AmbientEffects.UpdateSplashes` steps each frame through
  `WaterSplash.Track` from the body's lowest point (the player by `Camera.IsInWater`, the rest by that
  point being under its cell's fluid surface). The tracker measures the vertical speed from the body's
  own height between frames (so a client mirror's 10 Hz jumps read as the speed over the snapshot),
  seeds on its first call, ignores a teleport-sized step and treats a body still for
  `WaterSplashStaleSeconds` as still; on a crossing it reports the entry's downward or the exit's upward
  speed, and `WaterSplash.Count` / `Speed` (none under `WaterSplashMinSpeed`, growing along
  `MathUtils.ImpactStrength` to `WaterSplashFullSpeed`, the count scaled by the body: a mob by its width,
  drops and projectiles `WaterSplashSmallScale`, cubes `WaterSplashCubeScale`) size a ring of pale
  `WaterSplashColor` `DebrisLook.Color` droplets (`BlockDebrisSystem.Splash`) thrown up from the water
  top of the column crossed (`ChunkManager.GetFluidColumnTopGlobal`), never off lava. Every crossing (even one too gentle for droplets) also starts a **ripple ring** (`RippleSystem`, owned by `AmbientEffects.Ripples`; rule `WaterRipple`, GL-free: the leading edge's radius eases out and the opacity fades over a life that grows with `WaterSplash.Strength`; `RippleRenderer` draws flat quads on the water top through `ripple-fragment.glsl`, which shades a short train of crests and troughs behind that edge, dying away behind it and out with distance, the edge wobbled per ripple by its seed and the bubble vertex shader, from `BillboardPasses.RenderRipples` after the bubbles, skipped with the eye under water; `ParticleConfig.Ripple*`; `WaterRippleTests`, `visual-examples/water-ripples.json` at `VOXEL_TIME=147`). A client splashes
  its own player, the mob mirrors and the primed-TNT mirrors (all persist across snapshots); its drops and
  falling cubes are rebuilt every snapshot, so they never splash there (`WaterSplashTests`). Torch
  heads burn: the mesher records the chunk-local point of every block with a JSON `flame` (turned to the
  cell's facing) in `ChunkMeshData.FlamePoints` beside `HotFluidVents`, kept on `Chunk.FlamePoints` by
  `UpdateMesh` and dropped by `Chunk.MarkMeshedHeadless`, since a server never draws one; each frame
  `AmbientEffects.UpdateTorchFlames` gathers the points within `TorchFlameRadius` of the eye from the
  `Renderable` chunks (nearest first, at most `TorchFlameMaxEmitters`) into a third
  `FireParticleSystem` (`TorchEmberPerSecond`, `TorchSmokePerSecond`, no spawn offset, the spawn scatter
  `TorchFlameJitter` and the particle scale `TorchFlameScale`, both constructor parameters, the scale
  shrinking a `FireParticle`'s size, life, speed and sway), drawn after the lava batch. No point lights:
  the torch's own block light lights it (`TorchFlameTests`; `visual-examples/torches.json` at
  `VOXEL_TIME=700` ends on close shots of a floor and a wall torch).
  `visual-examples/world-events.json` (`VOXEL_TIME=147`) films
  these effects; its blasts use the scenario op `prime` (`{ "op": "prime", "pos": [x, y, z] }`,
  `WorldItemUse.PrimeTnt`), a firestarter strike on the TNT block in that cell that never misses, since
  `give` cannot equip a tool instance and the strike's chance roll would make the scenario flaky.
- **Remote players** (protocol 7): the `PlayerState` flags carry cosmetic `Sneaking`, `Swimming`,
  `Sprinting` and `Airborne` bits (rebroadcast untrusted, never acted on) and every `PlayerList`
  entry ends with the player's last `NetPlayerAction` and a counter that moves per swing, so
  `RemotePlayer` poses the model and replays punches, slices, throws and staff casts (`NetPlayerAction.Cast`,
  added without a version bump; `TakeCast` hands the client's `SpellFeedback` one default-colored burst per cast).
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
  `LocalPlayer.NotifyPlayerHurt` gives `AddHitKick` would not) and, on a client, by `Camera.AddDamageDirection` from
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
- **Slice trail** (`Particles/SliceTrail*`): a blade swing (the obsidian blade's slice and its return,
  the iron sword's cut, back cut and chop) leaves a glowing, curved crescent behind the blade tip that
  fades out within `SliceTrailLifeSeconds`, in first person and in the shoulder view. The tip and an
  inner point are read along the held sprite's (1, 1, 0) diagonal (`SliceTrailTipReach`,
  `SliceTrailInnerReach`) through `HeldItemPose.SpriteMatrix`, the very matrix the held item is drawn
  with. `SliceTrail` stores its samples in the swinging player's **owner frame** (the eye plus
  `PlayerModel.DrawnBodyYaw`, so a lagging body keeps the arc on the blade; pitch is not in it) and turns
  them back at build time, so the arc rides with a walking or turning player and camera motion alone adds
  nothing. A sample is committed at most once every `SliceTrailMinSampleSeconds` (the life divided over
  the ring, so the whole life fits `SliceTrailMaxSamples` at any frame rate, since VSync is off) after
  `SliceTrailMinStepBlocks` of travel, while a live head moves every update and keeps the newest end on
  the drawn tip. Ages run on `World.Now`: a hit-stop (`PlayerModel.IsHitStopped`) commits nothing while
  the trail keeps fading, the tip speed counts only the time the tip was really moving, and a paused
  world keeps fading at render time. A restarted swing progress starts a new segment, so a slice and its
  return are never bridged. `SliceTrailRibbon` builds the ribbon (Catmull-Rom, `SliceTrailSubdivisions`
  per gap): the tail tapers by position along the segment (`SliceTrailTailTaper`), the head by tip
  speed (`SliceTrailMinSpeed..FullSpeed`), the width shrinks and the alpha falls with age, and UV.x is
  the stable arc length the streaks are keyed on, so the wisps never crawl as the tail expires.
  `slice-trail-fragment.glsl` (twin `SliceTrailGlow`) draws a soft colored body brightening toward the
  tip edge, a white core band near it and hashed streak lanes (the `SliceTrailCore*`, `GlowPower` and
  `Streak*` knobs in `ParticleConfig`); the color is the item's `trail.color` (else
  `SliceTrailDefaultColor`), decoded from sRGB into the linear HDR target. `SliceTrailRenderer` draws
  it additively (`SrcAlpha, One`, depth test on, no depth write, no cull) in `WorldRenderer`'s late
  particle block after the clouds (`BillboardPasses.RenderSliceTrails`), never in the mirror or the
  shadow pass. `SliceTrailSystem` (World's component, fed right after the remote players update, never
  in server mode) runs the local trail and one per remote player: a remote's held item is not on the
  wire, so while it swings the client assumes, and `PlayerPasses.RenderRemotePlayers` draws, the first
  item with that attack pattern (`SliceTrailSystem.AssumedBlade` over `ItemRegistry.FindByAttack`). The
  item debugger's trail is fed per fixed step, so a scrub rebuilds it (see "Build and run").
  `visual-examples/slice-trail.json` (`VOXEL_TIME=147`, using the `player` op's `slicereturn` action)
  films every swing; `SliceTrailTests`, `SliceTrailRibbonTests`, `SliceTrailGlowTests`,
  `SliceTrailSystemTests`, `SliceTrailContentTests` and `ServerWorldTests.AServerModeWorldFeedsNoSliceTrail`
  lock it down.
- **Floating damage numbers**: a landed hit floats its damage over the mob (`World.DamageNumbers`, a
  fixed `DamageNumberPool` of `DamageNumberCapacity` that replaces its oldest number when full),
  drawn by `DamageNumberRenderer` right after the healthbars with their billboard shader and the HUD's
  font atlas. Singleplayer sees every accepted `Mob.TakeDamage` through `Mob.HitsTaken` /
  `LastHitDamage` (compared around `MobStrikes.StrikeMob`, which punch, slice and throws all use); a client,
  which never damages a mob, floats the health drop between two `EntityList` snapshots, plus the health
  left on a mob that leaves the snapshot within `DamageNumberDeathRangeBlocks` of the camera (the
  server stops listing a mob the moment it dies, so that is the killing blow). A headless server keeps
  none. `visual-examples/damage-numbers.json` films them.
- **Verification**: `visual-examples/animations.json` (run with `VOXEL_TIME=147`) films the player's
  poses from the shoulder camera and the mobs' spawn, hit and death; its `damage`, `view` and
  `player` ops (`hurt`, `punch`, `slice`, `slicereturn`, `cut`, `backcut`, `chop`, `axechop`, `equip`, `charge`, `release`, `throw`, `draw`, `loose`, `cast`, `die`, `revive`) exist for it. For
  iterating on a single animation prefer the **item debugger** (see "Build and run"): its ITEMS tab has a
  button for every player animation and its ENTITIES tab previews the goblin and slime life-cycles,
  including the slime's code-driven squash, with `ITEM_DEBUGGER_PLAYER_ANIM` / `ITEM_DEBUGGER_ANIM` and
  `ITEM_DEBUGGER_SHOT_AT` for scripted captures. A new player animation gets a mode in
  `PlayerPreviewState`, a new mob animation a mode in `EntityPreviewState`.

## Code organization (standing rules)

These are standing rules for every change to this codebase, not a one-off task list. When you
touch a file that violates one of them, fixing it is in scope.

- **No god classes.** One class, one coherent job. Past roughly 700 lines a class is a smell:
  move the stray responsibilities out into a well-named class (composition, not `partial`).
  No source file goes past ~1000 lines without a stated reason. `World.cs`,
  `Program.cs`
  are the current offenders — keep shrinking them, never
  grow them. `World.cs` (about 1,000 lines, down from 4,700) has a stated reason: it is the
  composition root that builds and owns the per-job components (`BlockEditor`, `BlockPlacer`,
  `LeftClickAction`, `RightClickAction`, `ThrowAction`, `BlockInteractions`, `WorldItemUse`,
  `MobStrikes`, `AmbientEffects`, `LocalPlayer`, `ClientMirror`, `WorldPersistence`,
  `DropPickup`, `MobDirector`, `WorldRenderer`) and keeps the order-sensitive `Update`/`UpdateClient` frame
  orchestration. `RenderManager.cs` (1,032 lines, down from 4,588) has a stated reason
  too: it is the GL resource owner whose `LoadResources`/`Dispose` fix the creation and
  disposal order, plus the world programs' one-time and per-frame uniforms
  (`InitWorldProgram`, `ApplyFrameUniforms`, `SetDrawUniforms`); its passes live in the
  components it builds and exposes as internal properties (`GpuPassTimer`, `TextureUnits`,
  `RenderTargets`, `FrameLighting`, `SkyPasses`, `ScreenPasses`, `BillboardPasses`,
  `SelectionPasses`, `StationPasses`, `ObjectPasses`, `PlayerPasses`, `MobPasses`,
  `ShadowPass`, `TerrainPasses`, `ReflectionPass`, `UiPasses`). `UIRenderer.cs` (801
  lines, down from 2,504) has a stated reason too: it owns the UI's GL resources and their
  creation and disposal order (the ui program, the count font, the text batch buffers, the
  item-icon textures and frame meshes) and the shared draw primitives every page and the item
  debugger use; its pages live in the components it builds and exposes as internal properties
  (`HudRenderer`, `TopStripRenderer`, `InventoryPanelRenderer`, `CraftCardRenderer`,
  `MenuRenderer`, `LoadingScreenRenderer`, `ChatRenderer`). `Chunk.cs` (675 lines, down from 1,944) is under the smell line now: it is the one owner of a chunk's storage (ids, the volatile state byte and light snapshot, the raw writers and edit API, the save/network snapshots, its GPU slices), with its worldgen fill in `WorldGen/ChunkTerrainFill`, its restore from a snapshot in `Chunks/ChunkRestore`, its mesh build in `Chunks/Meshing/ChunkMeshBuilder` and `ChunkLighting`, and the face and AO tables in `ChunkFaceTables`. `Camera.cs` (684 lines, down from 1,175) is under the smell line now: it is the player's public facade, the one type `PlayerPhysics`, `World` and its components, the renderer passes, the server and the tests compile against, and it owns the pose, the mouse look, the eye fluid state, the mode setters and the `IMobTarget` side; its view juice, F5 rig, mode toggles, view-projection cache, spawn overrides and falling-cube shove live in `HeadMotion`, `ShoulderRig`, `ModeToggles`, `ViewProjectionCache`, `SpawnOverrides` and `BodyCollision`, and about 290 of its lines are the XML docs of the members it forwards to them. `Program.cs` (628 lines, down from 2,297) has a
  stated reason too: it is the process entry point and the app's composition root: `Main` registers the
  window callbacks, `OnLoad` builds the platform managers and the components (`DevDiagnostics`,
  `ClientSession`, `InventoryPanelInput`, `ChatInput`, `MenuInput`, `GameplayInput`, `InputEvents`), it
  owns the app-screen flow and the world lifecycle (the deferred PLAY/CREATE/SAVE & QUIT/CONNECT work,
  opening and saving a world, the save on close), which the components read through `Program.ActiveWorld`,
  `Program.CurrentWorld` and `Program.Screen`, and it keeps the frame-order-sensitive
  `OnUpdate`/`OnRender` orchestration.
- **No god directories.** Keep any one folder under ~30 files and group by domain. The `src/`
  root holds the app entry and core types only; a new file goes into the folder of its area
  (a new feature area gets a new folder), never into a catch-all. `Networking/` is split into
  its shared protocol (the folder root), `Client/` and `ServerSide/` (not `Server/`, which would read
  as the dedicated server's `VoxelEngine.Server`); every src folder stays near ~30 files (`WorldGen/` is at
  31, its worldgen core types; its generators already sit in `Features/`, `Hell/` and `Trees/`), so the god-class
  split-outs land in their area's folder without crowding it.
- **Namespaces mirror the folder tree.** Root namespace is `VoxelEngine`; a file in
  `OpenGLTest/src/WorldGen/Trees/` declares `namespace VoxelEngine.WorldGen.Trees;`, and only
  the six files directly in `src/` (Program, World, WorldPhase, WorldLoadGate, WorldSeed,
  BlockType) are in `VoxelEngine` itself. The tests mirror their own tree the same way:
  `OpenGLTest.Tests/Chunks/` is `VoxelEngine.Tests.Chunks` and the shared fixtures in
  `Support/` are `VoxelEngine.Tests.Support` (no test sits at the project root); the server and
  the item debugger are flat (`VoxelEngine.Server`, `VoxelEngine.ItemDebugger`). Moving a file
  means changing its namespace line and letting the compiler name the usings to add. Never
  park a new file in the root namespace out of convenience; never add an unrelated type to
  whichever namespace a file happened to have. Each project keeps a short, deliberate
  `GlobalUsings.cs` at its root beside the csproj (`OpenGLTest/GlobalUsings.cs`,
  `OpenGLTest.Tests/GlobalUsings.cs`, `OpenGLTest.Server/GlobalUsings.cs`,
  `OpenGLTest.ItemDebugger/GlobalUsings.cs`) listing only the namespaces most of that
  project's files need: `VoxelEngine.Core` everywhere (the `Vector2Utils`/`Vector3Utils`
  extension methods, `MathUtils`, `Log`) and `VoxelEngine.Config` everywhere (the per-domain tuning
  constants, read by the game, the server, the item debugger and the tests alike), plus `VoxelEngine.Chunks`,
  `VoxelEngine.Content` and `VoxelEngine.Tests.Support` in the test project. Everything else is
  an explicit `using` in the files that use it, including a using that only an XML `cref`
  needs (a doc build, `-p:GenerateDocumentationFile=true`, counts it as used; the plain build
  checks neither crefs nor unused usings). The game and the item debugger also carry
  `global using Shader = VoxelEngine.Graphics.Shader;`, because `Silk.NET.OpenGL` has a
  `Shader` struct too; `Plane` likewise clashes with `System.Numerics.Plane`, so a cref to the
  engine's is written `VoxelEngine.Data.Plane`. The vendored `Core/FastNoiseLite.cs` carries
  `namespace VoxelEngine.Core;` as its only local edit.
- **American English** in identifiers, comments and docs — `color` (not colour), `behavior`,
  `initializer`, `center`, `gray`, `normalize`, `canceled`, `traveled`, `catalog`, `license`,
  `meter`. Applies to renames of existing symbols too.
- **No dead code.** Delete unused private fields, methods, parameters, locals and `using`
  directives on sight; git keeps the history. Do not keep anything "for later". Build warnings
  must stay at zero. The plain build does not report dead code; measure it with the doc+style
  build in the code-cleanup skill.
- **No duplication.** A rule that exists twice becomes one shared (and, where it is a rule,
  tested) helper. The GL-free-twin pattern (a `Foo` next to a `FooRenderer`) is the same idea
  for shader/renderer pairs: one definition, two consumers.
- **Early returns over nesting.** Guard clauses at the top of a method, `continue`/`break` over
  a wrapped loop body. A loop body that still nests past ~3 levels gets an extracted method.
- **Plans live in `plans/`.** The roadmap, perf, biome, cleanup and tree plans share one layout
  (Status / In progress / Open / Reference, `[ ]` checkboxes, finished entries deleted); the guide is
  `plans/README.md` and the procedure is the `plan-docs` skill.
- **Keep this file honest.** When a file moves or splits, update the Layout section and any
  path references in this document; stale docs send the next agent to a file that no longer
  exists.

## Workflows

Three tiers, cheapest that fits (details in `.claude/workflows/README.md`): **direct** for small or well-scoped changes; `plan-and-implement-lite` for mid-sized work (roughly 2-6 files, design mostly clear: 4 Sonnet scouts, one Opus planner, 1-2 Opus implementers run sequentially, the last one builds, tests and self-reviews; args `{task, extraContext?, effort?: "medium"|"high", dryRun?}`); `plan-and-implement` for large, cross-cutting or design-unclear work (adds plan red-team, a verifier and an acceptance review, ~20 min / ~1M tokens). **Codebase scouting is always Sonnet** in every tier, and any agent you spawn by hand must pass `model` explicitly.

## Conventions

- C# with file-scoped namespaces (one per folder, see "Code organization"), one type per file, `_camelCase` private fields, PascalCase public members. Primary constructors are used where natural (`ChunkGenerationWorker(ChunkManager chunkManager, StreamingFocus focus)`).
- XML doc comments (`/// <summary>`) on classes and non-obvious public methods; brief `//` comments explaining *why* for tricky logic. Match the existing density — the code is moderately commented.
- **New blocks go in JSON, not C#.** When a block has to behave differently, prefer a new data flag on `BlockDefinition`/`BlockType` that every call site reads over another per-id branch in gameplay code — the point of the content registry is that `World`, `TntManager` and the mesher ask "what does this block do?" instead of "which block is this?".
- `FastNoiseLite.cs` is vendored third-party code — do not reformat or refactor it.
- xUnit tests live in `OpenGLTest.Tests/` — run `dotnet test OpenGLTest.Tests/VoxelEngine.Tests.csproj -c Release`. They cover headless logic and simulated gameplay without a window (fixture: `Support/TestContent.cs`); add a test with every feature instead of a throwaway script — but only when there is a *rule* to lock down. A test must assert behavior (a gate, a formula, a state transition, an invariant that survives content tuning), never a constant, a layout number, a content literal or a starting-stock/seed count (`Assert.Equal(1, inventory holds one converter)` is exactly as pointless as pinning a block's hardness) — see the `functional-tests` skill's ban list for the full rule and rewrite table. Verify feel-in-game by running the game and using the `X` debug overlay (FPS, position, grounded state). Watch the console for chunk generation errors — worker threads catch and log exceptions rather than crashing.
- Branch naming follows feature work off `master` (current work is on `worldgen`). Commit messages are short imperative sentences ("Add block placement", "Fix performance").
