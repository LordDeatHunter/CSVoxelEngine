using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoxelEngine.Commands.Arguments;

/// <summary>A finite number with an optional fraction, read under the invariant culture (a <see cref="double"/>).</summary>
public sealed class NumberArgument : IArgumentType
{
    public bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error)
    {
        if (double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number))
        {
            value = number;
            error = "";
            return true;
        }
        value = 0.0;
        error = $"'{tokens[0]}' is not a number";
        return false;
    }

    public void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into)
    {
    }
}
