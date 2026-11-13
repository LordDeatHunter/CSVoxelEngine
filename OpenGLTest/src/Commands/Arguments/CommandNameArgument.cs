using System;
using System.Collections.Generic;

namespace VoxelEngine.Commands.Arguments;

/// <summary>The name (or an alias) of a command the source may run, in <paramref name="dispatcher"/>; its value is the
/// command's root node.</summary>
public sealed class CommandNameArgument(CommandDispatcher dispatcher) : IArgumentType
{
    public bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error)
    {
        var name = tokens[0].TrimStart('/');
        if (dispatcher.FindRoot(name) is { } root && source.Allows(root.Permission))
        {
            value = root;
            error = "";
            return true;
        }
        value = "";
        error = $"Unknown command '/{name}'";
        return false;
    }

    public void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into)
    {
        foreach (var root in dispatcher.Visible(source))
            foreach (var name in root.Names)
                into.Add(new Suggestion(name));
    }
}
