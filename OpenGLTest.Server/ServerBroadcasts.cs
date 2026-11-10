using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Networking;

namespace VoxelEngine.Server;

/// <summary>The world state the server broadcasts to every client: the journaled cell writes (BlockChanges), the live entities under stable ids, the station block entities and the day clock. Main thread only, except <see cref="OnCellWritten"/>, which only enqueues.</summary>
internal sealed class ServerBroadcasts
{
    private readonly ServerState _state;

    // Long-lived, coalesced cell changes the server's own simulation produced (explosions, fluid
    // flow, decay, felling), drained and broadcast in batches.
    private readonly ConcurrentQueue<NetCellChange> _cellJournal = new();
    private readonly Dictionary<(int X, int Y, int Z), (ushort Id, byte State)> _cellCoalesce = new();
    private const int MaxCellChangesPerBroadcast = 8192;

    // Whether the last station broadcast carried anything, so the one after the last station went
    // away still goes out and clears the clients' mirrors
    private bool _stationsSent;

    // Stable network ids for the live mobs and drops, keyed by object identity (reference equality).
    private readonly Dictionary<object, int> _entityIds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _entitySeen = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _entityStale = [];
    private int _nextEntityId = 1;

    internal ServerBroadcasts(ServerState state)
    {
        _state = state;
    }

    internal void BroadcastTime()
    {
        using var writer = new PacketWriter(NetMessage.TimeSync);
        writer.Write(_state.World.LightingTime);
        _state.Broadcast(writer.ToArray());
    }

    /// <summary>
    /// Broadcasts every live mob, drop, primed TNT and falling block (<see cref="NetMessage.EntityList"/>),
    /// each under a stable id by object identity. The TNT and falling cubes are drawn by clients as
    /// display-only mirrors; only this world ticks, detonates and lands them.
    /// </summary>
    internal void BroadcastEntities()
    {
        var mobs = _state.World.Mobs;
        var drops = _state.World.Drops;
        var primed = _state.World.PrimedTnt;
        var falling = _state.World.Falling;
        var icebergs = _state.World.Icebergs;
        _entitySeen.Clear();

        using var writer = new PacketWriter(NetMessage.EntityList);
        // A digging sandworm also sends the point it will surface at, one extra entry sharing its id
        var wormTargets = 0;
        for (var i = 0; i < mobs.Count; i++)
            if (mobs[i] is Sandworm { Phase: SandwormPhase.Dig, Telegraph: > 0f }) wormTargets++;
        writer.Write(mobs.Count + wormTargets + drops.Count + primed.Count + falling.Count + icebergs.Count);
        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            var kind = mob switch
            {
                Slime => NetEntityKind.Slime,
                Hellbrute => NetEntityKind.Hellbrute,
                Cinderwitch => NetEntityKind.Cinderwitch,
                Galeraptor => NetEntityKind.Galeraptor,
                Cloudgolem => NetEntityKind.Cloudgolem,
                Sandworm => NetEntityKind.Sandworm,
                Skeleton => NetEntityKind.Skeleton,
                SkeletonKing => NetEntityKind.SkeletonKing,
                Shardling => NetEntityKind.Shardling,
                Sporeling => NetEntityKind.Sporeling,
                Magmaborn => NetEntityKind.Magmaborn,
                Animal animal => NetEntity.KindOf(animal.Kind),
                _ => NetEntityKind.Goblin,
            };
            // The int field is a goblin's ammo, a slime's size and a realm mob's attack count (NetEntity)
            var ammo = mob switch
            {
                Goblin goblin => goblin.Ammo,
                Slime slime => slime.Size,
                RealmMob realm => realm.AttackCount,
                Sandworm worm => (int)worm.Phase,
                _ => 0,
            };
            // The byte field is a goblin's torch count, a skeleton's tier, role and outfit, a cave mob's tier, a wild animal's posture or the Skeleton King's phase (0 for the
            // rest); every entry ends with a status-effect mask (NetEntity.Effects), a mob's own
            var itemByte = mob switch
            {
                Goblin goblin => (byte)goblin.Torches,
                Skeleton skeleton => Skeleton.PackLook(skeleton.Tier, skeleton.Role, skeleton.Outfit),
                SkeletonKing king => (byte)king.Phase,
                CaveMob cave => (byte)cave.Tier,
                Animal animal => animal.Posture,
                _ => (byte)0,
            };
            NetEntityCodec.Write(writer, new NetEntity(kind, IdFor(mob), mob.Position, mob.Facing, mob.Health, ammo,
                itemByte, mob.Velocity, mob.Effects.Mask));
            if (mob is Sandworm { Phase: SandwormPhase.Dig, Telegraph: > 0f } digging)
                NetEntityCodec.Write(writer, new NetEntity(NetEntityKind.SandwormTarget, IdFor(mob), digging.SurfacePoint, 0f,
                    (int)MathF.Round(digging.Telegraph * 1000f), 0, 0, Vector3.Zero));
        }

