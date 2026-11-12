using System;
using VoxelEngine.Config;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>
/// The creative-flight pose (<see cref="PlayerModel"/> with <c>flying</c>): the legs trail back and bend and
/// the torso leans more the faster the body flies, never past the caps, and the pose eases in and out.
/// Behavior only: no pose numbers.
/// </summary>
public sealed class FlightPoseTests
{
    private const float Dt = 1f / 60f;

    private static PlayerModel Flown(float speed, float seconds = 3f, bool flying = true)
    {
        var model = new PlayerModel();
        for (var t = 0f; t < seconds; t += Dt)
            model.Update(Dt, 0f, grounded: true, flying: flying, flightSpeed: speed);
        return model;
    }

    // Mean over a sway period, so the sway itself averages out
    private static (float Hip, float Knee, float Lean) Averaged(float speed)
    {
        var model = Flown(speed);
        float hip = 0f, knee = 0f, lean = 0f;
        var n = 0;
        for (var t = 0f; t < 2f * MathF.PI / PlayerConfig.FlightSwayRate; t += Dt, n++)
        {
            model.Update(Dt, 0f, grounded: true, flying: true, flightSpeed: speed);
            var pose = model.GetPose();
            hip += (pose.LeftHip + pose.RightHip) * 0.5f;
            knee += (pose.LeftKnee + pose.RightKnee) * 0.5f;
            lean += pose.TorsoPitch;
        }
        return (hip / n, knee / n, lean / n);
    }

    [Fact]
    public void TheLegsTrailBentBackAndTheBodyLeansMoreTheFasterItFlies()
    {
        var hover = Averaged(0f);
        var cruise = Averaged(PlayerConfig.FlightFullSpeed * 0.5f);
        var full = Averaged(PlayerConfig.FlightFullSpeed);

        Assert.True(cruise.Hip < hover.Hip && full.Hip < cruise.Hip);
        Assert.True(cruise.Knee < hover.Knee && full.Knee < cruise.Knee);
        Assert.True(cruise.Lean < hover.Lean && full.Lean < cruise.Lean);
    }

    [Fact]
    public void NoSpeedBendsTheLegsOrLeansTheBodyPastTheCap()
    {
        var full = Averaged(PlayerConfig.FlightFullSpeed);
        var absurd = Averaged(PlayerConfig.FlightFullSpeed * 50f);

        Assert.Equal(full.Hip, absurd.Hip, 3);
        Assert.Equal(full.Knee, absurd.Knee, 3);
        Assert.Equal(full.Lean, absurd.Lean, 3);
    }

    [Fact]
    public void TheLegsSwayOutOfStepEvenWhenHovering()
    {
        var model = Flown(0f);
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var t = 0f; t < 2f * MathF.PI / PlayerConfig.FlightSwayRate; t += Dt)
        {
            model.Update(Dt, 0f, grounded: true, flying: true, flightSpeed: 0f);
            var pose = model.GetPose();
            var gap = pose.LeftHip - pose.RightHip;
            min = MathF.Min(min, gap);
            max = MathF.Max(max, gap);
        }
        Assert.True(max - min > 0.1f);
    }

    [Fact]
    public void TheFlightPoseEasesInAndOutInsteadOfSnapping()
    {
        var model = Flown(PlayerConfig.FlightFullSpeed, seconds: 2f);
        var flying = model.GetPose().TorsoPitch;
        model.Update(Dt, 0f, grounded: true, flying: false);
        var next = model.GetPose().TorsoPitch;

        Assert.True(MathF.Abs(next - flying) < 0.1f);
        for (var t = 0f; t < 3f; t += Dt)
            model.Update(Dt, 0f, grounded: true, flying: false);
        Assert.True(model.FlightBlend < 0.01f);
        Assert.True(MathF.Abs(model.GetPose().TorsoPitch) < MathF.Abs(flying));
    }
}
