using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Juice;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Tower;

namespace VoxelEngine.Particles.Collapse;

/// <summary>
/// The cosmetic show of a defeated battle tower falling into its ruin (BT-8's <see cref="TowerCollapse"/>): every unbreakable
/// cell of a defeated tower that gives way (<see cref="OnCellChanged"/>) within <see cref="JuiceConfig.TowerCollapseEffectRangeBlocks"/>
/// of the camera crumbles into chips of itself, the ones near the wall that fall away to air with open air outside them throw
/// whole blocks off the tower (<see cref="CollapseDebris"/>, which burst into more chips when their life runs out), and the camera rumbles by its
/// distance to the nearest falling cell (<see cref="ShakeFalloff"/>), in overlapping pulses like the sandworm's thump and the
/// altar ritual's rumble, for as long as cells keep falling. Fed by the tower's own writer in singleplayer
/// (<see cref="TowerBlockWriter"/>'s <c>replaced</c> hook) and by the cells a client's journal brings
/// (<c>ClientMirror.ApplyNetworkBlockChanges</c>), the same rule on both, so a client plays the same show with no wire of
/// its own. Never built on a headless server. Main thread only.
/// </summary>
internal sealed class TowerCollapseEffects
{
    private readonly Camera _camera;
    private readonly BlockDebrisSystem _chips;
    private readonly ChunkManager _chunks;
    private readonly TowerManager _towers;
    private readonly Random _random;
    private readonly List<CollapseDebris> _debris = [];

    // What the per-second caps still allow: chips and launches owed but not yet spent
    private float _chipAllowance;
    private float _launchAllowance;
    // The rumble: the nearest falling cell seen since the last frame, how long the shake still holds and its pulse clock
    private Vector3 _rumbleAt;
    private float _rumbleNewDistance = float.MaxValue;
    private float _rumbleLeft;
    private float _rumbleClock;

    internal TowerCollapseEffects(Camera camera, BlockDebrisSystem chips, ChunkManager chunks, TowerManager towers,
        Random? random = null)
    {
        _camera = camera;
        _chips = chips;
        _chunks = chunks;
        _towers = towers;
        _random = random ?? Random.Shared;
    }

    /// <summary>The blocks the collapse has thrown off that are still flying or rolling.</summary>
    public IReadOnlyList<CollapseDebris> Debris => _debris;

    /// <summary>
    /// Whether a cell going from <paramref name="before"/> to <paramref name="after"/> shows as a collapse falling in: an
    /// unbreakable block (the tower's bricks, doors, piles and altar) giving way, which only the tower's own writer ever
    /// does, so another player's edit never shakes anyone. A see-through one (the roof dome's forcefield) has no rubble to
    /// throw and just winks out. The caller still checks that a defeated tower stands there.
    /// </summary>
    internal static bool ShowsCollapse(ushort before, ushort after) =>
        before != after && BlockType.IsUnbreakable(before) && !BlockType.ById[before].Translucent;

    /// <summary>
    /// One cell rewritten from <paramref name="before"/> to <paramref name="after"/> (the tower writer's hook in
    /// singleplayer, the journal on a client): when it is a defeated tower's collapse (<see cref="ShowsCollapse"/>, inside
    /// the footprint of a tracked tower whose king is killed) within the effect range, it feeds the rumble, crumbles into
    /// chips while the chip cap allows, and, when it fell away to air near the wall with the cell outside it open, may throw
    /// a block off the tower.
    /// </summary>
    internal void OnCellChanged(int x, int y, int z, ushort before, ushort after)
    {
        if (!ShowsCollapse(before, after)) return;
        if (_towers.RecordAt(x, z) is not { BossDefeated: true } tower) return;
        var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        var distance = Vector3.Distance(_camera.Position, center);
        if (distance >= JuiceConfig.TowerCollapseEffectRangeBlocks) return;

        if (distance < _rumbleNewDistance)
        {
            _rumbleNewDistance = distance;
            _rumbleAt = center;
        }

        var chips = Math.Min(JuiceConfig.TowerCollapseChipsPerCell, (int)_chipAllowance);
        if (chips > 0)
        {
            _chips.Puff(center, new Vector3(x, y, z), before, chips, JuiceConfig.TowerCollapseChipSpeed,
                JuiceConfig.TowerCollapseChipSize, _random, _chunks.ColumnSource);
            _chipAllowance -= chips;
        }

        if (after != BlockType.Air || _launchAllowance < 1f || _debris.Count >= JuiceConfig.TowerDebrisMax) return;
        var outward = new Vector2(center.X - (tower.CenterX + 0.5f), center.Z - (tower.CenterZ + 0.5f));
        if (outward.Length() < JuiceConfig.TowerDebrisMinRadiusShare * tower.OuterRadius || !OpenOutward(x, y, z, outward))
            return;
        _launchAllowance -= 1f;
        Launch(center, before, Vector2.Normalize(outward));
    }

