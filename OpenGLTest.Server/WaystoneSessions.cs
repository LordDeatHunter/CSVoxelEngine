using System.Collections.Generic;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Simulation.Waystones;

namespace VoxelEngine.Server;

/// <summary>
/// The server's side of waystones. The registry and every rule are the world's (<see cref="World.Waystones"/>,
/// <see cref="WaystoneTeleport"/>, <see cref="WaystoneArrival"/>); this runs them for each connected player and tells the
/// client what it needs to draw: a replayed right-click on a waystone discovers it for that player and answers with a
/// <see cref="NetMessage.WaystoneOpened"/> (the popup or the list); a <see cref="NetMessage.WaystoneRename"/> is taken
/// from the waystone's first discoverer only; each player's <see cref="NetMessage.WaystoneRows"/> (the registry rows of
/// the waystones it discovered) are resent whenever the registry or its list changed; and a
/// <see cref="NetIntentKind.Waystone"/> trip is judged, billed and held until the destination has loaded, then landed
/// with a <see cref="NetMessage.PlayerCorrection"/>. Main thread only.
/// </summary>
internal sealed class WaystoneSessions(ServerState state)
{
    private readonly List<WaystoneNetRow> _rows = [];
    private ChunkArrivalCells? _cells;

    private ChunkArrivalCells Cells => _cells ??= new ChunkArrivalCells(state.World.GetChunkManager());

    /// <summary>
    /// A replayed right-click claimed a waystone: the player discovers it (on the world's registry, under its name slug)
    /// and stands at it from now on, and its client is sent the fresh rows and what the activation came to.
    /// </summary>
    internal void OnActivated(ClientConnection client, ServerPlayer player, (int X, int Y, int Z) cell)
    {
        var world = state.World;
        if (world.DiscoverWaystoneFor(player.Context, new System.Numerics.Vector3(cell.X, cell.Y, cell.Z)) is not { } found)
            return;
        player.AtWaystone = cell;
        SendRows(client, player);
        using var writer = new PacketWriter(NetMessage.WaystoneOpened);
        WaystoneNetCodec.WriteOpened(writer,
            new WaystoneOpened(cell.X, cell.Y, cell.Z, found.Record.Name, found.FirstDiscoverer && !found.Record.Lost,
                found.NewToPlayer));
        client.Send(writer.ToArray());
    }

    /// <summary>A player asks to rename a waystone: only its first discoverer may (<see cref="World.RenameWaystoneFor"/>
    /// sanitizes the name). Either way its client gets the rows back, so a refused rename shows the truth again.</summary>
    internal void OnRename(ClientConnection client, PacketReader reader)
    {
        var (cell, name) = WaystoneNetCodec.ReadRename(reader);
        if (!state.Players.TryGetValue(client.Id, out var player)) return;
        if (!player.IsDead)
            state.World.RenameWaystoneFor(player.Context, new System.Numerics.Vector3(cell.X, cell.Y, cell.Z), name);
        SendRows(client, player);
    }

    /// <summary>
    /// Judges a <see cref="NetIntentKind.Waystone"/> trip for <paramref name="player"/>: standing at the waystone it last
    /// activated, not already traveling, then <see cref="WaystoneTeleport.TryPay"/> (discovered, not lost, mana over
    /// half unless creative or flying, which the trip drains). An accepted trip moves the player over the destination
    /// (its streaming focus follows) and holds it there until <see cref="Tick"/> lands it; until then its client's
    /// states move nothing, through a correction it is never sent and so never acks. The pool goes out at once either
    /// way it was read.
    /// </summary>
    internal bool TripFor(ServerPlayer player, in WorldIntent intent)
    {
        var world = state.World;
        if (player.WaystoneTrip != null || player.AtWaystone is not { } from
            || !WaystoneTeleport.IsAtWaystone(world.GetChunkManager(), player.Position, from, World.InteractionReach))
            return false;
        var to = (intent.X, intent.Y, intent.Z);
        var verdict = WaystoneTeleport.TryPay(player.Inventory.Waystones, world.Waystones.Peek(to.X, to.Y, to.Z), from, to,
            WaystoneTeleport.IsFree(player.Creative, player.Flying), player.Status.Mana);
        if (verdict is WaystoneTeleportVerdict.Teleport or WaystoneTeleportVerdict.NeedsMana)
            player.StatusDirty = true;
        if (verdict != WaystoneTeleportVerdict.Teleport)
            return false;

        var trip = new WaystoneArrivalHold(to);
        player.WaystoneTrip = trip;
        player.AtWaystone = null;
        player.Breaking.Reset();
        player.Teleport(trip.HoldEye);
        player.IssueCorrection();
        return true;
    }

    /// <summary>
    /// Once a pump: lands every held traveler whose destination has loaded (or whose hold timed out) and sends each
    /// player whose rows went stale its fresh rows (at join, and after any discovery, rename or loss).
    /// </summary>
    internal void Tick(double deltaTime)
    {
        var registry = state.World.Waystones.Version;
        foreach (var player in state.PlayerList)
        {
            if (!state.Clients.TryGetValue(player.Id, out var client)) continue;
            if (player.WaystoneTrip is { } trip)
            {
                if (trip.Step(deltaTime, Cells) is { } spot)
                    Land(client, player, spot);
                else
                    player.Position = trip.HoldEye;
            }
            if (player.WaystoneRowsSent != (registry, player.Inventory.Waystones.Version))
                SendRows(client, player);
        }
    }

    // The trip ends: the player stands on the spot, facing away from the waystone, its fall forgotten, and its client
    // snaps there (and holds until its own chunks are in)
    private static void Land(ClientConnection client, ServerPlayer player, WaystoneArrivalSpot spot)
    {
        player.WaystoneTrip = null;
        player.Teleport(WaystoneArrival.EyeOver(spot.Feet));
        if (spot.Yaw is { } yaw)
        {
            player.Yaw = yaw;
            player.Pitch = 0f;
        }
        PlayerSessions.SendPlayerCorrection(client, player.IssueCorrection(), player.Position);
        player.StatusDirty = true;
    }

    // This player's rows: one per discovered waystone the registry knows, with whether this player may rename it
    private void SendRows(ClientConnection client, ServerPlayer player)
    {
        var waystones = state.World.Waystones;
        var discoverer = WaystoneManager.DiscovererOf(player.Context);
        _rows.Clear();
        foreach (var entry in player.Inventory.Waystones.Entries)
        {
            if (waystones.Peek(entry.X, entry.Y, entry.Z) is not { } record) continue;
            _rows.Add(new WaystoneNetRow(entry.X, entry.Y, entry.Z, record.Name, record.Lost,
                waystones.MayRename(entry.X, entry.Y, entry.Z, discoverer)));
        }
        player.WaystoneRowsSent = (waystones.Version, player.Inventory.Waystones.Version);
        using var writer = new PacketWriter(NetMessage.WaystoneRows);
        WaystoneNetCodec.WriteRows(writer, _rows);
        client.Send(writer.ToArray());
    }
}
