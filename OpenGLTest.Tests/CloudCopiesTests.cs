using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <c>CloudCopies</c> is the GL-free arithmetic behind the cloud layer's 3x3 sweep: the per-copy
/// offsets, the cull AABB, and the packing of the survivors. The GL half — the vertex-array binding,
/// the two sweeps with their colour/depth mask toggles, and the draw submission itself — is
/// deliberately NOT covered here, exactly as HalfResTargetTests says of the half-res target's
/// texture and framebuffer: it needs a live GL context and is only reachable by running the game.
///
/// <c>GatherOffsetsMatchThePerCopyArithmetic</c> is the load-bearing fact. It reimplements the
/// pre-extraction expression inline and asserts exact float equality against it, so it is the
/// checked-in proof that the extraction (and, after it, the instanced draw that consumes the packed
/// array) submits the very offsets the old per-copy loop drew.
/// </summary>
public sealed class CloudCopiesTests
{
    private const float Field = WorldGenerationConfig.CloudFieldSize;
    private const float Thickness = WorldGenerationConfig.CloudMaxLevels * WorldGenerationConfig.CloudLayerThickness;

    /// <summary>
    /// A frustum that accepts every copy the tests place: an axis-aligned box ten million blocks on
    /// a side, which is a real <see cref="Frustum"/> built the ordinary way rather than a stub, so
    /// the arithmetic under test still runs through the genuine AABB test.
    /// </summary>
    private static Frustum AcceptsEverything() =>
        new(Matrix4x4.CreateOrthographicOffCenter(-1e7f, 1e7f, -1e7f, 1e7f, -1e7f, 1e7f));

