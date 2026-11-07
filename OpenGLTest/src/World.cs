using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using Silk.NET.Input;
using Silk.NET.Windowing;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.Chunks.Streaming;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Lod;
using VoxelEngine.Magic;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Rendering;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.Icebergs;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.Status;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine;

/// <summary>
/// The world's composition root. It owns the camera, the chunk manager and its generation and
/// far-LOD workers, the simulations (drops, TNT, falling blocks, the mob managers, fluids, leaf
/// decay, saplings and the stations) and the per-job components it builds in its constructor:
/// <see cref="LocalPlayer"/>, <see cref="Strikes"/>, <see cref="Ambient"/>, <see cref="Edits"/>,
/// <see cref="LeftClick"/>, <see cref="Placer"/>, <see cref="Throws"/>,
/// <see cref="Interactions"/>, <see cref="ItemUse"/>, <see cref="RightClick"/>, <see cref="Caster"/>,
/// <see cref="Spells"/>, <see cref="Feedback"/>,
/// <see cref="ClientMirror"/>, <see cref="Persistence"/>, <see cref="Pickup"/>,
/// <see cref="Director"/> and <see cref="Renderer"/>. It runs the frame (<see cref="Update"/>, which hands a multiplayer
/// client to <see cref="UpdateClient"/> and a loading world to <see cref="UpdateLoading"/>) and
/// the lifecycle (the constructor and <see cref="SaveAndDispose"/>), and keeps the small public
/// facade the dedicated server needs. Every <see cref="Item"/> override runs its effects through
/// <see cref="WorldItemUse"/>, the one <see cref="IItemUseWorld"/> implementation, instead of
/// growing a branch in the world.
/// </summary>
public class World
{
    // How far the player can target blocks (10 blocks)
    public const float InteractionReach = 10f;

    private readonly ChunkManager _chunkManager;
    private readonly ChunkGenerationWorker _generationWorker;
    private readonly LodManager _lodManager;
    private readonly DropManager _dropManager = new();
    private readonly TntManager _tntManager = new();
    private readonly FallingBlockManager _falling = new();
    private readonly SlimeManager? _slimes;
    private readonly IcebergManager? _icebergs;
    private readonly GoblinManager? _goblins;
    private readonly HellMobManager? _hellMobs;
    private readonly SkyMobManager? _skyMobs;
    private readonly SaplingManager _saplings = new();
    // The sapling clock's grow callback, built once: a method group allocates a delegate each time it is converted
    private Func<string, int, int, int, int, TreeGrowthResult>? _growTree;
    // The natural and hard mob caps every manager above spawns under, counted over all of them.
    private readonly MobCaps _mobCaps = new();
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
    private readonly FireParticleSystem _lavaFire = new(ParticleConfig.LavaEmberPerSecond,
        ParticleConfig.LavaSmokePerSecond, ParticleConfig.LavaParticleMax,
        new Vector3(0.5f, FluidState.SourceHeight, 0.5f));
    // Torch flames: a third instance with tiny, tight particles spawned exactly at each flame point
    // (Chunk.FlamePoints, already world-exact, so no spawn offset)
    private readonly FireParticleSystem _torchFire = new(ParticleConfig.TorchEmberPerSecond,
        ParticleConfig.TorchSmokePerSecond, ParticleConfig.TorchParticleMax, Vector3.Zero,
        ParticleConfig.TorchFlameJitter, ParticleConfig.TorchFlameScale);

    private readonly BubbleParticleSystem _bubbles = new();

    // The glowing arcs behind swinging blades, the local player's and the remote players'; cosmetic
    private readonly SliceTrailSystem _sliceTrails = new();

    // Where edited chunks, the far-LOD edit state, the player and the entities are persisted; null = no
    // persistence (tests, VOXEL_SCRIPT runs, and the game until the menu attaches one)
    private readonly ChunkSaveStore? _saveStore;

    // Readiness of the chunks around the player while Loading; null for a world that starts
    // Playing (no save store)
    private WorldLoadGate? _loadGate;

    // What the first SaveAndDispose returned, so a repeated call reports the same thing
    private bool _disposeResult;

    // Trees currently toppling over after their base log was mined
    private readonly List<TopplingTree> _toppling = [];

    // Other players on this client's server (multiplayer only); empty in singleplayer.
    private readonly List<RemotePlayer> _remotePlayers = [];

    // Null on the headless path (tests), where Now runs off the summed Update deltas instead
    private readonly IWindow? _window;
    private double _headlessTime;

    // True for a dedicated server: no local player, a headless chunk manager and no far LOD.
    private readonly bool _serverMode;

    // True for a multiplayer client: no worldgen and no local simulation; chunks stream from the server.
    private readonly bool _clientMode;

    // The window clock, or the headless one: every gameplay time read goes through this
    internal double Now => _window?.Time ?? _headlessTime;

    // Every airborne projectile to draw and mirror this frame: the player's throws followed by
    // the goblins' stones, rebuilt after both updated. Drawing only - hit tests stay per owner.
    private readonly List<ThrownProjectile> _projectiles = [];

    public Camera Camera { get; }

    /// <summary>The blocky player body: walk-swung limbs, jabbing right arm.</summary>
    public PlayerModel Player { get; } = new();

