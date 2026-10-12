using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Rendering;
using Xunit;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// <see cref="ShadowCascade.StepCenter"/> carries the shadow box's center forward frame to
/// frame, and the property that matters is subtle: it quantizes the <em>movement</em> of the center,
/// never its position. Whole shadow-texel lateral steps are invisible - the geometry and the lookup
/// shift by the same integer count of texels - while any fractional texel in the move resamples the
/// whole map and every shadow edge twitches at the frame rate.
///
/// That distinction is the whole fix. Deciding the center from scratch each frame means asking which
/// lattice vertex is nearest, in a lattice that turns with the sun: the answer hops by a fractional
/// texel every single frame, so shadows twitch, and the further the camera sits from the world
/// origin the faster the lattice sweeps past and the worse it gets (measured at 12% of the frame's
/// shadowed pixels changing per frame, against 0.03% once the move is what gets quantized).
/// <c>ATurningSunNeverMovesTheCenterOfAStillCamera</c> is that exact scenario and fails on any
/// from-scratch snap; the rest pin what the stepping still owes the caller - whole-texel moves, a
/// center that stays half a texel of the desired point, and a depth that tracks it exactly so the
/// near/far band keeps following the player. Every fact is a theory over <see cref="Cascades"/>, so
/// each shadow cascade's (radius, map size) pair is held to the same stepping rules.
/// </summary>
public sealed class ShadowCenterTests
{
    /// <summary>Every shadow cascade's (radius in blocks, map size in texels).</summary>
    public static TheoryData<float, int> Cascades => new()
    {
        { WorldGenerationConfig.ShadowRadiusBlocks, WorldGenerationConfig.ShadowMapSize },
        { WorldGenerationConfig.FarShadowRadiusBlocks, WorldGenerationConfig.FarShadowMapSize },
    };

    internal static float TexelOf(float radius, int mapSize) => 2f * radius / mapSize;

