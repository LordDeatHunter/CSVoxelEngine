# Layout, part 2: Graphics through Rendering

File tree of `OpenGLTest/src/` (part 2 of 4): Graphics, Interaction, Items, Juice, Lod, Magic, Networking, Particles, Platform, Player, Rendering.

```
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
      ChatCommands.cs          # the chat command table: /help, /tp, /give, /time, /locate and their rights, the shared
                               #   /locate reply (its coordinates linked) and the /tp suggestion (TeleportCommand)
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
      RightClickOutcome.cs     # what claimed a right-click: a station, a chest, the spell table, a waystone, a tower altar, the item's use, a placement or a cast
      SelectionOutlineMesh.cs  # GL-free selection outline: the 12 box edges as thin beams (no glLineWidth)
      SelectionPulse.cs        # GL-free twin of the outline's breathing opacity (steady while mining)
      SliceCombo.cs            # the blade's two-phase slice combo: press/queue/advance state
      SwordCombo.cs            # the sword's cut / back cut / chop chain: press/queue/advance state and the
                               #   window after a swing in which the next press continues the chain
      SwordSwing.cs            # the three sword swings, in chain order
      SwordSwings.cs           # GL-free per-swing rules: the chain's successor, power, targets, cone vs
                               #   hemisphere, shove and hit-stop weight
      TallBlock.cs             # GL-free two-cell block rules (a door, a waystone): each half's partner cell
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
      ItemRegistry.cs          # every item by shared id: pure items (vine, branch, flint, ...) plus
                               #   one BlockItem per placeable block
      ShrapnelProfile.cs       # GL-free one hit's bone-shrapnel payload and the rule that resolves it from the
                               #   held weapon and the worn bone_shrapnel ability
      LearnPartsItem.cs        # a teaching scroll (learn_parts): teaches its parts, spent once, refused when nothing is new
      RestoreManaItem.cs       # a mana draught (restore_mana): refills a share of the pool, spent once, refused at a full pool
      ItemUseResult.cs         # outcome of Item.OnUse: did the use take effect, what
                               #   the tool morphs into, and whether the player punches
      PlaceStackItem.cs        # builds up a multi-state block (flint or obsidian on a pebble pile)
      PlainItem.cs             # a plain pickup: no right-click behavior (vine, branch, compass)
      PlantTreeItem.cs         # plants a sapling of the item's Item.UseTreeName species
                               #   on the aimed ground (an acorn on dirt or grass)
      StarterKit.cs            # the hint scroll a new survival player (and a server newcomer) starts with
      SummonItem.cs            # the thumper: wakes the boss use.entity names at the aimed cell (the sandworm), desert sand only
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
      ChatMessageCodec.cs      # one ChatMessage frame (kind, sender, text, optional link), its one writer and reader;
                               #   a link that does not fit its text is a malformed frame
      ChunkNetCodec.cs         # the chunk payload (mode byte, then one id | id palette + index bytes | ushort per
                               #   cell, then the state; malformed -> InvalidDataException) and column terrain
                               #   (heights, water levels, corner climate), Deflate-compressed
      InventoryIntent.cs       # one client inventory intent (select, drop, equip, craft, creative grant/clear, pick)
      InventoryIntentCodec.cs  # its bounds-checked wire form
      InventoryNetCodec.cs     # the server-owned inventory on the wire (the save JSON shape)
      LodNetCodec.cs           # a far-LOD region's mesh on the wire
      NetAttacks.cs            # what each NetAttackKind stands for: the attack pattern it needs in hand, the
                               #   sword swing it names, the NetPlayerAction it is rebroadcast as
      NetCellChangeCodec.cs    # one BlockChanges/BlockCorrection cell on the wire (the server writes, the client reads)
      NetEntity.cs             # NetEntityKind/NetEntity (ItemId: a drop's, TNT's or falling block's id, or a goblin's
                               #   torches or a skeleton's tower tier) and NetCellChange
      NetEntityCodec.cs        # one EntityList entry on the wire (the server writes, the client reads)
      NetPlayerAction.cs       # the swing a player just made, as the server rebroadcasts it in each
                               #   player list entry (with a counter that moves on every new one)
      NetPlayerFlags.cs        # the PlayerState flags byte (creative, flying), a request to the server
      NetProtocol.cs           # NetMessage ids, length-prefixed framing, PacketWriter/PacketReader
      PlayerStatusCodec.cs     # PlayerStatusData (modes, health, dead, knockback) and its bounds-checked wire form
      ServerAddress.cs         # host[:port] parsing and formatting (the default port)
      StationNetCodec.cs       # the StationState snapshot: campfires, querns, kilns, cast molds
      WaystoneNetCodec.cs      # the waystone messages: the rename request, a player's rows (WaystoneNetRow) and the
                               #   activation answer (WaystoneOpened), bounds-checked
      WireIds.cs               # the server's id name tables in its hello and the client's translation of every wire id
      WorldIntent.cs           # one client world intent (break start/complete, place, use, throw, attack, cast, hammer,
                               #   waystone trip)
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
        WaystoneMirror.cs      # GL-free: the waystone rows the server sent, its activation answer and the trip awaiting
                               #   its landing correction
      ServerSide/              # the server's side of a session: the connected player and the rules its intents and
                               #   moves are judged by (the dedicated server itself is OpenGLTest.Server, VoxelEngine.Server)
        AttackPacing.cs        # GL-free: the shortest gap from one attack to the next (a punch's pace, a
                               #   slice's swing, the sword swing before the one asked for)
        BreakProgress.cs       # GL-free: a player's mining on the server, judged against the held item's hits
        InventoryIntentRules.cs # GL-free: what the server accepts of an intent, against that player's inventory
        MovementRules.cs       # GL-free movement rubberband: speed clamp, then collision, rise and hover checks
        MovementState.cs       # a player's per-connection movement memory (airborne-still time, the airtime rise budget's
                               #   climb, takeoff and bounce, the lift credit)
        MoveVerdict.cs         # Accepted / Clamped / Rejected
        NetValidation.cs       # server movement, edit-reach, intent-eye and visible-face sanity rules
        OperatorList.cs        # who may be creative: ops.json in the world folder plus --op, matched by save slug
        RateLimiter.cs         # GL-free token bucket (a player's edit rate)
        ServerPlayer.cs        # a connected player on the server: own Inventory, creative/flight flags, pose,
                               #   its PlayerContext, newest sequence id, mining progress, edit rate, attack pace,
                               #   cast pacing (Casting), its PlayerHealth/fall tracking/respawn eye, mob-hit sink,
                               #   correction numbers, its open chest, the waystone it stands at, a waiting trip
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
      SandwormEffectRules.cs   # GL-free numbers of the worm's sand and earthquake: chips and speed by closeness, the quake by distance
      SandwormEffects.cs       # the worm's cosmetic show: thump rumble, surfacing sand, breach boom, dive splash, dash dust, quake
      SliceTrail.cs            # GL-free ring of blade-tip samples in the swinging player's owner frame, plus a live head
      SliceTrailGlow.cs        # GL-free twin of slice-trail-fragment.glsl: the soft body, the white core, the streak lanes
      SliceTrailRenderer.cs    # the additive ribbon draw of one SliceTrail (slice-trail-*.glsl)
      SliceTrailRibbon.cs      # GL-free builder of the tapered, age-faded Catmull-Rom ribbon quads of a SliceTrail
      SliceTrailSystem.cs      # World component: feeds the local and remote players' slice trails; AssumedBlade
      TreeShudder.cs           # GL-free: the leaf cells above and around a chopped trunk cell, thinned to a few, that an axe hit shakes loose
      WaterSplash.cs           # GL-free splash rule (droplets by crossing speed) + Track: a body's droplets off the water top
      RippleSurface.cs         # GL-free: the water cells (at the crossing's level) a ripple ring may spread over, so it stops at the water's edge
      WaterSplashTracker.cs    # GL-free per-body water crossing memory: in water last frame, speed from its own height
      Collapse/                # a defeated tower's collapse show (never on a server)
        CollapseDebris.cs      # one block thrown off the tower: tumbles, bounces and rolls on the terrain (read-only), then bursts
        TowerCollapseEffects.cs # World component: the chips, thrown blocks and rumble of a defeated tower's falling cells,
                               #   fed by TowerBlockWriter's replaced hook or a client's journal (ShowsCollapse)
      Heave/                   # the Skeleton King's slam heaving the ground (never on a server)
        GroundHeave.cs         # GL-free: the full-cube ground round a strike pops up and settles in an outward ripple; no write
        HeavedBlock.cs         # one heaving block: its cell center, block, biome tint, start and height
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
                               #   landing (bounce or softLanding), ground within a reach, a bounce block below; what the
                               #   server judges a remote player's body by
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
                               #   the slime mesh and skin, one RigMeshes per clip-animated kind, SlimeTransform,
                               #   a rigged kind's held item (EntityType.HeldMatrix) at its hand
        RigMeshes.cs           # one clip-animated kind's rig, part meshes (ordinal-sorted) and skin, PartMatrix
        ObjectPasses.cs        # drops, thrown items, primed TNT, falling blocks, mining debris, a collapsing tower's
                               #   thrown blocks (RenderCollapseDebris) and toppling trees
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
        ChatRenderer.cs        # the chat HUD: draws the log from ChatLogLayout (link runs colored and underlined) and the
                               #   input line; LinkCommandAt, the click hit-test over the same layout
        CraftCardRenderer.cs   # the [E] panel's crafting card and its hit-tests over the last drawn
                               #   CraftCardLayout
        DamageNumberRenderer.cs # draws the floating damage numbers (DamageNumberPool)
                                #   as world-space text that always faces the camera
        DamageVignetteRenderer.cs # full-screen damage vignette: one oversized triangle with
                                  #   a radial fragment gradient, drawn inside the UI pass
        EntityHealthbarRenderer.cs # five hearts floating above every live mob, always facing the camera
        ForgeRenderer.cs       # the anvil's recipe list: title, the stacked metal, a grid of recipe cards (result icon, rarity
                               #   name, metal cost) with a hover look, CLOSE; geometry from ForgeLayout
        HudRenderer.cs         # crosshair, hearts, hotbar and its feedback, toasts, FPS and profiler, tooltip,
                               #   player list, the status effect icons (DrawEffects); owns the crosshair, heart and
                               #   effect icon sprites
        InventoryPanelRenderer.cs # the [E] panel: tabs, search field and scope button, rows, creative catalog
                                  #   buttons (on the tab's list and on creative item hits), preview, and its hit-tests
        RarityChromeRenderer.cs # the rarity chrome over a list row or the tooltip box: one uChrome quad of the ui
                                #   program (RarityChrome.For's uniforms), reset to 0 right after; Ui.Chrome
        LoadingScreenRenderer.cs # the loading/saving screen and the fade over the first playing frames
        MenuRenderer.cs        # the pause and settings menus and every main-menu page, with their hit-tests
        NoticePipRenderer.cs   # draws the "!" a goblin pops over its head the moment it first notices a player
        SelectionPasses.cs     # the mining and placement pop (RenderBlockPop), the selection outline and face
                               #   highlight (RenderBlockSelection) and the crack overlay (RenderBlockBreaking)
        SpellBuilderRenderer.cs # the spell table's builder page: staff slots, the known parts by kind, the live
                                #   cost/verdict/fizzle/factors, BIND and CLEAR (or the hold-a-staff hint)
        WaystoneRenderer.cs    # the waystone screen: the discovery popup, the rename dialog and the teleport list (rows with realm
                               #   tag and distance, lost rows dimmed, search field, the mana footer)
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
```
