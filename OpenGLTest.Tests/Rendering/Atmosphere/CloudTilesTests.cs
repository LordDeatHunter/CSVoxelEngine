using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Player;
using VoxelEngine.Rendering.Atmosphere;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Rendering.Atmosphere;

/// <summary>
/// <c>CloudTiles</c> turns the cloud layer's instanced draw into frustum-culled indirect commands.
/// What it must never do is change a pixel, so the load-bearing facts are that the commands are an
/// order-preserving subsequence of the old draw (every copy, every index, instance-major), that no
/// triangle the frustum touches is left out, and that the cull margin covers the band in front of
/// the eye GL still rasterizes although the engine's frustum rejects it. The GL draw itself is only
/// reachable by running the game.
/// </summary>
public sealed class CloudTilesTests
{
    private const int Cells = 32;
    private const int TileCells = 4;
    private const float Field = Cells * SkyConfig.CloudCellSize;
    private const float Thickness = SkyConfig.CloudMaxLevels * SkyConfig.CloudLayerThickness;

    private static readonly byte[,] Levels = CloudField.BuildLevels(Cells, SkyConfig.CloudSeed,
        SkyConfig.CloudThreshold, SkyConfig.CloudLevelStep, SkyConfig.CloudMaxLevels);

    private static readonly (CloudVertex[] Vertices, uint[] Indices, int[] Starts) Mesh = CloudField.BuildMesh(Levels);

    private static CloudTiles NewTiles() =>
        new(Mesh.Vertices, Mesh.Indices, Mesh.Starts, Cells, TileCells, SkyConfig.CloudTileCullMarginBlocks);

