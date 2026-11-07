using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Interaction;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Icebergs;

namespace VoxelEngine.Networking.Client;

/// <summary>
/// The multiplayer client's mirror of its server: applies the server's cell changes, its entity
/// snapshots (mobs and drops, plus the primed TNT and falling cubes it mirrors for display only),
/// its station snapshots and the player's health, and keeps the remote player set in step with the
/// player list. Owned by <see cref="World"/>; main thread only.
/// </summary>
internal sealed class ClientMirror
{
    private readonly World _world;
    private readonly ChunkManager _chunkManager;
    private readonly DropManager _dropManager;
    private readonly TntManager _tntManager;
    private readonly FallingBlockManager _falling;
    private readonly SlimeManager? _slimes;
    private readonly GoblinManager? _goblins;
    private readonly HellMobManager? _hellMobs;
    private readonly SkyMobManager? _skyMobs;
    private readonly CampfireManager _campfires;
    private readonly QuernManager _querns;
    private readonly KilnManager _kilns;
    private readonly CastingManager _casting;
    private readonly BlockDebrisSystem _debris;
    private readonly PlayerHealth _health;
    private readonly List<RemotePlayer> _remotePlayers;
    private readonly bool _clientMode;
    private readonly bool _serverMode;

    private Camera Camera => _world.Camera;
    private PlayerModel Player => _world.Player;
    private AmbientEffects Ambient => _world.Ambient;
    private MobStrikes Strikes => _world.Strikes;
    private LocalPlayer LocalPlayer => _world.LocalPlayer;

    internal ClientMirror(World world, ChunkManager chunkManager, DropManager dropManager, TntManager tntManager,
        FallingBlockManager falling, SlimeManager? slimes, GoblinManager? goblins, HellMobManager? hellMobs,
        SkyMobManager? skyMobs, IcebergManager? icebergs,
        CampfireManager campfires, QuernManager querns, KilnManager kilns, CastingManager casting,
        BlockDebrisSystem debris, PlayerHealth health, List<RemotePlayer> remotePlayers, bool clientMode,
        bool serverMode)
    {
        _world = world;
        _chunkManager = chunkManager;
        _dropManager = dropManager;
        _tntManager = tntManager;
        _falling = falling;
        _slimes = slimes;
        _goblins = goblins;
        _hellMobs = hellMobs;
        _skyMobs = skyMobs;
        _icebergs = icebergs;
        _campfires = campfires;
        _querns = querns;
        _kilns = kilns;
        _casting = casting;
        _debris = debris;
        _health = health;
        _remotePlayers = remotePlayers;
        _clientMode = clientMode;
        _serverMode = serverMode;
    }

    private readonly HashSet<int> _remoteIdsSeen = [];

    // Multiplayer client: server-authoritative mobs by network id, and the reconcile scratch.
    private readonly Dictionary<int, Mob> _netMobs = [];
    private readonly HashSet<int> _netMobsSeen = [];
    private readonly List<int> _netMobRemovals = [];
    private readonly HashSet<Vector3> _netTouchedChunks = [];
    // Multiplayer client: the cells of a block-change batch that are lava hardening, for the steam
    private readonly List<Vector3> _netHardened = [];

    // Multiplayer client: the server's primed TNT by network id (display-only mirrors, so a cube
    // keeps its strobe clock across snapshots), its reconcile scratch, and the falling cubes of the
    // newest snapshot, rebuilt wholesale.
    private readonly Dictionary<int, PrimedTnt> _netTnt = [];
    private readonly HashSet<int> _netTntSeen = [];
    private readonly List<int> _netTntRemovals = [];
    private readonly List<(Vector3 Center, byte BlockId)> _netFalling = [];
    private readonly List<(Vector3 Center, byte BlockId)> _netFallingPrevious = [];
    // Multiplayer client: the server's icebergs by network id, mirrored without simulation
    private readonly IcebergManager? _icebergs;
    private readonly Dictionary<int, Iceberg> _netIcebergs = [];
    private readonly HashSet<int> _netIcebergsSeen = [];
    private readonly List<int> _netIcebergRemovals = [];

