using System;
using System.Collections.Generic;

namespace VoxelEngine.Commands;

/// <summary>A fixed word in a command tree (a command's name, a subcommand such as <c>/time set</c>), matched
/// case-insensitively. A root may carry aliases: the same node under other names, so an alias parses, completes and
/// helps exactly like its command.</summary>
public sealed class LiteralNode(string name) : CommandNode(name)
{
    private readonly List<string> _names = [name];

    /// <summary>The word and then its aliases.</summary>
    public IReadOnlyList<string> Names => _names;

    public override string Syntax => Name;

    public bool Matches(string token)
    {
        foreach (var candidate in _names)
            if (string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    internal void AddAlias(string alias) => _names.Add(alias);
}
