using VoxelEngine.Commands.Arguments;

namespace VoxelEngine.Commands.Definitions;

/// <summary><c>/give &lt;item&gt; [count] [target]</c>: puts blocks or items into the source's inventory, or a named
/// player's; one unless a count is given.</summary>
public static class GiveCommand
{
    /// <summary>The most one <c>/give</c> grants.</summary>
    public const int MaxCount = 9999;

    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("give").Requires(CommandPermission.Operator).Describe("Give blocks or items")
            .Then(Arg.Item("item").Executes(c => Give(c, c.Self, 1))
                .Then(Arg.Int("count", 1, MaxCount).Executes(c => Give(c, c.Self, c.Get<int>("count")))
                    .Then(Arg.Player("target").Executes(c => Give(c, c.Player("target"), c.Get<int>("count")))))));

    private static void Give(CommandContext context, ICommandPlayer player, int count)
    {
        var item = context.Get<ContentItem>("item");
        player.Give(item.Id, count);
        context.Reply($"Gave {count} x {item.Name} to {player.Name}");
    }
}
