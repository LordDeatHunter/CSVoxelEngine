namespace VoxelEngine.Commands.Definitions;

/// <summary><c>/op &lt;name&gt;</c> and <c>/deop &lt;name&gt;</c>: grant or take a player's operator rights (the
/// operator commands, and creative). Multiplayer only: singleplayer has no operator list.</summary>
public static class OperatorCommands
{
    public static void Register(CommandDispatcher dispatcher)
    {
        dispatcher.Register(Cmd.Literal("op").Requires(CommandPermission.Operator).MultiplayerOnly()
            .Describe("Make a player an operator")
            .Then(Arg.PlayerName("name").Executes(Op)));
        dispatcher.Register(Cmd.Literal("deop").Requires(CommandPermission.Operator).MultiplayerOnly()
            .Describe("Take a player's operator rights")
            .Then(Arg.PlayerName("name").Executes(Deop)));
    }

    private static void Op(CommandContext context)
    {
        var name = context.Get<string>("name");
        if (!Admin(context).TryOp(name, out var refusal))
            throw new CommandException(refusal);
        context.Reply($"{name} is now an operator");
    }

    private static void Deop(CommandContext context)
    {
        var name = context.Get<string>("name");
        if (!Admin(context).TryDeop(name, out var refusal))
            throw new CommandException(refusal);
        context.Reply($"{name} is no longer an operator");
    }

    // The dispatcher never runs a multiplayer-only command without an operator list
    private static IOperatorAdmin Admin(CommandContext context) =>
        context.Source.Host.Operators ?? throw new CommandException(CommandDispatcher.MultiplayerOnlyReply);
}
