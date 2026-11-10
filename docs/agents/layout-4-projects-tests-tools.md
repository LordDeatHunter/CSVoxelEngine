# Layout, part 4: other projects, tests, tools

File tree (part 4 of 4): the item debugger, the dedicated server, the wiki exporter, the test project, `USER_TODO.md` and `tools/`.

```
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
  ServerChat.cs                       # chat relay and the server's chat commands (over ChatCommands), /locate
                                      #   answered from the player's eye with its teleport link
  ServerProgram.cs                    # content load, world/port prompts, headless World + tick loop
  ServerState.cs                      # what the components share: world, client and player tables,
                                      #   pending connections, clock, join spawn, creative rights
  ServerTransport.cs                  # TCP listener, accept thread, per-connection reader threads
  StreamRequests.cs                   # chunk, column and far-LOD region requests and the LOD queue
  WaystoneSessions.cs                 # waystones for each player: discovery on a replayed Use (WaystoneOpened),
                                      #   the rows kept current, the rename, the trip judged, held and landed
OpenGLTest.WikiExport/                # headless exporter for the wiki (wiki/): grows every tree species step by step
                                      #   over flat ground (the item debugger's TREES setup) and writes per-step cell
                                      #   diffs; CI runs it before the wiki build (`dotnet run --project
                                      #   OpenGLTest.WikiExport -- <outDir>`)
  Program.cs                          # content load, then TreeExporter.WriteAll to the output directory
  TreeExporter.cs                     # GrowSteps/CellsAt (the engine calls) and the per-species JSON writer
  TreeStepDiff.cs                     # the cells to write and remove between two steps, and Apply (the replay)
