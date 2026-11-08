using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Config;
using VoxelEngine.Content;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation;

/// <summary>
/// The bone-shrapnel effect, owned by <see cref="World"/> and built only where the world is
/// authoritative (singleplayer and a server, never a client, whose hits are its server's): a landed melee
/// hit sprays <see cref="ShrapnelProfile"/> bone fragments out of the struck body, and each fragment
/// wounds the first other enemy it touches through <see cref="MobStrikes.StrikeMob"/>, so its damage,
/// shove, sparks and number are the weapon path's own. The fragments are ordinary
/// <see cref="ThrownProjectile"/>s of the <c>bone</c> item, so the renderer and the water mirror draw
/// them without new plumbing; the struck mob is spared, so a burst never doubles a blow on its source.
/// Main thread only.
/// </summary>
internal sealed class BoneShrapnel
{
    private readonly World _world;
    private readonly ChunkManager _chunks;
    private readonly BlockDebrisSystem _debris;
    private readonly bool _serverMode;
    private readonly byte _boneId;
    private readonly List<Fragment> _live = [];

    internal BoneShrapnel(World world, ChunkManager chunks, BlockDebrisSystem debris, bool serverMode)
    {
        _world = world;
        _chunks = chunks;
        _debris = debris;
        _serverMode = serverMode;
        // Resolved once: a fragment's sprite is the bone item's
        _boneId = ContentRegistry.RequireItem("bone").Id;
    }

    /// <summary>The fragments in flight, for tests and diagnostics.</summary>
    internal int LiveCount => _live.Count;

    /// <summary>
    /// Sprays one burst of <paramref name="profile"/> out of <paramref name="source"/>: the payload the strike
    /// path resolved from the weapon and the striker's worn charm (<see cref="ShrapnelProfile.Resolve"/>).
    /// </summary>
    internal void Burst(Mob source, ShrapnelProfile profile, PlayerHealth? striker)
    {
        if (profile.IsEmpty || !_world.IsPlaying) return;

        var center = source.Position;
        for (var i = 0; i < profile.Count; i++)
        {
            var azimuth = Random.Shared.NextSingle() * MathF.Tau;
            var elevation = ShrapnelConfig.MinElevationRadians
                            + Random.Shared.NextSingle() * (ShrapnelConfig.MaxElevationRadians - ShrapnelConfig.MinElevationRadians);
            var cosElevation = MathF.Cos(elevation);
            var direction = new Vector3(cosElevation * MathF.Cos(azimuth), MathF.Sin(elevation),
                cosElevation * MathF.Sin(azimuth));
            var speed = ShrapnelConfig.SpeedMin
                        + Random.Shared.NextSingle() * (ShrapnelConfig.SpeedMax - ShrapnelConfig.SpeedMin);
            Launch(center + direction * ShrapnelConfig.SpawnOffsetBlocks, direction * speed, profile.Damage, source, striker);
        }

        if (!_serverMode)
            _debris.Spark(center, ShrapnelConfig.BurstSparkColor, source.BodyHalfExtent,
                ShrapnelConfig.BurstSparkCount, ShrapnelConfig.BurstSparkSpeed, ShrapnelConfig.BurstSparkSize);
    }

    /// <summary>
    /// Launches one fragment from <paramref name="origin"/> along <paramref name="velocity"/> that deals
    /// <paramref name="damage"/> to the first enemy it touches, never <paramref name="source"/>.
    /// </summary>
    internal void Launch(Vector3 origin, Vector3 velocity, int damage, Mob source, PlayerHealth? striker)
    {
        if (_live.Count >= ShrapnelConfig.MaxLiveFragments) _live.RemoveAt(0);
        var projectile = new ThrownProjectile(_boneId, origin, velocity, damage, 1f, ShrapnelConfig.GravityScale);
        _live.Add(new Fragment(projectile, source, striker));
    }

    /// <summary>Flies every fragment one frame and bills what it hits; nothing while the world is not playing.</summary>
    internal void Update(float deltaTime)
    {
        if (_live.Count == 0 || !_world.IsPlaying) return;
        var mobs = _world.Mobs;
        for (var i = _live.Count - 1; i >= 0; i--)
        {
            var fragment = _live[i];
            fragment.Age += deltaTime;
            if (fragment.Age >= ShrapnelConfig.LifeSeconds)
            {
                _live.RemoveAt(i);
                continue;
            }

            var flying = fragment.Projectile.Update(deltaTime, _chunks, mobs, out var hit, fragment.Source);
            if (hit != null)
            {
                _world.Strikes.StrikeMob(hit, fragment.Projectile.Damage, fragment.Projectile.Origin, fragment.Striker);
                _live.RemoveAt(i);
                continue;
            }

            if (!flying) _live.RemoveAt(i);
        }
    }

    /// <summary>Appends the live fragments to a frame's projectile list, so the existing thrown-object draw
    /// renders them (and the water mirror with it).</summary>
    internal void AppendTo(List<ThrownProjectile> target)
    {
        for (var i = 0; i < _live.Count; i++)
            target.Add(_live[i].Projectile);
    }

    private sealed class Fragment(ThrownProjectile projectile, Mob source, PlayerHealth? striker)
    {
        internal ThrownProjectile Projectile { get; } = projectile;
        internal Mob Source { get; } = source;
        internal PlayerHealth? Striker { get; } = striker;
        internal float Age { get; set; }
    }
}
