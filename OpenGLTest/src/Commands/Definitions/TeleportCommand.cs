using System.Globalization;
using System.Numerics;

namespace VoxelEngine.Commands.Definitions;

/// <summary>
/// <c>/tp &lt;x y z&gt;</c> moves the source, <c>/tp &lt;target&gt;</c> moves the source to a player, and
/// <c>/tp &lt;target&gt; &lt;x y z&gt;|&lt;destination&gt;</c> moves another player. Coordinates name where the feet
/// land; <c>~</c> is relative to the source. Also answers to <c>/teleport</c>.
/// </summary>
public static class TeleportCommand
{
    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("tp").Alias("teleport").Requires(CommandPermission.Operator)
            .Describe("Teleport yourself or a player to a place or to a player")
            .Then(Arg.Coordinates("pos").Executes(c => Move(c, c.Self, c.Get<Vector3>("pos"))))
            .Then(Arg.Player("target")
                .Executes(c => Move(c, c.Self, c.Player("target").Feet))
                .Then(Arg.Coordinates("pos").Executes(c => Move(c, c.Player("target"), c.Get<Vector3>("pos"))))
                .Then(Arg.Player("destination").Executes(c => Move(c, c.Player("target"), c.Player("destination").Feet)))));

    /// <summary>The <c>/tp</c> line that puts the source's feet at <paramref name="feet"/> (within two decimals), under
    /// any culture: what a chat link suggests.</summary>
    public static string LineTo(Vector3 feet) =>
        string.Create(CultureInfo.InvariantCulture, $"/tp {feet.X:0.##} {feet.Y:0.##} {feet.Z:0.##}");

    private static void Move(CommandContext context, ICommandPlayer player, Vector3 feet)
    {
        player.Teleport(feet);
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Teleported {player.Name} to {feet.X:0.#} {feet.Y:0.#} {feet.Z:0.#}"));
    }
}
