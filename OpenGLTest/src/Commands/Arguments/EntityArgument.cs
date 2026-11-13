using System;
using System.Collections.Generic;
using VoxelEngine.Entities;

namespace VoxelEngine.Commands.Arguments;

/// <summary>A registered entity kind, by its file name (<see cref="EntityRegistry"/>), matched case-insensitively; its
/// value is the registered name.</summary>
public sealed class EntityArgument : IArgumentType
{
    public bool TryParse(ReadOnlySpan<string> tokens, CommandSource source, out object value, out string error)
    {
        foreach (var name in EntityRegistry.ByName.Keys)
        {
            if (!string.Equals(name, tokens[0], StringComparison.OrdinalIgnoreCase)) continue;
            value = name;
            error = "";
            return true;
        }
        value = "";
        error = $"Unknown creature '{tokens[0]}'";
        return false;
    }

    public void Suggest(CommandSource source, int tokenIndex, string typed, List<Suggestion> into)
    {
        foreach (var name in EntityRegistry.ByName.Keys)
            into.Add(new Suggestion(name));
    }
}