    /// <summary>
    /// Applies a batch of server cell values (an explosion, a fluid step, a fell, or the correction
    /// answering one of this client's intents) in one pass: raw writes with no per-cell remesh, then
    /// one light-radius area rebuild per touched chunk (<see cref="Chunk.RemeshAfterBatchEdit"/>,
    /// the same dirtying a local batch edit uses, so a remote lamp or roof edit relights the edge
    /// and corner chunks too), each chunk once however many of its cells changed. A cell that
    /// already holds the value is skipped, so a correction confirming a prediction costs nothing. A
    /// quern that arrives registers its block entity so it draws. Never raises
    /// <see cref="World.BlockEdited"/>, so it cannot loop.
    /// </summary>
    internal void ApplyNetworkBlockChanges(IReadOnlyList<NetCellChange> changes)
    {
        _netTouchedChunks.Clear();
        _netHardened.Clear();
        foreach (var change in changes)
        {
            var chunk = _chunkManager.ChunkAt(change.X, change.Y, change.Z);
            if (chunk == null)
                continue;
            if (_chunkManager.TryGetCell(change.X, change.Y, change.Z, out var id, out var state)
                && id == change.Id && state == change.State)
                continue;
            chunk.SetBlockRaw(change.X & Chunk.SizeMask, change.Y & Chunk.SizeMask, change.Z & Chunk.SizeMask,
                change.Id, change.State);
            _netTouchedChunks.Add(chunk.Position);
            // The server's falling cube writing itself back: dust as if it fell the client fall, since
            // the mirror never told how far (a client in a server world only; never on a server)
            if (_clientMode && id != change.Id && BlockType.IsFalling(change.Id)
                && NearMirroredFallingCube(change.X, change.Y, change.Z, change.Id))
                GroundDust.LandBlock(new Vector3(change.X, change.Y, change.Z), change.Id,
                    JuiceConfig.FallingBlockDustClientFallBlocks, submerged: BlockType.IsFluid(id), _debris,
                    _chunkManager.ColumnSource);
            // The server's lava hardening into its rock: steam, like a local hardening
            if (_clientMode && change.Id != 0 && BlockType.IsFluid(id) && BlockType.HardensToById[id] == change.Id)
                _netHardened.Add(new Vector3(change.X, change.Y, change.Z));
            if (BlockType.ById[change.Id]?.Renderer == BlockRenderer.Quern)
                _querns.Ensure(change.X, change.Y, change.Z);
        }

        foreach (var pos in _netTouchedChunks)
        {
            if (_chunkManager.Chunks.TryGetValue(pos, out var touched))
                touched.RemeshAfterBatchEdit(_chunkManager);
        }

        if (_netHardened.Count > 0)
            Ambient.PuffSteam(_netHardened);
    }

    /// <summary>
    /// Reconciles the client's mobs and drops with a server snapshot: mobs are spawned, updated or
    /// removed by network id, drops are rebuilt wholesale. Only a client calls this; it never ticks
    /// the mob AI, so the entities it holds are server state drawn in place. In a client world the
    /// snapshot's primed TNT and falling blocks become display-only mirrors too (TNT by network id,
    /// falling cubes wholesale); any other world ignores them, since its own Update would tick and
    /// detonate them.
    /// </summary>
    internal void ApplyNetworkEntities(IReadOnlyList<NetEntity> entities)
    {
        _netMobsSeen.Clear();
        foreach (var entry in entities)
        {
            if (!NetEntity.IsMob(entry.Kind))
                continue;
            _netMobsSeen.Add(entry.Id);
            if (_netMobs.TryGetValue(entry.Id, out var existing) && KindMatches(existing, entry))
            {
                // A client never damages a mob itself: a hit shows up as the health drop between
                // two snapshots, and that drop is what its damage number floats
                var before = existing.Health;
                existing.NetworkSetState(entry.Position, entry.Yaw, entry.Health, entry.Velocity);
                existing.Effects.SetMirrorMask(entry.Effects);
                // A realm mob's attack count moving means the server started an attack: play its clip
                if (existing is RealmMob realm) realm.MirrorAttackCount(entry.Ammo);
                if (!_serverMode && existing.Health < before)
                    Strikes.ShowDamageNumber(existing, before - existing.Health);
                continue;
            }

            if (existing != null)
                RemoveNetMob(entry.Id);
            if (SpawnNetMob(entry) is { } mob)
                _netMobs[entry.Id] = mob;
        }

        _netMobRemovals.Clear();
        foreach (var id in _netMobs.Keys.Where(id => !_netMobsSeen.Contains(id)))
            _netMobRemovals.Add(id);

        foreach (var id in _netMobRemovals)
        {
            // The server stops listing a mob the moment it dies, so the killing blow never shows as a
            // drop; one leaving near the camera (far inside every despawn radius) died, and floats
            // the health it had left
            if (!_serverMode && _netMobs.TryGetValue(id, out var gone) && gone.Health > 0
                && Vector3.DistanceSquared(gone.Position, Camera.Position)
                <= MobConfig.DamageNumberDeathRangeBlocks * MobConfig.DamageNumberDeathRangeBlocks)
                Strikes.ShowDamageNumber(gone, gone.Health);
            RemoveNetMob(id);
        }

        _dropManager.Clear();
        foreach (var netEntity in entities)
        {
            if (netEntity.Kind == NetEntityKind.Drop)
                _dropManager.SpawnRestored(netEntity.ItemId, netEntity.Position, netEntity.Velocity, 0f);
        }

        if (_clientMode)
            ApplyNetworkMirrors(entities);

        _world.RefreshMobs();
    }

