using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Entities;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Interaction;
using VoxelEngine.Juice;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Camps;
using VoxelEngine.Simulation.Caves;
using VoxelEngine.Simulation.Icebergs;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.Status;

namespace VoxelEngine.Simulation;

/// <summary>
/// The per-frame mob job: the players the mobs chase (the local camera, or every connected player on a
/// server), the slime and goblin updates with the damage they deal billed to the local player's pool, the
/// TNT blast shove of the players and mobs, the rebuild of the shared mob list (<see cref="World.Mobs"/>)
/// with its scenario seams, and the icebergs (not mobs, but driven by the same targets). It also runs the battle
/// towers' floor locks and roof altars, whose ritual raises the Skeleton King. Owned by <see cref="World"/>, which runs
/// <see cref="Update"/> inside its entity update; main thread only.
/// </summary>
internal sealed class MobDirector
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly DropManager _dropManager;
    private readonly SlimeManager? _slimes;
    private readonly GoblinManager? _goblins;
    private readonly HellMobManager? _hellMobs;
    private readonly SkyMobManager? _skyMobs;
    private readonly WildlifeManager? _wildlife;
    private readonly CaveMobManager? _caveMobs;
    private readonly SkeletonManager? _skeletons;
    private readonly SkeletonKingManager? _kings;
    private readonly SandwormManager? _sandworms;
    private readonly IcebergManager? _icebergs;
    private readonly BlockDebrisSystem _debris;
    // The campfire particles a fire bolt's ember trail sheds into; null on a server, which draws nothing
    private readonly FireParticleSystem? _embers;
    private readonly PlayerHealth _health;
    private readonly List<Mob> _mobs;
    private readonly bool _serverMode;
    private readonly bool _clientMode;
    // Where the bodies' status effects convert cells; null on a client, whose effects are mirrors
    private readonly StatusEffectBlockWriter? _statusBlocks;
    // The players the mob managers chase this frame, rebuilt right before they run.
    private readonly List<IMobTarget> _mobTargets = [];
    // The night's goblin camp; null without goblins and on a client, which never pitches one
    private readonly GoblinCamps? _camps;
    private readonly CampfireManager _campfires;
    private readonly MobCaps _caps;

    private Camera Camera => _world.Camera;
    private IReadOnlyList<ServerPlayer> ServerPlayers => _world.ServerPlayers;
    private double LightingTime => _world.LightingTime;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;
    private MobStrikes Strikes => _world.Strikes;

    internal MobDirector(World world, ChunkManager chunkManager, DropManager dropManager, SlimeManager? slimes,
        GoblinManager? goblins, HellMobManager? hellMobs, SkyMobManager? skyMobs, CaveMobManager? caveMobs,
        SkeletonManager? skeletons, SkeletonKingManager? kings, SandwormManager? sandworms, IcebergManager? icebergs, BlockDebrisSystem debris,
        FireParticleSystem? embers, PlayerHealth health, List<Mob> mobs, bool serverMode, bool clientMode,
        StatusEffectBlockWriter? statusBlocks, CampfireManager campfires, MobCaps caps, WildlifeManager? wildlife)
    {
        _wildlife = wildlife;
        _campfires = campfires;
        _caps = caps;
        if (goblins != null && !clientMode) _camps = new GoblinCamps(goblins, PitchCampfire);
        _hellMobs = hellMobs;
        _skyMobs = skyMobs;
        _caveMobs = caveMobs;
        _skeletons = skeletons;
        _kings = kings;
        _sandworms = sandworms;
        _embers = embers;
        _statusBlocks = statusBlocks;
        _world = world;
        _chunkManager = chunkManager;
        _dropManager = dropManager;
        _slimes = slimes;
        _goblins = goblins;
        _icebergs = icebergs;
        _debris = debris;
        _health = health;
        _mobs = mobs;
        _serverMode = serverMode;
        _clientMode = clientMode;
    }

    /// <summary>
    /// A goblin camp's lit fire on ground cell (<paramref name="x"/>, <paramref name="groundY"/>, <paramref name="z"/>):
    /// a full branch pile placed on top through the world's placement path (every placement hook runs) and lit to burn
    /// the night (<see cref="MobConfig.GoblinCampFireSeconds"/>). False when the cell above will not take it.
    /// </summary>
    private bool PitchCampfire(int x, int groundY, int z)
    {
        var pile = ContentRegistry.RequireBlock("branch_pile");
        if (!_world.Placer.PlaceBlockAtFace(new Vector3(x, groundY, z), Vector3.UnitY, pile.Id, pile.StackMax, Vector3.UnitZ,
                out var cell))
            return false;
        _campfires.Ignite((int)cell.X, (int)cell.Y, (int)cell.Z, MobConfig.GoblinCampFireSeconds);
        return true;
    }

    /// <summary>The scenario's <c>goblincamp</c> step: pitches a camp at once on the ground of column
    /// (<paramref name="x"/>, <paramref name="z"/>), night or not; how many goblins rose, -1 when none could be pitched.</summary>
    internal int PitchGoblinCamp(int x, int z)
    {
        if (_camps == null || !_world.TryGroundHeight(x, z, out var groundY)) return -1;
        return _camps.Pitch(_chunkManager, x, groundY, z);
    }

    /// <summary>
    /// Rebuilds the players the mobs chase (and the icebergs answer to) this frame. Singleplayer: the
    /// local camera, whose TakeHit applies only the knockback, since the mob damage still lands on
    /// this world's own pool. A server: every connected player, each taking its hits on its own pool
    /// (ServerPlayer.TakeHit), never the hovering camera, so nobody online means nobody chased.
    /// </summary>
    internal IReadOnlyList<IMobTarget> RefreshTargets()
    {
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
        return _mobTargets;
    }

    /// <summary>
    /// The icebergs' frame in the authoritative world: they drift, are pushed and live out their life
    /// around the targets, then (singleplayer only; a server's camera is nobody) the local player is
    /// carried by the berg it rides and pushed out of any it is left inside.
    /// </summary>
    internal void UpdateIcebergs(double deltaTime)
    {
        if (_icebergs == null)
            return;
        _icebergs.Update((float)deltaTime, _chunkManager, RefreshTargets(), LightingTime);
        if (!_serverMode)
            _icebergs.CarryAndPushOut(Camera, _chunkManager);
    }

    /// <summary>A client's iceberg mirrors between snapshots: they dead-reckon, and carry the local player.</summary>
    internal void AnimateIcebergMirrors(double deltaTime)
    {
        if (_icebergs == null)
            return;
        _icebergs.AnimateMirrors((float)deltaTime);
        _icebergs.CarryAndPushOut(Camera, _chunkManager);
    }

    /// <summary>The mob part of the world's entity update: rebuilds the targets, runs the slime, goblin, hell, sky,
    /// skeleton, Skeleton King and sandworm managers and bills the damage they dealt, ticks the towers' locks and altars,
    /// then converts the cells the bodies' status effects pulsed (<see cref="ApplyStatusBlockActions"/>).</summary>
    internal void Update(double deltaTime)
    {
        RefreshTargets();
        // The natural cap grows with the players in play, and a server's players report only positions,
        // so their headings (which the ring spawners lead) are estimated here
        _caps.Players = _mobTargets.Count;
        if (_serverMode)
        {
            var players = ServerPlayers;
            for (var i = 0; i < players.Count; i++)
                players[i].ObserveMotion((float)deltaTime);
        }

        // Slimes hop at the player: an airborne touch bills damage, a grounded one is just
        // shoved aside. The manager only reports the amount; the health pool stays here (a
        // server's players already took theirs through TakeHit).
        if (_slimes != null)
        {
            // The lighting clock's night, so the ring rolls larger slimes after dark
            _slimes.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, DayCycle.NightFactor(LightingTime),
                _serverMode ? null : _debris);
            BillLocalPlayer(_slimes.DamageDealtThisFrame);
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
            BillLocalPlayer(_goblins.DamageDealtThisFrame);
            _camps?.Update((float)deltaTime, _chunkManager, _mobTargets, DayCycle.NightFactor(LightingTime));
        }

        // Hell's brutes slam and its casters throw fire bolts (which also set the target burning)
        if (_hellMobs != null)
        {
            _hellMobs.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, _serverMode ? null : _debris,
                _embers);
            BillLocalPlayer(_hellMobs.DamageDealtThisFrame);
        }

        // The animals of the wild graze, flee and, provoked, charge
        if (_wildlife != null)
        {
            _wildlife.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, _serverMode ? null : _debris);
            BillLocalPlayer(_wildlife.DamageDealtThisFrame);
        }

        // The sky's galeraptors dive and its golems slam a shockwave into the ground
        if (_skyMobs != null)
        {
            _skyMobs.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, _serverMode ? null : _debris);
            BillLocalPlayer(_skyMobs.DamageDealtThisFrame);
        }

        // The cave biomes' mobs: crystal shardlings throw chilling shards, fungal sporelings burst spores and the
        // magma deep's magmaborn set their target burning
        if (_caveMobs != null)
        {
            _caveMobs.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, _serverMode ? null : _debris,
                DayCycle.NightFactor(LightingTime));
            BillLocalPlayer(_caveMobs.DamageDealtThisFrame);
        }

        // The tower's skeletons rake up close and shoot arrows at range; they never spawn on their own
        if (_skeletons != null)
        {
            _skeletons.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, _serverMode ? null : _debris);
            BillLocalPlayer(_skeletons.DamageDealtThisFrame);
        }

        // The tower's piles raise skeletons, and its floor locks open once their floor is cleared
        // (a client's tower mirror has no writer and never ticks)
        if (!_clientMode)
            _world.Towers.Update((float)deltaTime, _chunkManager, _skeletons, _mobTargets,
                _world.Persistence.PendingTowerSkeletons, _world.Persistence.TakeDiscardedTowerSkeletons);

        // The Skeleton King slams, throws bone volleys and summons skeletons; then the altars run their rituals and
        // settle with their kings, after the king update so a kill (or a fade on an empty server) settles this frame
        if (!_clientMode)
        {
            if (_kings != null)
            {
                _kings.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, _serverMode ? null : _debris,
                    _skeletons);
                BillLocalPlayer(_kings.DamageDealtThisFrame);
            }
            _world.Towers.UpdateAltars((float)deltaTime, _kings, _world.Persistence.PendingKingFor);
        }

        // The sandworm boss hunts under the sand, erupts under its target and slides at it
        if (_sandworms != null)
        {
            _sandworms.Update((float)deltaTime, _chunkManager, _dropManager, _mobTargets, _serverMode ? null : _debris);
            BillLocalPlayer(_sandworms.DamageDealtThisFrame);
        }

        FireWearerPassives((float)deltaTime);
        ApplyStatusBlockActions();
    }

    // The on-damaged and low-health passives of every worn set: the local player's in singleplayer, each connected
    // player's on a server, firing on the mobs around them
    private void FireWearerPassives(float deltaTime)
    {
        if (_serverMode)
        {
            var players = ServerPlayers;
            for (var i = 0; i < players.Count; i++)
                GearPassives.Retaliate(players[i].Status, players[i].Position, _mobs, deltaTime, Random.Shared.NextSingle);
            return;
        }
        GearPassives.Retaliate(_health, Camera.Position, _mobs, deltaTime, Random.Shared.NextSingle);
    }

    // Singleplayer's pool takes what a manager dealt this frame (a server's players already took theirs
    // through TakeHit), with a shake sized by the hit
    private void BillLocalPlayer(int damage)
    {
        if (_serverMode || damage <= 0)
            return;
        _health.TakeDamage(damage, DamageSource.Mob, Camera.LastMobAttacker);
        Camera.AddShake(Math.Clamp(damage / 20f, 0.2f, 1f));
        LocalPlayer.NotifyPlayerHurt(fromMob: true);
    }

    /// <summary>
    /// Drains the block actions every body's status effects pulsed since the last frame (a burning
    /// body's ignite and thaw, a frostburned one's freeze) into <see cref="StatusEffectBlockWriter"/>,
    /// around the body's feet: each living mob, then each target (the local player, or every connected
    /// player). Authoritative worlds only; a client has no writer.
    /// </summary>
    private void ApplyStatusBlockActions()
    {
        if (_statusBlocks == null)
            return;
        _statusBlocks.BeginFrame();
        if (_slimes != null)
            ApplyMobBlockActions(_slimes.Slimes);
        if (_goblins != null)
            ApplyMobBlockActions(_goblins.Goblins);
        if (_hellMobs != null)
            ApplyMobBlockActions(_hellMobs.Mobs);
        if (_skyMobs != null)
            ApplyMobBlockActions(_skyMobs.Mobs);
        if (_wildlife != null)
            ApplyMobBlockActions(_wildlife.Mobs);
        if (_caveMobs != null)
            ApplyMobBlockActions(_caveMobs.Mobs);
        if (_skeletons != null)
            ApplyMobBlockActions(_skeletons.Mobs);
        if (_kings != null)
            ApplyMobBlockActions(_kings.Mobs);
        if (_sandworms != null)
            ApplyMobBlockActions(_sandworms.Worms);
        for (var i = 0; i < _mobTargets.Count; i++)
        {
            var target = _mobTargets[i];
            if (target.Effects is not { } effects)
                continue;
            var actions = effects.TakeBlockActions();
            if (actions != StatusEffectBlockAction.None)
                _statusBlocks.Apply(target.Eye - new Vector3(0f, Camera.EyeHeight, 0f), actions);
        }
    }

    // One manager's living mobs, with an indexed loop so a frame allocates no enumerator
    private void ApplyMobBlockActions<T>(IReadOnlyList<T> mobs) where T : Mob
    {
        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            if (mob.IsDying)
                continue;
            var actions = mob.Effects.TakeBlockActions();
            if (actions != StatusEffectBlockAction.None)
                _statusBlocks!.Apply(mob.Position - new Vector3(0f, mob.BodyHalfHeight, 0f), actions);
        }
    }

    /// <summary>
    /// A TNT blast at <paramref name="center"/> shoves the players and the living mobs in range away
    /// from it (<see cref="BlastImpulse"/>): the local camera in singleplayer, every connected player
    /// on a server (through <see cref="IMobTarget.TakeHit"/>, which queues the knockback the player's
    /// status carries to its client), a creative, flying or dead player being immune; a mob is shoved
    /// sideways, a flying mob along the whole impulse (<see cref="Mob.Shove"/>). The drops, falling cubes and other TNT the blast reached
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
            var push = mob.Flies ? impulse : impulse with { Y = 0f };
            if (push.LengthSquared() > 1e-6f)
                mob.Shove(push, push.Length());
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
            AddLiving(_slimes.Slimes);

        if (_goblins != null)
            AddLiving(_goblins.Goblins);

        if (_hellMobs != null)
            AddLiving(_hellMobs.Mobs);

        if (_skyMobs != null)
            AddLiving(_skyMobs.Mobs);

        if (_wildlife != null)
            AddLiving(_wildlife.Mobs);

        if (_caveMobs != null)
            AddLiving(_caveMobs.Mobs);

        if (_skeletons != null)
            AddLiving(_skeletons.Mobs);

        if (_kings != null)
            AddLiving(_kings.Mobs);

        if (_sandworms != null)
            AddLiving(_sandworms.Worms);
    }

    // Appends the mobs that are not dying, with an indexed loop so a frame allocates no enumerator.
    private void AddLiving<T>(IReadOnlyList<T> mobs) where T : Mob
    {
        for (var i = 0; i < mobs.Count; i++)
            if (!mobs[i].IsDying)
                _mobs.Add(mobs[i]);
    }

    /// <summary>
    /// Spawns one mob of the named entity kind standing with its feet at <paramref name="feet"/>
    /// (the scripted visual tests' <c>spawn</c> op), a forced spawn under the hard mob cap. False
    /// for an unknown name, a kind whose manager does not exist because its content is not
    /// registered, or a full hard cap. <paramref name="size"/> is a slime's size and
    /// <paramref name="tier"/> a cave mob's depth tier (other kinds ignore it); a cave mob given no tier (0) takes the depth tier of its feet (<see cref="CaveDifficulty.TierAt"/>).
    /// </summary>
    internal bool SpawnEntity(string name, Vector3 feet, int size = SlimeSize.Min, int tier = 0)
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
        if (_hellMobs != null && HellMobManager.HeightOf(name) > 0f)
        {
            if (_hellMobs.Spawn(name, feet + new Vector3(0f, HellMobManager.HeightOf(name) * 0.5f, 0f)) == null)
                return false;
            RefreshMobs();
            return true;
        }
        if (_wildlife != null && WildlifeSpecies.HeightOf(name) > 0f)
        {
            if (_wildlife.Spawn(name, feet + new Vector3(0f, WildlifeSpecies.HeightOf(name) * 0.5f, 0f)) == null)
                return false;
            RefreshMobs();
            return true;
        }
        if (_skyMobs != null && SkyMobManager.HeightOf(name) > 0f)
        {
            if (_skyMobs.Spawn(name, feet + new Vector3(0f, SkyMobManager.HeightOf(name) * 0.5f, 0f)) == null)
                return false;
            RefreshMobs();
            return true;
        }
        if (_caveMobs != null && CaveMobManager.HeightOf(name) > 0f)
        {
            // A scenario's tier overrides the depth tier
            var caveTier = tier > 0 ? tier : CaveDifficulty.TierAt(feet.Y);
            if (_caveMobs.Spawn(name, feet + new Vector3(0f, CaveMobManager.HeightOf(name) * 0.5f, 0f), caveTier) == null)
                return false;
            RefreshMobs();
            return true;
        }
        if (_skeletons != null && SkeletonManager.HeightOf(name) > 0f)
        {
            // The named kind's role, in an outfit rolled like a pile's
            var role = Skeleton.RoleOf(name);
            var outfit = SkeletonOutfit.Roll(role, Random.Shared.NextSingle(), Random.Shared.NextSingle());
            if (_skeletons.Spawn(feet + new Vector3(0f, SkeletonManager.HeightOf(name) * 0.5f, 0f), role, outfit) == null)
                return false;
            RefreshMobs();
            return true;
        }
        if (_kings != null && SkeletonKingManager.HeightOf(name) > 0f)
        {
            // A scenario king is bound to no tower: it leashes to where it was spawned
            if (_kings.Spawn(feet + new Vector3(0f, SkeletonKingManager.HeightOf(name) * 0.5f, 0f),
                    SkeletonKingHome.Unbound(feet)) == null)
                return false;
            RefreshMobs();
            return true;
        }
        if (string.Equals(name, Sandworm.EntityName, StringComparison.OrdinalIgnoreCase) && _sandworms != null)
        {
            // A scenario worm starts burrowed, its head one burrow depth under the given feet (the surface
            // point it will rise at: the terrain top for the [x, z] form of the spawn op)
            if (_sandworms.Spawn(new Vector3(feet.X, feet.Y - SandwormConfig.BurrowDepthBlocks, feet.Z)) == null)
                return false;
            RefreshMobs();
            return true;
        }
        if (string.Equals(name, "iceberg", StringComparison.OrdinalIgnoreCase) && _icebergs != null)
            return SpawnIceberg(feet, size);
        return false;
    }

    /// <summary>Whether the boss <paramref name="entity"/> could be woken at the solid <paramref name="cell"/> now
    /// (the summon item's rule, the same a client predicts).</summary>
    internal bool CanSummonBoss(string entity, Vector3 cell) =>
        _sandworms != null && string.Equals(entity, Sandworm.EntityName, StringComparison.OrdinalIgnoreCase)
        && _sandworms.CanSummonAt(_chunkManager, (int)cell.X, (int)cell.Y, (int)cell.Z);

    /// <summary>Wakes the boss <paramref name="entity"/> at the solid <paramref name="cell"/> when it can be
    /// (authoritative worlds only); false when refused.</summary>
    internal bool SummonBoss(string entity, Vector3 cell)
    {
        if (!CanSummonBoss(entity, cell)) return false;
        if (_sandworms!.Summon(_chunkManager, (int)cell.X, (int)cell.Y, (int)cell.Z) == null) return false;
        RefreshMobs();
        return true;
    }

    // How far above the given feet the scenario's iceberg spawn looks for the sea's top water cell
    // (the snapped feet of an ocean column stand on the sea floor, under the water)
    private const int IcebergWaterSearchBlocks = 64;

    /// <summary>
    /// Scenario seam: a berg of size class <paramref name="size"/> (1 the smallest) floating at the
    /// top water cell of the column under <paramref name="feet"/>, at scale 1, unturned and with a
    /// full life. The feet's own height is only where the search for the water starts.
    /// </summary>
    private bool SpawnIceberg(Vector3 feet, int size)
    {
        var x = MathUtils.FloorToInt(feet.X);
        var z = MathUtils.FloorToInt(feet.Z);
        var top = MathUtils.FloorToInt(feet.Y) + IcebergWaterSearchBlocks;
        for (var y = top; y > top - 2 * IcebergWaterSearchBlocks; y--)
        {
            if (!IcebergSpawner.TryWaterline(_chunkManager, x, y, z, out var waterline))
                continue;
            var variant = Math.Clamp(size - 1, 0, _icebergs!.VariantCount - 1);
            var seed = x * 73856093 ^ z * 19349663;
            return _icebergs.Spawn(_chunkManager, variant, 1f, 0, seed, new Vector3(x + 0.5f, waterline, z + 0.5f),
                IcebergConfig.LifeMaxSeconds) != null;
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
