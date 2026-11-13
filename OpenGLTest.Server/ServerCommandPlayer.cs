using System.Numerics;
using VoxelEngine.Commands;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;

namespace VoxelEngine.Server;

/// <summary>A connected player as commands see it: each effect lands on the server's <see cref="ServerPlayer"/> and is
/// sent to its client (a teleport as a correction, a mode change as a status, a grant through the inventory sync).
/// Main thread only.</summary>
internal sealed class ServerCommandPlayer(ClientConnection client, ServerPlayer player, ServerState state) : ICommandPlayer
{
    public string Name => player.Name;

    public Vector3 Feet => player.Position - new Vector3(0f, Camera.EyeHeight, 0f);

    public bool IsCreative => player.Creative;

    public bool IsFlying => player.Flying;

    public void Teleport(Vector3 feet)
    {
        // A teleport ends a waystone trip still waiting for its destination
        player.WaystoneTrip = null;
        player.Teleport(feet + new Vector3(0f, Camera.EyeHeight, 0f));
        PlayerSessions.SendPlayerCorrection(client, player.IssueCorrection(), player.Position);
    }

    public void Give(ushort id, int count)
    {
        player.Inventory.Add(id, count);
        player.InventoryChanged = true;
    }

    public bool TrySetModes(bool creative, bool flying, out string refusal)
    {
        if (creative && !state.MayBeCreative(player.Name))
        {
            refusal = $"{player.Name} may not be creative on this server";
            return false;
        }
        player.Creative = creative;
        player.Flying = creative && flying;
        // The client adopts the modes its status reports, and asks for them from then on
        PlayerSessions.SendPlayerStatus(client, player);
        refusal = "";
        return true;
    }

    public bool Kill()
    {
        if (player.IsDead) return false;
        player.Status.TakeDamage(player.Status.Health);
        player.StatusDirty = true;
        return true;
    }
}
