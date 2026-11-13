using VoxelEngine.Commands;
using VoxelEngine.Networking;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Server;

/// <summary>Chat on the server: relays a player's line to everyone, runs a command line for its sender on the shared command trees (<see cref="GameCommands"/>) and sends the server's system lines. Main thread only.</summary>
internal sealed class ServerChat
{
    private readonly ServerState _state;
    private readonly CommandDispatcher _commands = GameCommands.Create();
    private readonly ServerCommandHost _host;

    internal ServerChat(ServerState state, ServerBroadcasts broadcasts)
    {
        _state = state;
        _host = new ServerCommandHost(state, broadcasts, BroadcastSystem);
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
    /// Runs one command line for the player who sent it, on the same trees singleplayer uses: the player is an
    /// operator while it may be creative here (<see cref="ServerState.MayBeCreative"/>), its <c>~</c> coordinates
    /// and <c>/locate</c> search start where it stands (a client holds no seed), and every answer is a system line
    /// to it, apart from what a command announces to everyone.
    /// </summary>
    private void RunCommand(ClientConnection client, string line)
    {
        if (!_state.Players.TryGetValue(client.Id, out var player)) return;
        var self = new ServerCommandPlayer(client, player, _state);
        var permission = _state.MayBeCreative(player.Name) ? CommandPermission.Operator : CommandPermission.Everyone;
        _commands.Execute(line, new CommandSource(player.Name, permission, self.Feet, self, _host,
            (text, link) => SendSystem(client, text, link)));
        Log.WriteLine($"command: <{client.Name}> {line}");
    }

    private static void SendSystem(ClientConnection client, string text, ChatLink? link)
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