    /// <summary>
    /// A client's primed TNT and falling cubes from a server snapshot. A cube already mirrored under
    /// the same id takes the new position and fuse (its strobe clock carries on), a new one is
    /// mirrored at its reported fuse, an unlisted one goes; the falling cubes are rebuilt wholesale.
    /// The client never ticks either manager (see <see cref="World.UpdateClient"/>), so neither moves,
    /// lands nor detonates here.
    /// </summary>
    private void ApplyNetworkMirrors(IReadOnlyList<NetEntity> entities)
    {
        _netTntSeen.Clear();
        // The last snapshot's cubes stay known for one more, so a cube's cell write is recognized
        // whichever of its EntityList and BlockChanges arrives first
        _netFallingPrevious.Clear();
        _netFallingPrevious.AddRange(_netFalling);
        _netFalling.Clear();
        foreach (var netEntity in entities)
        {
            if (netEntity.Kind == NetEntityKind.FallingBlock)
            {
                _netFalling.Add((netEntity.Position, netEntity.ItemId));
                continue;
            }
            if (netEntity.Kind != NetEntityKind.PrimedTnt)
                continue;

            // Milliseconds on the wire; a fuse is never shorter than what is left of it, so a bad
            // pair cannot divide the flash by zero
            var duration = Math.Max(netEntity.Ammo, 1) / 1000f;
            var remaining = Math.Clamp(netEntity.Health, 0, Math.Max(netEntity.Ammo, 1)) / 1000f;
            _netTntSeen.Add(netEntity.Id);
            if (_netTnt.TryGetValue(netEntity.Id, out var existing))
                existing.MirrorSetState(netEntity.Position, remaining);
            else
                _netTnt[netEntity.Id] = VoxelEngine.Entities.PrimedTnt.Mirror(netEntity.Position, netEntity.ItemId, duration, remaining);
        }

        _netTntRemovals.Clear();
        foreach (var id in _netTnt.Keys.Where(id => !_netTntSeen.Contains(id)))
            _netTntRemovals.Add(id);

        foreach (var id in _netTntRemovals)
        {
            // The server drops a cube only when it detonates, so a mirror leaving the snapshot with
            // its locally burnt fuse spent is a blast: shake the camera at its last position
            if (_netTnt.Remove(id, out var gone)
                && gone.FuseRemaining <= JuiceConfig.BlastMirrorFuseSlackSeconds)
                Ambient.ShakeForBlast(gone.Position);
        }

        _tntManager.ReplaceMirrors(_netTnt.Values);
        _falling.ReplaceMirrors(_netFalling);
        ApplyNetworkIcebergs(entities);
    }