OpenGLTest.Tests/                     # xUnit tests, headless (no window); a test sits beside the area
                                      #   of the code it tests (by the type under test), a probe beside
                                      #   the area it probes, a helper of several areas in Support/;
                                      #   namespaces mirror the test folders (VoxelEngine.Tests.<folder>)
  VoxelEngine.Tests.csproj            # the test project (references the game, the server and the item debugger)
  GlobalUsings.cs                     # VoxelEngine.Core, VoxelEngine.Chunks, VoxelEngine.Content and VoxelEngine.Tests.Support
  Support/                     # cross-area fixtures: TestContent (the headless content load, and
                               #   SwapIn/RestoreReal for the registry-swap tests), SyntheticContentRoot (a
                               #   temp asset root with N extra stone copies, so ids pass 255),
                               #   MeshTestWorld, MobTestWorld, FlatWorldSource, StampedTree,
                               #   WideTreeSpecies, TreeWorldgenFixture, IcebergTestWorld (a hand-built sea with a
                               #   pack-ice strip), StubTarget, BoxSolids, SkyZoneFinder (sky zones under the seed),
                               #   SyntheticGear (gear items with made-up abilities and modifiers), TowerSiteFixture
                               #   (synthetic tower sites of any radius, floor count and turn), CaveTestWorld
                               #   (a carved, evenly lit cave for the cave spawn searches), StorageCells (cells with
                               #   N distinct ids and the smallest-storage rule, for the save and net codec tests)
                               #   and the four [CollectionDefinition]s (ContentRegistrySwapCollection
                               #   runs the tests that swap the registry)
  Audio/                       # the sfx library and the ambience bed
  Chunks/                      # chunk storage, keys and map, the chunk manager and its mesh scheduling, the
                               #   column cache, fluid surfaces, lava vents, EntityLight; the replay probes
    Meshing/                   # greedy and per-voxel meshing, smooth light, the light volume, plant sway,
                               #   cell batches; the mesh-cost probe
    Sources/                   # the generated and imported world sources
    Streaming/                 # the generation workers, the scan frontier, the streaming foci
  Content/                     # the registry and its baked tables: blocks, models, shapes (panes,
                               #   tree wood, multiblocks), loot, recipes, kiln and molds, spell parts and
                               #   recipes, status effects and the status block flags, the tower lock blocks (flat);
                               #   Blocks/ holds the per-id tables and WideIdRegistryTests (a registry past
                               #   the byte range, meshed, saved and streamed)
  Core/                        # the Core utilities and the src-root types (World lifecycle, WorldSeed, WorldLoadGate)
  Effects/                     # mirrors src/Effects: one folder per sub-domain of shader twins
    Climate/                   # the biome tint field and its chunk, LOD, pop and texture consumers
    Lighting/                  # the campfire flicker and the block-light tint code
    Post/                      # biome grade, contact AO, eye adaptation, godrays, sun dazzle, lens flare, underwater wobble
    Sky/                       # aurora, silver lining, moon, moonlight, sky palette, stars, glare, wind, the cave
                               #   biome atmosphere (CaveClimateTests)
    Surfaces/                  # forcefield, glass, grass, ice, lava, leaf and ore twins, the snow sparkle shader term
    Water/                     # deep-water fog, the waterline fresnel, sun glitter, the shore foam shader term
  Entities/                    # the Mob base and the shared mob rules (caps, targets, notice, interest, fall,
                               #   lava, ledge, loot, body separation), clips and the animator, drops, projectiles,
                               #   damage numbers
    Goblins/                   # goblin AI, aim and stones, flee, dodge, pack alert,
                               #   torches, cave spawns, the content contract (mirrors src/Entities/Goblins)
    HellMobs/                  # hell mob contracts, layer difficulty, brute blow, caster cast and bolt burn
    SkyMobs/                   # sky mob contracts, zone difficulty, the flyer's hover and dive, the golem's
                               #   shockwave and ledge caution
    CaveMobs/                  # cave mob contracts, the depth tier and its scaling, the shard band, each attack's event
    Spiders/                   # the leg contract and solver, climbing a cliff, rounding a ledge, the knock off a wall,
                               #   footholds, the wire stance, the cave spawn share, the venom bite
    TowerMobs/                 # the skeleton's contract, tier scaling, bow band and line of sight, melee, undead, the ambient rattle's timer and range gate; the
                               #   Skeleton King's contract, leash, shockwave and volley fan, its brain's phases and choices
    Sandworms/                 # the sandworm's chain, brain and pose, its melee through the real punch path
    Icebergs/                  # iceberg shape, motion, wobble, spawn band, contact, the berg (mirrors src/Entities/Icebergs)
    Slimes/                    # slime sizes, hops, knockback, split and merge (mirrors src/Entities/Slimes)
  Graphics/                    # shader defines and the chunk-draw shader guard, cutout mips, tile materials
  Interaction/                 # raycast and targeting, mining, combat and the slice combo,
                               #   placement, support, doors and stairs, multiblocks, the selection
                               #   outline, chat commands and the /locate reply (ChatLocateTests); its own helper
                               #   SupportTestWorld
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
  Networking/                  # protocol and codecs (the chat line's link: ChatMessageCodecTests), end-to-end chunk and
                               #   imported-world streaming
    Client/                    # client streaming, the mesh gate, predictions and intent flow, the entity
                               #   mirror, remote players, worldgen isolation
    ServerSide/                # the server player and its status (the eye's cave biome: ServerPlayerCaveBiomeTests),
                               #   movement and intent rules, operators, the loopback GameServer and the server-mode World
  Particles/                   # every particle system and the dust, splash and pop emitters
    Collapse/                  # a defeated tower's collapse show: its rule, thrown blocks burst and never write a cell
                               #   (TowerCollapseEffectsTests)
    Heave/                     # the king's slam heave: only exposed ground in the radius, the ripple, the settle, no write
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
    Camps/                     # goblin camps: the fire first, torch-bearing goblins round it, one a dark night
    Caves/                     # the cave spawn filter and rules, the cave mob manager (spawns by biome, tier, caps, attacks)
    Chests/                    # chests, tower chests, the underground ruin chests' cave biome tables (CaveChestTests) and
                               #   the lid: open while viewed, the wobble, the model's facing, no chunk quads (ChestLidTests)
    Tower/                     # the skeleton manager: forced spawn and the hard cap, tier, no natural spawn, rake, arrows,
                               #   despawn reports; the floor locks (TowerRulesTests, SkeletonPileManagerTests,
                               #   TowerBlockWriterTests, TowerManagerTests, TowerManagerSaveTests: stations.json); the
                               #   altar and the king (TowerAltarTests, TowerAltarWorldTests, SkeletonKingManagerTests);
                               #   the collapse (TowerCollapseTests) and its cost probe (TowerCollapseCostProbe)
    Waystones/                 # waystone names, the registry's discovery, rename, lost and save rules; the teleport rule,
                               #   the arrival search, the realm tag and a trip in a headless world
  Status/                      # the status effect set, the Apply API, the speed factors and the block rule and writer
  UI/                          # the canvas, settings, scroll lists, text wrap, the rarity style table and the
                               #   rarity chrome twin (RarityStyleTests, RarityChromeTests)
    Hud/                       # chat (its log layout and link hit-test: ChatLogLayoutTests), clock, compass, hit marker,
                               #   hotbar feedback, toasts, tooltips, the look label, screenshot paths
    InventoryPanel/            # the inventory and its save shape, the [E] panel layout, crafting cards, the creative catalog, search
                               #   and its scope, the tab counts, the right-click equip
    Menus/                     # main and pause menus, the loading screen, text fields
    SpellBuilder/              # the builder's selection rules, live stats and layout hit-tests
    Waystone/                  # the waystone screen's page rules, rows and layout hit-tests
  VisualTest/                  # scenario parsing and ops, PNG capture
  WorldGen/                    # terrain, biomes, lakes, ravines, clouds, the generational
                               #   cache, the tree front end and batches, worldgen stats,
                               #   the worldgen cost probe and the stage-timer tests
    Features/                  # every feature generator (mirrors src/WorldGen/Features)
      Underground/             # coal, malachite, hematite, tin, vulcanite, the barrier, gravel pockets, cave life (mirrors src/WorldGen/Features/Underground)
    Caves/                     # the cave biomes: point query equals stamp, the map, bands, determinism, safe palettes,
                               #   the cave shape and worm factor, the ore richness, the exclusive ores, and the spot
                               #   finder behind cave-biomes.json, the eye probe (EyeCaveBiomeProbeTests) (mirrors
                               #   src/WorldGen/Caves)
    Hell/                      # the hell caverns, biomes, palette and flora (mirrors src/WorldGen/Hell)
    Sky/                       # the solid cloud patches, the cloud sea, the sky islands, the sky biomes and their
                               #   flora (mirrors src/WorldGen/Sky)
    Structures/                # templates, the ruins and their waystone satellites, the battle towers (TowerShellTests:
                               #   stair, landings, the door and roof seal flood fills (the parapet and the forcefield
                               #   dome), the closed doors; TowerPlacementTests: rarity,
                               #   point query equals stamp, generated chunks, every floor's piles), the /locate search
                               #   (StructureLocatorTests: exactly the nearest, the ring cap, the landing) (mirrors
                               #   src/WorldGen/Structures)
    Trees/                     # the tree growth engine, flat over src's Trees/Shape, Logics and Features