    /// <summary>The engine's own order: view * projection (Camera.GetViewProjection), with Camera's planes.</summary>
    private static Frustum Looking(Vector3 eye, Vector3 target, Vector3 up, float fovRadians, float far) =>
        new(Matrix4x4.CreateLookAt(eye, target, up)
            * Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, 16f / 9f, Camera.NearPlane, far));

    private static Vector2[] Copies(in Frustum frustum, Vector3 eye, Vector2 drift)
    {
        Span<Vector2> offsets = stackalloc Vector2[CloudCopies.MaxCopies];
        var count = CloudCopies.Gather(frustum, CloudCopies.Snap(eye.X, Field), CloudCopies.Snap(eye.Z, Field),
            drift, Field, Thickness, offsets);
        return offsets[..count].ToArray();
    }

    private static DrawElementsIndirectCommand[] Gather(CloudTiles tiles, in Frustum frustum, Vector2[] copies)
    {
        var dest = new DrawElementsIndirectCommand[tiles.MaxCommands];
        var scratch = new bool[tiles.TilesPerSide * tiles.TilesPerSide];
        var count = tiles.Gather(frustum, copies, scratch, dest);
        return dest[..count];
    }

    /// <summary>
    /// Views that cull part of the layer: from inside it, from below and above at an angle, and
    /// with a short far plane, each with and without drift.
    /// </summary>
    private static IEnumerable<(Vector3 Eye, Frustum Frustum, Vector2 Drift)> Poses()
    {
        var y = SkyConfig.CloudHeight;
        var views = new (Vector3 Eye, Vector3 Target, float Fov, float Far)[]
        {
            (new Vector3(100f, y + 2f, 300f), new Vector3(900f, y + 2f, 340f), MathF.PI / 3f, Camera.FarPlane),
            (new Vector3(-40f, y + 3f, 20f), new Vector3(-40f, y - 50f, -400f), 1.2f, 700f),
            (new Vector3(250f, y - 60f, 250f), new Vector3(400f, y + 10f, 100f), MathF.PI / 3f, 900f),
            (new Vector3(10f, y + 120f, -700f), new Vector3(60f, y, -650f), 0.6f, Camera.FarPlane),
            (new Vector3(1300f, y + 6f, 77f), new Vector3(1290f, y + 4f, 900f), 1.4f, 300f),
        };
        foreach (var (eye, target, fov, far) in views)
        foreach (var drift in new[] { Vector2.Zero, new Vector2(137.5f, 401.25f) })
            yield return (eye, Looking(eye, target, Vector3.UnitY, fov, far), drift);
    }

    [Fact]
    public void AFrustumSeeingEverythingDrawsEachCopyAsOneFullRun()
    {
        // Straight down from far above with a wide field of view, so every copy and every tile is
        // in view: each copy is then exactly the old instanced draw of its instance.
        var eye = new Vector3(0f, 40000f, 0f);
        var frustum = Looking(eye, new Vector3(0f, 0f, 0f), Vector3.UnitZ, 2.6f, 200000f);
        var copies = Copies(frustum, eye, Vector2.Zero);
        Assert.Equal(CloudCopies.MaxCopies, copies.Length);

        var commands = Gather(NewTiles(), frustum, copies);

        Assert.Equal(copies.Length, commands.Length);
        for (var c = 0; c < commands.Length; c++)
        {
            Assert.Equal(0u, commands[c].FirstIndex);
            Assert.Equal((uint)Mesh.Indices.Length, commands[c].Count);
            Assert.Equal((uint)c, commands[c].BaseInstance);
            Assert.Equal(1u, commands[c].InstanceCount);
            Assert.Equal(0, commands[c].BaseVertex);
        }
    }

    [Fact]
    public void CommandsAreAnOrderPreservingSubsequenceOfTheInstancedDraw()
    {
        var tiles = NewTiles();
        var culledSomewhere = false;
        foreach (var (eye, frustum, drift) in Poses())
        {
            var copies = Copies(frustum, eye, drift);
            var commands = Gather(tiles, frustum, copies);

            long drawn = 0;
            for (var i = 0; i < commands.Length; i++)
            {
                var cmd = commands[i];
                drawn += cmd.Count;
                Assert.True(cmd.Count > 0, "An empty command was emitted");
                Assert.Equal(1u, cmd.InstanceCount);
                Assert.Equal(0, cmd.BaseVertex);
                Assert.True(cmd.BaseInstance < copies.Length, "A command names a copy that was not gathered");
                Assert.True(cmd.FirstIndex + cmd.Count <= Mesh.Indices.Length, "A command runs past the index list");
                if (i == 0) continue;

                var prev = commands[i - 1];
                Assert.True(cmd.BaseInstance >= prev.BaseInstance, "Copies are out of slot order");
                if (cmd.BaseInstance != prev.BaseInstance) continue;

                // Strictly after the previous run, and not touching it (touching runs are merged).
                Assert.True(cmd.FirstIndex > prev.FirstIndex + prev.Count,
                    "Runs of one copy overlap, go backwards or were left unmerged");
            }

            if (drawn < (long)copies.Length * Mesh.Indices.Length) culledSomewhere = true;
        }

        Assert.True(culledSomewhere, "No pose culled anything, so the ordering was never exercised");
    }

    [Fact]
    public void NoTriangleTouchingTheFrustumIsDropped()
    {
        var tiles = NewTiles();
        var (vertices, indices, _) = Mesh;
        foreach (var (eye, frustum, drift) in Poses())
        {
            var copies = Copies(frustum, eye, drift);
            var commands = Gather(tiles, frustum, copies);

            var covered = new bool[copies.Length, indices.Length];
            foreach (var cmd in commands)
                for (var i = cmd.FirstIndex; i < cmd.FirstIndex + cmd.Count; i++)
                    covered[cmd.BaseInstance, i] = true;

            for (var c = 0; c < copies.Length; c++)
            {
                var offset = new Vector3(copies[c].X, 0f, copies[c].Y);
                for (var t = 0; t < indices.Length; t += 3)
                {
                    var a = vertices[indices[t]].Position + offset;
                    var b = vertices[indices[t + 1]].Position + offset;
                    var p = vertices[indices[t + 2]].Position + offset;
                    var min = Vector3.Min(a, Vector3.Min(b, p));
                    var max = Vector3.Max(a, Vector3.Max(b, p));
                    if (!frustum.IntersectsAABB(min, max)) continue;

                    Assert.True(covered[c, t], $"Triangle at index {t} of copy {c} is in view but was not drawn");
                }
            }
        }
    }

    [Fact]
    public void GeometryInGlsNearClipBandIsNeverCulled()
    {
        // The engine's projection is System.Numerics' Direct3D-style one, whose near plane Frustum
        // tests as z_clip >= 0, but GL clips at z_clip >= -w: everything between n*f/(2f-n) and n in
        // front of the eye is still rasterized. A box in that band must survive the cull rule.
        const float n = Camera.NearPlane;
        const float f = Camera.FarPlane;
        var viewProj = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
                       * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, n, f);
        var frustum = new Frustum(viewProj);

        var glNear = n * f / (2f * f - n);
        var depth = 0.5f * (glNear + n);
        var half = 0.1f * (n - glNear);
        var center = new Vector3(0f, 0f, -depth);
        var min = center - new Vector3(half);
        var max = center + new Vector3(half);

        var clip = Vector4.Transform(new Vector4(center, 1f), viewProj);
        Assert.True(clip.Z >= -clip.W, "The box is not in the band GL rasterizes");
        Assert.True(clip.Z < 0f, "The box is not in the band the Direct3D near plane rejects");

        Assert.False(frustum.IntersectsAABB(min, max), "The bare frustum should reject the near band");
        Assert.True(CloudTiles.MayRasterize(frustum, min, max, SkyConfig.CloudTileCullMarginBlocks),
            "The cloud tile cull drops geometry GL would still draw in front of the near plane");
    }

    [Fact]
    public void APartialViewDrawsFewerIndicesThanTheWholeCopy()
    {
        // Straight down onto the cloudy cell nearest one corner of the field, with a narrow view:
        // only the tiles around it can be in view.
        var (cx, cz) = NearestCloudyCellToTheCorner();
        var target = new Vector3((cx + 0.5f) * SkyConfig.CloudCellSize, SkyConfig.CloudHeight,
            (cz + 0.5f) * SkyConfig.CloudCellSize);
        var eye = target + new Vector3(0f, 60f, 0f);
        var frustum = Looking(eye, target, Vector3.UnitZ, 0.3f, 1000f);
        var copies = Copies(frustum, eye, Vector2.Zero);

        var commands = Gather(NewTiles(), frustum, copies);

        long drawn = 0;
        foreach (var cmd in commands) drawn += cmd.Count;
        Assert.InRange(drawn, 1, (long)copies.Length * Mesh.Indices.Length - 1);
    }

    private static (int X, int Z) NearestCloudyCellToTheCorner()
    {
        var best = (X: -1, Z: -1);
        for (var x = 0; x < Cells; x++)
        for (var z = 0; z < Cells; z++)
        {
            if (Levels[x, z] == 0) continue;
            if (best.X < 0 || x * x + z * z < best.X * best.X + best.Z * best.Z) best = (x, z);
        }

        Assert.True(best.X >= 0, "The test field holds no cloud at all");
        return best;
    }

    [Fact]
    public void GatherRejectsAShortDestination()
    {
        // A destination that cannot hold every run of every copy would silently drop clouds, so it
        // is refused outright rather than truncated.
        var tiles = NewTiles();
        var eye = new Vector3(0f, 40000f, 0f);
        var frustum = Looking(eye, Vector3.Zero, Vector3.UnitZ, 2.6f, 200000f);
        var copies = Copies(frustum, eye, Vector2.Zero);
        var tooShort = new DrawElementsIndirectCommand[copies.Length * Cells * tiles.TilesPerSide - 1];
        var scratch = new bool[tiles.TilesPerSide * tiles.TilesPerSide];

        Assert.Throws<ArgumentException>(() => tiles.Gather(frustum, copies, scratch, tooShort));
    }
}
