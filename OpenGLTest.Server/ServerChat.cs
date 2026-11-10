using System;
using VoxelEngine.Content;
using VoxelEngine.Interaction;
using VoxelEngine.Networking;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Server;

/// <summary>Chat on the server: relays a player's line to everyone, runs a command line for its sender on the shared table (<see cref="ChatCommands"/>) and sends the server's system lines. Main thread only.</summary>
internal sealed class ServerChat
{
    private readonly ServerState _state;
    private readonly ServerBroadcasts _broadcasts;

    internal ServerChat(ServerState state, ServerBroadcasts broadcasts)
    {
        _state = state;
        _broadcasts = broadcasts;
    }

    internal void OnChatSend(ClientConnection client, PacketReader reader)
    {
        var text = reader.ReadString();
        if (text.Length > ChatView.MaxInputLength)
            text = text[..ChatView.MaxInputLength];
        if (string.IsNullOrWhiteSpace(text))
            return;
        // A command line runs here for the sending player (never echoed as chat);
        // anything else is chat
        if (text[0] == '/')
        {
            RunCommand(client, text);
            return;
        }
        BroadcastChat(ChatKind.Player, client.Name, text);
        Log.WriteLine($"chat: <{client.Name}> {text}");
    }

    /// <summary>
    /// Runs one chat command for the player who sent it, on the same table singleplayer uses
    /// (<see cref="ChatCommands"/>): <c>/tp</c> and <c>/give</c> need operator rights, and so does
    /// <c>/time</c>, while <c>/locate</c> and <c>/help</c> need none; the answers are system lines to
    /// that player (a time change also goes out as a <see cref="NetMessage.TimeSync"/> at once, and
    /// <c>/locate</c>'s carries a link, searched from that player's eye, since a client holds no seed).
    /// </summary>
    private void RunCommand(ClientConnection client, string line)
    {
        if (!_state.Players.TryGetValue(client.Id, out var player)) return;
        var parsed = ChatCommands.Parse(line);
        if (ChatCommands.RequiresOperator(parsed.Kind) || parsed.Kind == ChatCommands.CommandKind.Time)
        {
            if (!_state.MayBeCreative(player.Name))
            {
                SendSystem(client, "That command is for operators only");
                return;
            }
        }

        switch (parsed.Kind)
        {
            case ChatCommands.CommandKind.Time:
                if (parsed.Args.Length == 0 || ChatCommands.TimeSeconds(parsed.Args[0]) is not { } seconds)
                {
                    SendSystem(client, "Usage: /time day|night|<seconds>");
                    return;
                }
                _state.World.SetLightingTime(seconds);
                _broadcasts.BroadcastTime();
                BroadcastSystem($"Time set to {seconds:0.#} s by {player.Name}");
                return;

            case ChatCommands.CommandKind.Teleport:
                if (ChatCommands.TeleportTarget(parsed.Args) is not { } target)
                {
                    SendSystem(client, "Usage: /tp x y z");
                    return;
                }
                // A /tp ends a waystone trip still waiting for its destination
                player.WaystoneTrip = null;
                player.Teleport(target);
                PlayerSessions.SendPlayerCorrection(client, player.IssueCorrection(), player.Position);
                SendSystem(client, $"Teleported to {target.X:0.#} {target.Y:0.#} {target.Z:0.#}");
                return;

            case ChatCommands.CommandKind.Give:
                if (ChatCommands.GiveRequest(parsed.Args) is not { } request)
                {
                    SendSystem(client, "Usage: /give <item> [count]");
                    return;
                }
                if (!TryResolveContent(request.Item, out var id))
                {
                    SendSystem(client, $"Unknown item '{request.Item}'");
                    return;
                }
                player.Inventory.Add(id, request.Count);
                player.InventoryChanged = true;
                SendSystem(client, $"Gave {request.Count} x {request.Item}");
                return;

            case ChatCommands.CommandKind.Locate:
            {
                var (text, link) = ChatCommands.Locate(parsed.Args, player.Position, _state.World.Towers);
                SendSystem(client, text, link);
                return;
            }

            default:
                SendSystem(client, ChatCommands.HelpText);
                return;
        }
    }

    // An item or block by plain name (blocks first, like a loot entry) or by its block//item/
    // pinned name
    private static bool TryResolveContent(string name, out ushort id)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("block/", StringComparison.OrdinalIgnoreCase))
            return ContentRegistry.TryResolveBlock(trimmed[6..], out id);
        if (trimmed.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
            return ContentRegistry.TryResolveItem(trimmed[5..], out id);
        return ContentRegistry.TryResolveBlock(trimmed, out id) || ContentRegistry.TryResolveItem(trimmed, out id);
    }

    private static void SendSystem(ClientConnection client, string text, ChatLink? link = null)
    {
        using var writer = new PacketWriter(NetMessage.ChatMessage);
        ChatMessageCodec.Write(writer, new ChatMessage(ChatKind.System, "", text, link));
        client.Send(writer.ToArray());
    }

    private void BroadcastChat(ChatKind kind, string sender, string text)
    {
        using var writer = new PacketWriter(NetMessage.ChatMessage);
        ChatMessageCodec.Write(writer, new ChatMessage(kind, sender, text));
        _state.Broadcast(writer.ToArray());
    }

    internal void BroadcastSystem(string text) => BroadcastChat(ChatKind.System, "", text);
}
