using System.Collections.Generic;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Simulation.Chests;

namespace VoxelEngine.Server;

/// <summary>
/// The server's side of chests: which chest each player has open, and the three requests that work it. A chest is
/// shared and the server's alone: any number of players may have the same one open, every move is judged here
/// (<see cref="ChestAccess"/>) against the mover's own inventory, and the new contents go to everyone who has that chest
/// open (<see cref="NetMessage.ChestContents"/>), the mover's inventory following through the usual changed-inventory
/// snapshot. A chest that is out of reach, broken or blasted away closes for its viewers. Main thread only.
/// </summary>
internal sealed class ChestSessions(ServerState state)
{
    // Scratch for the per-pump sweep, so a quiet frame allocates nothing
    private readonly List<ServerPlayer> _closing = [];
    // The cells at least one player has open, rebuilt each pump for the world's lids
    private readonly HashSet<(int X, int Y, int Z)> _viewed = [];

    /// <summary>A player asks to open the chest at a cell: its contents come back, or the closed answer when it
    /// cannot be opened (not a chest, unloaded, out of reach, dead player).</summary>
    internal void OnOpen(ClientConnection client, PacketReader reader)
    {
        var (x, y, z) = ChestNetCodec.ReadOpen(reader);
        if (!state.Players.TryGetValue(client.Id, out var player)) return;
        player.OpenChest = null;
        if (player.IsDead)
        {
            SendClosed(client);
            return;
        }

        var chest = ChestAccess.TryOpen(state.World.Chests, state.World.GetChunkManager(), player.Position, x, y, z,
            World.InteractionReach);
        if (chest == null)
        {
            SendClosed(client);
            return;
        }
        player.OpenChest = (x, y, z);
        state.World.Towers.AlarmPilesNear((x, y, z));
        state.World.Towers.BeginRitualFromChest((x, y, z));
        SendContents(client, x, y, z, chest);
    }

    /// <summary>A player moves things between its inventory and the chest it has open.</summary>
    internal void OnMove(ClientConnection client, PacketReader reader)
    {
        var move = ChestNetCodec.ReadMove(reader);
        if (!state.Players.TryGetValue(client.Id, out var player) || player.OpenChest is not { } cell) return;
        var chest = ChestAccess.TryOpen(state.World.Chests, state.World.GetChunkManager(), player.Position, cell.X, cell.Y,
            cell.Z, World.InteractionReach);
        if (chest == null)
        {
            Close(player);
            return;
        }

        var moved = ChestAccess.Apply(player.Inventory, chest, move);
        if (moved > 0) player.InventoryChanged = true;
        // Everyone looking at this chest sees the change, the mover included (a refused move re-sends the truth)
        BroadcastContents(cell, chest);
    }

    /// <summary>A player closed its chest panel.</summary>
    internal void OnClose(ClientConnection client)
    {
        if (state.Players.TryGetValue(client.Id, out var player))
            player.OpenChest = null;
    }

    /// <summary>
    /// Once a pump: closes the chest of every player who walked out of reach, died, or whose chest no longer exists
    /// (broken, blasted, replaced), telling them with the closed answer. Then tells the world's lids which chests
    /// still have a viewer (<see cref="ChestLids.SetViewed"/>), which the station broadcast carries to every client:
    /// a chest's lid opens for everyone when its first viewer arrives and shuts when its last one leaves.
    /// </summary>
    internal void Tick()
    {
        _closing.Clear();
        foreach (var player in state.PlayerList)
        {
            if (player.OpenChest is not { } cell) continue;
            var chests = state.World.Chests;
            var gone = chests.Peek(cell.X, cell.Y, cell.Z) == null
                || !ChestManager.TryProfileAt(state.World.GetChunkManager(), cell.X, cell.Y, cell.Z, out _);
            if (gone || player.IsDead || !ChestAccess.InReach(player.Position, cell.X, cell.Y, cell.Z, World.InteractionReach))
                _closing.Add(player);
        }
        foreach (var player in _closing)
            Close(player);

        _viewed.Clear();
        foreach (var player in state.PlayerList)
            if (player.OpenChest is { } cell)
                _viewed.Add(cell);
        state.World.ChestLids.SetViewed(_viewed);
    }

    private void Close(ServerPlayer player)
    {
        player.OpenChest = null;
        if (state.Clients.TryGetValue(player.Id, out var client))
            SendClosed(client);
    }

    private void BroadcastContents((int X, int Y, int Z) cell, ChestState chest)
    {
        foreach (var player in state.PlayerList)
            if (player.OpenChest == cell && state.Clients.TryGetValue(player.Id, out var client))
                SendContents(client, cell.X, cell.Y, cell.Z, chest);
    }

    private static void SendContents(ClientConnection client, int x, int y, int z, ChestState chest)
    {
        using var writer = new PacketWriter(NetMessage.ChestContents);
        ChestNetCodec.WriteOpenContents(writer, x, y, z, chest);
        client.Send(writer.ToArray());
    }

    private static void SendClosed(ClientConnection client)
    {
        using var writer = new PacketWriter(NetMessage.ChestContents);
        ChestNetCodec.WriteClosed(writer);
        client.Send(writer.ToArray());
    }
}