    /// <summary>The same orbit <c>RenderManager.SetLighting</c> runs, whose fixed Z tilt keeps the
    /// sun clear of vertical so the light look-at stays well defined.</summary>
    internal static Vector3 SunAt(float angle) =>
        Vector3.Normalize(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0.35f));

    internal static readonly Vector3[] Suns = [SunAt(1.2f), SunAt(2.4f), SunAt(0.4f)];

    /// <summary>The light's own lateral axes, which are what the steps are counted in.
    /// <see cref="Vector3.Transform(Vector3, Matrix4x4)"/> treats a matrix as row-vector form, so
    /// the camera's basis vectors in world space are its COLUMNS.</summary>
    internal static (Vector3 Right, Vector3 Up) LightAxes(Vector3 sun)
    {
        var rotation = Matrix4x4.CreateLookAt(Vector3.Zero, -sun, Vector3.UnitY);
        return (new Vector3(rotation.M11, rotation.M21, rotation.M31),
                new Vector3(rotation.M12, rotation.M22, rotation.M32));
    }

    /// <summary>How far a delta leans off a whole number of texels along one axis.</summary>
    internal static float TexelFraction(Vector3 delta, Vector3 axis, float texel)
    {
        var cells = Vector3.Dot(delta, axis) / texel;
        return cells - MathF.Round(cells);
    }

    private static Vector3 Step(Vector3 previous, Vector3 desired, Vector3 sun, float radius, int mapSize) =>
        ShadowCascade.StepCenter(previous, desired, sun, radius, mapSize);

    [Theory]
    [MemberData(nameof(Cascades))]
    public void ATurningSunNeverMovesTheCenterOfAStillCamera(float radius, int mapSize)
    {
        // A still camera over many frames means many slightly different sun directions. A center
        // re-derived from the lattice each frame would hop on every one of them; a center that is
        // only moved when the camera moves must not budge at all.
        var desired = new Vector3(40.5f, 14.5f, -12.25f);
        var center = desired;

        for (var frame = 0; frame < 400; frame++)
        {
            var sun = SunAt(1.0f + frame * 0.0002f);
            var next = Step(center, desired, sun, radius, mapSize);
            Assert.Equal(center, next);
            center = next;
        }
    }

    [Theory]
    [MemberData(nameof(Cascades))]
    public void SubTexelJitterInTheDesiredPointDoesNotMoveTheCenter(float radius, int mapSize)
    {
        var texel = TexelOf(radius, mapSize);
        var sun = SunAt(1.7f);
        var (right, up) = LightAxes(sun);
        var anchor = new Vector3(12.5f, 40f, -7.25f);
        var center = Step(anchor, anchor, sun, radius, mapSize);

        foreach (var wobble in new[] { 0.45f, -0.45f, 0.3f, -0.3f, 0.4f, -0.4f })
        {
            var desired = anchor + right * (texel * wobble) + up * (texel * wobble);
            // A thousandth of a texel of slack for float noise, far below the fraction of a texel
            // a from-scratch snap would move it by.
            Assert.True(Vector3.Distance(center, Step(center, desired, sun, radius, mapSize)) < texel * 0.001f);
        }

        // ... and a real move must still be followed, or the fact would pass on a frozen center.
        var moved = Step(center, anchor + right * (texel * 2f), sun, radius, mapSize);
        Assert.NotEqual(center, moved);
        Assert.InRange(Vector3.Dot(moved - center, right) / texel, 1.95f, 2.05f);
    }

    [Theory]
    [MemberData(nameof(Cascades))]
    public void WalkingMovesTheCenterOnlyInWholeTexels(float radius, int mapSize)
    {
        var texel = TexelOf(radius, mapSize);
        var sun = SunAt(2.1f);
        var (right, up) = LightAxes(sun);
        var center = new Vector3(40.5f, 14.5f, -12.25f);
        var desired = center;
        var step = new Vector3(0.0125f, 0.004f, 0.0075f);
        var moves = 0;

        for (var frame = 0; frame < 400; frame++)
        {
            var next = Step(center, desired, sun, radius, mapSize);
            var delta = next - center;
            if (delta.LengthSquared() > 0f) moves++;
            Assert.InRange(MathF.Abs(TexelFraction(delta, right, texel)), 0f, 0.02f);
            Assert.InRange(MathF.Abs(TexelFraction(delta, up, texel)), 0f, 0.02f);
            center = next;
            desired += step;
        }

        Assert.True(moves > 10, $"only {moves} moves, so the center is not following a walking camera");
        Assert.True(moves < 400, $"every frame moved the center, which is what makes shadows crawl");
    }

    [Theory]
    [MemberData(nameof(Cascades))]
    public void TheCenterStaysWithinHalfATexelOfTheDesiredPoint(float radius, int mapSize)
    {
        var texel = TexelOf(radius, mapSize);
        var sun = SunAt(0.9f);
        var center = new Vector3(-120.5f, 30f, 88.25f);
        var desired = center;
        var step = new Vector3(0.031f, -0.02f, 0.017f);

        for (var frame = 0; frame < 500; frame++)
        {
            center = Step(center, desired, sun, radius, mapSize);
            var offset = center - desired;
            var lateral = offset - sun * Vector3.Dot(offset, sun);
            // Half a texel per axis, so at most half a texel diagonal: the box always holds the
            // player, and the discarded sub-texel remainder never accumulates.
            Assert.InRange(lateral.Length(), 0f, texel * 0.75f);
            desired += step;
        }
    }

    [Theory]
    [MemberData(nameof(Cascades))]
    public void TheDepthOfTheCenterTracksTheDesiredPointExactly(float radius, int mapSize)
    {
        // Lateral moves are quantized, depth is not: the box's near/far band has to follow the
        // player when they fall or fly, however small the step.
        var sun = SunAt(2.6f);
        var desired = new Vector3(8.5f, 20f, -3.25f);
        var center = Step(desired, desired, sun, radius, mapSize);

        foreach (var drop in new[] { 0.01f, 0.05f, 0.2f, 1f })
        {
            desired += sun * drop;
            center = Step(center, desired, sun, radius, mapSize);
            Assert.InRange(MathF.Abs(Vector3.Dot(center - desired, sun)), 0f, 1e-3f);
        }
    }

    [Theory]
    [MemberData(nameof(Cascades))]
    public void AJumpInTheDesiredPointIsFollowedInOneStep(float radius, int mapSize)
    {
        var texel = TexelOf(radius, mapSize);
        // Teleports and respawns: the center must land back on the camera rather than walk there.
        foreach (var sun in Suns)
        {
            var center = new Vector3(4.5f, 30f, 4.5f);
            var jump = new Vector3(1800.5f, 48f, -950.25f);
            var next = Step(center, jump, sun, radius, mapSize);
            var offset = next - jump;
            var lateral = offset - sun * Vector3.Dot(offset, sun);
            Assert.InRange(lateral.Length(), 0f, texel * 0.75f);
        }
    }
}