USER_TODO.md                          # placeholder art list: every stand-in asset an agent added, its generator and
                                      #   what a replacement must keep (see "Placeholder art" under "Content")
tools/                                # asset generators: models, animation clips, sounds and the wiki scenarios, each
                                      #   re-run byte-identical; most pixel art is committed art now, but the generators
                                      #   below still write the entity models/skins and a few placeholder sprites (listed
                                      #   in USER_TODO.md)
  generate-goblin-clips.py            # deterministic generator of the goblin's animation clips (goblin.bbmodel)
  generate-hell-mob-models.py         # deterministic hellbrute/cinderwitch .bbmodel (rig + clips) and skins, and the
                                      #   ember_shard sprite; its header pins the rig and clip contract, --check
  generate-sky-mob-models.py          # deterministic galeraptor/cloudgolem .bbmodel (rig + clips) and skins, and the
                                      #   cloud_shard sprite, on the hell script's machinery; --check
  generate-skeleton-model.py          # deterministic skeleton (lanky archer with its bow) and skeleton_warrior (shorter,
                                      #   bulkier, empty handed: its dagger is the held item) .bbmodels (rigs from proportions,
                                      #   shared clips) and palette skins, and the bone sprite, on the hell script's machinery;
                                      #   --check
  generate-chest-textures.py          # deterministic placeholder chest art: one 128 x 128 skin per chest variant for the
                                      #   shared chest.bbmodel (textures/chests/) and the static twin's front/side/top tiles
                                      #   cut from the same faces; stdlib PNG writer; listed in USER_TODO.md; --check
  generate-tower-lock-textures.py     # deterministic placeholder tower_door (portcullis cutout), skeleton_pile and
                                      #   tower_altar_top tiles, stdlib PNG writer, small palettes; listed in USER_TODO.md; --check
  generate-critter-models.py          # deterministic dragonfly, crow and deer .bbmodels (their own bone sets around the
                                      #   humanoid six, clips) and skins with cutout texels, on the hell script's machinery;
                                      #   models only, no mob class yet; --check
  generate-forcefield-texture.py      # deterministic placeholder tower_forcefield tile (a faint honeycomb, only the fallback
                                      #   of the world shader's forcefield term), integer geometry; listed in USER_TODO.md; --check
  generate-cave-ore-textures.py       # deterministic placeholder amethyst_ore, jade_ore and cinnabar_ore tiles: palette swaps
                                      #   of malachite_ore's green shades, stdlib PNG reader and writer; listed in USER_TODO.md;
                                      #   --check
  generate-vulcanite-sprites.py       # deterministic placeholder vulcanite tool heads and sword: palette swaps of the iron
                                      #   sprites' metal onto vulcanite_shard's shades, stdlib PNG reader and writer; never writes
                                      #   the committed vulcanite art; listed in USER_TODO.md; --check
  generate-cave-mob-models.py         # deterministic shardling, sporeling and magmaborn .bbmodels (humanoid rigs, clips) and
                                      #   palette skins, on the hell script's machinery; listed in USER_TODO.md; --check
  generate-skeleton-king-model.py     # deterministic skeleton_king .bbmodel (crowned rig with its club, clips) and palette
                                      #   skin, on the hell and skeleton scripts' machinery; listed in USER_TODO.md; --check
  generate-iceberg-models.py          # deterministic iceberg_{small,medium,large}.bbmodel and iceberg.png; its header pins
                                      #   the geometry rules (waterline, shelf, 8 px inset steps) and the UV layout
  generate-mold-models.ps1            # the twelve cast molds' block models under assets/models/block/, from their sprites' alpha silhouettes
  generate-sandworm-model.py          # deterministic sandworm .bbmodel, skin and fang sprite (placeholder art); --check
  generate-spell-sfx.py               # deterministic placeholder spell sounds (spell_cast/impact/fizzle.wav), --check
  generate-spider-item-sprites.py     # deterministic placeholder sprite of the spiders' drop (spider silk); --check
  generate-wildlife-item-sprites.py   # deterministic placeholder sprites of the wild animals' drops (meats raw and cooked,
                                      #   hide, feather, tusks, dragonfly wing); --check
  generate-wiki-shots.mjs             # the wiki's screenshot scenarios (visual-examples/wiki-*.json) and wiki/data/screenshots.json from one spec of poses and captions
  scroll-sprite.py                    # utility: lays one sprite out as a scrolling animation sheet (frames wrapped toward a direction,
                                      #   N px/frame, vertical/horizontal/square), PNG in and out, stdlib only
```
