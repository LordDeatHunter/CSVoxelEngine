using System;

namespace VoxelEngine.Commands;

/// <summary>
/// The fluent surface command trees are declared with: at any point of a chain, <see cref="Then{T}"/> names a step
/// that may come next (a <see cref="Cmd.Literal"/> word, or an <see cref="Arg"/> player, creature, item, number,
/// coordinates or word from a list), <see cref="Executes{T}"/> lets a line end there, and <see cref="Requires{T}"/>
/// gates a whole subtree. Each method returns the node it was called on, typed as it was, so chains nest:
/// <code>
/// Cmd.Literal("tp").Requires(CommandPermission.Operator)
///     .Then(Arg.Coordinates("pos").Executes(c => ...))
///     .Then(Arg.Player("target").Executes(c => ...)
///         .Then(Arg.Coordinates("pos").Executes(c => ...)));
/// </code>
/// </summary>
public static class CommandBuilder
{
    /// <summary>Adds a step that may follow this node; steps are tried in the order they were added.</summary>
    public static T Then<T>(this T node, CommandNode next) where T : CommandNode
    {
        node.AddChild(next);
        return node;
    }

    /// <summary>Lets a line end at this node, running <paramref name="handler"/>.</summary>
    public static T Executes<T>(this T node, Action<CommandContext> handler) where T : CommandNode
    {
        node.Handler = handler;
        return node;
    }

    /// <summary>Gates this node and everything after it behind <paramref name="permission"/>.</summary>
    public static T Requires<T>(this T node, CommandPermission permission) where T : CommandNode
    {
        node.Permission = permission;
        return node;
    }

    /// <summary>The line <c>/help</c> shows for a command.</summary>
    public static T Describe<T>(this T node, string description) where T : CommandNode
    {
        node.Description = description;
        return node;
    }

    /// <summary>Other names a root command answers to.</summary>
    public static LiteralNode Alias(this LiteralNode node, params string[] aliases)
    {
        foreach (var alias in aliases)
            node.AddAlias(alias);
        return node;
    }
}
