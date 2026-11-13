using System;
using System.Globalization;
using VoxelEngine.Effects.Sky;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Commands.Definitions;

/// <summary><c>/time sunrise|noon|day|sunset|night|midnight</c>, <c>/time set &lt;seconds&gt;</c> and
/// <c>/time add &lt;seconds&gt;</c>: set the lighting clock for everyone (seconds in the units <c>VOXEL_TIME</c> uses,
/// one full cycle being <see cref="SkyConfig.DayLengthSeconds"/>).</summary>
public static class TimeCommand
{
    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("time").Requires(CommandPermission.Operator).Describe("Set the time of day")
            .Then(Arg.Word("when", "sunrise", "noon", "day", "sunset", "night", "midnight")
                .Executes(c => Set(c, Named(c.Get<string>("when")))))
            .Then(Cmd.Literal("set")
                .Then(Arg.Number("seconds").Executes(c => Set(c, c.Get<double>("seconds")))))
            .Then(Cmd.Literal("add")
                .Then(Arg.Number("seconds").Executes(c => Set(c, c.Source.Host.LightingTime + c.Get<double>("seconds"))))));

    /// <summary>The lighting time a named point of the day stands for: day is noon, night is midnight, and sunrise and
    /// sunset sit a quarter cycle either side of noon.</summary>
    public static double Named(string when) => when switch
    {
        "sunrise" => DayCycle.NoonTime - SkyConfig.DayLengthSeconds * 0.25,
        "sunset" => DayCycle.NoonTime + SkyConfig.DayLengthSeconds * 0.25,
        "night" or "midnight" => Clock.MidnightTime,
        _ => DayCycle.NoonTime,
    };

    private static void Set(CommandContext context, double seconds)
    {
        context.Source.Host.SetLightingTime(seconds);
        context.Source.Host.Announce(string.Create(CultureInfo.InvariantCulture,
            $"{context.Source.Name} set the time to {Math.Round(seconds, 1)} s"));
    }
}
