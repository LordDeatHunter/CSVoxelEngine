using System;
using System.Diagnostics;
using System.Linq;
using VoxelEngine.Chunks.Meshing;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>
/// A MEASUREMENT HARNESS for the chunk mesh build, not a regression guard. It prints the mean
/// cost of one greedy <see cref="Chunk.GenerateMeshData(ChunkManager)"/> over a fixed
/// random soup of cubes and lamps, so a change to the light propagation or the mesher (the
/// colored block light of SH-26) can report its before/after cost on the same scene.
///
/// Nothing here asserts a TIMING: this is a shared dev machine with no CI, so a timing
/// assertion would be a flake generator. The numbers go through <see cref="ITestOutputHelper"/>
/// and are read by a human. The only assertion is anti-vacuity: the build must emit geometry,
/// so a silently-empty build cannot be mistaken for a fast one. The probe is single-threaded
/// and warm, so it reads one builder's cost with nothing competing for the cores; quote it as a
/// relative before/after figure, never as an in-game rate.
///
/// Case A is the default-color scene (every lamp on the warm palette slot). Case B swaps
/// every second lamp for <c>blue_lamp</c> and only runs once that block exists. Cases C1 and C2 measure the battle
/// tower's sky cover (BT-10): C1 raises the cover flag with no covering cell in the volume (every column scanned, none
/// covered), C2 lays a brick roof over the volume's top.
/// </summary>
public sealed class MeshBuildCostProbe
{
    private readonly ITestOutputHelper _output;

    public MeshBuildCostProbe(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    private const int WarmBuilds = 20;
    private const int TimedBuilds = 300;
    private const int Seed = 26;

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void GreedyMeshBuildCost()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var lamp = Block("lamp");
        byte[] solids = [Block("stone"), Block("dirt"), Block("oak_leaves"), Block("glass")];

        // Fill in a fixed order: the dictionary's enumeration order changes per process
        var chunks = manager.Chunks.Values
            .OrderBy(c => c.Position.X).ThenBy(c => c.Position.Y).ThenBy(c => c.Position.Z)
            .ToArray();
        var rng = new Random(Seed);
        foreach (var chunk in chunks)
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var r = rng.NextDouble();
                var id = r < 0.55 ? (byte)0
                    : r < 0.565 ? lamp
                    : solids[rng.Next(solids.Length)];
                chunk.SetBlockRaw(x, y, z, id, 0);
            }

        Measure("A (default-color lamps)", manager, center);

        byte blueLamp;
        try
        {
            blueLamp = Block("blue_lamp");
        }
        catch (Exception)
        {
            _output.WriteLine("case B skipped: no blue_lamp block");
            return;
        }

        var nth = 0;
        foreach (var chunk in chunks)
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
                if (chunk.GetLocalBlock(x, y, z) == lamp && nth++ % 2 == 1)
                    chunk.SetBlockRaw(x, y, z, blueLamp, 0);

        Measure("B (every second lamp blue)", manager, center);

        // C1, the sky cover's worst case: one tower brick in a neighbor's corner cell, outside the light volume, raises
        // the neighborhood's flag, so every volume column is scanned from the top down and none is covered
        var brick = Block("tower_bricks");
        var west = manager.Chunks[center.Position + new System.Numerics.Vector3(-1, 0, 0)];
        west.SetBlockRaw(0, 0, 0, brick, 0);
        Assert.True(ChunkLighting.AnySkyCover([west]));
        Measure("C1 (sky-cover flag, one brick outside the volume)", manager, center);

        // C2: a full brick roof at the top of the volume (local y 14 of the chunk above), so each column's scan stops
        // at its first cell and everything under the roof gets no sun: the tower chunk's rebuild cost
        var above = manager.Chunks[center.Position + new System.Numerics.Vector3(0, 1, 0)];
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            above.SetBlockRaw(x, 14, z, brick, 0);
        Measure("C2 (brick roof over the volume)", manager, center);
    }

    private void Measure(string label, ChunkManager manager, Chunk center)
    {
        for (var i = 0; i < WarmBuilds; i++)
            Assert.NotNull(center.GenerateMeshData(manager, greedy: true));

        ChunkMeshData? mesh = null;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < TimedBuilds; i++)
            mesh = center.GenerateMeshData(manager, greedy: true);
        sw.Stop();

        Assert.NotNull(mesh);
        Assert.True(mesh!.Vertices.Count > 0);
        var meanUs = sw.Elapsed.TotalMilliseconds * 1000.0 / TimedBuilds;
        _output.WriteLine(
            $"case {label}: mean {meanUs:F1} us/build over {TimedBuilds} builds, " +
            $"{mesh.Vertices.Count} opaque + {mesh.TranslucentVertices.Count} translucent vertices");
    }
}
