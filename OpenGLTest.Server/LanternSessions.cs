using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Simulation.Lanterns;

namespace VoxelEngine.Server;

/// <summary>Server-owned lantern sessions: validate selection/reach on every move; snapshots are answers only.</summary>
internal sealed class LanternSessions(ServerState state)
{
    private LanternState? Resolve(ServerPlayer player, LanternTarget target)
    {
        player.Aim(player.Position, player.Context.Forward);
        return state.World.Lanterns.Resolve(player.Context, state.World.GetChunkManager(), target.Instance, target.Cell);
    }

    internal void Open(ClientConnection client, PacketReader reader)
    {
        var target = LanternNetCodec.ReadTarget(reader);
        if (!state.Players.TryGetValue(client.Id, out var player)) return;
        player.OpenLantern = null;
        var fuel = Resolve(player, target);
        // A placed opening must be aimed at, not merely somewhere inside the reach sphere.
        if (fuel == null || !LanternAccess.TryTarget(player.Context, state.World.GetChunkManager(),
                World.InteractionReach, out var expected, out var pickup) || pickup || expected != target)
        { Closed(client); return; }
        player.OpenChest = null;
        player.OpenLantern = target;
        Send(client, target, fuel);
    }

    internal void Move(ClientConnection client, PacketReader reader)
    {
        var move = ChestNetCodec.ReadMove(reader);
        if (!state.Players.TryGetValue(client.Id, out var player) || player.OpenLantern is not { } target) return;
        var fuel = Resolve(player, target);
        if (fuel == null) { player.OpenLantern = null; Closed(client); return; }
        if (fuel.Move(player.Inventory, move) > 0) player.InventoryChanged = true;
        Send(client, target, fuel);
    }

    internal void Close(ClientConnection client)
    {
        if (state.Players.TryGetValue(client.Id, out var player)) player.OpenLantern = null;
    }

    internal void Tick()
    {
        foreach (var player in state.PlayerList)
        {
            if (player.OpenLantern is not { } target || !state.Clients.TryGetValue(player.Id, out var client)) continue;
            var fuel = Resolve(player, target);
            if (fuel == null) { player.OpenLantern = null; Closed(client); }
            else Send(client, target, fuel);
        }
    }
    private static void Send(ClientConnection client, LanternTarget target, LanternState fuel)
    {
        using var writer = new PacketWriter(NetMessage.LanternContents);
        LanternNetCodec.WriteContents(writer, target, fuel);
        client.Send(writer.ToArray());
    }
    private static void Closed(ClientConnection client)
    {
        using var writer = new PacketWriter(NetMessage.ChestContents);
        ChestNetCodec.WriteClosed(writer);
        client.Send(writer.ToArray());
    }
}
