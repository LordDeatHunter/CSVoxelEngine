using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="RenderManager.StepShadowCentre"/> carries the shadow box's centre forward frame to
/// frame, and the property that matters is subtle: it quantizes the <em>movement</em> of the centre,
/// never its position. Whole shadow-texel lateral steps are invisible - the geometry and the lookup
/// shift by the same integer count of texels - while any fractional texel in the move resamples the
/// whole map and every shadow edge twitches at the frame rate.
///
/// That distinction is the whole fix. Deciding the centre from scratch each frame means asking which
/// lattice vertex is nearest, in a lattice that turns with the sun: the answer hops by a fractional
/// texel every single frame, so shadows twitch, and the further the camera sits from the world
/// origin the faster the lattice sweeps past and the worse it gets (measured at 12% of the frame's
/// shadowed pixels changing per frame, against 0.03% once the move is what gets quantized).
/// <c>ATurningSunNeverMovesTheCentreOfAStillCamera</c> is that exact scenario and fails on any
/// from-scratch snap; the rest pin what the stepping still owes the caller - whole-texel moves, a
/// centre that stays half a texel of the desired point, and a depth that tracks it exactly so the
/// near/far band keeps following the player.
/// </summary>
public sealed class ShadowCentreTests
{
    private const float Radius = WorldGenerationConfig.ShadowRadiusBlocks;
    private const int MapSize = WorldGenerationConfig.ShadowMapSize;
    private static readonly float Texel = 2f * Radius / MapSize;

    /// <summary>The same orbit <c>RenderManager.SetLighting</c> runs, whose fixed Z tilt keeps the
    /// sun clear of vertical so the light look-at stays well defined.</summary>
    private static Vector3 SunAt(float angle) =>
        Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));

    private static readonly Vector3[] Suns = [SunAt(1.2f), SunAt(2.4f), SunAt(0.4f)];

    /// <summary>The light's own lateral axes, which are what the steps are counted in.
    /// <see cref="Vector3.Transform(Vector3, Matrix4x4)"/> treats a matrix as row-vector form, so
    /// the camera's basis vectors in world space are its COLUMNS.</summary>
    private static (Vector3 Right, Vector3 Up) LightAxes(Vector3 sun)
    {
        var rotation = Matrix4x4.CreateLookAt(Vector3.Zero, -sun, Vector3.UnitY);
        return (new Vector3(rotation.M11, rotation.M21, rotation.M31),
                new Vector3(rotation.M12, rotation.M22, rotation.M32));
    }

    /// <summary>How far a delta leans off a whole number of texels along one axis.</summary>
    private static float TexelFraction(Vector3 delta, Vector3 axis)
    {
        var cells = Vector3.Dot(delta, axis) / Texel;
        return cells - MathF.Round(cells);
    }

    private static Vector3 Step(Vector3 previous, Vector3 desired, Vector3 sun) =>
        RenderManager.StepShadowCentre(previous, desired, sun, Radius, MapSize);

    [Fact]
    public void ATurningSunNeverMovesTheCentreOfAStillCamera()
    {
        // A still camera over many frames means many slightly different sun directions. A centre
        // re-derived from the lattice each frame would hop on every one of them; a centre that is
        // only moved when the camera moves must not budge at all.
        var desired = new Vector3(40.5f, 14.5f, -12.25f);
        var centre = desired;

        for (var frame = 0; frame < 400; frame++)
        {
            var sun = SunAt(1.0f + frame * 0.0002f);
            var next = Step(centre, desired, sun);
            Assert.Equal(centre, next);
            centre = next;
        }
    }

    [Fact]
    public void SubTexelJitterInTheDesiredPointDoesNotMoveTheCentre()
    {
        var sun = SunAt(1.7f);
        var (right, up) = LightAxes(sun);
        var anchor = new Vector3(12.5f, 40f, -7.25f);
        var centre = Step(anchor, anchor, sun);

        foreach (var wobble in new[] { 0.45f, -0.45f, 0.3f, -0.3f, 0.4f, -0.4f })
        {
            var desired = anchor + right * (Texel * wobble) + up * (Texel * wobble);
            // A thousandth of a texel of slack for float noise, far below the fraction of a texel
            // a from-scratch snap would move it by.
            Assert.True(Vector3.Distance(centre, Step(centre, desired, sun)) < Texel * 0.001f);
        }

        // ... and a real move must still be followed, or the fact would pass on a frozen centre.
        var moved = Step(centre, anchor + right * (Texel * 2f), sun);
        Assert.NotEqual(centre, moved);
        Assert.InRange(Vector3.Dot(moved - centre, right) / Texel, 1.95f, 2.05f);
    }

    [Fact]
    public void WalkingMovesTheCentreOnlyInWholeTexels()
    {
        var sun = SunAt(2.1f);
        var (right, up) = LightAxes(sun);
        var centre = new Vector3(40.5f, 14.5f, -12.25f);
        var desired = centre;
        var step = new Vector3(0.0125f, 0.004f, 0.0075f);
        var moves = 0;

        for (var frame = 0; frame < 400; frame++)
        {
            var next = Step(centre, desired, sun);
            var delta = next - centre;
            if (delta.LengthSquared() > 0f) moves++;
            Assert.InRange(MathF.Abs(TexelFraction(delta, right)), 0f, 0.02f);
            Assert.InRange(MathF.Abs(TexelFraction(delta, up)), 0f, 0.02f);
            centre = next;
            desired += step;
        }

        Assert.True(moves > 10, $"only {moves} moves, so the centre is not following a walking camera");
        Assert.True(moves < 400, $"every frame moved the centre, which is what makes shadows crawl");
    }

    [Fact]
    public void TheCentreStaysWithinHalfATexelOfTheDesiredPoint()
    {
        var sun = SunAt(0.9f);
        var centre = new Vector3(-120.5f, 30f, 88.25f);
        var desired = centre;
        var step = new Vector3(0.031f, -0.02f, 0.017f);

        for (var frame = 0; frame < 500; frame++)
        {
            centre = Step(centre, desired, sun);
            var offset = centre - desired;
            var lateral = offset - sun * Vector3.Dot(offset, sun);
            // Half a texel per axis, so at most half a texel diagonal: the box always holds the
            // player, and the discarded sub-texel remainder never accumulates.
            Assert.InRange(lateral.Length(), 0f, Texel * 0.75f);
            desired += step;
        }
    }

    [Fact]
    public void TheDepthOfTheCentreTracksTheDesiredPointExactly()
    {
        // Lateral moves are quantized, depth is not: the box's near/far band has to follow the
        // player when they fall or fly, however small the step.
        var sun = SunAt(2.6f);
        var desired = new Vector3(8.5f, 20f, -3.25f);
        var centre = Step(desired, desired, sun);

        foreach (var drop in new[] { 0.01f, 0.05f, 0.2f, 1f })
        {
            desired += sun * drop;
            centre = Step(centre, desired, sun);
            Assert.InRange(MathF.Abs(Vector3.Dot(centre - desired, sun)), 0f, 1e-3f);
        }
    }

    [Fact]
    public void AJumpInTheDesiredPointIsFollowedInOneStep()
    {
        // Teleports and respawns: the centre must land back on the camera rather than walk there.
        foreach (var sun in Suns)
        {
            var centre = new Vector3(4.5f, 30f, 4.5f);
            var jump = new Vector3(1800.5f, 48f, -950.25f);
            var next = Step(centre, jump, sun);
            var offset = next - jump;
            var lateral = offset - sun * Vector3.Dot(offset, sun);
            Assert.InRange(lateral.Length(), 0f, Texel * 0.75f);
        }
    }
}
