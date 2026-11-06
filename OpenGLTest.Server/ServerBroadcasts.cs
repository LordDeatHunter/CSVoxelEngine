using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Networking;

namespace VoxelEngine.Server;

/// <summary>The world state the server broadcasts to every client: the journaled cell writes (BlockChanges), the live entities under stable ids, the station block entities and the day clock. Main thread only, except <see cref="OnCellWritten"/>, which only enqueues.</summary>
internal sealed class ServerBroadcasts
{
    private readonly ServerState _state;

    // Long-lived, coalesced cell changes the server's own simulation produced (explosions, fluid
    // flow, decay, felling), drained and broadcast in batches.
    private readonly ConcurrentQueue<CellChange> _cellJournal = new();
    private readonly Dictionary<(int X, int Y, int Z), (byte Id, byte State)> _cellCoalesce = new();
    private const int MaxCellChangesPerBroadcast = 8192;

    private readonly record struct CellChange(int X, int Y, int Z, byte Id, byte State);

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
        writer.Write(mobs.Count + drops.Count + primed.Count + falling.Count + icebergs.Count);
        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            var kind = mob is Slime ? NetEntityKind.Slime : NetEntityKind.Goblin;
            // The int field is a goblin's ammo and a slime's size (NetEntity)
            var ammo = mob switch
            {
                Goblin goblin => goblin.Ammo,
                Slime slime => slime.Size,
                _ => 0,
            };
            writer.Write((byte)kind);
            writer.Write(IdFor(mob));
            writer.Write(mob.Position);
            writer.Write(mob.Facing);
            writer.Write(mob.Health);
            writer.Write(ammo);
            // The byte field is a goblin's torch count (a slime carries none)
            writer.Write(mob is Goblin torchGoblin ? (byte)torchGoblin.Torches : (byte)0);
            writer.Write(mob.Velocity);
            // Every entry ends with a status-effect mask (NetEntity.Effects), a mob's own
            writer.Write(mob.Effects.Mask);
        }

        for (var i = 0; i < drops.Count; i++)
        {
            var drop = drops[i];
            writer.Write((byte)NetEntityKind.Drop);
            writer.Write(IdFor(drop));
            writer.Write(drop.Position);
            writer.Write(0f);
            writer.Write(0);
            writer.Write(0);
            writer.Write(drop.BlockId);
            writer.Write(drop.Velocity);
            writer.Write((byte)0);
        }

        // The fuse travels in milliseconds in the two int fields, so the client's flash and swell
        // follow the server's fuse
        for (var i = 0; i < primed.Count; i++)
        {
            var tnt = primed[i];
            writer.Write((byte)NetEntityKind.PrimedTnt);
            writer.Write(IdFor(tnt));
            writer.Write(tnt.Position);
            writer.Write(0f);
            writer.Write((int)MathF.Round(tnt.FuseRemaining * 1000f));
            writer.Write((int)MathF.Round(tnt.FuseDuration * 1000f));
            writer.Write(tnt.BlockId);
            writer.Write(Vector3.Zero);
            writer.Write((byte)0);
        }

        for (var i = 0; i < falling.Count; i++)
        {
            var block = falling[i];
            writer.Write((byte)NetEntityKind.FallingBlock);
            writer.Write(IdFor(block));
            writer.Write(block.Position);
            writer.Write(0f);
            writer.Write(0);
            writer.Write(0);
            writer.Write(block.BlockId);
            writer.Write(Vector3.Zero);
            writer.Write((byte)0);
        }

        // An iceberg's fields are packed as NetEntity documents: turns in the yaw, the seed in the
        // health, the scale and sinking in the ammo, the variant in the byte
        for (var i = 0; i < icebergs.Count; i++)
        {
            var berg = icebergs[i];
            writer.Write((byte)NetEntityKind.Iceberg);
            writer.Write(IdFor(berg));
            writer.Write(berg.Center);
            writer.Write(NetEntity.YawFromTurns(berg.Turns));
            writer.Write(berg.Seed);
            writer.Write(NetEntity.PackIcebergAmmo(berg.Scale, berg.Sink01));
            writer.Write((byte)berg.Variant);
            writer.Write(berg.Velocity);
            writer.Write((byte)0);
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

    internal void OnCellWritten(int x, int y, int z, byte id, byte state) =>
        _cellJournal.Enqueue(new CellChange(x, y, z, id, state));

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
        {
            writer.Write(cell.X);
            writer.Write(cell.Y);
            writer.Write(cell.Z);
            writer.Write(value.Id);
            writer.Write(value.State);
        }

        _state.Broadcast(writer.ToArray());
        _cellCoalesce.Clear();
    }
}
