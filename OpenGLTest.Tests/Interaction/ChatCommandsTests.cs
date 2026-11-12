using System;
using System.Globalization;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Interaction;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The chat command table (<see cref="ChatCommands"/>): plain lines are not commands, the words
/// parse case-insensitively with their arguments, the time arguments resolve to cycle points and
/// seconds, the operator commands are the ones that need rights, and a suggested teleport parses
/// back to its eye whatever the culture.
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

        var locate = ChatCommands.Parse("/LOCATE Tower");
        Assert.Equal(ChatCommands.CommandKind.Locate, locate.Kind);
        Assert.Equal(["Tower"], locate.Args);
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
        Assert.False(ChatCommands.RequiresOperator(ChatCommands.CommandKind.Locate));
        Assert.False(ChatCommands.RequiresOperator(ChatCommands.CommandKind.Unknown));
    }

    [Fact]
    public void TheSuggestedTeleportParsesBackToItsEyeUnderAnyCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // A culture whose decimal separator is a comma must not leak into the command
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Vector3[] eyes = [new(2875.5f, 12.62f, -1410.5f), new(-0.25f, 300f, 7.75f), new(0.5f, -401.38f, -9999.5f)];
            foreach (var eye in eyes)
            {
                var parsed = ChatCommands.Parse(ChatCommands.TeleportCommand(eye));
                Assert.Equal(ChatCommands.CommandKind.Teleport, parsed.Kind);
                var target = ChatCommands.TeleportTarget(parsed.Args);
                Assert.NotNull(target);
                Assert.InRange(MathF.Abs(target!.Value.X - eye.X), 0f, 0.006f);
                Assert.InRange(MathF.Abs(target.Value.Y - eye.Y), 0f, 0.006f);
                Assert.InRange(MathF.Abs(target.Value.Z - eye.Z), 0f, 0.006f);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
