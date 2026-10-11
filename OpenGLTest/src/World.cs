using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace silkgltriangle;

/// <summary>
/// Main world class that manages the camera, chunks, and world generation. Implements
/// <see cref="IItemUseWorld"/> once so every <see cref="Item"/> override runs its effects here
/// instead of growing a branch in the world.
/// </summary>
public class World : IItemUseWorld
{
    // How far the player can target blocks (10 blocks)
    public const float InteractionReach = 10f;

    // Seconds of holding left-click per bare-hand mining hit. Each hit removes the held
    // tool's power from the block's hardness; time short of a full interval deals nothing
    // (creative breaks instantly). A tool declares its own pace through item JSON.
    public const float BlockHitSeconds = Mining.DefaultHitGapMs / 1000f;

    // A hit makes the block jump out and settle back. Kept below BlockHitSeconds so the
    // pop always finishes before the next hit restarts it.
    public const float BlockPopSeconds = 0.15f;

    // Absolute growth in blocks the mined block gains at the peak of a hit, added to the
    // block's hitbox on every axis rather than multiplied, so tiny blocks pop by the same
    // amount as full cubes instead of barely moving
    public const float BlockPopGrow = 0.12f;

    // A placement pops the block in: it appears a little too big and settles into its cell
    // with one small overshoot (PlacePop), so a placement reads differently from a mining hit
    public const float PlacePopSeconds = 0.22f;
    public const float PlacePopGrow = 0.1f;

    private readonly ChunkManager _chunkManager;
    private readonly ChunkGenerationWorker _generationWorker;
    private readonly LodManager _lodManager;
    private readonly DropManager _dropManager = new();
    private readonly TntManager _tntManager = new();
    private readonly FallingBlockManager _falling = new();
    private readonly SlimeManager? _slimes;
    private readonly GoblinManager? _goblins;
    private readonly SaplingManager _saplings = new();
    // The natural and hard mob caps every manager above spawns under, counted over all of them.
    private readonly MobCaps _mobCaps = new();
    // The players the mob managers chase this frame, rebuilt right before they run.
    private readonly List<IMobTarget> _mobTargets = [];
    private readonly FluidManager _fluidManager = new();
    private readonly LeafDecayManager _leafDecay = new();
    private readonly TreeGrowthWriter _treeGrowth = new();
    private readonly LeafParticleSystem _leaves = new();
    private readonly BlockDebrisSystem _debris = new();
    private readonly GroundDust _dust = new();
    private readonly CampfireManager _campfires = new();
    private readonly QuernManager _querns = new();
    private readonly KilnManager _kilns = new();
    private readonly CastingManager _casting = new();
    private readonly FireParticleSystem _fire = new();
    // Embers and smoke over exposed lava: a separately tuned instance so a lava lake never
    // competes with campfires for spawn rate or ring slots
    private readonly FireParticleSystem _lavaFire = new(WorldGenerationConfig.LavaEmberPerSecond,
        WorldGenerationConfig.LavaSmokePerSecond, WorldGenerationConfig.LavaParticleMax,
        new Vector3(0.5f, FluidState.SourceHeight, 0.5f));
    private readonly List<Vector3> _lavaVents = [];
    private readonly List<(float dist2, Chunk chunk)> _lavaVentChunks = [];
    // Torch flames: a third instance with tiny, tight particles spawned exactly at each flame point
    // (Chunk.FlamePoints, already world-exact, so no spawn offset)
    private readonly FireParticleSystem _torchFire = new(WorldGenerationConfig.TorchEmberPerSecond,
        WorldGenerationConfig.TorchSmokePerSecond, WorldGenerationConfig.TorchParticleMax, Vector3.Zero,
        WorldGenerationConfig.TorchFlameJitter, WorldGenerationConfig.TorchFlameScale);
    private readonly List<Vector3> _torchFlames = [];
    private readonly List<(float dist2, Chunk chunk)> _torchFlameChunks = [];
    private static readonly Comparison<(float dist2, Chunk chunk)> NearestVentChunkFirst =
        static (a, b) => a.dist2.CompareTo(b.dist2);

    // Every cell whose flame the particle system and the point lights draw: campfires plus kilns,
    // rebuilt once per update so both consumers read one list
    private readonly List<Vector3> _fireCells = [];
    private readonly BubbleParticleSystem _bubbles = new();
    private readonly SfxManager? _sfx;

    // Where edited chunks, the far-LOD edit state, the player and the entities are persisted; null = no
    // persistence (tests, VOXEL_SCRIPT runs, and the game until the menu attaches one)
    private readonly ChunkSaveStore? _saveStore;

    // Readiness of the chunks around the player while Loading; null for a world that starts
    // Playing (no save store)
    private WorldLoadGate? _loadGate;

    // The player.json this world was opened with, kept until the Loading -> Playing flip has
    // applied its lighting clock; null when the world has no saved player
    private PlayerSaveData? _savedPlayer;

    // Server mode only: the player.json the hosted world was opened with (null for a new world),
    // kept for the whole session because the server camera is no player and must never be saved
    // as one. CapturePlayer writes it back with only the clock refreshed (players keep their own
    // inventories, see ServerPlayer).
    private readonly PlayerSaveData? _hostedPlayer;

    // What the first SaveAndDispose returned, so a repeated call reports the same thing
    private bool _disposeResult;

    // Saved mobs and drops (entities.json) not spawned yet: each waits for its chunk to load,
    // and the lot is discarded after EntityRestoreTimeoutSeconds of play (RestorePendingEntities)
    private readonly List<EntitySaveData.MobData> _pendingMobs = [];
    private readonly List<EntitySaveData.DropData> _pendingDrops = [];
    private double _entityRestoreTimer;

    // Scratch for CaptureEntities' nearest-first drop order, reused across saves
    private readonly List<(float dist2, ItemDrop drop)> _dropSort = [];

    // Seconds since the last completed autosave pass
    private double _autosaveTimer;

    // LodEditState.CombinedVersion when lod-edits.bin was last enqueued; -1 so the first
    // autosave always writes it
    private int _lodStateVersionSaved = -1;

    // Inventory is assigned in its own initialiser and never replaced, so the method group is the
    // same closed delegate every frame; passing `Inventory.CanAccept` inline at the DropManager call
    // allocated a fresh one per frame (PERF_TODO C7).
    private readonly Func<byte, bool> _canAcceptDrop;

    // Trees currently toppling over after their base log was mined
    private readonly List<TopplingTree> _toppling = [];

    // Other players on this client's server (multiplayer only); empty in singleplayer.
    private readonly List<RemotePlayer> _remotePlayers = [];
    private readonly HashSet<int> _remoteIdsSeen = [];

    // Multiplayer client: server-authoritative mobs by network id, and the reconcile scratch.
    private readonly Dictionary<int, Mob> _netMobs = [];
    private readonly HashSet<int> _netMobsSeen = [];
    private readonly List<int> _netMobRemovals = [];
    private readonly HashSet<Vector3> _netTouchedChunks = [];
    // Multiplayer client: the cells of a block-change batch that are lava hardening, for the steam
    private readonly List<Vector3> _netHardened = [];

    // Multiplayer client: the server's primed TNT by network id (display-only mirrors, so a cube
    // keeps its strobe clock across snapshots), its reconcile scratch, and the falling cubes of the
    // newest snapshot, rebuilt wholesale.
    private readonly Dictionary<int, PrimedTnt> _netTnt = [];
    private readonly HashSet<int> _netTntSeen = [];
    private readonly List<int> _netTntRemovals = [];
    private readonly List<(Vector3 Centre, byte BlockId)> _netFalling = [];
    private readonly List<(Vector3 Centre, byte BlockId)> _netFallingPrevious = [];

    // Null on the headless path (tests), where Now runs off the summed Update deltas instead
    private readonly IWindow? _window;
    private double _headlessTime;

    // True for a dedicated server: no local player, a headless chunk manager and no far LOD.
    private readonly bool _serverMode;

    // True for a multiplayer client: no worldgen and no local simulation; chunks stream from the server.
    private readonly bool _clientMode;

    // The window clock, or the headless one: every gameplay time read goes through this
    private double Now => _window?.Time ?? _headlessTime;
    private bool _hasSelectedBlock;
    private Vector3 _selectedBlockPosition;
    private Vector3 _selectedFaceNormal;
    private int _selectedBoxIndex;
    private bool _isBreaking;
    private Vector3 _breakingBlockPosition;
    private byte _breakingBlockId;
    private byte _breakingHeldId;
    private float _breakDamage;
    private float _breakHardness;
    private float _breakPower;
    private float _breakHitSeconds = Mining.DefaultHitGapMs / 1000f;
    private bool _breakAllowed;
    private float _hitTimer;
    private float _popElapsed = BlockPopSeconds;
    private float _placePopElapsed = PlacePopSeconds;
    private Vector3 _placePopPosition;
    private byte _placePopId;

    // Punch cadence: the left-click attack runs on the held item's mining pace. Seeded to
    // infinity while the button is up, so the first frame of a press punches at once, exactly
    // like the break timer's seeded first hit.
    private float _punchTimer = float.PositiveInfinity;
    private float _punchGap = Mining.DefaultHitGapMs / 1000f;

    // Slice attack (AttackPattern.Slice): the two-phase combo, one click-buffered return swing,
    // and the per-swing latch that lands its damage once at the impact frame.
    private readonly SliceCombo _sliceCombo = new();
    private bool _sliceImpactDone;
    // Previous frame's left-click state, so the slice combo can see a fresh press.
    private bool _attackWasPressed;
    private readonly List<ThrownProjectile> _thrown = [];
    // Every airborne projectile to draw and mirror this frame: the player's throws followed by
    // the goblins' stones, rebuilt after both updated. Drawing only - hit tests stay per owner.
    private readonly List<ThrownProjectile> _projectiles = [];
    private bool _throwCharging;
    private float _throwChargeSeconds;
    private byte _throwItemId;
    private float _throwBaseDamage;
    private bool _useCharging;
    private float _useChargeSeconds;
    private byte _useItemId;
    private float _contactTimer;
    public Camera Camera { get; }

    /// <summary>The blocky player body: walk-swung limbs, jabbing right arm.</summary>
    public PlayerModel Player { get; } = new();

    private Vector3 _prevCamPos;
    private bool _hasPrevCamPos;
    private byte _heldId;
    private bool _hasHeld;

    // Tool instance the item use dispatcher is currently running for (0 for stackable misc
    // items like flint). Read by the IItemUseWorld tail so overrides never thread it through.
    // Main thread only, set and cleared around each OnUse call.
    private int _activeToolInstance;

    // The player that use runs for (the IItemUseWorld tail's raycasts, creative flag, tool and
    // stack spending), set and cleared with _activeToolInstance; null outside a dispatch, which
    // falls back to the local player.
    private PlayerContext? _activeContext;
    private PlayerContext ActiveContext => _activeContext ?? LocalContext();
    private Inventory ActiveInventory => ActiveContext.Inventory;

    // The local player as a PlayerContext: this world's own inventory, aimed by its camera.
    // Refreshed from the camera by LocalContext() before every use, so it never allocates.
    private readonly PlayerContext _localContext;

    // The head ray the selection raycast fired along (Camera.LastAimDirection), which a client's
    // break intents carry so the server's first-person ray lands on the same cell.
    private Vector3 _selectedAimDirection = -Vector3.UnitZ;

    // The hit cell and face the last raycast placement went against, which a client's placement
    // intent names so the server places against the same face
    private Vector3 _lastPlaceHit;
    private Vector3 _lastPlaceNormal;

    // A connected client's creative mining pace: one block per ClientCreativeBreakSeconds while
    // the button is held (singleplayer mines one per frame). Infinity while released, so a click
    // breaks at once.
    private float _creativeBreakTimer = float.PositiveInfinity;

    // True once the player rests exactly on real ground at the origin spawn. The camera's
    // initial position is only a heightmap guess (pre-carve, see Camera.SpawnPosition), and
    // physics starts before the spawn chunks stream in (unloaded cells read as air), so
    // without this the player falls through and ends up buried once the terrain loads.
    private bool _spawnSettled;
    private readonly float _spawnX;
    private readonly float _spawnZ;

    /// <summary>The player's hotbar; its selected slot decides what block placing puts down.
    /// A new survival player starts with nothing; creative mode grants from the catalog. On a
    /// server it belongs to no one: every connected player carries their own
    /// (<see cref="ServerPlayer.Inventory"/>).</summary>
    public Inventory Inventory { get; } = Inventory.Empty();

    // The selected item's name over the hotbar, fed from Inventory's selection every frame
    private readonly HotbarNameFade _hotbarName = new();

    // The hotbar's pickup pops, fed from Inventory's slots every frame (a rising count pops)
    private readonly HotbarPickupPop _hotbarPops = new();

    // Floating damage numbers: from landed hits in singleplayer, from snapshot health drops on a client
    private readonly DamageNumberPool _damageNumbers = new(WorldGenerationConfig.DamageNumberCapacity);

    /// <summary>
    /// Server mode: the connected players, owned by the server and read once a frame by the drop
    /// pickup, which credits each player's own inventory (flagging
    /// <see cref="ServerPlayer.InventoryChanged"/>). Empty, and ignored, outside server mode.
    /// </summary>
    public IReadOnlyList<ServerPlayer> ServerPlayers { get; set; } = Array.Empty<ServerPlayer>();

    // Scratch for the server's per-player pickup, rebuilt each frame from ServerPlayers
    private readonly List<DropCollector> _dropCollectors = [];

    /// <summary>
    /// Where chunks stream: around every connected player on a server (<see cref="ServerPlayers"/>),
    /// and around the camera otherwise - in singleplayer, on a client, and on a server while nobody
    /// is online. Refreshed every frame right before the chunk update (<see cref="UpdateFocus"/>);
    /// the chunk manager and the generation workers both read it, so chunks load around each player
    /// and unload only outside every player's radius.
    /// </summary>
    public StreamingFocus Focus { get; } = new();

    // Scratch for the server's player eyes handed to Focus, so a frame allocates nothing
    private Vector3[] _focusEyes = new Vector3[4];

    /// <summary>
    /// The save store this world writes through, or null for a storeless world. A server keeps its
    /// players' files in it (<see cref="ServerPlayerSaves"/>); the world still owns its shutdown
    /// (<see cref="SaveAndDispose"/>), so nothing may be queued after that.
    /// </summary>
    public ChunkSaveStore? SaveStore => _saveStore;

    // First-cut survival: the HP pool plus the fall state feeding it. Landings of 5+
    // blocks bill (blocks-4)*10 HP, lava burns 10 HP/s on body contact, and death
    // respawns at spawn keeping the inventory. Creative and flight are immune.
    private readonly PlayerHealth _health = new();

    /// <summary>Current HP, 0-100, drawn as 10 hearts in the HUD.</summary>
    public int Health => _health.Health;
    private readonly FallTracker _fall = new();
    private Vector3 _respawnEye;
    private bool _hasRespawnEye;

    /// <summary>Where the world is in its lifecycle (see <see cref="WorldPhase"/>).</summary>
    public WorldPhase Phase { get; private set; }

    /// <summary>Whether the full game runs: input, physics, mobs, fluids and autosave.</summary>
    public bool IsPlaying => Phase == WorldPhase.Playing;

    /// <summary>False only while the world is still <see cref="WorldPhase.Loading"/>.</summary>
    public bool IsLoaded => Phase != WorldPhase.Loading;

    /// <summary>How far loading got, 0..1 and never decreasing; 1 for a world that never loaded.</summary>
    public float LoadProgress => _loadGate?.Progress ?? 1f;

    /// <summary>
    /// True when the world started playing because <see cref="WorldGenerationConfig.LoadTimeoutSeconds"/>
    /// ran out rather than because its ready sphere was meshed. A loading screen can report it; the
    /// lifecycle tests assert it stays false.
    /// </summary>
    public bool LoadTimedOut => _loadGate?.TimedOut ?? false;

    /// <summary>Live item drops lying in (or flying through) the world.</summary>
    public IReadOnlyList<ItemDrop> Drops => _dropManager.Drops;

    /// <summary>Live primed TNT, feeding the renderer's dynamic point lights.</summary>
    public IReadOnlyList<PrimedTnt> PrimedTnt => _tntManager.Primed;

    /// <summary>Live falling sand/gravel cubes.</summary>
    public IReadOnlyList<FallingBlock> Falling => _falling.Falling;

    /// <summary>Live slimes, feeding the entity and shadow passes.</summary>
    public IReadOnlyList<Slime> Slimes => _slimes?.Slimes ?? (IReadOnlyList<Slime>)Array.Empty<Slime>();

    /// <summary>Live goblins, feeding the entity, shadow and mirror passes.</summary>
    public IReadOnlyList<Goblin> Goblins => _goblins?.Goblins ?? (IReadOnlyList<Goblin>)Array.Empty<Goblin>();

    // Every live mob of every kind, rebuilt once a frame after the mob managers ran
    // (RefreshMobs), so punch, slice, thrown items and healthbars target them all.
    private readonly List<Mob> _mobs = [];

    /// <summary>Every live mob (slimes, goblins), feeding targeting and the healthbars.</summary>
    public IReadOnlyList<Mob> Mobs => _mobs;

    /// <summary>Live falling leaves, feeding the batched billboard pass.</summary>
    public IReadOnlyList<LeafParticle> LeafParticles => _leaves.Particles;

    /// <summary>Live mining debris, feeding the small-cube entity pass.</summary>
    public IReadOnlyList<BlockDebris> Debris => _debris.Debris;

    /// <summary>Lit campfire block entities, feeding lights and fire particles.</summary>
    public CampfireManager Campfires => _campfires;

    /// <summary>Quern block entities: waiting inputs and spinning tops.</summary>
    public QuernManager Querns => _querns;

    /// <summary>Kiln structures: stored fuel and burn state.</summary>
    public KilnManager Kilns => _kilns;

    /// <summary>Cast molds: molten metal being poured, cooling and waiting to be collected.</summary>
    public CastingManager Castings => _casting;

    /// <summary>Every burning cell this frame (campfires and lit kilns), feeding both the fire
    /// particles and the warm point lights.</summary>
    public IReadOnlyList<Vector3> FireCells => _fireCells;

    /// <summary>Live campfire embers and smoke, feeding the fire billboard pass.</summary>
    public IReadOnlyList<FireParticle> FireParticles => _fire.Particles;

    /// <summary>Live torch flame embers and smoke (a third fire instance).</summary>
    public IReadOnlyList<FireParticle> TorchFlameParticles => _torchFire.Particles;

    /// <summary>Live exhaled bubbles, feeding the bubble billboard pass.</summary>
    public IReadOnlyList<BubbleParticle> Bubbles => _bubbles.Particles;

    public IReadOnlyList<ThrownProjectile> Thrown => _thrown;

    /// <summary>Every projectile in flight this frame - the player's throws and the goblins'
    /// stones - feeding the thrown-item pass and the water mirror.</summary>
    public IReadOnlyList<ThrownProjectile> Projectiles => _projectiles;
    public bool IsThrowCharging => _throwCharging;
    public float ThrowCharge01 => MathUtils.Clamp01(_throwChargeSeconds / WorldGenerationConfig.ThrowChargeSeconds);

    // Break overlay strength. Quantized to whole hits, so it steps with the damage dealt
    // instead of sweeping continuously.
    private float BreakProgress => _breakHardness > 0f ? _breakDamage / _breakHardness : 0f;

    // Growth of the block being mined: 0 at rest, eased up to BlockPopGrow and back down
    // over BlockPopSeconds after each hit lands
    private float BlockPopGrowNow =>
        BlockPopGrow * MathUtils.Pulse(_popElapsed / BlockPopSeconds);

    // Growth of a just-placed block's pop: it starts a little too big and settles with one small
    // overshoot (PlacePop), rendered only while grown (the chunk mesh holds the rest-size block)
    private float PlacePopGrowNow =>
        PlacePop.Grow(_placePopElapsed, PlacePopSeconds, PlacePopGrow);

    /// <summary>
    /// Builds the world and starts its chunk-generation and LOD workers. With a
    /// <paramref name="saveStore"/>, edited chunks are snapshotted to it on unload and on autosave,
    /// restored from it when they stream back in, the far-LOD edit state
    /// (<see cref="LodEditState"/>) is imported from it before the first LOD region builds, and the
    /// saved player (<see cref="PlayerSaveCodec.FileName"/>: pose, modes, health, inventory) is
    /// applied before the workers start, so they stream around the restored position; the saved mobs
    /// and drops (<see cref="EntitySaveCodec.FileName"/>) are spawned once loading finishes, each
    /// as soon as its chunk is loaded (<see cref="RestorePendingEntities"/>). A world with
    /// a store starts <see cref="WorldPhase.Loading"/>; null (the default) keeps the old
    /// regenerate-and-forget behaviour and starts <see cref="WorldPhase.Playing"/> at once.
    /// </summary>
    /// <remarks>
    /// Passing null for <paramref name="gl"/>, the three pools, <paramref name="window"/> and
    /// <paramref name="input"/> is the headless path the tests use: the camera takes no input and
    /// the clock runs off the summed <see cref="Update"/> deltas.
    /// <para>
    /// The constructor registers the world as live (<see cref="WorldSeed.BeginWorld"/>), so
    /// <see cref="WorldSeed.Apply"/> refuses to run from here on: its worker threads read the
    /// worldgen statics unsynchronised. <see cref="SaveAndDispose"/> is the teardown; it stops the
    /// workers and calls <see cref="WorldSeed.EndWorld"/> last. A constructor that throws stops what
    /// it started and ends the world itself.
    /// </para>
    /// <para>
    /// <paramref name="spawn"/> is the eye to start at, which a multiplayer client passes from the
    /// server hello; null means the worldgen spawn (<c>VOXEL_SPAWN</c>, else the surface at the
    /// origin), except in client mode, where it means <see cref="Camera.PlaceholderSpawn"/>, since a
    /// client never reads worldgen.
    /// </para>
    /// <para>
    /// <paramref name="worldSource"/> is where the terrain comes from: the blocks of every chunk the
    /// save store does not hold and the column data the fluid wake, the LOD edit hooks, the spawn
    /// settle, the mob spawn ring and the far LOD read (<see cref="ChunkManager.WorldSource"/>, and
    /// the mesher's <see cref="ChunkManager.ColumnSource"/>). A server passes the world's source
    /// (an imported map, say); null means worldgen, which leaves both manager defaults untouched.
    /// </para>
    /// </remarks>
    public World(GL gl, ChunkGeometryPool opaqueGeometry, ChunkGeometryPool translucentGeometry,
        ChunkGeometryPool lodGeometry, IWindow window, IInputContext input, SfxManager? sfx = null,
        ChunkSaveStore? saveStore = null, bool serverMode = false, bool clientMode = false,
        Vector3? spawn = null, IWorldSource? worldSource = null)
    {
        WorldSeed.BeginWorld();
        ChunkGenerationWorker? generationWorker = null;
        LodManager? lodManager = null;
        try
        {
            _window = window;
            _sfx = sfx;
            _saveStore = saveStore;
            _serverMode = serverMode;
            _clientMode = clientMode;
            // A client never searches the worldgen spawn: it has no terrain of its own, so it
            // starts at the server's spawn (or a fixed placeholder until one is known).
            Camera = new Camera(window, input, clientMode ? spawn ?? Camera.PlaceholderSpawn : spawn);
            _localContext = new PlayerContext(Inventory) { Camera = Camera };
            _savedPlayer = PlayerSaveCodec.TryParse(saveStore?.ReadFile(PlayerSaveCodec.FileName));
            if (_savedPlayer != null)
                ApplySavedPlayer(_savedPlayer);
            if (saveStore != null)
            {
                // Held until the Loading -> Playing flip, then spawned as their chunks load
                var savedEntities = EntitySaveCodec.TryParse(saveStore.ReadFile(EntitySaveCodec.FileName));
                if (savedEntities?.Mobs != null)
                    _pendingMobs.AddRange(savedEntities.Mobs);
                if (savedEntities?.Drops != null)
                    _pendingDrops.AddRange(savedEntities.Drops);
            }
            _spawnX = Camera.Position.X;
            _spawnZ = Camera.Position.Z;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOXEL_SPAWN")))
                _spawnSettled = true;
            _chunkManager = new ChunkManager(gl, opaqueGeometry, translucentGeometry, saveStore);
            if (worldSource != null)
            {
                // Before any worker starts (Thread.Start publishes both): the source fills the
                // chunks and is also what the mesher, the fluid surface query and a server's
                // column answers read per chunk column
                _chunkManager.WorldSource = worldSource;
                _chunkManager.ColumnSource = worldSource;
            }
            // A server never draws: finished meshes are marked and their light published, but the
            // geometry is discarded instead of uploaded to pools it does not have.
            _chunkManager.Headless = serverMode;
            if (serverMode)
            {
                // No local player on a server: hover the camera (creative and flying, so mobs
                // cannot hurt it and gravity cannot drop it) at the spawn focus so chunk streaming
                // follows it and has a stable target until real players connect.
                Camera.RestoreState(Camera.Position, Camera.Yaw, Camera.Pitch, creative: true, flying: true);
            }
            if (serverMode)
            {
                // The camera is the server's chunk focus, not a player: the player.json this world
                // was opened with is kept and written back unchanged (see CapturePlayer), so a
                // survival world hosted once does not reopen in creative flight at the centroid.
                _hostedPlayer = _savedPlayer;
            }
            if (clientMode)
            {
                // A client starts at the server's spawn (the spawn argument), so its own worldgen
                // spawn search never runs - which would compute columns the server owns.
                _spawnSettled = true;
                // Nor does it tick fluids or leaf decay (the server's broadcast carries both), so
                // the edit paths' scheduling would only pile up cells nothing ever drains.
                _fluidManager.Enabled = false;
                _leafDecay.Enabled = false;
            }
            // Seeded before the workers start, so they generate around the spawn from the first
            // scan rather than idling until the first frame publishes a focus
            UpdateFocus();
            generationWorker = _generationWorker = new ChunkGenerationWorker(_chunkManager, Focus);
            lodManager = _lodManager = new LodManager(lodGeometry);
            _lodManager.WorldSource = _chunkManager.WorldSource;
            _canAcceptDrop = Inventory.CanAccept;
            if (EntityRegistry.TryGet("slime") is { } slimeType)
                _slimes = new SlimeManager(slimeType, _mobCaps);
            if (EntityRegistry.TryGet("goblin") is { } goblinType)
                _goblins = new GoblinManager(goblinType, _mobCaps);

            // A store means a real world opened from (or created into) a save: it streams first and
            // plays once the chunks around the player are there. Larger, still bounded budgets
            // speed the stream up without letting any one frame spike.
            Phase = saveStore != null ? WorldPhase.Loading : WorldPhase.Playing;
            if (Phase == WorldPhase.Loading)
            {
                _loadGate = new WorldLoadGate(WorldGenerationConfig.LoadReadyRadiusChunks,
                    WorldGenerationConfig.LoadTimeoutSeconds);
                _chunkManager.MeshUploadBudget = WorldGenerationConfig.LoadingMeshUploadsPerFrame;
                _chunkManager.DirtyStartBudget = WorldGenerationConfig.LoadingDirtyChunksPerFrame;
                _lodManager.UploadBudget = WorldGenerationConfig.LoadingLodUploadsPerFrame;
                Camera.SuppressLook = true;
            }

            // A client never generates: its chunks stream from the server (ClientChunkStreamer).
            if (!clientMode)
                _generationWorker.Start();
            // Before the LOD workers start, so the first region builds already see the saved
            // caps, surface overrides and felled canopies; a missing or corrupt file imports nothing.
            LodEditState.TryParse(_saveStore?.ReadFile(LodEditState.FileName))?.Import();
            // A server never renders the far LOD, so it does not start the LodGen threads; the
            // imported edit state still round-trips through a save untouched. A client gets its
            // far LOD from the server (S2), so it starts none either.
            if (!serverMode && !clientMode)
                _lodManager.Start(Camera);
            else if (clientMode)
                _lodManager.BindCamera(Camera);
            RefreshMobs();
        }
        catch
        {
            // Nothing else will ever tear this world down, so stop what was started and end it
            // here; a worker that outlived its join keeps the seed locked instead.
            var joined = (generationWorker?.Stop() ?? true) & (lodManager?.Stop() ?? true);
            Camera?.Detach();
            if (joined)
                WorldSeed.EndWorld();
            else
                Log.Warn("World construction failed with a worker still running; the seed can no longer be changed in this process");
            throw;
        }
    }

