using System;
using System.Collections.Generic;

namespace VoxelEngine.Commands;

/// <summary>
/// One step of a command tree: a fixed word (<see cref="LiteralNode"/>) or an argument (<see cref="ArgumentNode"/>).
/// Its children are the steps that may follow it, tried in the order they were added; a node with a
/// <see cref="Handler"/> is a place a line may end. Its <see cref="Permission"/> holds for its whole subtree, so a
/// child is never looser than its parent. Built through <see cref="CommandBuilder"/>, then read-only.
/// </summary>
public abstract class CommandNode(string name)
{
    private readonly List<CommandNode> _children = [];

    /// <summary>The literal's word, or the argument's name (the key its value is read back by).</summary>
    public string Name { get; } = name;

    public IReadOnlyList<CommandNode> Children => _children;

    /// <summary>What a line ending at this node does, or null when the line must go on.</summary>
    public Action<CommandContext>? Handler { get; internal set; }

    public CommandPermission Permission { get; internal set; }

    /// <summary>One line for <c>/help</c>; only a root's is shown.</summary>
    public string Description { get; internal set; } = "";

    /// <summary>How the node shows in a usage line.</summary>
    public abstract string Syntax { get; }

    internal void AddChild(CommandNode child) => _children.Add(child);
}
