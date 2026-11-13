using System;
using System.Collections.Generic;

namespace VoxelEngine.Commands.Arguments;

/// <summary>
/// What one argument of a command accepts (a player, a creature, an item, a number, coordinates, a word from a
/// list). It has the two behaviors every tree walk needs: resolving the tokens it reads into a value (or an error a
/// player can read), and offering the completions for the word being typed. Parsing, completion, usage and help all
/// reach an argument only through this, so a new command reuses the types without any per-command parsing or UI code.
/// </summary>
public interface IArgumentType
{
    /// <summary>How many space-separated tokens the argument reads (coordinates read three).</summary>
    int TokenCount => 1;

    /// <summary>How the argument shows in a usage line.</summary>
    string Syntax(string name) => $"<{name}>";

    /// <summary>Resolves exactly <see cref="TokenCount"/> tokens, or says why they do not fit.</summary>
    bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error);

    /// <summary>Whether <paramref name="tokens"/> (fewer than <see cref="TokenCount"/>) can begin this argument, so
    /// the word being typed continues it. Only multi-token arguments are asked.</summary>
    bool CouldStart(ReadOnlySpan<string> tokens) => true;

    /// <summary>Adds the completions for the argument's token number <paramref name="tokenIndex"/>, given what is
    /// typed of it so far (ranking and filtering by <paramref name="typed"/> happen afterwards).</summary>
    void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into);
}