    /// <summary>The local player's survival and body: spawn settle, health, death, the body model.</summary>
    internal LocalPlayer LocalPlayer { get; }

    /// <summary>What a landed hit does to a mob: damage, shove, sparks and the floating number.</summary>
    internal MobStrikes Strikes { get; }

    /// <summary>The ambient cosmetic effects: steam, lava embers, torch flames, bubbles, splashes, blast shake.</summary>
    internal AmbientEffects Ambient { get; }

    /// <summary>The block edit paths: breaking, felling, multiblocks, tall blocks, toppling trees, edit reports.</summary>
    internal BlockEditor Edits { get; }

    /// <summary>The left-click action: the crosshair target, mining, punch and slice, the crosshair and look-at label.</summary>
    internal LeftClickAction LeftClick { get; }

    /// <summary>Block placement: the placement edit paths, the place pop, the pick block and the last placement hit.</summary>
    internal BlockPlacer Placer { get; }

    /// <summary>Throwing: the Q-drop, the throw charge and release, and the flying thrown items.</summary>
    internal ThrowAction Throws { get; }

    /// <summary>The station and openable right-clicks: campfire, quern, kiln, cast mold, door and trapdoor.</summary>
    internal BlockInteractions Interactions { get; }

    /// <summary>Item use: the one <see cref="IItemUseWorld"/> implementation and the use dispatcher.</summary>
    internal WorldItemUse ItemUse { get; }

    /// <summary>The right-click action: the station, item-use and placement chain and the timed use charge.</summary>
    internal RightClickAction RightClick { get; }

    /// <summary>The cast seam: every staff cast, local or replayed for a server's player, passes it.</summary>
    internal SpellCaster Caster { get; }

    /// <summary>What accepted casts do: the spell bolts in flight and the element impacts; null on a client.</summary>
    internal SpellEffects? Spells { get; }

    /// <summary>The cosmetic side of casts (the cast burst and sounds, a client's preview bolts); null on a headless server.</summary>
    internal SpellFeedback? Feedback { get; }

    /// <summary>The multiplayer client's mirror of its server: cell changes, entities, stations, health, remote players.</summary>
    internal ClientMirror ClientMirror { get; }

    /// <summary>The world's persistence: autosave, the full flush, the player and entity capture and the pending restore.</summary>
    internal WorldPersistence Persistence { get; }

    /// <summary>The per-frame drop pickup: the collectors, the drop update, crediting and the full-inventory warning.</summary>
    internal DropPickup Pickup { get; }

    /// <summary>The per-frame mob job: the mob targets, the slime and goblin updates and their damage, the blast shove and the mob list.</summary>
    internal MobDirector Director { get; }

    /// <summary>The world's draw: the frame's render passes in order and the HUD.</summary>
    internal WorldRenderer Renderer { get; }

    /// <summary>The player's hotbar; its selected slot decides what block placing puts down.
    /// A new survival player starts with nothing; creative mode grants from the catalog. On a
    /// server it belongs to no one: every connected player carries their own
    /// (<see cref="ServerPlayer.Inventory"/>).</summary>
    public Inventory Inventory { get; } = Inventory.Empty();

    // The selected item's name over the hotbar, fed from Inventory's selection every frame
    private readonly HotbarNameFade _hotbarName = new();

    // The hotbar's pickup pops, fed from Inventory's slots every frame (a rising count pops)
    private readonly HotbarPickupPop _hotbarPops = new();

    /// <summary>The hotbar's pickup pops and flying pickup icons, fed by <see cref="DropPickup"/>.</summary>
    internal HotbarPickupPop HotbarPops => _hotbarPops;

    /// <summary>The floating damage numbers over struck mobs (see <see cref="DamageNumberPool"/>): from
    /// landed hits in singleplayer, from snapshot health drops on a client.</summary>
    internal DamageNumberPool DamageNumbers { get; } = new(MobConfig.DamageNumberCapacity);

    /// <summary>
    /// Server mode: the connected players, owned by the server and read once a frame by the drop
    /// pickup, which credits each player's own inventory (flagging
    /// <see cref="ServerPlayer.InventoryChanged"/>). Empty, and ignored, outside server mode.
    /// </summary>
    public IReadOnlyList<ServerPlayer> ServerPlayers { get; set; } = Array.Empty<ServerPlayer>();

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

    /// <summary>The air under water as a share of a full lung, 0..1; the HUD's bubbles.</summary>
    public float AirShare => _health.Breath.Fraction;

    /// <summary>The HUD toast line (the day change, a first pickup, a full inventory), drawn while
    /// its alpha is above zero.</summary>
    public HudToast Toast { get; } = new();

    // The crosshair's hit marker, shown where the hit-stop is requested
    // (LeftClickAction.UpdatePunch/UpdateSlice)
    private readonly HitMarker _hitMarker = new();

    /// <summary>
    /// Credits <paramref name="count"/> of an item to this world's inventory (the creative grants
    /// and the /give command), toasting a first find like a pickup does.
    /// </summary>
    public void CreditItem(byte id, int count)
    {
        var known = Inventory.Knows(id);
        if (Inventory.Add(id, count) && !known)
            Toast.Show($"New: {Inventory.DisplayName(id)}");
    }