    /// <summary>
    /// Applies a parsed <see cref="PlayerSaveCodec.FileName"/> field by field: a saved position
    /// restores the pose and marks the spawn settled (a restored player is never relocated to the
    /// surface), and without one the modes still apply at the current pose. The respawn eye,
    /// health and inventory are restored when present. The lighting clock waits for the
    /// Loading -> Playing flip (<see cref="FinishLoading"/>).
    /// </summary>
    private void ApplySavedPlayer(PlayerSaveData saved)
    {
        if (saved.Position is { Length: 3 } p)
        {
            Camera.RestoreState(new Vector3(p[0], p[1], p[2]), saved.Yaw ?? Camera.Yaw, saved.Pitch ?? Camera.Pitch,
                saved.Creative ?? false, saved.Flying ?? false);
            _spawnSettled = true;
        }
        else
        {
            Camera.RestoreState(Camera.Position, Camera.Yaw, Camera.Pitch, saved.Creative ?? false, saved.Flying ?? false);
        }

        if (saved.RespawnEye is { Length: 3 } r)
        {
            _respawnEye = new Vector3(r[0], r[1], r[2]);
            _hasRespawnEye = true;
        }

        if (saved.Health is { } hp)
            _health.Restore(hp);
        if (saved.Inventory is { } inventory)
            Inventory.Import(inventory);
    }

    /// <summary>
    /// Rebuilds <see cref="Mobs"/> from every mob manager. Indexed loops rather than
    /// <c>AddRange</c>, whose IEnumerable overload would allocate an enumerator every frame.
    /// </summary>
    private void RefreshMobs()
    {
        _mobs.Clear();
        if (_slimes != null)
        {
            var slimes = _slimes.Slimes;
            for (var i = 0; i < slimes.Count; i++)
                if (!slimes[i].IsDying) _mobs.Add(slimes[i]);
        }
        if (_goblins != null)
        {
            var goblins = _goblins.Goblins;
            for (var i = 0; i < goblins.Count; i++)
                if (!goblins[i].IsDying) _mobs.Add(goblins[i]);
        }
    }

    /// <summary>Rebuilds <see cref="Projectiles"/> from the player's throws and the goblins'
    /// stones, with indexed loops for the same reason as <see cref="RefreshMobs"/>.</summary>
    private void RefreshProjectiles()
    {
        _projectiles.Clear();
        for (var i = 0; i < _thrown.Count; i++)
            _projectiles.Add(_thrown[i]);
        if (_goblins != null)
        {
            var stones = _goblins.Stones;
            for (var i = 0; i < stones.Count; i++)
                _projectiles.Add(stones[i]);
        }
    }

    /// <summary>
    /// Spawns one mob of the named entity kind standing with its feet at <paramref name="feet"/>
    /// (the scripted visual tests' <c>spawn</c> op), a forced spawn under the hard mob cap. False
    /// for an unknown name, a kind whose manager does not exist because its content is not
    /// registered, or a full hard cap. <paramref name="size"/> is a slime's size (other kinds
    /// have none and ignore it).
    /// </summary>
    public bool SpawnEntity(string name, Vector3 feet, int size = SlimeSize.Min)
    {
        if (string.Equals(name, "slime", StringComparison.OrdinalIgnoreCase))
        {
            var centre = feet + new Vector3(0f, Slime.HalfExtentFor(size), 0f);
            if (_slimes?.Spawn(centre, size) == null) return false;
            RefreshMobs();
            return true;
        }
        if (string.Equals(name, "goblin", StringComparison.OrdinalIgnoreCase))
        {
            if (_goblins?.Spawn(feet + new Vector3(0f, Goblin.Height * 0.5f, 0f)) == null) return false;
            RefreshMobs();
            return true;
        }
        return false;
    }

