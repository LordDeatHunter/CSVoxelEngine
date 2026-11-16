using System;
using System.Collections.Generic;

namespace VoxelEngine.Commands.Arguments;

/// <summary>
/// A player's name, online or not (an operator is granted by name before the player ever joins): any one word, taken
/// as typed. It completes the names of the players online.
/// </summary>
public sealed class PlayerNameArgument : IArgumentType
{
    public bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error)
    {
        value = tokens[0];
        error = "";
        return true;
    }

    public void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into)
    {
        foreach (var name in source.Host.PlayerNames)
            into.Add(new Suggestion(name));
    }
}
