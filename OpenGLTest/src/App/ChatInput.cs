using System;
using System.Collections.Generic;
using Silk.NET.Input;
using VoxelEngine.Content;
using VoxelEngine.Interaction;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.App;

/// <summary>The chat's input: the keys an open chat reacts to, submitting a line (to the server, or a local echo or command in singleplayer), the local chat commands and the Tab player list's names. Owned by Program; main thread only.</summary>
internal sealed class ChatInput
{
    private readonly ChatView chatView;
    private readonly ClientSession session;

    private readonly TextFieldKeys textKeys;

    internal ChatInput(ChatView chatView, ClientSession session, InputManager inputManager)
    {
        this.chatView = chatView;
        this.session = session;
        textKeys = new TextFieldKeys(inputManager);
    }

    private World? ActiveWorld => Program.ActiveWorld;

    /// <summary>The keys the chat input reacts to while it is open. ESC is left to the polled
    /// handler, so closing chat and the pause menu never both fire on one press.</summary>
    internal void HandleChatKey(Key key)
    {
        // Up and Down recall sent lines here, so only the other editing keys go to the field
        if (key is not (Key.Up or Key.Down) && textKeys.TryHandle(chatView.Input, key, upDownJump: false, out _))
            return;
        switch (key)
        {
            case Key.Enter or Key.KeypadEnter:
                SubmitChat();
                break;
            case Key.Up:
                chatView.HistoryUp();
                break;
            case Key.Down:
                chatView.HistoryDown();
                break;
        }
    }

    private void SubmitChat()
    {
        var text = chatView.Submit();
        if (string.IsNullOrEmpty(text)) return;
        // A command line runs locally in singleplayer and goes up to a server otherwise (which
        // runs it for that player); either way it is never echoed as chat
        if (text[0] == '/')
        {
            if (session.Client is { Status: NetworkStatus.Handshaken })
                session.SendChat(text);
            else
                RunCommand(text);
            return;
        }
        session.SendChat(text);
    }

    /// <summary>The Tab player list's lines: the local player first, then the server's others.</summary>
    internal IReadOnlyList<string> PlayerListNames()
    {
        _playerNames.Clear();
        _playerNames.Add($"{chatView.LocalName} (you)");
        if (ActiveWorld == null) return _playerNames;
        foreach (var remote in ActiveWorld.RemotePlayers)
            _playerNames.Add(remote.Name);
        return _playerNames;
    }

    private readonly List<string> _playerNames = [];

    /// <summary>
    /// Runs one chat command locally, on the same table a server uses (<see cref="ChatCommands"/>):
    /// <c>/tp</c> and <c>/give</c> need creative mode, <c>/time</c>, <c>/locate</c> and <c>/help</c>
    /// do not, and every answer is a system chat line (<c>/locate</c>'s with a link).
    /// </summary>
    private void RunCommand(string line)
    {
        if (ActiveWorld is not { } w) return;
        var parsed = ChatCommands.Parse(line);
        switch (parsed.Kind)
        {
            case ChatCommands.CommandKind.Time:
                if (parsed.Args.Length == 0 || ChatCommands.TimeSeconds(parsed.Args[0]) is not { } seconds)
                {
                    chatView.AddSystem("Usage: /time day|night|<seconds>");
                    return;
                }
                w.SetLightingTime(seconds);
                chatView.AddSystem($"Time set to {seconds:0.#} s");
                return;

            case ChatCommands.CommandKind.Teleport:
                if (!w.Camera.IsCreative)
                {
                    chatView.AddSystem("/tp needs creative mode");
                    return;
                }
                if (ChatCommands.TeleportTarget(parsed.Args) is not { } target)
                {
                    chatView.AddSystem("Usage: /tp x y z");
                    return;
                }
                w.Camera.TestTeleport(target, w.Camera.Yaw, w.Camera.Pitch);
                chatView.AddSystem($"Teleported to {target.X:0.#} {target.Y:0.#} {target.Z:0.#}");
                return;

            case ChatCommands.CommandKind.Give:
                if (!w.Camera.IsCreative)
                {
                    chatView.AddSystem("/give needs creative mode");
                    return;
                }
                if (ChatCommands.GiveRequest(parsed.Args) is not { } request)
                {
                    chatView.AddSystem("Usage: /give <item> [count]");
                    return;
                }
                if (!TryResolveContent(request.Item, out var id))
                {
                    chatView.AddSystem($"Unknown item '{request.Item}'");
                    return;
                }
                w.CreditItem(id, request.Count);
                chatView.AddSystem($"Gave {request.Count} x {request.Item}");
                return;

            case ChatCommands.CommandKind.Locate:
            {
                // A client holds no seed to search worldgen with; its '/' lines go to the server, which answers
                if (w.IsClientMode)
                {
                    chatView.AddSystem("/locate needs a connection to the server");
                    return;
                }
                var (text, link) = ChatCommands.Locate(parsed.Args, w.Camera.Position, w.Towers);
                chatView.AddSystem(text, link);
                return;
            }

            default:
                chatView.AddSystem(ChatCommands.HelpText);
                return;
        }
    }

    // An item or block by plain name (blocks first, like a loot entry) or by its block//item/
    // pinned name
    private static bool TryResolveContent(string name, out byte id)
    {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("block/", StringComparison.OrdinalIgnoreCase))
            return ContentRegistry.TryResolveBlock(trimmed[6..], out id);
        if (trimmed.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
            return ContentRegistry.TryResolveItem(trimmed[5..], out id);
        return ContentRegistry.TryResolveBlock(trimmed, out id) || ContentRegistry.TryResolveItem(trimmed, out id);
    }
}