    /// <summary>Where the world is in its lifecycle (see <see cref="WorldPhase"/>).</summary>
    public WorldPhase Phase { get; private set; }

    /// <summary>Whether the full game runs: input, physics, mobs, fluids and autosave.</summary>
    public bool IsPlaying => Phase == WorldPhase.Playing;

    /// <summary>False only while the world is still <see cref="WorldPhase.Loading"/>.</summary>
    public bool IsLoaded => Phase != WorldPhase.Loading;

    /// <summary>How far loading got, 0..1 and never decreasing; 1 for a world that never loaded.</summary>
    public float LoadProgress => _loadGate?.Progress ?? 1f;

    /// <summary>
    /// True when the world started playing because <see cref="StreamingConfig.LoadTimeoutSeconds"/>
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
    public IReadOnlyList<Slime> Slimes => _slimes?.Slimes ?? [];

    /// <summary>Live goblins, feeding the entity, shadow and mirror passes.</summary>
    public IReadOnlyList<Goblin> Goblins => _goblins?.Goblins ?? [];

    /// <summary>Live hell mobs (brutes and casters), feeding the entity and shadow passes.</summary>
    public IReadOnlyList<HellMob> HellMobs => _hellMobs?.Mobs ?? [];

    /// <summary>Live sky mobs (galeraptors and cloud golems), feeding the entity and shadow passes.</summary>
    public IReadOnlyList<SkyMob> SkyMobs => _skyMobs?.Mobs ?? [];

    /// <summary>Live icebergs (a client's mirrors of the server's); public for the server's broadcast.</summary>
    public IReadOnlyList<Iceberg> Icebergs => _icebergs?.Icebergs ?? [];

    /// <summary>The iceberg simulation, when the iceberg entity exists.</summary>
    internal IcebergManager? IcebergManager => _icebergs;

    /// <summary>The non-block solids a player body collides with (the icebergs), for the server's judging.</summary>
    public IBodySolids? BodySolids => _icebergs;

    // Every live mob of every kind, rebuilt once a frame after the mob managers ran
    // (MobDirector.RefreshMobs), so punch, slice, thrown items and healthbars target them all.
    private readonly List<Mob> _mobs = [];

    /// <summary>Every live mob (slimes, goblins), feeding targeting and the healthbars.</summary>
    public IReadOnlyList<Mob> Mobs => _mobs;

    /// <summary>Every burning cell this frame, gathered by <see cref="Ambient"/>.</summary>
    public IReadOnlyList<Vector3> FireCells => Ambient.FireCells;

