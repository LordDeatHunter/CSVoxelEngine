namespace VoxelEngine.Commands;

/// <summary>Starts a fixed-word step of a command tree: a command's name at the root, a subcommand below it. The
/// argument steps start from <see cref="Arg"/>; both chain through <see cref="CommandBuilder"/>.</summary>
public static class Cmd
{
    public static LiteralNode Literal(string word) => new(word);
}
