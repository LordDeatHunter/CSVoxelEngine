using System.Collections.Generic;

namespace VoxelEngine.Commands;

/// <summary>
/// What <see cref="CommandDispatcher.Suggest"/> offers for a line being typed: the completions for the word at the
/// caret, best fit first, the index that word starts at (a completion replaces the text from there to the word's
/// end), and the usage of the command being typed (null while typing the command's name, or on a plain chat line).
/// </summary>
public sealed record CommandSuggestions(int ReplaceStart, IReadOnlyList<Suggestion> Items, string? UsageHint)
{
    /// <summary>Nothing to offer: not a command line, or nothing the source may run.</summary>
    public static readonly CommandSuggestions None = new(0, [], null);
}
