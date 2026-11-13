using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoxelEngine.Commands.Arguments;

/// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/> (an <see cref="int"/>); offers no
/// completions, since a range is not a list to pick from.</summary>
public sealed class IntArgument(int min, int max) : IArgumentType
{
    public bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error)
    {
        if (int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && number >= min && number <= max)
        {
            value = number;
            error = "";
            return true;
        }
        value = 0;
        error = $"'{tokens[0]}' must be a whole number from {min} to {max}";
        return false;
    }

    public void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into)
    {
    }
}
