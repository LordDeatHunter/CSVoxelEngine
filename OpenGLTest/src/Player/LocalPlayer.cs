using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Entities;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Saves;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.Menus;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.Player;

/// <summary>
/// The local player's survival and body: the spawn settle, health, death and respawn, the body
/// model and its secondary motion, the hurt and punch feedback, and the apply of a saved player.
/// Owned by <see cref="World"/>, which builds it before its chunk manager exists, so the methods
/// that read chunks take the manager per call, as the camera's update does; main thread only.
/// </summary>
internal sealed class LocalPlayer
{
    private readonly World _world;
    private readonly PlayerHealth _health;
    private readonly GroundDust _dust;
    private readonly BlockDebrisSystem _debris;
    private readonly List<Mob> _mobs;
    private readonly bool _serverMode;
    private readonly bool _clientMode;

    private Camera Camera => _world.Camera;
    private PlayerModel Player => _world.Player;
    private Inventory Inventory => _world.Inventory;
    private HudToast Toast => _world.Toast;
    private double LightingTime => _world.LightingTime;

    internal LocalPlayer(World world, PlayerHealth health, GroundDust dust, BlockDebrisSystem debris, List<Mob> mobs,
        bool serverMode, bool clientMode)
    {
        _world = world;
        _health = health;
        _dust = dust;
        _debris = debris;
        _mobs = mobs;
        _serverMode = serverMode;
        _clientMode = clientMode;
        _localContext = new PlayerContext(Inventory) { Camera = Camera, Health = _health };
    }

    /// <summary>Whether the player rests on real ground at the spawn (or a saved pose placed it).</summary>
    internal bool SpawnSettled => _spawnSettled;

    /// <summary>Whether a respawn eye is known (restored from the save or taken once settled).</summary>
    internal bool HasRespawnEye => _hasRespawnEye;

    /// <summary>Where a death respawns the player, valid while <see cref="HasRespawnEye"/>.</summary>
    internal Vector3 RespawnEye => _respawnEye;

    /// <summary>The item in hand this frame, as the body model last read it.</summary>
    internal ushort HeldId => _heldId;

    /// <summary>Whether the hand holds anything this frame (a stack or a tool instance).</summary>
    internal bool HasHeld => _hasHeld;

    /// <summary>
    /// Records the spawn column from the camera's starting position; a <c>VOXEL_SPAWN</c> override
    /// counts as settled at once.
    /// </summary>
    internal void RecordSpawn()
    {
        _spawnX = Camera.Position.X;
        _spawnZ = Camera.Position.Z;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOXEL_SPAWN")))
            _spawnSettled = true;
    }

    /// <summary>Marks the spawn settled (a client starts at its server's spawn).</summary>
    internal void MarkSpawnSettled() => _spawnSettled = true;

    /// <summary>Forgets the fall in progress, so a teleport (a waystone arrival) lands as a teleport, never a drop.</summary>
    internal void ResetFall(float feet) => _fall.Reset(feet, grounded: false);

    /// <summary>Forgets the walk cycle's previous position, so a whole load never reads as one step.</summary>
    internal void ForgetPreloadPosition() => _hasPrevCamPos = false;

    private float _contactTimer;

    private Vector3 _prevCamPos;
    private bool _hasPrevCamPos;
    private ushort _heldId;
    private bool _hasHeld;

    // The local player as a PlayerContext: this world's own inventory, aimed by its camera.
    // Refreshed from the camera by LocalContext() before every use, so it never allocates.
    private readonly PlayerContext _localContext;

    // True once the player rests exactly on real ground at the origin spawn. The camera's
    // initial position is only a heightmap guess (pre-carve, see SpawnOverrides.SpawnPosition), and
    // physics starts before the spawn chunks stream in (unloaded cells read as air), so
    // without this the player falls through and ends up buried once the terrain loads.
    private bool _spawnSettled;
    private float _spawnX;
    private float _spawnZ;

    // The sun's elevation last frame, for the dawn watch behind the day toast
    private float _lastSunElevation;
    private bool _sunElevationKnown;

    /// <summary>The death line posted on the first dead frame, waiting for the UI to drain it into
    /// the chat (<see cref="TakeDeathMessage"/>); null while alive.</summary>
    private string? _pendingDeathMessage;

