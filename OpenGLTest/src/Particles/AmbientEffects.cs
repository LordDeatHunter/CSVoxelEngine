using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Juice;
using VoxelEngine.Player;
using VoxelEngine.Simulation;

namespace VoxelEngine.Particles;

/// <summary>
/// The world's ambient cosmetic effects: steam off hardening lava and hot springs, the embers over
/// lava vents, the torch flames, the local player's bubbles, the water splashes of every body and
/// the camera shake of a blast. Never gameplay, so a headless server runs none of it. Owned by
/// <see cref="World"/>, which keeps the particle systems it feeds and draws them; main thread only.
/// </summary>
internal sealed class AmbientEffects
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly FireParticleSystem _fire;
    private readonly FireParticleSystem _torchFire;
    private readonly BubbleParticleSystem _bubbles;
    private readonly BlockDebrisSystem _debris;
    private readonly DropManager _dropManager;
    private readonly FallingBlockManager _falling;
    private readonly TntManager _tntManager;
    private readonly List<Mob> _mobs;
    private readonly List<ThrownProjectile> _projectiles;
    private readonly bool _serverMode;

    private Camera Camera => _world.Camera;

    internal AmbientEffects(World world, ChunkManager chunkManager, FireParticleSystem fire, FireParticleSystem torchFire,
        BubbleParticleSystem bubbles, BlockDebrisSystem debris, DropManager dropManager, FallingBlockManager falling,
        TntManager tntManager, List<Mob> mobs, List<ThrownProjectile> projectiles, bool serverMode)
    {
        _world = world;
        _chunkManager = chunkManager;
        _fire = fire;
        _torchFire = torchFire;
        _bubbles = bubbles;
        _debris = debris;
        _dropManager = dropManager;
        _falling = falling;
        _tntManager = tntManager;
        _mobs = mobs;
        _projectiles = projectiles;
        _serverMode = serverMode;
    }

    /// <summary>The exposed hot-fluid vents the lava embers burn over this frame
    /// (<see cref="GatherLavaVents"/>).</summary>
    internal IReadOnlyList<Vector3> LavaVents => _lavaVents;

    private readonly List<Vector3> _lavaVents = [];
    private readonly List<(float dist2, Chunk chunk)> _lavaVentChunks = [];

    private readonly List<Vector3> _torchFlames = [];
    private readonly List<(float dist2, Chunk chunk)> _torchFlameChunks = [];
    private static readonly Comparison<(float dist2, Chunk chunk)> NearestVentChunkFirst =
        static (a, b) => a.dist2.CompareTo(b.dist2);

    private readonly List<Vector3> _springVents = [];
    private readonly List<(float DistanceSquared, Chunk Chunk)> _springVentChunks = [];
    private float _springSteamDebt;

    /// <summary>
    /// Puffs steam (smoke on the campfire particle instance, <see cref="FireParticleSystem.Puff"/>)
    /// off the top of each cell in <paramref name="cells"/> that contact hardening just turned solid,
    /// for at most <see cref="WorldGenerationConfig.SteamPuffMaxCellsPerFrame"/> cells within
    /// <see cref="WorldGenerationConfig.FireParticleRadius"/> of the eye. Cosmetic: singleplayer and a client.
    /// </summary>
    internal void PuffSteam(IReadOnlyList<Vector3> cells)
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

    /// <summary>Shakes the local camera for a TNT blast at <paramref name="center"/>, by its distance
    /// (<see cref="ShakeFalloff"/>). Cosmetic: singleplayer and a client.</summary>
    internal void ShakeForBlast(Vector3 center) =>
        Camera.AddShake(ShakeFalloff.Trauma(WorldGenerationConfig.BlastShakeStrength, TntManager.BlastRadius,
            WorldGenerationConfig.BlastShakeRangeBlocks, Vector3.Distance(Camera.Position, center)));

    // The local player's water-splash memory (its swim state, Camera.IsInWater)
    private readonly WaterSplashTracker _playerSplash = new();

    /// <summary>
    /// Every body that crossed a water surface this frame throws droplets off it
    /// (<see cref="WaterSplash.Track"/>): the local player by its swim state, and the mobs, drops,
    /// projectiles, falling cubes and primed TNT by their lowest point. A client's mobs and TNT are
    /// mirrors that keep their trackers across snapshots and splash from the snapshot positions; its
    /// drops and falling cubes are rebuilt each snapshot, so they never do. Cosmetic: never on a server.
    /// </summary>
    internal void UpdateSplashes(float deltaTime)
    {
        if (_serverMode)
            return;
        WaterSplash.Track(_playerSplash, Camera.Position - new Vector3(0f, Camera.EyeHeight, 0f), deltaTime, 1f,
            _chunkManager, _debris, inWater: Camera.IsInWater);
        foreach (var mob in _mobs)
        {
            WaterSplash.Track(mob.Splash, mob.Position - new Vector3(0f, mob.BodyHalfHeight, 0f), deltaTime,
                mob.BodyHalfExtent / PlayerPhysics.PlayerRadius, _chunkManager, _debris);
        }
        // Index loops over the read-only lists, so a frame allocates no enumerator
        var drops = _dropManager.Drops;
        foreach (var itemDrop in drops)
            if (!itemDrop.IsAttracted)
                WaterSplash.Track(itemDrop.Splash, itemDrop.Position - new Vector3(0f, ItemDrop.Size * 0.5f, 0f),
                    deltaTime, WorldGenerationConfig.WaterSplashSmallScale, _chunkManager, _debris);

        foreach (var t in _projectiles)
            WaterSplash.Track(t.Splash, t.Position, deltaTime,
                WorldGenerationConfig.WaterSplashSmallScale, _chunkManager, _debris);

        var falling = _falling.Falling;
        foreach (var fallingBlock in falling)
            WaterSplash.Track(fallingBlock.Splash, fallingBlock.Position - new Vector3(0f, FallingBlock.HalfExtent, 0f),
                deltaTime, WorldGenerationConfig.WaterSplashCubeScale, _chunkManager, _debris);

        var primed = _tntManager.Primed;
        foreach (var primedTnt in primed)
            WaterSplash.Track(primedTnt.Splash, primedTnt.Position - new Vector3(0f, VoxelEngine.Entities.PrimedTnt.HalfExtent, 0f),
                deltaTime, WorldGenerationConfig.WaterSplashCubeScale, _chunkManager, _debris);
    }

    /// <summary>
    /// The local player's bubbles: the steady exhale while the eye is under, plus a burst from the
    /// head on each swim stroke (not in lava). Cosmetic, so singleplayer and a client run it and a
    /// server, whose hovering camera is nobody, does not.
    /// </summary>
    internal void UpdateBubbles(float deltaTime)
    {
        if (_serverMode)
            return;
        _bubbles.TrackSwimStrokes(Camera.SwimStrokeCount, Camera.Position,
            Vector3Utils.FromYawPitch(Camera.PlayerYaw, 0f), Camera.IsEyeInWater && !Camera.IsEyeInLava);
        _bubbles.Update(deltaTime, Camera.RenderEye, Camera.ViewDirection, Camera.IsEyeInWater, _chunkManager);
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
    internal void GatherLavaVents(Vector3 eye)
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
            candidates += chunk.HotFluidVents!.Count(idx => Vector3.DistanceSquared(origin + Chunk.LocalOfIdx(idx), eye) <= radius * radius);
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
    /// The hot-spring steam: gathers every spring water top (<see cref="Chunk.SpringVents"/>) within
    /// <see cref="WorldGenerationConfig.SpringSteamRadius"/> of the eye, nearest chunks first and at
    /// most <see cref="WorldGenerationConfig.SpringSteamMaxVents"/> of them, and puffs a pale
    /// <see cref="FireParticleKind.Steam"/> wisp over each at
    /// <see cref="WorldGenerationConfig.SpringSteamPerSecond"/> per vent (the campfire instance's
    /// <see cref="FireParticleSystem.Puff"/>, like the hardening-lava steam). Cosmetic: a headless
    /// server never runs it.
    /// </summary>
    internal void UpdateSpringSteam(float deltaTime, Vector3 eye)
    {
        _springVents.Clear();
        _springVentChunks.Clear();
        const float radius = WorldGenerationConfig.SpringSteamRadius;
        const float chunkReach = radius + Chunk.Size;
        const float half = Chunk.Size * 0.5f;
        foreach (var chunk in _chunkManager.Renderable)
        {
            if (chunk.SpringVents is null) continue;
            var d2 = Vector3.DistanceSquared(chunk.Position * Chunk.Size + new Vector3(half), eye);
            if (d2 > chunkReach * chunkReach) continue;
            _springVentChunks.Add((d2, chunk));
        }
        if (_springVentChunks.Count == 0)
        {
            _springSteamDebt = 0f;
            return;
        }

        _springVentChunks.Sort(NearestVentChunkFirst);
        foreach (var (_, chunk) in _springVentChunks)
        {
            var origin = chunk.Position * Chunk.Size;
            foreach (var idx in chunk.SpringVents!)
            {
                var cell = origin + Chunk.LocalOfIdx(idx);
                if (Vector3.DistanceSquared(cell, eye) > radius * radius) continue;
                _springVents.Add(cell);
                if (_springVents.Count >= WorldGenerationConfig.SpringSteamMaxVents) break;
            }
            if (_springVents.Count >= WorldGenerationConfig.SpringSteamMaxVents) break;
        }

        _springSteamDebt += deltaTime * WorldGenerationConfig.SpringSteamPerSecond * _springVents.Count;
        var puffs = (int)_springSteamDebt;
        if (puffs <= 0) return;
        _springSteamDebt -= puffs;
        puffs = Math.Min(puffs, WorldGenerationConfig.SpringSteamMaxPuffsPerFrame);
        for (var i = 0; i < puffs; i++)
        {
            var vent = _springVents[Random.Shared.Next(_springVents.Count)];
            _fire.Puff(vent + new Vector3(0f, 1f, 0f), WorldGenerationConfig.SpringSteamCount,
                WorldGenerationConfig.SpringSteamSpread, FireParticleKind.Steam);
        }
    }

    /// <summary>
    /// The torch flames: gathers every flame point (<see cref="Chunk.FlamePoints"/>) within
    /// <see cref="WorldGenerationConfig.TorchFlameRadius"/> of the eye, nearest chunks first and at most
    /// <see cref="WorldGenerationConfig.TorchFlameMaxEmitters"/> of them, and burns them in the torch
    /// particle instance, drifting with <paramref name="wind"/>. No point lights: a torch's own block
    /// light already lights it. Cosmetic: a headless server never runs it.
    /// </summary>
    internal void UpdateTorchFlames(float deltaTime, Vector2 wind)
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
        foreach (var chunk in chunks)
        {
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
}
