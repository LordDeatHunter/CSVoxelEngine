using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="RenderManager.SnapShadowTarget"/> pins the shadow box's centre to the light-space
/// texel lattice so the shadow map's texels stay welded to the world as the camera moves. Lose
/// that and every shadow edge slides sub-texel by sub-texel under the player, which reads as
/// shadows that crawl, shimmer and wobble.
///
/// The subtle half is that the snap has to measure the centre in light space built from the light
/// <em>rotation</em>. The view the pass actually draws with is anchored on the centre being
/// measured, so it reports (0, 0, -d) for it wherever the player stands: measuring that view made
/// the whole snap a silent no-op, every edge crawling while the code and its comment claimed
/// otherwise. <c>CentresMoveOntoLatticePoints</c> is the fact that catches exactly that - it fails
/// outright if the centre is measured against itself - and the remaining facts pin the lattice
/// behaviour the fix restores: sub-texel walks are invisible, whole-texel steps are the only
/// movement allowed, and the centre never drifts along the light axis into or out of the box.
/// </summary>
public sealed class ShadowCameraSnapTests
{
    private const float Radius = WorldGenerationConfig.ShadowRadiusBlocks;
    private const int MapSize = WorldGenerationConfig.ShadowMapSize;

    /// <summary>The same orbit <c>RenderManager.SetLighting</c> runs, whose fixed Z tilt keeps the
    /// sun clear of vertical so the light look-at stays well defined.</summary>
    private static Vector3 SunAt(float time) =>
        Vector3.Normalize(new Vector3(MathF.Cos(time), MathF.Sin(time), 0.35f));

    private static readonly Vector3[] Suns = [SunAt(1.2f), SunAt(2.4f), SunAt(0.4f)];
    private static readonly float Texel = 2f * Radius / MapSize;

    /// <summary>Light-space coordinates of a world point in the light's own frame.</summary>
    private static Vector3 InLight(Vector3 world, Vector3 sun) =>
        Vector3.Transform(world, Matrix4x4.CreateLookAt(Vector3.Zero, -sun, Vector3.UnitY));

    /// <summary>The lattice cell a snapped centre occupies: the two axes the snap quantizes.
    /// Its light-space depth is deliberately left alone, so it is not part of the cell.</summary>
    private static (long X, long Y) CellOf(Vector3 world, Vector3 sun)
    {
        var inLight = InLight(world, sun);
        return ((long)MathF.Round(inLight.X / Texel), (long)MathF.Round(inLight.Y / Texel));
    }

    /// <summary>The light's own lateral axes, the directions the snap actually quantizes along.
    /// <see cref="Vector3.Transform(Vector3, Matrix4x4)"/> treats the matrix as row-vector form, so
    /// the camera's basis vectors in world space are its COLUMNS - the rotation's rows are what
    /// world points are measured against, not what world directions are built from.</summary>
    private static (Vector3 Right, Vector3 Up) LightAxes(Vector3 sun)
    {
        var rotation = Matrix4x4.CreateLookAt(Vector3.Zero, -sun, Vector3.UnitY);
        return (new Vector3(rotation.M11, rotation.M21, rotation.M31),
                new Vector3(rotation.M12, rotation.M22, rotation.M32));
    }

    private static List<(Vector3 Target, Vector3 Sun)> Targets()
    {
        var list = new List<(Vector3, Vector3)>();
        for (var x = -260; x <= 260; x += 37)
        for (var z = -180; z <= 180; z += 71)
            foreach (var sun in Suns)
                list.Add((new Vector3(x + 0.5f, 14.5f, z + 0.25f), sun));
        return list;
    }

    [Fact]
    public void CentresMoveOntoLatticePoints()
    {
        // The regression fact. Measured against the pass's own view every centre lands on itself,
        // so a no-op snap would leave the whole set where it started and this fails.
        var moved = 0;
        foreach (var (target, sun) in Targets())
        {
            var snapped = RenderManager.SnapShadowTarget(target, sun, Radius, MapSize);
            if (Vector3.Distance(snapped, target) > Texel * 0.05f) moved++;
        }

        Assert.True(moved > Targets().Count / 2,
            $"only {moved}/{Targets().Count} centres were snapped, so the measurement is not seeing the light grid");
    }