    /// <summary>
    /// A client's icebergs from a server snapshot: a berg already mirrored under the same id snaps
    /// to its new center, velocity and sinking, a new one is mirrored fully emerged, an unlisted one
    /// goes. Between snapshots they only dead-reckon (<see cref="IcebergManager.AnimateMirrors"/>).
    /// </summary>
    private void ApplyNetworkIcebergs(IReadOnlyList<NetEntity> entities)
    {
        if (_icebergs == null)
            return;
        _netIcebergsSeen.Clear();
        foreach (var netEntity in entities)
        {
            if (netEntity.Kind != NetEntityKind.Iceberg)
                continue;
            var variant = Math.Clamp((int)netEntity.ItemId, 0, _icebergs.VariantCount - 1);
            NetEntity.UnpackIcebergAmmo(netEntity.Ammo, out var scale, out var sink01);
            scale = Math.Clamp(scale, IcebergConfig.ScaleMin, IcebergConfig.ScaleMax);
            _netIcebergsSeen.Add(netEntity.Id);
            if (_netIcebergs.TryGetValue(netEntity.Id, out var existing))
                existing.MirrorSetState(netEntity.Position, netEntity.Velocity, sink01);
            else
                _netIcebergs[netEntity.Id] = Iceberg.Mirror(_icebergs.ShapeOf(variant), variant, scale,
                    NetEntity.TurnsFromYaw(netEntity.Yaw), netEntity.Health, netEntity.Position, netEntity.Velocity,
                    sink01);
        }

        _netIcebergRemovals.Clear();
        foreach (var id in _netIcebergs.Keys)
            if (!_netIcebergsSeen.Contains(id))
                _netIcebergRemovals.Add(id);
        foreach (var id in _netIcebergRemovals)
            _netIcebergs.Remove(id);

        _icebergs.ReplaceMirrors(_netIcebergs.Values);
    }

    /// <summary>Whether a falling-cube mirror of <paramref name="blockId"/> from the newest or the
    /// previous snapshot sat within a block and a half of cell (x, y, z): the cube that just wrote
    /// itself back there.</summary>
    private bool NearMirroredFallingCube(int x, int y, int z, byte blockId)
    {
        var center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        const float reach = 1.5f * 1.5f;
        return
            _netFalling.Any(c => c.BlockId == blockId && Vector3.DistanceSquared(c.Center, center) <= reach)
            || _netFallingPrevious.Any(c => c.BlockId == blockId && Vector3.DistanceSquared(c.Center, center) <= reach);
    }

    /// <summary>
    /// A client replaces its station mirrors with the server's snapshot: the fires, quern tops,
    /// kilns and pours it draws are the server's, which alone runs them.
    /// </summary>
    internal void ApplyNetworkStations(PacketReader reader)
    {
        StationNetCodec.Read(reader, _campfires, _querns, _kilns, _casting);
        _world.Smithing.ReadNet(reader);
    }

    /// <summary>
    /// A client mirrors the health the server's <see cref="NetMessage.PlayerStatus"/> reports: the
    /// server alone bills falls, lava and mob hits, so a lower value is taken as damage (the flash
    /// and a shake sized like singleplayer's) and a higher one (a respawn) is restored outright.
    /// Only a client calls this; anywhere else the world owns its own pool.
    /// </summary>
    internal void ApplyNetworkHealth(int health)
    {
        if (!_clientMode)
            return;
        // The pool's size follows the mirrored gear, so the server's health is not clamped below it
        LocalPlayer.ApplyGear(simulatesMana: false);
        var current = _health.Health;
        if (health < current)
        {
            var damage = current - health;
            _health.TakeDamage(damage);
            Camera.AddShake(Math.Clamp(damage / 40f, 0.2f, 1f));
            LocalPlayer.NotifyPlayerHurt(fromMob: true);
        }
        else if (health > current)
        {
            _health.Restore(health);
        }
    }

    /// <summary>A client shows the air the server's status reports; the server alone drains and
    /// refills it, so nothing is simulated here.</summary>
    internal void ApplyNetworkAir(int airStep)
    {
        if (_clientMode)
            _health.Breath.SetNetStep(airStep);
    }

    /// <summary>A client shows the mana the server's status reports; the server alone regenerates
    /// and spends it, so nothing is simulated here.</summary>
    internal void ApplyNetworkMana(int mana, int maxMana)
    {
        if (_clientMode)
            _health.Mana.SetNet(mana, maxMana);
    }

    /// <summary>A client shows the status effects the server's status reports (an effect bitmask,
    /// unknown bits dropped); the server alone ticks them, so a mirrored effect never deals damage
    /// here, while the physics and the attack pace still read its factors.</summary>
    internal void ApplyNetworkEffects(byte mask)
    {
        if (_clientMode)
            _health.Effects.SetMirrorMask(mask);
    }

