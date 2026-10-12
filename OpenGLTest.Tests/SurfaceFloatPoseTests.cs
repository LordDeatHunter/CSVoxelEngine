using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The player's surface-float pose (<see cref="PlayerModel"/> with <c>floating</c>): upright and straight
/// like standing, the arms sculling low beside the hips, the legs hanging, blended in and out of the
/// swim pose and handed back to it for a stroke. Behavior only: no pose numbers.
/// </summary>
public sealed class SurfaceFloatPoseTests
{
    private const float Dt = 1f / 60f;
    private static readonly Vector3 Eye = new(0f, 1.62f, 0f);

    private static Vector3 Origin(PlayerModel model, string part) =>
        Vector3.Transform(Vector3.Zero, model.GetPartMatrix(part, Eye, -90f, 0f));

    private static Vector3 Hand(PlayerModel model, string forearm) =>
        Vector3.Transform(new Vector3(0f, model.HandDepth(), 0f), model.GetPartMatrix(forearm, Eye, -90f, 0f));

    private static void Advance(PlayerModel model, float seconds, bool swimming, bool floating)
    {
        for (var t = 0f; t < seconds; t += Dt)
            model.Update(Dt, 0f, swimming: swimming, floating: floating);
    }

    private static PlayerModel Made(bool swimming, bool floating, float seconds = 2f)
    {
        var model = new PlayerModel();
        Advance(model, seconds, swimming, floating);
        return model;
    }

    // How far the head sits off the vertical through the shins, horizontally
    private static float Lean(PlayerModel model)
    {
        var head = Origin(model, "Head");
        var shins = (Origin(model, "ShinL") + Origin(model, "ShinR")) * 0.5f;
        return new Vector2(head.X - shins.X, head.Z - shins.Z).Length();
    }

    [Fact]
    public void TheFloatStandsTheBodyAsUprightAsStandingWhereTheSwimTipsIt()
    {
        var standing = Made(swimming: false, floating: false);
        var swimmer = Made(swimming: true, floating: false);
        var floater = Made(swimming: true, floating: true);

        Assert.True(floater.FloatBlend > 0.95f);
        Assert.True(Lean(swimmer) > Lean(standing) + 0.05f);
        Assert.InRange(Lean(floater), 0f, Lean(standing) + 0.05f);
    }

    [Fact]
    public void TheFloatersArmsScullLowBesideTheBodyAndItsLegsHang()
    {
        var swimmer = Made(swimming: true, floating: false);
        var floater = Made(swimming: true, floating: true);

        // The swimmer reaches its hands out ahead at shoulder height; the floater keeps them low,
        // below the shoulders and nearer the body
        float Below(PlayerModel m) => Origin(m, "UpperArmR").Y - Hand(m, "ForearmR").Y;
        float Ahead(PlayerModel m) => Origin(m, "UpperArmR").Z - Hand(m, "ForearmR").Z;
        Assert.True(Below(floater) > Below(swimmer) + 0.2f);
        Assert.True(Ahead(floater) < Ahead(swimmer) - 0.1f);

        // The hands sweep in and out over a few seconds: a sculling motion, not a held pose
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        for (var t = 0f; t < 4f; t += Dt)
        {
            floater.Update(Dt, 0f, swimming: true, floating: true);
            var x = Hand(floater, "ForearmR").X;
            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
        }
        Assert.True(maxX - minX > 0.05f);

        // The legs hang nearly straight instead of the swimmer's drawn-up kick
        Assert.True(floater.GetPose().LeftKnee > swimmer.GetPose().LeftKnee + 0.2f);
    }

    [Fact]
    public void AStrokeHandsTheFloaterToTheSwimPoseAndItEasesBackWithoutSnapping()
    {
        var model = Made(swimming: true, floating: true);
        var lowest = model.FloatBlend;
        var largestStep = 0f;
        var last = model.FloatBlend;

        model.TriggerSwimStroke();
        for (var t = 0f; t < PlayerPhysics.SwimStrokeSeconds + 2f; t += Dt)
        {
            model.Update(Dt, 0f, swimming: true, floating: true);
            lowest = MathF.Min(lowest, model.FloatBlend);
            largestStep = MathF.Max(largestStep, MathF.Abs(model.FloatBlend - last));
            last = model.FloatBlend;
        }

        Assert.True(lowest < 0.3f, "the stroke never showed the swim pose");
        Assert.True(model.FloatBlend > 0.9f, "the float pose did not come back after the stroke");
        Assert.True(largestStep < 0.15f, $"the float blend jumped by {largestStep} in a frame");

        // Swimming without floating, and leaving the water, both ease it out
        Advance(model, 2f, swimming: true, floating: false);
        Assert.True(model.FloatBlend < 0.05f);
        var floater = Made(swimming: true, floating: true);
        Advance(floater, 2f, swimming: false, floating: false);
        Assert.True(floater.FloatBlend < 0.05f);
    }

    [Fact]
    public void TheDebuggersSurfaceModeShowsTheFloat()
    {
        var model = new PlayerModel();
        var state = new PlayerPreviewState(model);
        Assert.True(state.SetMode("SURFACE"));
        Assert.True(state.IsContinuous);
        for (var t = 0f; t < 2f; t += Dt) state.Update(Dt);
        Assert.True(state.Model.FloatBlend > 0.9f);
        Assert.Equal(1f, state.Model.SwimStrokeProgress);
    }
}
