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

    private readonly ChunkManager _chunkManager;
    private readonly ChunkGenerationWorker _generationWorker;
    private readonly LodManager _lodManager;
    private readonly DropManager _dropManager = new();
    private readonly TntManager _tntManager = new();
    private readonly FallingBlockManager _falling = new();
    private readonly SlimeManager? _slimes;
    private readonly FluidManager _fluidManager = new();
    private readonly LeafParticleSystem _leaves = new();
    private readonly BlockDebrisSystem _debris = new();
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
    private static readonly Comparison<(float dist2, Chunk chunk)> NearestVentChunkFirst =
        static (a, b) => a.dist2.CompareTo(b.dist2);

    // Every cell whose flame the particle system and the point lights draw: campfires plus kilns,
    // rebuilt once per update so both consumers read one list
    private readonly List<Vector3> _fireCells = [];
    private readonly BubbleParticleSystem _bubbles = new();
    private readonly SfxManager? _sfx;

    // Inventory is assigned in its own initialiser and never replaced, so the method group is the
    // same closed delegate every frame; passing `Inventory.CanAccept` inline at the DropManager call
    // allocated a fresh one per frame (PERF_TODO C7).
    private readonly Func<byte, bool> _canAcceptDrop;

    // Trees currently toppling over after their base log was mined
    private readonly List<TopplingTree> _toppling = [];
    private readonly IWindow _window;
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
    private bool _throwCharging;
    private float _throwChargeSeconds;
    private byte _throwItemId;
    private float _throwBaseDamage;
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

    // True once the player rests exactly on real ground at the origin spawn. The camera's
    // initial position is only a heightmap guess (pre-carve, see Camera.SpawnPosition), and
    // physics starts before the spawn chunks stream in (unloaded cells read as air), so
    // without this the player falls through and ends up buried once the terrain loads.
    private bool _spawnSettled;
    private readonly float _spawnX;
    private readonly float _spawnZ;

    /// <summary>The player's hotbar; its selected slot decides what block placing puts down.</summary>
    public Inventory Inventory { get; } = new();

    // First-cut survival: the HP pool plus the fall state feeding it. Landings of 5+
    // blocks bill (blocks-4)*10 HP, lava burns 10 HP/s on body contact, and death
    // respawns at spawn keeping the inventory. Creative and flight are immune.
    private readonly PlayerHealth _health = new();

    /// <summary>Current HP, 0-100, drawn as 10 hearts in the HUD.</summary>
    public int Health => _health.Health;
    private bool _wasGrounded = true;
    private float _fallPeakFeet;
    private float _prevFeet;
    private bool _hasFallState;
    private Vector3 _respawnEye;
    private bool _hasRespawnEye;

    /// <summary>Live primed TNT, feeding the renderer's dynamic point lights.</summary>
    public IReadOnlyList<PrimedTnt> PrimedTnt => _tntManager.Primed;

    /// <summary>Live falling sand/gravel cubes.</summary>
    public IReadOnlyList<FallingBlock> Falling => _falling.Falling;

    /// <summary>Live slimes, feeding the entity and shadow passes.</summary>
    public IReadOnlyList<Slime> Slimes => _slimes?.Slimes ?? (IReadOnlyList<Slime>)Array.Empty<Slime>();

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

    /// <summary>Live exhaled bubbles, feeding the bubble billboard pass.</summary>
    public IReadOnlyList<BubbleParticle> Bubbles => _bubbles.Particles;

    public IReadOnlyList<ThrownProjectile> Thrown => _thrown;
    public bool IsThrowCharging => _throwCharging;
    public float ThrowCharge01 => MathUtils.Clamp01(_throwChargeSeconds / WorldGenerationConfig.ThrowChargeSeconds);

    // Break overlay strength. Quantized to whole hits, so it steps with the damage dealt
    // instead of sweeping continuously.
    private float BreakProgress => _breakHardness > 0f ? _breakDamage / _breakHardness : 0f;

    // Growth of the block being mined: 0 at rest, eased up to BlockPopGrow and back down
    // over BlockPopSeconds after each hit lands
    private float BlockPopGrowNow =>
        BlockPopGrow * MathUtils.Pulse(_popElapsed / BlockPopSeconds);

    public World(GL gl, ChunkGeometryPool opaqueGeometry, ChunkGeometryPool translucentGeometry,
        ChunkGeometryPool lodGeometry, IWindow window, IInputContext input, SfxManager? sfx = null)
    {
        _window = window;
        _sfx = sfx;
        Camera = new Camera(window, input);
        _spawnX = Camera.Position.X;
        _spawnZ = Camera.Position.Z;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOXEL_SPAWN")))
            _spawnSettled = true;
        _chunkManager = new ChunkManager(gl, opaqueGeometry, translucentGeometry);
        _generationWorker = new ChunkGenerationWorker(_chunkManager, Camera);
        _lodManager = new LodManager(lodGeometry);
        _canAcceptDrop = Inventory.CanAccept;
        if (EntityRegistry.TryGet("slime") is { } slimeType)
            _slimes = new SlimeManager(slimeType);

        _generationWorker.Start();
        _lodManager.Start(Camera);
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
        var heightmap = Chunk.GetColumnHeights(cx, cz)[VoxelCoords.ColumnIndex(lx, lz)];
        var estimate = Math.Max(heightmap, WorldGenerationConfig.SeaLevel);
        const float spawnLift = 0.02f;
        var holdEye = estimate + 1 + Camera.EyeHeight + spawnLift;

        if (TerrainGenerator.IsWaterColumn(sx, sz, heightmap))
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

        var top = estimate + WorldGenerationConfig.TreeMaxHeightBlocks + 2;
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
    /// Seconds added to the window clock to get the lighting clock (Program's <c>VOXEL_TIME</c>
    /// offset). Set once before the first <see cref="Update"/>, so gameplay-side wind consumers
    /// read the same <see cref="Wind"/> as the render side.
    /// </summary>
    public double TimeOffset { get; set; }

    public void Update(double deltaTime, IKeyboard? keyboard)
    {
        FrameProfiler.Begin(ProfileSection.Player);
        EnsureSpawnSettled();
        FrameProfiler.End(ProfileSection.Player);

        FrameProfiler.Begin(ProfileSection.Camera);
        Camera.Update((float)deltaTime, keyboard, _chunkManager);
        FrameProfiler.End(ProfileSection.Camera);

        FrameProfiler.Begin(ProfileSection.Player);

        UpdateHealth((float)deltaTime);

        // The walk cycle runs off real horizontal speed, so the limbs keep up with sprint
        // and swim instead of playing a fixed-rate loop. Flying poses no stride, so it
        // feeds zero speed and the limbs ease back to rest.
        var camPos = Camera.Position;
        var horizontalSpeed = 0f;
        if (_hasPrevCamPos && deltaTime > 0.0)
        {
            var step = camPos - _prevCamPos;
            horizontalSpeed = new Vector3(step.X, 0f, step.Z).Length() / (float)deltaTime;
        }
        _prevCamPos = camPos;
        _hasPrevCamPos = true;
        _heldId = Inventory.SelectedBlockId;
        _hasHeld = _heldId != 0 && (Inventory.SelectedCount > 0 || Inventory.SelectedToolInstance > 0);
        Player.HoldingItem = _hasHeld;
        var animSpeed = Camera.IsFlying ? 0f : horizontalSpeed;
        var supported = Camera.IsGrounded || Camera.IsFlying || Camera.IsInWater;
        Player.Update((float)deltaTime, animSpeed, Camera.IsSprinting, supported,
            Camera.VerticalVelocity);

        // The chunk update below times itself, so the player span closes here.
        FrameProfiler.End(ProfileSection.Player);

        // Times itself, as ChunkUnload / ChunkApply / ChunkDirty
        _chunkManager.Update(Camera);

        FrameProfiler.Begin(ProfileSection.Lod);
        _lodManager.Update(_chunkManager);
        FrameProfiler.End(ProfileSection.Lod);

        FrameProfiler.Begin(ProfileSection.Entities);

        // Drops collected this tick land in the inventory pools. A full pool ignores the
        // drop (it is never attracted), and an id with no pool is discarded.
        _dropManager.Update((float)deltaTime, Camera.Position, _chunkManager, _canAcceptDrop);
        var pickedUp = false;
        foreach (var blockId in _dropManager.Collected)
            pickedUp |= Inventory.Add(blockId);
        if (pickedUp)
            _sfx?.Play(SfxLibrary.PickupGroup);

        _tntManager.Update((float)deltaTime, _chunkManager, _lodManager, _dropManager, _fluidManager, _falling, _leaves);
        _falling.Update((float)deltaTime, _chunkManager, _dropManager, _fluidManager);
        _leaves.Update((float)deltaTime, Camera.RenderEye, _chunkManager);
        _debris.Update((float)deltaTime, _chunkManager);
        _campfires.Update((float)deltaTime, _chunkManager, _dropManager);
        _querns.Update((float)deltaTime, _chunkManager, _dropManager);
        _kilns.Update((float)deltaTime, _chunkManager, _dropManager);
        _casting.Update((float)deltaTime, _chunkManager, _dropManager, Inventory,
            Inventory.SelectedToolInstance, Camera.Position, InteractionReach);
        _fireCells.Clear();
        _fireCells.AddRange(_campfires.LitCells);
        _fireCells.AddRange(_kilns.LitCells);
        // The lighting clock (window time plus VOXEL_TIME), so the smoke blows the way the leaves lean
        var wind = Wind.Direction(_window.Time + TimeOffset);
        _fire.Update((float)deltaTime, _fireCells, Camera.RenderEye, wind: wind);
        GatherLavaVents(Camera.RenderEye);
        _lavaFire.Update((float)deltaTime, _lavaVents, Camera.RenderEye, wind: wind);
        _bubbles.Update((float)deltaTime, Camera.RenderEye, Camera.ViewDirection, Camera.IsEyeInWater, _chunkManager);

        // Falling cubes shove the player aside instead of landing inside them; strictly
        // one-way, the player never deflects a cube
        foreach (var block in _falling.Falling)
            Camera.PushOutOf(block.Position, FallingBlock.HalfExtent, _chunkManager);

        // Slimes hop at the player: an airborne touch bills damage, a grounded one is just
        // shoved aside. The manager only reports the amount; the health pool stays here.
        if (_slimes != null)
        {
            _slimes.Update((float)deltaTime, _chunkManager, _dropManager, Camera);
            if (_slimes.DamageDealtThisFrame > 0)
            {
                _health.TakeDamage(_slimes.DamageDealtThisFrame);
                Camera.AddShake(Math.Clamp(_slimes.DamageDealtThisFrame / 20f, 0.2f, 1f));
            }
        }

        UpdateToppling((float)deltaTime);
        UpdateThrown((float)deltaTime);
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
        _fluidManager.Update((float)deltaTime, _chunkManager, _dropManager);

        FrameProfiler.End(ProfileSection.FluidTick);

        Inventory.Update((float)deltaTime);

        // Water is aimed through unless the held item is out to scoop it, so a pond never
        // stops the player targeting the block under it
        FrameProfiler.Begin(ProfileSection.Raycast);
        _hasSelectedBlock = Camera.Raycast(InteractionReach, _chunkManager,
            out _selectedBlockPosition, out _selectedFaceNormal, out _selectedBoxIndex,
            includeFluids: ItemRegistry.TryGet(Inventory.SelectedBlockId)?.TargetsFluids ?? false);
        FrameProfiler.End(ProfileSection.Raycast);
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
        if (!_hasFallState)
        {
            _fallPeakFeet = feet;
            _prevFeet = feet;
            _wasGrounded = Camera.IsGrounded;
            _hasFallState = true;
        }

        if (MathF.Abs(feet - _prevFeet) > 4f)
        {
            _fallPeakFeet = feet;
            _wasGrounded = Camera.IsGrounded;
        }
        else if (Camera.IsFlying)
        {
            _fallPeakFeet = feet;
            _wasGrounded = Camera.IsGrounded;
        }
        else if (!Camera.IsGrounded)
        {
            if (_wasGrounded)
                _fallPeakFeet = MathF.Max(_fallPeakFeet, _prevFeet);
            _fallPeakFeet = MathF.Max(_fallPeakFeet, feet);
            _wasGrounded = false;
        }
        else if (!_wasGrounded)
        {
            var drop = _fallPeakFeet - feet;
            if (!Camera.IsCreative && !Camera.IsInWater)
                _health.TakeDamage(PlayerHealth.FallDamage(drop));
            _fallPeakFeet = feet;
            _wasGrounded = true;
        }
        else
        {
            _fallPeakFeet = feet;
        }
        _prevFeet = feet;

        _health.UpdateLava(deltaTime, Camera.IsInLava, Camera.IsCreative || Camera.IsFlying);
        _health.Update(deltaTime);

        if (_health.Health < beforeHp)
            Camera.AddShake(Math.Clamp((beforeHp - _health.Health) / 40f, 0.2f, 1f));

        if (!_health.IsDead)
            return;

        _health.Respawn();
        var home = _hasRespawnEye ? _respawnEye : Camera.Position;
        Camera.TestTeleport(home, Camera.Yaw, Camera.Pitch);
        _fallPeakFeet = home.Y - Camera.EyeHeight;
        _prevFeet = _fallPeakFeet;
        _wasGrounded = false;
        Log.WriteLine("Respawned");
    }

    public void Render(RenderManager renderManager)
    {
        // Depth-only sun pass first: every world draw below samples the shadow map it fills
        FrameProfiler.Begin(ProfileSection.Shadows);
        renderManager.BeginGpuPass(ProfileSection.Shadows);
        renderManager.RenderShadows(Camera, _chunkManager.Renderable, _tntManager.Primed, _falling.Falling, _toppling, Player, Slimes,
            _lodManager.RegionList);
        renderManager.EndGpuPass(ProfileSection.Shadows);
        FrameProfiler.End(ProfileSection.Shadows);

        FrameProfiler.Begin(ProfileSection.Reflection);
        renderManager.BeginGpuPass(ProfileSection.Reflection);
        // One frustum pass over the translucent draws feeds both this and the water pass, and
        // tells the reflection whether there is any water on screen to be worth rendering
        renderManager.CullWater(Camera, _chunkManager.TranslucentRenderable, _lodManager.RegionList);
        renderManager.RenderReflection(Camera, _chunkManager.Renderable, _lodManager.RegionList,
            new ReflectionEntities(_dropManager.Drops, _thrown, _tntManager.Primed, _falling.Falling, Slimes, _toppling, Player));
        renderManager.EndGpuPass(ProfileSection.Reflection);
        FrameProfiler.End(ProfileSection.Reflection);

        FrameProfiler.Begin(ProfileSection.LodPass);
        renderManager.BeginGpuPass(ProfileSection.LodPass);
        renderManager.RenderLod(Camera, _lodManager.RegionList);
        renderManager.EndGpuPass(ProfileSection.LodPass);
        FrameProfiler.End(ProfileSection.LodPass);

        FrameProfiler.Begin(ProfileSection.WorldPass);
        renderManager.BeginGpuPass(ProfileSection.WorldPass);
        renderManager.RenderWorld(Camera, _chunkManager.Renderable, _window.Time);
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
        renderManager.RenderThrown(Camera, _thrown);
        renderManager.RenderCampfireCooking(Camera, _campfires);
        renderManager.RenderQuerns(Camera, _querns);
        renderManager.RenderKilns(Camera, _kilns);
        renderManager.RenderCastings(Camera, _casting);
        renderManager.RenderPrimedTnt(Camera, _tntManager.Primed);
        renderManager.RenderFallingBlocks(Camera, _falling.Falling);
        renderManager.RenderSlimes(Camera, Slimes);
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
        renderManager.RenderEntityHealthbars(Camera, Slimes);
        renderManager.RenderBlockDebris(Camera, _debris.Debris);
        foreach (var tree in _toppling)
            renderManager.RenderTopplingTree(Camera, tree);
        renderManager.RenderPlayer(Camera, Player);
        renderManager.RenderHeldItem(Camera, Player, _heldId, _hasHeld,
            (float)_window.Time, Compass.HeadingFromYaw(Camera.Yaw));
        renderManager.EndGpuPass(ProfileSection.EntityPass);
        FrameProfiler.End(ProfileSection.EntityPass);

        // The selection/breaking overlay is world geometry on the targeted block, so it draws with
        // the entities and BEFORE the water pass. Drawn after it, the water's written depth would
        // cull the overlay outright whenever the targeted block sits under the surface while the
        // eye is above it - the crack simply would not appear. Drawing it here lets the water blend
        // and refract over it exactly as it does over the block itself. Guarded rather than an early
        // return, which would skip the passes below.
        if (_hasSelectedBlock)
        {
            FrameProfiler.Begin(ProfileSection.SelectPass);
            renderManager.BeginGpuPass(ProfileSection.SelectPass);

            // While mining, the outline and the break overlay ride along with the popping block
            var popGrow = _isBreaking ? BlockPopGrowNow : 0f;

            // The chunk mesh still holds the block at rest size, so the popped cube is only
            // needed (and only hides it) while it is grown
            if (popGrow > 0f)
                renderManager.RenderBlockPop(Camera, _breakingBlockPosition, _breakingBlockId, popGrow,
                    HitboxesAt(_breakingBlockPosition, _breakingBlockId));

            var selectedId = BlockIdAt(_selectedBlockPosition);
            renderManager.RenderBlockSelection(Camera, _selectedBlockPosition, _selectedFaceNormal, popGrow,
                selectedId, _selectedBoxIndex, HitboxesAt(_selectedBlockPosition, selectedId));

            if (_isBreaking)
                renderManager.RenderBlockBreaking(Camera, _breakingBlockPosition, BreakProgress, popGrow, _breakingBlockId,
                    HitboxesAt(_breakingBlockPosition, _breakingBlockId));

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

        // Last world pass: tonemap the linear HDR scene target into the default framebuffer.
        // The UI draws after this, straight onto that gamma framebuffer.
        FrameProfiler.Begin(ProfileSection.PostPass);
        renderManager.BeginGpuPass(ProfileSection.PostPass);
        renderManager.RenderPostProcess();
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
        renderManager.RenderUI(_window, Inventory, view, time, fps, showProfiler, Camera, showCoordinates, menu, settings,
            _health.Health, _health.DamageFlash);
    }
    
    /// <summary>
    /// Drives the left-click action, attack-first. A slime inside the forward punch cone
    /// (see <see cref="Combat"/>) takes the swing — <c>entityDamage</c> plus a shove — and the
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
    /// Call after Update so the selection is current.
    /// </summary>
    public void UpdateBlockBreaking(double deltaTime, bool isHolding)
    {
        var justPressed = isHolding && !_attackWasPressed;
        _attackWasPressed = isHolding;

        var held = HeldItem();

        // A slice weapon owns the left-click outright: every press swings the blade (and buffers
        // the return while one plays), so it never mines.
        if (held is { Attack: AttackPattern.Slice })
        {
            Camera.NotifyPlayerAction();
            UpdateSlice((float)deltaTime, justPressed);
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
            return;
        }

        // Orbit mode turns the body toward the aim while the action fires at once.
        Camera.NotifyPlayerAction();

        // Attack-first: while a slime is in the punch cone it owns the swing, so no block is
        // mined through it (including while the punch itself is on cooldown).
        if (UpdatePunch(deltaTime))
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
            Player.Punch();
            PlayHitSfx(BlockIdAt(_selectedBlockPosition));
            RemoveBlock(_selectedBlockPosition);
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
            _breakHardness = BlockType.ById[_breakingBlockId]?.Hardness ?? 0f;
            _breakAllowed = Mining.CanMine(_breakingBlockId, held);
            _breakPower = Mining.BlockDamage(held);
            _breakHitSeconds = Mining.HitGapSeconds(held);
            _breakDamage = 0f;
            // Seed the timer a whole interval in so the loop below lands the first hit this
            // frame: a tap then counts as one hit instead of being released before the gap
            // it would have to survive.
            _hitTimer = _breakHitSeconds;
            _popElapsed = BlockPopSeconds;
        }

        // Zero hardness covers two cases: a real block of hardness 0 (pebbles) pops on first
        // contact with no hold-to-mine, while air or an unloaded chunk has nothing to mine
        // at all — the id being a registered block is what tells them apart. A refused tool
        // whiffs instead of popping the block.
        if (_breakHardness <= 0f)
        {
            if (BlockType.ById[_breakingBlockId] != null)
            {
                Player.Punch();
                PlayHitSfx(_breakingBlockId);
                if (_breakAllowed)
                    RemoveBlock(_selectedBlockPosition);
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
            Player.Punch();
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
                _debris.Burst(_breakingBlockPosition, _breakingBlockId, WorldGenerationConfig.BlockDebrisPerHit);
                continue;
            }

            RemoveBlock(_breakingBlockPosition);
            StopBreaking();
            return;
        }
    }

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
    private Item? HeldItem()
    {
        var id = Inventory.SelectedBlockId;
        return id != 0 && Inventory.SelectedCount > 0 ? ItemRegistry.TryGet(id) : null;
    }

    /// <summary>
    /// The punch half of the left-click action. While a slime sits in the forward cone the
    /// swing belongs to it: once the held item's pace has elapsed the nearest one takes
    /// <c>entityDamage</c> and a knockback shove. Returns true when a slime owns the swing (the
    /// caller must not mine). With the cone clear the timer keeps running, so the first slime to
    /// enter is punched at once; and when there is no block targeted either, the fist still
    /// swings on the same pace, so left-clicking the sky plays the animation.
    /// </summary>
    private bool UpdatePunch(double deltaTime)
    {
        var held = HeldItem();
        _punchGap = Mining.HitGapSeconds(held);
        _punchTimer += (float)deltaTime;

        if (TryFindPunchTarget(out var target))
        {
            if (_punchTimer < _punchGap)
                return true;

            _punchTimer = 0f;
            Player.Punch();
            target.TakeDamage(Combat.PunchDamage(held));
            var away = new Vector3(
                target.Position.X - Camera.Position.X, 0f, target.Position.Z - Camera.Position.Z);
            target.Shove(away, WorldGenerationConfig.SlimePunchShoveSpeed);
            return true;
        }

        if (!_hasSelectedBlock && _punchTimer >= _punchGap)
        {
            _punchTimer = 0f;
            Player.Punch();
        }

        return false;
    }

    /// <summary>
    /// Nearest slime inside the forward punch cone with an unobstructed line of sight, or false
    /// when none is in reach. The punch fires from the head along the aim, so a wall between the
    /// player and a slime stops it.
    /// </summary>
    private bool TryFindPunchTarget(out Slime target)
    {
        target = null!;
        if (_slimes == null)
            return false;

        var slimes = _slimes.Slimes;
        if (slimes.Count == 0)
            return false;

        var origin = Camera.Position;
        var direction = Camera.ViewDirection;
        var bestDistance = float.PositiveInfinity;
        Slime? best = null;

        for (var i = 0; i < slimes.Count; i++)
        {
            var slime = slimes[i];
            if (!Combat.InPunchCone(origin, direction, slime.Position, out var distance))
                continue;
            if (Camera.SegmentHitsBlock(origin, slime.Position, _chunkManager))
                continue;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = slime;
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
    /// in the hemisphere then. The swing animation itself lives in <see cref="PlayerModel"/>.
    /// </summary>
    private void UpdateSlice(double deltaTime, bool justPressed)
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
            ApplySliceDamage(_sliceCombo.ReturnSwing);
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
    /// Lands one slice swing: the nearest two slimes inside the forward hemisphere with an
    /// unobstructed line of sight each take the swing's damage and a knockback shove. The return
    /// swing is at half power (<paramref name="returnSwing"/>).
    /// </summary>
    private void ApplySliceDamage(bool returnSwing)
    {
        if (_slimes == null)
            return;

        var slimes = _slimes.Slimes;
        if (slimes.Count == 0)
            return;

        var origin = Camera.Position;
        var direction = Camera.ViewDirection;
        var damage = Combat.SliceDamage(HeldItem(), returnSwing);

        Slime? first = null;
        Slime? second = null;
        var firstDistance = float.PositiveInfinity;
        var secondDistance = float.PositiveInfinity;

        for (var i = 0; i < slimes.Count; i++)
        {
            var slime = slimes[i];
            if (!Combat.InFrontHemisphere(origin, direction, slime.Position, out var distance))
                continue;
            if (Camera.SegmentHitsBlock(origin, slime.Position, _chunkManager))
                continue;

            // Keep the two closest, shifting the old nearest down when a closer one arrives.
            if (distance < firstDistance)
            {
                second = first;
                secondDistance = firstDistance;
                first = slime;
                firstDistance = distance;
            }
            else if (distance < secondDistance)
            {
                second = slime;
                secondDistance = distance;
            }
        }

        StrikeSliceTarget(first, damage);
        StrikeSliceTarget(second, damage);
    }

    private void StrikeSliceTarget(Slime? slime, int damage)
    {
        if (slime == null) return;
        slime.TakeDamage(damage);
        var away = new Vector3(
            slime.Position.X - Camera.Position.X, 0f, slime.Position.Z - Camera.Position.Z);
        slime.Shove(away, WorldGenerationConfig.SlimePunchShoveSpeed);
    }

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
    /// Hitboxes for the outline, break overlay and mining pop. Tapered wood resolves
    /// its drawn shape from the live neighbourhood (see <see cref="WoodShape"/>) and layered
    /// snow from its stack depth (see <see cref="SnowShape"/>); everything else uses its
    /// registered boxes.
    /// </summary>
    private BlockHitbox[] HitboxesAt(Vector3 blockPos, byte blockId)
    {
        var type = BlockType.ById[blockId];
        if (type?.Renderer == BlockRenderer.SnowLayers)
            return SnowShape.GetBoxes((int)blockPos.X, (int)blockPos.Z,
                _chunkManager.GetStateGlobal((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z),
                type.StackMax);
        if (!WoodShape.IsTapered(blockId))
            return type?.Hitboxes ?? [BlockHitbox.FullCube];
        return WoodShape.GetBoxesAt(blockId, _chunkManager.GetBlockGlobal,
            (int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z);
    }

    private void RemoveBlock(Vector3 blockPos)
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

        _debris.Burst(blockPos, brokenId, WorldGenerationConfig.BlockDebrisOnBreak);

        // Creative breaks drop nothing, but trees still fall.
        var creative = Camera.IsCreative;

        // A mined wood block fells whatever it was holding up instead of dropping one
        // block (see FellWood): a trunk cut drops the crown, a branch cut only its children
        if (TreeGenerator.WoodLevel(brokenId) >= 0)
            FellWood(blockPos, brokenId, chunk, creative);
        // Mining any cell of a multiblock takes the whole structure apart instead of leaving
        // invisible part cells standing (see RevertMultiblock)
        else if (Multiblock.IsMultiblockCell(brokenId))
            RevertMultiblock(blockPos, brokenId, chunk, creative);
        // Everything else rolls its loot table, but only once a block was actually cleared
        else if (chunk.RemoveBlockAt(blockPos, _chunkManager))
        {
            if (!creative)
                _dropManager.SpawnLoot(brokenId, blockPos,
                    DropManager.PileCount(brokenId, (int)blockPos.X, (int)blockPos.Z, brokenState),
                    DropManager.PileSecondary(brokenId, (int)blockPos.X, (int)blockPos.Z, brokenState));
            if (BlockType.ById[brokenId]?.Renderer == BlockRenderer.BranchPile)
                _campfires.NotifyBlockRemoved((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z, _dropManager);
            if (BlockType.ById[brokenId]?.Renderer == BlockRenderer.Quern)
                _querns.NotifyBlockRemoved((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z, _dropManager);
            // A mined mold throws out a cast that had already set, even in creative — the mold
            // block itself is what creative withholds, not the metal someone poured into it
            if (ContentRegistry.IsCastMold(brokenId))
                _casting.NotifyBlockRemoved(blockPos, _dropManager);
        }

        // Water beside or above the hole pours into it on the next fluid tick
        _fluidManager.ScheduleAround(blockPos);

        // Keep the buried LOD mesh below the newly exposed hole
        // (blockPos components are floored floats, so the int casts are exact)
        if (LodRegion.LowerColumnBelow((int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z))
            _lodManager.MarkEditedColumn((int)blockPos.X, (int)blockPos.Z);

        // Wood and leaves live above the heightmap caps, so the gate above never fires for
        // them — yet the far LOD draws their canopy. Disturb it and rebuild regardless.
        if (TreeGenerator.IsTreeBlock(brokenId))
        {
            TreeGenerator.PoisonCanopy((int)blockPos.X, (int)blockPos.Z);
            _lodManager.MarkEditedColumn((int)blockPos.X, (int)blockPos.Z);
            _leaves.Burst(blockPos, WorldGenerationConfig.LeafParticleBurstMined);
        }

        // Pebbles and their kind need the block under them: pop whatever sits right above the
        // break. This routes through the normal break path, so it rolls its loot; its own
        // check then no-ops on air, so the recursion always terminates.
        var above = blockPos + Vector3.UnitY;
        if (BlockType.ById[BlockIdAt(above)]?.NeedsSupport == true)
            RemoveBlock(above);

        // Sand and its kind need the block under them too, but they topple into a falling
        // cube instead of popping: a no-op unless a falls block sits right above the break,
        // and its own check chains the rest of the column
        _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
            (int)above.X, (int)above.Y, (int)above.Z);
    }

    /// <summary>
    /// Fells the wood a mined wood block was holding up: collects the severed part (the break
    /// plus its children — a branch cut leaves the trunk standing), clears it in one batch
    /// (same pattern as a TNT blast), and launches a toppling entity that drops its blocks
    /// once it has lain still. Player-placed logs are not trees: they pop out as a plain
    /// drop with no entity and no animation. A lone block with no children just drops as-is.
    /// A creative break (<paramref name="creative"/>) still topples, but drops nothing.
    /// </summary>
    private void FellWood(Vector3 blockPos, byte brokenId, Chunk chunk, bool creative)
    {
        var x = (int)blockPos.X;
        var y = (int)blockPos.Y;
        var z = (int)blockPos.Z;

        // Player-built logs never topple: no entity, no animation, just a drop.
        // RemoveBlockAt remeshes, so unlike the batch paths below nothing lingers.
        if (_chunkManager.IsPlayerPlaced(x, y, z))
        {
            if (chunk.RemoveBlockAt(blockPos, _chunkManager) && !creative)
                _dropManager.SpawnLoot(brokenId, blockPos);
            _fluidManager.ScheduleAround(blockPos);
            return;
        }

        var collected = TopplingTree.Collect(_chunkManager, blockPos, brokenId);
        if (collected.Count < 2)
        {
            // Nothing was held up by this block: drop it as-is through the normal
            // single-block path, which remeshes synchronously.
            if (chunk.RemoveBlockAt(blockPos, _chunkManager) && !creative)
                _dropManager.SpawnLoot(brokenId, blockPos);
            _fluidManager.ScheduleAround(blockPos);
            return;
        }

        var touchedChunks = new HashSet<Vector3>();
        var lowestY = new Dictionary<(int X, int Z), int>();
        foreach (var block in collected)
        {
            var chunkPos = VoxelCoords.ChunkPosOf(block.Cell);
            if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var target)) continue;
            if (target.ClearBlockAt(block.Cell) == 0) continue;

            // A player-built extension felled with the tree stops counting as placed
            _chunkManager.UnmarkPlayerPlaced((int)block.Cell.X, (int)block.Cell.Y, (int)block.Cell.Z);
            touchedChunks.Add(chunkPos);
            _fluidManager.ScheduleAround(block.Cell);

            var key = ((int)block.Cell.X, (int)block.Cell.Z);
            if (!lowestY.TryGetValue(key, out var lowest) || (int)block.Cell.Y < lowest)
                lowestY[key] = (int)block.Cell.Y;
        }

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
        {
            if (LodRegion.LowerColumnBelow(lx, ly, lz))
                _lodManager.MarkEditedColumn(lx, lz);
        }

        // The whole canopy came down above the caps: disturb it once at the break (its reach
        // covers the crown) and rebuild every touched column so no ghost lingers at range. A
        // cactus has no canopy, so it only needs the column rebuild.
        var brokenFamily = BlockType.WoodFamilyOf(brokenId);
        if (brokenFamily == BlockType.TreeWoodFamily || brokenFamily == BlockType.SpruceWoodFamily)
            TreeGenerator.PoisonCanopy((int)blockPos.X, (int)blockPos.Z);
        foreach (var ((lx, lz), _) in lowestY)
            _lodManager.MarkEditedColumn(lx, lz);

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
            blockPos.X + 0.5f - Camera.Position.X, 0f,
            blockPos.Z + 0.5f - Camera.Position.Z);
        _toppling.Add(new TopplingTree(collected,
            new Vector3(blockPos.X + 0.5f, blockPos.Y, blockPos.Z + 0.5f), away, drops: !creative));
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
                hitNormal, Camera.ViewDirection, out var anchor, out var facing))
            return false;

        // A live falling cube owns its cell: never bury one inside the structure
        foreach (var (dx, dy, dz) in Multiblock.Offsets)
        {
            if (_falling.OverlapsAny((int)anchor.X + dx, (int)anchor.Y + dy, (int)anchor.Z + dz))
                return false;
        }

        var touchedChunks = new HashSet<Vector3>();
        Multiblock.Convert(_chunkManager, anchor, facing, anchorType, touchedChunks);

        _chunkManager.ImmediateRemeshWithBorders(VoxelCoords.ChunkPosOf(hitCell), hitCell);
        foreach (var chunkPos in touchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        OnMultiblockConverted(anchor, facing, anchorType);
        return true;
    }

    /// <summary>
    /// Mining any cell of a multiblock takes the whole structure down: the other seven cells go
    /// back to the source block in one batch and the mined cell rolls the structure's loot. An
    /// orphaned cell (its anchor already blasted away) just pops on its own.
    /// </summary>
    private void RevertMultiblock(Vector3 blockPos, byte brokenId, Chunk chunk, bool creative)
    {
        if (!Multiblock.TryResolveAnchor(_chunkManager, (int)blockPos.X, (int)blockPos.Y, (int)blockPos.Z,
                out var anchor, out _, out var anchorType) || anchorType == null)
        {
            if (chunk.RemoveBlockAt(blockPos, _chunkManager) && !creative)
                _dropManager.SpawnLoot(brokenId, blockPos);
            return;
        }

        var touchedChunks = new HashSet<Vector3>();
        Multiblock.Revert(_chunkManager, anchor, anchorType, blockPos, touchedChunks);

        _chunkManager.ImmediateRemeshWithBorders(VoxelCoords.ChunkPosOf(blockPos), blockPos);
        foreach (var chunkPos in touchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(chunkPos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        if (!creative)
            _dropManager.SpawnLoot(brokenId, blockPos);

        OnMultiblockBroken(anchor, anchorType, blockPos);
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
        if (anchorType.Kiln)
            _kilns.NotifyBlockRemoved(anchor, blockPos, _dropManager);
    }

    /// <summary>Swings falling trees, then pops their wood out as item drops once rested.</summary>
    private void UpdateToppling(float deltaTime)
    {
        for (var i = _toppling.Count - 1; i >= 0; i--)
        {
            var tree = _toppling[i];
            tree.Update(deltaTime);
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
    /// </summary>
    public void ThrowSelected(bool wholeStack = false)
    {
        var forward = Camera.ViewDirection;

        // A held tool is one instance: it leaves the pool and every hotbar slot holding it
        if (Inventory.SelectedToolInstance is var instance and > 0)
        {
            var id = Inventory.SelectedBlockId;
            if (id == 0) return;
            if (!Inventory.RemoveTool(instance)) return;
            _dropManager.SpawnAt(id, Camera.Position + forward * 0.6f, forward * 4f + Vector3.UnitY * 1.5f);
            return;
        }

        var stackId = Inventory.SelectedBlockId;
        if (stackId == 0 || Inventory.SelectedCount <= 0) return;

        var units = wholeStack ? Inventory.SelectedCount : 1;
        for (var i = 0; i < units; i++)
        {
            // Spend first so a failed spend never duplicates the drop
            if (!Inventory.ConsumeSelected()) break;

            _dropManager.SpawnAt(stackId, Camera.Position + forward * 0.6f, forward * 4f + Vector3.UnitY * 1.5f);
        }
    }

    /// <summary>
    /// Right-click: runs the first thing that claims the click — a campfire, quern, kiln or cast
    /// mold interaction, then the selected slot's own <see cref="Item.OnUse"/> (a bucket scoops
    /// or pours, flint grounds a pile, a firestarter strikes), otherwise places the selected
    /// block. A successful tool use morphs the instance in place, so every hotbar slot holding
    /// that bucket flips with it and scooping and pouring alternate on one button. Returns
    /// whether the click was claimed, so a plain throwable only starts charging when nothing
    /// here owned it — a mudball aimed at a lit campfire cooks instead of being thrown.
    /// </summary>
    public bool TryUseSelected(float maxDistance)
    {
        if (_throwCharging) return false;
        Camera.NotifyPlayerAction();
        if (TryCookOrCollect(maxDistance))
        {
            Player.Punch();
            return true;
        }
        if (TryQuernInteract(maxDistance))
        {
            Player.Punch();
            return true;
        }
        if (TryKilnInteract(maxDistance))
        {
            Player.Punch();
            return true;
        }
        if (TryCastInteract(maxDistance))
        {
            Player.Punch();
            return true;
        }
        var id = Inventory.SelectedBlockId;
        var item = id != 0 && Inventory.SelectedCount > 0 ? ItemRegistry.TryGet(id) : null;
        if (item == null || item is BlockItem || item.UseAction == ItemUseAction.None)
            return TryPlaceSelectedBlock(maxDistance);
        return DispatchUse(item, maxDistance);
    }

    public bool TryBeginThrowCharge()
    {
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

    public void UpdateThrowCharge(float deltaTime)
    {
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

    public void CancelThrowCharge()
    {
        _throwCharging = false;
        Player.CancelThrowCharge();
    }

    public bool ReleaseThrow()
    {
        if (!_throwCharging) return false;
        _throwCharging = false;
        var charge01 = MathUtils.Clamp01(_throwChargeSeconds / WorldGenerationConfig.ThrowChargeSeconds);
        var id = _throwItemId;
        if (id == 0 || Inventory.SelectedBlockId != id || Inventory.SelectedCount <= 0)
        {
            Player.CancelThrowCharge();
            return false;
        }
        if (!Camera.IsCreative && !Inventory.ConsumeSelected())
        {
            Player.CancelThrowCharge();
            return false;
        }
        var origin = Camera.Position + Camera.ViewDirection * 0.6f;
        var velocity = ThrownProjectile.LaunchVelocity(Camera.ViewDirection, charge01);
        _thrown.Add(new ThrownProjectile(id, origin, velocity, _throwBaseDamage, charge01));
        Camera.NotifyPlayerAction();
        Player.ReleaseThrow(charge01);
        return true;
    }

    private void UpdateThrown(float deltaTime)
    {
        if (_thrown.Count == 0) return;
        var slimes = _slimes?.Slimes ?? (IReadOnlyList<Slime>)Array.Empty<Slime>();
        for (var i = _thrown.Count - 1; i >= 0; i--)
        {
            var projectile = _thrown[i];
            var alive = projectile.Update(deltaTime, _chunkManager, slimes, out var hit);
            if (hit != null)
            {
                hit.TakeDamage(projectile.Damage);
                var away = new Vector3(
                    hit.Position.X - Camera.Position.X, 0f, hit.Position.Z - Camera.Position.Z);
                hit.Shove(away, WorldGenerationConfig.SlimePunchShoveSpeed);
                _thrown.RemoveAt(i);
                continue;
            }
            if (!alive)
            {
                _dropManager.SpawnAt(projectile.ItemId, projectile.Position);
                _thrown.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Runs one item's <see cref="Item.OnUse"/> and applies its outcome: morph the tool instance
    /// in place (only when the use took effect) and punch. Durability, cooldowns and stack
    /// spending already happened inside the override. Returns whether the use took effect.
    /// </summary>
    private bool DispatchUse(Item item, float maxDistance)
    {
        var instance = Inventory.SelectedToolInstance;
        _activeToolInstance = instance;
        try
        {
            var stack = instance > 0 ? Inventory.GetToolStack(instance) : null;
            var result = item.OnUse(new ItemUseContext(this, maxDistance, instance), stack);
            if (result.Applied && result.MorphToId.HasValue && instance > 0)
                Inventory.MorphTool(instance, result.MorphToId.Value);
            if (result.Punch)
                Player.Punch();
            return result.Applied;
        }
        finally
        {
            _activeToolInstance = 0;
        }
    }

    #region IItemUseWorld
    // One implementation for every Item override: queries read the live managers, effects write
    // them, and the inventory tail runs through the dispatcher's active tool instance.

    double IItemUseWorld.NowSeconds => _window.Time;

    bool IItemUseWorld.IsCreative => Camera.IsCreative;

    bool IItemUseWorld.RaycastFluidSource(float maxDistance, out Vector3 hit) =>
        Camera.Raycast(maxDistance, _chunkManager, out hit, out _, includeFluids: true);

    bool IItemUseWorld.RaycastSolid(float maxDistance, out Vector3 hit, out Vector3 normal) =>
        Camera.Raycast(maxDistance, _chunkManager, out hit, out normal);

    bool IItemUseWorld.TryGetCell(int x, int y, int z, out byte id, out byte state) =>
        _chunkManager.TryGetCell(x, y, z, out id, out state);

    byte IItemUseWorld.GetBlockAt(Vector3 pos) => BlockIdAt(pos);

    bool IItemUseWorld.ScoopFluidCell(Vector3 pos) =>
        _fluidManager.SetCellImmediate(pos, 0, 0, _chunkManager);

    bool IItemUseWorld.PourFluidCell(Vector3 pos, byte fluidId) =>
        _fluidManager.SetCellImmediate(pos, fluidId, FluidState.Source, _chunkManager);

    bool IItemUseWorld.PlaceBlockAtFace(Vector3 hit, Vector3 normal, byte blockId, byte state,
        out Vector3 placedCell) =>
        PlaceBlockAtFace(hit, normal, blockId, state, out placedCell);

    void IItemUseWorld.AfterPlaced(Vector3 pos) =>
        _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
            (int)pos.X, (int)pos.Y, (int)pos.Z);

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
        return chunk != null && chunk.SetStateAt(pos, count, _chunkManager);
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
        return chunk.SetStateAt(pos, state, _chunkManager);
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

    bool IItemUseWorld.RemoveCellForPrime(Vector3 cell) =>
        _chunkManager.ChunkAt((int)cell.X, (int)cell.Y, (int)cell.Z)?.RemoveBlockAt(cell, _chunkManager) == true;

    void IItemUseWorld.SpawnPrimedTnt(Vector3 cell, byte blockId) =>
        _tntManager.Spawn(cell, blockId, TntManager.PlayerFuseSeconds);

    void IItemUseWorld.ScheduleFluidAround(Vector3 cell) => _fluidManager.ScheduleAround(cell);

    void IItemUseWorld.MarkLodColumnEdited(int x, int y, int z)
    {
        if (LodRegion.LowerColumnBelow(x, y, z))
            _lodManager.MarkEditedColumn(x, z);
    }

    bool IItemUseWorld.ToolReady() =>
        _activeToolInstance > 0 && Inventory.ToolReadyAt(_activeToolInstance, _window.Time);

    void IItemUseWorld.MarkToolUsed(float delaySeconds)
    {
        if (_activeToolInstance > 0)
            Inventory.MarkToolUsed(_activeToolInstance, _window.Time, delaySeconds);
    }

    bool IItemUseWorld.DamageTool(int amount) =>
        _activeToolInstance > 0 && Inventory.DamageTool(_activeToolInstance, amount);

    void IItemUseWorld.ConsumeSelected() => Inventory.ConsumeSelected();
    #endregion

    private bool TryCookOrCollect(float maxDistance)
    {
        if (!Camera.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (BlockType.ById[hitId]?.Renderer != BlockRenderer.BranchPile) return false;
        if (!_campfires.IsLit(hx, hy, hz)) return false;

        var selected = Inventory.SelectedBlockId;
        if (selected != 0 && Inventory.SelectedCount > 0
            && ContentRegistry.CookRecipeForInput(selected) != null
            && _campfires.TryPlaceFood(hx, hy, hz, selected))
        {
            if (!Camera.IsCreative)
                Inventory.ConsumeSelected();
            return true;
        }

        var finished = _campfires.CollectFinished(hx, hy, hz);
        if (finished.Count == 0) return selected != 0 && ContentRegistry.CookRecipeForInput(selected) != null;
        foreach (var output in finished)
        {
            if (!Inventory.Add(output))
                _dropManager.Spawn(output, new Vector3(hx, hy, hz));
        }
        return true;
    }

    /// <summary>
    /// Right-click on a quern: holding a crushable inserts one unit into an empty quern,
    /// anything else spins the plate through one full rotation (a quern already turning
    /// swallows the click). Returns whether a quern was aimed at.
    /// </summary>
    private bool TryQuernInteract(float maxDistance)
    {
        if (!Camera.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (BlockType.ById[hitId]?.Renderer != BlockRenderer.Quern) return false;

        if (_querns.IsSpinning(hx, hy, hz)) return true;

        var selected = Inventory.SelectedBlockId;
        if (selected != 0 && Inventory.SelectedCount > 0
            && ContentRegistry.CrushRecipeForInput(selected) != null
            && _querns.TryInsert(hx, hy, hz, selected))
        {
            if (!Camera.IsCreative)
                Inventory.ConsumeSelected();
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
    private bool TryKilnInteract(float maxDistance)
    {
        if (!Camera.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
        var hx = (int)hit.X;
        var hy = (int)hit.Y;
        var hz = (int)hit.Z;
        if (!_chunkManager.TryGetCell(hx, hy, hz, out var hitId, out _)) return false;
        if (!Multiblock.IsMultiblockCell(hitId)) return false;
        if (!TryTrackKilnAt(hx, hy, hz, out var anchor)) return false;

        var held = HeldItem();

        // The firestarter lights the kiln from its own OnUse, which also spends its durability
        if (held is { UseAction: ItemUseAction.IgniteCampfire }) return false;

        // An empty hand lifts the crucible out; a full tools pool puts it straight back
        if (held == null)
        {
            if (_kilns.TryTakeCrucible(anchor, out var takenId, out var takenContents)
                && CrucibleInventory.Add(Inventory, takenId, takenContents) == null)
                _kilns.TryPlaceCrucible(anchor, takenId, takenContents);
            return true;
        }

        // A crucible in hand stands in the hearth, carrying whatever that instance held
        if (held is CrucibleItem crucible)
        {
            var instance = Inventory.SelectedToolInstance;
            var stack = instance > 0 ? Inventory.GetToolStack(instance) : null;
            if (stack != null && _kilns.TryPlaceCrucible(anchor, crucible.Id, stack.Get<CrucibleComponent>()))
                Inventory.RemoveTool(instance);
            return true;
        }

        // Ore (or anything else that melts) charges the crucible standing inside
        if (ContentRegistry.MeltRecipeForInput(held.Id) != null && _kilns.TryInsertMelt(anchor, held.Id))
        {
            if (!Camera.IsCreative)
                Inventory.ConsumeSelected();
            return true;
        }

        if (held is { FuelSeconds: > 0f } && _kilns.TryAddFuel(anchor, held))
        {
            if (!Camera.IsCreative)
                Inventory.ConsumeSelected();
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
    private bool TryCastInteract(float maxDistance)
    {
        if (!Camera.Raycast(maxDistance, _chunkManager, out var hit, out _)) return false;
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
                if (!Inventory.Add(id))
                    _dropManager.Spawn(id, cell);
            return true;
        }

        if (HeldItem() is CrucibleItem)
        {
            var instance = Inventory.SelectedToolInstance;
            var stack = instance > 0 ? Inventory.GetToolStack(instance) : null;
            if (stack != null)
                _casting.TryStartPour(cell, hitId, stack);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Places the hotbar's selected block against the face the player is looking at and spends
    /// one from that slot — except in creative mode, where placing is free. Does nothing
    /// when the slot is empty or holds no blocks. Returns whether one was placed.
    /// </summary>
    public bool TryPlaceSelectedBlock(float maxDistance)
    {
        if (!Inventory.CanPlaceSelected) return false;

        // Only spend a block if one actually went down (and never in creative)
        if (!TryPlaceBlockFromRaycast(maxDistance, Inventory.SelectedBlockId)) return false;
        if (!Camera.IsCreative)
            Inventory.ConsumeSelected();
        return true;
    }

    public bool TryPickTargetedBlock()
    {
        if (!_hasSelectedBlock) return false;
        var id = BlockIdAt(_selectedBlockPosition);
        if (id == 0) return false;
        return Inventory.PickBlock(id);
    }

    /// <summary>Places a block against the targeted face. Returns whether one was placed.</summary>
    public bool TryPlaceBlockFromRaycast(float maxDistance, byte blockId)
    {
        if (blockId == 0) return false;

        if (!Camera.Raycast(maxDistance, _chunkManager, out var hitPos, out var hitNormal))
            return false;

        if (!PlaceBlockAtFace(hitPos, hitNormal, blockId, state: 0, out var placePos)) return false;

        Player.Punch();
        // Sand placed into thin air falls at once instead of hovering (stackable piles never
        // fall — the registry forbids the combination — so only the block path topples)
        _falling.TryTopple(_chunkManager, _lodManager, _fluidManager,
            (int)placePos.X, (int)placePos.Y, (int)placePos.Z);
        return true;
    }

    /// <summary>
    /// Guards and grounds one block against a raycast hit face: live cubes and the player's
    /// body refuse solid entries, fragile blocks need solid ground, and a placement into
    /// water displaces it. The filled cell is the neighbour across the hit face, or the hit
    /// cell itself when it holds a stack a placement may take over (layered snow), which it
    /// overwrites in place. <paramref name="state"/> rides into the state byte (a fresh rock
    /// pile starts at 1) and <paramref name="placedCell"/> reports the cell that was filled.
    /// Returns whether one was placed, without spending anything.
    /// </summary>
    private bool PlaceBlockAtFace(Vector3 hitPos, Vector3 hitNormal, byte blockId, byte state,
        out Vector3 placedCell)
    {
        placedCell = Placement.TargetCell(hitPos, hitNormal, BlockIdAt(hitPos),
            _chunkManager.GetStateGlobal((int)hitPos.X, (int)hitPos.Y, (int)hitPos.Z));
        var placePos = placedCell;
        var chunkPos = VoxelCoords.ChunkPosOf(placePos);

        if (!_chunkManager.Chunks.TryGetValue(chunkPos, out var chunk)) return false;

        // A live cube owns its cell until it lands: placing into one would cement the cube
        // inside the new block, so the placement fails and the caller keeps its item
        if (_falling.OverlapsAny((int)placePos.X, (int)placePos.Y, (int)placePos.Z))
            return false;

        // A solid block inside the player's body would trap them (or force the physics to
        // shove them out); non-solid blocks are fine to stand in. Flying is noclip but the
        // placement is still refused, so dropping out of flight never leaves the player buried.
        if (BlockType.IsSolid(blockId) && Camera.OverlapsPlayer(placePos))
            return false;

        // Fragile blocks need solid ground: a flower planted into mid-air would hover
        // until anything remeshed it, so the placement fails and the caller keeps its item
        if (BlockType.ById[blockId]?.NeedsSupport == true)
        {
            var below = placePos - Vector3.UnitY;
            if (!BlockType.IsSolid(BlockIdAt(below))) return false;
        }

        if (!chunk.PlaceBlockAt(placePos, blockId, _chunkManager, state)) return false;

        // The chunk only meshes a quern's base: register the cell so its plate draws at
        // rest until it is inserted into or spun.
        if (BlockType.ById[blockId]?.Renderer == BlockRenderer.Quern)
            _querns.Ensure((int)placePos.X, (int)placePos.Y, (int)placePos.Z);

        // A block placed into water displaces it, so the neighbours recompute their levels
        _fluidManager.ScheduleAround(placePos);
        return true;
    }
    
    public ChunkManager GetChunkManager() => _chunkManager;
}