    /// <summary>
    /// Builds the world and starts its chunk-generation and LOD workers. With a
    /// <paramref name="saveStore"/>, edited chunks are snapshotted to it on unload and on autosave,
    /// restored from it when they stream back in, the far-LOD edit state
    /// (<see cref="LodEditState"/>) is imported from it before the first LOD region builds, and the
    /// saved player (<see cref="PlayerSaveCodec.FileName"/>: pose, modes, health, inventory) is
    /// applied before the workers start, so they stream around the restored position; the saved mobs
    /// and drops (<see cref="EntitySaveCodec.FileName"/>) are spawned once loading finishes, each
    /// as soon as its chunk is loaded (<see cref="WorldPersistence.RestorePendingEntities"/>). A
    /// world with a store starts <see cref="WorldPhase.Loading"/>; null (the default) keeps the old
    /// regenerate-and-forget behavior and starts <see cref="WorldPhase.Playing"/> at once.
    /// </summary>
    /// <remarks>
    /// Passing null for <paramref name="gl"/>, the three pools, <paramref name="window"/> and
    /// <paramref name="input"/> is the headless path the tests use: the camera takes no input and
    /// the clock runs off the summed <see cref="Update"/> deltas.
    /// <para>
    /// The constructor registers the world as live (<see cref="WorldSeed.BeginWorld"/>), so
    /// <see cref="WorldSeed.Apply"/> refuses to run from here on: its worker threads read the
    /// worldgen statics unsynchronized. <see cref="SaveAndDispose"/> is the teardown; it stops the
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
            _saveStore = saveStore;
            _serverMode = serverMode;
            _clientMode = clientMode;
            // A client never searches the worldgen spawn: it has no terrain of its own, so it
            // starts at the server's spawn (or a fixed placeholder until one is known).
            Camera = new Camera(window, input, clientMode ? spawn ?? Camera.PlaceholderSpawn : spawn);
            // A server's camera is nobody, so no effect may land on it; a client's carries the mirror
            if (!serverMode)
                Camera.Effects = _health.Effects;
            LocalPlayer = new LocalPlayer(this, _health, _dust, _debris, _mobs, serverMode, clientMode);
            var savedPlayer = PlayerSaveCodec.TryParse(saveStore?.ReadFile(PlayerSaveCodec.FileName));
            if (savedPlayer != null)
                LocalPlayer.ApplySavedPlayer(savedPlayer);
            // A new store-backed survival player starts with the hint scroll; storeless scenario and
            // test worlds, the server camera and clients never do
            if (saveStore != null && !serverMode && !clientMode && StarterKit.NeededBy(savedPlayer))
                StarterKit.GiveTo(Inventory);
            EntitySaveData? savedEntities = null;
            StationSaveData? savedStations = null;
            if (saveStore != null)
            {
                // Held until the Loading -> Playing flip, then spawned as their chunks load
                savedEntities = EntitySaveCodec.TryParse(saveStore.ReadFile(EntitySaveCodec.FileName));
                savedStations = StationSaveCodec.TryParse(saveStore.ReadFile(StationSaveCodec.FileName));
            }
            LocalPlayer.RecordSpawn();
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
            if (clientMode)
            {
                // A client starts at the server's spawn (the spawn argument), so its own worldgen
                // spawn search never runs - which would compute columns the server owns.
                LocalPlayer.MarkSpawnSettled();
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
            if (EntityRegistry.TryGet("slime") is { } slimeType)
                _slimes = new SlimeManager(slimeType, _mobCaps);
            if (EntityRegistry.TryGet("goblin") is { } goblinType)
                _goblins = new GoblinManager(goblinType, _mobCaps);
            var bruteType = EntityRegistry.TryGet(Hellbrute.EntityName);
            var casterType = EntityRegistry.TryGet(Cinderwitch.EntityName);
            if (bruteType != null || casterType != null)
                _hellMobs = new HellMobManager(bruteType, casterType, _mobCaps);
            var flyerType = EntityRegistry.TryGet(Galeraptor.EntityName);
            var golemType = EntityRegistry.TryGet(Cloudgolem.EntityName);
            if (flyerType != null || golemType != null)
                _skyMobs = new SkyMobManager(flyerType, golemType, _mobCaps);
            if (EntityRegistry.TryGet("iceberg") is { } icebergType)
                _icebergs = new IcebergManager(icebergType);
            Camera.BodySolids = _icebergs;
            // The per-job components: each only stores its references here and reads this world lazily
            Strikes = new MobStrikes(this, _debris, serverMode);
            Ambient = new AmbientEffects(this, _chunkManager, _fire, _torchFire, _bubbles, _debris, _dropManager,
                _falling, _tntManager, _mobs, _projectiles, serverMode, _campfires, _kilns, _goblins);
            Edits = new BlockEditor(this, _chunkManager, _debris, _dropManager, _campfires, _querns, _kilns, _casting,
                _fluidManager, _leafDecay, _lodManager, _leaves, _falling, _toppling, clientMode, serverMode);
            LeftClick = new LeftClickAction(this, _chunkManager, _debris, _leaves, _hitMarker, _mobs, sfx, clientMode);
            Placer = new BlockPlacer(this, _chunkManager, _falling, _icebergs, _querns, _fluidManager, _leafDecay,
                _lodManager, _debris, clientMode, serverMode);
            Throws = new ThrowAction(this, _chunkManager, _dropManager, _goblins, _hellMobs, _mobs, _projectiles,
                clientMode);
            Interactions = new BlockInteractions(this, _chunkManager, _dropManager, _campfires, _querns, _kilns,
                _casting, _fluidManager, _leafDecay);
            ItemUse = new WorldItemUse(this, _chunkManager, _dropManager, _fluidManager, _leafDecay, _falling,
                _lodManager, _tntManager, _campfires, _kilns, _health, _hotbarPops, _debris, clientMode, serverMode);
            RightClick = new RightClickAction(this, clientMode);
            Caster = new SpellCaster(this, clientMode);
            ClientMirror = new ClientMirror(this, _chunkManager, _dropManager, _tntManager, _falling, _slimes, _goblins, _hellMobs,
                _skyMobs, _icebergs, _campfires, _querns, _kilns, _casting, _debris, _health, _remotePlayers, clientMode, serverMode);
            Persistence = new WorldPersistence(this, saveStore, _chunkManager, _dropManager, _slimes, _goblins, _hellMobs, _skyMobs,
                _icebergs, _health, serverMode, savedPlayer, savedEntities, _campfires, _querns, _kilns, _casting, savedStations);
            Pickup = new DropPickup(this, _dropManager, _chunkManager, _goblins, _fire, sfx, serverMode, clientMode);
            // Status effects convert cells only where the world is authoritative: a client's effects
            // are mirrors, and the server's writes reach it through the journal
            var statusBlocks = clientMode
                ? null
                : new StatusEffectBlockWriter(_chunkManager, _fluidManager, _leafDecay, _falling, _lodManager,
                    Edits.NotifyLodEdit);
            Director = new MobDirector(this, _chunkManager, _dropManager, _slimes, _goblins, _hellMobs, _skyMobs, _icebergs,
                _debris, serverMode ? null : _fire, _health, _mobs, serverMode, clientMode, statusBlocks);
            Spells = statusBlocks == null
                ? null
                : new SpellEffects(this, _chunkManager, statusBlocks, ItemUse, _debris, sfx, serverMode);
            if (Spells != null) Caster.Effect = Spells.Cast;
            Feedback = serverMode ? null : new SpellFeedback(this, _chunkManager, _debris, sfx, clientMode);
            if (Feedback != null) Caster.Feedback = Feedback.OnCast;
            Renderer = new WorldRenderer(this, _window, _chunkManager, _lodManager, _dropManager, _tntManager, _falling,
                _goblins, _campfires, _querns, _kilns, _casting, _toppling, _projectiles, _remotePlayers, _debris,
                _leaves, _fire, _lavaFire, _torchFire, _bubbles, _sliceTrails, _health, _hotbarName, _hotbarPops);

            // A store means a real world opened from (or created into) a save: it streams first and
            // plays once the chunks around the player are there. Larger, still bounded budgets
            // speed the stream up without letting any one frame spike.
            Phase = saveStore != null ? WorldPhase.Loading : WorldPhase.Playing;
            if (Phase == WorldPhase.Loading)
            {
                _loadGate = new WorldLoadGate(StreamingConfig.LoadReadyRadiusChunks,
                    StreamingConfig.LoadTimeoutSeconds);
                _chunkManager.MeshUploadBudget = StreamingConfig.LoadingMeshUploadsPerFrame;
                _chunkManager.DirtyStartBudget = StreamingConfig.LoadingDirtyChunksPerFrame;
                _lodManager.UploadBudget = StreamingConfig.LoadingLodUploadsPerFrame;
                Camera.SuppressLook = true;
            }

            // The chop damage of natural trunks, before any chunk meshes so the first meshes already show it;
            // only a singleplayer world keeps any (a server and a client never chop for themselves)
            if (!serverMode && !clientMode)
                if (WoodDamageCodec.TryParse(_saveStore?.ReadFile(WoodDamageCodec.FileName)) is { } savedDamage)
                    _chunkManager.ChopDamage.Import(savedDamage);

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
            Director.RefreshMobs();
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

    /// <summary>Rebuilds <see cref="Mobs"/> (see <see cref="MobDirector.RefreshMobs"/>).</summary>
    internal void RefreshMobs() => Director.RefreshMobs();

    /// <summary>Spawns one mob of the named kind with its feet at <paramref name="feet"/>, the scenario
    /// <c>spawn</c> op (see <see cref="MobDirector.SpawnEntity"/>).</summary>
    public bool SpawnEntity(string name, Vector3 feet, int size = SlimeSize.Min) =>
        Director.SpawnEntity(name, feet, size);

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
    /// <paramref name="yHint"/> centers the column scan (the generated ground + 1). Refused with
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

    /// <summary>Holds the lighting clock still for a paused frame: the window clock keeps running, so
    /// the offset gives that time back. A client follows the server's day clock and is left alone.</summary>
    public void HoldLightingClock(double deltaTime)
    {
        if (_clientMode)
            return;
        TimeOffset -= deltaTime;
    }

    /// <summary>The other players on this client's server, drawn each frame. Empty in singleplayer.</summary>
    public IReadOnlyList<RemotePlayer> RemotePlayers => _remotePlayers;

    /// <summary>The slice trails behind the local and remote players' blade swings; fed outside server mode.</summary>
    internal SliceTrailSystem SliceTrails => _sliceTrails;

    // Feeds the slice trails right after the remote players moved, from the same eye, look and back
    // offset the held item is drawn with; cosmetic, so never on a server
    private void UpdateSliceTrails()
    {
        if (_serverMode)
            return;
        var held = LocalPlayer.HasHeld ? ItemRegistry.TryGet(LocalPlayer.HeldId) : null;
        _sliceTrails.Update(Now, Player, held, Camera.Position, Camera.PlayerYaw, Camera.Pitch,
            HeldItemPose.BackOffset(Camera.ShoulderBlend), _remotePlayers);
    }

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

    /// <summary>Hands an intent to a connected client's sender; nothing in singleplayer or on a server.</summary>
    internal void RaiseIntent(in WorldIntent intent) => IntentRaised?.Invoke(intent);

    /// <summary>
    /// The chance roll every random item use reads (a firestarter's spark), 0..1. A test seam:
    /// tests make a roll succeed by returning 0.
    /// </summary>
    internal Func<float> UseRoll { get; set; } = static () => Random.Shared.NextSingle();

    /// <summary>
    /// The cell <paramref name="player"/> aims at within <see cref="InteractionReach"/> and the face
    /// the aim entered it through: what a server checks a client's claimed target against, so an
    /// intent can only touch a cell the player's own eye reaches and sees.
    /// </summary>
    public bool RaycastFor(PlayerContext player, bool includeFluids, out Vector3 hit, out Vector3 normal) =>
        player.Raycast(InteractionReach, _chunkManager, out hit, out normal, includeFluids);

    /// <summary>Breaks the block in <paramref name="cell"/> for <paramref name="player"/> with every
    /// consequence a local break has (see <see cref="BlockEditor.BreakBlockFor"/>); public for the
    /// dedicated server.</summary>
    public bool BreakBlockFor(PlayerContext player, Vector3 cell) => Edits.BreakBlockFor(player, cell);

    /// <summary>Places <paramref name="player"/>'s selected block against a face it can see (see
    /// <see cref="BlockPlacer.TryPlaceSelectedBlockAt"/>); public for the dedicated server.</summary>
    public bool TryPlaceSelectedBlockAt(PlayerContext player, Vector3 hitCell, Vector3 hitNormal) =>
        Placer.TryPlaceSelectedBlockAt(player, hitCell, hitNormal);

    /// <summary>Lands <paramref name="player"/>'s punch (see <see cref="LeftClickAction.PunchFor"/>); public
    /// for the dedicated server.</summary>
    public bool PunchFor(PlayerContext player) => LeftClick.PunchFor(player);

    /// <summary>Lands one of <paramref name="player"/>'s slice swings (see
    /// <see cref="LeftClickAction.SliceFor"/>); public for the dedicated server.</summary>
    public int SliceFor(PlayerContext player, bool returnSwing) => LeftClick.SliceFor(player, returnSwing);

    /// <summary>Lands one of <paramref name="player"/>'s sword swings (see
    /// <see cref="LeftClickAction.SwordFor"/>); public for the dedicated server.</summary>
    public int SwordFor(PlayerContext player, SwordSwing swing) => LeftClick.SwordFor(player, swing);

    /// <summary>Releases a throw of <paramref name="player"/>'s selected throwable (see
    /// <see cref="ThrowAction.ThrowFor"/>); public for the dedicated server.</summary>
    public bool ThrowFor(PlayerContext player, float charge01) => Throws.ThrowFor(player, charge01);

    /// <summary>Casts <paramref name="player"/>'s held staff's <paramref name="slot"/> through the one cast
    /// seam (see <see cref="SpellCaster.CastFor"/>), paced by <paramref name="pacing"/>; public for the
    /// dedicated server.</summary>
    public CastVerdict CastFor(PlayerContext player, int slot, CastPacing pacing, double now, double slackSeconds) =>
        Caster.CastFor(player, slot, pacing, now, slackSeconds);

    /// <summary>The effect every accepted cast fires (see <see cref="SpellCaster.Effect"/>): by default
    /// <see cref="SpellEffects.Cast"/> in an authoritative world; tests may replace it.</summary>
    public Action<SpellCast>? SpellEffect
    {
        get => Caster.Effect;
        set => Caster.Effect = value;
    }

    /// <summary>Throws <paramref name="inventory"/>'s selected slot onto the ground from an explicit
    /// eye (see <see cref="ThrowAction.ThrowSelected(bool, Vector3, Vector3, Inventory)"/>); public
    /// for the dedicated server.</summary>
    public bool ThrowSelected(bool wholeStack, Vector3 eye, Vector3 forward, Inventory inventory) =>
        Throws.ThrowSelected(wholeStack, eye, forward, inventory);

    /// <summary>Runs <paramref name="player"/>'s right-click (see
    /// <see cref="RightClickAction.TryUseSelected(float, PlayerContext, out RightClickOutcome, bool)"/>);
    /// public for the dedicated server.</summary>
    public bool TryUseSelected(float maxDistance, PlayerContext player, out RightClickOutcome outcome,
        bool allowPlace = true) => RightClick.TryUseSelected(maxDistance, player, out outcome, allowPlace);

    /// <summary>Whether any station block entity (campfire, quern, kiln, cast mold) is tracked,
    /// so a server with none sends no station snapshots.</summary>
    public bool HasStations => StationNetCodec.AnyStations(_campfires, _querns, _kilns, _casting);

    /// <summary>Writes every station block entity for the server's broadcast
    /// (<see cref="NetMessage.StationState"/>).</summary>
    public void WriteStations(PacketWriter writer) =>
        StationNetCodec.Write(writer, _campfires, _querns, _kilns, _casting);

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

        Placer.AdvancePlacePop(deltaTime);
        LocalPlayer.UpdatePlayerModel(deltaTime, _chunkManager);

        foreach (var remotePlayer in _remotePlayers)
            remotePlayer.Update(deltaTime);
        UpdateSliceTrails();

        // The purely cosmetic systems a client's own edits still feed (mining debris, leaf bursts
        // and the ambient leaf fall) run locally, or what WorldRenderer.Render draws would hang in
        // mid-air. Falling blocks, TNT and toppling trees are never created on a client (see
        // BlockEditor.RemoveBlock), so their ticks stay the server's: the falling cubes and primed
        // TNT drawn here are the server's, mirrored from its EntityList
        // (ClientMirror.ApplyNetworkMirrors), and only the TNT fuse burns down locally so the flash
        // keeps its pace between snapshots. The station mirrors only animate (a quern top turning
        // between snapshots) and feed the fire particles; the server runs the stations and its
        // StationState snapshots replace them. A throw this client released flies as a cosmetic
        // copy of the server's, which alone deals damage and lands as a drop.
        FrameProfiler.Begin(ProfileSection.Entities);
        _leaves.Update((float)deltaTime, Camera.RenderEye, _chunkManager, windTime: LightingTime);
        _debris.Update((float)deltaTime, _chunkManager);
        _querns.Animate((float)deltaTime);
        _slimes?.AnimateMirrors((float)deltaTime, _debris);
        _goblins?.AnimateMirrors((float)deltaTime, _debris, _chunkManager, DayCycle.NightFactor(LightingTime));
        _hellMobs?.AnimateMirrors((float)deltaTime, _debris);
        _skyMobs?.AnimateMirrors((float)deltaTime, _debris);
        _tntManager.AnimateMirrors((float)deltaTime);
        Director.AnimateIcebergMirrors(deltaTime);
        Ambient.UpdateStatusParticles((float)deltaTime);
        Ambient.GatherStationFires();
        Ambient.AddGoblinTorches();
        _fire.Update((float)deltaTime, Ambient.FireCells, Camera.RenderEye, wind: Wind.Direction(LightingTime));
        Ambient.UpdateTorchFlames((float)deltaTime, Wind.Direction(LightingTime));
        Ambient.UpdateBubbles((float)deltaTime);
        Throws.UpdateThrown((float)deltaTime);
        Throws.RefreshProjectiles();
        Ambient.UpdateSplashes((float)deltaTime);
        Feedback?.Update((float)deltaTime, _remotePlayers);
        FrameProfiler.End(ProfileSection.Entities);

        _hotbarPops.Update(Inventory, (float)deltaTime);
        _hotbarName.Update(Inventory, (float)deltaTime);
        DamageNumbers.Update((float)deltaTime);
        // The pool itself is the server's (ClientMirror.ApplyNetworkHealth mirrors it); only the
        // damage flash fades here
        _health.Update((float)deltaTime);

        FrameProfiler.Begin(ProfileSection.Raycast);
        LeftClick.UpdateSelection();
        FrameProfiler.End(ProfileSection.Raycast);
    }