    /// <summary>
    /// Takes the pending death message (see <see cref="DeathMessages"/>) once: the UI shows it as a
    /// system chat line. Null when nobody died since the last call.
    /// </summary>
    internal string? TakeDeathMessage()
    {
        var message = _pendingDeathMessage;
        _pendingDeathMessage = null;
        return message;
    }
    private readonly FallTracker _fall = new();
    private Vector3 _respawnEye;
    private bool _hasRespawnEye;

    /// <summary>
    /// Applies a parsed <see cref="PlayerSaveCodec.FileName"/> field by field: a saved position
    /// restores the pose and marks the spawn settled (a restored player is never relocated to the
    /// surface), and without one the modes still apply at the current pose. The respawn eye is
    /// restored when present, and the inventory, health and mana through
    /// <see cref="SavedPlayerPools.Apply"/> (known parts before mana). The lighting clock waits for the
    /// Loading -> Playing flip (<see cref="World.FinishLoading"/>).
    /// </summary>
    internal void ApplySavedPlayer(PlayerSaveData saved)
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

        SavedPlayerPools.Apply(saved, Inventory, _health);
    }

    // Standing against a cactus pricks: one tick per ContactDamage.TickSeconds while the body box
    // touches one; leaving it (or going creative/flying) clears the partial tick. Singleplayer
    // only: a client mirrors the server's pool, and a server's players tick their own
    // (ServerPlayer.TickStatus).
    private void UpdateCactusContact(float deltaTime, ChunkManager chunkManager)
    {
        if (_serverMode || _clientMode) return;
        ContactDamage.Touches(chunkManager, Camera.Position - new Vector3(0f, Camera.EyeHeight, 0f),
            PlayerPhysics.PlayerRadius, PlayerPhysics.PlayerHeight, out var damage);
        var tick = ContactDamage.Tick(ref _contactTimer, deltaTime, (int)damage,
            !(Camera.IsCreative || Camera.IsFlying));
        if (tick > 0)
            _health.TakeDamage(tick, DamageSource.Cactus);
    }

    /// <summary>
    /// Applies what the worn gear changes to this player: the health pool (every mode, so a client's hearts
    /// scale), the mana pool where it is simulated (<paramref name="simulatesMana"/>; a client mirrors its server's),
    /// the walk speed and the air jump. Called every frame, cheap: the stats and abilities are cached until the
    /// worn set changes.
    /// </summary>
    internal void ApplyGear(bool simulatesMana)
    {
        _health.ApplyGear(Inventory.Equipment, simulatesMana);
        Camera.GearMoveFactor = Inventory.Equipment.Stats.MoveFactor;
        Camera.GearAirJump = Inventory.Equipment.Has(GearAbility.AirJump);
    }

    internal void EnsureSpawnSettled(ChunkManager chunkManager)
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
        var column = chunkManager.WorldSource.GetColumn(cx, cz);
        var columnIndex = VoxelCoords.ColumnIndex(lx, lz);
        var heightmap = column.Heights[columnIndex];
        var estimate = Math.Max(heightmap, WorldGenerationConfig.SeaLevel);
        const float spawnLift = 0.02f;
        var holdEye = estimate + 1 + Camera.EyeHeight + spawnLift;

        if (heightmap < column.WaterLevel[columnIndex])
        {
            if (!chunkManager.TryGetCell(sx, WorldGenerationConfig.SeaLevel, sz, out _, out _))
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
        if (!chunkManager.TryGetCell(sx, top, sz, out _, out _))
        {
            Camera.TestTeleport(new Vector3(_spawnX, holdEye, _spawnZ), Camera.Yaw, Camera.Pitch);
            return;
        }

        for (var y = top; y > estimate - 64; y--)
        {
            if (!chunkManager.TryGetCell(sx, y, sz, out var id, out _))
            {
                Camera.TestTeleport(new Vector3(_spawnX, holdEye, _spawnZ), Camera.Yaw, Camera.Pitch);
                return;
            }

            if (!BlockType.IsSolid(id))
                continue;

            if (!chunkManager.TryGetCell(sx, y + 1, sz, out var above1, out _)
                || !chunkManager.TryGetCell(sx, y + 2, sz, out var above2, out _))
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

    /// <summary>The local player as a <see cref="PlayerContext"/>, refreshed from the camera.</summary>
    internal PlayerContext LocalContext()
    {
        _localContext.Eye = Camera.Position;
        _localContext.Forward = Camera.ViewDirection;
        _localContext.ViewDirection = Camera.ViewDirection;
        _localContext.IsCreative = Camera.IsCreative;
        return _localContext;
    }

    /// <summary>
    /// The local player's body for this frame: the held item, and the walk cycle driven by real
    /// horizontal speed so the limbs keep up with sprint and swim instead of playing a fixed-rate
    /// loop. Flying poses no stride, so it feeds zero speed and the limbs ease back to rest. The
    /// punch, slice and throw animations run inside it.
    /// </summary>
    internal void UpdatePlayerModel(double deltaTime, ChunkManager chunkManager)
    {
        var camPos = Camera.Position;
        var horizontalSpeed = 0f;
        var headingForward = 0f;
        var headingSide = 0f;
        var totalSpeed = 0f;
        if (_hasPrevCamPos && deltaTime > 0.0)
        {
            var step = camPos - _prevCamPos;
            horizontalSpeed = new Vector3(step.X, 0f, step.Z).Length() / (float)deltaTime;
            totalSpeed = step.Length() / (float)deltaTime;
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
            headingForward, headingSide, floating: waterPhase == WaterPhase.Float,
            flying: Camera.IsFlying, flightSpeed: totalSpeed);
        // A hit-stop holds the punch pinch with the arm (read by the camera's next update)
        Camera.HoldPunchFov = Player.IsHitStopped;
        // The throw being wound up narrows the view with the arm (ThrowZoom, the camera's next update)
        // (a drawn bow narrows it the same way)
        Camera.ThrowCharge = Player.ThrowCharge ?? Player.BowCharge;
        // The dust a landing or a sprinting footfall kicks up: cosmetic, so a headless server spawns none
        if (!_serverMode)
            _dust.UpdatePlayer(Camera, Player.WalkPhase, horizontalSpeed, chunkManager, _debris);
    }

    private int _lastSwimStrokes;

    private bool _equipTracked;

    /// <summary>Scenario seam: takes health off the local player's pool (the hearts, the damage flash
    /// and the low-health vignette all follow it), so a script can film the low-health HUD.</summary>
    internal void DamagePlayer(int amount) => _health.TakeDamage(amount);

    /// <summary>Scenario seam: makes the player's model play one reaction or swing, so a script can film it.</summary>
    internal void TestPlayerAction(string action)
    {
        switch (action)
        {
            case "hurt": NotifyPlayerHurt(fromMob: false); break;
            case "punch": PunchSwing(); break;
            case "slice": Player.StartSlice(1); break;
            case "slicereturn": Player.StartSlice(-1); break;
            case "cut": Player.StartSword(SwordSwing.Cut); break;
            case "backcut": Player.StartSword(SwordSwing.BackCut); break;
            case "chop": Player.StartSword(SwordSwing.Chop); break;
            case "axechop": Player.StartAxeChop(AxeChopSeconds(Mining.BlockHitGapSeconds(ItemRegistry.TryGet(Inventory.SelectedBlockId)))); break;
            case "equip": Player.NotifyEquip(); break;
            case "charge": Player.StartThrowCharge(); Player.UpdateThrowCharge(1f); break;
            case "release": Player.ReleaseThrow(1f); break;
            // A bow drawn to full and held, then let go (the world drives the same through RightClickAction)
            case "draw": Player.StartUseCharge(UsePose.Bow); Player.UpdateUseCharge(1f); break;
            case "loose": Player.CancelUseCharge(); break;
            // The staff cast's gesture only (the scenario's cast op fires a real bolt)
            case "cast": CastSwing(); break;
            // Launches the selected throwable at full charge (the model plays its own release separately)
            case "throw": _world.Throws.ThrowFor(LocalContext(), 1f); Player.ReleaseThrow(1f); break;
            case "die": Player.DeathFall = 1f; Camera.DeathProgress = 1f; break;
            case "revive": Player.DeathFall = 0f; Camera.DeathProgress = 0f; break;
        }
    }

    /// <summary>How long an axe's chop lasts for a hit gap of <paramref name="hitGapSeconds"/>: the gap
    /// itself, held between the shortest swing that still reads and the longest that stays crisp.</summary>
    internal static float AxeChopSeconds(float hitGapSeconds) =>
        Math.Clamp(hitGapSeconds, PlayerConfig.AxeChopMinSeconds, PlayerConfig.AxeChopMaxSeconds);

    // A swing of the fist: the arm throws the jab and the view pinches in for a beat. Returns the
    // swing's weight, which a connecting punch's hit-stop reads.
    internal float PunchSwing(bool chopStarted = false)
    {
        // A slower tool swings heavier: from the bare hand's pace (no weight) up to a tool with
        // a long hit gap.
        // A slowing status effect lengthens the swing's pace (an axe chop takes longer) but not its
        // weight, which stays the tool's own.
        var held = ItemRegistry.TryGet(Inventory.SelectedBlockId);
        var toolGap = Mining.BlockHitGapSeconds(held);
        var gap = Mining.BlockHitGapSeconds(held,
            (Camera.Effects?.AttackFactor ?? 1f) * Inventory.Equipment.Stats.MiningFactor);
        var weight = MathUtils.InverseLerp(Mining.BlockHitGapSeconds(null), Mining.BlockHitGapSeconds(null) + 0.5f, toolGap);
        // An axe chops: one swing paced to its hits (the click loop starts it early, so the blow comes
        // down as the hit lands, and says so with chopStarted)
        if (Mining.IsAxe(held))
        {
            if (!chopStarted) Player.StartAxeChop(AxeChopSeconds(gap));
        }
        else
        {
            Player.Punch(weight);
        }
        Camera.PunchFov(-0.8f - 0.8f * weight);
        return weight;
    }

    /// <summary>The staff cast's gesture: the staff raised and thrust forward, with a light view pinch.</summary>
    internal void CastSwing()
    {
        Player.Cast();
        Camera.PunchFov(SpellConfig.CastFovKickDegrees);
    }

    /// <summary>
    /// Plays the player's hurt feedback: the torso flinches away from the blow and the view rolls
    /// and dips. <paramref name="fromMob"/> finds the nearest living mob and reads the blow's
    /// direction off it; a fall or lava burn has none and just rocks the body back.
    /// </summary>
    internal void NotifyPlayerHurt(bool fromMob)
    {
        if (!fromMob)
        {
            Player.Hurt(0f);
            Camera.AddHitKick(Vector3.Zero, 0.5f);
            return;
        }

        Mob? nearest = null;
        var best = 8f * 8f;
        foreach (var mob in _mobs)
        {
            var d = Vector3.DistanceSquared(mob.Position, Camera.Position);
            if (d >= best) continue;
            best = d;
            nearest = mob;
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

    /// <summary>The death screen's state: the death plays, then the player stays down until they ask to respawn.</summary>
    internal RespawnPrompt RespawnPrompt { get; } = new();

    /// <summary>The line the death screen shows ("Player fell from a high place"); null while alive.</summary>
    internal string? DeathLine { get; private set; }

    // The eye's cave biome probe, memoized per cell (singleplayer only)
    private readonly EyeCaveBiomeProbe _caveProbe = new();

    /// <summary>The cave biome at the eye (CB-3): computed in singleplayer by <see cref="UpdateHealth"/>,
    /// mirrored from the server's status on a client (<see cref="MirrorCaveBiome"/>), plain otherwise.</summary>
    internal CaveBiome EyeCaveBiome { get; private set; }

    /// <summary>A client takes the eye's cave biome the server's status reports.</summary>
    internal void MirrorCaveBiome(CaveBiome biome) => EyeCaveBiome = biome;

    /// <summary>
    /// First-cut survival tick, after <see cref="Camera.Update"/> so grounded state is fresh:
    /// tracks the fall from its highest feet position, burns in lava, decays the damage
    /// flash, and, once the death screen's RESPAWN is chosen, respawns at the respawn eye keeping
    /// the inventory. A multi-block jump in one frame
    /// is a teleport (spawn settle, scripts), never a fall. Landing in water cushions the
    /// fall; creative and flight skip every damage source.
    /// </summary>
    internal void UpdateHealth(float deltaTime, ChunkManager chunkManager)
    {
        ApplyGear(simulatesMana: true);
        // The eye's cave biome (CB-3), even while dead. UpdateHealth never runs on a client or a server, so no
        // worldgen is read there: a client mirrors the byte from its status
        EyeCaveBiome = _caveProbe.Update(chunkManager.WorldSource, Camera.Position);
        if (_spawnSettled && !_hasRespawnEye)
        {
            _respawnEye = Camera.Position;
            _hasRespawnEye = true;
        }

        var beforeHp = _health.Health;
        var feet = Camera.Position.Y - Camera.EyeHeight;
        // A bounce block (PlayerPhysics sprang the body back up) or a soft-landing block under the
        // feet cushioned the landing: it still counts as a landing, but bills no damage
        var damage = _fall.Observe(feet, Camera.IsGrounded, Camera.IsFlying, Camera.IsInWater, Camera.IsCreative,
            FallTracker.DefaultMaxStepBlocks, cushioned: PlayerBodyProbe.LandingCushioned(Camera.Position, chunkManager));
        // Worn no_fall_damage gear negates the blow inside PlayerHealth.TakeDamage; the landing still counts
        if (damage > 0)
            _health.TakeDamage(damage, DamageSource.Fall);

        _health.UpdateLava(deltaTime, Camera.IsInLava, Camera.IsCreative || Camera.IsFlying);
        _health.UpdateBreath(deltaTime, Camera.IsEyeInWater && !Camera.IsEyeInLava, Camera.IsCreative || Camera.IsFlying);
        UpdateCactusContact(deltaTime, chunkManager);
        _health.Update(deltaTime);
        _health.UpdateRegen(deltaTime, Camera.IsSprinting, Camera.IsCreative || Camera.IsFlying);
        // Singleplayer only: a client mirrors its server's mana and a server ticks ServerPlayer.TickStatus
        _health.UpdateMana(deltaTime);
        // Singleplayer only, like the mana: a client's effects are the server's mirror
        _health.UpdateEffects(deltaTime, Camera.IsCreative || Camera.IsFlying);

        // The day toast: the line appears the moment the sun rises through dawn, once a day
        var elevation = DayCycle.SunDirection(LightingTime).Y;
        if (_sunElevationKnown && DayToast.CrossedDawn(_lastSunElevation, elevation))
            Toast.Show($"Day {Clock.DayNumber(LightingTime)}");
        _lastSunElevation = elevation;
        _sunElevationKnown = true;

        if (_health.Health < beforeHp)
        {
            Camera.AddShake(Math.Clamp((beforeHp - _health.Health) / 40f, 0.2f, 1f));
            NotifyPlayerHurt(fromMob: false);
        }

        RespawnPrompt.Update(deltaTime, _health.IsDead);
        Camera.IsDead = _health.IsDead;
        if (!_health.IsDead)
        {
            _pendingDeathMessage = null;
            DeathLine = null;
            return;
        }

        // The first dead frame posts the death line once; GameplayInput drains it into the chat
        if (DeathLine == null)
        {
            DeathLine = DeathMessages.For("Player", _health.LastSource, _health.LastKiller);
            _pendingDeathMessage = DeathLine;
        }

        // The death plays first: the view tips over and the body falls. Then the player stays down
        // under the death screen until they ask to respawn.
        var progress = MathUtils.Clamp01(RespawnPrompt.DeadSeconds / RespawnPrompt.DelaySeconds);
        Camera.DeathProgress = progress;
        Player.DeathFall = progress;
        if (!RespawnPrompt.Requested)
            return;

        RespawnPrompt.Reset();
        Camera.DeathProgress = 0f;
        Player.DeathFall = 0f;
        _health.Respawn();
        Camera.IsDead = false;
        var home = _hasRespawnEye ? _respawnEye : Camera.Position;
        Camera.TestTeleport(home, Camera.Yaw, Camera.Pitch);
        _fall.Reset(home.Y - Camera.EyeHeight, grounded: false);
        Log.WriteLine("Respawned");
    }
}
