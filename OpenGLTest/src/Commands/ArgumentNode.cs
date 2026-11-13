using VoxelEngine.Commands.Arguments;

namespace VoxelEngine.Commands;

/// <summary>An argument in a command tree: its <see cref="Type"/> reads the tokens and the value lands in the
/// <see cref="CommandContext"/> under the node's name.</summary>
public sealed class ArgumentNode(string name, IArgumentType type) : CommandNode(name)
{
    public IArgumentType Type { get; } = type;

    public override string Syntax => Type.Syntax(Name);
}