    /// <summary>Scenario seam: damages every live mob (see <see cref="MobDirector.DamageAllMobs"/>).</summary>
    internal void DamageAllMobs(int amount) => Director.DamageAllMobs(amount);

    /// <summary>Scenario seam: applies a status effect to the local player's own pool, refused while
    /// the camera is immune (creative, flying) or the player is dead (see
    /// <see cref="StatusEffects.Apply(PlayerHealth, bool, string, float)"/>).</summary>
    internal bool ApplyPlayerEffect(string effectName, float durationScale) =>
        StatusEffects.Apply(_health, Camera.IsImmune, effectName, durationScale);

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
        LocalPlayer.EnsureSpawnSettled(_chunkManager);
        FrameProfiler.End(ProfileSection.Player);

        FrameProfiler.Begin(ProfileSection.Camera);
        Camera.Update((float)deltaTime, keyboard, _chunkManager);
        FrameProfiler.End(ProfileSection.Camera);

        FrameProfiler.Begin(ProfileSection.Player);

        // A server's hovering camera is nobody: its pool never changes, and each connected
        // player's status is ticked by the server (ServerPlayer.TickStatus)
        if (!_serverMode)
            LocalPlayer.UpdateHealth((float)deltaTime, _chunkManager);
        Placer.AdvancePlacePop(deltaTime);
        LocalPlayer.UpdatePlayerModel(deltaTime, _chunkManager);

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
        // picks the new mobs up this same frame. Returns at once, once nothing is pending.
        Persistence.RestorePendingEntities(deltaTime);

