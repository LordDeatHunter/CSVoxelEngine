namespace VoxelEngine.Commands.Definitions;

/// <summary><c>/kill [target]</c>: kills the source or a named player, whatever its mode or armor; the death is
/// answered by the usual death message.</summary>
public static class KillCommand
{
    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("kill").Requires(CommandPermission.Operator).Describe("Kill yourself or a player")
            .Executes(c => Kill(c.Self))
            .Then(Arg.Player("target").Executes(c => Kill(c.Player("target")))));

    private static void Kill(ICommandPlayer player)
    {
        if (!player.Kill())
            throw new CommandException($"{player.Name} is already dead");
    }
}