    // Whether the cell beside (x, y, z) on the tower's outward side (along its main axis) is open, so a block thrown from it
    // clears the wall: one from deeper in the shell would hit the brick still standing outside it and sit on the stump
    private bool OpenOutward(int x, int y, int z, Vector2 outward) =>
        MathF.Abs(outward.X) >= MathF.Abs(outward.Y)
            ? !BlockType.IsSolid(_chunks.GetBlockGlobal(x + Math.Sign(outward.X), y, z))
            : !BlockType.IsSolid(_chunks.GetBlockGlobal(x, y, z + Math.Sign(outward.Y)));

    /// <summary>
    /// One frame: refills the per-second caps, holds the rumble, and moves every thrown block, bursting the ones whose life
    /// ran out into rubble chips (with a jolt for a camera close by).
    /// </summary>
    internal void Update(float deltaTime)
    {
        if (deltaTime <= 0f) return;
        _chipAllowance = Refill(_chipAllowance, JuiceConfig.TowerCollapseChipsPerSecond, deltaTime);
        _launchAllowance = Refill(_launchAllowance, JuiceConfig.TowerDebrisLaunchesPerSecond, deltaTime);
        Rumble(deltaTime);

        for (var i = _debris.Count - 1; i >= 0; i--)
        {
            var block = _debris[i];
            if (block.Update(deltaTime, _chunks)) continue;
            Burst(block);
            _debris[i] = _debris[^1];
            _debris.RemoveAt(_debris.Count - 1);
        }
    }

    // A cap's allowance after deltaTime at perSecond, never banking more than a quarter second's worth
    private static float Refill(float allowance, float perSecond, float deltaTime) =>
        MathF.Min(allowance + perSecond * deltaTime, MathF.Max(1f, perSecond * 0.25f));

    // Pulses the shake by the distance to the nearest falling cell while cells keep falling, and for the hold after: the
    // first pulse at once, then one every pulse period, overlapping as the camera decays each, so the view keeps shuddering
    private void Rumble(float deltaTime)
    {
        if (_rumbleNewDistance < float.MaxValue)
        {
            _rumbleLeft = JuiceConfig.TowerCollapseRumbleHoldSeconds;
            _rumbleNewDistance = float.MaxValue;
        }
        if (_rumbleLeft <= 0f)
        {
            _rumbleClock = 0f;
            return;
        }
        _rumbleLeft -= deltaTime;
        _rumbleClock -= deltaTime;
        if (_rumbleClock > 0f) return;
        _rumbleClock = JuiceConfig.TowerCollapseRumblePulseSeconds;
        _camera.AddShake(ShakeFalloff.Trauma(JuiceConfig.TowerCollapseRumbleStrength, JuiceConfig.TowerCollapseRumbleNearBlocks,
            JuiceConfig.TowerCollapseRumbleRangeBlocks, Vector3.Distance(_camera.Position, _rumbleAt)));
    }

    // Throws a block of blockId off the tower from the cell centered at center, away from the tower's middle along outward
    private void Launch(Vector3 center, ushort blockId, Vector2 outward)
    {
        var side = new Vector2(-outward.Y, outward.X) * ((_random.NextSingle() * 2f - 1f) * JuiceConfig.TowerDebrisSideSpeed);
        var horizontal = outward * Between(JuiceConfig.TowerDebrisOutwardSpeedMin, JuiceConfig.TowerDebrisOutwardSpeedMax) + side;
        var velocity = new Vector3(horizontal.X, _random.NextSingle() * JuiceConfig.TowerDebrisLiftMax, horizontal.Y);
        var axis = new Vector3(_random.NextSingle() * 2f - 1f, _random.NextSingle() * 2f - 1f, _random.NextSingle() * 2f - 1f);
        _debris.Add(new CollapseDebris(blockId, center, velocity,
            Between(JuiceConfig.TowerDebrisSizeMin, JuiceConfig.TowerDebrisSizeMax),
            Between(JuiceConfig.TowerDebrisLifetimeMin, JuiceConfig.TowerDebrisLifetimeMax), axis,
            Between(JuiceConfig.TowerDebrisTumbleMin, JuiceConfig.TowerDebrisTumbleMax)));
    }

    // A thrown block at the end of its life: rubble chips of it where it lies, and a jolt for a camera close by
    private void Burst(CollapseDebris block)
    {
        var (x, y, z) = block.Position.BlockCell();
        _chips.Puff(block.Position, new Vector3(x, y, z), block.BlockId, JuiceConfig.TowerDebrisBurstChips,
            JuiceConfig.TowerDebrisBurstSpeed, JuiceConfig.TowerCollapseChipSize, _random, _chunks.ColumnSource);
        _camera.AddShake(ShakeFalloff.Trauma(JuiceConfig.TowerDebrisBurstShake, JuiceConfig.TowerDebrisBurstShakeNearBlocks,
            JuiceConfig.TowerDebrisBurstShakeRangeBlocks, Vector3.Distance(_camera.Position, block.Position)));
    }

    private float Between(float min, float max) => min + (max - min) * _random.NextSingle();
}
