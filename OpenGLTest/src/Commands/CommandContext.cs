using System;
using System.Collections.Generic;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Commands;

/// <summary>A parsed line handed to a node's handler: who ran it and the argument values by name.</summary>
public sealed class CommandContext(CommandSource source)
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public CommandSource Source { get; } = source;

    /// <summary>The player the source acts on by default; a source with none cannot run a command that needs one.</summary>
    public ICommandPlayer Self => Source.Self ?? throw new CommandException("Only a player can do that");

    /// <summary>The value the argument named <paramref name="name"/> resolved to.</summary>
    public T Get<T>(string name) => (T)_values[name];

    /// <summary>The online player a player argument named; one who left since the line was parsed is an error.</summary>
    public ICommandPlayer Player(string name)
    {
        var player = Get<string>(name);
        return Source.Host.FindPlayer(player) ?? throw new CommandException($"{player} is not online");
    }

    /// <summary>A system line to the source.</summary>
    public void Reply(string text, ChatLink? link = null) => Source.Reply(text, link);

    internal void Set(string name, object value) => _values[name] = value;
}
