# Layout, part 1: src root through Entities

File tree of `OpenGLTest/` (part 1 of 4): the src root, App, Audio, Chunks, Config, Content, Core, Data, Effects, Entities. Grep it by file name.

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
      ChatInput.cs             # the chat's keys, submitting a line (a singleplayer command runs on GameCommands), the command
                               #   autocomplete (Tab, Shift+Tab, the per-frame SyncCompletion), the scenario chat op and the Tab
                               #   player list's names
      LocalCommandHost.cs      # singleplayer's ICommandHost: the local player, the world's clock, mobs and towers, chat announcements
      LocalCommandPlayer.cs    # singleplayer's ICommandPlayer: the camera, the world's inventory and LocalPlayer.Kill
      DevDiagnostics.cs        # the startup build line, the VOXEL_TIME read, the live debug flag (X, VOXEL_DEBUG) and the X debug line
      ForgeInput.cs            # the anvil's recipe list input (built by GameplayInput): open on a hammer's right-click on a stacked
                               #   anvil, cursor and look capture, card clicks forge, the close rules; connected, predicts and
                               #   sends the Forge intent
      GameplayInput.cs         # a playing world's per-frame polled input: key edges, hotbar, right-click chain, pause-gated world update, mining, middle pick;
                               #   frees the cursor while the chat is open (SyncChatCursor)
      InputEvents.cs           # the window's mouse and keyboard events, routed to whatever owns them on the current screen (a click on a chat link
                               #   suggests its command)
      FieldPointer.cs          # the mouse's side of text fields: a press places the caret, Shift extends, holding drags a selection (menu fields, the chat line,
                               #   [E] and chest search, the waystone screen's rename and search fields)
      TextFieldKeys.cs         # maps key events (and the Ctrl/Shift state and clipboard) onto TextFieldEditing for the focused field
      InventoryPanelInput.cs   # the [E] panel's input: clicks, the right-click equip, scrollbar, search keys and scope button,
                               #   creative buttons, hovered row, tooltip
      RowDragInput.cs          # dragging a listed [E] row or a hotbar slot onto a hotbar slot, equipment slot, chest or the panel: press, per-frame lift, drop
      MenuInput.cs             # the pause menu's and shared settings page's input, and applying the window-level settings
      QuestBookInput.cs        # the [P] quest book's input (built by GameplayInput): the toggle, cursor and look capture, the
                               #   press that pans past the drag threshold or selects on release, the wheel zoom
      ScenarioUi.cs            # the app's IScenarioUi: lets a VOXEL_SCRIPT run open the [E] panel and the spell builder, pick a tab or row,
                               #   type a search, hold the pointer and drive the waystone screen, the anvil's recipe list, the chat and the quest book
                               #   through the real input components
      SpellBuilderInput.cs     # the spell builder's input: open from the table (or the hold-a-staff toast), close on
                               #   E/Escape or a changed held staff, cursor and look capture, clicks, bind/clear requests
      WaystoneInput.cs         # the waystone screen's input: discover on a right-click, the popup, rename dialog and teleport list,
                               #   cursor and look capture, clicks, keys, wheel, the close rules, the trip request; connected,
                               #   opens on the server's answer and sends the rename and the trip intent
    Audio/                     # sound: the sfx library, mixer and manager, the ambience bed, the OpenAL output
      AmbienceManager.cs       # looping day/night ambience with a slow crossfade
      AudioDevice.cs           # the shared OpenAL Soft device and context (refcounted; natives for Windows, Linux, macOS);
                               #   asks for or forbids HRTF (SetHrtf, live through alcResetDeviceSOFT) and logs its status
      AudioOutput.cs           # streams a 16-bit PCM IWaveProvider to a head-locked OpenAL source on its own refill thread
      SfxLibrary.cs            # maps the loose .wav files under a folder onto sound groups (and names the
                               #   code-played ones: item_pickup, spell_cast, spell_impact, spell_fizzle, skeleton_rattle, skeleton_hit, skeleton_death, crow_hit, crow_death, crow_caw, goblin_hit, goblin_death, slime_hit, slime_death, slime_jump, slime_land)
      AmbientCallTimer.cs      # a randomized repeat timer for ambient calls (skeleton rattle, crow caw); Tick fires once per interval
      SfxManager.cs            # one-shot sound effects: Play mixes a clip centered through one AudioOutput, PlayAt
                               #   plays its mono downmix in 3D (SpatialVoices) against the listener SetListener sets
      SfxMixer.cs              # mixes any number of overlapping 16-bit stereo 44100 Hz clips over a silent baseline
      SoundFalloff.cs          # GL-free: a positional sound's distance gain and the earshot a server sends within
      SpatialVoices.cs         # the pool of OpenAL 3D sources positional one-shots play on (oldest taken when full)
      WorldSounds.cs           # where a World's components send sounds (SoundEvent): played here, or on a server
                               #   queued for the players in earshot (see "World sounds")
    Chunks/                    # chunk storage and lifecycle, the column cache and column terrain, the light
                               #   query entities read (EntityLight); meshing, world sources and streaming below
      BoxSweep.cs              # GL-free, state-free solid-box axis sweep every loose body shares (drops, TNT, falling
                               #   blocks, projectiles, goblins, slimes, debris): StepSize, OffsetAxis, IsColliding, MoveOnAxis
      BurningFluid.cs          # GL-free burning-fluid (lava) cell test over a box, behind MobLava.BodyInLava and
                               #   PlayerBodyProbe.InLava
      CellKey.cs               # packs a world block cell into one long (fluid and leaf-decay schedules, tree growth sets),
                               #   forwarding to ChunkKey's one 21-bit bit math
      Chunk.cs                 # one 16^3 chunk: the volatile reference to its id storage (ChunkBlocks), the per-block
                               #   state byte, the published light snapshot, the raw writers and edit API, save/network
                               #   snapshots, and its GPU slices (UpdateMesh, MarkMeshedHeadless); GenerateBlockData,
                               #   RestoreFrom and GenerateMeshData forward to WorldGen/ChunkTerrainFill, ChunkRestore
                               #   and Meshing/ChunkMeshBuilder
      ChunkBlocks.cs           # a chunk's palette-compressed ids: Uniform (one id), Indexed (256-id palette + one index
                               #   byte per cell) or Direct (ushort per cell); one-way upgrades, FromDense compaction,
                               #   the single-writer lock-free rules, the thread-static DenseScratch
      ChunkCellCache.cs        # a simulation's per-frame cell access: the 16-slot chunk cache, TryGetCell, the raw Write
                               #   that queues the touched chunks, the MarkDirty batch; one instance per owner
      ChunkFluidQueries.cs     # the fluid surface, column-top and flow queries over the loaded cells (behind ChunkManager.GetFluid*)
      ChunkKey.cs              # packs a chunk position into one long, so the chunk
                               #   dictionaries hash one integer instead of three floats; the one 21-bit bit math, which CellKey shares at block scale
      ChunkManager.cs          # the chunk lifecycle and composition root: load/restore/stream-in/unload against a focus list
                               #   (unload only outside every focus's radius), saves of edited chunks, the world cell queries;
                               #   composes ChunkMeshScheduler, ChunkRenderLists and PlayerPlacedIndex and forwards their public members
      ChunkMap.cs              # the loaded-chunk table (ChunkManager.Chunks): a ConcurrentDictionary keyed on a packed ChunkKey long
      ChunkRenderLists.cs      # Renderable / TranslucentRenderable / ChestChunks swap-remove bookkeeping and MeshVersion (behind ChunkManager's forwarders)
      ChunkRestore.cs          # rebuilds a chunk from a ChunkSnapshot (a save, a world source's map, a streamed chunk):
                               #   shares a Uniform storage, clones a compact one or recompacts an edited one, copies
                               #   state, recounts, surface floor, fluid frontier, publishes (behind Chunk.RestoreFrom)
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
        ChunkLighting.cs       # the wide light-volume snapshot and flood (sun + colored lamp) + corner light queries;
                               #   the sky cover rule (no sun under a coversSky block, gated by AnySkyCover; BT-10)
        ChunkMeshBuilder.cs    # the per-chunk mesh build: neighborhood resolve, padded and light snapshots, the light flood
                               #   and the per-voxel emitter loop (behind Chunk.GenerateMeshData)
        ChunkMeshData.cs       # CPU-side mesh of one chunk (opaque + translucent lists, light snapshot, vents, flame points)
        ChunkMeshScheduler.cs  # the dirty set, the per-frame dirty scan, the thread-pool mesh runners, the completed queue and budgeted upload,
                               #   the immediate remesh paths (behind ChunkManager.Update, MarkDirty, ImmediateRemesh*)
        ThinWoodBite.cs        # the chop slit of a thin (node-and-tubes) wood cell: the bite box, box subtraction, emission
        WoodDamage.cs          # the permanent chop damage per natural trunk cell (damage + first-hit side face) the mesher reads, and the damage -> slit depth rule
      ChunkMesher.cs         # the binary greedy pass, the biome-tinted cube face and model-tint helpers, and the box-face/quad primitives the emitters share
        FaceLight.cs           # a cell face's four smooth corner-light codes, bilinearly sampled by the procedural emitters
        FluidEmitter.cs        # a fluid cell's surface and side quads: order-independent corner heights, corner light, per-corner flow codes
        GreedyKey.cs           # the greedy pass's packed face key (id 0-15, AO 16-23, submerged 24, water surface 25-31): Pack, WithSurface and the decoders
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
    Commands/                  # GL-free chat commands, shared by singleplayer, the server and a client's autocomplete (see "Chat")
      CommandNode.cs           # one step of a command tree: children tried in order, Handler, Permission (holds for the subtree),
                               #   Description, Syntax
      LiteralNode.cs           # a fixed word (a command, a subcommand), case-insensitive, with a root's aliases
      ArgumentNode.cs          # an argument: its IArgumentType and the name its value is read back by
      Cmd.cs                   # starts a literal step (Cmd.Literal)
      Arg.cs                   # starts an argument step: Word, Int, Number, Coordinates, Player, Entity, Item, Command
      CommandBuilder.cs        # the fluent extensions: Then (a possible next step), Executes, Requires, Describe, Alias
      CommandDispatcher.cs     # the trees and the one walk over them: Execute (answers unknown, refused, a bad token or the
                               #   usage), Suggest (the completions for the word at the caret, and the usage), Usage, Visible
      CommandContext.cs        # a parsed line for a handler: Source, Get<T>, Self, Player (an online player), Reply
      CommandSource.cs         # who runs a line: name, rights, feet (the base of ~), Self, Host, Reply
      CommandPermission.cs     # Everyone / Operator
      CommandException.cs      # a handler's refusal, answered as a system line
      ICommandHost.cs          # the world a command runs in: players, the clock, SpawnEntity, Towers, Announce
      ICommandPlayer.cs        # a player a command acts on: Feet, modes, Teleport, Give, TrySetModes, Kill
      Suggestion.cs            # one completion: what it inserts and what the popup shows
      CommandSuggestions.cs    # Suggest's answer: ReplaceStart, the ranked completions, the usage hint
      SuggestionRanking.cs     # filters and orders completions: prefix match, then a match inside, by letter-pair similarity
      GameCommands.cs          # Create: the dispatcher with every game command registered
      Arguments/               # IArgumentType (TokenCount, Syntax, TryParse, CouldStart, Suggest) and its kinds: WordArgument,
                               #   IntArgument, NumberArgument, CoordinatesArgument (x y z with ~), PlayerArgument (exact or
                               #   unique prefix), EntityArgument (EntityRegistry), ItemArgument (block/ and item/ pinned names,
                               #   TryResolve; ContentItem), CommandNameArgument (/help's)
      Definitions/             # one class per command with its Register: HelpCommand, TeleportCommand (LineTo, the /tp a link
                               #   suggests), GiveCommand, SummonCommand, TimeCommand, GameModeCommand, FlyCommand, KillCommand,
                               #   LocateCommand (Tower: the linked /locate answer)
    Config/                    # every tuning constant, one static class per domain (all const or static
                               #   readonly, read concurrently by every thread; namespace VoxelEngine.Config)
      AudioConfig.cs           # ambience bed paths, volumes and fades; the sfx volume
      BloomeryConfig.cs        # iron smelting: the bloomery's heat band, bellows pump, smelt and burn times, the anvil's slack
      HudConfig.cs             # selection outline and pulse, hotbar name fade, pop and flash, the loading screen
      IcebergConfig.cs         # icebergs: caps and the spawn ring, the spawn band, size and mass, drift and push,
                               #   wobble, life cycle, the client mirror's lead
      RarityStyleConfig.cs     # the rarity chrome's per-rarity layer strengths (opacity, wash, glow, sheen, sparkle,
                               #   pulse depth) and its shape and motion knobs; Common all zero
      JuiceConfig.cs           # landing and footstep dust, slime splat, hit sparks, death poof, blast shake and
                               #   push, falling-block dust, lava steam, tree thud, tower collapse, splashes, tool shatter, pickup
                               #   sparkle, low-health vignette, damage arc, surfacing droplets
      LanternConfig.cs         # eight fuel slots, slime duration ratio and raised hanging carry pose
      LightingConfig.cs        # alpha cutoff, atlas anisotropy, light spread, shadow cascades, point and fire
                               #   lights, fog, tonemap exposure, eye adaptation
      ManaConfig.cs            # the mana pool: start max, growth per known part and its cap, the refill time
      QuestConfig.cs           # the quest toast's timing and size; the quest book's grid step, node size, zoom range, drag threshold
      SpellConfig.cs           # spell recipe rules: when complexity starts to fizzle, the chance per unit and its cap;
                               #   the staff slot cap and the cast cooldown (CastCooldownSeconds)
      WildlifeConfig.cs        # the wild animals: their budget and spawn ring, species weights and groups, gaits,
                               #   the deer's and the boars' bodies, speeds and temper
      MobConfig.cs             # mob caps and shared mob rules (flight's probes, arrival and wander rise), layer difficulty, hell and sky mobs, slimes, goblins (flee, dodge, pack, torches, notice
                               #   pip, cave weights), the cave mobs and their depth tier, healthbars, damage numbers, the realm layer
                               #   difficulty step
      NetworkConfig.cs         # multiplayer cadences, movement, edit and intent rules (the attack and cast slack),
                               #   timeouts, LOD streaming, ServerAllowCreative
      ParticleConfig.cs        # falling leaves, mining debris, campfire, lava and torch embers and smoke, bubbles,
                               #   spring steam, the cave biome motes (CaveMote*)
      PlayerConfig.cs          # break overlay, camera and swing feel (sprint FOV, strafe tilt, hit-stop, throw
                               #   zoom), water depth states, lava speed, throws, slime block, sink blocks, bandage
      PostProcessConfig.cs     # biome grade, damage fringe, underwater wobble, FXAA, contact AO, godrays, sun dazzle, lens flare, GPU timer
                               #   gate
      SaveConfig.cs            # autosave, save writer retries and shutdown, teardown joins and drain, entity restore
      ScorpionConfig.cs        # the giant scorpion: body, pinch and sting, desert cave and surface spawn chances
      SandwormConfig.cs        # the sandworm boss: chain, burrow, breach, air and dash numbers, contact, the sand and quake effects
      ShrapnelConfig.cs        # the bone shrapnel: the charm's weaker default burst, the factor a worn charm raises a
                               #   blade's own by, the fragments' life, speed, gravity and launch spread, the live cap
      SimulationConfig.cs      # fluid tick, tree felling, reactive growth, leaf decay, ash, saplings
      SkyIslandConfig.cs       # the sky layer's worldgen (L-12): island zones (Zone*: cells, sizes, tiers, gap),
                               #   islands (Island*: cells, radii, dome, taper, stalactites, the flora headroom),
                               #   the sky biomes and their flora (L-13: SkyBiome*, Meadow*/Frost* rates, vines,
                               #   the tree cells and chances) and the cloud sea (CloudSea*: its band from
                               #   SkyConfig.SkyCloudSea*, cover and detail noises)
      SpiderConfig.cs          # the giant spider and the broodmother: body, bite, cave and surface spawn chances
      StatusEffectConfig.cs    # status effects: the duration scale cap, the block writer's reach and write caps,
                               #   the mob tint strength, the particle emitter cap and rate
      SkyConfig.cs             # day length, sun and glare, sky dome, stars, moon, moonlight, aurora, clouds, wind,
                               #   the sky-island cloud sea (SkyCloudSea*), the realm layer fog (HellFog*, SkyAltitudeTint),
                               #   the cave biome atmosphere (CaveFog*, CaveAmbient*)
      StreamingConfig.cs       # view radius, per-frame mesh, dirty and scan budgets, thread counts, LOD build and
                               #   upload budgets, loading budgets and gate
      StumpConfig.cs           # the log standing on a tree stump: size, reach on the stump's top, wobble length, tilt and blow gap
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
                               #   CloudPatchCoreValue, CloudPatchDetailFrequency), and the cave biomes
                               #   (CaveBiome*, CaveEcho*, CaveDeep*, CaveSubBiomeFrequency, Cave*AccentRate), their
                               #   shape (CaveShapeScale*, Cave*CheeseBoost, CaveMagmaCavernBoost, CaveFungalTunnelBoost,
                               #   Cave*WormRadiusFactor), the Y-band ores' cave richness (*CrystalShift, *FungalShift,
                               #   *EchoShift, *MagmaDeepShift beside each ore's threshold) and the exclusive cave ores
                               #   (CaveOresEnabled, CaveAmethyst*, CaveJade*, CaveCinnabar*)
    Content/                   # the data-driven content pipeline (ContentRegistry) — see "Content" below
      ContentRegistry.cs       # the static facade: owns the content tables, assigns ids, runs Load / BakeModels /
                               #   Reset in order and serves the public lookups (RequireBlock, recipes, tags, ...)
      ContentFiles.cs          # the one JsonSerializerOptions, JSON file listing and reading, name -> id resolution
      EntityLoader.cs          # data/entities -> EntityRegistry (hp, loot table, the parsed .bbmodel, the held item)
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
        BlockTables.cs         # sizes every BlockType per-id table to the loaded id count (Allocate, air's defaults) and
                               #   bounds peer ids (IsAssignedId on the server, RequireAssigned on the client)
      Definitions/             # the JSON DTOs
        BlockDefinition.cs, ItemDefinition.cs,
          LootTableDefinition.cs, ModelDefinition.cs  # JSON DTOs
        AlloyRecipeDefinition.cs # JSON shape of assets/data/recipes/alloy/<name>.json: result + parts
        CastRecipeDefinition.cs # (fired mold, material) -> result + cooling
        CookRecipeDefinition.cs # JSON shape of assets/data/recipes/cook/<name>.json
        CrushRecipeDefinition.cs # JSON shape of assets/data/recipes/crush/<name>.json
        EntityDefinition.cs    # JSON shape of assets/data/entities/<name>.json
        ForgeRecipeDefinition.cs # JSON shape of assets/data/recipes/forge/<result>.json: metal + metalCount -> result + count
        MeltRecipeDefinition.cs # block/item -> (material, ml)
        MoltenMaterialDefinition.cs # a molten metal: id, tile, tint
        RecipeDefinition.cs    # JSON shape of assets/data/recipes/<name>.json
        SpellPartDefinition.cs # JSON shape of assets/data/spell/<kind>/<name>.json
        StatusEffectDefinition.cs # JSON shape of assets/data/effects/<name>.json
        QuestGraphDefinition.cs # JSON shape of assets/data/quests/<graph>.json (the graph, its quests, their requirements)
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
        ChestModel.cs          # the one Blockbench model every chest is drawn with (models/block/chest.bbmodel): its rig,
                               #   the open/close/wobble clips, the contract check, SkinPath, Root (facing) and PartMatrix
        ModelBakeSteps.cs      # BakeModels' steps: facing/multiblock bake checks, BakesGreedy, tint resolution, parent self-check
      Recipes/                 # compiled craft, cook, crush, melt, cast, alloy and forge recipes
        AlloyRecipe.cs         # one alloy: a set of molten parts (material, ml) -> the same ml of the result
        CastRecipe.cs          # (fired mold, material) -> result + cooling
        CookRecipe.cs          # one compiled campfire recipe: raw input, cooked result and seconds on the fire
        CraftingRecipe.cs      # one compiled crafting recipe: what it makes and what one craft consumes
        CrushRecipe.cs         # one compiled quern recipe: crushable input and the crushed result
        ForgeRecipe.cs         # one anvil recipe: so many of a solid metal stacked on an anvil -> the result (one per result)
        MeltRecipe.cs          # block/item -> (material, ml)
        RecipeLoader.cs        # data/recipes (craft, cook, crush, melt, cast, alloy, forge) and data/molten -> the compiled lists
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
      Quests/                  # quests (see "Quests (U-15)"): the compiled quest book, loader and registry
        QuestBook.cs           # every compiled quest and the graph titles, by index and id
        QuestLoader.cs         # data/quests/*.json -> a QuestBook; Compile is pure (tests); refuses cycles and unknown names
        QuestRegistry.cs       # the published QuestBook, set once by ContentRegistry.Load
        QuestType.cs           # one compiled quest: id, text, icon, grid position, shape, parents, children, requirements
        QuestRequirement.cs, QuestRequirementKind.cs  # obtain/craft/break/place/kill with its target and count
        QuestShape.cs          # Hex / Circle node frame
      StatusEffects/           # status effects (MG-4b): the compiled effect, its block actions, loader and registry
        StatusEffect.cs        # one compiled effect: duration, tick, damage share, factors, block actions, visuals; Bit
        StatusEffectBlockAction.cs # [Flags] Ignite / Freeze / Thaw, what an effect's tick does to the cells around
        StatusEffectLoader.cs  # data/effects/*.json -> effects (id = file-name order, at most MaxKinds); Compile is pure
        StatusEffectRegistry.cs # every effect by name and id, KnownMask, published once by ContentRegistry.Load
      Structures/              # structure templates (see "Structure templates and ruins")
        StructureCell.cs       # the packed template/tower cell: block id bits 0-15, state 16-23, None (-1) writes nothing
        StructureLoader.cs     # data/structures/*.json -> templates; a two-cell block stands whole, a satellite holds
                               #   exactly one waystone, a ruin none
        StructureRegistry.cs   # templates by kind and role: Of (ruins), SatellitesOf (waystones), TryPick(..., satellite)
        StructureTemplate.cs   # one compiled template: kind, Satellite, weight, size, height, ground layer, four turns
      Stations/                # GL-free render constants of the kiln, quern and casting passes, molten materials
        KilnRenderer.cs, QuernRenderer.cs,
          CastingRenderer.cs   # GL-free placement/animation
                               #   constants tying a station's render pass to its
                               #   authored model (the crucible bowl, fuel lump and
                               #   pending-charge sizes every kiln shares; the mold
                               #   pocket levels and the cooling dissolve curves)
        BloomeryProfile.cs     # a bloomery anchor's compiled `bloomery` object (caps and item ids) and an anvil's `anvil` object
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
      MathUtils.cs             # lerp/easing/pulse/sweep helpers for animation curves + shared constants
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
                               #   adaptation, biome grade, contact AO, godrays, sun dazzle, lens flare, underwater wobble
        BiomeGrade.cs          # GL-free post color grade from the eye's climate: warm over desert, cool over snow, eased
        ContactAo.cs           # GL-free twin of contact-ao-fragment.glsl (SH-28)
        ContactAoTaps.cs       # the nine raw window depths the contact AO pass reads around one pixel (ContactAo.Occlusion)
        EyeAdaptation.cs       # GL-free exposure target from the voxel light at the eye, eased slower into the dark
        Godrays.cs             # GL-free twin of the godrays' strength (facing, sun height, cloud, wet), scatter and phase
        LensFlare.cs           # GL-free twin of the lens flare's circles: its strength (off the sun, in view), cover, centers and shape
        LensFlareGhost.cs      # one circle of the lens flare: offset along the flare's line, radius, hollow, softness, tint, brightness
        SunDazzle.cs           # GL-free twin of the sun dazzle: its strength (facing, disc, wet), disc radius, visibility ease and glow
        Tonemap.cs             # the C# twin of post-fragment.glsl's tonemap, used only
                               #   by tests: exposure, then a per-channel clamp to 0..1
        Shockwave.cs           # one expanding cylindrical shell the post pass bends the screen with (a conquered tower floor's pulse)
        UnderwaterWobble.cs    # GL-free twin of the post pass's underwater screen sway: the offset and its wrapped clock
      Sky/                     # sun, moon, stars, aurora, glare, silver lining, wind and the day cycle
        Aurora.cs              # GL-free twin of the aurora's fade (eye coldness x star visibility) and its wrapped noise drift
        CloudSilverLining.cs   # GL-free twin of the clouds' silver lining: sun gate, forward lobe, thinness along the view
        DayCycle.cs            # GL-free sun direction, dayness and night factor of the lighting clock
                               #   (FrameLighting.SetLighting and the slime size roll both read it)
        LayerClimate.cs        # GL-free realm layer sky: hell tint and altitude shift of the eye's height, eased,
                               #   applied to the sRGB SkyPalette; the hell layer index
        CaveAtmosphere.cs      # one cave biome atmosphere: strength plus full-strength fog color, fog near/far, ambient tint
        CaveClimate.cs         # GL-free cave biome atmosphere: per-biome target, the ease, the palette shift, fog
                               #   distances, ambient factor and motes (CB-3)
        Moonlight.cs           # GL-free key-light rule: sun by day, moon at night, handed over with both dark
        MoonPhase.cs           # GL-free moon: the day's phase (steps at noon), its light direction, lit share, visibility
        SkyPalette.cs          # GL-free zenith/horizon/glow sky palette and the C# reference of SkySample
        ShootingStar.cs        # GL-free twin of the night dome's shooting stars: hashed slots, the great-circle streak, its light
        StarField.cs           # GL-free twin of the night dome's star field (cells, hash, visibility)
        SunGlare.cs            # GL-free sun lens glare: per-frame intensity and its sky falloff
        Wind.cs                # the one wind of the world: a horizontal direction that turns slowly
                               #   and steadily with the lighting clock (window time plus VOXEL_TIME)
      Surfaces/                # per-tile terms of test-fragment.glsl: forcefield, glass, grass, ice, lava, leaves, ore
        ForcefieldShimmer.cs   # GL-free twin of the tower dome's forcefield film: rim, honeycomb patches and rising band
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
      BossSelection.cs         # GL-free pick of the boss the HUD's boss bar shows: the nearest living IsBoss mob within its
                               #   BossBarRange (the sandworm's unlimited, the Skeleton King's MobConfig.SkeletonKingBossBarRangeBlocks)
      ContactDamage.cs         # GL-free contact damage a body takes standing against a cactus
      DamageNumber.cs          # one floating damage number in a DamageNumberPool: where it started, the damage it shows
      DamageNumberPool.cs      # the floating damage numbers over struck mobs, in one fixed array: a hit claims a free slot
      EntityAnimator.cs        # GL-free per-entity animator: idle/walk (or idle/fly) locomotion + one-shot action
                               #   layer, events, parent-chain part matrices, HandAnchor
      EntityModel.cs           # a runtime entity model: the cubes parsed from a Blockbench project
      EntityType.cs            # one entity kind loaded from assets/data/entities/<name>.json: its display name, health pool
      FallingBlock.cs          # a gravity-affected block (sand, gravel) in flight
      FallingBlockLanding.cs   # a falling cube written back as a block: cell, block, fall, submerged (its dust)
      FlightSteering.cs        # GL-free flight rules: chase goal, floor probe, hover/clearance height, bob, steered
                               #   velocity, the wall probe that slides or lifts a flyer (RiggedMob.Fly)
      FlightTuning.cs          # one flying kind's speeds, acceleration, hover band, clearance and bob (Validate)
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
      MotionEstimate.cs        # GL-free velocity estimate from a body's positions (a server player's heading)
      MobTargets.cs            # GL-free target rules: body contact, nearest non-immune target, min distance, out of play
      RealmMob.cs              # abstract realm mob (the hell and sky mobs): the chase frame (face, Decide, Locomote: a
                               #   walk by default, a flyer steers), the throttled line of sight (SeesTarget), ChestOf,
                               #   the attack clip and its replicated count (StartAttack, AttackCount, MirrorAttackCount)
      RiggedMob.cs             # abstract walking or flying mob animated from its clips (Goblin, the realm mobs): the
                               #   EntityAnimator, walk/auto-jump/float physics or Fly, facing, death/spawn/hurt clips,
                               #   the mirror's animation and BodyEffect (a flyer's pitch and bank)
      PrimedTnt.cs             # a primed TNT block: a full-size cube that falls under gravity and
                               #   collides with blocks axis by axis like the player and the item drops (BoxSweep)
      ThrownProjectile.cs      # a thrown item or a goblin stone in flight: falls under Gravity,
                               #   its damage scaled by the charge, hits a mob or stops on a block
      VantageSeeker.cs         # GL-free vantage seeking: a ranged mob without a line walks around its target to a
                               #   standable, reachable spot with one (the skeleton archer)
      Goblins/                 # the goblin: the mob, its stone aim and its torch roll
        Goblin.cs              # the goblin: body physics, throw-then-melee AI, clip/event contract
                               #   (ValidateContent), animation-driven release/impact events
        GoblinAim.cs           # GL-free low-arc ballistic solver for the thrown stones, and its lead solve
        AimTracker.cs          # a ranged mob's eye on its target through the wind-up: smoothed velocity, the
                               #   ground under it, the led launch velocity at the release (every ranged mob)
        GoblinTorches.cs       # GL-free torch roll for a goblin spawn (night, depth, one uniform roll)
      HellMobs/                # hell's mobs (see "Hell mobs"): the shared base, the two kinds, the layer difficulty
        HellMob.cs             # abstract hell mob (a RealmMob): the layer it spawned in and its layer-scaled pool
        Hellbrute.cs           # the melee brute: walk, a charge with a clear line, the melee clip's impact event
        Cinderwitch.cs         # the fire caster: the range band, a cast with a clear line, the release event's bolt
        LayerDifficulty.cs     # GL-free: the per-layer multiplier (health, damage, cast cooldown) and pack size;
                               #   ScaleBy, the rounding every realm's difficulty scales through
      Posing/                  # rig poses, namespace VoxelEngine.Entities.Posing
        IBonePose.cs           # a pose of a Blockbench rig: each bone's animated local transform
        RigPose.cs             # GL-free: composes a pose down the rig's parent chain into a part's world matrix
        ClipPose.cs            # one clip sampled at one time as a pose, no layers (a chest's lid); default = rest
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
      SkyMobs/                 # the sky islands' mobs (see "Sky mobs"): the shared base, the two kinds, the zone difficulty
      Wildlife/                # the animals of the wild (see "Wildlife"): Animal (heeding, idle habits, gaits), the
                               #   WildlifeSpecies table, Deer (watches, bolts, grazes), Boar (neutral until crowded or
                               #   hurt, then charges and gores; the dire boar is the same class oversized), FlyingAnimal
                               #   (lands, rests, takes off; posture for mirrors), Crow (flock pest: swoops, pecks, caws),
                               #   CrowCaws (the flock's ambient caw: a randomized timer, played when a crow is in earshot),
                               #   Dragonfly (waterside hunter: darts and bites)
        SkyMob.cs              # abstract sky mob (a RealmMob): the zone tier it spawned in and its tier-scaled pool
        Galeraptor.cs          # the diving flyer: FlightTuning, the hover over its target, the dive clip's impact event
        Cloudgolem.cs          # the ground bruiser: walk with ledge caution, the slam clip's impact event, the shockwave
        SkyDifficulty.cs       # GL-free: the zone tier's multiplier (health, damage), pack size, the bruiser's big-zone gate
      CaveMobs/                # the cave sub-biomes' mobs (see "Cave mobs"): the shared base, the three kinds, the depth tier
        CaveMob.cs             # abstract cave mob (a RealmMob): its depth tier, fixed at spawn, and its tier-scaled pool
        CaveDifficulty.cs      # GL-free: the depth tier of a y (TierAt), Clamp, the tier's multiplier (health, damage)
        Shardling.cs           # the crystal caves' caster: the range band, a cast with a clear line, the release event's shard
        Sporeling.cs           # the fungal caves' walker: the melee clip's impact event as a spore burst (InBurst)
        Magmaborn.cs           # the magma deep's brute: the melee clip's impact event, a burning blow
      TowerMobs/               # the battle tower's mobs (see "Skeleton" and "Battle tower"): the skeleton, the Skeleton
                               #   King and the floor-tier pile waves and limits
        Skeleton.cs            # the undead archer or warrior (a RealmMob, Role by kind): melee stab or punch at its impact
                               #   event, the archer's bow shot at its release event inside its band with a clear line (it
                               #   backs away nearer), two attack clips over one count, its role, outfit and wire look byte
        SkeletonRole.cs        # Archer / Warrior: how a tower skeleton fights, named by its entity kind
        SkeletonArmor.cs       # None / Leather / Copper: one piece of a skeleton's outfit
        SkeletonOutfit.cs      # GL-free: a skeleton's armor (one of 16 head/chest/legs outfits), its roll by role, its
                               #   damage share and the item colors its shells are tinted by
        SkeletonRattle.cs      # the skeletons' ambient bone rattle: a randomized timer plays a skeleton_rattle clip when a
                               #   skeleton is in earshot of the camera (World ticks it; injected play and random, GL-free)
        SkeletonKing.cs        # the tower boss (a RealmMob, IsBoss, IsUndead): slam, volley and summon clips over one count,
                               #   the leash to its home (HomeMove, EnforceLeash), the unengaged reset, the volley burst and its
                               #   scatter, the slam's landing cue for the ground heave
        SkeletonKingBrain.cs   # GL-free: phases by health share (the first observation silent, a phase change owes a summon),
                               #   the attack choice by range, sight and cooldown with an injected roll, the phase-3 enrage
        SkeletonKingAttack.cs  # None / Slam / Volley / Summon
        BoneBurst.cs           # GL-free: the king's volley as a burst, its bones due one after another over a window
        SkeletonKingHome.cs    # where a king is leashed: its tower (or none), the altar-top feet, the leash radius
        TowerDifficulty.cs     # GL-free: the floor tier's pile wave size (PackSize), the living-skeleton limit near a pile (PileNearLimit), clamp
        TowerFloorRef.cs       # the tower (by placement cell) and floor whose lock a bound skeleton counts toward
      Sandworms/               # the desert boss (see "Sandworm"): the mob, its chain, its brain and its pose
        Sandworm.cs            # the mob: the head is its position, the exposed segments by the sand surface, the segment hit tests and
                               #   damage split, the jaw, the contact test; also the client mirror mode (phase, target and velocity from the server)
        SandwormBrain.cs       # GL-free state machine: thump, dig, breach, air (dive or slide), dash, retreat; the head velocity and the signals
        SandwormChain.cs       # GL-free follow-the-leader chain of segments: fixed link lengths, a bend limit, the taper
        SandwormPose.cs        # GL-free world matrices of the model parts: a segment's, the lower jaw's
      Crawlers/                # the mobs that walk every surface on solved legs (see "Spiders", "Scorpion")
        CrawlerMob.cs          # abstract crawler (a CaveMob): the crawl toward its target, the leap and the path when
                               #   it stalls, the ceiling drop, the knock off a wall, the ranged shot's aim and release,
                               #   the drawn orientation, its part matrices, wire stance and model contract
        CrawlerNavigator.cs    # GL-free: notices a stalled chase, follows a planned path foothold by foothold
        CrawlerPathfinder.cs   # GL-free A* over block faces: along a face, climb, wrap an edge, let go and fall
        CrawlerFoothold.cs     # one path step: an air cell and the face held there, its body center, a fall marker
        Spider.cs              # the giant spider and the broodmother: the bite's impact event, the web shot, death on
                               #   its back
        Scorpion.cs            # the giant scorpion: sting first, pinch while the sting recovers, spit out of reach,
                               #   which blow landed
        ScorpionStrike.cs      # which of its three attacks a scorpion landed
        SurfaceCrawler.cs      # GL-free body physics on any face of the grid: cling, climb a wall, wrap an edge, fall,
                               #   float and grab; a leap's launch velocity
        CrawlFrame.cs          # GL-free: the six faces by wire index, a heading as an angle on a face, the body rotation
        CrawlerRig.cs          # the leg contract read from a model once: hips, segment lengths, resting foot points
        CrawlerLegs.cs         # the eight feet: planted holds probed in the world, alternating steps, the feet's plane
        LegIk.cs               # GL-free analytic two-bone solver: upper and lower rotations that put the foot on a point
        CrawlerPose.cs         # the IBonePose of a crawler: clips for the body, solved legs unless the action keys them
      Slimes/                  # the slime: the mob, its sizes and its merge
        Slime.cs               # a slime: a gel cube that hops toward the player and only hurts on contact while it is off the ground
        SlimeMerge.cs          # GL-free slime merge: who merges (same size, touching, past the cooldown),
                               #   the merged size, center and health
        SlimeSize.cs           # GL-free per-size slime stats: body scale, health, contact damage,
                               #   loot rolls, each 1 + (size - 1) * its config step; the spawn size roll
```
