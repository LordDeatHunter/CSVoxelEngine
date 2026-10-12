using System;
using System.Numerics;
using VoxelEngine.Data;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// <c>Chunk.WorldMin</c> / <c>Chunk.WorldMax</c> are the world-space corners of a chunk's cell,
/// computed once in the constructor (PERF_TODO R11) instead of being rebuilt as
/// <c>chunk.Position * Chunk.Size</c> and <c>min + new Vector3(Chunk.Size)</c> by each of the four
/// render passes that frustum-test chunks every frame — RenderShadows, RenderReflection,
/// RenderWorld and CullWater.
///
/// Every fact here reimplements the pre-change expression inline and asserts <em>exact</em>
/// equality against it, the <c>CloudCopiesTests</c> pattern: that is the checked-in proof that the
/// stored bounds are bit-identical to what the passes computed before, and therefore that the
/// culling decision and the per-draw base offset cannot have moved.
///
/// What is NOT covered here: the call sites themselves. Whether <c>RenderManager</c> actually reads
/// <c>chunk.WorldMin</c> where it used to read the local is a GL-bound edit with no headless seam —
/// it is covered by the acceptance greps (no <c>Position * Chunk.Size</c> and no
/// <c>new Vector3(Chunk.Size)</c> left in RenderManager.cs) and by the visual gate, exactly as
/// HalfResTargetTests says of the half-res target's framebuffer.
/// </summary>
public sealed class ChunkBoundsTests
{
    /// <summary>The positions the bounds facts walk: origin, a positive cell, a negative cell, and
    /// one far out on both signs, so a sign or overflow slip in the multiply shows up.</summary>
    private static readonly Vector3[] Positions =
    [
        new(0f, 0f, 0f),
        new(1f, 2f, 3f),
        new(-4f, -1f, -9f),
        new(511f, 15f, -511f),
    ];

    [Fact]
    public void WorldBoundsMatchThePerPassArithmetic()
    {
        foreach (var pos in Positions)
        {
            var chunk = new Chunk(null!, pos, generateBlockData: false);

            // The pre-change expressions, verbatim off the four passes.
            Assert.Equal(pos * Chunk.Size, chunk.WorldMin);
            Assert.Equal(pos * Chunk.Size + new Vector3(Chunk.Size), chunk.WorldMax);
        }
    }

    [Fact]
    public void WaterCentreMatchesThePreChangeExpression()
    {
        foreach (var pos in Positions)
        {
            var chunk = new Chunk(null!, pos, generateBlockData: false);

            // CullWater's near-water test: was chunk.Position * Chunk.Size + Vector3(Size / 2),
            // is now chunk.WorldMin + Vector3(Size / 2).
            Assert.Equal(
                pos * Chunk.Size + new Vector3(Chunk.Size / 2f),
                chunk.WorldMin + new Vector3(Chunk.Size / 2f));
        }
    }

    [Fact]
    public void FrustumCullingIsUnchangedByTheStoredBounds()
    {
        // A real Frustum built the engine's way — view * projection, as Camera.GetViewProjection
        // orders it and CloudCopiesTests.Looking does — rather than a stub, so the comparison runs
        // through the genuine plane arithmetic the passes use.
        var frustum = new Frustum(
            Matrix4x4.CreateLookAt(new Vector3(8f, 40f, 8f), new Vector3(8f, 36f, 200f), Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(70f * MathF.PI / 180f, 16f / 9f, 0.1f, 300f));

        var accepted = 0;
        var rejected = 0;

        for (var x = -6; x <= 6; x++)
        for (var y = 0; y <= 6; y++)
        for (var z = -6; z <= 18; z++)
        {
            var pos = new Vector3(x, y, z);
            var chunk = new Chunk(null!, pos, generateBlockData: false);

            var reference = frustum.IntersectsAABB(
                pos * Chunk.Size,
                pos * Chunk.Size + new Vector3(Chunk.Size));

            Assert.Equal(reference, frustum.IntersectsAABB(chunk.WorldMin, chunk.WorldMax));

            if (reference) accepted++;
            else rejected++;
        }

        // Both outcomes must actually occur, or the fact would pass vacuously against a frustum
        // that accepts (or rejects) the whole grid.
        Assert.True(accepted > 0, "grid produced no visible chunks");
        Assert.True(rejected > 0, "grid produced no culled chunks");
    }
}
