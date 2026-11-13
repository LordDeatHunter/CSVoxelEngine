using System.Numerics;

namespace VoxelEngine.Commands.Definitions;

/// <summary><c>/summon &lt;entity&gt; [x y z]</c>: spawns one mob of a registered kind where the source stands, or at
/// the coordinates (its feet there).</summary>
public static class SummonCommand
{
    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("summon").Requires(CommandPermission.Operator).Describe("Spawn a creature")
            .Then(Arg.Entity("entity").Executes(c => Summon(c, c.Source.Feet))
                .Then(Arg.Coordinates("pos").Executes(c => Summon(c, c.Get<Vector3>("pos"))))));

    private static void Summon(CommandContext context, Vector3 feet)
    {
        var entity = context.Get<string>("entity");
        if (!context.Source.Host.SpawnEntity(entity, feet))
            throw new CommandException($"Could not summon {entity} here");
        context.Reply($"Summoned {entity}");
    }
}
