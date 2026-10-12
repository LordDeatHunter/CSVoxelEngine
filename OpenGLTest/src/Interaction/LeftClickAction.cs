using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using VoxelEngine.Juice;
using VoxelEngine.Networking;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Interaction;

/// <summary>
/// The left-click action and the target it works on: the crosshair raycast that picks the
/// targeted block (with the face, box and aim a client's intents carry), hit-based mining with its
/// pop and break overlay, the attack-first punch and the blade's slice combo, the HUD crosshair and
/// look-at label read from them, and the punch and slice a server lands for a connected player.
/// Owned by <see cref="World"/>; main thread only.
/// </summary>
internal sealed class LeftClickAction
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly BlockDebrisSystem _debris;
    private readonly HitMarker _hitMarker;
    private readonly List<Mob> _mobs;
    private readonly SfxManager? _sfx;
    private readonly bool _clientMode;

    private bool IsPlaying => _world.IsPlaying;
    private Camera Camera => _world.Camera;
    private PlayerModel Player => _world.Player;
    private Inventory Inventory => _world.Inventory;
    private BlockEditor Edits => _world.Edits;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;
    private MobStrikes Strikes => _world.Strikes;

    internal LeftClickAction(World world, ChunkManager chunkManager, BlockDebrisSystem debris, HitMarker hitMarker,
        List<Mob> mobs, SfxManager? sfx, bool clientMode)
    {
        _world = world;
        _chunkManager = chunkManager;
        _debris = debris;
        _hitMarker = hitMarker;
        _mobs = mobs;
        _sfx = sfx;
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
    internal byte BreakingBlockId => _breakingBlockId;

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

    // The head ray the selection raycast fired along (Camera.LastAimDirection), which a client's
    // break intents carry so the server's first-person ray lands on the same cell.
    private Vector3 _selectedAimDirection = -Vector3.UnitZ;

    // A connected client's creative mining pace: one block per ClientCreativeBreakSeconds while
    // the button is held (singleplayer mines one per frame). Infinity while released, so a click
    // breaks at once.
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
            var blockId = Edits.BlockIdAt(_selectedBlockPosition);
            var state = LookLabel.For(blockId, Camera.IsCreative, Inventory.Knows, HeldItem());
            if (state.Text.Length == 0) return null;
            var (x, y, z) = _selectedBlockPosition.BlockCell();
            return new LookLabelState(state.Text, state.Known, state.Mineable, _selectedBlockPosition,
                blockId, _chunkManager.GetStateGlobal(x, y, z),
                EntityLight.Sample(_chunkManager, _selectedBlockPosition));
        }
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
        Strikes.StrikeMob(target, Combat.PunchDamage(HeldItem(player.Inventory)), player.Eye);
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
        return ApplySliceDamage(player.Eye, player.ViewDirection, HeldItem(player.Inventory), returnSwing);
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
    /// crosshair still swings the fist. Creative instamines and ignores the gate.
    /// Call after Update so the selection is current. Does nothing unless the world is playing:
    /// nothing may edit an unloaded world.
    /// </summary>
    internal void UpdateBlockBreaking(double deltaTime, bool isHolding)
    {
        if (!IsPlaying) return;
        var justPressed = isHolding && !_attackWasPressed;
        _attackWasPressed = isHolding;

        var held = HeldItem();
        var player = LocalPlayer.LocalContext();

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
        if (BlockType.IsFluid(Edits.BlockIdAt(_selectedBlockPosition)))
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
            LocalPlayer.PunchSwing();
            PlayHitSfx(Edits.BlockIdAt(_selectedBlockPosition));
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
            _breakingBlockId = Edits.BlockIdAt(_selectedBlockPosition);
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
            LocalPlayer.PunchSwing();
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
        Edits.RemoveBlock(cell, player);
        _world.RaiseIntent(WorldIntent.BreakComplete(Camera.Position, _selectedAimDirection, cell));
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
            var weight = LocalPlayer.PunchSwing();
            if (_clientMode)
            {
                _world.RaiseIntent(WorldIntent.AttackWith(player.Eye, player.ViewDirection, NetAttackKind.Punch));
                // The predicted connect sparks too, like the hit-stop's freeze below
                Strikes.SpawnHitSparks(target, player.Eye);
            }
            else
                Strikes.StrikeMob(target, Combat.PunchDamage(held), player.Eye);
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
            if (!Combat.InPunchCone(origin, direction, mob.Position, out var distance))
                continue;
            if (BlockRaycaster.SegmentHitsBlock(origin, mob.Position, _chunkManager))
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
                _world.RaiseIntent(WorldIntent.AttackWith(player.Eye, player.ViewDirection,
                    returnSwing ? NetAttackKind.SliceReturn : NetAttackKind.Slice));
                // Predicted, like the punch's: the server lands the swing on its own mobs
                connected = HasSliceTarget(player.Eye, player.ViewDirection, out var predicted);
                if (predicted != null) Strikes.SpawnHitSparks(predicted, player.Eye);
            }
            else
            {
                connected = ApplySliceDamage(player.Eye, player.ViewDirection, HeldItem(), returnSwing) > 0;
            }
            // A connecting swing holds at the impact; it still ends on time, so the combo's next
            // swing is never delayed
            if (connected)
                Player.HitStop(SwingHitStop.SecondsFor(0f));
                _hitMarker.Show();
        }

        if (Player.IsSlicing)
            return;

        if (!_sliceCombo.Advance()) return;
        Player.StartSlice(_sliceCombo.Direction);
        _sliceImpactDone = false;
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

        foreach (var mob in mobs)
        {
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
            Strikes.StrikeMob(first, damage, origin);
            struck++;
        }

        if (second == null) return struck;
        Strikes.StrikeMob(second, damage, origin);
        struck++;
        return struck;
    }

    /// <summary>Whether a slice from <paramref name="origin"/> along <paramref name="direction"/> would
    /// strike anything: some mob in the forward hemisphere with a clear line of sight, the targets
    /// <see cref="ApplySliceDamage"/> picks from. <paramref name="first"/> is that mob (null when
    /// nothing would be struck), which a client's predicted hit-sparks land on.</summary>
    private bool HasSliceTarget(Vector3 origin, Vector3 direction, out Mob? first)
    {
        first = null;
        foreach (var mob in _mobs)
        {
            if (!Combat.InFrontHemisphere(origin, direction, mob.Position, out _)
                || BlockRaycaster.SegmentHitsBlock(origin, mob.Position, _chunkManager)) continue;
            first = mob;
            return true;
        }
        return false;
    }

    // One hit, one sound: an unknown or air id falls back to the default group, which is what
    // a block with no authored sfx plays too. A null manager (headless/tests) is silent.
    private void PlayHitSfx(byte blockId) =>
        _sfx?.Play(BlockType.ById[blockId]?.Sfx ?? SfxLibrary.DefaultGroup);
}