    // A slime mirror also has to be the reported size (the ammo field), since a size is fixed for life
    private static bool KindMatches(Mob mob, NetEntity entry) => entry.Kind switch
    {
        NetEntityKind.Slime => mob is Slime slime && slime.Size == SlimeSize.Clamp(entry.Ammo),
        NetEntityKind.Goblin => mob is Goblin,
        NetEntityKind.Hellbrute => mob is Hellbrute,
        NetEntityKind.Cinderwitch => mob is Cinderwitch,
        NetEntityKind.Galeraptor => mob is Galeraptor,
        NetEntityKind.Cloudgolem => mob is Cloudgolem,
        _ => false,
    };

    private Mob? SpawnNetMob(NetEntity entry)
    {
        Mob? mob = entry.Kind switch
        {
            // A slime's size rides the goblin-only ammo field (NetEntity)
            NetEntityKind.Slime => _slimes?.Spawn(entry.Position, entry.Ammo),
            NetEntityKind.Goblin => _goblins?.Spawn(entry.Position, entry.Ammo, entry.ItemId),
            // A hell mob's layer, and so its pool, follows from where it is first seen, as on the server
            NetEntityKind.Hellbrute => _hellMobs?.Spawn(Hellbrute.EntityName, entry.Position),
            NetEntityKind.Cinderwitch => _hellMobs?.Spawn(Cinderwitch.EntityName, entry.Position),
            // Likewise a sky mob's zone tier, from the column it is first seen over
            NetEntityKind.Galeraptor => _skyMobs?.Spawn(Galeraptor.EntityName, entry.Position),
            NetEntityKind.Cloudgolem => _skyMobs?.Spawn(Cloudgolem.EntityName, entry.Position),
            _ => null,
        };
        mob?.NetworkSetState(entry.Position, entry.Yaw, entry.Health, entry.Velocity);
        mob?.Effects.SetMirrorMask(entry.Effects);
        // The first count only sets it: an attack already under way when first seen is not replayed
        if (mob is RealmMob realm) realm.MirrorAttackCount(entry.Ammo);
        return mob;
    }

    private void RemoveNetMob(int id)
    {
        if (!_netMobs.Remove(id, out var mob))
            return;
        switch (mob)
        {
            case Slime slime: _slimes?.Remove(slime); break;
            case Goblin goblin: _goblins?.Remove(goblin); break;
            case HellMob hellMob: _hellMobs?.Remove(hellMob); break;
            case SkyMob skyMob: _skyMobs?.Remove(skyMob); break;
        }
    }

    /// <summary>
    /// Reconciles the remote player set with a server broadcast: upserts each entry (skipping the
    /// client's own id), then removes anyone no longer listed.
    /// </summary>
    internal void SyncRemotePlayers(IReadOnlyList<RemotePlayerSnapshot> snapshot, int selfId)
    {
        _remoteIdsSeen.Clear();
        foreach (var entry in snapshot)
        {
            if (entry.Id == selfId)
                continue;
            _remoteIdsSeen.Add(entry.Id);
            var index = IndexOfRemote(entry.Id);
            if (index < 0)
            {
                var remote = new RemotePlayer(entry.Id, entry.Name, Player.Rig);
                remote.SetTarget(entry.Eye, entry.Yaw, entry.Pitch, entry.Flags, entry.Action, entry.ActionSeq, entry.Armor);
                _remotePlayers.Add(remote);
            }
            else
            {
                var remote = _remotePlayers[index];
                remote.Name = entry.Name;
                remote.SetTarget(entry.Eye, entry.Yaw, entry.Pitch, entry.Flags, entry.Action, entry.ActionSeq, entry.Armor);
            }
        }

        for (var i = _remotePlayers.Count - 1; i >= 0; i--)
            if (!_remoteIdsSeen.Contains(_remotePlayers[i].Id))
                _remotePlayers.RemoveAt(i);
    }

    internal void ClearRemotePlayers() => _remotePlayers.Clear();

    private int IndexOfRemote(int id) => _remotePlayers.FindIndex(p => p.Id == id);
}
