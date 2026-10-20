using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Interaction;
using VoxelEngine.Juice;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation;

/// <summary>
/// The per-frame mob job: the players the mobs chase (the local camera, or every connected player on a
/// server), the slime and goblin updates with the damage they deal billed to the local player's pool, the
/// TNT blast shove of the players and mobs, and the rebuild of the shared mob list (<see cref="World.Mobs"/>)
/// with its scenario seams. Owned by <see cref="World"/>, which runs <see cref="Update"/> inside its entity
/// update; main thread only.
/// </summary>
internal sealed class MobDirector
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly DropManager _dropManager;
    private readonly SlimeManager? _slimes;
    private readonly GoblinManager? _goblins;
    private readonly BlockDebrisSystem _debris;
    private readonly PlayerHealth _health;
    private readonly List<Mob> _mobs;
    private readonly bool _serverMode;
    private readonly bool _clientMode;
    // The players the mob managers chase this frame, rebuilt right before they run.
    private readonly List<IMobTarget> _mobTargets = [];

    private Camera Camera => _world.Camera;
    private IReadOnlyList<ServerPlayer> ServerPlayers => _world.ServerPlayers;
    private double LightingTime => _world.LightingTime;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;
    private MobStrikes Strikes => _world.Strikes;

    internal MobDirector(World world, ChunkManager chunkManager, DropManager dropManager, SlimeManager? slimes,
        GoblinManager? goblins, BlockDebrisSystem debris, PlayerHealth health, List<Mob> mobs, bool serverMode,
        bool clientMode)
    {
        _world = world;
        _chunkManager = chunkManager;
        _dropManager = dropManager;
        _slimes = slimes;
        _goblins = goblins;
        _debris = debris;
        _health = health;
        _mobs = mobs;
        _serverMode = serverMode;
        _clientMode = clientMode;
    }

    /// <summary>The mob part of the world's entity update: rebuilds the targets, then runs the slime and goblin managers and bills the damage they dealt.</summary>
    internal void Update(double deltaTime)
    {
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
                _health.TakeDamage(_slimes.DamageDealtThisFrame, DamageSource.Mob, Camera.LastMobAttacker);
                Camera.AddShake(Math.Clamp(_slimes.DamageDealtThisFrame / 20f, 0.2f, 1f));
                LocalPlayer.NotifyPlayerHurt(fromMob: true);
            }
        }

        // Goblins lob their flint and then close in for a club swing; like the slimes, the
        // manager only reports the damage and the health pool stays here.
        if (_goblins != null)
        {
            _goblins.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, _serverMode ? null : _debris,
                _slimes?.Slimes,
                aimOrigin: _serverMode || _clientMode ? null : Camera.Position,
                aimDirection: _serverMode || _clientMode ? null : Camera.LastAimDirection,
                aimCharge: _serverMode || _clientMode ? 0f : Camera.ThrowCharge ?? 0f,
                night: DayCycle.NightFactor(LightingTime));
            var goblinDamage = _goblins.DamageDealtThisFrame;
            if (!_serverMode && goblinDamage > 0)
            {
                _health.TakeDamage(goblinDamage, DamageSource.Mob, Camera.LastMobAttacker);
                Camera.AddShake(Math.Clamp(goblinDamage / 20f, 0.2f, 1f));
                LocalPlayer.NotifyPlayerHurt(fromMob: true);
            }
        }
    }

    /// <summary>
    /// A TNT blast at <paramref name="center"/> shoves the players and the living mobs in range away
    /// from it (<see cref="BlastImpulse"/>): the local camera in singleplayer, every connected player
    /// on a server (through <see cref="IMobTarget.TakeHit"/>, which queues the knockback the player's
    /// status carries to its client), a creative, flying or dead player being immune; a mob is shoved
    /// sideways (<see cref="Mob.Shove"/>). The drops, falling cubes and other TNT the blast reached
    /// were already flung by <see cref="TntManager"/>. Gameplay: singleplayer and the server.
    /// </summary>
    internal void PushFromBlast(Vector3 center)
    {
        if (_serverMode)
        {
            var players = ServerPlayers;
            for (var i = 0; i < players.Count; i++)
                PushTargetFromBlast(players[i], center);
        }
        else
        {
            PushTargetFromBlast(Camera, center);
        }

        foreach (var mob in _mobs)
        {
            if (mob.IsDying) continue;
            var impulse = BlastImpulse.At(center, mob.Position, TntManager.BlastRadius);
            var sideways = impulse with { Y = 0f };
            if (sideways.LengthSquared() > 1e-6f)
                mob.Shove(sideways, sideways.Length());
        }
    }

    // One player's share of a blast, aimed at its body's middle rather than its eye
    private static void PushTargetFromBlast(IMobTarget target, Vector3 center)
    {
        if (target.IsImmune) return;
        var body = target.Eye - new Vector3(0f, Camera.EyeHeight * 0.5f, 0f);
        var impulse = BlastImpulse.At(center, body, TntManager.BlastRadius);
        if (impulse == Vector3.Zero) return;
        var sideways = impulse with { Y = 0f };
        target.TakeHit(0, sideways, sideways.Length(), MathF.Max(0f, impulse.Y));
    }

    /// <summary>
    /// Rebuilds <see cref="World.Mobs"/> in place from every mob manager, leaving the dying mobs out.
    /// </summary>
    internal void RefreshMobs()
    {
        _mobs.Clear();
        if (_slimes != null)
            _mobs.AddRange(_slimes.Slimes.Where(t => !t.IsDying));

        if (_goblins != null)
            _mobs.AddRange(_goblins.Goblins.Where(t => !t.IsDying));
    }

    /// <summary>
    /// Spawns one mob of the named entity kind standing with its feet at <paramref name="feet"/>
    /// (the scripted visual tests' <c>spawn</c> op), a forced spawn under the hard mob cap. False
    /// for an unknown name, a kind whose manager does not exist because its content is not
    /// registered, or a full hard cap. <paramref name="size"/> is a slime's size (other kinds
    /// have none and ignore it).
    /// </summary>
    internal bool SpawnEntity(string name, Vector3 feet, int size = SlimeSize.Min)
    {
        if (string.Equals(name, "slime", StringComparison.OrdinalIgnoreCase))
        {
            var center = feet + new Vector3(0f, Slime.HalfExtentFor(size), 0f);
            if (_slimes?.Spawn(center, size) == null) return false;
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

    /// <summary>Scenario seam: damages every live mob, so a script can film the hit reaction and the death.</summary>
    internal void DamageAllMobs(int amount)
    {
        RefreshMobs();
        foreach (var mob in _mobs)
        {
            var hits = mob.HitsTaken;
            mob.TakeDamage(amount);
            Strikes.ShowLandedHit(mob, hits);
        }
    }
}
