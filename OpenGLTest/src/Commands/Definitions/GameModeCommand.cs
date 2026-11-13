namespace VoxelEngine.Commands.Definitions;

/// <summary><c>/gamemode survival|creative [target]</c>: switches the source, or a named player, between survival
/// and creative. Leaving creative also ends flight; a server refuses creative to a player it does not let be
/// creative.</summary>
public static class GameModeCommand
{
    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("gamemode").Requires(CommandPermission.Operator).Describe("Switch between survival and creative")
            .Then(Arg.Word("mode", "survival", "creative").Executes(c => Set(c, c.Self))
                .Then(Arg.Player("target").Executes(c => Set(c, c.Player("target"))))));

    private static void Set(CommandContext context, ICommandPlayer player)
    {
        var mode = context.Get<string>("mode");
        var creative = mode == "creative";
        if (!player.TrySetModes(creative, creative && player.IsFlying, out var refusal))
            throw new CommandException(refusal);
        context.Reply($"{player.Name} is now in {mode}");
    }
}
