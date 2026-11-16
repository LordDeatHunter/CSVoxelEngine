using System;
using VoxelEngine.Commands;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking.ServerSide;

/// <summary>
/// The server's <c>/op</c> and <c>/deop</c>: changes the operator list, saves it as the world folder's
/// <see cref="OperatorList.FileName"/>, and tells every online player under that name (slug match) at once: its
/// status carries the new operator flag, and a deop also ends its creative and flight. The hosting player
/// (<see cref="ServerState.HostPlayer"/>) is always an operator and cannot be deopped. Main thread only.
/// </summary>
internal sealed class ServerOperators(ServerState state, Action<ClientConnection, string> tell) : IOperatorAdmin
{
    public bool TryOp(string name, out string refusal)
    {
        if (state.IsOperator(name) || !state.Operators.Add(name))
        {
            refusal = $"{name} is already an operator";
            return false;
        }
        Changed(name, "You are now an operator");
        refusal = "";
        return true;
    }

    public bool TryDeop(string name, out string refusal)
    {
        if (state.IsHost(name))
        {
            refusal = $"{name} is hosting this world and stays an operator";
            return false;
        }
        if (!state.Operators.Remove(name))
        {
            refusal = $"{name} is not an operator";
            return false;
        }
        Changed(name, "You are no longer an operator");
        refusal = "";
        return true;
    }

    private void Changed(string name, string notice)
    {
        state.World.SaveStore?.EnqueueFile(OperatorList.FileName, state.Operators.ToJson());

        var slug = WorldNames.Slug(name.Trim());
        foreach (var (id, player) in state.Players)
        {
            if (WorldNames.Slug(player.Name) != slug || !state.Clients.TryGetValue(id, out var client)) continue;
            player.IsOperator = state.IsOperator(player.Name);
            if (!player.IsOperator)
            {
                // Creative is an operator's right: losing it ends creative (and flight with it) now
                player.Creative = false;
                player.Flying = false;
            }
            PlayerSessions.SendPlayerStatus(client, player);
            tell(client, notice);
        }
    }
}
