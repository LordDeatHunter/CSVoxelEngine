using System;
using System.Collections.Generic;

namespace VoxelEngine.Commands.Arguments;

/// <summary>
/// An online player, by name: an exact match (case-insensitive), else the one player whose name starts with what was
/// typed. Its value is the player's name as the host spells it; <see cref="CommandContext.Player"/> turns it into the
/// player when the line runs.
/// </summary>
public sealed class PlayerArgument : IArgumentType
{
    public bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error)
    {
        var token = tokens[0];
        var names = source.Host.PlayerNames;
        string? prefixMatch = null;
        var prefixMatches = 0;
        foreach (var name in names)
        {
            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase))
            {
                value = name;
                error = "";
                return true;
            }
            if (!name.StartsWith(token, StringComparison.OrdinalIgnoreCase)) continue;
            prefixMatch = name;
            prefixMatches++;
        }
        if (prefixMatches == 1)
        {
            value = prefixMatch!;
            error = "";
            return true;
        }
        value = "";
        error = prefixMatches > 1 ? $"'{token}' matches several players" : $"No player named '{token}' is online";
        return false;
    }

    public void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into)
    {
        foreach (var name in source.Host.PlayerNames)
            into.Add(new Suggestion(name));
    }
}