        Pickup.Update(deltaTime);

        _tntManager.Update((float)deltaTime, _chunkManager, _lodManager, _dropManager, _fluidManager, _falling, _leaves, _leafDecay);
        // Every blast this frame shoves the players and mobs in range and shakes the local camera by its
        // distance; a server's camera is nobody
        foreach (var blast in _tntManager.Blasts)
        {
            Director.PushFromBlast(blast);
            if (!_serverMode)
                Ambient.ShakeForBlast(blast);
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
        Ambient.GatherStationFires();
        Ambient.AddGoblinTorches();
        // The lighting clock (window time plus VOXEL_TIME), so the smoke blows the way the leaves lean
        var wind = Wind.Direction(LightingTime);
        _fire.Update((float)deltaTime, Ambient.FireCells, Camera.RenderEye, wind: wind);
        Ambient.GatherLavaVents(Camera.RenderEye);
        _lavaFire.Update((float)deltaTime, Ambient.LavaVents, Camera.RenderEye, wind: wind);
        // Hot springs steam while the eye is in range; cosmetic, so never on a server
        if (!_serverMode)
            Ambient.UpdateSpringSteam((float)deltaTime, Camera.RenderEye);
        Ambient.UpdateTorchFlames((float)deltaTime, wind);
        Ambient.UpdateBubbles((float)deltaTime);

        // Falling cubes shove the player aside instead of landing inside them; strictly
        // one-way, the player never deflects a cube
        foreach (var block in _falling.Falling)
            Camera.PushOutOf(block.Position, FallingBlock.HalfExtent, _chunkManager);

        // The icebergs drift and carry their rider, then the mobs: their targets, the slime and goblin
        // updates and the damage they deal (MobDirector)
        Director.UpdateIcebergs(deltaTime);
        Director.Update(deltaTime);

        // After every mob manager and before the thrown items, so a mob killed or despawned
        // this frame is never hit by a projectile or drawn a healthbar.
        Director.RefreshMobs();
        // A mob that landed this frame kicks up its dust and an affected mob sheds its effects'
        // particles; a server spawns no particles
        if (!_serverMode)
        {
            _dust.TakeMobLandings(_mobs, _chunkManager, _debris);
            Ambient.UpdateStatusParticles((float)deltaTime);
        }

        Edits.UpdateToppling((float)deltaTime);
        Throws.UpdateThrown((float)deltaTime);
        Throws.RefreshProjectiles();
        Spells?.Update((float)deltaTime);
        Ambient.UpdateSplashes((float)deltaTime);
        // Remote player poses ease toward the last broadcast and drive their own walk cycles.
        for (var i = 0; i < _remotePlayers.Count; i++)
            _remotePlayers[i].Update(deltaTime);
        UpdateSliceTrails();
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
            Ambient.PuffSteam(_fluidManager.HardenedCells);

        FrameProfiler.End(ProfileSection.FluidTick);

        // Like the fluid tick: after every edit path of the frame, so the leaves and orphaned
        // wood they scheduled re-derive together, and never while loading (UpdateLoading skips it)
        FrameProfiler.Begin(ProfileSection.LeafDecay);
        _leafDecay.Update((float)deltaTime, _chunkManager, _dropManager, _lodManager, _leaves);
        FrameProfiler.End(ProfileSection.LeafDecay);

        // A client never grows anything itself: the server owns the world and broadcasts its cells
        if (!_clientMode)
            _saplings.Update((float)deltaTime, _chunkManager, _growTree ??= TryGrowTree);

        // After the fluid tick, so every edit path of the frame precedes the autosave. The one
        // Save span in the codebase, entered here only and never nested inside another section.
        FrameProfiler.Begin(ProfileSection.Save);
        Persistence.UpdateAutosave(deltaTime);
        FrameProfiler.End(ProfileSection.Save);

        _hotbarPops.Update(Inventory, (float)deltaTime);
        _hotbarName.Update(Inventory, (float)deltaTime);
        DamageNumbers.Update((float)deltaTime);
        Toast.Update((float)deltaTime);
        _hitMarker.Update((float)deltaTime);

        // Water is aimed through unless the held item is out to scoop it, so a pond never
        // stops the player targeting the block under it
        FrameProfiler.Begin(ProfileSection.Raycast);
        LeftClick.UpdateSelection();
        FrameProfiler.End(ProfileSection.Raycast);
    }

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
        LocalPlayer.EnsureSpawnSettled(_chunkManager);
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
        if ((gateDone && LocalPlayer.SpawnSettled) || _loadGate.TimedOut)
            FinishLoading();
    }

    /// <summary>
    /// The Loading -> Playing flip: the play budgets and mouse look come back, the saved lighting
    /// clock is applied against the clock as it stands now (so the day did not advance while
    /// loading), and the walk cycle forgets the preload position so the whole load never reads
    /// as one enormous step. The saved mobs and drops whose chunks the ready sphere already covers
    /// spawn here, before any manager has run; the rest keep waiting for their chunks.
    /// </summary>
    private void FinishLoading()
    {
        _chunkManager.MeshUploadBudget = StreamingConfig.MaxMeshUploadsPerFrame;
        _chunkManager.DirtyStartBudget = StreamingConfig.MaxDirtyChunksPerFrame;
        _lodManager.UploadBudget = StreamingConfig.MaxLodUploadsPerFrame;
        Camera.SuppressLook = false;
        if (Persistence.SavedPlayer?.LightingClock is { } clock)
            TimeOffset = clock - Now;
        Phase = WorldPhase.Playing;
        LocalPlayer.ForgetPreloadPosition();
        Persistence.RestorePendingEntities(0);
        Director.RefreshMobs();
        // The clock is applied and the capture no longer needs the saved one
        Persistence.ForgetSavedPlayer();
    }

    /// <summary>
    /// Saves and tears the world down so a second <see cref="World"/> (with any seed) can be built
    /// in this process. Main thread only, and blocking: up to
    /// <see cref="SaveConfig.WorkerStopJoinTimeoutMs"/> per worker thread, plus
    /// <see cref="SaveConfig.MeshDrainTimeoutMs"/> and
    /// <see cref="SaveConfig.SaveWriterShutdownTimeoutSeconds"/>.
    /// <list type="number">
    /// <item>The camera stops listening to the window and mouse.</item>
    /// <item>The generation and LOD workers are stopped and joined and the mesh runners drained
    /// FIRST, so the flush below sees a quiescent world and no thread can touch a chunk that is
    /// about to be freed.</item>
    /// <item><see cref="WorldPersistence.FlushAll"/> hands every edit, the player and the entities
    /// to the store, BEFORE <see cref="ChunkSaveStore.Shutdown"/>, since a store that stopped
    /// accepting drops every later write.</item>
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
    /// out. Afterwards <see cref="Update"/> is a no-op and <see cref="WorldRenderer.Render"/> must
    /// not be called.
    /// </summary>
    public bool SaveAndDispose(RenderManager? renderManager = null)
    {
        if (Phase == WorldPhase.Disposed)
            return _disposeResult;

        Camera.Detach();
        var genStopped = _generationWorker.Stop();
        var lodStopped = _lodManager.Stop();
        var drained = _chunkManager.DrainMeshWork(TimeSpan.FromMilliseconds(SaveConfig.MeshDrainTimeoutMs));
        Persistence.FlushAll();
        var written = _saveStore?.Shutdown(TimeSpan.FromSeconds(SaveConfig.SaveWriterShutdownTimeoutSeconds)) ?? true;

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

    public ChunkManager GetChunkManager() => _chunkManager;

    internal LodManager GetLodManager() => _lodManager;

    internal BlockDebrisSystem GetDebrisSystem() => _debris;
}