    /// <summary>The engine's own order: view * projection (Camera.GetViewProjection).</summary>
    private static Frustum Looking(Vector3 eye, Vector3 target, Vector3 up, float fovRadians, float far) =>
        new(Matrix4x4.CreateLookAt(eye, target, up)
            * Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, 16f / 9f, 0.1f, far));

    /// <summary>The pre-extraction loop, verbatim, as the reference the extraction must reproduce.</summary>
    private static List<Vector2> ReferenceLoop(in Frustum frustum, float snapX, float snapZ, float drift)
    {
        var expected = new List<Vector2>();
        for (var ix = -1; ix <= 1; ix++)
        for (var iz = -1; iz <= 1; iz++)
        {
            var minX = snapX + ix * Field + drift;
            var minZ = snapZ + iz * Field;
            var min = new Vector3(minX, WorldGenerationConfig.CloudHeight, minZ);
            if (!frustum.IntersectsAABB(min, min + new Vector3(Field, Thickness, Field)))
                continue;

            expected.Add(new Vector2(minX, minZ));
        }

        return expected;
    }

    [Fact]
    public void MaxCopiesIsTheThreeByThreeGrid()
    {
        // Counted off the same -1..1 double loop Gather walks, rather than asserted against a bare
        // 9, so widening the ring cannot silently leave the destination size behind.
        var cells = 0;
        for (var ix = -1; ix <= 1; ix++)
        for (var iz = -1; iz <= 1; iz++)
            cells++;

        Assert.Equal(CloudCopies.MaxCopies, cells);
    }

    [Fact]
    public void DriftWrapsWithinTheField()
    {
        // Game time only ever moves forward from zero, so the wrap is tested over non-negative
        // times; the pattern repeats every field, which is what makes the wrap invisible.
        foreach (var time in new[] { 0f, 0.5f, 1f, 60f, 1365.25f, 4096f, 100000f })
        {
            var drift = CloudCopies.Drift(time, Field);

            Assert.Equal((time * WorldGenerationConfig.CloudDriftBlocksPerSecond) % Field, drift);
            Assert.InRange(drift, 0f, Field);
            Assert.NotEqual(Field, drift);
        }
    }

    [Fact]
    public void SnapFloorsToFieldMultiples()
    {
        Assert.Equal(0f, CloudCopies.Snap(0f, Field));
        Assert.Equal(0f, CloudCopies.Snap(Field - 1f, Field));
        Assert.Equal(Field, CloudCopies.Snap(Field, Field));
        Assert.Equal(2f * Field, CloudCopies.Snap(2f * Field + 7f, Field));

        // The negative half is where floor and truncation disagree: truncating -1 / Field towards
        // zero would snap the grid to 0 and slide every copy a whole field off the camera.
        Assert.Equal(-Field, CloudCopies.Snap(-1f, Field));
        Assert.Equal(-Field, CloudCopies.Snap(-Field, Field));
        Assert.Equal(-2f * Field, CloudCopies.Snap(-Field - 1f, Field));
        Assert.NotEqual((float)(int)(-1f / Field) * Field, CloudCopies.Snap(-1f, Field));
    }

    [Fact]
    public void GatherOffsetsMatchThePerCopyArithmetic()
    {
        // R4's exact, checked-in equivalence proof: every offset Gather produces is bit-for-bit the
        // expression the old DrawCopies loop fed to the uOffset uniform.
        var frustum = AcceptsEverything();
        Span<Vector2> offsets = stackalloc Vector2[CloudCopies.MaxCopies];

        foreach (var snapX in new[] { 0f, Field, -Field, 8f * Field, -32f * Field })
        foreach (var snapZ in new[] { 0f, -Field, 4f * Field })
        foreach (var drift in new[] { 0f, 1.5f, 0.0009765625f, Field - 0.5f })
        {
            var expected = ReferenceLoop(frustum, snapX, snapZ, drift);
            var count = CloudCopies.Gather(frustum, snapX, snapZ, drift, Field, Thickness, offsets);

            Assert.Equal(CloudCopies.MaxCopies, count);
            Assert.Equal(expected.Count, count);
            for (var i = 0; i < count; i++)
                Assert.Equal(expected[i], offsets[i]);
        }
    }

    [Fact]
    public void GatherReturnsAllNineWhenTheWholeLayerIsInView()
    {
        // Straight down from far above the layer with a wide field of view, so all nine copies of
        // the 3 * CloudFieldSize span at CloudHeight are comfortably inside the frustum. Up is +Z
        // because the view direction is parallel to +Y.
        var frustum = Looking(new Vector3(0f, 40000f, 0f), Vector3.Zero, Vector3.UnitZ, 2.6f, 200000f);
        Span<Vector2> offsets = stackalloc Vector2[CloudCopies.MaxCopies];

        var count = CloudCopies.Gather(frustum, 0f, 0f, 0f, Field, Thickness, offsets);

        Assert.Equal(CloudCopies.MaxCopies, count);
    }

    [Fact]
    public void GatherPacksSurvivorsToTheFrontPreservingOrder()
    {
        // A real perspective view from the middle of the layer looking along +X: the copies behind
        // the camera are culled, so the survivors land packed at the front of the destination while
        // the tail is left untouched.
        var frustum = Looking(new Vector3(0f, WorldGenerationConfig.CloudHeight + 4f, 0f),
            new Vector3(10000f, WorldGenerationConfig.CloudHeight + 4f, 0f), Vector3.UnitY, MathF.PI / 3f, 100000f);
        var expected = ReferenceLoop(frustum, 0f, 0f, 0f);

        Span<Vector2> offsets = stackalloc Vector2[CloudCopies.MaxCopies];
        var sentinel = new Vector2(-12345f, -6789f);
        offsets.Fill(sentinel);

        var count = CloudCopies.Gather(frustum, 0f, 0f, 0f, Field, Thickness, offsets);

        // The cull must actually be doing something here, or the packing is untested.
        Assert.InRange(count, 1, CloudCopies.MaxCopies - 1);
        Assert.Equal(expected.Count, count);
        for (var i = 0; i < count; i++)
            Assert.Equal(expected[i], offsets[i]);
        for (var i = count; i < CloudCopies.MaxCopies; i++)
            Assert.Equal(sentinel, offsets[i]);
    }

    [Fact]
    public void GatherReturnsZeroWhenNothingIntersects()
    {
        // Looking straight down from below the layer: every copy sits behind the near plane.
        var frustum = Looking(Vector3.Zero, new Vector3(0f, -1000f, 0f), Vector3.UnitZ, MathF.PI / 3f, 1000f);
        Span<Vector2> offsets = stackalloc Vector2[CloudCopies.MaxCopies];

        var count = CloudCopies.Gather(frustum, 0f, 0f, 0f, Field, Thickness, offsets);

        Assert.Equal(0, count);
    }

    [Fact]
    public void GatherRejectsAShortDestination()
    {
        // A destination that cannot hold the whole ring would silently drop copies at the far edge
        // of the sky, so it is refused outright rather than truncated.
        var frustum = AcceptsEverything();
        var tooShort = new Vector2[CloudCopies.MaxCopies - 1];

        Assert.Throws<ArgumentException>(() =>
            CloudCopies.Gather(frustum, 0f, 0f, 0f, Field, Thickness, tooShort));
    }
}
