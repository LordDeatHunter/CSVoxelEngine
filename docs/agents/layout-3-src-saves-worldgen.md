# Layout, part 3: Saves through WorldGen

File tree of `OpenGLTest/src/` (part 3 of 4): Saves, Simulation, Status, UI, VisualTest, WorldGen.

```
    Saves/                     # GL-free world saves: stores, codecs, names, world sources (see "Saves")
      ChunkSaveCodec.cs        # the chunk file format (v3): block-name palette + Deflate body of byte/ushort indices,
                               #   written from a dense decode of the storage, read back through ChunkBlocks.FromDense
      CrucibleSaveCodec.cs     # a crucible's metals by name (CrucibleData), shared by player.json, stations.json
                               #   and the inventory snapshot; Import clamps to the capacity
      ChunkSaveStore.cs        # one world's chunk files + side cars: pending map, index, async writer thread
      ChunkSnapshot.cs         # immutable copy of a chunk's ids (a ChunkBlocks clone), state bytes and player-placed cells
      ColumnFileCodec.cs       # an imported column file: magic CVXL, version, Deflate body of ColumnData
                               #   (heights, bed, water levels, biomes, dirt depth, corner climate)
      EntitySaveCodec.cs       # reads/writes entities.json
      EntitySaveData.cs        # the entities.json shape: mobs (health/facing/velocity/ammo) and drops
      ImportedMapWriter.cs     # writes an imported map (columns/*.col + chunks/*.bin); ExportGenerated seeds
                               #   one from any IWorldSource (tool/test use)
      LodEditState.cs          # lod-edits.bin: export/import of the LOD caps, surface overrides, canopy markers
      MobSaveCodec.cs          # a live Mob -> its EntitySaveData form (per-kind ammo)
      PlayerSaveCodec.cs       # reads/writes player.json
      PlayerSaveData.cs        # the player.json shape: pose, modes, health, clock, inventory + hotbar, quests
      QuestSaveData.cs, QuestViewData.cs  # quest progress by id (completed, counts) and the quest book's pan and zoom
      SaveNames.cs             # block/ and item/ namespaced content names, so saves never store raw ids
      StationSaveCodec.cs      # reads/writes stations.json
      StationSaveData.cs       # the stations.json shape: campfires, querns, kilns, cast molds (content by name), tower locks
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
      BoneShrapnel.cs          # the bone fragments a landed hit sprays: spawn spread, flight against the mobs,
                               #   bills each hit through MobStrikes, and appends them to the frame's projectile draw
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
                               #   event, loot + carried flint on death, ring spawning (night surface; caves by biome weight)
      Camps/                   # goblin camps, namespace VoxelEngine.Simulation.Camps (see "Goblin camps")
        GoblinCamps.cs         # one camp a dark night by chance, far from a player: a lit fire and torch-bearing goblins
                               #   milling round it (MobDirector owns it, pitching the fire through BlockPlacer)
      Lanterns/                # per-instance fuel/storage (LanternState, LanternComponent), target/precedence/access
                               #   (LanternTarget, LanternAccess, LanternInteraction), placed ownership/ticking
                               #   (LanternManager) and versioned persistence (LanternData, LanternCodec)
      Stumps/                  # the log standing on a tree stump: StumpManager (placement, pick-up, wobble, save, snapshot),
                               #   StumpLog (one log's spot, turn and wobble), StumpInteraction (right-click place/lift, left-click blow)
      Chests/                  # chest block entities, namespace VoxelEngine.Simulation.Chests (see "Chests (I-4)")
        ChestManager.cs        # the registry by cell: state created on first use (a ruin chest rolls its loot), the
                               #   spill on a break and the stale sweep, the stations.json capture and restore
        ChestState.cs, ChestSlot.cs  # what a chest holds: per slot one stackable or one tool/gear instance
        ChestLoot.cs           # a never-opened chest's contents from its (tier's) loot table, its cell and the seed
        ChestTransfer.cs, ChestMove.cs, ChestAccess.cs  # the conserving moves between an inventory and a chest, the
                               #   move a click or the wire names, and the reach-checked open and apply both sides share
        ChestLids.cs           # GL-free: which chests stand open (at least one viewer) and what each lid plays (open,
                               #   close, the locked wobble), the viewed cells on the wire (the end of StationState)
      HellMobManager.cs        # the hell mobs: AI tick, the brute's blow at its impact, the casters' fire bolts
                               #   (MobMissiles; damage + burning), loot, despawn, the hell-only ring spawn (packs by layer)
      Icebergs/                # the iceberg simulation, namespace VoxelEngine.Simulation.Icebergs
        IcebergManager.cs      # the live bergs: forced and ring spawns, drift and push, the terrain/berg/body
                               #   collision, the life cycle, IBodySolids, the rider carry and push-out, client mirrors
        IcebergSpawner.cs      # where a berg may spawn: ocean at sea level, the band (injectable IcebergClimate),
                               #   the footprint clear and loaded, no target near
      KilnManager.cs           # kiln block entities keyed by anchor: fuel, ignition, the crucible
                               #   slot and its melt loop, plus the multiblock integrity sweep
      Smithing/                # the iron works, namespace VoxelEngine.Simulation.Smithing (see "The bloomery and the anvil")
        AnvilManager.cs        # anvils keyed by cell: one piece (hammer hits gated by the hammer's gap, result and slag) or a
                               #   stack of one forge metal (stack, take back, the recipes Offered, TryForge), save and wire
        BloomeryManager.cs     # bloomeries keyed by anchor: ore, fuel, heat, smelting, the integrity sweep, save and wire
        BloomeryRules.cs       # GL-free heat model: zones, decay, pump, fuel factor, burning
        SmithingReadout.cs     # GL-free look-at status lines of a bloomery and an anvil
        SmithingStations.cs    # both registries as one unit (World.Smithing): update, save, wire, and Forge (a pick into an inventory)
      LeafDecay/               # leaf decay and the orphaned-wood crumble, namespace VoxelEngine.Simulation.LeafDecay
        LeafDecayManager.cs    # the composition root and facade: the scheduled cells, the tick and its slicing,
                               #   ScheduleAround/ScheduleBox from every edit path, TickLeaf/TickWood and the removal
        OrphanWoodCheck.cs     # the orphaned-wood test: the parent-chain fast path, the full TreeSupport search, frame memos
        TipHydration.cs        # the leaf hydration rule on world cells: best-first tip offsets, live tips, tip support
      MobDirector.cs           # World component: the per-frame mob job (the targets, the slime and goblin updates and
                               #   their damage, the blast shove, the mob list and its scenario spawn/damage seams,
                               #   the drain of status-effect block actions into the StatusEffectBlockWriter)
      MobMissiles.cs           # GL-free: the shared mob missile flight (hell bolts, skeleton arrows, the king's bones and
                               #   shards): launch (gravity share and life per missile), fly, the touch test (targets only,
                               #   never a mob), an immune target spared, the hits of the frame for the owning manager to bill
      SpawnOrigin.cs           # where a ring search is centered and how that center moves (a target's eye and velocity)
      MobSpawner.cs            # the ring spawn searches, leaning toward a running target's heading: dry land surface (never in a tower footprint), dark caves,
                               #   hell floors, sky island tops
      RealmMobManager.cs       # the realm mob managers' shared frame: the capped list, forced spawn, mirrors, the update
                               #   loop (target, hazards, loot, attack, shove, despawn), Hit and the spawn turn
      QuernManager.cs          # block-entity registry for querns: one crushable unit per cell plus a one-rotation spin timer
      SaplingManager.cs        # the saplings planted in the world (an acorn, or any
                               #   ItemUseAction.PlantTree use), GL-free and main thread only
      WildlifeManager.cs       # the wild animals: heeded targets only, the boar's gore at its impact, herds spawned
                               #   on dry surface under the wildlife budget, no idle despawn
      WildlifeSpawnRules.cs    # GL-free: which species a surface spawn becomes for its biome, by weight
      SkyMobManager.cs         # the sky mobs: the galeraptor's strike at its dive impact, the golem's shockwave at its
                               #   slam impact, the sky-band ring spawn on island tops (tier-scaled packs, big-zone golems)
      SandwormGround.cs        # what the worm reads of the ground: the surface height, sand or not, desert or not
      SandwormManager.cs       # the sandworm boss: one at a time, summoned (by a dragonfly wing), hunts, bites, loot, retreat; client mirrors
      SlimeManager.cs          # slimes: hop AI tick, contact damage, loot, ring spawning (surface, and caves but the magma deep)
      Caves/                   # cave spawning by cave biome, namespace VoxelEngine.Simulation.Caves (see "Cave mobs")
        CaveSpawnSearch.cs     # the cave spawn search's parameters: ring radii, tries, heights, light limits, allowed biomes
        CaveBiomeResolver.cs   # the delegate the search asks a standing cell's cave biome through (a test seam)
        CaveSpawnRules.cs      # GL-free: the biome masks, slimes everywhere but the magma deep, the goblin weight per biome,
                               #   the cave mob kind per sub-biome, the spiders' share of every cave and the scorpions'
                               #   of the desert's (KindFor), the scorpion's surface biome and hour
        CaveMobManager.cs      # the cave mobs: the shardlings' shards (MobMissiles; damage + frostburn), the spore burst
                               #   (spore), the magmaborn's burning blow, the spiders' venom bite, the scorpion's pinch
                               #   and sting, loot, despawn, the ring spawn by cave biome, roll and tier, the rare night
                               #   spider on the surface and the desert's scorpions
      TntManager.cs            # owns the live primed TNT: spawns cubes where firestarter
                               #   strikes and blasts light TNT, ticks their fuses
      Tower/                   # the battle tower's simulation, namespace VoxelEngine.Simulation.Tower
        SkeletonManager.cs     # the skeletons: forced spawn only (Spawn(position), under the hard cap), the rake at
                               #   its impact, the arrows (MobMissiles), loot, despawn, client mirrors; no ring spawn; reports a
                               #   bound skeleton's despawn, leash release or idle crumble back to its piles (TakeReleased)
                               #   so its floor owes it
        TowerManager.cs        # the floor locks (see "Battle tower"): records materialized near a player, the recount, owed
                               #   and leash, the door judgement, the capped sweep, ClearFloor, Detail, stations.json and the wire;
                               #   the roof altar: JudgeAltar, BeginRitual, UpdateAltars/SettleAltars, the BossDefeated event;
                               #   a defeated tower's collapse (TowerRules.CollapseDue, saved with the tower)
        TowerCollapse.cs       # the staged, capped, resumable collapse of a defeated tower into its TowerRuin: a top-down
                               #   layer cursor under a scan budget, unloaded chunks waiting, the saved NextY/Waiting/Done
        SkeletonKingManager.cs # the Skeleton King: forced spawn only, one per tower (StatusFor), the slam shockwave and its
                               #   bone shrapnel, the bone volley (both MobMissiles), the summoned minions (capped), the
                               #   empty-server fade, client mirrors
        KingStatus.cs          # None / Alive / Killed (health 0) / Fading (a despawn with health left): what a tower's king is
        TowerAltarState.cs     # Dormant / Ritual / Awake / Defeated: a tower's altar
        AltarVerdict.cs        # Begin / Sealed / Stirring / Awake / Defeated / NoTower: what an altar click comes to
        TowerRecord.cs         # one tower: key, center, radius and a TowerFloorState per floor, the altar state, cell and the
                               #   king's home; Build (explicit data, the test seam), FromSite (the one PileCells/DoorCells scan)
                               #   and Mirror (the client's copy)
        TowerFloorState.cs     # one floor: band, piles, door cells and the ones still to sweep, living, owed, piles left,
                               #   alive and the door bit
        PileState.cs           # one skeleton pile: cell, waves left, cooldown, removed
        SkeletonPileManager.cs # a floor's piles: waves of TowerDifficulty.PackSize skeletons for a non-immune target in
                               #   range, then depleted; owed skeletons re-raised at any pile; never writes blocks
        TowerRules.cs          # GL-free: the pile trigger, floor awake, the door rule, spawn points round a pile, the leash,
                               #   the altar verdict (AltarVerdictOf), the collapse gate (CollapseDue)
        TowerBlockWriter.cs    # the capped writer past `unbreakable` (Chunk.CollapseWriteAt plus every edit-path hook): the
                               #   BT-4 doors and depleted piles (TryClear), BT-8's collapse (TryReplace, which spills a replaced
                               #   chest and tells the replaced hook, the collapse show); TowerWriteResult.cs is its answer
        TowerReadout.cs        # the look-at status line of a pile, a sealed door or the altar, and the altar's toasts
      TopplingTree.cs          # a felled tree: the wood TreeSupport finds cut off plus its canopy, meshed once by
                               #   CellBatchMesher (one mesh per chunk it spans), dropped as a rigid body (FellBody), then popped out as drops
      FellContacts.cs          # GL-free: per block, the points that touch the ground (thin wood reaches half its diameter)
      FellBranches.cs          # GL-free: a felled tree's main stem and side branches (which blocks snap off together)
      FellPart.cs              # one rigid piece of a felled tree: its blocks, FellBody and meshes (a branch snaps off into its own)
      FellBody.cs              # GL-free rigid body of a felled tree: point masses, gravity, stump pin, terrain contacts, settle
      TreeGrowthResult.cs      # the outcome of one TreeGrowthWriter call: its status,
                               #   the root cell, the added nodes and the written cells
      TreeGrowthStatus.cs      # what one TreeGrowthWriter.Grow call did
      TreeGrowthWriter.cs      # reactive growth on the live world: rebuild a column's tree from its wood
                               #   state bytes (or plant a sapling), GrowFurther in a WorldTreeSpace, write
                               #   the difference back; TreeGrowthResult.cs / TreeGrowthStatus.cs are its result
      Waystones/               # the waystone registry and discovery, namespace VoxelEngine.Simulation.Waystones (see "Waystones")
        DiscoveredWaystones.cs # GL-free: one player's discovered waystones (cell + name at discovery) and their version
        WaystoneDiscovery.cs   # what one player's activation came to: the record, may rename, new to the player
        WaystoneManager.cs     # the world's registry keyed by the lower half's cell: lazy records, first discoverer, rename,
                               #   lost marking (break hook + sweep), either half -> its cell, stations.json Capture/Restore
        WaystoneNames.cs       # GL-free: the mod's random name (4 consonant-vowel pairs) and the rename sanitizer
        WaystoneRecord.cs      # one registry row: name, first discoverer id, lost flag
        WaystoneRealm.cs, WaystoneRealms.cs  # surface / underground / hell / sky; GL-free Classify by the bands and the
                                              #   column's surface, and the list's tag
        WaystoneTeleport.cs    # GL-free teleport rule: IsAtWaystone, Judge (current, discovered, registry row, lost, mana over
                               #   half unless free) and TryPay (drains); WaystoneTeleportVerdict.cs its answer
        WaystoneArrival.cs     # GL-free arrival search over IArrivalCells: the nearest free two-cell spot facing away, on top
                               #   of the stone, or Unloaded; ArrivalCell.cs, IArrivalCells.cs, ChunkArrivalCells.cs (the live
                               #   chunks), WaystoneArrivalSpot.cs and WaystoneArrivalStatus.cs are its seam and answer
        WaystoneTravel.cs      # World component: the local trip, holding the camera over an unloaded destination, then landing;
                               #   a client's hold on the spot the server landed it on (BeginLanding)
        WaystoneArrivalHold.cs # GL-free: a server player's trip waiting for its destination (HoldEye; Step: the spot once
                               #   loaded, on top after the timeout)
    Status/                    # status effects at run time (MG-4b), see "Status effect schema"
      StatusEffectSet.cs       # GL-free per-target set: apply (refresh, never stack), tick (DoT, block actions), the
                               #   lowest move/attack factor, the client mirror mask, the tint
      StatusEffects.cs         # the one Apply API (a Mob, an IMobTarget, a PlayerHealth), authoritative only
      StatusEffectBlockRule.cs # GL-free: which cell an ignite/freeze/thaw converts, and whether it holds something up
      StatusEffectBlockWriter.cs # the authoritative block writes with every edit hook, capped per pulse and per frame
    UI/                        # UI state, layout and rules, mostly GL-free (UIRenderer's page renderers in Rendering/Overlays draw them): canvas, font, settings
      BitmapFont.cs            # TrueType font baked into a GL atlas; CPU glyph layout (printable ASCII, any run length, a cap-box-centered layout)
      GameSettings.cs          # player settings with JSON persistence (settings.json)
      Primitives.cs            # Vertex-layout mesh factories for simple shapes
      ScrollList.cs            # scrolled equal-height row math: clamp, visible range, row at a point
      TextFit.cs               # GL-free single-line fit: shrink to a floor, then an ellipsis, reporting whether it cut
      TextWrap.cs              # GL-free word wrap against a measure delegate (+ WrapRanges: the same lines as slices of the source)
      UiTheme.cs               # the spacing scale, border widths, text sizes and palette every list-based UI draws with
      RarityStyle.cs           # how a rarity dresses an item: its color and chrome layer strengths (RarityStyleConfig);
                               #   the one table the [E] rows, chest rows and tooltip share; Common has no chrome
      RarityChrome.cs          # GL-free twin of ui-fragment.glsl's rarity chrome: packs the uChrome* uniforms (For)
                               #   and evaluates the border, glow, wash, sheen and sparkles (Shade)
      UiCanvas.cs              # the virtual UI canvas: reference size, uniform scale, scissor rects
      Widgets/                 # GL-free widget layouts the renderers only draw: UiRect, PanelFrameLayout, ButtonLayout (+ WidgetState),
                               #   TabStripLayout, ListRowLayout, TextBoxLayout, ScrollViewLayout, TooltipLayout; the kit the [E] panel,
                               #   craft card, spell builder and the gear and chest panels build on (plans/INVENTORY_UI.md)
      Forge/                   # the anvil's recipe list, GL-free (ForgeRenderer draws it, ForgeInput drives it)
        ForgeView.cs           # open flag, the anvil, its metal stack and the recipes offered for it, hover, transition
        ForgeLayout.cs         # canvas geometry of the panel, the card grid and CLOSE, and HitAt; ForgeHitKind.cs
      Hud/                     # HUD widgets: chat, clock, compass, crosshair state,
                               #   toasts, hotbar feedback, look label, tooltips, screenshots
        BossBarState.cs        # what one frame's boss bar draws: the boss's name and health share (BossSelection)
        Chat.cs                # ChatKind/ChatMessage (with its optional ChatLink), ChatView (log/input/history, Suggest, the
                               #   Completion) and ChatLayout (with the completion popup's geometry)
        ChatCompletion.cs      # GL-free command autocomplete of the chat input: Sync, Tab cycling (Cycle), the ghost text
        ChatLink.cs            # a clickable span of a chat line and the command a click suggests (FitsIn)
        ChatLogLayout.cs       # GL-free chat log layout shared by the draw and the click hit-test: the shown messages, their
                               #   wrapped lines and where a link lies on every line it spans (LinkAt)
        ChatLogLine.cs         # one laid-out log line: its message, text, top, alpha and its part of the link
        Clock.cs               # pure clock math behind the HUD's held-clock readout (see TopStripRenderer.DrawClock)
        Compass.cs             # HUD compass heading math
        CrosshairState.cs      # throw charge, break progress and hit marker, handed to the UI pass
        DayToast.cs            # the dawn watch behind "Day N"
        HitMarker.cs           # the crosshair's connecting-hit X: its flash clock and fade
        HotbarNameFade.cs      # the selected item's name over the hearts after a switch
        HotbarPickupPop.cs     # the bump a hotbar slot gets when its count rises
        PickupFlyIcons.cs      # GL-free ring of collected-drop icons arcing into their hotbar slots
        HudToast.cs            # one hold-then-fade HUD line (the day toast, a first find, packs full)
        ItemTooltip.cs         # the hover panel's lines, built from the item's own fields; Hover packs them with the rarity
        HoverTooltip.cs        # the tooltip one frame shows: anchor, lines, line colors and the hovered item's rarity
        LookLabel.cs           # the look-at label's rule: name, "Unknown", the mineable flag
        LookLabelState.cs      # what one frame's label draws: text, flags, cell, state byte, light, detail line, station bars
        StationBar.cs          # one progress bar (caption, fraction, fuel/progress/done kind)
        StationBars.cs         # GL-free builders of the bars: campfire fuel + cook slots, kiln, bloomery, anvil, mold
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
        InventoryPanelLayout.cs # [E] panel geometry, tabs, the search field and its scope button, the catalog buttons, rows
                               #   and scrollbar hit-testing
        InventorySaveCodec.cs  # Inventory's player.json mapping (Export/Import forwarders), HandledComponentTypes
        InventorySearch.cs     # GL-free matching of owned items and recipes behind the search field (survival: only the
                               #   recipes the Craft tab lists), narrowed by a SearchScope
        InventoryView.cs       # [E] panel selection/scroll/focus state, the search scope; ResetForNewWorld between worlds
        PanelTabLabels.cs      # the tab strip's titles and counts (survival's Craft count names no recipe total)
        RowEquip.cs            # equipping a listed row: the right-click (gear worn or taken off, a tool or a stackable into
                               #   the selected hotbar slot), a drop onto a hotbar or equipment slot, and whether a row is
                               #   on the hotbar or worn; returns the intent that mirrors a change
        RowDrag.cs             # a pressed [E] row or hotbar slot that lifts into a drag once the pointer leaves the press
        EquippedMark.cs        # how a listed row shows it is equipped: the hotbar key chip, "worn", the green bar
        StaffComponent.cs      # a staff's bound spells (part keys per slot) and selected slot, immutable
        PanelTransition.cs     # the [E] panel's open/close easing: eased alpha and slide from a self-clocked progress
        SearchFieldState.cs    # a dropdown search field's query, suggestion cursor and scroll
        SearchScope.cs         # what the [E] search lists: items and recipes (default), items only, recipes only
      Menus/                   # main, pause and settings menus, the loading screen, text fields
        LoadingScreen.cs       # LoadingScreenView (eased monotone progress, fade-out) + LoadingScreenLayout (spinner)
        MainMenu.cs            # main-menu pages, hit kinds, fields, keys, actions and MainMenuView
        MainMenuController.cs  # every main-menu page transition, key rule and WorldStore call (tested)
        MainMenuLayout.cs      # main-menu geometry + HitAt: buttons, scrolled world rows, fields, delete popup
        PauseMenu.cs           # pause menu pages, hit kinds, view and PauseMenuLayout (RESUME/SETTINGS/SAVE & QUIT)
        RespawnPrompt.cs       # GL-free death screen rule (shows after the death plays, one respawn request per death) + DeathScreenLayout
        SettingsMenu.cs        # settings page rows, tabs, sliders and SettingsLayout (shared by both menus)
        TextFieldState.cs      # one single-line text field: printable-ASCII typing rules, length cap, caret and selection
        TextFieldEditing.cs    # the editing keys every field shares (arrows, Home/End, Ctrl words, Shift select, Delete, Ctrl+A/C/X/V; Up/Down jump only where the owner asks), over an IClipboard
        WordBoundary.cs        # the word rule of Ctrl+arrows and Ctrl+Backspace/Delete
      SpellBuilder/            # the spell table's builder: GL-free state, catalog, layout and text (SpellBuilderRenderer draws it)
        SpellBuilderCatalog.cs # the four part columns (element, shape, modifier, trigger) and the known parts of a kind
        SpellBuilderHit.cs     # one hit-test result: kind, column, row or slot
        SpellBuilderHitKind.cs # None / Panel / Part / Slot / Bind / Clear
        SpellBuilderLayout.cs  # virtual-canvas geometry shared by the draw and HitAt: panel, slots, part rows, stats, buttons
        SpellBuilderText.cs    # the title, the hold-a-staff hint and a phrase per SpellVerdict
        SpellBuilderView.cs    # open flag, transition, chosen slot, the selection (one element/shape/trigger, any
                               #   modifiers), live stats cached per Version; Refresh rebuilds only on a knowledge change
      Quests/                  # the [P] quest book, GL-free (QuestBookRenderer draws it, QuestBookInput drives it)
        QuestBookLayout.cs     # grid <-> canvas, zoom about the pointer, pan by a drag, the node hit test, the details panel
        QuestBookView.cs       # open flag, transition, the bound pan/zoom, the selection, the press and drag
        QuestText.cs           # the fixed strings and the requirement lines
      Waystone/                # the waystone screen, GL-free (WaystoneRenderer draws it, WaystoneInput drives it)
        WaystonePage.cs        # Closed / Discovered (the popup) / Rename (the dialog) / List
        WaystoneView.cs        # the page, the waystone, its name and rename right, the rename field, search, scroll, the cached
                               #   rows and the mana footer state; Confirm, BeginRename, FinishRename, Back (Escape)
        WaystoneRow.cs, WaystoneRows.cs  # one list row (name, realm, distance, lost, current); Build: filter, order
        WaystoneLayout.cs      # canvas geometry of the popup, dialog and list panel, and HitAt; WaystoneHit.cs, WaystoneHitKind.cs
        WaystoneText.cs        # the labels, the mana footer rule and a toast per refused trip
    VisualTest/                # the VOXEL_SCRIPT scenario runner: parsed scenario, director, scripted keyboard, PNG capture
      ScriptedKeyboard.cs      # an IKeyboard whose pressed keys are set by code instead of the OS
      VisualCapture.cs         # reads the finished frame back from the GPU and writes it as a PNG
      IScenarioUi.cs           # what a scenario may do to the live UI (panel, tab, search, row, hover, spell builder, waystone, forge, chat)
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
      NoiseCaves.cs            # 3D-noise caves (cheese rooms, wide/narrow spaghetti, deep caverns) on a strided lattice,
                               #   nine channels (the ninth the cave sub-biomes' shape scale, seed offset 182)
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
      TerrainSites.cs          # the shared hashed-site helper used by lakes, massifs, peaks and the sky islands: cell
                               #   range, roll, wobbles, rim samples, lowest-hash spacing (fixed, or by rolled size)
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
      Caves/                   # the overworld cave biomes (plans/CAVE_BIOMES.md), namespace VoxelEngine.WorldGen.Caves
        CaveBiome.cs           # Plain / FrozenEcho / DesertEcho / Crystal / Fungal / MagmaDeep, apart from the surface Biome
        CaveCellRole.cs        # Interior / Wall / Floor / Ceiling: where a rock cell sits against the open cave
        CaveBiomePalette.cs    # one cave biome's blocks: base stone, floor, ceiling, an accent and its rate
        CaveBiomes.cs          # the field (three 3D noises, seed offsets 179-181, on a stride-8 lattice), the one
                               #   Classify rule, the point query At, Fill (the per-chunk map), the palette pass Stamp
                               #   (stone hosts only) and its band
        CaveBiomeMap.cs        # one chunk's cave biome per cell plus a mask of the biomes present, classified once by
                               #   CaveBiomes.Fill before the carvers and read by every cave consumer
        CaveShape.cs           # the sub-biomes' cave shape: cheese, tunnel and cavern boosts, the worm radius factor
        EyeCaveBiomeProbe.cs   # the cave biome at one player's eye over an IWorldSource, memoized per cell (CB-3;
                               #   singleplayer and the server only)
        CaveRichness.cs        # a Y-band ore's threshold shift per cave biome (negative richer)
        CaveOres.cs            # the exclusive ores (amethyst, jade, cinnabar; seeds 183-185): HasOre, the stamp (plain
                               #   stone of its own biome, one merged host scan) and its band
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
        CloudLayer.cs          # one solid cloud layer's shape (band, cover and detail noise, threshold, core): the
                               #   column rule and the air-only stamp the cloud patches and the cloud sea share
        CloudPatchGenerator.cs # the solid cloud patches in the CloudSlab band: a CloudLayer over a 2D patch noise
                               #   (seed 166) and a detail noise (seed 167); TryGetColumn is the one rule IsCloud and
                               #   the stamp share; into air only
        CloudSeaGenerator.cs   # the thick, nearly continuous cloud sea under the islands (290..301): a CloudLayer
                               #   over a broad cover noise (seed 172) and a detail noise (seed 173)
        SkyBiome.cs            # the sky biomes (L-13): Meadow, FrostHighland; apart from the surface Biome enum
        SkyBiomes.cs           # the sky biome at a column (a 2D noise, seed 174; an island takes its center's) and
                               #   each biome's palette; the column hash the flora scatters by
        SkyFlora.cs            # an island column's sparkle top, the plant over it and the vine under it: part of the
                               #   one column rule BlockAt and the stamp share
        SkyLandmasses.cs       # the great sky landmasses: sites, the warped elliptical column rule, the zone and island overlap tests
        SkyIsland.cs           # one accepted island: center, nominal radius, base height, dome, underside depth
        SkyIslandGenerator.cs  # the island column shape (domed top, tapering underside, stalactites), the point query
                               #   (BlockAt, with the flora) and the stamp, which collects a chunk's few islands once
        SkyIslandPalette.cs    # what a sky biome builds its islands of: top, soil, core, the sparkle, plants, vine
                               #   rate and tree species
        SkyTrees.cs            # sky trees: one per 16x16 cell rooted on an island's plain top, a pure function grown
                               #   in a SkyTreeSpace and cached (the twelfth GenerationalCache), stamped after the islands
        SkyTreeSpace.cs        # the ITreeSpace a sky tree grows in: its own cell's columns, the band's open sky only
        SkyIslands.cs          # where the islands are: hashed zones (ZoneAt, TryGetZone, tiers), hashed islands everywhere
                               #   in the band, denser inside zones (TryGetIsland, CollectIslands, MaxIslandsPerBox); the
                               #   outline/top/stalactite noises
        SkyZone.cs             # the zone at a point: weight, tier (bigger is rarer; L-15 spawns by it), radius, center
      WorldgenStats.cs         # lock-free worldgen timing from the generation workers and
                               #   the LOD builder, drained once a second by FrameProfiler
      WormCarver.cs            # worm-carved tunnels: per-XZ-cell deterministic paths, cached, carved sphere by sphere;
                               #   a node's radius times its center's cave biome factor (RadiusFactorAt)
      Structures/              # template placement (see "Structure templates and ruins")
        PlacedStructure.cs     # a template placed in the world: center column, origin y, turns, footprint box
        RuinPlacement.cs       # the ruins of the four realms on hashed cells, and the realm site rules satellites share
        StructureLocation.cs   # where StructureLocator found a structure: its center and ground floor, the landing beside it,
                               #   the distance
        StructureLocator.cs    # /locate: the nearest tower from worldgen within TowerLocateRings, exact by roll distance,
                               #   and the landing beside it; main thread, never on a client
        StructureStamper.cs    # writes a template part inside one chunk, clipped at its border and to a y window
        WaystonePlacement.cs   # a waystone satellite a short walk from a placed ruin, by the ruin's realm rule
        TowerPlacement.cs      # the battle towers (plans/BATTLE_TOWER.md): 1536-block hashed cells (salt 0x5E7F), the cached
                               #   site verdict, the band, the per-chunk stamp, TryGetSite/TryFindAt/Meets/TopReaching
        TowerSite.cs           # one accepted tower: center, ground, radius, floors, palette, dressing picks, and the BT-4/6/8
                               #   hooks (FloorY, RoofY, Tier, TreasureTier, DoorCells, PileCells, AltarCell, BottomY/TopY,
                               #   chunk range)
        TowerShell.cs          # GL-free pure shell geometry: the rings, the helical half-step stair, doorways, slabs, parapet,
                               #   the closed tower_door cells (IsDoorStep, closed-form)
        TowerDome.cs           # GL-free pure geometry of the roof's forcefield dome: a half spheroid on the parapet's top,
                               #   its 6-tight shell (Holds), BaseY/TopY
        TowerGenerator.cs      # merges the shell with the floor and altar templates and the dome's forcefield; the point
                               #   query CellAt, StampSite and PileCells (a floor template's skeleton piles),
                               #   ChestCells/AltarChestCells (the tower chests, each carrying its floor's tier in its state)
        TowerPalette.cs        # the biome palette swap: tower_bricks / _sandstone (desert) / _frost (frozen, taiga), the step
                               #   slab, the door, pile, chest, altar and forcefield blocks
        TowerRing.cs           # Outside / Apron / OuterWall / Corridor / InnerWall / Room
        TowerRuin.cs           # GL-free, pure: what BT-8's collapse leaves of a tower cell (RuinAt: hashed wall stumps, brick
                               #   rubble, floor 0's dressing kept, gravel and cobblestone piles on the ground slab; salt 0x5E80)
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
          VulcaniteGenerator.cs # vulcanite ore veins over basalt only (the barrier band, the Magma Caverns), denser by the lava
                                #   (LavaProximity lowers the OreNoiseLattice threshold; seed offset 186)
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
        TreeStump.cs                          # what a trunk cut above its root leaves: the root cell as the wood's stump (FellWood)
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
  assets/data/recipes/forge/<out>.json # an anvil recipe: metal + metalCount stacked on the anvil -> result + count
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
```

`WorldGen/Caves/CaveEchoVolumes.cs`: deterministic sites that extend Frozen/Taiga and Desert echo materials through solid rock; shared point/map containment and echo policy.

`WorldGen/Caves/DesertCaves.cs`: bounded desert rooms with broad scale/aspect/depth variation and coherent inward/outward wall detail, composed with elongated noise tunnels.

`WorldGen/Features/Underground/CaveFluidSources.cs`: shared, spaced 3D source-group candidates for cave water and lava.

`WorldGen/Features/Underground/CaveLavaLakes.cs`: seed-owned bounded floods of natural cave depressions, flat contained source surfaces, depth-dependent size budgets, cross-chunk plans and local writes.

`WorldGen/Features/Underground/CaveTerrainProbe.cs`: private pure completed-terrain queries without lakes/cave life, sharing production generation with independent scratch/maps and no generation-statistics side effects.