    /// <summary>
    /// The terrain height of the column at (<paramref name="x"/>, <paramref name="z"/>) from the
    /// world's source (<see cref="ChunkManager.WorldSource"/>: worldgen by default): the top solid
    /// cell before player edits, the same lookup the mob spawn ring uses. A source answers every
    /// column, so it is always available.
    /// </summary>
    public bool TryGroundHeight(int x, int z, out int height)
    {
        var column = _chunkManager.WorldSource.GetColumn(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(z));
        height = column.Heights[VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(x), VoxelCoords.WorldToLocal(z))];
        return true;
    }

    /// <summary>
    /// Grows the tree of the named species rooted in column (<paramref name="x"/>,
    /// <paramref name="z"/>) by up to <paramref name="steps"/> growth pulses, or plants a sapling
    /// on the column's ground when it has no such tree (<see cref="TreeGrowthWriter"/>). Main
    /// thread only; this is the scripted visual tests' <c>grow</c> op's entry point.
    /// <paramref name="yHint"/> centres the column scan (the generated ground + 1). Refused with
    /// <see cref="TreeGrowthStatus.NotGrowable"/> while the world is still loading or for an
    /// unknown species.
    /// </summary>
    public TreeGrowthResult TryGrowTree(string speciesName, int x, int yHint, int z, int steps)
    {
        var species = IsPlaying ? TreeSpeciesRegistry.TryGet(speciesName) : null;
        if (species == null)
            return new TreeGrowthResult(TreeGrowthStatus.NotGrowable, x, yHint, z, 0, 0, 0, 0);

        var result = _treeGrowth.Grow(_chunkManager, species, x, yHint, z, steps, WorldGenerationConfig.Seed,
            _fluidManager, _leafDecay, _lodManager, _falling.OverlapsAny);

        // One synchronous remesh at the root so the grow frame already shows the base; the
        // writer's MarkDirty batch rebuilds every other touched chunk asynchronously. Never loop
        // immediate remeshes here, and never use RemeshAfterBatchEdit: tree blocks are
        // light-neutral, and it would dirty the whole light radius around every touched chunk.
        if (result.MeshCellsWritten > 0)
        {
            var rootCell = new Vector3(result.RootX, result.RootY, result.RootZ);
            _chunkManager.ImmediateRemeshWithBorders(VoxelCoords.ChunkPosOf(rootCell), rootCell);
        }

        // A freshly planted sapling joins the sapling clock and grows on its own over the next
        // few minutes (SaplingManager); an existing tree the call grew needs no clock
        if (result.Status == TreeGrowthStatus.Planted)
            _saplings.Schedule(species.Name, result.RootX, result.RootY, result.RootZ);
        return result;
    }

    // Standing against a cactus pricks: one tick per ContactDamage.TickSeconds while the body box
    // touches one; leaving it (or going creative/flying) clears the partial tick. Singleplayer
    // only: a client mirrors the server's pool, and a server's players tick their own
    // (ServerPlayer.TickStatus).
    private void UpdateCactusContact(float deltaTime)
    {
        if (_serverMode || _clientMode) return;
        ContactDamage.Touches(_chunkManager, Camera.Position - new Vector3(0f, Camera.EyeHeight, 0f),
            PlayerPhysics.PlayerRadius, PlayerPhysics.PlayerHeight, out var damage);
        var tick = ContactDamage.Tick(ref _contactTimer, deltaTime, (int)damage,
            !(Camera.IsCreative || Camera.IsFlying));
        if (tick > 0)
            _health.TakeDamage(tick);
    }

    private void EnsureSpawnSettled()
    {
        if (_spawnSettled)
            return;

        var dx = Camera.Position.X - _spawnX;
        var dz = Camera.Position.Z - _spawnZ;
        if (dx * dx + dz * dz > 64f)
        {
            _spawnSettled = true;
            return;
        }

        var sx = MathUtils.FloorToInt(_spawnX);
        var sz = MathUtils.FloorToInt(_spawnZ);
        var cx = VoxelCoords.WorldToChunk(sx);
        var cz = VoxelCoords.WorldToChunk(sz);
        var lx = VoxelCoords.WorldToLocal(sx);
        var lz = VoxelCoords.WorldToLocal(sz);
        // The world source's column (worldgen by default). Its stored water level is exactly what
        // TerrainGenerator.IsWaterColumn evaluates for a generated column, and an imported one
        // answers with its own.
        var column = _chunkManager.WorldSource.GetColumn(cx, cz);
        var columnIndex = VoxelCoords.ColumnIndex(lx, lz);
        var heightmap = column.Heights[columnIndex];
        var estimate = Math.Max(heightmap, WorldGenerationConfig.SeaLevel);
        const float spawnLift = 0.02f;
        var holdEye = estimate + 1 + Camera.EyeHeight + spawnLift;

        if (heightmap < column.WaterLevel[columnIndex])
        {
            if (!_chunkManager.TryGetCell(sx, WorldGenerationConfig.SeaLevel, sz, out _, out _))
            {
                Camera.TestTeleport(new Vector3(_spawnX, holdEye, _spawnZ), Camera.Yaw, Camera.Pitch);
                return;
            }

            Camera.TestTeleport(new Vector3(_spawnX, WorldGenerationConfig.SeaLevel + 1 + Camera.EyeHeight + spawnLift, _spawnZ),
                Camera.Yaw, Camera.Pitch);
            _spawnSettled = true;
            return;
        }

        var top = estimate + TreeGenerator.ReachUp + 1;
        if (!_chunkManager.TryGetCell(sx, top, sz, out _, out _))
        {
            Camera.TestTeleport(new Vector3(_spawnX, holdEye, _spawnZ), Camera.Yaw, Camera.Pitch);
            return;
        }

        for (var y = top; y > estimate - 64; y--)
        {
            if (!_chunkManager.TryGetCell(sx, y, sz, out var id, out _))
            {
                Camera.TestTeleport(new Vector3(_spawnX, holdEye, _spawnZ), Camera.Yaw, Camera.Pitch);
                return;
            }

            if (!BlockType.IsSolid(id))
                continue;

            if (!_chunkManager.TryGetCell(sx, y + 1, sz, out var above1, out _)
                || !_chunkManager.TryGetCell(sx, y + 2, sz, out var above2, out _))
            {
                Camera.TestTeleport(new Vector3(_spawnX, holdEye, _spawnZ), Camera.Yaw, Camera.Pitch);
                return;
            }

            if (BlockType.IsSolid(above1) || BlockType.IsSolid(above2))
                continue;

            Camera.TestTeleport(new Vector3(_spawnX, y + 1 + Camera.EyeHeight + spawnLift, _spawnZ),
                Camera.Yaw, Camera.Pitch);
            _spawnSettled = true;
            return;
        }

        Camera.TestTeleport(new Vector3(_spawnX, holdEye, _spawnZ), Camera.Yaw, Camera.Pitch);
    }

    /// <summary>
    /// Seconds added to the window clock to get <see cref="LightingTime"/>. Program sets it from
    /// <c>VOXEL_TIME</c> right after construction; a world restored from a save with a lighting
    /// clock overrides it at the Loading -> Playing flip, so the day resumes where it was saved.
    /// </summary>
    public double TimeOffset { get; set; }

    /// <summary>
    /// THE lighting clock: the window (or headless) clock plus <see cref="TimeOffset"/>. Program's
    /// <c>SetLighting</c> and HUD read it, the wind here reads it, and the save stores it as
    /// <see cref="PlayerSaveData.LightingClock"/>.
    /// </summary>
    public double LightingTime => Now + TimeOffset;

    /// <summary>
    /// Moves a server world's headless camera to <paramref name="position"/>. The camera is the
    /// chunk-streaming focus only while no player is connected (see <see cref="Focus"/>); once
    /// players are, chunks stream around each of them instead. No-op for a normal world, whose
    /// camera is driven by input.
    /// </summary>
    public void SetServerFocus(Vector3 position)
    {
        if (!_serverMode)
            return;
        Camera.TestTeleport(position, Camera.Yaw, Camera.Pitch);
    }

    /// <summary>
    /// Publishes this frame's streaming foci: every connected player's position on a server with
    /// players online, the camera otherwise. <see cref="StreamingFocus"/> republishes only when a
    /// focus changed chunk or the set changed, so a still frame allocates nothing.
    /// </summary>
    private void UpdateFocus()
    {
        var players = ServerPlayers;
        if (!_serverMode || players.Count == 0)
        {
            Focus.SetEye(Camera.Position);
            return;
        }

        if (_focusEyes.Length < players.Count)
            _focusEyes = new Vector3[Math.Max(players.Count, _focusEyes.Length * 2)];
        for (var i = 0; i < players.Count; i++)
            _focusEyes[i] = players[i].Position;
        Focus.SetEyes(_focusEyes.AsSpan(0, players.Count));
    }

    /// <summary>Sets the lighting clock so <see cref="LightingTime"/> reads <paramref name="time"/>
    /// (a client following the server's day clock).</summary>
    public void SetLightingTime(double time) => TimeOffset = time - Now;

    /// <summary>The other players on this client's server, drawn each frame. Empty in singleplayer.</summary>
    public IReadOnlyList<RemotePlayer> RemotePlayers => _remotePlayers;

    /// <summary>
    /// Raised on a multiplayer client for every cell its own action just changed locally (id 0 for
    /// a removal), with the id and state the cell now holds: the prediction the intent raised right
    /// after it (<see cref="IntentRaised"/>) will be judged on, so the client can tell the server's
    /// answer to it from an older message (<see cref="ClientPrediction"/>). Null in singleplayer
    /// and on the server.
    /// </summary>
    public Action<int, int, int, byte, byte>? BlockEdited { get; set; }

    /// <summary>
    /// Raised on a multiplayer client for every action the server has to replay: a mining start or
    /// completion, a placement, a claimed right-click, a throw, a landed attack. The client applied
    /// the action locally as a prediction first (its changed cells were reported through
    /// <see cref="BlockEdited"/> just before); the server answers with what really happened. Null
    /// in singleplayer and on the server.
    /// </summary>
    public Action<WorldIntent>? IntentRaised { get; set; }

    /// <summary>
    /// The chance roll every random item use reads (a firestarter's spark), 0..1. A test seam:
    /// tests make a roll succeed by returning 0.
    /// </summary>
    internal Func<float> UseRoll { get; set; } = static () => Random.Shared.NextSingle();

    /// <summary>The local player as a <see cref="PlayerContext"/>, refreshed from the camera.</summary>
    private PlayerContext LocalContext()
    {
        _localContext.Eye = Camera.Position;
        _localContext.Forward = Camera.ViewDirection;
        _localContext.ViewDirection = Camera.ViewDirection;
        _localContext.IsCreative = Camera.IsCreative;
        return _localContext;
    }

    /// <summary>
    /// The cell <paramref name="player"/> aims at within <see cref="InteractionReach"/> and the face
    /// the aim entered it through: what a server checks a client's claimed target against, so an
    /// intent can only touch a cell the player's own eye reaches and sees.
    /// </summary>
    public bool RaycastFor(PlayerContext player, bool includeFluids, out Vector3 hit, out Vector3 normal) =>
        player.Raycast(InteractionReach, _chunkManager, out hit, out normal, includeFluids);

    /// <summary>
    /// Breaks the block in <paramref name="cell"/> for <paramref name="player"/> with every
    /// consequence a local break has (<see cref="RemoveBlock"/>): wood fells what it held up, any
    /// cell of a multiblock reverts the structure, loot drops unless the player is creative, a
    /// station hands back what it held, fragile blocks above pop and sand above topples, and the
    /// fluid, leaf-decay, far-LOD and canopy hooks all run. A server calls it once a player's mining
    /// was judged complete. Returns whether the cell was cleared; false for an unloaded chunk, air
    /// or a fluid (never mined), or while the world is not playing.
    /// </summary>
    public bool BreakBlockFor(PlayerContext player, Vector3 cell)
    {
        if (!IsPlaying) return false;
        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
        if (!_chunkManager.TryGetCell(x, y, z, out var id, out _) || id == 0 || BlockType.IsFluid(id))
            return false;
        RemoveBlock(cell, player);
        return _chunkManager.TryGetCell(x, y, z, out var after, out _) && after == 0;
    }

    /// <summary>
    /// Places <paramref name="player"/>'s selected block against the face of
    /// <paramref name="hitCell"/> with outward normal <paramref name="hitNormal"/> (a target a
    /// server has already checked the player can see), spending one unless the player is creative.
    /// The block comes from the player's own selected slot and must be owned and placeable; the
    /// cell follows the placement rules (<see cref="Placement"/>, no player body or live falling
    /// cube in the way, fragile blocks on solid ground) and the state byte is the world's own
    /// (a placed log is detached, a placed leaf persistent). Returns whether one was placed.
    /// </summary>
    public bool TryPlaceSelectedBlockAt(PlayerContext player, Vector3 hitCell, Vector3 hitNormal)
    {
        if (!IsPlaying) return false;
        var inventory = player.Inventory;
        if (!inventory.CanPlaceSelected) return false;
        if (!PlaceAgainstFace(hitCell, hitNormal, inventory.SelectedBlockId, player)) return false;
        if (!player.IsCreative)
            inventory.ConsumeSelected();
        return true;
    }

    /// <summary>
    /// Lands <paramref name="player"/>'s punch: the nearest mob inside the forward cone from the
    /// player's eye with a clear line of sight takes the held item's <c>entityDamage</c> and a
    /// shove. Returns whether a mob was struck.
    /// </summary>
    public bool PunchFor(PlayerContext player)
    {
        if (!IsPlaying) return false;
        if (!TryFindPunchTarget(player.Eye, player.ViewDirection, out var target)) return false;
        StrikeMob(target, Combat.PunchDamage(HeldItem(player.Inventory)), player.Eye);
        return true;
    }

    /// <summary>
    /// Lands one of <paramref name="player"/>'s slice swings (<paramref name="returnSwing"/>: the
    /// half-power return): the nearest two mobs in the forward hemisphere with a clear line of sight
    /// are struck. Returns how many were.
    /// </summary>
    public int SliceFor(PlayerContext player, bool returnSwing)
    {
        if (!IsPlaying) return 0;
        return ApplySliceDamage(player.Eye, player.ViewDirection, HeldItem(player.Inventory), returnSwing);
    }

    /// <summary>
    /// Releases a throw of <paramref name="player"/>'s selected throwable at
    /// <paramref name="charge01"/> from the player's eye along its view, spending the unit unless
    /// the player is creative. False, spending nothing, when the selected slot holds nothing
    /// throwable.
    /// </summary>
    public bool ThrowFor(PlayerContext player, float charge01)
    {
        if (!IsPlaying) return false;
        var inventory = player.Inventory;
        var id = inventory.SelectedBlockId;
        if (id == 0 || inventory.SelectedCount <= 0) return false;
        if (ItemRegistry.TryGet(id) is not { IsThrowable: true } item) return false;
        return TryLaunchThrow(inventory, id, item.ThrowDamage ?? 0f, MathUtils.Clamp01(charge01), player.Eye,
            player.ViewDirection, player.IsCreative);
    }

    /// <summary>
    /// Applies a batch of server cell values (an explosion, a fluid step, a fell, or the correction
    /// answering one of this client's intents) in one pass: raw writes with no per-cell remesh, then
    /// one light-radius area rebuild per touched chunk (<see cref="Chunk.RemeshAfterBatchEdit"/>,
    /// the same dirtying a local batch edit uses, so a remote lamp or roof edit relights the edge
    /// and corner chunks too), each chunk once however many of its cells changed. A cell that
    /// already holds the value is skipped, so a correction confirming a prediction costs nothing. A
    /// quern that arrives registers its block entity so it draws. Never raises
    /// <see cref="BlockEdited"/>, so it cannot loop.
    /// </summary>
    public void ApplyNetworkBlockChanges(IReadOnlyList<NetCellChange> changes)
    {
        _netTouchedChunks.Clear();
        _netHardened.Clear();
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var chunk = _chunkManager.ChunkAt(change.X, change.Y, change.Z);
            if (chunk == null)
                continue;
            if (_chunkManager.TryGetCell(change.X, change.Y, change.Z, out var id, out var state)
                && id == change.Id && state == change.State)
                continue;
            chunk.SetBlockRaw(change.X & Chunk.SizeMask, change.Y & Chunk.SizeMask, change.Z & Chunk.SizeMask,
                change.Id, change.State);
            _netTouchedChunks.Add(chunk.Position);
            // The server's falling cube writing itself back: dust as if it fell the client fall, since
            // the mirror never told how far (a client in a server world only; never on a server)
            if (_clientMode && id != change.Id && BlockType.IsFalling(change.Id)
                && NearMirroredFallingCube(change.X, change.Y, change.Z, change.Id))
                GroundDust.LandBlock(new Vector3(change.X, change.Y, change.Z), change.Id,
                    WorldGenerationConfig.FallingBlockDustClientFallBlocks, submerged: BlockType.IsFluid(id), _debris,
                    _chunkManager.ColumnSource);
            // The server's lava hardening into its rock: steam, like a local hardening
            if (_clientMode && change.Id != 0 && BlockType.IsFluid(id) && BlockType.HardensToById[id] == change.Id)
                _netHardened.Add(new Vector3(change.X, change.Y, change.Z));
            if (BlockType.ById[change.Id]?.Renderer == BlockRenderer.Quern)
                _querns.Ensure(change.X, change.Y, change.Z);
        }

        foreach (var pos in _netTouchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(pos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        if (_netHardened.Count > 0)
            PuffSteam(_netHardened);
    }

    /// <summary>
    /// Reconciles the client's mobs and drops with a server snapshot: mobs are spawned, updated or
    /// removed by network id, drops are rebuilt wholesale. Only a client calls this; it never ticks
    /// the mob AI, so the entities it holds are server state drawn in place. In a client world the
    /// snapshot's primed TNT and falling blocks become display-only mirrors too (TNT by network id,
    /// falling cubes wholesale); any other world ignores them, since its own Update would tick and
    /// detonate them.
    /// </summary>
    public void ApplyNetworkEntities(IReadOnlyList<NetEntity> entities)
    {
        _netMobsSeen.Clear();
        for (var i = 0; i < entities.Count; i++)
        {
            var entry = entities[i];
            if (!NetEntity.IsMob(entry.Kind))
                continue;
            _netMobsSeen.Add(entry.Id);
            if (_netMobs.TryGetValue(entry.Id, out var existing) && KindMatches(existing, entry))
            {
                // A client never damages a mob itself: a hit shows up as the health drop between
                // two snapshots, and that drop is what its damage number floats
                var before = existing.Health;
                existing.NetworkSetState(entry.Position, entry.Yaw, entry.Health, entry.Velocity);
                if (!_serverMode && existing.Health < before)
                    ShowDamageNumber(existing, before - existing.Health);
                continue;
            }

            if (existing != null)
                RemoveNetMob(entry.Id);
            if (SpawnNetMob(entry) is { } mob)
                _netMobs[entry.Id] = mob;
        }

        _netMobRemovals.Clear();
        foreach (var id in _netMobs.Keys)
            if (!_netMobsSeen.Contains(id))
                _netMobRemovals.Add(id);
        foreach (var id in _netMobRemovals)
        {
            // The server stops listing a mob the moment it dies, so the killing blow never shows as a
            // drop; one leaving near the camera (far inside every despawn radius) died, and floats
            // the health it had left
            if (!_serverMode && _netMobs.TryGetValue(id, out var gone) && gone.Health > 0
                && Vector3.DistanceSquared(gone.Position, Camera.Position)
                <= WorldGenerationConfig.DamageNumberDeathRangeBlocks * WorldGenerationConfig.DamageNumberDeathRangeBlocks)
                ShowDamageNumber(gone, gone.Health);
            RemoveNetMob(id);
        }

        _dropManager.Clear();
        for (var i = 0; i < entities.Count; i++)
        {
            var entry = entities[i];
            if (entry.Kind == NetEntityKind.Drop)
                _dropManager.SpawnRestored(entry.ItemId, entry.Position, entry.Velocity, 0f);
        }

        if (_clientMode)
            ApplyNetworkMirrors(entities);

        RefreshMobs();
    }

    /// <summary>
    /// A client's primed TNT and falling cubes from a server snapshot. A cube already mirrored under
    /// the same id takes the new position and fuse (its strobe clock carries on), a new one is
    /// mirrored at its reported fuse, an unlisted one goes; the falling cubes are rebuilt wholesale.
    /// The client never ticks either manager (see <see cref="UpdateClient"/>), so neither moves,
    /// lands nor detonates here.
    /// </summary>
    private void ApplyNetworkMirrors(IReadOnlyList<NetEntity> entities)
    {
        _netTntSeen.Clear();
        // The last snapshot's cubes stay known for one more, so a cube's cell write is recognised
        // whichever of its EntityList and BlockChanges arrives first
        _netFallingPrevious.Clear();
        _netFallingPrevious.AddRange(_netFalling);
        _netFalling.Clear();
        for (var i = 0; i < entities.Count; i++)
        {
            var entry = entities[i];
            if (entry.Kind == NetEntityKind.FallingBlock)
            {
                _netFalling.Add((entry.Position, entry.ItemId));
                continue;
            }
            if (entry.Kind != NetEntityKind.PrimedTnt)
                continue;

            // Milliseconds on the wire; a fuse is never shorter than what is left of it, so a bad
            // pair cannot divide the flash by zero
            var duration = Math.Max(entry.Ammo, 1) / 1000f;
            var remaining = Math.Clamp(entry.Health, 0, Math.Max(entry.Ammo, 1)) / 1000f;
            _netTntSeen.Add(entry.Id);
            if (_netTnt.TryGetValue(entry.Id, out var existing))
                existing.MirrorSetState(entry.Position, remaining);
            else
                _netTnt[entry.Id] = silkgltriangle.PrimedTnt.Mirror(entry.Position, entry.ItemId, duration, remaining);
        }

        _netTntRemovals.Clear();
        foreach (var id in _netTnt.Keys)
            if (!_netTntSeen.Contains(id))
                _netTntRemovals.Add(id);
        foreach (var id in _netTntRemovals)
        {
            // The server drops a cube only when it detonates, so a mirror leaving the snapshot with
            // its locally burnt fuse spent is a blast: shake the camera at its last position
            if (_netTnt.Remove(id, out var gone)
                && gone.FuseRemaining <= WorldGenerationConfig.BlastMirrorFuseSlackSeconds)
                ShakeForBlast(gone.Position);
        }

        _tntManager.ReplaceMirrors(_netTnt.Values);
        _falling.ReplaceMirrors(_netFalling);
    }

    /// <summary>
    /// Puffs steam (smoke on the campfire particle instance, <see cref="FireParticleSystem.Puff"/>)
    /// off the top of each cell in <paramref name="cells"/> that contact hardening just turned solid,
    /// for at most <see cref="WorldGenerationConfig.SteamPuffMaxCellsPerFrame"/> cells within
    /// <see cref="WorldGenerationConfig.FireParticleRadius"/> of the eye. Cosmetic: singleplayer and a client.
    /// </summary>
    private void PuffSteam(IReadOnlyList<Vector3> cells)
    {
        const float reach = WorldGenerationConfig.FireParticleRadius * WorldGenerationConfig.FireParticleRadius;
        var puffed = 0;
        for (var i = 0; i < cells.Count && puffed < WorldGenerationConfig.SteamPuffMaxCellsPerFrame; i++)
        {
            var top = cells[i] + new Vector3(0.5f, 1.05f, 0.5f);
            if (Vector3.DistanceSquared(top, Camera.RenderEye) > reach) continue;
            _fire.Puff(top, WorldGenerationConfig.SteamPuffSmoke, WorldGenerationConfig.SteamPuffSpread,
                FireParticleKind.Steam);
            puffed++;
        }
    }

    /// <summary>Whether a falling-cube mirror of <paramref name="blockId"/> from the newest or the
    /// previous snapshot sat within a block and a half of cell (x, y, z): the cube that just wrote
    /// itself back there.</summary>
    private bool NearMirroredFallingCube(int x, int y, int z, byte blockId)
    {
        var centre = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        const float reach = 1.5f * 1.5f;
        foreach (var (cube, id) in _netFalling)
            if (id == blockId && Vector3.DistanceSquared(cube, centre) <= reach)
                return true;
        foreach (var (cube, id) in _netFallingPrevious)
            if (id == blockId && Vector3.DistanceSquared(cube, centre) <= reach)
                return true;
        return false;
    }

    /// <summary>Shakes the local camera for a TNT blast at <paramref name="centre"/>, by its distance
    /// (<see cref="ShakeFalloff"/>). Cosmetic: singleplayer and a client.</summary>
    private void ShakeForBlast(Vector3 centre) =>
        Camera.AddShake(ShakeFalloff.Trauma(WorldGenerationConfig.BlastShakeStrength, TntManager.BlastRadius,
            WorldGenerationConfig.BlastShakeRangeBlocks, Vector3.Distance(Camera.Position, centre)));

    /// <summary>
    /// A TNT blast at <paramref name="centre"/> shoves the players and the living mobs in range away
    /// from it (<see cref="BlastImpulse"/>): the local camera in singleplayer, every connected player
    /// on a server (through <see cref="IMobTarget.TakeHit"/>, which queues the knockback the player's
    /// status carries to its client), a creative, flying or dead player being immune; a mob is shoved
    /// sideways (<see cref="Mob.Shove"/>). The drops, falling cubes and other TNT the blast reached
    /// were already flung by <see cref="TntManager"/>. Gameplay: singleplayer and the server.
    /// </summary>
    private void PushFromBlast(Vector3 centre)
    {
        if (_serverMode)
        {
            var players = ServerPlayers;
            for (var i = 0; i < players.Count; i++)
                PushTargetFromBlast(players[i], centre);
        }
        else
        {
            PushTargetFromBlast(Camera, centre);
        }

        for (var i = 0; i < _mobs.Count; i++)
        {
            var mob = _mobs[i];
            if (mob.IsDying) continue;
            var impulse = BlastImpulse.At(centre, mob.Position, TntManager.BlastRadius);
            var sideways = new Vector3(impulse.X, 0f, impulse.Z);
            if (sideways.LengthSquared() > 1e-6f)
                mob.Shove(sideways, sideways.Length());
        }
    }

    // One player's share of a blast, aimed at its body's middle rather than its eye
    private static void PushTargetFromBlast(IMobTarget target, Vector3 centre)
    {
        if (target.IsImmune) return;
        var body = target.Eye - new Vector3(0f, Camera.EyeHeight * 0.5f, 0f);
        var impulse = BlastImpulse.At(centre, body, TntManager.BlastRadius);
        if (impulse == Vector3.Zero) return;
        var sideways = new Vector3(impulse.X, 0f, impulse.Z);
        target.TakeHit(0, sideways, sideways.Length(), MathF.Max(0f, impulse.Y));
    }

    /// <summary>Whether any station block entity (campfire, quern, kiln, cast mold) is tracked,
    /// so a server with none sends no station snapshots.</summary>
    public bool HasStations => StationNetCodec.AnyStations(_campfires, _querns, _kilns, _casting);

    /// <summary>Writes every station block entity for the server's broadcast
    /// (<see cref="NetMessage.StationState"/>).</summary>
    public void WriteStations(PacketWriter writer) =>
        StationNetCodec.Write(writer, _campfires, _querns, _kilns, _casting);

    /// <summary>
    /// A client replaces its station mirrors with the server's snapshot: the fires, quern tops,
    /// kilns and pours it draws are the server's, which alone runs them.
    /// </summary>
    public void ApplyNetworkStations(PacketReader reader) =>
        StationNetCodec.Read(reader, _campfires, _querns, _kilns, _casting);

    /// <summary>
    /// A client mirrors the health the server's <see cref="NetMessage.PlayerStatus"/> reports: the
    /// server alone bills falls, lava and mob hits, so a lower value is taken as damage (the flash
    /// and a shake sized like singleplayer's) and a higher one (a respawn) is restored outright.
    /// Only a client calls this; anywhere else the world owns its own pool.
    /// </summary>
    public void ApplyNetworkHealth(int health)
    {
        if (!_clientMode)
            return;
        var current = _health.Health;
        if (health < current)
        {
            var damage = current - health;
            _health.TakeDamage(damage);
            Camera.AddShake(Math.Clamp(damage / 40f, 0.2f, 1f));
            NotifyPlayerHurt(fromMob: true);
        }
        else if (health > current)
        {
            _health.Restore(health);
        }
    }

    // A slime mirror also has to be the reported size (the ammo field), since a size is fixed for life
    private static bool KindMatches(Mob mob, NetEntity entry) =>
        entry.Kind == NetEntityKind.Slime
            ? mob is Slime slime && slime.Size == SlimeSize.Clamp(entry.Ammo)
            : entry.Kind == NetEntityKind.Goblin && mob is Goblin;

    private Mob? SpawnNetMob(NetEntity entry)
    {
        Mob? mob = entry.Kind switch
        {
            // A slime's size rides the goblin-only ammo field (NetEntity)
            NetEntityKind.Slime => _slimes?.Spawn(entry.Position, entry.Ammo),
            NetEntityKind.Goblin => _goblins?.Spawn(entry.Position, entry.Ammo),
            _ => null,
        };
        mob?.NetworkSetState(entry.Position, entry.Yaw, entry.Health, entry.Velocity);
        return mob;
    }

    private void RemoveNetMob(int id)
    {
        if (!_netMobs.Remove(id, out var mob))
            return;
        switch (mob)
        {
            case Slime slime: _slimes?.Remove(slime); break;
            case Goblin goblin: _goblins?.Remove(goblin); break;
        }
    }

    /// <summary>
    /// Reconciles the remote player set with a server broadcast: upserts each entry (skipping the
    /// client's own id), then removes anyone no longer listed.
    /// </summary>
    public void SyncRemotePlayers(IReadOnlyList<RemotePlayerSnapshot> snapshot, int selfId)
    {
        _remoteIdsSeen.Clear();
        for (var i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            if (entry.Id == selfId)
                continue;
            _remoteIdsSeen.Add(entry.Id);
            var index = IndexOfRemote(entry.Id);
            if (index < 0)
            {
                var remote = new RemotePlayer(entry.Id, entry.Name, Player.Rig);
                remote.SetTarget(entry.Eye, entry.Yaw, entry.Pitch, entry.Flags, entry.Action, entry.ActionSeq);
                _remotePlayers.Add(remote);
            }
            else
            {
                var remote = _remotePlayers[index];
                remote.Name = entry.Name;
                remote.SetTarget(entry.Eye, entry.Yaw, entry.Pitch, entry.Flags, entry.Action, entry.ActionSeq);
            }
        }

        for (var i = _remotePlayers.Count - 1; i >= 0; i--)
            if (!_remoteIdsSeen.Contains(_remotePlayers[i].Id))
                _remotePlayers.RemoveAt(i);
    }

    public void ClearRemotePlayers() => _remotePlayers.Clear();

    private int IndexOfRemote(int id)
    {
        for (var i = 0; i < _remotePlayers.Count; i++)
            if (_remotePlayers[i].Id == id)
                return i;
        return -1;
    }

    /// <summary>
    /// One multiplayer-client frame: no local simulation, only the camera (and only once the chunk
    /// under it has streamed in, so it cannot fall through a world that has not arrived), the mesh
    /// pass, remote player interpolation and the selection raycast. Mobs, drops, fluids,
    /// explosions and edits are the server's.
    /// </summary>
    private void UpdateClient(double deltaTime, IKeyboard? keyboard)
    {
        UpdateFocus();
        _chunkManager.Update(Focus.Chunks);
        _lodManager.Update(_chunkManager);

        var feet = Camera.Position - new Vector3(0f, Camera.EyeHeight, 0f);
        if (_chunkManager.ChunkAt(MathUtils.FloorToInt(feet.X), MathUtils.FloorToInt(feet.Y),
                MathUtils.FloorToInt(feet.Z)) != null)
            Camera.Update((float)deltaTime, keyboard, _chunkManager);

        UpdatePlayerModel(deltaTime);

        for (var i = 0; i < _remotePlayers.Count; i++)
            _remotePlayers[i].Update(deltaTime);

        // The purely cosmetic systems a client's own edits still feed (mining debris, leaf
        // bursts and the ambient leaf fall) run locally, or what Render draws would hang in
        // mid-air. Falling blocks, TNT and toppling trees are never created on a client (see
        // RemoveBlock), so their ticks stay the server's: the falling cubes and primed TNT drawn here
        // are the server's, mirrored from its EntityList (ApplyNetworkMirrors), and only the TNT
        // fuse burns down locally so the flash keeps its pace between snapshots. The station mirrors
        // only animate (a quern top turning between snapshots) and feed the fire particles; the
        // server runs the stations and its StationState snapshots replace them. A throw this client
        // released flies as a cosmetic copy of the server's, which alone deals damage and lands as a
        // drop.
        FrameProfiler.Begin(ProfileSection.Entities);
        _leaves.Update((float)deltaTime, Camera.RenderEye, _chunkManager, windTime: LightingTime);
        _debris.Update((float)deltaTime, _chunkManager);
        _querns.Animate((float)deltaTime);
        _slimes?.AnimateMirrors((float)deltaTime, _debris);
        _goblins?.AnimateMirrors((float)deltaTime, _debris);
        _tntManager.AnimateMirrors((float)deltaTime);
        _fireCells.Clear();
        _fireCells.AddRange(_campfires.LitCells);
        _fireCells.AddRange(_kilns.LitCells);
        _fire.Update((float)deltaTime, _fireCells, Camera.RenderEye, wind: Wind.Direction(LightingTime));
        UpdateTorchFlames((float)deltaTime, Wind.Direction(LightingTime));
        UpdateBubbles((float)deltaTime);
        UpdateThrown((float)deltaTime);
        RefreshProjectiles();
        UpdateSplashes((float)deltaTime);
        FrameProfiler.End(ProfileSection.Entities);

        _hotbarPops.Update(Inventory, (float)deltaTime);
        _hotbarName.Update(Inventory, (float)deltaTime);
        _damageNumbers.Update((float)deltaTime);
        // The pool itself is the server's (ApplyNetworkHealth mirrors it); only the damage flash
        // fades here
        _health.Update((float)deltaTime);

        FrameProfiler.Begin(ProfileSection.Raycast);
        _hasSelectedBlock = Camera.Raycast(InteractionReach, _chunkManager,
            out _selectedBlockPosition, out _selectedFaceNormal, out _selectedBoxIndex,
            includeFluids: ItemRegistry.TryGet(Inventory.SelectedBlockId)?.TargetsFluids ?? false);
        _selectedAimDirection = Camera.LastAimDirection;
        FrameProfiler.End(ProfileSection.Raycast);
    }

    /// <summary>
    /// The local player's body for this frame: the held item, and the walk cycle driven by real
    /// horizontal speed so the limbs keep up with sprint and swim instead of playing a fixed-rate
    /// loop. Flying poses no stride, so it feeds zero speed and the limbs ease back to rest. The
    /// punch, slice and throw animations run inside it.
    /// </summary>
    private void UpdatePlayerModel(double deltaTime)
    {
        // The placement pop's clock runs every frame on every path (a client predicts its own
        // placements), independent of the mining pop's hit-timed one
        _placePopElapsed += (float)deltaTime;

        var camPos = Camera.Position;
        var horizontalSpeed = 0f;
        var headingForward = 0f;
        var headingSide = 0f;
        if (_hasPrevCamPos && deltaTime > 0.0)
        {
            var step = camPos - _prevCamPos;
            horizontalSpeed = new Vector3(step.X, 0f, step.Z).Length() / (float)deltaTime;
            // The velocity along and across the body's facing, which tips a swimmer toward its heading.
            var yaw = MathUtils.ToRadians(Camera.PlayerYaw);
            var fx = MathF.Cos(yaw);
            var fz = MathF.Sin(yaw);
            headingForward = (step.X * fx + step.Z * fz) / (float)deltaTime;
            headingSide = (-step.X * fz + step.Z * fx) / (float)deltaTime;
        }
        _prevCamPos = camPos;
        _hasPrevCamPos = true;
        var previousHeld = _heldId;
        _heldId = Inventory.SelectedBlockId;
        // Swapping what is in hand dips the arm and springs it back up (not on the first frame).
        if (_equipTracked && _heldId != previousHeld) Player.NotifyEquip();
        _equipTracked = true;
        _hasHeld = _heldId != 0 && (Inventory.SelectedCount > 0 || Inventory.SelectedToolInstance > 0);
        Player.HoldingItem = _hasHeld;
        // The shoulder camera shows the body from outside, where it trails the look; first person
        // and orbit (which turns the body itself) keep it on the look.
        Player.BodyYawLags = Camera.IsShoulderSurf && !Camera.IsOrbit;
        Player.SetLook(Camera.PlayerYaw, Camera.Pitch);
        var animSpeed = Camera.IsFlying ? 0f : horizontalSpeed;
        // How deep the body stands (WaterImmersion): wading walks with no water pose, floating at the
        // surface takes the upright float pose and only an eye under water swims
        var waterPhase = Camera.IsFlying ? WaterPhase.Dry : Camera.WaterPhase;
        var supported = Camera.IsGrounded || Camera.IsFlying || waterPhase >= WaterPhase.Float;
        // A swim stroke the physics just started thrusts the arms out on the same frame.
        var strokes = Camera.SwimStrokeCount;
        if (strokes != _lastSwimStrokes) Player.TriggerSwimStroke();
        _lastSwimStrokes = strokes;
        Player.Update((float)deltaTime, animSpeed, Camera.IsSprinting, supported,
            Camera.VerticalVelocity, Camera.IsSneaking, waterPhase >= WaterPhase.Float,
            headingForward, headingSide, floating: waterPhase == WaterPhase.Float);
        // A hit-stop holds the punch pinch with the arm (read by the camera's next update)
        Camera.HoldPunchFov = Player.IsHitStopped;
        // The throw being wound up narrows the view with the arm (ThrowZoom, the camera's next update)
        Camera.ThrowCharge = Player.ThrowCharge;
        // The dust a landing or a sprinting footfall kicks up: cosmetic, so a headless server spawns none
        if (!_serverMode)
            _dust.UpdatePlayer(Camera, Player.WalkPhase, horizontalSpeed, _chunkManager, _debris);
    }

    private int _lastSwimStrokes;

    // The local player's water-splash memory (its swim state, Camera.IsInWater)
    private readonly WaterSplashTracker _playerSplash = new();

    /// <summary>
    /// Every body that crossed a water surface this frame throws droplets off it
    /// (<see cref="WaterSplash.Track"/>): the local player by its swim state, and the mobs, drops,
    /// projectiles, falling cubes and primed TNT by their lowest point. A client's mobs and TNT are
    /// mirrors that keep their trackers across snapshots and splash from the snapshot positions; its
    /// drops and falling cubes are rebuilt each snapshot, so they never do. Cosmetic: never on a server.
    /// </summary>
    private void UpdateSplashes(float deltaTime)
    {
        if (_serverMode)
            return;
        WaterSplash.Track(_playerSplash, Camera.Position - new Vector3(0f, Camera.EyeHeight, 0f), deltaTime, 1f,
            _chunkManager, _debris, inWater: Camera.IsInWater);
        for (var i = 0; i < _mobs.Count; i++)
        {
            var mob = _mobs[i];
            WaterSplash.Track(mob.Splash, mob.Position - new Vector3(0f, mob.BodyHalfHeight, 0f), deltaTime,
                mob.BodyHalfExtent / PlayerPhysics.PlayerRadius, _chunkManager, _debris);
        }
        // Index loops over the read-only lists, so a frame allocates no enumerator
        var drops = _dropManager.Drops;
        for (var i = 0; i < drops.Count; i++)
            if (!drops[i].IsAttracted)
                WaterSplash.Track(drops[i].Splash, drops[i].Position - new Vector3(0f, ItemDrop.Size * 0.5f, 0f),
                    deltaTime, WorldGenerationConfig.WaterSplashSmallScale, _chunkManager, _debris);
        for (var i = 0; i < _projectiles.Count; i++)
            WaterSplash.Track(_projectiles[i].Splash, _projectiles[i].Position, deltaTime,
                WorldGenerationConfig.WaterSplashSmallScale, _chunkManager, _debris);
        var falling = _falling.Falling;
        for (var i = 0; i < falling.Count; i++)
            WaterSplash.Track(falling[i].Splash, falling[i].Position - new Vector3(0f, FallingBlock.HalfExtent, 0f),
                deltaTime, WorldGenerationConfig.WaterSplashCubeScale, _chunkManager, _debris);
        var primed = _tntManager.Primed;
        for (var i = 0; i < primed.Count; i++)
            WaterSplash.Track(primed[i].Splash, primed[i].Position - new Vector3(0f, silkgltriangle.PrimedTnt.HalfExtent, 0f),
                deltaTime, WorldGenerationConfig.WaterSplashCubeScale, _chunkManager, _debris);
    }

    /// <summary>
    /// The local player's bubbles: the steady exhale while the eye is under, plus a burst from the
    /// head on each swim stroke (not in lava). Cosmetic, so singleplayer and a client run it and a
    /// server, whose hovering camera is nobody, does not.
    /// </summary>
    private void UpdateBubbles(float deltaTime)
    {
        if (_serverMode)
            return;
        _bubbles.TrackSwimStrokes(Camera.SwimStrokeCount, Camera.Position,
            Vector3Utils.FromYawPitch(Camera.PlayerYaw, 0f), Camera.IsEyeInWater && !Camera.IsEyeInLava);
        _bubbles.Update(deltaTime, Camera.RenderEye, Camera.ViewDirection, Camera.IsEyeInWater, _chunkManager);
    }

    private bool _equipTracked;

    /// <summary>Scenario seam: damages every live mob, so a script can film the hit reaction and the death.</summary>
    internal void DamageAllMobs(int amount)
    {
        RefreshMobs();
        for (var i = 0; i < _mobs.Count; i++)
        {
            var hits = _mobs[i].HitsTaken;
            _mobs[i].TakeDamage(amount);
            ShowLandedHit(_mobs[i], hits);
        }
    }

    /// <summary>Scenario seam: takes health off the local player's pool (the hearts, the damage flash
    /// and the low-health vignette all follow it), so a script can film the low-health HUD.</summary>
    internal void DamagePlayer(int amount) => _health.TakeDamage(amount);

    /// <summary>Scenario seam: primes the primable block (TNT) in <paramref name="cell"/> the way a
    /// firestarter strike does, minus its chance roll, so a script can film a blast. False when the
    /// cell holds nothing primable.</summary>
    internal bool TestPrimeTnt(Vector3 cell)
    {
        var x = MathUtils.FloorToInt(cell.X);
        var y = MathUtils.FloorToInt(cell.Y);
        var z = MathUtils.FloorToInt(cell.Z);
        IItemUseWorld use = this;
        if (!_chunkManager.TryGetCell(x, y, z, out var id, out _) || !use.IsPrimedTntBlock(id))
            return false;
        var at = new Vector3(x, y, z);
        if (!use.RemoveCellForPrime(at))
            return false;
        use.SpawnPrimedTnt(at, id);
        use.ScheduleFluidAround(at);
        use.MarkLodColumnEdited(x, y, z);
        return true;
    }

    /// <summary>Scenario seam: makes the player's model play one reaction or swing, so a script can film it.</summary>
    internal void TestPlayerAction(string action)
    {
        switch (action)
        {
            case "hurt": NotifyPlayerHurt(fromMob: false); break;
            case "punch": PunchSwing(); break;
            case "slice": Player.StartSlice(1); break;
            case "equip": Player.NotifyEquip(); break;
            case "charge": Player.StartThrowCharge(); Player.UpdateThrowCharge(1f); break;
            case "release": Player.ReleaseThrow(1f); break;
            case "die": Player.DeathFall = 1f; Camera.DeathProgress = 1f; break;
            case "revive": Player.DeathFall = 0f; Camera.DeathProgress = 0f; break;
        }
    }

    // A swing of the fist: the arm throws the jab and the view pinches in for a beat. Returns the
    // swing's weight, which a connecting punch's hit-stop reads.
    private float PunchSwing()
    {
        // A slower tool swings heavier: from the bare hand's pace (no weight) up to a tool with
        // a long hit gap.
        var gap = Mining.HitGapSeconds(ItemRegistry.TryGet(Inventory.SelectedBlockId));
        var weight = MathUtils.InverseLerp(Mining.HitGapSeconds(null), Mining.HitGapSeconds(null) + 0.5f, gap);
        Player.Punch(weight);
        Camera.PunchFov(-0.8f - 0.8f * weight);
        return weight;
    }

    /// <summary>
    /// Plays the player's hurt feedback: the torso flinches away from the blow and the view rolls
    /// and dips. <paramref name="fromMob"/> finds the nearest living mob and reads the blow's
    /// direction off it; a fall or lava burn has none and just rocks the body back.
    /// </summary>
    private void NotifyPlayerHurt(bool fromMob)
    {
        if (!fromMob)
        {
            Player.Hurt(0f);
            Camera.AddHitKick(Vector3.Zero, 0.5f);
            return;
        }

        Mob? nearest = null;
        var best = 8f * 8f;
        for (var i = 0; i < _mobs.Count; i++)
        {
            var d = Vector3.DistanceSquared(_mobs[i].Position, Camera.Position);
            if (d >= best) continue;
            best = d;
            nearest = _mobs[i];
        }

        if (nearest == null)
        {
            Player.Hurt(0f);
            Camera.AddHitKick(Vector3.Zero, 0.5f);
            return;
        }

        var toMob = new Vector3(nearest.Position.X - Camera.Position.X, 0f, nearest.Position.Z - Camera.Position.Z);
        if (toMob.LengthSquared() < 1e-6f)
        {
            Player.Hurt(0f);
            return;
        }

        toMob = Vector3.Normalize(toMob);
        var forward = Camera.GetForwardVector();
        forward.Y = 0f;
        var fromFront = forward.LengthSquared() > 1e-6f ? Vector3.Dot(toMob, Vector3.Normalize(forward)) : 0f;
        Player.Hurt(fromFront);
        Camera.AddHitKick(-toMob);
    }

    // Seconds the player's death plays (the view tips over, the body falls) before the respawn.
    private const float DeathPlaySeconds = 0.9f;
    private float _deathTimer;

    /// <summary>
    /// One gameplay frame. Each block of work runs under its own profiler section; the autosave
    /// runs under <see cref="ProfileSection.Save"/>, which never nests inside another section.
    /// While <see cref="WorldPhase.Loading"/> only the streaming runs (<see cref="UpdateLoading"/>),
    /// and once <see cref="WorldPhase.Disposed"/> it does nothing.
    /// </summary>
    public void Update(double deltaTime, IKeyboard? keyboard)
    {
        if (_window == null)
            _headlessTime += deltaTime;
        if (Phase == WorldPhase.Disposed)
            return;
        if (_clientMode)
        {
            UpdateClient(deltaTime, keyboard);
            return;
        }
        if (Phase == WorldPhase.Loading)
        {
            UpdateLoading(deltaTime);
            return;
        }

        FrameProfiler.Begin(ProfileSection.Player);
        EnsureSpawnSettled();
        FrameProfiler.End(ProfileSection.Player);

        FrameProfiler.Begin(ProfileSection.Camera);
        Camera.Update((float)deltaTime, keyboard, _chunkManager);
        FrameProfiler.End(ProfileSection.Camera);

        FrameProfiler.Begin(ProfileSection.Player);

        // A server's hovering camera is nobody: its pool never changes, and each connected
        // player's status is ticked by the server (ServerPlayer.TickStatus)
        if (!_serverMode)
            UpdateHealth((float)deltaTime);
        UpdatePlayerModel(deltaTime);

        // The chunk update below times itself, so the player span closes here.
        FrameProfiler.End(ProfileSection.Player);

        // After the camera moved this frame, as the chunk update always read it; the chunk update
        // times itself, as ChunkUnload / ChunkApply / ChunkDirty
        UpdateFocus();
        _chunkManager.Update(Focus.Chunks);

        FrameProfiler.Begin(ProfileSection.Lod);
        _lodManager.Update(_chunkManager);
        FrameProfiler.End(ProfileSection.Lod);

        FrameProfiler.Begin(ProfileSection.Entities);

        // Saved mobs and drops whose chunk has streamed in since the flip; RefreshMobs below
        // picks the new mobs up this same frame. Returns at once once nothing is pending.
        RestorePendingEntities(deltaTime);

        // Drops collected this tick land in the inventory pools. A full pool ignores the
        // drop (it is never attracted), and an id with no pool is discarded. The collectors are
        // the local player (singleplayer and a client), or every connected player on a server
        // (which has no player of its own), plus every goblin with ammo room, which scavenges
        // flint off the ground as its own ammo.
        _dropCollectors.Clear();
        if (_serverMode)
        {
            var players = ServerPlayers;
            for (var i = 0; i < players.Count; i++)
                _dropCollectors.Add(new DropCollector(players[i].Position, players[i].CanAcceptDrop));
        }
        else
        {
            _dropCollectors.Add(new DropCollector(Camera.Position, _canAcceptDrop));
        }

        // A client's goblins are mirrors whose ammo is the server's (EntityList): never scavenge
        var goblinCollectors = _dropCollectors.Count;
        if (!_clientMode)
            _goblins?.AppendCollectors(_dropCollectors);
        _dropManager.Update((float)deltaTime, _dropCollectors, _chunkManager);

        var pickedUp = false;
        var collected = _dropManager.Collected;
        var collectors = _dropManager.CollectedBy;
        for (var i = 0; i < collected.Length; i++)
        {
            if (collectors[i] >= goblinCollectors)
            {
                _goblins!.AwardScavenged(collectors[i] - goblinCollectors, collected[i]);
                continue;
            }

            if (_serverMode)
            {
                var player = ServerPlayers[collectors[i]];
                if (player.Inventory.Add(collected[i]))
                    player.InventoryChanged = true;
                continue;
            }

            pickedUp |= Inventory.Add(collected[i]);
        }

        if (pickedUp)
        {
            _sfx?.Play(SfxLibrary.PickupGroup);
            // Bright motes pop where the drop was collected, at the chest
            _fire.Puff(Camera.Position - new Vector3(0f, WorldGenerationConfig.PickupSparkleDrop, 0f),
                WorldGenerationConfig.PickupSparkleCount, WorldGenerationConfig.PickupSparkleSpread,
                FireParticleKind.Spark);
        }

        _tntManager.Update((float)deltaTime, _chunkManager, _lodManager, _dropManager, _fluidManager, _falling, _leaves, _leafDecay);
        // Every blast this frame shoves the players and mobs in range and shakes the local camera by its
        // distance; a server's camera is nobody
        foreach (var blast in _tntManager.Blasts)
        {
            PushFromBlast(blast);
            if (!_serverMode)
                ShakeForBlast(blast);
        }
        _falling.Update((float)deltaTime, _chunkManager, _dropManager, _fluidManager, _lodManager);
        // A cube that settled throws a skirt of its own chips; cosmetic, so never on a server
        if (!_serverMode)
            foreach (var landing in _falling.Landings)
                GroundDust.LandBlock(landing.Cell, landing.BlockId, landing.FallBlocks, landing.Submerged, _debris,
                    _chunkManager.ColumnSource);
        _leaves.Update((float)deltaTime, Camera.RenderEye, _chunkManager, windTime: LightingTime);
        _debris.Update((float)deltaTime, _chunkManager);
        _campfires.Update((float)deltaTime, _chunkManager, _dropManager);
        _querns.Update((float)deltaTime, _chunkManager, _dropManager);
        _kilns.Update((float)deltaTime, _chunkManager, _dropManager, _lodManager);
        // A pour bound to a connected player (its PlayerContext) drains that player's crucible; the
        // world's own inventory and camera stand for the local player
        _casting.Update((float)deltaTime, _chunkManager, _dropManager, Inventory,
            Inventory.SelectedToolInstance, Camera.Position, InteractionReach);
        _fireCells.Clear();
        _fireCells.AddRange(_campfires.LitCells);
        _fireCells.AddRange(_kilns.LitCells);
        // The lighting clock (window time plus VOXEL_TIME), so the smoke blows the way the leaves lean
        var wind = Wind.Direction(LightingTime);
        _fire.Update((float)deltaTime, _fireCells, Camera.RenderEye, wind: wind);
        GatherLavaVents(Camera.RenderEye);
        _lavaFire.Update((float)deltaTime, _lavaVents, Camera.RenderEye, wind: wind);
        UpdateTorchFlames((float)deltaTime, wind);
        UpdateBubbles((float)deltaTime);

        // Falling cubes shove the player aside instead of landing inside them; strictly
        // one-way, the player never deflects a cube
        foreach (var block in _falling.Falling)
            Camera.PushOutOf(block.Position, FallingBlock.HalfExtent, _chunkManager);

        // The players the mobs chase, rebuilt each frame. Singleplayer: the local camera, whose
        // TakeHit applies only the knockback, since the damage below still lands on this world's
        // own pool. A server: every connected player, each taking its hits on its own pool
        // (ServerPlayer.TakeHit), never the hovering camera, so nobody online means nobody chased.
        _mobTargets.Clear();
        if (_serverMode)
        {
            var players = ServerPlayers;
            for (var i = 0; i < players.Count; i++)
                _mobTargets.Add(players[i]);
        }
        else
        {
            _mobTargets.Add(Camera);
        }

        // Slimes hop at the player: an airborne touch bills damage, a grounded one is just
        // shoved aside. The manager only reports the amount; the health pool stays here (a
        // server's players already took theirs through TakeHit).
        if (_slimes != null)
        {
            // The lighting clock's night, so the ring rolls larger slimes after dark
            _slimes.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, DayCycle.NightFactor(LightingTime),
                _serverMode ? null : _debris);
            if (!_serverMode && _slimes.DamageDealtThisFrame > 0)
            {
                _health.TakeDamage(_slimes.DamageDealtThisFrame);
                Camera.AddShake(Math.Clamp(_slimes.DamageDealtThisFrame / 20f, 0.2f, 1f));
                NotifyPlayerHurt(fromMob: true);
            }
        }

        // Goblins lob their flint and then close in for a club swing; like the slimes, the
        // manager only reports the damage and the health pool stays here.
        if (_goblins != null)
        {
            _goblins.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, _serverMode ? null : _debris,
                _slimes?.Slimes);
            var goblinDamage = _goblins.DamageDealtThisFrame;
            if (!_serverMode && goblinDamage > 0)
            {
                _health.TakeDamage(goblinDamage);
                Camera.AddShake(Math.Clamp(goblinDamage / 20f, 0.2f, 1f));
                NotifyPlayerHurt(fromMob: true);
            }
        }

        // After every mob manager and before the thrown items, so a mob killed or despawned
        // this frame is never hit by a projectile or drawn a healthbar.
        RefreshMobs();
        // A mob that landed this frame kicks up its dust; a server spawns no particles
        if (!_serverMode)
            _dust.TakeMobLandings(_mobs, _chunkManager, _debris);

        UpdateToppling((float)deltaTime);
        UpdateThrown((float)deltaTime);
        RefreshProjectiles();
        UpdateSplashes((float)deltaTime);
        // Remote player poses ease toward the last broadcast and drive their own walk cycles.
        for (var i = 0; i < _remotePlayers.Count; i++)
            _remotePlayers[i].Update(deltaTime);
        FrameProfiler.End(ProfileSection.Entities);

        // Chunks that streamed in this frame hand over their water/air frontier, so a sea a
        // cave broke into starts flowing as soon as it is loaded rather than waiting for the
        // player to disturb something next to it
        FrameProfiler.Begin(ProfileSection.FluidWake);
        _fluidManager.WakeLoadedChunks(_chunkManager);
        FrameProfiler.End(ProfileSection.FluidWake);

        FrameProfiler.Begin(ProfileSection.FluidTick);

        // Runs after every path that can edit a block this frame, so the cells they scheduled
        // are all in the same tick
        _fluidManager.Update((float)deltaTime, _chunkManager, _dropManager, _lodManager);
        // Lava that hardened this frame gives off steam; cosmetic, so never on a server
        if (!_serverMode)
            PuffSteam(_fluidManager.HardenedCells);

        FrameProfiler.End(ProfileSection.FluidTick);

        // Like the fluid tick: after every edit path of the frame, so the leaves and orphaned
        // wood they scheduled re-derive together, and never while loading (UpdateLoading skips it)
        FrameProfiler.Begin(ProfileSection.LeafDecay);
        _leafDecay.Update((float)deltaTime, _chunkManager, _dropManager, _lodManager, _leaves);
        FrameProfiler.End(ProfileSection.LeafDecay);

        // A client never grows anything itself: the server owns the world and broadcasts its cells
        if (!_clientMode)
            _saplings.Update((float)deltaTime, _chunkManager,
                (species, x, y, z, steps) => TryGrowTree(species, x, y, z, steps));

        // After the fluid tick, so every edit path of the frame precedes the autosave. The one
        // Save span in the codebase, entered here only and never nested inside another section.
        FrameProfiler.Begin(ProfileSection.Save);
        if (_saveStore != null)
        {
            _autosaveTimer += deltaTime;
            if (_autosaveTimer >= WorldGenerationConfig.AutosaveIntervalSeconds)
                Autosave();
        }
        FrameProfiler.End(ProfileSection.Save);

        _hotbarPops.Update(Inventory, (float)deltaTime);
        _hotbarName.Update(Inventory, (float)deltaTime);
        _damageNumbers.Update((float)deltaTime);

        // Water is aimed through unless the held item is out to scoop it, so a pond never
        // stops the player targeting the block under it
        FrameProfiler.Begin(ProfileSection.Raycast);
        _hasSelectedBlock = Camera.Raycast(InteractionReach, _chunkManager,
            out _selectedBlockPosition, out _selectedFaceNormal, out _selectedBoxIndex,
            includeFluids: ItemRegistry.TryGet(Inventory.SelectedBlockId)?.TargetsFluids ?? false);
        _selectedAimDirection = Camera.LastAimDirection;
        FrameProfiler.End(ProfileSection.Raycast);
    }

    /// <summary>
    /// The server's drop pickup: each connected player (<see cref="ServerPlayers"/>) is a collector
    /// at their eye, a drop flies to the nearest one with room for it, and what lands is credited
    /// to that player's own inventory, flagging it for a fresh snapshot. The server camera (the
    /// chunk focus) never collects anything.
    /// </summary>
    /// <summary>
    /// The <see cref="WorldPhase.Loading"/> frame: streaming only. Chunks and far LOD stream and
    /// upload under the loading budgets, the spawn settles and freshly loaded water wakes, but
    /// there is no camera physics (unloaded cells read as air and the player would fall), no
    /// health, mobs, drops, fluid tick, autosave or raycast. The gate's sphere walk runs only
    /// here, never in the Playing hot path.
    /// </summary>
    private void UpdateLoading(double deltaTime)
    {
        FrameProfiler.Begin(ProfileSection.Player);
        EnsureSpawnSettled();
        FrameProfiler.End(ProfileSection.Player);

        // After the spawn settled this frame; the chunk update times itself, as ChunkUnload /
        // ChunkApply / ChunkDirty
        UpdateFocus();
        _chunkManager.Update(Focus.Chunks);

        FrameProfiler.Begin(ProfileSection.Lod);
        _lodManager.Update(_chunkManager);
        FrameProfiler.End(ProfileSection.Lod);

        FrameProfiler.Begin(ProfileSection.FluidWake);
        _fluidManager.WakeLoadedChunks(_chunkManager);
        FrameProfiler.End(ProfileSection.FluidWake);

        var gateDone = _loadGate!.Update(_chunkManager, Camera.Position, deltaTime);
        if ((gateDone && _spawnSettled) || _loadGate.TimedOut)
            FinishLoading();
    }

    /// <summary>
    /// The Loading -> Playing flip: the play budgets and mouse look come back, the saved lighting
    /// clock is applied against the clock as it stands now (so the day did not advance while
    /// loading), and the walk cycle forgets the pre-load position so the whole load never reads
    /// as one enormous step. The saved mobs and drops whose chunks the ready sphere already covers
    /// spawn here, before any manager has run; the rest keep waiting for their chunks.
    /// </summary>
    private void FinishLoading()
    {
        _chunkManager.MeshUploadBudget = WorldGenerationConfig.MaxMeshUploadsPerFrame;
        _chunkManager.DirtyStartBudget = WorldGenerationConfig.MaxDirtyChunksPerFrame;
        _lodManager.UploadBudget = WorldGenerationConfig.MaxLodUploadsPerFrame;
        Camera.SuppressLook = false;
        if (_savedPlayer?.LightingClock is { } clock)
            TimeOffset = clock - Now;
        Phase = WorldPhase.Playing;
        _hasPrevCamPos = false;
        RestorePendingEntities(0);
        RefreshMobs();
        // The clock is applied and the capture no longer needs the saved one
        _savedPlayer = null;
    }

    /// <summary>
    /// Spawns every pending saved mob and drop whose chunk is loaded, through the managers' own
    /// spawn paths, and restores its health, facing and motion. Each waits for its chunk because
    /// an entity restored over an unloaded chunk would fall through air (slimes, goblins and drops
    /// all read unloaded cells as air). A restore is a forced spawn: it may pass the natural mob cap
    /// but a mob waits the same way while the hard cap (<see cref="WorldGenerationConfig.MobHardCap"/>,
    /// which sizes the healthbar buffers) is full, so an over-full save never overfills the world
    /// and loses nothing, since pending entries are re-saved. An unknown entity kind or item name, or a record with no
    /// position, is dropped with one warning per pass. After
    /// <see cref="WorldGenerationConfig.EntityRestoreTimeoutSeconds"/> of play whatever is still
    /// pending is discarded. Main thread, Playing only; nothing pending costs one check.
    /// </summary>
    private void RestorePendingEntities(double deltaTime)
    {
        if (_pendingMobs.Count == 0 && _pendingDrops.Count == 0)
            return;
        _entityRestoreTimer += deltaTime;
        List<string>? dropped = null;

        for (var i = _pendingMobs.Count - 1; i >= 0; i--)
        {
            var mob = _pendingMobs[i];
            var type = mob.Type == null ? null : EntityRegistry.TryGet(mob.Type);
            var known = type != null
                && ((_slimes != null && string.Equals(type.Name, "slime", StringComparison.OrdinalIgnoreCase))
                    || (_goblins != null && string.Equals(type.Name, "goblin", StringComparison.OrdinalIgnoreCase)));
            if (!known || mob.Position is not { Length: 3 } p)
            {
                (dropped ??= []).Add($"mob '{mob.Type ?? "?"}'");
                _pendingMobs.RemoveAt(i);
                continue;
            }

            var centre = new Vector3(p[0], p[1], p[2]);
            if (!IsCellLoaded(centre))
                continue;

            // A forced spawn, bounded by the hard cap rather than the natural one: a save only holds
            // mobs that lived together (splits may have taken them past the natural cap) plus entries
            // still pending, so restoring up to the hard cap reproduces the saved world, and the ring
            // spawners stay refused until the population falls under the natural cap again. At a full
            // hard cap the entry waits like an unloaded chunk: it stays pending (and is re-saved by
            // CaptureEntities) instead of overfilling the cap-sized healthbar buffers.
            var isSlime = string.Equals(type!.Name, "slime", StringComparison.OrdinalIgnoreCase);
            // A file saved before slimes had sizes carries none: those were all the smallest size
            Mob? spawned = isSlime
                ? _slimes!.Spawn(centre, mob.Size ?? SlimeSize.Min)
                : _goblins!.Spawn(centre, mob.Ammo ?? 0);
            if (spawned == null)
                continue;

            var velocity = ToVector(mob.Velocity);
            spawned.RestoreState(mob.Health ?? type.MaxHealth, mob.Facing ?? 0f, velocity);
            _pendingMobs.RemoveAt(i);
        }

        for (var i = _pendingDrops.Count - 1; i >= 0; i--)
        {
            var drop = _pendingDrops[i];
            if (!SaveNames.TryResolve(drop.Item, out var id) || drop.Position is not { Length: 3 } p)
            {
                (dropped ??= []).Add($"drop '{drop.Item ?? "?"}'");
                _pendingDrops.RemoveAt(i);
                continue;
            }

            var position = new Vector3(p[0], p[1], p[2]);
            if (!IsCellLoaded(position))
                continue;

            if (!_dropManager.SpawnRestored(id, position, ToVector(drop.Velocity), drop.Age ?? 0f))
                (dropped ??= []).Add($"drop '{drop.Item}'");
            _pendingDrops.RemoveAt(i);
        }

        if (dropped != null)
            Log.Warn($"Entity save: dropped {dropped.Count} unrestorable entr{(dropped.Count == 1 ? "y" : "ies")}: {string.Join(", ", dropped)}");

        if (_entityRestoreTimer >= WorldGenerationConfig.EntityRestoreTimeoutSeconds
            && (_pendingMobs.Count > 0 || _pendingDrops.Count > 0))
        {
            Log.Warn($"Entity save: discarded {_pendingMobs.Count} mob(s) and {_pendingDrops.Count} drop(s) whose chunk never loaded or that the full mob hard cap kept out");
            _pendingMobs.Clear();
            _pendingDrops.Clear();
        }
    }

    // Whether the chunk owning the cell under a world position is loaded
    private bool IsCellLoaded(Vector3 position) =>
        _chunkManager.ChunkAt(MathUtils.FloorToInt(position.X), MathUtils.FloorToInt(position.Y),
            MathUtils.FloorToInt(position.Z)) != null;

    // A saved float[3] as a vector; anything else is zero
    private static Vector3 ToVector(float[]? v) => v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

    /// <summary>
    /// One autosave pass: snapshots up to <see cref="WorldGenerationConfig.MaxAutosaveSnapshotsPerCall"/>
    /// modified loaded chunks to the save store (the copies are cheap; encoding and disk I/O run
    /// on its writer thread), then the far-LOD edit state if it changed, the player and the
    /// entities. A burst
    /// that fills the cap leaves the timer due, so the rest continues next frame instead of
    /// stalling this one.
    /// </summary>
    private void Autosave()
    {
        var snapshots = _chunkManager.FlushModified(WorldGenerationConfig.MaxAutosaveSnapshotsPerCall);
        if (snapshots >= WorldGenerationConfig.MaxAutosaveSnapshotsPerCall)
        {
            _autosaveTimer = WorldGenerationConfig.AutosaveIntervalSeconds;
            return;
        }

        _autosaveTimer = 0;
        SaveLodEditStateIfChanged();
        SavePlayerState();
        SaveEntityState();
    }

    /// <summary>
    /// The player as <see cref="PlayerSaveCodec"/> writes it. While still Loading the saved clock
    /// has not been applied yet, so a flush then keeps the saved one instead of overwriting it
    /// with an unrestored clock. No position is written before the spawn settled: until
    /// <c>EnsureSpawnSettled</c> has placed the player, <see cref="Camera.Position"/> is the
    /// pre-settle hold eye, and saving it would make <c>ApplySavedPlayer</c> mark the spawn settled
    /// on the next load and skip the surface search. A close mid-load therefore persists everything
    /// but the pose, and the next load searches again.
    /// </summary>
    private PlayerSaveData CapturePlayer()
    {
        if (_serverMode)
            return CaptureHostedPlayer();

        var pos = Camera.Position;
        return new PlayerSaveData
        {
            Position = _spawnSettled ? [pos.X, pos.Y, pos.Z] : null,
            Yaw = Camera.Yaw,
            Pitch = Camera.Pitch,
            Creative = Camera.IsCreative,
            Flying = Camera.IsFlying,
            Health = _health.Health,
            LightingClock = Phase == WorldPhase.Loading ? _savedPlayer?.LightingClock ?? LightingTime : LightingTime,
            RespawnEye = _hasRespawnEye ? [_respawnEye.X, _respawnEye.Y, _respawnEye.Z] : null,
            Inventory = Inventory.Export()
        };
    }

    /// <summary>
    /// The player a server writes: never its own camera (forced creative and flying, parked at
    /// the players' centroid), but the player.json the world was opened with, with only the
    /// lighting clock refreshed. Its inventory too is written back as it was opened: every
    /// connected player carries their own (saved under <see cref="WorldStore.PlayersDirectoryName"/>),
    /// so nothing a client picks up or spends reaches the singleplayer owner's file. A world that
    /// had none gets no pose, no creative or flight, no respawn eye and no inventory, so opening it
    /// in singleplayer later searches the surface for a survival spawn as a new world would.
    /// </summary>
    private PlayerSaveData CaptureHostedPlayer()
    {
        var hosted = _hostedPlayer;
        return new PlayerSaveData
        {
            Position = hosted?.Position,
            Yaw = hosted?.Yaw,
            Pitch = hosted?.Pitch,
            Creative = hosted?.Creative ?? false,
            Flying = hosted?.Flying ?? false,
            Health = hosted?.Health ?? _health.Health,
            // Like a game world: until the flip has applied the saved clock, keep the saved one
            LightingClock = Phase == WorldPhase.Loading ? hosted?.LightingClock ?? LightingTime : LightingTime,
            RespawnEye = hosted?.RespawnEye,
            Inventory = hosted?.Inventory
        };
    }

    /// <summary>Enqueues <see cref="PlayerSaveCodec.FileName"/>; does nothing without a save store.</summary>
    private void SavePlayerState()
    {
        if (_saveStore == null)
            return;
        _saveStore.EnqueueFile(PlayerSaveCodec.FileName, PlayerSaveCodec.ToBytes(CapturePlayer()));
    }

    /// <summary>
    /// The live mobs and drops as <see cref="EntitySaveCodec"/> writes them: every living slime and
    /// goblin (kind by <see cref="EntityType.Name"/>, body centre, health, facing, velocity, a
    /// goblin's ammo, a slime's size) and the item drops nearest the player first, capped at
    /// <see cref="WorldGenerationConfig.MaxSavedDrops"/>, by <see cref="SaveNames"/> name (an id
    /// with no name is skipped with one warning). Entries still pending a restore are appended, so a
    /// flush during loading, or before a far entity's chunk arrived, never loses them.
    /// </summary>
    private EntitySaveData CaptureEntities()
    {
        var mobs = new List<EntitySaveData.MobData>();
        var slimes = Slimes;
        for (var i = 0; i < slimes.Count; i++)
            if (!slimes[i].IsDead)
                mobs.Add(MobSaveCodec.Capture(slimes[i], null, slimes[i].Size));
        var goblins = Goblins;
        for (var i = 0; i < goblins.Count; i++)
            if (!goblins[i].IsDead)
                mobs.Add(MobSaveCodec.Capture(goblins[i], goblins[i].Ammo));
        mobs.AddRange(_pendingMobs);

        _dropSort.Clear();
        var eye = Camera.Position;
        var liveDrops = _dropManager.Drops;
        for (var i = 0; i < liveDrops.Count; i++)
            _dropSort.Add((Vector3.DistanceSquared(liveDrops[i].Position, eye), liveDrops[i]));
        _dropSort.Sort(static (a, b) => a.dist2.CompareTo(b.dist2));

        var drops = new List<EntitySaveData.DropData>();
        var unnamed = 0;
        for (var i = 0; i < _dropSort.Count && drops.Count < WorldGenerationConfig.MaxSavedDrops; i++)
        {
            var drop = _dropSort[i].drop;
            if (SaveNames.NameOf(drop.BlockId) is not { } name)
            {
                unnamed++;
                continue;
            }

            drops.Add(new EntitySaveData.DropData
            {
                Item = name,
                Position = [drop.Position.X, drop.Position.Y, drop.Position.Z],
                Velocity = [drop.Velocity.X, drop.Velocity.Y, drop.Velocity.Z],
                Age = drop.Age
            });
        }
        _dropSort.Clear();
        for (var i = 0; i < _pendingDrops.Count && drops.Count < WorldGenerationConfig.MaxSavedDrops; i++)
            drops.Add(_pendingDrops[i]);

        if (unnamed > 0)
            Log.Warn($"Entity save: skipped {unnamed} drop(s) of an item with no save name");
        return new EntitySaveData { Mobs = mobs, Drops = drops };
    }

    /// <summary>Enqueues <see cref="EntitySaveCodec.FileName"/>; does nothing without a save store.</summary>
    private void SaveEntityState()
    {
        if (_saveStore == null)
            return;
        _saveStore.EnqueueFile(EntitySaveCodec.FileName, EntitySaveCodec.ToBytes(CaptureEntities()));
    }

    /// <summary>
    /// Enqueues <see cref="LodEditState.FileName"/> when the LOD edit state moved since the last
    /// write (<see cref="LodEditState.CombinedVersion"/>), or unconditionally when
    /// <paramref name="force"/> is set. Main thread only: <see cref="LodEditState.Capture"/>
    /// reads the process-global edit stores.
    /// </summary>
    private void SaveLodEditStateIfChanged(bool force = false)
    {
        if (_saveStore == null)
            return;
        var version = LodEditState.CombinedVersion;
        if (!force && version == _lodStateVersionSaved)
            return;
        _saveStore.EnqueueFile(LodEditState.FileName, LodEditState.Capture().ToBytes());
        _lodStateVersionSaved = version;
    }

    /// <summary>
    /// Hands every modified loaded chunk, the far-LOD edit state, the player
    /// (<see cref="PlayerSaveCodec.FileName"/>) and the mobs and drops
    /// (<see cref="EntitySaveCodec.FileName"/>) to the save store, so what the player sees now is
    /// what the save holds once the writer drains. Main thread only; no GL, no profiler span and no
    /// shutdown. <see cref="SaveAndDispose"/> is the caller that closes the world: it stops and
    /// joins the generation and LOD workers and drains the mesh runners
    /// (<see cref="ChunkManager.DrainMeshWork"/>) first, so this flush sees a quiescent world, then
    /// calls this, then <see cref="ChunkSaveStore.Shutdown"/> with
    /// <see cref="WorldGenerationConfig.SaveWriterShutdownTimeoutSeconds"/>, releases the geometry
    /// and finally <see cref="WorldSeed.EndWorld"/>. Does nothing without a save store.
    /// </summary>
    public void FlushAll()
    {
        if (_saveStore == null)
            return;
        _chunkManager.FlushModified(int.MaxValue);
        SaveLodEditStateIfChanged(force: true);
        SavePlayerState();
        SaveEntityState();
        _autosaveTimer = 0;
    }

    /// <summary>
    /// Saves and tears the world down so a second <see cref="World"/> (with any seed) can be built
    /// in this process. Main thread only, and blocking: up to
    /// <see cref="WorldGenerationConfig.WorkerStopJoinTimeoutMs"/> per worker thread, plus
    /// <see cref="WorldGenerationConfig.MeshDrainTimeoutMs"/> and
    /// <see cref="WorldGenerationConfig.SaveWriterShutdownTimeoutSeconds"/>.
    /// <list type="number">
    /// <item>The camera stops listening to the window and mouse.</item>
    /// <item>The generation and LOD workers are stopped and joined and the mesh runners drained
    /// FIRST, so the flush below sees a quiescent world and no thread can touch a chunk that is
    /// about to be freed.</item>
    /// <item><see cref="FlushAll"/> hands every edit, the player and the entities to the store, BEFORE
    /// <see cref="ChunkSaveStore.Shutdown"/>, since a store that stopped accepting drops every later
    /// write.</item>
    /// <item>Only when every join and the drain succeeded: every chunk and LOD slice goes back to
    /// the shared pools (<see cref="ChunkManager.ReleaseAll"/>, <see cref="LodManager.ReleaseAll"/>),
    /// the toppling-tree meshes are disposed and <see cref="WorldSeed.EndWorld"/> runs last. A
    /// timed-out join leaves all of that in place (freeing a slice a straggler may still write to
    /// would be worse) and keeps <see cref="WorldSeed.Apply"/> illegal; the process must not open
    /// another world then.</item>
    /// <item><paramref name="renderManager"/>, when given, forgets its references into this world
    /// (<see cref="RenderManager.ForgetWorld"/>); its pools and programs stay alive.</item>
    /// </list>
    /// The World owns the store's shutdown: the caller must not shut it down again, and must give
    /// the next World a NEW <see cref="ChunkSaveStore"/> over the same folder. Idempotent: a
    /// repeated call returns the first result. False means a join, the drain or the writer timed
    /// out. Afterwards <see cref="Update"/> is a no-op and <see cref="Render"/> must not be called.
    /// </summary>
    public bool SaveAndDispose(RenderManager? renderManager = null)
    {
        if (Phase == WorldPhase.Disposed)
            return _disposeResult;

        Camera.Detach();
        var genStopped = _generationWorker.Stop();
        var lodStopped = _lodManager.Stop();
        var drained = _chunkManager.DrainMeshWork(TimeSpan.FromMilliseconds(WorldGenerationConfig.MeshDrainTimeoutMs));
        FlushAll();
        var written = _saveStore?.Shutdown(TimeSpan.FromSeconds(WorldGenerationConfig.SaveWriterShutdownTimeoutSeconds)) ?? true;

        var quiet = genStopped && lodStopped && drained;
        if (quiet)
        {
            _chunkManager.ReleaseAll();
            _lodManager.ReleaseAll();
            foreach (var tree in _toppling)
                tree.Dispose();
            _toppling.Clear();
            WorldSeed.EndWorld();
        }
        else
        {
            Log.Warn("World teardown: a worker or mesh build outlived its timeout; the world's chunks were left in place and the seed cannot be changed in this process");
        }

        renderManager?.ForgetWorld();
        Phase = WorldPhase.Disposed;
        _disposeResult = quiet && written;
        return _disposeResult;
    }

    /// <summary>
    /// Fills <see cref="_lavaVents"/> with the world cells of the exposed hot-fluid vents
    /// (<see cref="Chunk.HotFluidVents"/>) within <see cref="WorldGenerationConfig.LavaVentRadius"/>
    /// of <paramref name="eye"/>, at most <see cref="WorldGenerationConfig.LavaVentMaxEmitters"/>
    /// of them. Chunks are walked nearest-first so a hard cap is spent on the pool beside the
    /// player, and when a lake offers more vents than the cap the set is thinned by a stable
    /// per-cell hash rather than truncated: truncating hands every emitter to the one chunk
    /// nearest the eye, which may lie entirely behind the camera.
    /// </summary>
    private void GatherLavaVents(Vector3 eye)
    {
        _lavaVents.Clear();
        _lavaVentChunks.Clear();
        const float radius = WorldGenerationConfig.LavaVentRadius;
        const float chunkReach = radius + Chunk.Size;
        const float half = Chunk.Size * 0.5f;
        foreach (var chunk in _chunkManager.TranslucentRenderable)
        {
            if (chunk.HotFluidVents is null) continue;
            var d2 = Vector3.DistanceSquared(chunk.Position * Chunk.Size + new Vector3(half), eye);
            if (d2 > chunkReach * chunkReach) continue;
            _lavaVentChunks.Add((d2, chunk));
        }
        if (_lavaVentChunks.Count == 0) return;
        _lavaVentChunks.Sort(NearestVentChunkFirst);

        // Pass 1 counts the candidates, so pass 2 can keep each with probability cap / count
        var candidates = 0;
        foreach (var (_, chunk) in _lavaVentChunks)
        {
            var origin = chunk.Position * Chunk.Size;
            foreach (var idx in chunk.HotFluidVents!)
                if (Vector3.DistanceSquared(origin + Chunk.LocalOfIdx(idx), eye) <= radius * radius)
                    candidates++;
        }
        if (candidates == 0) return;
        var keep = MathF.Min(1f, WorldGenerationConfig.LavaVentMaxEmitters / (float)candidates);
        foreach (var (_, chunk) in _lavaVentChunks)
        {
            var origin = chunk.Position * Chunk.Size;
            foreach (var idx in chunk.HotFluidVents!)
            {
                var cell = origin + Chunk.LocalOfIdx(idx);
                if (Vector3.DistanceSquared(cell, eye) > radius * radius) continue;
                if (keep < 1f && VentHash01(cell) >= keep) continue;
                _lavaVents.Add(cell);
                if (_lavaVents.Count >= WorldGenerationConfig.LavaVentMaxEmitters) return;
            }
        }
    }

    /// <summary>
    /// The torch flames: gathers every flame point (<see cref="Chunk.FlamePoints"/>) within
    /// <see cref="WorldGenerationConfig.TorchFlameRadius"/> of the eye, nearest chunks first and at most
    /// <see cref="WorldGenerationConfig.TorchFlameMaxEmitters"/> of them, and burns them in the torch
    /// particle instance, drifting with <paramref name="wind"/>. No point lights: a torch's own block
    /// light already lights it. Cosmetic: a headless server never runs it.
    /// </summary>
    private void UpdateTorchFlames(float deltaTime, Vector2 wind)
    {
        if (_serverMode)
            return;
        var eye = Camera.RenderEye;
        _torchFlames.Clear();
        _torchFlameChunks.Clear();
        const float radius = WorldGenerationConfig.TorchFlameRadius;
        const float chunkReach = radius + Chunk.Size;
        const float half = Chunk.Size * 0.5f;
        var chunks = _chunkManager.Renderable;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (chunk.FlamePoints is null) continue;
            var d2 = Vector3.DistanceSquared(chunk.Position * Chunk.Size + new Vector3(half), eye);
            if (d2 <= chunkReach * chunkReach)
                _torchFlameChunks.Add((d2, chunk));
        }

        if (_torchFlameChunks.Count > 1)
            _torchFlameChunks.Sort(NearestVentChunkFirst);
        foreach (var (_, chunk) in _torchFlameChunks)
        {
            var origin = chunk.Position * Chunk.Size;
            foreach (var local in chunk.FlamePoints!)
            {
                var point = origin + local;
                if (Vector3.DistanceSquared(point, eye) > radius * radius) continue;
                _torchFlames.Add(point);
                if (_torchFlames.Count >= WorldGenerationConfig.TorchFlameMaxEmitters) break;
            }
            if (_torchFlames.Count >= WorldGenerationConfig.TorchFlameMaxEmitters) break;
        }

        _torchFire.Update(deltaTime, _torchFlames, eye, wind: wind);
    }

    /// <summary>A stable 0..1 hash of a cell, so the thinned vent set does not flicker frame to frame.</summary>
    private static float VentHash01(Vector3 cell)
    {
        var h = (uint)(int)cell.X * 73856093u ^ (uint)(int)cell.Y * 19349663u ^ (uint)(int)cell.Z * 83492791u;
        h ^= h >> 13;
        h *= 0x5bd1e995u;
        h ^= h >> 15;
        return (h & 0xFFFFFF) / 16777216f;
    }

    /// <summary>
    /// First-cut survival tick, after <see cref="Camera.Update"/> so grounded state is fresh:
    /// tracks the fall from its highest feet position, burns in lava, decays the damage
    /// flash, and respawns at spawn keeping the inventory. A multi-block jump in one frame
    /// is a teleport (spawn settle, scripts), never a fall. Landing in water cushions the
    /// fall; creative and flight skip every damage source.
    /// </summary>
    private void UpdateHealth(float deltaTime)
    {
        if (_spawnSettled && !_hasRespawnEye)
        {
            _respawnEye = Camera.Position;
            _hasRespawnEye = true;
        }

        var beforeHp = _health.Health;
        var feet = Camera.Position.Y - Camera.EyeHeight;
        // A bounce block under the feet cushioned the landing (PlayerPhysics sprang the body back
        // up): it still counts as a landing, but bills no damage
        var damage = _fall.Observe(feet, Camera.IsGrounded, Camera.IsFlying, Camera.IsInWater, Camera.IsCreative,
            FallTracker.DefaultMaxStepBlocks, cushioned: PlayerPhysics.BounceBelow(Camera, _chunkManager));
        if (damage > 0)
            _health.TakeDamage(damage);

        _health.UpdateLava(deltaTime, Camera.IsInLava, Camera.IsCreative || Camera.IsFlying);
        UpdateCactusContact(deltaTime);
        _health.Update(deltaTime);

        if (_health.Health < beforeHp)
        {
            Camera.AddShake(Math.Clamp((beforeHp - _health.Health) / 40f, 0.2f, 1f));
            NotifyPlayerHurt(fromMob: false);
        }

        if (!_health.IsDead)
        {
            _deathTimer = 0f;
            return;
        }

        // The death plays first: the view tips over and the body falls, then the respawn.
        _deathTimer += deltaTime;
        var progress = MathUtils.Clamp01(_deathTimer / DeathPlaySeconds);
        Camera.DeathProgress = progress;
        Player.DeathFall = progress;
        if (progress < 1f)
            return;

        _deathTimer = 0f;
        Camera.DeathProgress = 0f;
        Player.DeathFall = 0f;
        _health.Respawn();
        var home = _hasRespawnEye ? _respawnEye : Camera.Position;
        Camera.TestTeleport(home, Camera.Yaw, Camera.Pitch);
        _fall.Reset(home.Y - Camera.EyeHeight, grounded: false);
        Log.WriteLine("Respawned");
    }

    public void Render(RenderManager renderManager)
    {
        // Depth-only sun pass first: every world draw below samples the shadow map it fills
        FrameProfiler.Begin(ProfileSection.Shadows);
        renderManager.BeginGpuPass(ProfileSection.Shadows);
        renderManager.RenderShadows(Camera, _chunkManager.Renderable, _tntManager.Primed, _falling.Falling, _toppling, Player, Slimes,
            _lodManager.RegionList, goblins: Goblins);
        renderManager.EndGpuPass(ProfileSection.Shadows);
        FrameProfiler.End(ProfileSection.Shadows);

        FrameProfiler.Begin(ProfileSection.Reflection);
        renderManager.BeginGpuPass(ProfileSection.Reflection);
        // One frustum pass over the translucent draws feeds both this and the water pass, and
        // tells the reflection whether there is any water on screen to be worth rendering
        renderManager.CullWater(Camera, _chunkManager.TranslucentRenderable, _lodManager.RegionList);
        renderManager.RenderReflection(Camera, _chunkManager.Renderable, _lodManager.RegionList,
            new ReflectionEntities(_dropManager.Drops, _projectiles, _tntManager.Primed, _falling.Falling, Slimes, Goblins,
                _toppling, Player));
        renderManager.EndGpuPass(ProfileSection.Reflection);
        FrameProfiler.End(ProfileSection.Reflection);

        FrameProfiler.Begin(ProfileSection.LodPass);
        renderManager.BeginGpuPass(ProfileSection.LodPass);
        renderManager.RenderLod(Camera, _lodManager.RegionList);
        renderManager.EndGpuPass(ProfileSection.LodPass);
        FrameProfiler.End(ProfileSection.LodPass);

        FrameProfiler.Begin(ProfileSection.WorldPass);
        renderManager.BeginGpuPass(ProfileSection.WorldPass);
        renderManager.RenderWorld(Camera, _chunkManager.Renderable, Now);
        renderManager.EndGpuPass(ProfileSection.WorldPass);
        FrameProfiler.End(ProfileSection.WorldPass);

        // The frustum test inside RenderWorld has just counted what is on screen
        FrameProfiler.SetCounts(_chunkManager.LoadedChunkCount, renderManager.LastVisibleChunkCount,
            _lodManager.CompletedBuildCount, _lodManager.RegionList.Count);

        // Free-list instrument for item D5, in the fixed order opaque/translucent/lod. Same call
        // site and same frame position as the counts above: the pools are render-thread-only and
        // this is where they already live.
        FrameProfiler.SetGeometryCounts(
            renderManager.OpaqueChunkGeometry.FreeBlockCount,
            renderManager.TranslucentChunkGeometry.FreeBlockCount,
            renderManager.LodGeometry.FreeBlockCount,
            renderManager.OpaqueChunkGeometry.PeakFreeBlockCount,
            renderManager.TranslucentChunkGeometry.PeakFreeBlockCount,
            renderManager.LodGeometry.PeakFreeBlockCount,
            renderManager.OpaqueChunkGeometry.ScanSteps,
            renderManager.TranslucentChunkGeometry.ScanSteps,
            renderManager.LodGeometry.ScanSteps);

        FrameProfiler.Begin(ProfileSection.EntityPass);
        renderManager.BeginGpuPass(ProfileSection.EntityPass);
        renderManager.RenderItemDrops(Camera, _dropManager.Drops);
        renderManager.RenderThrown(Camera, _projectiles);
        renderManager.RenderCampfireCooking(Camera, _campfires);
        renderManager.RenderQuerns(Camera, _querns);
        renderManager.RenderKilns(Camera, _kilns);
        renderManager.RenderCastings(Camera, _casting);
        renderManager.RenderPrimedTnt(Camera, _tntManager.Primed);
        renderManager.RenderFallingBlocks(Camera, _falling.Falling);
        renderManager.RenderSlimes(Camera, Slimes);
        renderManager.RenderGoblins(Camera, Goblins);
        renderManager.RenderRemotePlayers(Camera, _remotePlayers);
        renderManager.EndGpuPass(ProfileSection.EntityPass);
        FrameProfiler.End(ProfileSection.EntityPass);

        // Contact AO reads the scene depth, so it goes after everything above (which writes
        // honest depth) and before the healthbars and first-person head (drawn without depth, so
        // terrain AO would smudge them) and every later pass. Debris, toppling trees, the player
        // and the held item simply receive none.
        FrameProfiler.Begin(ProfileSection.AoPass);
        renderManager.BeginGpuPass(ProfileSection.AoPass);
        renderManager.RenderContactAo(Camera);
        renderManager.EndGpuPass(ProfileSection.AoPass);
        FrameProfiler.End(ProfileSection.AoPass);

        FrameProfiler.Begin(ProfileSection.EntityPass);
        renderManager.BeginGpuPass(ProfileSection.EntityPass);
        renderManager.RenderEntityHealthbars(Camera, Mobs);
        renderManager.RenderDamageNumbers(Camera, _damageNumbers);
        if (_goblins != null)
            renderManager.RenderNoticePips(Camera, _goblins.Goblins);
        renderManager.RenderBlockDebris(Camera, _debris.Debris);
        foreach (var tree in _toppling)
            renderManager.RenderTopplingTree(Camera, tree);
        renderManager.RenderPlayer(Camera, Player);
        renderManager.RenderHeldItem(Camera, Player, _heldId, _hasHeld,
            (float)Now, Compass.HeadingFromYaw(Camera.Yaw));
        renderManager.EndGpuPass(ProfileSection.EntityPass);
        FrameProfiler.End(ProfileSection.EntityPass);

        // The selection/breaking overlay is world geometry on the targeted block, so it draws with
        // the entities and BEFORE the water pass. Drawn after it, the water's written depth would
        // cull the overlay outright whenever the targeted block sits under the surface while the
        // eye is above it - the crack simply would not appear. Drawing it here lets the water blend
        // and refract over it exactly as it does over the block itself. Guarded rather than an early
        // return, which would skip the passes below.
        var placePopGrow = PlacePopGrowNow;
        if (_hasSelectedBlock || placePopGrow > 0f)
        {
            FrameProfiler.Begin(ProfileSection.SelectPass);
            renderManager.BeginGpuPass(ProfileSection.SelectPass);

            if (_hasSelectedBlock)
            {
                // While mining, the outline and the break overlay ride along with the popping block
                var popGrow = _isBreaking ? BlockPopGrowNow : 0f;

                // The chunk mesh still holds the block at rest size, so the popped cube is only
                // needed (and only hides it) while it is grown
                if (popGrow > 0f)
                    renderManager.RenderBlockPop(Camera, _breakingBlockPosition, _breakingBlockId, popGrow,
                        HitboxesAt(_breakingBlockPosition, _breakingBlockId));

                var selectedId = BlockIdAt(_selectedBlockPosition);
                renderManager.RenderBlockSelection(Camera, _selectedBlockPosition, _selectedFaceNormal, popGrow,
                    selectedId, _selectedBoxIndex, HitboxesAt(_selectedBlockPosition, selectedId), mining: _isBreaking);

                if (_isBreaking)
                    renderManager.RenderBlockBreaking(Camera, _breakingBlockPosition, BreakProgress, popGrow, _breakingBlockId,
                        HitboxesAt(_breakingBlockPosition, _breakingBlockId));
            }

            // A just-placed block pops in; skipped while that same cell is the one being mined,
            // whose own out-and-back pop is already playing there
            if (placePopGrow > 0f && !(_isBreaking && _breakingBlockPosition == _placePopPosition))
                renderManager.RenderBlockPop(Camera, _placePopPosition, _placePopId, placePopGrow,
                    HitboxesAt(_placePopPosition, _placePopId));

            renderManager.EndGpuPass(ProfileSection.SelectPass);
            FrameProfiler.End(ProfileSection.SelectPass);
        }

        // Water draws after the world and entities: it blends, so everything it can cover has to be
        // in the depth buffer already
        FrameProfiler.Begin(ProfileSection.WaterPass);
        renderManager.BeginGpuPass(ProfileSection.WaterPass);
        renderManager.RenderWorldTranslucent(Camera);
        renderManager.EndGpuPass(ProfileSection.WaterPass);
        FrameProfiler.End(ProfileSection.WaterPass);

        // Clouds blend too, so they go after water has written its depth
        FrameProfiler.Begin(ProfileSection.CloudPass);
        renderManager.BeginGpuPass(ProfileSection.CloudPass);
        renderManager.RenderClouds(Camera, Camera.IsEyeInWater);
        renderManager.EndGpuPass(ProfileSection.CloudPass);
        FrameProfiler.End(ProfileSection.CloudPass);

        // The depthless blended particles draw last, after the clouds, because none of them writes
        // depth: a cloud painted after them would cover every leaf and ember in front of it, since
        // the depth buffer still holds whatever is behind the particle rather than the particle.
        // Drawing them here lets the clouds' own depth pre-pass do the occluding - a particle in
        // front of a cloud passes the test and blends over it, one behind it is hidden - while the
        // terrain and water depth already in the buffer still hides particles behind them.
        // Bubbles ride along: they need the water pass' depth (they only exist submerged, where
        // the clouds are skipped anyway) and used to be the only pass drawn at this point.
        FrameProfiler.Begin(ProfileSection.EntityPass);
        renderManager.BeginGpuPass(ProfileSection.EntityPass);
        renderManager.RenderLeafParticles(Camera, _leaves.Particles);
        renderManager.RenderFireParticles(Camera, _fire.Particles);
        // Lava embers are a second batch; the renderer re-uploads with BufferSubData, so two
        // calls a frame are safe. The batches are not depth-sorted against each other, which
        // only matters where campfire and lava smoke overlap (cosmetic).
        renderManager.RenderFireParticles(Camera, _lavaFire.Particles);
        renderManager.RenderFireParticles(Camera, _torchFire.Particles);
        renderManager.RenderBubbleParticles(Camera, _bubbles.Particles);
        renderManager.EndGpuPass(ProfileSection.EntityPass);
        FrameProfiler.End(ProfileSection.EntityPass);

        // Additive sun shafts on top of the finished underwater view; it re-reads the depth
        // buffer, so it has to follow everything that writes depth
        FrameProfiler.Begin(ProfileSection.RaysPass);
        renderManager.BeginGpuPass(ProfileSection.RaysPass);
        renderManager.RenderUnderwaterRays(Camera);
        renderManager.EndGpuPass(ProfileSection.RaysPass);
        FrameProfiler.End(ProfileSection.RaysPass);

        // Godrays: the same march over dry air when the sun is in view; also after everything that
        // writes depth, since it reads the scene depth
        FrameProfiler.Begin(ProfileSection.GodraysPass);
        renderManager.BeginGpuPass(ProfileSection.GodraysPass);
        renderManager.RenderGodrays(Camera);
        renderManager.EndGpuPass(ProfileSection.GodraysPass);
        FrameProfiler.End(ProfileSection.GodraysPass);

        // Last world pass: tonemap the linear HDR scene target into the default framebuffer.
        // The UI draws after this, straight onto that gamma framebuffer.
        FrameProfiler.Begin(ProfileSection.PostPass);
        renderManager.BeginGpuPass(ProfileSection.PostPass);
        renderManager.RenderPostProcess(_health.DamageFlash);
        renderManager.EndGpuPass(ProfileSection.PostPass);
        FrameProfiler.End(ProfileSection.PostPass);
    }

    /// <summary>
    /// Draws the HUD. <paramref name="showProfiler"/> adds the frame-timing table under the FPS
    /// counter (the same flag that turns the debug console logging on).
    /// </summary>
    public void RenderUI(RenderManager renderManager, InventoryView view, float time,
        int fps, bool showProfiler, bool showCoordinates = true, PauseMenuView? menu = null,
        SettingsMenuView? settings = null)
    {
        renderManager.RenderUI(_window!, Inventory, view, time, fps, showProfiler, Camera, showCoordinates, menu, settings,
            _health.Health, _health.DamageFlash, _hotbarName, _hotbarPops, LightingTime);
    }
    
    /// <summary>
    /// Drives the left-click action, attack-first. A mob inside the forward punch cone
    /// (see <see cref="Combat"/>) takes the swing � <c>entityDamage</c> plus a shove � and the
    /// block under the crosshair is left alone for as long as one is there; with the cone clear
    /// the block is mined. Mining itself is hit-based: the first hit on a block lands the instant
    /// it is targeted, so a quick tap counts as one hit, then one hit per the held item's pace
    /// while it is held (<see cref="Mining"/>; a bare hand uses the baseline). Each hit removes
    /// the held item's <c>blockDamage</c> from the block's float hardness, and the block breaks
    /// when the damage reaches it. A block whose <c>requiresTool</c> gate refuses the held tool
    /// (wrong kind, or below its required tier) takes 0 damage and shows no progress, though the
    /// swing still plays. A block of hardness 0 breaks on first contact. Damage is discarded when
    /// the target, the held tool or the button changes. A click with no block under the
    /// crosshair still swings the fist. Creative instamines and ignores the gate.
    /// Call after Update so the selection is current. Does nothing unless the world is playing:
    /// nothing may edit an unloaded world.
    /// </summary>
    public void UpdateBlockBreaking(double deltaTime, bool isHolding)
    {
        if (!IsPlaying) return;
        var justPressed = isHolding && !_attackWasPressed;
        _attackWasPressed = isHolding;

        var held = HeldItem();
        var player = LocalContext();

        // A slice weapon owns the left-click outright: every press swings the blade (and buffers
        // the return while one plays), so it never mines.
        if (held is { Attack: AttackPattern.Slice })
        {
            Camera.NotifyPlayerAction();
            UpdateSlice((float)deltaTime, justPressed, player);
            StopBreaking();
            return;
        }

        // Switching away from a slice weapon abandons any in-flight combo; its animation still
        // finishes, it just no longer lands a hit.
        _sliceCombo.Reset();
        _sliceImpactDone = false;

        if (!isHolding)
        {
            StopBreaking();
            _punchTimer = float.PositiveInfinity;
            _creativeBreakTimer = float.PositiveInfinity;
            return;
        }

        // Orbit mode turns the body toward the aim while the action fires at once.
        Camera.NotifyPlayerAction();

        // Attack-first: while a mob is in the punch cone it owns the swing, so no block is
        // mined through it (including while the punch itself is on cooldown).
        if (UpdatePunch(deltaTime, player))
        {
            StopBreaking();
            return;
        }

        if (!_hasSelectedBlock)
        {
            StopBreaking();
            return;
        }

        // A bucket makes water selectable; left-clicking it must still do nothing, since
        // RemoveBlock refuses fluids anyway (and water's hardness of 0 would pop instantly)
        if (BlockType.IsFluid(BlockIdAt(_selectedBlockPosition)))
        {
            StopBreaking();
            return;
        }

        if (Camera.IsCreative)
        {
            StopBreaking();
            // A connected client mines at a pace the server's edit rate accepts; singleplayer
            // keeps taking one block a frame
            if (_clientMode)
            {
                _creativeBreakTimer += (float)deltaTime;
                if (_creativeBreakTimer < WorldGenerationConfig.ClientCreativeBreakSeconds)
                    return;
                _creativeBreakTimer = 0f;
            }
            PunchSwing();
            PlayHitSfx(BlockIdAt(_selectedBlockPosition));
            BreakMined(_selectedBlockPosition, player);
            return;
        }

        var heldId = Inventory.SelectedBlockId;

        // A new target or a different held item restarts the break: the gate, damage and pace
        // all belong to that pairing, so carrying progress over would apply the wrong numbers
        if (!_isBreaking || _selectedBlockPosition != _breakingBlockPosition || heldId != _breakingHeldId)
        {
            _isBreaking = true;
            _breakingBlockPosition = _selectedBlockPosition;
            _breakingBlockId = BlockIdAt(_selectedBlockPosition);
            _breakingHeldId = heldId;
            // Tree wood's hardness follows its thickness, which lives in the state byte
            _breakHardness = Mining.HardnessOf(_breakingBlockId, _chunkManager.GetStateGlobal(
                (int)_selectedBlockPosition.X, (int)_selectedBlockPosition.Y, (int)_selectedBlockPosition.Z));
            _breakAllowed = Mining.CanMine(_breakingBlockId, held);
            _breakPower = Mining.BlockDamage(held);
            _breakHitSeconds = Mining.HitGapSeconds(held);
            _breakDamage = 0f;
            // Seed the timer a whole interval in so the loop below lands the first hit this
            // frame: a tap then counts as one hit instead of being released before the gap
            // it would have to survive.
            _hitTimer = _breakHitSeconds;
            _popElapsed = BlockPopSeconds;
            // A connected client: the server starts its own count of this break here, and judges
            // the completion against it (BreakProgress)
            RaiseIntent(WorldIntent.BreakStart(Camera.Position, _selectedAimDirection, _breakingBlockPosition));
        }

        // Zero hardness covers two cases: a real block of hardness 0 (pebbles) pops on first
        // contact with no hold-to-mine, while air or an unloaded chunk has nothing to mine
        // at all - the id being a registered block is what tells them apart. A refused tool
        // whiffs instead of popping the block.
        if (_breakHardness <= 0f)
        {
            if (BlockType.ById[_breakingBlockId] != null)
            {
                PunchSwing();
                PlayHitSfx(_breakingBlockId);
                if (_breakAllowed)
                    BreakMined(_selectedBlockPosition, player);
            }
            StopBreaking();
            return;
        }

        _hitTimer += (float)deltaTime;
        _popElapsed += (float)deltaTime;

        // A long frame can carry more than one hit; the leftover time rolls into the next
        while (_hitTimer >= _breakHitSeconds)
        {
            _hitTimer -= _breakHitSeconds;
            PunchSwing();
            PlayHitSfx(_breakingBlockId);

            // Start the pop from where the hit actually landed inside this frame, not from
            // the frame boundary, so the animation stays in step with the hit rhythm
            _popElapsed = _hitTimer;

            // A refused tool still swings and sounds, but deals nothing: no damage, no debris
            // and no progress, so the block can never come down that way
            if (!_breakAllowed)
                continue;

            _breakDamage += _breakPower;

            if (_breakDamage < _breakHardness)
            {
                _debris.Burst(_breakingBlockPosition, _breakingBlockId, WorldGenerationConfig.BlockDebrisPerHit,
                    columns: _chunkManager.ColumnSource);
                continue;
            }

            BreakMined(_breakingBlockPosition, player);
            StopBreaking();
            return;
        }
    }

    /// <summary>
    /// Breaks a block the local player's mining finished. On a connected client the break is only
    /// a prediction: the completion goes out as an intent, and the server breaks the block only
    /// when its own count of the hits got there too.
    /// </summary>
    private void BreakMined(Vector3 cell, PlayerContext player)
    {
        RemoveBlock(cell, player);
        RaiseIntent(WorldIntent.BreakComplete(Camera.Position, _selectedAimDirection, cell));
    }

    /// <summary>Hands an intent to a connected client's sender; nothing in singleplayer or on a server.</summary>
    private void RaiseIntent(in WorldIntent intent) => IntentRaised?.Invoke(intent);

    private void StopBreaking()
    {
        _isBreaking = false;
        _breakDamage = 0f;
        _breakHardness = 0f;
        _breakAllowed = false;
        _breakHitSeconds = Mining.DefaultHitGapMs / 1000f;
        _hitTimer = 0f;
        _popElapsed = BlockPopSeconds;
    }

    /// <summary>The item in hand, or null when the selected slot holds nothing.</summary>
    private Item? HeldItem() => HeldItem(Inventory);

    /// <summary>The item in <paramref name="inventory"/>'s selected slot, or null when it holds nothing.</summary>
    private static Item? HeldItem(Inventory inventory) => inventory.HeldItem;

    /// <summary>
    /// The punch half of the left-click action. While a mob sits in the forward cone the
    /// swing belongs to it: once the held item's pace has elapsed the nearest one takes
    /// <c>entityDamage</c> and a knockback shove. Returns true when a mob owns the swing (the
    /// caller must not mine). With the cone clear the timer keeps running, so the first mob to
    /// enter is punched at once; and when there is no block targeted either, the fist still
    /// swings on the same pace, so left-clicking the sky plays the animation. A connected client
    /// never damages a mob itself: it sends the punch, and the server lands it on its own mobs.
    /// </summary>
    private bool UpdatePunch(double deltaTime, PlayerContext player)
    {
        var held = HeldItem();
        _punchGap = Mining.HitGapSeconds(held);
        _punchTimer += (float)deltaTime;

        if (TryFindPunchTarget(player.Eye, player.ViewDirection, out var target))
        {
            if (_punchTimer < _punchGap)
                return true;

            _punchTimer = 0f;
            var weight = PunchSwing();
            if (_clientMode)
            {
                RaiseIntent(WorldIntent.AttackWith(player.Eye, player.ViewDirection, NetAttackKind.Punch));
                // The predicted connect sparks too, like the hit-stop's freeze below
                SpawnHitSparks(target, player.Eye);
            }
            else
                StrikeMob(target, Combat.PunchDamage(held), player.Eye);
            // The punch connected, so the swing holds at full extension for a beat (cosmetic: the
            // punch timer above keeps the pace). A client only predicts the connect - a mob was in
            // the cone with a clear line when the swing landed - since the server's verdict comes
            // back far too late for a freeze that has to start with the swing.
            Player.HitStop(SwingHitStop.SecondsFor(weight));
            return true;
        }

        if (!_hasSelectedBlock && _punchTimer >= _punchGap)
        {
            _punchTimer = 0f;
            PunchSwing();
        }

        return false;
    }

    /// <summary>
    /// Nearest mob inside the forward punch cone from <paramref name="origin"/> along
    /// <paramref name="direction"/> with an unobstructed line of sight, or false when none is in
    /// reach. The punch fires from the head along the aim, so a wall between the player and a mob
    /// stops it.
    /// </summary>
    private bool TryFindPunchTarget(Vector3 origin, Vector3 direction, out Mob target)
    {
        target = null!;
        var mobs = _mobs;
        if (mobs.Count == 0)
            return false;

        var bestDistance = float.PositiveInfinity;
        Mob? best = null;

        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            if (!Combat.InPunchCone(origin, direction, mob.Position, out var distance))
                continue;
            if (BlockRaycaster.SegmentHitsBlock(origin, mob.Position, _chunkManager))
                continue;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = mob;
            }
        }

        if (best == null)
            return false;

        target = best;
        return true;
    }

    /// <summary>
    /// Advances the blade's two-phase combo against the player's clicks. A fresh press starts a
    /// right-to-left swing; a press while one plays buffers the left-to-right return, which
    /// starts (at half power) once the current swing finishes. Damage lands once per swing at
    /// the impact frame (<see cref="PlayerModel.SliceImpactProgress"/>), so targets must still be
    /// in the hemisphere then; a connected client sends the swing to the server instead of
    /// striking anything itself. The swing animation itself lives in <see cref="PlayerModel"/>.
    /// </summary>
    private void UpdateSlice(double deltaTime, bool justPressed, PlayerContext player)
    {
        if (justPressed && _sliceCombo.Press())
        {
            Player.StartSlice(_sliceCombo.Direction);
            _sliceImpactDone = false;
        }

        if (!_sliceCombo.Active)
            return;

        if (!_sliceImpactDone && Player.SliceProgress >= PlayerModel.SliceImpactProgress)
        {
            _sliceImpactDone = true;
            var returnSwing = _sliceCombo.ReturnSwing;
            bool connected;
            if (_clientMode)
            {
                RaiseIntent(WorldIntent.AttackWith(player.Eye, player.ViewDirection,
                    returnSwing ? NetAttackKind.SliceReturn : NetAttackKind.Slice));
                // Predicted, like the punch's: the server lands the swing on its own mobs
                connected = HasSliceTarget(player.Eye, player.ViewDirection, out var predicted);
                if (predicted != null) SpawnHitSparks(predicted, player.Eye);
            }
            else
            {
                connected = ApplySliceDamage(player.Eye, player.ViewDirection, HeldItem(), returnSwing) > 0;
            }
            // A connecting swing holds at the impact; it still ends on time, so the combo's next
            // swing is never delayed
            if (connected)
                Player.HitStop(SwingHitStop.SecondsFor(0f));
        }

        if (Player.IsSlicing)
            return;

        if (_sliceCombo.Advance())
        {
            Player.StartSlice(_sliceCombo.Direction);
            _sliceImpactDone = false;
        }
    }

    /// <summary>
    /// Lands one slice swing from <paramref name="origin"/> along <paramref name="direction"/>
    /// with <paramref name="held"/>: the nearest two mobs inside the forward hemisphere with an
    /// unobstructed line of sight each take the swing's damage and a knockback shove. The return
    /// swing is at half power (<paramref name="returnSwing"/>). Returns how many were struck.
    /// </summary>
    private int ApplySliceDamage(Vector3 origin, Vector3 direction, Item? held, bool returnSwing)
    {
        var mobs = _mobs;
        if (mobs.Count == 0)
            return 0;

        var damage = Combat.SliceDamage(held, returnSwing);

        Mob? first = null;
        Mob? second = null;
        var firstDistance = float.PositiveInfinity;
        var secondDistance = float.PositiveInfinity;

        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            if (!Combat.InFrontHemisphere(origin, direction, mob.Position, out var distance))
                continue;
            if (BlockRaycaster.SegmentHitsBlock(origin, mob.Position, _chunkManager))
                continue;

            // Keep the two closest, shifting the old nearest down when a closer one arrives.
            if (distance < firstDistance)
            {
                second = first;
                secondDistance = firstDistance;
                first = mob;
                firstDistance = distance;
            }
            else if (distance < secondDistance)
            {
                second = mob;
                secondDistance = distance;
            }
        }

        var struck = 0;
        if (first != null)
        {
            StrikeMob(first, damage, origin);
            struck++;
        }
        if (second != null)
        {
            StrikeMob(second, damage, origin);
            struck++;
        }
        return struck;
    }

    /// <summary>Whether a slice from <paramref name="origin"/> along <paramref name="direction"/> would
    /// strike anything: some mob in the forward hemisphere with a clear line of sight, the targets
    /// <see cref="ApplySliceDamage"/> picks from. <paramref name="first"/> is that mob (null when
    /// nothing would be struck), which a client's predicted hit-sparks land on.</summary>
    private bool HasSliceTarget(Vector3 origin, Vector3 direction, out Mob? first)
    {
        first = null;
        for (var i = 0; i < _mobs.Count; i++)
        {
            var mob = _mobs[i];
            if (Combat.InFrontHemisphere(origin, direction, mob.Position, out _)
                && !BlockRaycaster.SegmentHitsBlock(origin, mob.Position, _chunkManager))
            {
                first = mob;
                return true;
            }
        }
        return false;
    }

    /// <summary>Deals <paramref name="damage"/> to <paramref name="mob"/> and shoves it away from
    /// <paramref name="from"/> (the striker's eye, or where a thrown item left it).</summary>
    private void StrikeMob(Mob mob, int damage, Vector3 from)
    {
        var hits = mob.HitsTaken;
        mob.TakeDamage(damage);
        ShowLandedHit(mob, hits);
        SpawnHitSparks(mob, from);
        var away = new Vector3(mob.Position.X - from.X, 0f, mob.Position.Z - from.Z);
        mob.Shove(away, WorldGenerationConfig.SlimePunchShoveSpeed);
    }

    // A connecting hit throws a few pale chips off the mob's body where the striker faces it
    // (cosmetic: a server draws nothing, and a client sparks its own predicted connect instead)
    private void SpawnHitSparks(Mob mob, Vector3 from)
    {
        if (_serverMode) return;
        var toward = from - mob.Position;
        toward.Y = 0f;
        var direction = toward.LengthSquared() > 1e-6f ? Vector3.Normalize(toward) : Vector3.UnitZ;
        var contact = mob.Position + direction * (mob.BodyHalfExtent * 0.6f);
        _debris.Spark(contact, WorldGenerationConfig.HitSparkColor, mob.BodyHalfExtent * 0.5f,
            WorldGenerationConfig.HitSparkCount, WorldGenerationConfig.HitSparkSpeed,
            WorldGenerationConfig.HitSparkSize);
    }

    /// <summary>The floating damage numbers over struck mobs (see <see cref="DamageNumberPool"/>).</summary>
    internal DamageNumberPool DamageNumbers => _damageNumbers;

    // Floats the damage of a hit over the mob when TakeDamage accepted it (its hit count moved). A
    // headless server draws nothing, so it shows none.
    private void ShowLandedHit(Mob mob, int hitsBefore)
    {
        if (_serverMode || mob.HitsTaken == hitsBefore) return;
        ShowDamageNumber(mob, mob.LastHitDamage);
    }

    private void ShowDamageNumber(Mob mob, int amount) =>
        _damageNumbers.Spawn(new Vector3(mob.Position.X,
            EntityHealthbarRenderer.BarCentreY(mob.Position.Y, mob.HeadAboveCentre)
            + WorldGenerationConfig.DamageNumberLiftBlocks, mob.Position.Z), amount);

    /// <summary>
    /// Block id at a world position; 0 (air) for unloaded chunks.
    /// </summary>
    private byte BlockIdAt(Vector3 blockPos)
    {
        // blockPos components are floored floats, so the int casts are exact
        return _chunkManager.GetBlockGlobal((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);
    }

    // One hit, one sound: an unknown or air id falls back to the default group, which is what
    // a block with no authored sfx plays too. A null manager (headless/tests) is silent.
    private void PlayHitSfx(byte blockId) =>
        _sfx?.Play(BlockType.ById[blockId]?.Sfx ?? SfxLibrary.DefaultGroup);

    /// <summary>
    /// Hitboxes for the outline, break overlay and mining pop: the cell's drawn shape resolved
    /// from the live world exactly as the raycast aims at it (see <see cref="BlockShape"/>).
    /// </summary>
    private BlockHitbox[] HitboxesAt(Vector3 blockPos, byte blockId) =>
        BlockShape.BoxesAt(blockId, _chunkManager, (int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);

    /// <summary>
    /// Breaks the block at <paramref name="blockPos"/> for <paramref name="player"/>, with
    /// everything a break sets off. A creative player's break drops nothing (trees still fall).
    /// On a multiplayer client this is the local prediction only: no loot is rolled, no station
    /// hands anything back and nothing fells or topples, since the server runs the real break and
    /// broadcasts what it did.
    /// </summary>
    private void RemoveBlock(Vector3 blockPos, PlayerContext player)
    {
        var chunkPos = VoxelCoords.ChunkPosOf(blockPos);

        if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var chunk)) return;

        // Read the id and state before the block goes away, so the drop knows what it is
        // (a player-built rock pile drops its own count, not the worldgen hash)
        var brokenId = BlockIdAt(blockPos);
        var brokenState = _chunkManager.GetStateGlobal((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);

        // Water is not mineable: only a bucket takes it out (and its hardness of 0 would
        // otherwise pop it on first contact)
        if (BlockType.IsFluid(brokenId)) return;

        _debris.Burst(blockPos, brokenId, WorldGenerationConfig.BlockDebrisOnBreak,
            columns: _chunkManager.ColumnSource);

        // Creative breaks drop nothing, but trees still fall. A client rolls no loot at all: what
        // a break drops is the server's, and reaches the client as drops it can pick up.
        var creative = player.IsCreative;
        var dropsLoot = !creative && !_clientMode;
        // Stations hand back what they held only where they really run
        var stationDrops = _clientMode ? null : _dropManager;

        // A mined wood block fells whatever it was holding up instead of dropping one
        // block (see FellWood): a trunk cut drops the crown, a branch cut only its children.
        // A multiplayer client fells nothing itself: it clears the cut cell like any other block
        // and forwards it, and what the server makes of the cut arrives in its broadcast.
        if (BlockType.IsTreeWood(brokenId) && !_clientMode)
            FellWood(blockPos, brokenId, brokenState, chunk, creative, player.Eye, player);
        // Mining any cell of a multiblock takes the whole structure apart instead of leaving
        // invisible part cells standing (see RevertMultiblock)
        else if (Multiblock.IsMultiblockCell(brokenId))
            RevertMultiblock(blockPos, brokenId, chunk, dropsLoot);
        // Either half of a door takes the other down with it and drops the door once (see
        // RemoveTallBlock)
        else if (TallBlock.TryGetPartner(brokenId, blockPos, out var partnerCell, out var partnerId))
            RemoveTallBlock(blockPos, brokenId, partnerCell, partnerId, chunk, dropsLoot, player);
        // Everything else rolls its loot table, but only once a block was actually cleared
        else if (chunk.RemoveBlockAt(blockPos, _chunkManager))
        {
            if (dropsLoot)
                _dropManager.SpawnLoot(brokenId, blockPos,
                    DropManager.PileCount(brokenId, (int)blockPos.X, (int)blockPos.Z, brokenState),
                    DropManager.PileSecondary(brokenId, (int)blockPos.X, (int)blockPos.Z, brokenState),
                    brokenState);
            if (BlockType.ById[brokenId]?.Renderer == BlockRenderer.BranchPile)
                _campfires.NotifyBlockRemoved((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z, stationDrops);
            if (BlockType.ById[brokenId]?.Renderer == BlockRenderer.Quern)
                _querns.NotifyBlockRemoved((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z, stationDrops);
            // A mined mold throws out a cast that had already set, even in creative - the mold
            // block itself is what creative withholds, not the metal someone poured into it
            if (ContentRegistry.IsCastMold(brokenId))
                _casting.NotifyBlockRemoved(blockPos, stationDrops);
        }

        // Water beside or above the hole pours into it on the next fluid tick
        _fluidManager.ScheduleAround(blockPos);
        // Leaves fed by a mined tip, and wood whose parent this was, re-derive on the next decay tick
        _leafDecay.ScheduleAround(blockPos);

        // Keep the far LOD on the current terrain: a mined top lowers the column's surface, a
        // dig below it keeps the buried shell under the newly exposed hole
        // (blockPos components are floored floats, so the int casts are exact)
        NotifyLodEdit((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);

        // Wood and leaves are never surface-solid and sit above the terrain, so the surface
        // record above never fires for them - yet the far LOD draws their canopy. Disturb it
        // and rebuild regardless. A client's far LOD is the server's (streamed through
        // LodNetCodec), so it disturbs no canopy table of its own and only bursts the leaves.
        if (TreeGenerator.IsTreeBlock(brokenId))
        {
            if (!_clientMode)
            {
                TreeGenerator.PoisonCanopy((int)blockPos.X, (int)blockPos.Z, TreeGenerator.SingleEditPoisonRadius);
                _lodManager.MarkEditedColumn((int)blockPos.X, (int)blockPos.Z);
            }
            _leaves.Burst(blockPos, WorldGenerationConfig.LeafParticleBurstMined);
        }

        // Pebbles, torches and their kind need the block holding them up: pop whatever the break
        // held (the block right above, a wall torch on a side). This routes through the normal
        // break path, so it rolls its loot; its own check then no-ops on air, so the recursion
        // always terminates.
        PopUnsupportedAround(blockPos, player);
        var above = blockPos + Vector3.UnitY;

        // Sand and its kind need the block under them too, but they topple into a falling
        // cube instead of popping: a no-op unless a falls block sits right above the break,
        // and its own check chains the rest of the column. A client simulates no falling
        // blocks: the server topples them and broadcasts the cells.
        if (!_clientMode)
            _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
                (int)above.X, (int)above.Y, (int)above.Z);
        // The mined cell ends as air on every path (plain break, felling, a multiblock revert);
        // a client tags it as predicted, so the server's answer to the break is told from older
        // broadcasts
        if (BlockIdAt(blockPos) == 0)
            BlockEdited?.Invoke((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z, 0, 0);
    }

    /// <summary>
    /// Breaks every support-fragile block the emptied cell <paramref name="holder"/> was holding
    /// up (see <see cref="BlockSupport"/>): the pebble or floor torch right above it and a wall
    /// torch on any side, never a wall torch that merely stood over it. Each goes through
    /// <see cref="RemoveBlock"/>, so it rolls its loot and its own dependants are checked in turn.
    /// Nothing pops while the cell still holds something solid.
    /// </summary>
    private void PopUnsupportedAround(Vector3 holder, PlayerContext player)
    {
        if (BlockType.IsSolid(BlockIdAt(holder))) return;
        for (var face = 0; face < 6; face++)
        {
            var cell = holder + Chunk.CubeFaces[face].normal;
            var id = BlockIdAt(cell);
            if (id == 0) continue;
            var state = _chunkManager.GetStateGlobal((int)cell.X, (int)cell.Y, (int)cell.Z);
            if (BlockSupport.IsHeldUpBy(id, state, cell, holder))
                RemoveBlock(cell, player);
        }
    }

    /// <summary>
    /// Fells the wood a mined wood block was holding up: collects the severed part (the break
    /// plus its children - a branch cut leaves the trunk standing), clears it in one batch
    /// (same pattern as a TNT blast), and launches a toppling entity that drops its blocks
    /// once it has lain still. Player-placed (detached) logs are not trees: they pop out as a
    /// plain drop with no entity and no animation. A lone block with no children just drops
    /// as-is. A creative break (<paramref name="creative"/>) still topples, but drops nothing.
    /// The tree tips away from <paramref name="feller"/>, the eye of whoever cut it, and anything
    /// the felled cells held up (a torch on the trunk) pops for <paramref name="player"/>.
    /// </summary>
    private void FellWood(Vector3 blockPos, byte brokenId, byte brokenState, Chunk chunk, bool creative,
        Vector3 feller, PlayerContext player)
    {
        var x = (int)blockPos.X;
        var y = (int)blockPos.Y;
        var z = (int)blockPos.Z;

        // Player-built logs never topple: no entity, no animation, just a drop.
        // RemoveBlockAt remeshes, so unlike the batch paths below nothing lingers.
        if (WoodState.IsDetached(brokenState) || _chunkManager.IsPlayerPlaced(x, y, z))
        {
            if (chunk.RemoveBlockAt(blockPos, _chunkManager) && !creative)
                _dropManager.SpawnLoot(brokenId, blockPos, state: brokenState);
            _fluidManager.ScheduleAround(blockPos);
            _leafDecay.ScheduleAround(blockPos);
            return;
        }

        var collected = TopplingTree.Collect(_chunkManager, blockPos, brokenId, brokenState);
        if (collected.Count < 2)
        {
            // Nothing was held up by this block: drop it as-is through the normal
            // single-block path, which remeshes synchronously.
            if (chunk.RemoveBlockAt(blockPos, _chunkManager) && !creative)
                _dropManager.SpawnLoot(brokenId, blockPos, state: brokenState);
            _fluidManager.ScheduleAround(blockPos);
            _leafDecay.ScheduleAround(blockPos);
            return;
        }

        var touchedChunks = new HashSet<Vector3>();
        var lowestY = new Dictionary<(int X, int Z), int>();
        var min = blockPos;
        var max = blockPos;
        foreach (var block in collected)
        {
            var chunkPos = VoxelCoords.ChunkPosOf(block.Cell);
            if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var target)) continue;
            if (target.ClearBlockAt(block.Cell) == 0) continue;

            // A player-built extension felled with the tree stops counting as placed
            _chunkManager.UnmarkPlayerPlaced((int)block.Cell.X, (int)block.Cell.Y, (int)block.Cell.Z);
            touchedChunks.Add(chunkPos);
            _fluidManager.ScheduleAround(block.Cell);
            min = Vector3.Min(min, block.Cell);
            max = Vector3.Max(max, block.Cell);

            var key = ((int)block.Cell.X, (int)block.Cell.Z);
            if (!lowestY.TryGetValue(key, out var lowest) || (int)block.Cell.Y < lowest)
                lowestY[key] = (int)block.Cell.Y;
        }

        // One box for the whole fell, never one per cell (a 500-cell fell times a reach box
        // would be hundreds of thousands of adds): the leaves left standing beyond the
        // Chebyshev-2 sweep lose their tips
        _leafDecay.ScheduleBox(min, max);

        foreach (var chunkPos in touchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        // Sand stacked on the felled wood loses its support with it
        foreach (var block in collected)
            _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
                (int)block.Cell.X, (int)block.Cell.Y + 1, (int)block.Cell.Z);

        foreach (var ((lx, lz), ly) in lowestY)
            NotifyLodEdit(lx, ly, lz);

        // The whole canopy came down above the caps: disturb it once at the break (its reach
        // covers the crown) and rebuild every touched column so no ghost lingers at range. A
        // cactus has no canopy, so it only needs the column rebuild. A client's far LOD is the
        // server's, so it touches neither the canopy table nor its regions.
        if (!_clientMode)
        {
            if (TreeGenerator.IsTreeBlock(brokenId))
                TreeGenerator.PoisonCanopy((int)blockPos.X, (int)blockPos.Z, TreeGenerator.FellPoisonRadius);
            foreach (var ((lx, lz), _) in lowestY)
                _lodManager.MarkEditedColumn(lx, lz);
        }

        List<Vector3>? felledLeaves = null;
        foreach (var block in collected)
        {
            if (!TreeGenerator.IsLeaf(block.Id)) continue;
            felledLeaves ??= new List<Vector3>();
            felledLeaves.Add(block.Cell);
        }
        if (felledLeaves != null)
            _leaves.BurstCells(felledLeaves, WorldGenerationConfig.LeafParticleBurstFell);

        // The tree tips away from whoever felled it, pivoting on the mined cell's base
        var away = new Vector3(
            blockPos.X + 0.5f - feller.X, 0f,
            blockPos.Z + 0.5f - feller.Z);
        _toppling.Add(new TopplingTree(collected,
            new Vector3(blockPos.X + 0.5f, blockPos.Y, blockPos.Z + 0.5f), away, drops: !creative));

        // A torch on the trunk, or a flower on a felled log, loses its support with the wood
        // (RemoveBlock's own pop only looks around the cut cell)
        foreach (var block in collected)
            PopUnsupportedAround(block.Cell, player);
    }

    /// <summary>
    /// Converts the 2x2x2 volume of <paramref name="sourceId"/> that the clicked face belongs
    /// to into the <paramref name="anchorId"/> multiblock, opening toward the player
    /// (see <see cref="MultiblockPlacement.TryFindVolume"/>). The clicked cell's chunk remeshes
    /// synchronously so the click frame already shows the structure; the rest of the touched
    /// chunks rebuild asynchronously, exactly as a felled tree's do. Returns whether a volume
    /// was converted.
    /// </summary>
    private bool TryConvertMultiblock(Vector3 hitCell, Vector3 hitNormal, byte sourceId, byte anchorId)
    {
        if (BlockType.ById[anchorId] is not { } anchorType) return false;
        if (!MultiblockPlacement.TryFindVolume(_chunkManager.GetBlockGlobal, sourceId, hitCell,
                hitNormal, ActiveContext.ViewDirection, out var anchor, out var facing))
            return false;

        // A live falling cube owns its cell: never bury one inside the structure
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            if (_falling.OverlapsAny((int)anchor.X + dx, (int)anchor.Y + dy, (int)anchor.Z + dz))
                return false;
        }

        var touchedChunks = new HashSet<Vector3>();
        Multiblock.Convert(_chunkManager, anchor, facing, anchorType, touchedChunks);
        NotifyLodEditMultiblock(anchor);

        _chunkManager.ImmediateRemeshWithBorders(VoxelCoords.ChunkPosOf(hitCell), hitCell);
        foreach (var chunkPos in touchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        OnMultiblockConverted(anchor, facing, anchorType);
        // A client forwards all eight cells (the anchor and its parts, each with the facing and
        // offset in its state byte), so the server holds the same structure
        RaiseBlockEditedMultiblock(anchor);
        return true;
    }

    /// <summary><see cref="RaiseBlockEdited"/> over every cell of the 2x2x2 structure at
    /// <paramref name="anchor"/>.</summary>
    private void RaiseBlockEditedMultiblock(Vector3 anchor)
    {
        if (BlockEdited == null)
            return;
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
            RaiseBlockEdited(anchor + new Vector3(dx, dy, dz));
    }

    /// <summary>
    /// Mining any cell of a multiblock takes the whole structure down: the other seven cells go
    /// back to the source block in one batch and the mined cell rolls the structure's loot when
    /// <paramref name="dropsLoot"/> (not for a creative break, and never on a client, whose break
    /// is a prediction). An orphaned cell (its anchor already blasted away) just pops on its own.
    /// </summary>
    private void RevertMultiblock(Vector3 blockPos, byte brokenId, Chunk chunk, bool dropsLoot)
    {
        if (!Multiblock.TryResolveAnchor(_chunkManager, (int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z,
                out var anchor, out _, out var anchorType) || anchorType == null)
        {
            if (chunk.RemoveBlockAt(blockPos, _chunkManager) && dropsLoot)
                _dropManager.SpawnLoot(brokenId, blockPos);
            return;
        }

        var touchedChunks = new HashSet<Vector3>();
        Multiblock.Revert(_chunkManager, anchor, anchorType, blockPos, touchedChunks);
        NotifyLodEditMultiblock(anchor);

        _chunkManager.ImmediateRemeshWithBorders(VoxelCoords.ChunkPosOf(blockPos), blockPos);
        foreach (var chunkPos in touchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        if (dropsLoot)
            _dropManager.SpawnLoot(brokenId, blockPos);

        OnMultiblockBroken(anchor, anchorType, blockPos);
        // The other seven cells went back to the source block: a client forwards them too, or
        // the server would keep invisible part cells standing (RemoveBlock reports the mined one)
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            var cell = anchor + new Vector3(dx, dy, dz);
            if (cell != blockPos)
                RaiseBlockEdited(cell);
        }
    }

    /// <summary>
    /// Mining either half of a two-cell block (a door, <see cref="TallBlock"/>): the mined cell is
    /// cleared and drops the lower half once when <paramref name="dropsLoot"/>, and the other half,
    /// if it is still there, goes through the hooks a mined cell does (fluid and leaf-decay
    /// schedules, the far LOD, the blocks it held up, a falls block above it, the client's
    /// prediction report) without dropping anything of its own.
    /// </summary>
    private void RemoveTallBlock(Vector3 blockPos, byte brokenId, Vector3 partnerCell, byte partnerId, Chunk chunk,
        bool dropsLoot, PlayerContext player)
    {
        if (!chunk.RemoveBlockAt(blockPos, _chunkManager)) return;
        if (dropsLoot)
            _dropManager.SpawnLoot(TallBlock.LowerOf(brokenId), blockPos);

        int px = (int)partnerCell.X, py = (int)partnerCell.Y, pz = (int)partnerCell.Z;
        if (!_chunkManager.TryGetCell(px, py, pz, out var id, out _) || id != partnerId) return;
        if (_chunkManager.ChunkAt(px, py, pz) is not { } partnerChunk
            || !partnerChunk.RemoveBlockAt(partnerCell, _chunkManager)) return;
        _fluidManager.ScheduleAround(partnerCell);
        _leafDecay.ScheduleAround(partnerCell);
        NotifyLodEdit(px, py, pz);
        PopUnsupportedAround(partnerCell, player);
        if (!_clientMode)
            _falling.TryTopple(_chunkManager, _lodManager, _fluidManager, px, py + 1, pz);
        RaiseBlockEdited(partnerCell);
    }

    /// <summary>Runs right after a structure went up, so the managers that own its behaviour
    /// can start tracking it.</summary>
    private void OnMultiblockConverted(Vector3 anchor, int facing, BlockType anchorType)
    {
        if (anchorType.Kiln)
            _kilns.Ensure(anchor, facing, anchorType.Id);
    }

    /// <summary>Runs right after a structure came down, so the managers that owned it can hand
    /// back whatever it held. <paramref name="blockPos"/> is the mined cell, which is where its
    /// contents drop.</summary>
    private void OnMultiblockBroken(Vector3 anchor, BlockType anchorType, Vector3 blockPos)
    {
        // A client's kiln is a mirror of the server's: it forgets the structure but drops nothing
        if (anchorType.Kiln)
            _kilns.NotifyBlockRemoved(anchor, blockPos, _clientMode ? null : _dropManager);
    }

    /// <summary>Swings falling trees, then pops their wood out as item drops once rested.</summary>
    // Scratch for ThudTree: the landed blocks, split into canopy and trunk
    private readonly List<TopplingTree.FellBlock> _thudLanded = [];
    private readonly List<Vector3> _thudLeaves = [];
    private readonly List<Vector3> _thudWood = [];

    /// <summary>
    /// A felled tree just swung flat: the camera shakes by its distance to the landed trunk (harder
    /// the more wood fell, <see cref="ShakeFalloff"/>), the landed canopy bursts leaves
    /// (<see cref="LeafParticleSystem.BurstCells"/>) and the trunk kicks the ground's dust up
    /// (<see cref="GroundDust.TreeThud"/>). Cosmetic: singleplayer.
    /// </summary>
    private void ThudTree(TopplingTree tree)
    {
        _thudLanded.Clear();
        _thudLeaves.Clear();
        _thudWood.Clear();
        tree.GatherLanded(_thudLanded);
        var centre = Vector3.Zero;
        foreach (var block in _thudLanded)
        {
            if (TreeGenerator.IsLeaf(block.Id))
            {
                _thudLeaves.Add(block.Cell);
                continue;
            }
            _thudWood.Add(block.Cell);
            centre += block.Cell;
        }

        _leaves.BurstCells(_thudLeaves, WorldGenerationConfig.LeafParticleBurstThud);
        GroundDust.TreeThud(_thudWood, _chunkManager, _debris);
        if (_thudWood.Count == 0) return;
        centre = centre / _thudWood.Count + new Vector3(0.5f);
        var strength = WorldGenerationConfig.TreeThudShakeStrength
                       * MathUtils.Clamp01(_thudWood.Count / (float)WorldGenerationConfig.TreeThudFullWoodCells);
        Camera.AddShake(ShakeFalloff.Trauma(strength, WorldGenerationConfig.TreeThudShakeNearBlocks,
            WorldGenerationConfig.TreeThudShakeRangeBlocks, Vector3.Distance(Camera.Position, centre)));
    }

    private void UpdateToppling(float deltaTime)
    {
        for (var i = _toppling.Count - 1; i >= 0; i--)
        {
            var tree = _toppling[i];
            tree.Update(deltaTime);
            // The moment it hits the ground: cosmetic, so a server's trees land in silence
            if (tree.TryTakeLanding() && !_serverMode)
                ThudTree(tree);
            if (!tree.IsDone) continue;

            foreach (var (id, cell) in tree.TakeDrops())
                _dropManager.Spawn(id, cell);

            tree.Dispose();
            _toppling.RemoveAt(i);
        }
    }

    /// <summary>
    /// Throws the selected slot onto the ground in front of the player: one unit, or the whole
    /// stack when <paramref name="wholeStack"/> is set. Works for blocks and pure items alike;
    /// does nothing when the slot is empty. Each unit spawns its own drop, so a thrown stack
    /// scatters (every drop rolls its own random pop) and picks back up one unit at a time.
    /// Does nothing unless the world is playing: nothing may edit an unloaded world.
    /// </summary>
    public void ThrowSelected(bool wholeStack = false) =>
        ThrowSelected(wholeStack, Camera.Position, Camera.ViewDirection, Inventory);

    /// <summary>
    /// <see cref="ThrowSelected(bool)"/> out of <paramref name="inventory"/>, from an explicit
    /// <paramref name="eye"/> along <paramref name="forward"/> (a unit vector): what a server runs
    /// for a client's Q-drop, which must leave from that player and out of that player's own
    /// inventory, not from the server's camera. Returns whether anything was thrown.
    /// </summary>
    public bool ThrowSelected(bool wholeStack, Vector3 eye, Vector3 forward, Inventory inventory)
    {
        if (!IsPlaying) return false;

        // A held tool is one instance: it leaves the pool and every hotbar slot holding it
        if (inventory.SelectedToolInstance is var instance and > 0)
        {
            var id = inventory.SelectedBlockId;
            if (id == 0) return false;
            if (!inventory.RemoveTool(instance)) return false;
            _dropManager.SpawnAt(id, eye + forward * 0.6f, forward * 4f + Vector3.UnitY * 1.5f);
            return true;
        }

        var stackId = inventory.SelectedBlockId;
        if (stackId == 0 || inventory.SelectedCount <= 0) return false;

        var units = wholeStack ? inventory.SelectedCount : 1;
        var thrown = false;
        for (var i = 0; i < units; i++)
        {
            // Spend first so a failed spend never duplicates the drop
            if (!inventory.ConsumeSelected()) break;

            _dropManager.SpawnAt(stackId, eye + forward * 0.6f, forward * 4f + Vector3.UnitY * 1.5f);
            thrown = true;
        }
        return thrown;
    }

    /// <summary>
    /// Right-click: runs the first thing that claims the click � a campfire, quern, kiln or cast
    /// mold interaction, then the selected slot's own <see cref="Item.OnUse"/> (a bucket scoops
    /// or pours, flint grounds a pile, a firestarter strikes), otherwise places the selected
    /// block. A successful tool use morphs the instance in place, so every hotbar slot holding
    /// that bucket flips with it and scooping and pouring alternate on one button. Returns
    /// whether the click was claimed, so a plain throwable only starts charging when nothing
    /// here owned it � a mudball aimed at a lit campfire cooks instead of being thrown.
    /// False unless the world is playing: nothing may edit an unloaded world.
    /// </summary>
    public bool TryUseSelected(float maxDistance)
    {
        var claimed = TryUseSelected(maxDistance, LocalContext(), out var outcome);
        // A connected client predicted the click; the server replays it for this player. A
        // placement names the face it went against (the server checks the player can see it), any
        // other claim only the cell under the crosshair, which scopes the correction sent back.
        if (claimed && _clientMode)
        {
            if (outcome == RightClickOutcome.Place)
                RaiseIntent(WorldIntent.Place(Camera.Position, Camera.LastAimDirection, _lastPlaceHit, _lastPlaceNormal));
            else
                RaiseIntent(WorldIntent.Use(Camera.Position, Camera.LastAimDirection,
                    _hasSelectedBlock ? _selectedBlockPosition : null));
        }
        return claimed;
    }

    /// <summary>
    /// <see cref="TryUseSelected(float)"/> for <paramref name="player"/>: every station
    /// interaction, the item's own use (its tool morph, durability and cooldown) and the placement
    /// cost run against that player's inventory, aim and creative flag. Single player passes its
    /// own camera's context; a server passes the acting player's. <paramref name="outcome"/> says
    /// what claimed the click; <paramref name="allowPlace"/> false stops the chain before the
    /// placement fallback (a server replaying a click the client said was not a placement).
    /// </summary>
    public bool TryUseSelected(float maxDistance, PlayerContext player, out RightClickOutcome outcome,
        bool allowPlace = true)
    {
        outcome = RightClickOutcome.None;
        if (!IsPlaying) return false;
        var local = player.Camera != null;
        if (local && _throwCharging) return false;
        if (local)
            Camera.NotifyPlayerAction();
        if (TryCookOrCollect(maxDistance, player) || TryQuernInteract(maxDistance, player)
            || TryKilnInteract(maxDistance, player) || TryCastInteract(maxDistance, player)
            || TryToggleOpenable(maxDistance, player))
        {
            PunchSwing();
            outcome = RightClickOutcome.Station;
            return true;
        }
        var item = HeldItem(player.Inventory);
        if (item == null || item is BlockItem || item.UseAction == ItemUseAction.None)
        {
            if (!allowPlace || !TryPlaceSelectedBlock(maxDistance, player)) return false;
            outcome = RightClickOutcome.Place;
            return true;
        }
        if (!DispatchUse(item, maxDistance, player)) return false;
        outcome = RightClickOutcome.ItemUse;
        return true;
    }

    /// <summary>Starts charging a throw of the selected throwable. False unless the world is
    /// playing: nothing may edit an unloaded world.</summary>
    public bool TryBeginThrowCharge()
    {
        if (!IsPlaying) return false;
        if (_throwCharging) return true;
        var id = Inventory.SelectedBlockId;
        if (id == 0 || Inventory.SelectedCount <= 0) return false;
        if (ItemRegistry.TryGet(id) is not { IsThrowable: true } item) return false;
        _throwCharging = true;
        _throwChargeSeconds = 0f;
        _throwItemId = id;
        _throwBaseDamage = item.ThrowDamage ?? 0f;
        Player.StartThrowCharge();
        return true;
    }

    /// <summary>Whether a timed use is charging (see <see cref="TryBeginUseCharge"/>).</summary>
    public bool IsUseCharging => _useCharging;

    /// <summary>
    /// Starts the timed use of the selected item when it declares <see cref="Item.UseSeconds"/>:
    /// holding the button plays a use pose and the use runs only at completion, so letting go
    /// early (<see cref="CancelUseCharge"/>) spends nothing. False for anything else, which uses
    /// on the click or charges a throw. False unless the world is playing.
    /// </summary>
    public bool TryBeginUseCharge()
    {
        if (!IsPlaying) return false;
        if (_useCharging) return true;
        var id = Inventory.SelectedBlockId;
        if (id == 0 || Inventory.SelectedCount <= 0) return false;
        if (ItemRegistry.TryGet(id) is not { } item || item.UseSeconds <= 0f) return false;
        _useCharging = true;
        _useChargeSeconds = 0f;
        _useItemId = id;
        Player.StartUseCharge();
        return true;
    }

    /// <summary>
    /// Advances a held timed use; at the selected item's <see cref="Item.UseSeconds"/> the use
    /// runs exactly as a click would have run it. A changed selection cancels the charge.
    /// </summary>
    public void UpdateUseCharge(float deltaTime)
    {
        if (!IsPlaying) return;
        if (!_useCharging) return;
        var seconds = ItemRegistry.TryGet(_useItemId)?.UseSeconds ?? 0f;
        if (Inventory.SelectedBlockId != _useItemId || Inventory.SelectedCount <= 0 || seconds <= 0f)
        {
            CancelUseCharge();
            return;
        }

        _useChargeSeconds += Math.Max(0f, deltaTime);
        if (_useChargeSeconds < seconds)
        {
            Player.UpdateUseCharge(MathUtils.Clamp01(_useChargeSeconds / seconds));
            return;
        }

        // Completed: the use runs now, on the same path a click would have taken
        _useCharging = false;
        Player.CancelUseCharge();
        TryUseSelected(InteractionReach);
    }

    /// <summary>Cancels a held timed use; the item spends nothing.</summary>
    public void CancelUseCharge()
    {
        if (!IsPlaying) return;
        _useCharging = false;
        Player.CancelUseCharge();
    }

    /// <summary>Advances a held throw charge. Does nothing unless the world is playing: nothing
    /// may edit an unloaded world.</summary>
    public void UpdateThrowCharge(float deltaTime)
    {
        if (!IsPlaying) return;
        if (!_throwCharging) return;
        _throwChargeSeconds = Math.Min(_throwChargeSeconds + Math.Max(0f, deltaTime), WorldGenerationConfig.ThrowChargeSeconds);
        if (Inventory.SelectedBlockId != _throwItemId || Inventory.SelectedCount <= 0)
        {
            _throwCharging = false;
            Player.CancelThrowCharge();
            return;
        }
        Player.UpdateThrowCharge(ThrowCharge01);
    }

    /// <summary>Cancels a held throw charge. Does nothing unless the world is playing: nothing
    /// may touch an unloaded world's player state.</summary>
    public void CancelThrowCharge()
    {
        if (!IsPlaying) return;
        _throwCharging = false;
        Player.CancelThrowCharge();
    }

    /// <summary>Releases a charged throw. False unless the world is playing: nothing may edit an
    /// unloaded world. On a connected client the projectile is a cosmetic copy: the throw goes
    /// out as an intent and the server launches the one that deals damage and lands as a drop.</summary>
    public bool ReleaseThrow()
    {
        if (!IsPlaying) return false;
        if (!_throwCharging) return false;
        _throwCharging = false;
        var charge01 = MathUtils.Clamp01(_throwChargeSeconds / WorldGenerationConfig.ThrowChargeSeconds);
        if (!TryLaunchThrow(Inventory, _throwItemId, _throwBaseDamage, charge01, Camera.Position,
                Camera.ViewDirection, Camera.IsCreative))
        {
            Player.CancelThrowCharge();
            return false;
        }
        RaiseIntent(WorldIntent.Throw(Camera.Position, Camera.ViewDirection, charge01));
        Camera.NotifyPlayerAction();
        Player.ReleaseThrow(charge01);
        return true;
    }

    /// <summary>
    /// Launches one <paramref name="id"/> out of <paramref name="inventory"/>'s selected slot from
    /// <paramref name="eye"/> along <paramref name="forward"/> at <paramref name="charge01"/>,
    /// spending the unit unless <paramref name="creative"/>. False, spending nothing, when the slot
    /// no longer holds that item. The release half of a throw, apart from the local player's
    /// charge and arm pose.
    /// </summary>
    private bool TryLaunchThrow(Inventory inventory, byte id, float baseDamage, float charge01,
        Vector3 eye, Vector3 forward, bool creative)
    {
        if (id == 0 || inventory.SelectedBlockId != id || inventory.SelectedCount <= 0)
            return false;
        if (!creative && !inventory.ConsumeSelected())
            return false;
        var origin = eye + forward * 0.6f;
        var velocity = ThrownProjectile.LaunchVelocity(forward, charge01);
        _thrown.Add(new ThrownProjectile(id, origin, velocity, baseDamage, charge01));
        return true;
    }

    /// <summary>
    /// Flies every thrown item: one that hits a mob deals its damage and shoves it away from where
    /// it was thrown from, one that comes to rest becomes a drop. A client's throws are cosmetic
    /// copies of the server's, so they only fly and vanish.
    /// </summary>
    private void UpdateThrown(float deltaTime)
    {
        if (_thrown.Count == 0) return;
        for (var i = _thrown.Count - 1; i >= 0; i--)
        {
            var projectile = _thrown[i];
            var alive = projectile.Update(deltaTime, _chunkManager, _mobs, out var hit);
            if (hit != null)
            {
                if (!_clientMode)
                    StrikeMob(hit, projectile.Damage, projectile.Origin);
                else
                    // The cosmetic copy's connect sparks where it stops on the mirrored mob
                    SpawnHitSparks(hit, projectile.Origin);
                _thrown.RemoveAt(i);
                continue;
            }
            if (!alive)
            {
                if (!_clientMode)
                    _dropManager.SpawnAt(projectile.ItemId, projectile.Position);
                _thrown.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Runs one item's <see cref="Item.OnUse"/> for <paramref name="player"/> and applies its
    /// outcome: morph the tool instance in place (only when the use took effect) and punch.
    /// Durability, cooldowns and stack spending already happened inside the override, against the
    /// player's own inventory. Returns whether the use took effect.
    /// </summary>
    private bool DispatchUse(Item item, float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        var instance = inventory.SelectedToolInstance;
        _activeToolInstance = instance;
        _activeContext = player;
        try
        {
            var stack = instance > 0 ? inventory.GetToolStack(instance) : null;
            var result = item.OnUse(new ItemUseContext(this, maxDistance, instance), stack);
            if (result.Applied && result.MorphToId.HasValue && instance > 0)
                inventory.MorphTool(instance, result.MorphToId.Value);
            if (result.Punch)
                PunchSwing();
            return result.Applied;
        }
        finally
        {
            _activeToolInstance = 0;
            _activeContext = null;
        }
    }

    #region IItemUseWorld
    // One implementation for every Item override: queries read the live managers, effects write
    // them, and the inventory tail runs through the dispatcher's active tool instance.

    double IItemUseWorld.NowSeconds => Now;

    bool IItemUseWorld.IsCreative => ActiveContext.IsCreative;

    float IItemUseWorld.NextUseRoll() => UseRoll();

    bool IItemUseWorld.RaycastFluidSource(float maxDistance, out Vector3 hit) =>
        ActiveContext.Raycast(maxDistance, _chunkManager, out hit, out _, includeFluids: true);

    bool IItemUseWorld.RaycastSolid(float maxDistance, out Vector3 hit, out Vector3 normal) =>
        ActiveContext.Raycast(maxDistance, _chunkManager, out hit, out normal);

    bool IItemUseWorld.TryGetCell(int x, int y, int z, out byte id, out byte state) =>
        _chunkManager.TryGetCell(x, y, z, out id, out state);

    byte IItemUseWorld.GetBlockAt(Vector3 pos) => BlockIdAt(pos);

    bool IItemUseWorld.ScoopFluidCell(Vector3 pos)
    {
        var changed = _fluidManager.SetCellImmediate(pos, 0, 0, _chunkManager);
        // Scheduled for uniformity with every other edit path; a flow itself never overwrites a
        // leaf, since leaves are not replaceable
        if (changed)
        {
            _leafDecay.ScheduleAround(pos);
            RaiseBlockEdited(pos);
        }
        return changed;
    }

    bool IItemUseWorld.PourFluidCell(Vector3 pos, byte fluidId)
    {
        var changed = _fluidManager.SetCellImmediate(pos, fluidId, FluidState.Source, _chunkManager);
        // Scheduled for uniformity with every other edit path; a flow itself never overwrites a
        // leaf, since leaves are not replaceable
        if (changed)
        {
            _leafDecay.ScheduleAround(pos);
            RaiseBlockEdited(pos);
        }
        return changed;
    }

    /// <summary>
    /// Reports a player-driven edit of <paramref name="cell"/> through <see cref="BlockEdited"/>
    /// with the id and state the cell holds now, so a multiplayer client tags it as part of the
    /// prediction its next intent carries, whatever path made it. Nothing is subscribed in
    /// singleplayer or on a server.
    /// </summary>
    private void RaiseBlockEdited(Vector3 cell)
    {
        if (BlockEdited is not { } edited)
            return;
        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
        edited(x, y, z, _chunkManager.GetBlockGlobal(x, y, z), _chunkManager.GetStateGlobal(x, y, z));
    }

    bool IItemUseWorld.PlaceBlockAtFace(Vector3 hit, Vector3 normal, byte blockId, byte state,
        out Vector3 placedCell) =>
        PlaceBlockAtFace(hit, normal, blockId, state, ActiveContext.ViewDirection, out placedCell);

    void IItemUseWorld.AfterPlaced(Vector3 pos)
    {
        // As in TryPlaceBlockFromRaycast: a client leaves the fall to the server
        if (_clientMode) return;
        _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
            (int)pos.X, (int)pos.Y, (int)pos.Z);
    }

    int IItemUseWorld.PileCountFor(byte blockId, int x, int y, int z, byte state)
    {
        var pile = BlockType.ById[blockId];
        if (pile == null)
            return 0;
        return pile.Renderer switch
        {
            BlockRenderer.BranchPile => BranchPileGenerator.CountWithState(state, pile.StackMax),
            BlockRenderer.SnowLayers => SnowShape.CountWithState(x, z, state, pile.StackMax),
            _ => PebbleGenerator.CountWithState(x, z, state, pile.StackMax),
        };
    }

    bool IItemUseWorld.SetPileCount(Vector3 pos, byte count)
    {
        var chunk = _chunkManager.ChunkAt((int)pos.X, (int)pos.Y, (int)pos.Z);
        var changed = chunk != null && chunk.SetStateAt(pos, count, _chunkManager);
        if (changed) RaiseBlockEdited(pos);
        return changed;
    }

    int IItemUseWorld.PileSecondaryFor(byte blockId, int x, int y, int z, byte state)
    {
        var pile = BlockType.ById[blockId];
        if (pile?.Renderer != BlockRenderer.Pebbles)
            return 0;
        return PebbleGenerator.ObsidianCountWithState(x, z, state, pile.StackMax);
    }

    bool IItemUseWorld.SetPileComposition(Vector3 pos, byte blockId, int total, int secondary)
    {
        var chunk = _chunkManager.ChunkAt((int)pos.X, (int)pos.Y, (int)pos.Z);
        if (chunk == null)
            return false;
        byte state = BlockType.ById[blockId]?.Renderer == BlockRenderer.Pebbles
            ? PebbleGenerator.StateFor(total, secondary)
            : (byte)total;
        var changed = chunk.SetStateAt(pos, state, _chunkManager);
        if (changed) RaiseBlockEdited(pos);
        return changed;
    }

    void IItemUseWorld.UnmarkPlayerPlaced(int x, int y, int z) =>
        _chunkManager.UnmarkPlayerPlaced(x, y, z);

    void IItemUseWorld.SpawnLoot(byte blockId, Vector3 pos) =>
        _dropManager.SpawnLoot(blockId, pos);

    bool IItemUseWorld.IsCampfireLit(int x, int y, int z) => _campfires.IsLit(x, y, z);

    bool IItemUseWorld.IgniteCampfire(int x, int y, int z) => _campfires.Ignite(x, y, z);

    bool IItemUseWorld.CanIgniteKiln(int x, int y, int z) =>
        TryTrackKilnAt(x, y, z, out var anchor) && _kilns.CanIgnite(anchor);

    bool IItemUseWorld.IgniteKiln(int x, int y, int z) =>
        TryTrackKilnAt(x, y, z, out var anchor) && _kilns.Ignite(anchor);

    bool IItemUseWorld.TryConvertMultiblock(Vector3 hitCell, Vector3 hitNormal, byte sourceId, byte anchorId) =>
        TryConvertMultiblock(hitCell, hitNormal, sourceId, anchorId);

    bool IItemUseWorld.IsPrimedTntBlock(byte id) =>
        BlockType.ById[id]?.OnBreak == BlockBreakAction.PrimeTnt;

    bool IItemUseWorld.IsCellSubmerged(int x, int y, int z) =>
        TntManager.IsSubmerged(_chunkManager, x, y, z);

    bool IItemUseWorld.RemoveCellForPrime(Vector3 cell)
    {
        var removed = _chunkManager.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z)?.RemoveBlockAt(cell, _chunkManager) == true;
        // Scheduled for uniformity with every other edit path
        if (removed)
        {
            _leafDecay.ScheduleAround(cell);
            RaiseBlockEdited(cell);
        }
        return removed;
    }

    void IItemUseWorld.SpawnPrimedTnt(Vector3 cell, byte blockId)
    {
        // A client simulates no TNT: the removed cell was forwarded, and anything the server
        // makes of it arrives in its broadcast. A local fuse would blow a crater only this
        // client sees.
        if (_clientMode) return;
        _tntManager.Spawn(cell, blockId, TntManager.PlayerFuseSeconds);
    }

    void IItemUseWorld.ScheduleFluidAround(Vector3 cell) => _fluidManager.ScheduleAround(cell);

    void IItemUseWorld.MarkLodColumnEdited(int x, int y, int z) => NotifyLodEdit(x, y, z);

    /// <summary>
    /// Keeps the far LOD on the current terrain after the cell (x, y, z) was edited: records the
    /// column's new surface (or its sub-surface removal cap) through
    /// <see cref="LodSurface.RecordEdit"/> and rebuilds the regions that sample the column on
    /// every level when it moved. Main thread only, called after the edit. A no-op on a client.
    /// </summary>
    private void NotifyLodEdit(int x, int y, int z)
    {
        // A client's far LOD is the server's, streamed as finished regions through LodNetCodec:
        // LodSurface, LodRegion.ColumnCaps and TreeGenerator.CanopyDisturbed are worldgen-side
        // tables a client must never touch.
        if (_clientMode) return;
        if (LodSurface.RecordEdit(_chunkManager, x, y, z))
            _lodManager.MarkEditedColumn(x, z);
    }

    /// <summary>Runs <see cref="NotifyLodEdit"/> over every cell of the 2x2x2 structure at
    /// <paramref name="anchor"/>, after a convert or a revert rewrote them.</summary>
    private void NotifyLodEditMultiblock(Vector3 anchor)
    {
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
            NotifyLodEdit((int)anchor.X + dx, (int)anchor.Y + dy, (int)anchor.Z + dz);
    }

    bool IItemUseWorld.ToolReady() =>
        _activeToolInstance > 0 && ActiveInventory.ToolReadyAt(_activeToolInstance, Now);

    void IItemUseWorld.MarkToolUsed(float delaySeconds)
    {
        if (_activeToolInstance > 0)
            ActiveInventory.MarkToolUsed(_activeToolInstance, Now, delaySeconds);
    }

    bool IItemUseWorld.DamageTool(int amount)
    {
        if (_activeToolInstance <= 0) return false;
        var inventory = ActiveInventory;
        var itemId = inventory.ToolItemId(_activeToolInstance);
        var slot = inventory.SlotOfTool(_activeToolInstance);
        if (!inventory.DamageTool(_activeToolInstance, amount)) return false;
        ToolBroke(itemId, slot);
        return true;
    }

    bool IItemUseWorld.Heal(int amount)
    {
        // The pool belongs to the player the use runs for: this world's own in singleplayer and
        // on a client (predicted, then the server's status confirms), the sending ServerPlayer's
        // on a server, found through the dispatching context
        if (!_serverMode)
            return _health.Heal(amount);
        var context = _activeContext;
        if (context == null) return false;
        var players = ServerPlayers;
        for (var i = 0; i < players.Count; i++)
        {
            if (!ReferenceEquals(players[i].Context, context)) continue;
            if (!players[i].Status.Heal(amount)) return false;
            players[i].StatusDirty = true;
            return true;
        }
        return false;
    }

    bool IItemUseWorld.GrowTree(Vector3 cell)
    {
        // The aimed cell names the species (its wood or leaves); the growth writer then finds
        // the tree's root in that column and grows it, or plants a sapling when there is none
        if (TreeSpeciesRegistry.ForBlock(BlockIdAt(cell)) is not { } species) return false;
        return TryGrowTree(species.Name, (int)cell.X, (int)cell.Y, (int)cell.Z,
            WorldGenerationConfig.AshGrowthPulses).Succeeded;
    }

    bool IItemUseWorld.PlantTree(string species, Vector3 hit, Vector3 normal)
    {
        if (TreeSpeciesRegistry.TryGet(species) is null) return false;
        // The ground cell: the aimed cell itself when it was hit from above, else the cell
        // across the face (the writer plants on its ground when the column holds no tree)
        var ground = normal.Y > 0.5f ? hit : hit + normal;
        var result = TryGrowTree(species, (int)ground.X, (int)ground.Y, (int)ground.Z, steps: 0);
        return result.Status == TreeGrowthStatus.Planted;
    }

    void IItemUseWorld.ConsumeSelected() => ActiveInventory.ConsumeSelected();
    #endregion

    // A tool that ran out of durability shatters: chips of its sprite's colour burst off the hand
    // and its hotbar slot flashes (cosmetic: a server draws nothing)
    private void ToolBroke(byte itemId, int slot)
    {
        if (_serverMode) return;
        if (slot >= 0) _hotbarPops.Flash(slot);
        if (itemId == 0 || ItemRegistry.TryGet(itemId) is not { } item) return;
        var hand = Player.GetHandAnchorMatrix(Camera.Position, Camera.PlayerYaw,
            PlayerModel.FirstPersonBodyBack * (1f - Camera.ShoulderBlend), Camera.Pitch).Translation;
        _debris.Spark(hand, ToolShatter.ColourOf(item, WorldGenerationConfig.ToolShatterFallbackColor),
            0.12f, WorldGenerationConfig.ToolShatterCount, WorldGenerationConfig.ToolShatterSpeed,
            WorldGenerationConfig.ToolShatterSize);
    }

    private bool TryCookOrCollect(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (BlockType.ById[hitId]?.Renderer != BlockRenderer.BranchPile) return false;
        if (!_campfires.IsLit(hx, hy, hz)) return false;

        var selected = inventory.SelectedBlockId;
        if (selected != 0 && inventory.SelectedCount > 0
            && ContentRegistry.CookRecipeForInput(selected) != null
            && _campfires.TryPlaceFood(hx, hy, hz, selected))
        {
            if (!player.IsCreative)
                inventory.ConsumeSelected();
            return true;
        }

        var finished = _campfires.CollectFinished(hx, hy, hz);
        if (finished.Count == 0) return selected != 0 && ContentRegistry.CookRecipeForInput(selected) != null;
        foreach (var output in finished)
        {
            if (!inventory.Add(output))
                _dropManager.Spawn(output, new Vector3(hx, hy, hz));
        }
        return true;
    }

    /// <summary>
    /// Right-click on a quern: holding a crushable inserts one unit into an empty quern,
    /// anything else spins the plate through one full rotation (a quern already turning
    /// swallows the click). Returns whether a quern was aimed at.
    /// </summary>
    private bool TryQuernInteract(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (BlockType.ById[hitId]?.Renderer != BlockRenderer.Quern) return false;

        if (_querns.IsSpinning(hx, hy, hz)) return true;

        var selected = inventory.SelectedBlockId;
        if (selected != 0 && inventory.SelectedCount > 0
            && ContentRegistry.CrushRecipeForInput(selected) != null
            && _querns.TryInsert(hx, hy, hz, selected))
        {
            if (!player.IsCreative)
                inventory.ConsumeSelected();
            return true;
        }

        _querns.StartSpin(hx, hy, hz);
        return true;
    }

    /// <summary>
    /// Resolves the kiln structure a cell belongs to and makes sure it is tracked, so any of its
    /// eight cells answers for the whole. False when the cell is not part of a kiln.
    /// </summary>
    private bool TryTrackKilnAt(int x, int y, int z, out Vector3 anchor)
    {
        anchor = Vector3.Zero;
        if (!Multiblock.TryResolveAnchor(_chunkManager, x, y, z, out var resolved, out var facing, out var anchorType)
            || anchorType is not { Kiln: true })
            return false;

        _kilns.Ensure(resolved, facing, anchorType.Id);
        anchor = resolved;
        return true;
    }

    /// <summary>
    /// Right-click on a kiln: an empty hand lifts the crucible out of the hearth (contents and
    /// all), a crucible in hand stands one in, anything with a melt recipe is charged into the
    /// crucible already standing there, fuel goes into the hearth (up to
    /// <see cref="KilnManager.MaxFuel"/> units of one kind), a firestarter is passed through to
    /// its own <see cref="Item.OnUse"/> so it can strike, and a block in hand falls through to
    /// placement against the structure. Any other click on a kiln is spent. Returns whether the
    /// click belonged to the kiln.
    /// </summary>
    private bool TryKilnInteract(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (!Multiblock.IsMultiblockCell(hitId)) return false;
        if (!TryTrackKilnAt(hx, hy, hz, out var anchor)) return false;

        var held = HeldItem(inventory);

        // The firestarter lights the kiln from its own OnUse, which also spends its durability
        if (held is { UseAction: ItemUseAction.IgniteCampfire }) return false;

        // An empty hand lifts the crucible out; a full tools pool puts it straight back
        if (held == null)
        {
            if (_kilns.TryTakeCrucible(anchor, out var takenId, out var takenContents)
                && CrucibleInventory.Add(inventory, takenId, takenContents) == null)
                _kilns.TryPlaceCrucible(anchor, takenId, takenContents);
            return true;
        }

        // A crucible in hand stands in the hearth, carrying whatever that instance held
        if (held is CrucibleItem crucible)
        {
            var instance = inventory.SelectedToolInstance;
            var stack = instance > 0 ? inventory.GetToolStack(instance) : null;
            if (stack != null && _kilns.TryPlaceCrucible(anchor, crucible.Id, stack.Get<CrucibleComponent>()))
                inventory.RemoveTool(instance);
            return true;
        }

        // Ore (or anything else that melts) charges the crucible standing inside
        if (ContentRegistry.MeltRecipeForInput(held.Id) != null && _kilns.TryInsertMelt(anchor, held.Id))
        {
            if (!player.IsCreative)
                inventory.ConsumeSelected();
            return true;
        }

        if (held is { FuelSeconds: > 0f } && _kilns.TryAddFuel(anchor, held))
        {
            if (!player.IsCreative)
                inventory.ConsumeSelected();
            return true;
        }

        return held is not BlockItem;
    }

    /// <summary>
    /// Right-click on a cast mold: a cooled cast is collected into the inventory (or thrown out
    /// when there is no room), otherwise a crucible in hand starts or resumes pouring its molten
    /// metal into the pocket. A crucible click is spent either way, so a refused pour never falls
    /// through to placing anything; a block in hand does fall through, so molds can still be
    /// stacked against each other. Returns whether the click belonged to the mold.
    /// </summary>
    private bool TryCastInteract(float maxDistance, PlayerContext player)
    {
        var inventory = player.Inventory;
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (!ContentRegistry.IsCastMold(hitId)) return false;

        var cell = new Vector3(hx, hy, hz);
        var finished = _casting.Collect(cell);
        if (finished.Count > 0)
        {
            foreach (var id in finished)
                if (!inventory.Add(id))
                    _dropManager.Spawn(id, cell);
            return true;
        }

        if (HeldItem(inventory) is CrucibleItem)
        {
            var instance = inventory.SelectedToolInstance;
            var stack = instance > 0 ? inventory.GetToolStack(instance) : null;
            if (stack != null)
                // A connected player's pour drains that player's crucible and follows its eye
                _casting.TryStartPour(cell, hitId, stack, player.Camera == null ? player : null);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Right-click on an openable block (a trapdoor, either half of a door;
    /// <see cref="BlockType.Openable"/>): flips its open bit (<see cref="DoorState"/>), and a
    /// door's other half with it, whatever is in hand, so a held block never places against it. The flip is a state write through <see cref="Chunk.SetStateAt"/> (saved,
    /// journaled to clients and remeshed at once) with the edit-path hooks, and a client reports
    /// it as part of its prediction. Returns whether the click belonged to an openable block.
    /// </summary>
    private bool TryToggleOpenable(float maxDistance, PlayerContext player)
    {
        if (!player.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        if (!_chunkManager.TryGetCell((int)hit.X, (int)hit.Y, (int)hit.Z, out var id, out var state)) return false;
        if (BlockType.ById[id] is not { Openable: true }) return false;
        var toggled = DoorState.Toggle(state);
        ToggleOpenCell(hit, toggled);
        // Both halves of a door share one state: the other half swings with the clicked one
        if (TallBlock.TryGetPartner(id, hit, out var partner, out var partnerId)
            && _chunkManager.TryGetCell((int)partner.X, (int)partner.Y, (int)partner.Z, out var held, out _)
            && held == partnerId)
            ToggleOpenCell(partner, toggled);
        return true;
    }

    /// <summary>Writes an openable cell's new <paramref name="state"/> and runs the hooks every
    /// edit path runs: the fluid and leaf-decay schedules and the client's prediction report.</summary>
    private void ToggleOpenCell(Vector3 cell, byte state)
    {
        var chunk = _chunkManager.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z);
        if (chunk == null || !chunk.SetStateAt(cell, state, _chunkManager)) return;
        _fluidManager.ScheduleAround(cell);
        _leafDecay.ScheduleAround(cell);
        RaiseBlockEdited(cell);
    }

    /// <summary>
    /// Places the hotbar's selected block against the face the player is looking at and spends
    /// one from that slot � except in creative mode, where placing is free. Does nothing
    /// when the slot is empty or holds no blocks. Returns whether one was placed; false unless the
    /// world is playing, since nothing may edit an unloaded world.
    /// </summary>
    public bool TryPlaceSelectedBlock(float maxDistance) => TryPlaceSelectedBlock(maxDistance, LocalContext());

    /// <summary><see cref="TryPlaceSelectedBlock(float)"/> for <paramref name="player"/>: aimed by
    /// its raycast, placing from and spending out of its inventory unless it is creative.</summary>
    public bool TryPlaceSelectedBlock(float maxDistance, PlayerContext player)
    {
        if (!IsPlaying) return false;
        var inventory = player.Inventory;
        if (!inventory.CanPlaceSelected) return false;

        // Only spend a block if one actually went down (and never in creative)
        if (!TryPlaceBlockFromRaycast(maxDistance, inventory.SelectedBlockId, player)) return false;
        if (!player.IsCreative)
            inventory.ConsumeSelected();
        return true;
    }

    /// <summary>Picks the targeted block into the hotbar. False unless the world is playing:
    /// nothing may edit an unloaded world.</summary>
    public bool TryPickTargetedBlock() => TryPickTargetedBlock(Inventory, out _);

    /// <summary>
    /// <see cref="TryPickTargetedBlock()"/> into <paramref name="inventory"/>, reporting the picked
    /// block's id (0 when nothing is targeted), which a multiplayer client forwards as its
    /// <see cref="NetInventoryAction.PickBlock"/> intent.
    /// </summary>
    public bool TryPickTargetedBlock(Inventory inventory, out byte pickedId)
    {
        pickedId = 0;
        if (!IsPlaying) return false;
        if (!_hasSelectedBlock) return false;
        // A wall torch is picked as the torch it was placed from (BlockType.PickId)
        var id = BlockType.PickId(BlockIdAt(_selectedBlockPosition));
        if (id == 0) return false;
        pickedId = id;
        return inventory.PickBlock(id);
    }

    /// <summary>Places a block against the face the local player targets. Returns whether one was
    /// placed; false unless the world is playing, since nothing may edit an unloaded world.</summary>
    public bool TryPlaceBlockFromRaycast(float maxDistance, byte blockId) =>
        TryPlaceBlockFromRaycast(maxDistance, blockId, LocalContext());

    /// <summary>Places a block against the face <paramref name="player"/> targets, recording the
    /// hit (the face a client's placement intent names).</summary>
    private bool TryPlaceBlockFromRaycast(float maxDistance, byte blockId, PlayerContext player)
    {
        if (!IsPlaying) return false;
        if (blockId == 0) return false;

        if (!player.Raycast(maxDistance, _chunkManager, out var hitPos, out var hitNormal))
            return false;

        _lastPlaceHit = hitPos;
        _lastPlaceNormal = hitNormal;
        return PlaceAgainstFace(hitPos, hitNormal, blockId, player);
    }

    /// <summary>
    /// Places <paramref name="blockId"/> against the face of <paramref name="hitPos"/> with outward
    /// normal <paramref name="hitNormal"/> (see <see cref="PlaceBlockAtFace"/> for the rules), then
    /// lets sand placed into thin air fall. Spends nothing.
    /// </summary>
    private bool PlaceAgainstFace(Vector3 hitPos, Vector3 hitNormal, byte blockId, PlayerContext player)
    {
        if (!PlaceBlockAtFace(hitPos, hitNormal, blockId, state: 0, player.ViewDirection, out var placePos)) return false;

        PunchSwing();
        // Sand placed into thin air falls at once instead of hovering (stackable piles never
        // fall - the registry forbids the combination - so only the block path topples). A
        // client never ticks falling blocks, so it leaves the fall to the server, which topples
        // the placement it replays and broadcasts the cells.
        if (!_clientMode)
            _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
                (int)placePos.X, (int)placePos.Y, (int)placePos.Z);
        return true;
    }

    /// <summary>
    /// Whether a solid block in <paramref name="cell"/> would trap a player: on a server every
    /// connected player's body counts (the server's own camera is nobody), elsewhere the local
    /// player's.
    /// </summary>
    private bool BodyBlocksPlacement(Vector3 cell)
    {
        if (!_serverMode)
            return Camera.OverlapsPlayer(cell);
        var players = ServerPlayers;
        for (var i = 0; i < players.Count; i++)
            if (PlayerContext.BodyOverlapsCell(players[i].Position, cell))
                return true;
        return false;
    }

    /// <summary>
    /// Guards and grounds one block against a raycast hit face: live cubes and a player's
    /// body refuse solid entries, fragile blocks need solid ground, and a placement into
    /// water displaces it. The filled cell is the neighbour across the hit face, or the hit
    /// cell itself when it holds a stack a placement may take over (layered snow), which it
    /// overwrites in place. <paramref name="state"/> rides into the state byte (a fresh rock
    /// pile starts at 1) and <paramref name="placedCell"/> reports the cell that was filled.
    /// <paramref name="viewDirection"/> is the placing player's look, which faces a block that
    /// takes its facing from it (stairs, <see cref="Placement.LookFacing"/>).
    /// Returns whether one was placed, without spending anything.
    /// </summary>
    private bool PlaceBlockAtFace(Vector3 hitPos, Vector3 hitNormal, byte blockId, byte state,
        Vector3 viewDirection, out Vector3 placedCell)
    {
        placedCell = Placement.TargetCell(hitPos, hitNormal, BlockIdAt(hitPos),
            _chunkManager.GetStateGlobal((int)hitPos.X, (int)hitPos.Y, (int)hitPos.Z));
        var placePos = placedCell;
        var chunkPos = VoxelCoords.ChunkPosOf(placePos);

        if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var chunk)) return false;

        // A torch against a block's side becomes its wall torch, leaning out from that wall (the
        // facing rides in the state byte); under a ceiling it has nothing to hang from
        if (!Placement.TryMount(blockId, state, hitNormal, placePos == hitPos, viewDirection, out blockId, out state))
            return false;

        // A live cube owns its cell until it lands: placing into one would cement the cube
        // inside the new block, so the placement fails and the caller keeps its item
        if (_falling.OverlapsAny((int)placePos.X, (int)placePos.Y, (int)placePos.Z))
            return false;

        // A solid block inside a player's body would trap them (or force the physics to
        // shove them out); non-solid blocks are fine to stand in. Flying is noclip but the
        // placement is still refused, so dropping out of flight never leaves the player buried.
        if (BlockType.IsSolid(blockId) && BodyBlocksPlacement(placePos))
            return false;
        // Fragile blocks need something solid holding them up (the ground, or the wall a wall
        // torch hangs on): a flower planted into mid-air would hover until anything remeshed it,
        // so the placement fails and the caller keeps its item
        if (BlockSupport.TryGetSupportCell(blockId, state, placePos, out var support)
            && !BlockType.IsSolid(BlockIdAt(support)))
            return false;

        // A placed log is building material, not part of a tree: it is a plain full cube that
        // never connects to tree wood around it and never fells (see TreeWoodShape.Connects)
        if (BlockType.IsTreeWood(blockId) && state == 0) state = WoodState.Detached;
        // A placed leaf is decoration, not canopy: persistent, so no missing tip ever decays it
        if (TreeGenerator.IsLeaf(blockId) && state == 0) state = LeafState.Encode(0, persistent: true);

        // A door needs the cell above free for its upper half too, or nothing is placed or spent
        var upperId = BlockType.ById[blockId]?.UpperHalfId ?? 0;
        if (upperId != 0 && !CanPlaceUpperHalf(placePos + Vector3.UnitY, upperId)) return false;

        if (!chunk.PlaceBlockAt(placePos, blockId, _chunkManager, state)) return false;

        // A full solid cube can raise or re-tile the far LOD's surface. Anything else never
        // counts as that surface (LodSurface.IsSurfaceSolid), and recording it would only read as
        // a sub-surface removal and cap the column, so a torch in a cave never dents the LOD.
        if (LodSurface.IsSurfaceSolid(blockId))
            NotifyLodEdit((int)placePos.X, (int)placePos.Y, (int)placePos.Z);

        // The chunk only meshes a quern's base: register the cell so its plate draws at
        // rest until it is inserted into or spun.
        if (BlockType.ById[blockId]?.Renderer == BlockRenderer.Quern)
            _querns.Ensure((int)placePos.X, (int)placePos.Y, (int)placePos.Z);

        // A block placed into water displaces it, so the neighbours recompute their levels
        _fluidManager.ScheduleAround(placePos);
        _leafDecay.ScheduleAround(placePos);
        BlockEdited?.Invoke((int)placePos.X, (int)placePos.Y, (int)placePos.Z, blockId, state);
        if (upperId != 0)
            PlaceUpperHalf(placePos + Vector3.UnitY, upperId, state);

        // The placement pop: the block appears a little too big and settles into its cell
        // (rendered in the select pass); the upper half of a door rides along with its lower
        _placePopElapsed = 0f;
        _placePopPosition = placePos;
        _placePopId = blockId;
        return true;
    }

    /// <summary>Whether a door's upper half <paramref name="upperId"/> may go into
    /// <paramref name="cell"/>: loaded, holding nothing a placement may not take over, no live
    /// falling cube in it and no player's body in the way.</summary>
    private bool CanPlaceUpperHalf(Vector3 cell, byte upperId)
    {
        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
        if (!_chunkManager.TryGetCell(x, y, z, out var id, out var state) || !Placement.CanOccupy(id, state))
            return false;
        if (_falling.OverlapsAny(x, y, z)) return false;
        return !(BlockType.IsSolid(upperId) && BodyBlocksPlacement(cell));
    }

    /// <summary>Writes a door's upper half over its just-placed lower half, with the same state
    /// byte, and runs the placement hooks for it.</summary>
    private void PlaceUpperHalf(Vector3 cell, byte upperId, byte state)
    {
        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
        if (_chunkManager.ChunkAt(x, y, z) is not { } chunk || !chunk.PlaceBlockAt(cell, upperId, _chunkManager, state))
            return;
        _fluidManager.ScheduleAround(cell);
        _leafDecay.ScheduleAround(cell);
        BlockEdited?.Invoke(x, y, z, upperId, state);
    }
    
    public ChunkManager GetChunkManager() => _chunkManager;

    internal LodManager GetLodManager() => _lodManager;
}
