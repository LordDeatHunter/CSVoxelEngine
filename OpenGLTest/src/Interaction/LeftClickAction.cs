using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using VoxelEngine.Juice;
using VoxelEngine.Networking;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Smithing;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Interaction;

/// <summary>
/// The left-click action and the target it works on: the crosshair raycast that picks the
/// targeted block (with the face, box and aim a client's intents carry), hit-based mining with its
/// pop and break overlay, the attack-first punch, the short blade's slice combo and the sword's
/// three-swing chain, the HUD crosshair and look-at label read from them, and the punch and swings
/// a server lands for a connected player. Owned by <see cref="World"/>; main thread only.
/// </summary>
internal sealed class LeftClickAction
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly BlockDebrisSystem _debris;
    private readonly LeafParticleSystem _leaves;
    private readonly List<Vector3> _shudderCells = [];
    private readonly HitMarker _hitMarker;
    private readonly List<Mob> _mobs;
    private readonly bool _clientMode;

    private bool IsPlaying => _world.IsPlaying;
    private Camera Camera => _world.Camera;
    private PlayerModel Player => _world.Player;
    private Inventory Inventory => _world.Inventory;
    private BlockEditor Edits => _world.Edits;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;
    private MobStrikes Strikes => _world.Strikes;

    internal LeftClickAction(World world, ChunkManager chunkManager, BlockDebrisSystem debris, LeafParticleSystem leaves,
        HitMarker hitMarker, List<Mob> mobs, bool clientMode)
    {
        _leaves = leaves;
        _world = world;
        _chunkManager = chunkManager;
        _debris = debris;
        _hitMarker = hitMarker;
        _mobs = mobs;
        _clientMode = clientMode;
    }

    /// <summary>Whether the crosshair raycast found a block this frame.</summary>
    internal bool HasSelectedBlock => _hasSelectedBlock;

    /// <summary>The cell of the block under the crosshair.</summary>
    internal Vector3 SelectedBlockPosition => _selectedBlockPosition;

    /// <summary>The outward normal of the face the crosshair raycast entered the block through.</summary>
    internal Vector3 SelectedFaceNormal => _selectedFaceNormal;

    /// <summary>Which of the targeted block's hitboxes the crosshair raycast hit.</summary>
    internal int SelectedBoxIndex => _selectedBoxIndex;

    /// <summary>Whether a block is being mined (hit-based, survival only).</summary>
    internal bool IsBreaking => _isBreaking;

    /// <summary>The cell of the block being mined.</summary>
    internal Vector3 BreakingBlockPosition => _breakingBlockPosition;

    /// <summary>The id of the block being mined.</summary>
    internal ushort BreakingBlockId => _breakingBlockId;

    /// <summary>Whether the cell being mined shows its progress as an eaten-away face (a natural trunk or
    /// limb cell with a side bite, thin or full), so the crack overlay stays off for it.</summary>
    internal bool ShowsEatenFace =>
        _isBreaking && _breakNatural
        && _chunkManager.ChopDamage.TryGet((int)_breakingBlockPosition.X, (int)_breakingBlockPosition.Y,
            (int)_breakingBlockPosition.Z, out var chop) && chop.Face != WoodDamage.NoFace;

    /// <summary>
    /// Fires this frame's crosshair raycast: the targeted block, its face and box, and the head ray
    /// a client's break intents carry. Water is aimed through unless the held item scoops it.
    /// </summary>
    internal void UpdateSelection()
    {
        _hasSelectedBlock = Camera.Raycast(World.InteractionReach, _chunkManager,
            out _selectedBlockPosition, out _selectedFaceNormal, out _selectedBoxIndex,
            includeFluids: ItemRegistry.TryGet(Inventory.SelectedBlockId)?.TargetsFluids ?? false);
        _selectedAimDirection = Camera.LastAimDirection;
    }

    // A hit makes the block jump out and settle back. Kept below BlockHitSeconds so the
    // pop always finishes before the next hit restarts it.
    private const float BlockPopSeconds = 0.15f;

    // Absolute growth in blocks the mined block gains at the peak of a hit, added to the
    // block's hitbox on every axis rather than multiplied, so tiny blocks pop by the same
    // amount as full cubes instead of barely moving
    private const float BlockPopGrow = 0.12f;

    private bool _hasSelectedBlock;
    private Vector3 _selectedBlockPosition;
    private Vector3 _selectedFaceNormal;
    private int _selectedBoxIndex;
    private bool _isBreaking;
    private Vector3 _breakingBlockPosition;
    private ushort _breakingBlockId;
    private ushort _breakingHeldId;
    private float _breakDamage;
    private float _breakHardness;
    private float _breakPower;
    private float _breakHitSeconds = Mining.DefaultBlockHitGapMs / 1000f;
    private bool _breakAllowed;
    private float _hitTimer;
    // An axe's chop already started ahead of the coming hit, so the blow lands with it
    private bool _chopStarted;
    // A natural tree's wood keeps its chop damage for good (WoodDamage): the break is of such a cell, the
    // state byte it was started with, and the side face the crosshair hit (-1 for a top or bottom)
    private bool _breakNatural;
    private byte _breakState;
    private int _breakFace;
    private float _popElapsed = BlockPopSeconds;

    // Swing cadence: seconds since the last swing or block hit, the left-click attack running on the held item's
    // pace. It keeps counting while the button is up, so releasing and pressing again never skips the wait (no
    // spam-clicking past the pace); infinity until the first swing, so that one lands at once.
    private float _punchTimer = float.PositiveInfinity;
    private float _punchGap = Mining.DefaultHitGapMs / 1000f;

    // Slice attack (AttackPattern.Slice): the two-phase combo, one click-buffered return swing,
    // and the per-swing latch that lands its damage once at the impact frame.
    private readonly SliceCombo _sliceCombo = new();
    private bool _sliceImpactDone;
    // Sword attack (AttackPattern.Sword): the cut, back cut and chop chain and the same latch.
    private readonly SwordCombo _swordCombo = new();
    private bool _swordImpactDone;
    // Previous frame's left-click state, so the combos can see a fresh press.
    private bool _attackWasPressed;
    // The mobs a swing reaches, nearest first (reused every swing, so a swing allocates nothing).
    private readonly List<(float Distance, Mob Mob)> _strikeScratch = [];

    // The head ray the selection raycast fired along (Camera.LastAimDirection), which a client's
    // break intents carry so the server's first-person ray lands on the same cell.
    private Vector3 _selectedAimDirection = -Vector3.UnitZ;

    // Creative mining pace: one block per CreativeBreakRepeatSeconds while the button is held.
    // Infinity while released, so a click breaks at once.
    private float _creativeBreakTimer = float.PositiveInfinity;

    /// <summary>
    /// What the HUD crosshair draws with this frame: the throw charge (the arms spread), the mined
    /// block's break progress (0 in creative or with no target) and the hit marker's alpha.
    /// </summary>
    internal CrosshairState Crosshair =>
        new(Camera.ThrowCharge ?? 0f,
            _isBreaking && !Camera.IsCreative ? BreakProgress : 0f,
            _hitMarker.Alpha);

    /// <summary>
    /// The look-at label's state under the crosshair, or null with no target (see
    /// <see cref="LookLabel"/>): the block's name ("Unknown" until its item is obtained), whether
    /// the held item can mine it, and the cell, state byte and voxel light the profiler's debug
    /// lines read.
    /// </summary>
    internal LookLabelState? LookAt
    {
        get
        {
            if (!_hasSelectedBlock) return null;
            var cellId = Edits.BlockIdAt(_selectedBlockPosition);
            var (x, y, z) = _selectedBlockPosition.BlockCell();
            // Any cell of a multiblock shows the whole structure, not its plain part block; either half of a
            // two-cell block (a waystone) names its lower half, which the label draws stacked with the upper
            var blockId = Multiblock.TryResolveAnchor(_chunkManager, x, y, z, out var anchorPos, out _, out var anchorType)
                ? anchorType!.Id
                : TallBlock.LowerOf(cellId);
            var state = LookLabel.For(blockId, Camera.IsCreative, Inventory.Knows, HeldItem(),
                _chunkManager.GetStateGlobal(x, y, z));
            if (state.Text.Length == 0) return null;
            return new LookLabelState(state.Text, state.Known, state.Mineable, _selectedBlockPosition,
                blockId, _chunkManager.GetStateGlobal(x, y, z),
                EntityLight.Sample(_chunkManager, _selectedBlockPosition),
                IronWorksDetail(blockId, anchorPos, x, y, z) ?? _world.Towers.Detail(blockId, x, y, z),
                StationBarsAt(blockId, anchorPos, x, y, z));
        }
    }

    // The progress bars of the station under the crosshair (see StationBars), null for anything else
    private List<StationBar>? StationBarsAt(ushort blockId, Vector3 anchor, int x, int y, int z)
    {
        var type = BlockType.ById[blockId];
        if (type == null) return null;
        var (ax, ay, az) = ((int)anchor.X, (int)anchor.Y, (int)anchor.Z);
        if (type.Renderer == BlockRenderer.BranchPile)
            return StationBars.Campfire(_world.Campfires, x, y, z, _chunkManager.GetStateGlobal(x, y, z), type.StackMax);
        if (type.Renderer == BlockRenderer.Quern) return StationBars.Quern(_world.Querns.StateAt(x, y, z));
        if (type.CrucibleStand) return StationBars.StandingCrucible(_world.StandingCrucibles.Peek((x, y, z)));
        if (type.Kiln != null) return StationBars.Kiln(_world.Kilns.StateAt(ax, ay, az));
        if (type.Bloomery != null) return StationBars.Bloomery(_world.Smithing.Bloomeries.StateAt(ax, ay, az));
        if (type.Anvil != null) return StationBars.Anvil(_world.Smithing.Anvils.StateAt(x, y, z));
        return StationBars.Mold(_world.Casting.StateAt(x, y, z));
    }

    // The status line of the bloomery or anvil under the crosshair (see SmithingReadout), null for anything else
    private string? IronWorksDetail(ushort blockId, Vector3 anchor, int x, int y, int z)
    {
        var type = BlockType.ById[blockId];
        if (type?.Bloomery != null && _world.Smithing.Bloomeries.StateAt((int)anchor.X, (int)anchor.Y, (int)anchor.Z) is { } bloomery)
            return SmithingReadout.Bloomery(bloomery);
        if (type?.Anvil != null && _world.Smithing.Anvils.StateAt(x, y, z) is { } anvil)
            return SmithingReadout.Anvil(anvil);
        return null;
    }

    // Break overlay strength. Quantized to whole hits, so it steps with the damage dealt
    // instead of sweeping continuously.
    internal float BreakProgress => _breakHardness > 0f ? _breakDamage / _breakHardness : 0f;

    // Growth of the block being mined: 0 at rest, eased up to BlockPopGrow and back down
    // over BlockPopSeconds after each hit lands
    internal float BlockPopGrowNow =>
        BlockPopGrow * MathUtils.Pulse(_popElapsed / BlockPopSeconds);

    /// <summary>
    /// Lands <paramref name="player"/>'s punch: the nearest mob inside the forward cone from the
    /// player's eye with a clear line of sight takes the held item's <c>entityDamage</c> and a
    /// shove. Returns whether a mob was struck.
    /// </summary>
    internal bool PunchFor(PlayerContext player)
    {
        if (!IsPlaying) return false;
        if (!TryFindPunchTarget(player.Eye, player.ViewDirection, out var target)) return false;
        var held = HeldItem(player.Inventory);
        Strikes.StrikeMob(target, Combat.PunchDamage(held, GearDamage(player.Inventory)), player.Eye, player.Health,
            shrapnel: ShrapnelFor(held, player.Health));
        return true;
    }

    /// <summary>
    /// Lands one of <paramref name="player"/>'s slice swings (<paramref name="returnSwing"/>: the
    /// half-power return): the nearest two mobs in the forward hemisphere with a clear line of sight
    /// are struck. Returns how many were.
    /// </summary>
    internal int SliceFor(PlayerContext player, bool returnSwing)
    {
        if (!IsPlaying) return 0;
        return ApplySliceDamage(player.Eye, player.ViewDirection, HeldItem(player.Inventory), returnSwing, player.Health, GearDamage(player.Inventory));
    }

    /// <summary>
    /// Lands one of <paramref name="player"/>'s sword swings (<see cref="SwordSwings"/>: the cut and
    /// back cut on the nearest few mobs in the forward hemisphere, the chop on the one under the
    /// aim), each with a clear line of sight. Returns how many were struck.
    /// </summary>
    internal int SwordFor(PlayerContext player, SwordSwing swing)
    {
        if (!IsPlaying) return 0;
        return ApplySwordDamage(player.Eye, player.ViewDirection, HeldItem(player.Inventory), swing, player.Health, GearDamage(player.Inventory));
    }

    /// <summary>
    /// Drives the left-click action, attack-first. A mob inside the forward punch cone
    /// (see <see cref="Combat"/>) takes the swing - <c>entityDamage</c> plus a shove - and the
    /// block under the crosshair is left alone for as long as one is there; with the cone clear
    /// the block is mined. Mining itself is hit-based: the first hit on a block lands the instant
    /// it is targeted, so a quick tap counts as one hit, then one hit per the held item's pace
    /// while it is held (<see cref="Mining"/>; a bare hand uses the baseline). Each hit removes
    /// the held item's <c>blockDamage</c> from the block's float hardness, and the block breaks
    /// when the damage reaches it. A block whose <c>requiresTool</c> gate refuses the held tool
    /// (wrong kind, or below its required tier) takes 0 damage and shows no progress, though the
    /// swing still plays. A block of hardness 0 breaks on first contact. Damage is discarded when
    /// the target, the held tool or the button changes. A click with no block under the
    /// crosshair still swings the fist. Creative instamines and ignores the gate, but an
    /// unbreakable block never breaks (survival hits on it whiff with no progress).
    /// Call after Update so the selection is current. Does nothing unless the world is playing:
    /// nothing may edit an unloaded world.
    /// </summary>
    internal void UpdateBlockBreaking(double deltaTime, bool isHolding)
    {
        if (!IsPlaying) return;
        // A downed player lands nothing: a swing already in flight (or buffered) is abandoned, not just new presses
        if (Camera.IsDead)
        {
            ResetCombos();
            StopBreaking();
            _attackWasPressed = false;
            return;
        }
        var justPressed = isHolding && !_attackWasPressed;
        _attackWasPressed = isHolding;
        _anvilCooldown = MathF.Max(0f, _anvilCooldown - (float)deltaTime);
        _stumpCooldown = MathF.Max(0f, _stumpCooldown - (float)deltaTime);
        UpdateSplit((float)deltaTime);
        // A button still held after a split is spent: it must not go on to mine the stump the log stood on
        if (_awaitRelease)
        {
            if (isHolding)
            {
                StopBreaking();
                return;
            }
            _awaitRelease = false;
        }

        var held = HeldItem();
        var player = LocalPlayer.LocalContext();

        // A blade owns the left-click outright: every press swings it (and buffers the next swing
        // while one plays), so it never mines.
        if (held is { Attack: AttackPattern.Slice })
        {
            Camera.NotifyPlayerAction();
            UpdateSlice(justPressed, player);
            StopBreaking();
            return;
        }
        if (held is { Attack: AttackPattern.Sword })
        {
            Camera.NotifyPlayerAction();
            UpdateSword(justPressed, (float)deltaTime, player);
            StopBreaking();
            return;
        }

        // Switching away from a blade abandons any in-flight combo; its animation still finishes,
        // it just no longer lands a hit.
        ResetCombos();

        _punchTimer += (float)deltaTime;
        if (!isHolding)
        {
            StopBreaking();
            _creativeBreakTimer = float.PositiveInfinity;
            return;
        }

        // Orbit mode turns the body toward the aim while the action fires at once.
        Camera.NotifyPlayerAction();

        // Attack-first: while a mob is in the punch cone it owns the swing, so no block is
        // mined through it (including while the punch itself is on cooldown).
        if (UpdatePunch(player))
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
        if (BlockType.IsFluid(Edits.BlockIdAt(_selectedBlockPosition)))
        {
            StopBreaking();
            return;
        }

        // A smithing hammer on an anvil works what lies on it instead of mining the anvil
        if (Mining.IsHammer(held) && BlockType.ById[Edits.BlockIdAt(_selectedBlockPosition)]?.Anvil != null)
        {
            StopBreaking();
            UpdateAnvilHammer(held!);
            return;
        }

        // A log standing on a stump takes the blow (it wobbles) instead of the stump being mined
        if (_world.Stumps.LogAt((int)_selectedBlockPosition.X, (int)_selectedBlockPosition.Y, (int)_selectedBlockPosition.Z) != null)
        {
            StopBreaking();
            UpdateStumpHit();
            return;
        }

        if (Camera.IsCreative)
        {
            StopBreaking();
            // The press breaks at once; holding repeats only after a delay, so a tap never
            // takes the block behind the target too (this also keeps a client under the edit rate)
            _creativeBreakTimer += (float)deltaTime;
            if (_creativeBreakTimer < PlayerConfig.CreativeBreakRepeatSeconds)
                return;
            _creativeBreakTimer = 0f;
            var creativeTarget = Edits.BlockIdAt(_selectedBlockPosition);
            LocalPlayer.PunchSwing();
            PlayHitSfx(creativeTarget, _selectedBlockPosition);
            // Creative swings at an unbreakable block like at any other, but it never breaks
            if (!BlockType.IsUnbreakable(creativeTarget))
                BreakMined(_selectedBlockPosition, player);
            return;
        }

        var heldId = Inventory.SelectedBlockId;

        // A new target or a different held item restarts the break: the gate, damage and pace
        // all belong to that pairing, so carrying progress over would apply the wrong numbers
        if (!_isBreaking || _selectedBlockPosition != _breakingBlockPosition || heldId != _breakingHeldId)
        {
            _isBreaking = true;
            _breakFace = SideFaceOf(_selectedFaceNormal);
            _breakingBlockPosition = _selectedBlockPosition;
            _breakingBlockId = Edits.BlockIdAt(_selectedBlockPosition);
            _breakingHeldId = heldId;
            // Tree wood's hardness follows its thickness, which lives in the state byte
            _breakState = _chunkManager.GetStateGlobal(
                (int)_selectedBlockPosition.X, (int)_selectedBlockPosition.Y, (int)_selectedBlockPosition.Z);
            _breakHardness = Mining.HardnessOf(_breakingBlockId, _breakState);
            _breakAllowed = Mining.CanMine(_breakingBlockId, held, _breakState);
            _breakPower = Mining.BlockDamage(held);
            _breakHitSeconds = Mining.BlockHitGapSeconds(held,
                (Camera.Effects?.AttackFactor ?? 1f) * Inventory.Equipment.Stats.MiningFactor);
            // A natural tree's wood keeps the damage it took (only here: a connected client's server counts
            // its own break from zero, and a placed log never keeps any)
            _breakNatural = !_clientMode && BlockType.IsTreeWood(_breakingBlockId) && !WoodState.IsDetached(_breakState);
            _breakDamage = _breakNatural && _chunkManager.ChopDamage.TryGet((int)_selectedBlockPosition.X,
                (int)_selectedBlockPosition.Y, (int)_selectedBlockPosition.Z, out var kept) ? kept.Damage : 0f;
            // Seed the timer with the time since the last swing, up to a whole interval: after a pause the
            // loop below lands the first hit this frame (a tap counts as one hit), but a press right after
            // a swing waits out what is left of the pace instead of restarting it.
            _hitTimer = MathF.Min(_breakHitSeconds, _punchTimer);
            _popElapsed = BlockPopSeconds;
            // A connected client: the server starts its own count of this break here, and judges
            // the completion against it (BreakProgress)
            _world.RaiseIntent(WorldIntent.BreakStart(Camera.Position, _selectedAimDirection, _breakingBlockPosition));
        }

        // Zero hardness covers two cases: a real block of hardness 0 (pebbles) pops on first
        // contact with no hold-to-mine, while air or an unloaded chunk has nothing to mine
        // at all - the id being a registered block is what tells them apart. A refused tool
        // whiffs instead of popping the block.
        if (_breakHardness <= 0f)
        {
            if (BlockType.ById[_breakingBlockId] != null)
            {
                LocalPlayer.PunchSwing();
                PlayHitSfx(_breakingBlockId, _breakingBlockPosition);
                if (_breakAllowed)
                    BreakMined(_selectedBlockPosition, player);
            }
            StopBreaking();
            return;
        }

        _hitTimer += (float)deltaTime;
        _popElapsed += (float)deltaTime;

        // An axe winds up ahead of the hit: its chop starts early enough that the blade reaches the block
        // as the hit lands (the first hit of a break lands at once, so it chops a beat late)
        if (_breakAllowed && !_chopStarted && Mining.IsAxe(held) && _hitTimer < _breakHitSeconds)
        {
            var chopSeconds = LocalPlayer.AxeChopSeconds(_breakHitSeconds);
            var lead = chopSeconds * PlayerModel.SwordImpactProgress(SwordSwing.AxeChop);
            if (_hitTimer >= _breakHitSeconds - lead)
            {
                Player.StartAxeChop(chopSeconds);
                _chopStarted = true;
            }
        }

        // A long frame can carry more than one hit; the leftover time rolls into the next
        while (_hitTimer >= _breakHitSeconds)
        {
            _hitTimer -= _breakHitSeconds;
            _punchTimer = _hitTimer;
            LocalPlayer.PunchSwing(_chopStarted);
            _chopStarted = false;
            PlayHitSfx(_breakingBlockId, _breakingBlockPosition);

            // Start the pop from where the hit actually landed inside this frame, not from
            // the frame boundary, so the animation stays in step with the hit rhythm
            _popElapsed = _hitTimer;

            // A refused tool still swings and sounds, but deals nothing: no damage, no debris
            // and no progress, so the block can never come down that way
            if (!_breakAllowed)
                continue;

            _breakDamage += _breakPower;

            var breaks = _breakDamage >= _breakHardness;
            if (BlockType.IsTreeWood(_breakingBlockId) && !breaks)
            {
                ShudderCanopy(_breakingBlockPosition);
                if (_breakNatural) RecordChop();
            }
            var kick = BreakKick.Strength(_breakHardness, breaks);
            if (kick > 0f) Camera.AddHitKick(Vector3.Zero, kick);

            if (!breaks)
            {
                _debris.Burst(_breakingBlockPosition, _breakingBlockId, ParticleConfig.BlockDebrisPerHit,
                    columns: _chunkManager.ColumnSource);
                continue;
            }

            BreakMined(_breakingBlockPosition, player);
            StopBreaking();
            return;
        }
    }

    // Seconds until the next hammer hit may land: the hammer's own gap, counted down whether or not the button is
    // held, so releasing and pressing again never skips it
    private float _anvilCooldown;

    /// <summary>
    /// One hammer hit on the aimed anvil, while the button is held: the first lands at once, the next after the
    /// hammer's own gap (<see cref="Mining.HitGapSeconds(Item, float)"/>, a smithing hammer's is a couple of seconds, so
    /// there is no spamming). A hit with nothing on the anvil does nothing. A connected client only predicts it
    /// (the swing, sound and chips, from the anvil it mirrors) and raises <see cref="NetIntentKind.Hammer"/>; the
    /// server judges the gap itself and its snapshot carries the result.
    /// </summary>
    private void UpdateAnvilHammer(Item held)
    {
        if (_anvilCooldown > 0f) return;

        var cell = _selectedBlockPosition;
        var gap = Mining.HitGapSeconds(held,
            (Camera.Effects?.AttackFactor ?? 1f) * Inventory.Equipment.Stats.MiningFactor);
        if (_clientMode)
        {
            _world.Interactions.TryTrackAnvilAt(cell);
            if (_world.Smithing.Anvils.StateAt((int)cell.X, (int)cell.Y, (int)cell.Z) is not { HasPiece: true })
                return;
        }
        else if (_world.HammerFor(cell, gap, 0f).Kind == AnvilHitKind.Refused)
        {
            return;
        }

        _anvilCooldown = gap;
        Camera.NotifyPlayerAction();
        LocalPlayer.PunchSwing();
        var anvilId = Edits.BlockIdAt(cell);
        PlayHitSfx(anvilId, cell);
        _debris.Burst(cell, anvilId, ParticleConfig.BlockDebrisPerHit, columns: _chunkManager.ColumnSource);
        Camera.AddHitKick(Vector3.Zero, BreakKick.Strength(AnvilKickHardness, false));
        _world.RaiseIntent(WorldIntent.Hammer(Camera.Position, _selectedAimDirection, cell));
    }

    /// <summary>
    /// A blow on the log standing on the aimed stump. An axe starts the slow overhead split swing (see
    /// <see cref="UpdateSplit"/>, which lands it); anything else wobbles the log at
    /// <see cref="StumpConfig.HitGapSeconds"/> pace: the swing, the knock and chips of the log it hits, then the wobble
    /// itself (the server's, through <see cref="NetIntentKind.StumpHit"/>, on a connected client).
    /// </summary>
    private void UpdateStumpHit()
    {
        if (_stumpCooldown > 0f || _splitCell != null) return;

        var cell = _selectedBlockPosition;
        var (x, y, z) = cell.BlockCell();
        if (_world.Stumps.LogAt(x, y, z) is not { } log) return;
        Camera.NotifyPlayerAction();

        if (Mining.IsAxe(HeldItem()) && BlockType.ById[log.LogId]?.SplitLoot != null)
        {
            _splitCell = cell;
            _splitImpactIn = StumpConfig.SplitSwingSeconds * PlayerModel.SwordImpactProgress(SwordSwing.AxeSplit);
            _stumpCooldown = StumpConfig.SplitSwingSeconds;
            Player.StartAxeSplit();
            Camera.PunchFov(-1.2f);
            return;
        }

        _stumpCooldown = StumpConfig.HitGapSeconds;
        LocalPlayer.PunchSwing();
        PlayHitSfx(log.LogId, cell);
        _debris.Burst(cell, log.LogId, ParticleConfig.BlockDebrisPerHit, columns: _chunkManager.ColumnSource);
        _world.StumpHitFor(cell, split: false);
        _world.RaiseIntent(WorldIntent.StumpHit(Camera.Position, _selectedAimDirection, cell));
    }

    /// <summary>
    /// Runs the split swing started by <see cref="UpdateStumpHit"/> to its blow, whether or not the button is still held:
    /// when the axe comes down the log splits (in singleplayer; a connected client sends the blow as a
    /// <see cref="NetIntentKind.StumpHit"/> and the server splits it), wood chips burst from the stump, the view kicks, and
    /// the lumber thrown out is the server's drops. A log already gone by then (lifted off, split by someone else) is just
    /// a swing at the stump.
    /// </summary>
    private void UpdateSplit(float deltaTime)
    {
        if (_splitCell is not { } cell) return;
        _splitImpactIn -= deltaTime;
        if (_splitImpactIn > 0f) return;
        _splitCell = null;

        var (x, y, z) = cell.BlockCell();
        if (_world.Stumps.LogAt(x, y, z) is not { } log) return;

        PlayHitSfx(log.LogId, cell);
        // Only the blow that finishes the split bursts the whole log; the ones before it chip it
        var finishes = log.SplitHits + 1 >= (BlockType.ById[log.LogId]?.SplitHits ?? 1);
        _debris.Burst(cell, log.LogId, finishes ? ParticleConfig.StumpSplitDebris : ParticleConfig.BlockDebrisPerHit,
            columns: _chunkManager.ColumnSource);
        Camera.AddHitKick(Vector3.Zero, BreakKick.Strength(finishes ? SplitKickHardness : SplitKickHardness / 2f, false));
        _world.StumpHitFor(cell, split: true);
        _awaitRelease = finishes;
        _world.RaiseIntent(WorldIntent.StumpHit(Camera.Position, _selectedAimDirection, cell));
    }

    // The hardness the camera kick of a split is sized as: a heavy blow, like felling a trunk
    private const float SplitKickHardness = 12f;

    // The stump the split swing in flight is aimed at (null when none) and the seconds until its blow lands
    private Vector3? _splitCell;
    private float _splitImpactIn;
    private bool _awaitRelease;

    private float _stumpCooldown;

    // The hardness the camera kick of a hammer blow is sized as: a solid knock, like mining stone
    private const float AnvilKickHardness = 5f;

    /// <summary>
    /// Breaks a block the local player's mining finished. On a connected client the break is only
    /// a prediction: the completion goes out as an intent, and the server breaks the block only
    /// when its own count of the hits got there too.
    /// </summary>
    private void BreakMined(Vector3 cell, PlayerContext player)
    {
        // Mining a block discovers it (the look label's "Unknown"), whether or not its drop is picked up
        var mined = Edits.BlockIdAt(cell);
        Edits.RemoveBlock(cell, player);
        // A felled trunk's root stays behind as its stump, so the cell need not end as air, only as something else
        if (Edits.BlockIdAt(cell) != mined)
        {
            player.Inventory.MarkKnown(mined);
            // Only the mined cell counts, never the torches and pebbles it was holding up
            player.Quests?.OnBroke(mined);
        }
        _world.RaiseIntent(WorldIntent.BreakComplete(Camera.Position, _selectedAimDirection, cell));
    }

    // The engine side face (0 -Z, 1 +Z, 2 -X, 3 +X) a horizontal normal points out of, or -1 for a vertical one
    private static int SideFaceOf(Vector3 normal)
    {
        if (normal.Z < -0.5f) return 0;
        if (normal.Z > 0.5f) return 1;
        if (normal.X < -0.5f) return 2;
        return normal.X > 0.5f ? 3 : -1;
    }

    /// <summary>
    /// Keeps the damage so far on the natural trunk cell being chopped for good (<see cref="WoodDamage"/>) and
    /// remeshes its chunk when the slit in its face got deeper (<see cref="WoodDamage.BiteDepthPx"/>). The bite is
    /// on the side face the first hit landed on; a hit on a top or bottom face shows none.
    /// </summary>
    private void RecordChop()
    {
        var cell = _breakingBlockPosition;
        int x = (int)cell.X, y = (int)cell.Y, z = (int)cell.Z;
        var damage = _chunkManager.ChopDamage;
        var before = ShownDepth(damage, x, y, z);
        damage.Set(x, y, z, _breakDamage, _breakFace < 0 ? WoodDamage.NoFace : _breakFace);
        if (ShownDepth(damage, x, y, z) != before)
            _chunkManager.ImmediateRemesh(VoxelCoords.ChunkPosOf(cell));
    }

    // How deep the slit of the cell's stored damage is (0 with no damage or no side face)
    private int ShownDepth(WoodDamage damage, int x, int y, int z) =>
        damage.TryGet(x, y, z, out var entry) && entry.Face != WoodDamage.NoFace
            ? WoodDamage.BiteDepthPx(entry.Damage, _breakHardness) : 0;

    /// <summary>A hit on tree wood shakes some of the canopy above and around it loose.</summary>
    private void ShudderCanopy(Vector3 woodCell)
    {
        TreeShudder.Gather(woodCell, (x, y, z) => TreeGenerator.IsLeaf(_chunkManager.GetBlockGlobal(x, y, z)),
            _shudderCells, ParticleConfig.TreeShudderCells, ParticleConfig.TreeShudderReachBlocks,
            ParticleConfig.TreeShudderHeightBlocks);
        _leaves.BurstCells(_shudderCells, ParticleConfig.TreeShudderCells * ParticleConfig.TreeShudderLeavesPerCell);
    }

    private void StopBreaking()
    {
        _isBreaking = false;
        _breakNatural = false;
        _chopStarted = false;
        _breakDamage = 0f;
        _breakHardness = 0f;
        _breakAllowed = false;
        _breakHitSeconds = Mining.DefaultBlockHitGapMs / 1000f;
        _hitTimer = 0f;
        _popElapsed = BlockPopSeconds;
    }

    /// <summary>The item in hand, or null when the selected slot holds nothing.</summary>
    private Item? HeldItem() => HeldItem(Inventory);

    // The damage multiplier of the gear a player wears (1 when nothing changes it)
    private static float GearDamage(Inventory inventory) => inventory.Equipment.Stats.DamageFactor;

    // The bone shrapnel a landed strike with this item sprays for the striker's worn gear
    private static ShrapnelProfile ShrapnelFor(Item? held, PlayerHealth? striker) =>
        ShrapnelProfile.Resolve(held, striker?.Gear?.Has(GearAbility.BoneShrapnel) == true);

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
    private bool UpdatePunch(PlayerContext player)
    {
        var held = HeldItem();
        _punchGap = Mining.HitGapSeconds(held,
            (Camera.Effects?.AttackFactor ?? 1f) * player.Inventory.Equipment.Stats.AttackSpeedFactor);

        if (TryFindPunchTarget(player.Eye, player.ViewDirection, out var target))
        {
            if (_punchTimer < _punchGap)
                return true;

            _punchTimer = 0f;
            var weight = LocalPlayer.PunchSwing();
            if (_clientMode)
            {
                _world.RaiseIntent(WorldIntent.AttackWith(player.Eye, player.ViewDirection, NetAttackKind.Punch));
                // The predicted connect sparks too, like the hit-stop's freeze below
                Strikes.SpawnHitSparks(target, player.Eye);
            }
            else
                Strikes.StrikeMob(target, Combat.PunchDamage(held, GearDamage(player.Inventory)), player.Eye, player.Health,
                    shrapnel: ShrapnelFor(held, player.Health));
            // The punch connected, so the swing holds at full extension for a beat (cosmetic: the
            // punch timer above keeps the pace). A client only predicts the connect - a mob was in
            // the cone with a clear line when the swing landed - since the server's verdict comes
            // back far too late for a freeze that has to start with the swing.
            Player.HitStop(SwingHitStop.SecondsFor(weight));
            _hitMarker.Show();
            return true;
        }

        if (_hasSelectedBlock || !(_punchTimer >= _punchGap)) return false;
        _punchTimer = 0f;
        LocalPlayer.PunchSwing();

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
        if (_mobs.Count == 0)
            return false;

        var bestDistance = float.PositiveInfinity;
        Mob? best = null;

        foreach (var mob in _mobs)
        {
            if (mob.AimPointFrom(origin) is not { } aim) continue;
            if (!Combat.InPunchCone(origin, direction, aim, out var distance))
                continue;
            if (BlockRaycaster.SegmentHitsBlock(origin, aim, _chunkManager))
                continue;
            if (!(distance < bestDistance)) continue;
            bestDistance = distance;
            best = mob;
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
    private void UpdateSlice(bool justPressed, PlayerContext player)
    {
        if (justPressed && _sliceCombo.Press())
        {
            Player.StartSlice(_sliceCombo.Direction);
            _world.Sounds.Play(SfxLibrary.SwooshGroup);
            _sliceImpactDone = false;
        }

        if (!_sliceCombo.Active)
            return;

        if (!_sliceImpactDone && Player.SliceProgress >= PlayerModel.SliceImpactProgress)
        {
            _sliceImpactDone = true;
            var returnSwing = _sliceCombo.ReturnSwing;
            LandSwing(player, returnSwing ? NetAttackKind.SliceReturn : NetAttackKind.Slice,
                Combat.SliceDamage(HeldItem(), returnSwing, GearDamage(player.Inventory)), Combat.SliceTargets, cone: false,
                MobConfig.SlimePunchShoveSpeed, hitStopWeight: 0f);
        }

        if (Player.IsSlicing)
            return;

        if (!_sliceCombo.Advance()) return;
        Player.StartSlice(_sliceCombo.Direction);
        _world.Sounds.Play(SfxLibrary.SwooshGroup);
        _sliceImpactDone = false;
    }

    /// <summary>
    /// Advances the sword's chain against the player's clicks (<see cref="SwordCombo"/>): a press
    /// starts the cut, or the next swing of the chain while the window after the last one is open; a
    /// press while a swing plays buffers the next. Damage lands once per swing at its impact frame
    /// (<see cref="PlayerModel.SwordImpactProgress"/>) with that swing's reach, power and shove
    /// (<see cref="SwordSwings"/>); a connected client sends the swing to the server instead of
    /// striking anything itself. The swing animation lives in <see cref="PlayerModel"/>.
    /// </summary>
    private void UpdateSword(bool justPressed, float deltaTime, PlayerContext player)
    {
        _swordCombo.Update(deltaTime);
        if (justPressed && _swordCombo.Press())
        {
            Player.StartSword(_swordCombo.Swing);
            _world.Sounds.Play(SfxLibrary.SwooshGroup);
            _swordImpactDone = false;
        }

        if (!_swordCombo.Active)
            return;

        var swing = _swordCombo.Swing;
        if (!_swordImpactDone && Player.SwordProgress >= PlayerModel.SwordImpactProgress(swing))
        {
            _swordImpactDone = true;
            LandSwing(player, NetAttacks.OfSword(swing), Combat.SwordDamage(HeldItem(), swing, GearDamage(player.Inventory)),
                SwordSwings.MaxTargets(swing), SwordSwings.IsCone(swing),
                MobConfig.SlimePunchShoveSpeed * SwordSwings.ShoveFactor(swing), SwordSwings.HitStopWeight(swing));
        }

        if (Player.IsSwordSwinging)
            return;

        if (!_swordCombo.Advance()) return;
        Player.StartSword(_swordCombo.Swing);
        _world.Sounds.Play(SfxLibrary.SwooshGroup);
        _swordImpactDone = false;
    }

    private void ResetCombos()
    {
        _sliceCombo.Reset();
        _sliceImpactDone = false;
        _swordCombo.Reset();
        _swordImpactDone = false;
    }

    /// <summary>
    /// A blade swing reached its impact frame: lands it on the mobs it reaches, or, on a connected
    /// client, sends it as <paramref name="kind"/> and predicts the connect (a mob in reach with a clear
    /// line, since the server's verdict comes back far too late for a freeze that has to start with the
    /// swing) with its hit sparks. A connecting swing holds at the impact for the hit-stop of
    /// <paramref name="hitStopWeight"/> and flashes the hit marker; it still ends on time, so the combo's
    /// next swing is never delayed.
    /// </summary>
    private void LandSwing(PlayerContext player, NetAttackKind kind, int damage, int maxTargets, bool cone,
        float shoveSpeed, float hitStopWeight)
    {
        bool connected;
        if (_clientMode)
        {
            _world.RaiseIntent(WorldIntent.AttackWith(player.Eye, player.ViewDirection, kind));
            connected = HasBladeTarget(player.Eye, player.ViewDirection, cone, out var predicted);
            if (predicted != null) Strikes.SpawnHitSparks(predicted, player.Eye);
        }
        else
        {
            connected = StrikeNearest(player.Eye, player.ViewDirection, damage, maxTargets, cone, shoveSpeed, player.Health,
                ShrapnelFor(HeldItem(), player.Health), HeldItem()) > 0;
        }

        if (!connected) return;
        Player.HitStop(SwingHitStop.SecondsFor(hitStopWeight));
        _hitMarker.Show();
    }

    /// <summary>
    /// Lands one slice swing from <paramref name="origin"/> along <paramref name="direction"/>
    /// with <paramref name="held"/>: the nearest two mobs inside the forward hemisphere with an
    /// unobstructed line of sight each take the swing's damage and a knockback shove. The return
    /// swing is at half power (<paramref name="returnSwing"/>). Returns how many were struck.
    /// </summary>
    private int ApplySliceDamage(Vector3 origin, Vector3 direction, Item? held, bool returnSwing, PlayerHealth? striker,
        float damageFactor = 1f) =>
        StrikeNearest(origin, direction, Combat.SliceDamage(held, returnSwing, damageFactor), Combat.SliceTargets, cone: false,
            MobConfig.SlimePunchShoveSpeed, striker, ShrapnelFor(held, striker), held);

    /// <summary>
    /// Lands one sword swing from <paramref name="origin"/> along <paramref name="direction"/> with
    /// <paramref name="held"/>: the nearest mobs the swing reaches (<see cref="SwordSwings"/>) with an
    /// unobstructed line of sight each take its damage and its shove. Returns how many were struck.
    /// </summary>
    private int ApplySwordDamage(Vector3 origin, Vector3 direction, Item? held, SwordSwing swing, PlayerHealth? striker,
        float damageFactor = 1f) =>
        StrikeNearest(origin, direction, Combat.SwordDamage(held, swing, damageFactor), SwordSwings.MaxTargets(swing),
            SwordSwings.IsCone(swing), MobConfig.SlimePunchShoveSpeed * SwordSwings.ShoveFactor(swing), striker,
            ShrapnelFor(held, striker), held);

    /// <summary>
    /// Strikes the nearest <paramref name="maxTargets"/> mobs a swing from <paramref name="origin"/> along
    /// <paramref name="direction"/> reaches (the punch cone when <paramref name="cone"/>, else the forward
    /// hemisphere; see <see cref="Combat"/>) with an unobstructed line of sight: each takes
    /// <paramref name="damage"/> and a shove at <paramref name="shoveSpeed"/>, and a landed one sprays
    /// <paramref name="shrapnel"/> and takes <paramref name="weapon"/>'s on-hit effect. Returns how many were struck.
    /// </summary>
    private int StrikeNearest(Vector3 origin, Vector3 direction, int damage, int maxTargets, bool cone,
        float shoveSpeed, PlayerHealth? striker, ShrapnelProfile shrapnel, Item? weapon)
    {
        GatherReached(origin, direction, cone);
        var struck = Math.Min(maxTargets, _strikeScratch.Count);
        for (var i = 0; i < struck; i++)
            Strikes.StrikeMob(_strikeScratch[i].Mob, damage, origin, striker, shoveSpeed, shrapnel, weapon);
        return struck;
    }

    /// <summary>Whether a blade swing from <paramref name="origin"/> along <paramref name="direction"/> would
    /// strike anything: some mob it reaches (the punch cone when <paramref name="cone"/>, else the forward
    /// hemisphere) with a clear line of sight. <paramref name="nearest"/> is the nearest such mob (null when
    /// nothing would be struck), which a client's predicted hit-sparks land on.</summary>
    private bool HasBladeTarget(Vector3 origin, Vector3 direction, bool cone, out Mob? nearest)
    {
        GatherReached(origin, direction, cone);
        nearest = _strikeScratch.Count > 0 ? _strikeScratch[0].Mob : null;
        return nearest != null;
    }

    // Fills the scratch list with every mob the swing reaches with a clear line of sight, nearest first.
    private void GatherReached(Vector3 origin, Vector3 direction, bool cone)
    {
        _strikeScratch.Clear();
        foreach (var mob in _mobs)
        {
            if (mob.AimPointFrom(origin) is not { } aim) continue;
            var reached = cone
                ? Combat.InPunchCone(origin, direction, aim, out var distance)
                : Combat.InFrontHemisphere(origin, direction, aim, out distance);
            if (!reached) continue;
            if (BlockRaycaster.SegmentHitsBlock(origin, aim, _chunkManager))
                continue;
            _strikeScratch.Add((distance, mob));
        }
        _strikeScratch.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
    }

    // One hit, one sound, from the block's cell (SfxLibrary.HitGroupOf)
    private void PlayHitSfx(ushort blockId, Vector3 cell) =>
        _world.Sounds.PlayAt(SfxLibrary.HitGroupOf(blockId), cell + new Vector3(0.5f));
}