        for (var i = 0; i < drops.Count; i++)
        {
            var drop = drops[i];
            NetEntityCodec.Write(writer, new NetEntity(NetEntityKind.Drop, IdFor(drop), drop.Position, 0f, 0, 0,
                drop.BlockId, drop.Velocity));
        }

        // The fuse travels in milliseconds in the two int fields, so the client's flash and swell
        // follow the server's fuse
        for (var i = 0; i < primed.Count; i++)
        {
            var tnt = primed[i];
            NetEntityCodec.Write(writer, new NetEntity(NetEntityKind.PrimedTnt, IdFor(tnt), tnt.Position, 0f,
                (int)MathF.Round(tnt.FuseRemaining * 1000f), (int)MathF.Round(tnt.FuseDuration * 1000f), tnt.BlockId,
                Vector3.Zero));
        }

        for (var i = 0; i < falling.Count; i++)
        {
            var block = falling[i];
            NetEntityCodec.Write(writer, new NetEntity(NetEntityKind.FallingBlock, IdFor(block), block.Position, 0f, 0,
                0, block.BlockId, Vector3.Zero));
        }

        // An iceberg's fields are packed as NetEntity documents: turns in the yaw, the seed in the
        // health, the scale and sinking in the ammo, the variant in the byte
        for (var i = 0; i < icebergs.Count; i++)
        {
            var berg = icebergs[i];
            NetEntityCodec.Write(writer, new NetEntity(NetEntityKind.Iceberg, IdFor(berg), berg.Center,
                NetEntity.YawFromTurns(berg.Turns), berg.Seed, NetEntity.PackIcebergAmmo(berg.Scale, berg.Sink01),
                (byte)berg.Variant, berg.Velocity));
        }

        _state.Broadcast(writer.ToArray());
        PruneEntityIds();
    }

    private int IdFor(object entity)
    {
        _entitySeen.Add(entity);
        if (_entityIds.TryGetValue(entity, out var id))
            return id;
        id = _nextEntityId++;
        _entityIds[entity] = id;
        return id;
    }

    private void PruneEntityIds()
    {
        _entityStale.Clear();
        foreach (var key in _entityIds.Keys)
            if (!_entitySeen.Contains(key))
                _entityStale.Add(key);
        foreach (var key in _entityStale)
            _entityIds.Remove(key);
    }

    internal void OnCellWritten(int x, int y, int z, ushort id, byte state) =>
        _cellJournal.Enqueue(new NetCellChange(x, y, z, id, state));

    /// <summary>
    /// Broadcasts every campfire, quern, kiln and cast mold the server runs, which the clients draw
    /// in place of their own; nothing while there are none (after one last empty snapshot).
    /// </summary>
    internal void BroadcastStations()
    {
        var any = _state.World.HasStations;
        if (!any && !_stationsSent)
            return;
        _stationsSent = any;
        using var writer = new PacketWriter(NetMessage.StationState);
        _state.World.WriteStations(writer);
        _state.Broadcast(writer.ToArray());
    }

    // Coalesces this frame's journaled writes by cell (the last write wins) and sends one
    // BlockChanges batch; a burst past the cap leaves the rest for the next frame.
    internal void DrainCellJournal()
    {
        if (_cellJournal.IsEmpty)
            return;

        _cellCoalesce.Clear();
        var drained = 0;
        while (drained < MaxCellChangesPerBroadcast && _cellJournal.TryDequeue(out var change))
        {
            _cellCoalesce[(change.X, change.Y, change.Z)] = (change.Id, change.State);
            drained++;
        }

        if (_cellCoalesce.Count == 0)
            return;

        using var writer = new PacketWriter(NetMessage.BlockChanges);
        writer.Write(_cellCoalesce.Count);
        foreach (var (cell, value) in _cellCoalesce)
            NetCellChangeCodec.Write(writer, new NetCellChange(cell.X, cell.Y, cell.Z, value.Id, value.State));

        _state.Broadcast(writer.ToArray());
        _cellCoalesce.Clear();
    }
}
