namespace VoxelEngine.Commands.Definitions;

/// <summary><c>/help</c> lists every command the source may run with what it does; <c>/help &lt;command&gt;</c> shows
/// that command's usage. Written from the trees, so it lists exactly what the source can use.</summary>
public static class HelpCommand
{
    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("help").Describe("List the commands, or show how to use one")
            .Executes(c =>
            {
                foreach (var root in dispatcher.Visible(c.Source))
                    c.Reply($"/{root.Name} - {root.Description}");
            })
            .Then(Arg.Command("command", dispatcher).Executes(c =>
            {
                var root = c.Get<LiteralNode>("command");
                c.Reply($"{dispatcher.Usage(root, c.Source)} - {root.Description}");
            })));
}
