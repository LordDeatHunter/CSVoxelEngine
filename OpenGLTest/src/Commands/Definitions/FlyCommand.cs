namespace VoxelEngine.Commands.Definitions;

/// <summary><c>/fly [on|off] [target]</c>: toggles (or sets) flight for the source or a named player. Flight comes
/// with creative, as it does everywhere else; turning it off keeps the player's mode.</summary>
public static class FlyCommand
{
    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("fly").Requires(CommandPermission.Operator).Describe("Toggle flight")
            .Executes(c => Set(c, c.Self, !c.Self.IsFlying))
            .Then(Arg.Word("state", "on", "off").Executes(c => Set(c, c.Self, c.Get<string>("state") == "on"))
                .Then(Arg.Player("target").Executes(c => Set(c, c.Player("target"), c.Get<string>("state") == "on")))));

    private static void Set(CommandContext context, ICommandPlayer player, bool flying)
    {
        if (!player.TrySetModes(flying || player.IsCreative, flying, out var refusal))
            throw new CommandException(refusal);
        context.Reply($"{player.Name} can {(flying ? "now" : "no longer")} fly");
    }
}
