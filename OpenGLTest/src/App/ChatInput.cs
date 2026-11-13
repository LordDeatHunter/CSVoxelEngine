using System;
using System.Collections.Generic;
using Silk.NET.Input;
using VoxelEngine.Commands;
using VoxelEngine.Networking.Client;
using VoxelEngine.Platform;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.App;

/// <summary>The chat's input: the keys an open chat reacts to, submitting a line (to the server, or a local echo or command in singleplayer), the command line's autocomplete (Tab, Shift+Tab) and the Tab player list's names. Owned by Program; main thread only.</summary>
internal sealed class ChatInput
{
    private readonly ChatView chatView;
    private readonly ClientSession session;

    private readonly TextFieldKeys textKeys;
    private readonly CommandDispatcher commands = GameCommands.Create();
    // Built once, so a frame whose line did not change allocates nothing
    private readonly Func<CommandSource> commandSource;

    internal ChatInput(ChatView chatView, ClientSession session, InputManager inputManager)
    {
        this.chatView = chatView;
        this.session = session;
        textKeys = new TextFieldKeys(inputManager);
        commandSource = CommandSource;
    }

    private World? ActiveWorld => Program.ActiveWorld;

    private bool Connected => session.Client is { Status: NetworkStatus.Handshaken };

    /// <summary>The keys the chat input reacts to while it is open. ESC is left to the polled
    /// handler, so closing chat and the pause menu never both fire on one press.</summary>
    internal void HandleChatKey(Key key)
    {
        if (key == Key.Tab)
        {
            CycleCompletion(textKeys.ShiftHeld ? -1 : 1);
            return;
        }
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

    /// <summary>Brings the autocomplete up to date with the open input; called once a frame before the chat draws.</summary>
    internal void SyncCompletion()
    {
        if (!chatView.IsOpen || ActiveWorld == null) return;
        chatView.Completion.Sync(chatView.Input, commands, commandSource);
    }

    /// <summary>A scenario's chat step (<see cref="VisualTest.IScenarioUi.Chat"/>): the keys a player would press, the
    /// autocomplete synced after each as the frame would.</summary>
    internal void Scripted(string action, string? text)
    {
        switch (action)
        {
            case "type":
                if (!chatView.IsOpen) chatView.Open();
                foreach (var c in text ?? "")
                    chatView.Type(c);
                break;
            case "tab" or "backtab":
                CycleCompletion(action == "tab" ? 1 : -1);
                break;
            case "send":
                SubmitChat();
                break;
            case "close":
                chatView.Close();
                break;
            default:
                throw new InvalidOperationException($"'{action}' is not a chat action.");
        }
        SyncCompletion();
    }

    // Tab and Shift+Tab: the line must be synced first, since a key can arrive before the frame's sync
    private void CycleCompletion(int step)
    {
        SyncCompletion();
        chatView.Completion.Cycle(chatView.Input, step);
    }

    private void SubmitChat()
    {
        var text = chatView.Submit();
        if (string.IsNullOrEmpty(text)) return;
        // A command line runs locally in singleplayer and goes up to a server otherwise (which
        // runs it for that player); either way it is never echoed as chat
        if (text[0] != '/' || Connected)
        {
            session.SendChat(text);
            return;
        }
        if (ActiveWorld is not { } world) return;
        // A client world still waiting for its handshake has no server to run the line yet
        if (world.IsClientMode)
        {
            chatView.AddSystem("Commands need a connection to the server");
            return;
        }
        commands.Execute(text, CommandSource());
    }

    /// <summary>Who runs or completes a line: singleplayer's player, the operator of its own world, or, while connected,
    /// a client that only completes lines with the rights the server reported.</summary>
    private CommandSource CommandSource()
    {
        var world = ActiveWorld!;
        if (Connected)
        {
            return new CommandSource(chatView.LocalName,
                session.IsOperator ? CommandPermission.Operator : CommandPermission.Everyone,
                LocalCommandPlayer.FeetOf(world.Camera), null,
                new ClientCommandHost(world, chatView), chatView.AddSystem);
        }
        var host = new LocalCommandHost(world, chatView);
        return new CommandSource(chatView.LocalName, CommandPermission.Operator, host.Self.Feet, host.Self, host,
            chatView.AddSystem);
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
}