    [Fact]
    public void SnappedCentresSitOnTheLightLattice()
    {
        foreach (var (target, sun) in Targets())
        {
            var inLight = InLight(RenderManager.SnapShadowTarget(target, sun, Radius, MapSize), sun);
            foreach (var axis in new[] { inLight.X, inLight.Y })
            {
                var cells = axis / Texel;
                Assert.InRange(MathF.Abs(cells - MathF.Round(cells)), 0f, 0.02f);
            }
        }
    }

    [Fact]
    public void TheCentreNeverDriftsAlongTheLightAxis()
    {
        // Snapping is a lateral move only: slide the centre along the light and the box's depth
        // range would follow it, pulling casters in and out of the map.
        foreach (var (target, sun) in Targets())
        {
            var before = InLight(target, sun).Z;
            var after = InLight(RenderManager.SnapShadowTarget(target, sun, Radius, MapSize), sun).Z;
            Assert.InRange(MathF.Abs(before - after), 0f, 1e-3f);
        }
    }

    [Fact]
    public void ASnappedCentreStaysWithinOneTexelOfWhereItWas()
    {
        foreach (var (target, sun) in Targets())
        {
            var snapped = RenderManager.SnapShadowTarget(target, sun, Radius, MapSize);
            // A lattice snap can move a point at most half a texel diagonal.
            Assert.InRange(Vector3.Distance(snapped, target), 0f, Texel * 0.708f + 1e-4f);
        }
    }

    [Fact]
    public void SubTexelCameraMovesLeaveTheProjectionWhereItWas()
    {
        var moved = 0;
        foreach (var (target, sun) in Targets())
        {
            // Start from a snapped centre, so the lateral position sits exactly on a lattice point
            // and every nudge below has the full half texel of margin around it. (An arbitrary
            // target may sit anywhere in its cell, 0.01 texel from a boundary, and a nudge there
            // crosses it legitimately.) Nudge along the light's own lateral axes, the ones the
            // lattice is built on - a world-axis nudge leans into the light's depth.
            var (right, up) = LightAxes(sun);
            var anchor = RenderManager.SnapShadowTarget(target, sun, Radius, MapSize);
            var reference = InLight(anchor, sun);
            foreach (var offset in new[] { 0.3f, 0.45f, -0.1f })
            {
                var nudged = anchor + right * (Texel * offset) + up * (Texel * offset);
                var after = InLight(RenderManager.SnapShadowTarget(nudged, sun, Radius, MapSize), sun);
                // Only the two quantized axes: the centre is meant to keep tracking the camera
                // down the light axis, so its depth moves and the lateral position does not.
                Assert.InRange(MathF.Abs(after.X - reference.X), 0f, 1e-3f);
                Assert.InRange(MathF.Abs(after.Y - reference.Y), 0f, 1e-3f);
                moved++;
            }
        }

        Assert.True(moved > 0);
    }

    [Fact]
    public void WalkingOnlyEverMovesTheProjectionWholeTexelsAtATime()
    {
        var sun = SunAt(2.1f);
        var target = new Vector3(40.5f, 14.5f, -12.25f);
        var step = new Vector3(0.0125f, 0f, 0.0075f);

        var cells = new List<(long X, long Y)>();
        for (var i = 0; i < 400; i++)
        {
            var cell = CellOf(RenderManager.SnapShadowTarget(target, sun, Radius, MapSize), sun);
            if (cells.Count == 0 || !cell.Equals(cells[^1])) cells.Add(cell);
            target += step;
        }

        // 400 sub-texel steps are a few dozen cells of travel, not 400 ...
        Assert.True(cells.Count < 200, $"{cells.Count} distinct lattice cells over a {400 * step.Length():F1}-block walk");
        // ... it must still have moved, or the fact would pass on a frozen value ...
        Assert.True(cells.Count > 1);
        // ... and no step may jump a cell, which is what a snap measured off the lattice looks like.
        for (var i = 1; i < cells.Count; i++)
        {
            Assert.InRange(Math.Abs(cells[i].X - cells[i - 1].X), 0, 1);
            Assert.InRange(Math.Abs(cells[i].Y - cells[i - 1].Y), 0, 1);
        }
    }
}
