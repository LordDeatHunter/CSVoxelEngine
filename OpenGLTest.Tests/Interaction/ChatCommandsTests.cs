using VoxelEngine.Effects.Sky;
using VoxelEngine.Interaction;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The chat command table (<see cref="ChatCommands"/>): plain lines are not commands, the words
/// parse case-insensitively with their arguments, the time arguments resolve to cycle points and
/// seconds, and the operator commands are the ones that need rights.
/// </summary>
public sealed class ChatCommandsTests
{
    [Fact]
    public void PlainLinesAreNotCommands()
    {
        Assert.Equal(ChatCommands.CommandKind.None, ChatCommands.Parse("hello there").Kind);
        Assert.Equal(ChatCommands.CommandKind.None, ChatCommands.Parse("").Kind);
        Assert.Equal(ChatCommands.CommandKind.Unknown, ChatCommands.Parse("/").Kind);
        Assert.Equal(ChatCommands.CommandKind.Unknown, ChatCommands.Parse("/dance").Kind);
    }

    [Fact]
    public void CommandsParseCaseInsensitivelyWithTheirArguments()
    {
        var time = ChatCommands.Parse("/TIME day");
        Assert.Equal(ChatCommands.CommandKind.Time, time.Kind);
        Assert.Equal(["day"], time.Args);

        var tp = ChatCommands.Parse("/tp 10 -20 30.5");
        Assert.Equal(ChatCommands.CommandKind.Teleport, tp.Kind);
        Assert.Equal(3, tp.Args.Length);

        var give = ChatCommands.Parse("  /give  block/cobblestone   12  ");
        Assert.Equal(ChatCommands.CommandKind.Give, give.Kind);
        Assert.Equal(["block/cobblestone", "12"], give.Args);

        Assert.Equal(ChatCommands.CommandKind.Help, ChatCommands.Parse("/help").Kind);
    }

    [Fact]
    public void TimeResolvesToTheCyclePointsAndSeconds()
    {
        Assert.Equal(DayCycle.NoonTime, ChatCommands.TimeSeconds("day"));
        Assert.Equal(Clock.MidnightTime, ChatCommands.TimeSeconds("night"));
        Assert.Equal(DayCycle.NoonTime, ChatCommands.TimeSeconds("DAY"));
        Assert.Equal(120.5, ChatCommands.TimeSeconds("120.5"));
        Assert.Equal(-30.0, ChatCommands.TimeSeconds("-30"));
        Assert.Null(ChatCommands.TimeSeconds("dusk"));
        Assert.Null(ChatCommands.TimeSeconds(""));
    }

    [Fact]
    public void TeleportAndGiveParseTheirArguments()
    {
        var target = ChatCommands.TeleportTarget(["10", "-20", "30.5"]);
        Assert.NotNull(target);
        Assert.Equal(10f, target!.Value.X);
        Assert.Equal(-20f, target.Value.Y);
        Assert.Equal(30.5f, target.Value.Z);
        Assert.Null(ChatCommands.TeleportTarget(["10", "-20"]));
        Assert.Null(ChatCommands.TeleportTarget(["10", "x", "30"]));

        var give = ChatCommands.GiveRequest(["flint"]);
        Assert.Equal(("flint", 1), give);
        Assert.Equal(("flint", 12), ChatCommands.GiveRequest(["flint", "12"]));
        Assert.Equal(("flint", 1), ChatCommands.GiveRequest(["flint", "0"]));
        Assert.Null(ChatCommands.GiveRequest([]));
        Assert.Null(ChatCommands.GiveRequest(["flint", "many"]));
    }

    [Fact]
    public void OnlyTeleportAndGiveNeedRights()
    {
        Assert.True(ChatCommands.RequiresOperator(ChatCommands.CommandKind.Teleport));
        Assert.True(ChatCommands.RequiresOperator(ChatCommands.CommandKind.Give));
        Assert.False(ChatCommands.RequiresOperator(ChatCommands.CommandKind.Time));
        Assert.False(ChatCommands.RequiresOperator(ChatCommands.CommandKind.Help));
        Assert.False(ChatCommands.RequiresOperator(ChatCommands.CommandKind.Unknown));
    }
}
