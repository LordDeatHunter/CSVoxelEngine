using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Juice;
using VoxelEngine.Player;
using VoxelEngine.Simulation;

namespace VoxelEngine.Particles;

/// <summary>
/// The sandworm's cosmetic effects (never on a headless server): the thumping rumble of a summon, sand churned
/// up around the point it will surface at (more chips, faster and wider the closer it gets), the burst and boom
/// of the breach, the splash where it dives back under, the dust a dash drags along its body, and the
/// earthquake that shakes the camera harder the nearer the digging head is. Reads only what a mirror has too
/// (<see cref="Sandworm.Phase"/> and the brain signals), so a client plays the same show. Main thread only.
/// </summary>
internal sealed class SandwormEffects
{
    private readonly Camera _camera;
    private readonly BlockDebrisSystem _debris;
    private readonly ChunkManager _chunks;
    private readonly Dictionary<Sandworm, State> _states = [];
    private readonly List<Sandworm> _gone = [];

    private sealed class State
    {
        public SandwormPhase Phase;
        public float Chips;
        public float Pulse;
        public bool Seen;
    }

    internal SandwormEffects(Camera camera, BlockDebrisSystem debris, ChunkManager chunks)
    {
        _camera = camera;
        _debris = debris;
        _chunks = chunks;
    }

    /// <summary>Plays one frame of every live worm's effects.</summary>
    internal void Update(float deltaTime, IReadOnlyList<Sandworm> worms)
    {
        foreach (var state in _states.Values) state.Seen = false;
        foreach (var worm in worms)
        {
            if (worm.IsDying) continue;
            if (!_states.TryGetValue(worm, out var state))
                _states[worm] = state = new State { Phase = worm.Phase };
            state.Seen = true;
            Step(worm, state, deltaTime);
        }

        _gone.Clear();
        foreach (var (worm, state) in _states)
            if (!state.Seen) _gone.Add(worm);
        foreach (var worm in _gone) _states.Remove(worm);
    }

    private void Step(Sandworm worm, State state, float dt)
    {
        var head = worm.Position;
        var distance = Vector3.Distance(_camera.Position, head);
        if (worm.Phase != state.Phase) OnPhaseChange(worm, state, distance);
        state.Phase = worm.Phase;

        switch (worm.Phase)
        {
            case SandwormPhase.Thump:
                var rise = MathUtils.Clamp01(worm.Brain.PhaseSeconds / SandwormConfig.ThumpSeconds);
                Churn(state, head.X, head.Z, rise, dt);
                state.Pulse += dt;
                if (state.Pulse >= SandwormConfig.ThumpPulseSeconds)
                {
                    state.Pulse = 0f;
                    _camera.AddShake(ShakeFalloff.Trauma(SandwormConfig.ThumpShake * (0.5f + 0.5f * rise),
                        SandwormConfig.QuakeNearBlocks, SandwormConfig.QuakeRangeBlocks, distance));
                }
                break;
            case SandwormPhase.Dig:
                var point = worm.Brain.SurfacePoint;
                if (worm.Brain.Telegraph > 0f) Churn(state, point.X, point.Z, worm.Brain.Telegraph, dt);
                _camera.AddShake(SandwormEffectRules.HoldTrauma(SandwormEffectRules.QuakeTrauma(worm.Phase, distance), dt));
                break;
            case SandwormPhase.Dash:
                DustTrail(worm, state, dt);
                break;
        }
    }

    private void OnPhaseChange(Sandworm worm, State state, float distance)
    {
        var head = worm.Position;
        switch (worm.Phase)
        {
            case SandwormPhase.Breach:
                Burst(head.X, head.Z, SandwormConfig.BreachChips, SandwormConfig.TelegraphSpeedMax * 1.4f, SandwormConfig.TelegraphRadiusMax);
                _camera.AddShake(ShakeFalloff.Trauma(SandwormConfig.BreachShake, SandwormConfig.QuakeNearBlocks,
                    SandwormConfig.QuakeRangeBlocks * 1.4f, distance));
                break;
            case SandwormPhase.Dig when state.Phase is SandwormPhase.Air or SandwormPhase.Dash:
                Burst(head.X, head.Z, SandwormConfig.DiveChips, SandwormConfig.TelegraphSpeedMax, SandwormConfig.TelegraphRadiusMin * 2f);
                break;
        }
        state.Chips = 0f;
    }

    // Sand churned around (x, z) at the given 0..1 intensity: chips accumulate at its rate and are thrown low
    private void Churn(State state, float x, float z, float intensity, float dt)
    {
        state.Chips += SandwormEffectRules.ChipsPerSecond(intensity) * dt;
        var count = (int)state.Chips;
        state.Chips -= count;
        Sprinkle(x, z, count, SandwormEffectRules.ChipSpeed(intensity), SandwormEffectRules.Radius(intensity));
    }

    private void Burst(float x, float z, int count, float speed, float radius) => Sprinkle(x, z, count, speed, radius);

    private void DustTrail(Sandworm worm, State state, float dt)
    {
        state.Chips += SandwormConfig.DashChipsPerSecond * dt;
        var count = (int)state.Chips;
        state.Chips -= count;
        if (count <= 0) return;
        for (var i = 0; i < worm.Chain.Count; i += 3)
        {
            if (!worm.IsExposed(i)) continue;
            var at = worm.Chain[i];
            Sprinkle(at.X, at.Z, count, SandwormConfig.TelegraphSpeedMin * 2f, Sandworm.SegmentHalfExtent(i));
        }
    }

    // Throws count sand chips from random spots within radius of (x, z), low over the sand surface there
    private void Sprinkle(float x, float z, int count, float speed, float radius)
    {
        var random = Random.Shared;
        var sand = SandwormGround.SandId;
        for (var i = 0; i < count; i++)
        {
            var angle = random.NextSingle() * MathF.Tau;
            var reach = MathF.Sqrt(random.NextSingle()) * radius;
            var px = x + MathF.Cos(angle) * reach;
            var pz = z + MathF.Sin(angle) * reach;
            var surface = SandwormGround.At(_chunks, px, pz).SurfaceY;
            _debris.Puff(new Vector3(px, surface + 0.1f, pz), new Vector3(MathF.Floor(px), surface - 1f, MathF.Floor(pz)),
                sand, 1, speed * (0.6f + 0.8f * random.NextSingle()), SandwormConfig.SandChipSize, random);
        }
    }
}
