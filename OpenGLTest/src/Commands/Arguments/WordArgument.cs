using System;
using System.Collections.Generic;

namespace VoxelEngine.Commands.Arguments;

/// <summary>One word from a fixed list (<c>survival|creative</c>, <c>on|off</c>), matched case-insensitively; its
/// value is the option as listed.</summary>
public sealed class WordArgument(params string[] options) : IArgumentType
{
    public string Syntax(string name) => string.Join("|", options);

    public bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error)
    {
        foreach (var option in options)
        {
            if (!string.Equals(option, tokens[0], StringComparison.OrdinalIgnoreCase)) continue;
            value = option;
            error = "";
            return true;
        }
        value = "";
        error = $"Expected {string.Join(", ", options)}, not '{tokens[0]}'";
        return false;
    }

    public void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into)
    {
        foreach (var option in options)
            into.Add(new Suggestion(option));
    }
}
