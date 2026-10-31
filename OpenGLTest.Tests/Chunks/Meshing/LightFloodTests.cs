using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>
/// The wide light-volume flood (<see cref="ChunkLighting.ComputeLight"/>) against a naive
/// reference written here with no engine flood code: every sky cell at 15 and every lamp at its
/// emission, then a plain FIFO breadth-first flood that decodes each cell's coordinates by
/// division. The sun and block volumes must agree byte for byte on real generated terrain, so a
/// faster flood (pruned seeds, index stepping) can never change a lit cell.
/// </summary>
public sealed class LightFloodTests
{
    private const int Size = Chunk.Size;
    private const int LightSize = Chunk.LightSize;
    private const int LightSq = Chunk.LightSq;
    private const int LightCount = Chunk.LightCount;
    private const int Center = 13;

    public LightFloodTests() => TestContent.EnsureLoaded();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    [Theory]
    [InlineData("SurfaceChunk")]
    [InlineData("DugPit")]
    [InlineData("CaveChunk")]
    [InlineData("OceanFloor")]
    [InlineData("LampLit")]
    [InlineData("RoofAboveTerrain")]
    [InlineData("NullCorners")]
    [InlineData("HillsideTunnels")]
    public void TheFloodMatchesANaiveReferenceFlood(string scene)
    {
        var neighbors = BuildScene(scene);
        var center = neighbors[Center]!;

        var light = new byte[LightCount];
        ChunkLighting.FillLightSnapshot(light, neighbors);

        var (sun, block, anyTinted) = Flood(center, light);
        var (expectedSun, expectedBlock, expectedTinted) = ReferenceFlood(center, light);

        AssertSameVolume("sun", expectedSun, sun);
        AssertSameVolume("block", expectedBlock, block);
        Assert.Equal(expectedTinted, anyTinted);

        // The scenes must exercise what they claim to, or a broken flood could pass vacuously
        // (open terrain is all sky or all rock, so only a dug pit gives the sun a gradient)
        switch (scene)
        {
            case "CaveChunk":
                Assert.Contains(light, id => id != 0 && !ChunkLighting.PassesLight(id));
                Assert.Contains(light, ChunkLighting.PassesLight);
                break;
            case "OceanFloor":
                Assert.Contains(light, BlockType.IsFluid);
                Assert.Contains(sun, s => s == 15);
                break;
            case "SurfaceChunk":
            case "NullCorners":
                Assert.Contains(sun, s => s == 15);
                Assert.Contains(sun, s => s == 0);
                break;
            default:
                Assert.Contains(sun, s => s > 1 && s < 15);
                break;
        }
        if (scene == "LampLit")
        {
            Assert.True(anyTinted, "the lamp scene must seed a colored lamp");
            Assert.Contains(block, b => (b & 0x0F) > 1 && (b & 0x0F) < 15);
            Assert.Contains(block, b => b >> 4 != 0);
            Assert.Contains(block, b => b != 0 && b >> 4 == 0);
        }
    }

    // ---- the engine flood ----

    private static (byte[] Sun, byte[] Block, bool AnyTinted) Flood(Chunk center, byte[] light)
    {
        var sun = new byte[LightCount];
        var block = new byte[LightCount];
        // Garbage, so the flood is proven never to read a queue slot it did not write
        var queue = new int[LightCount];
        Array.Fill(queue, -1);

        try
        {
            Assert.True(ChunkLighting.FillColumns(WorldgenColumnSource.Instance,
                (int)center.Position.X, (int)center.Position.Z, MeshScratch.Current.Columns));
            var anyTinted = ChunkLighting.ComputeLight(center, light, sun, block, queue);
            return (sun, block, anyTinted);
        }
        finally
        {
            MeshScratch.Current.ClearColumns();
        }
    }

    // ---- the naive reference ----

    private static (byte[] Sun, byte[] Block, bool AnyTinted) ReferenceFlood(Chunk center, byte[] light)
    {
        var worldX0 = (int)center.Position.X * Size - Chunk.LightBorder;
        var worldZ0 = (int)center.Position.Z * Size - Chunk.LightBorder;
        var worldY0 = center.LowerBoundY - Chunk.LightBorder;

        // Sun: every light-passing cell at or above its column's terrain height sees the sky
        var sun = new byte[LightCount];
        var sunSeeds = new List<int>();
        for (var lx = 0; lx < LightSize; lx++)
        for (var lz = 0; lz < LightSize; lz++)
        {
            var height = ProbeHeight(worldX0 + lx, worldZ0 + lz);
            for (var ly = 0; ly < LightSize; ly++)
            {
                var i = (lx * LightSize + ly) * LightSize + lz;
                if (!ChunkLighting.PassesLight(light[i]) || worldY0 + ly < height) continue;
                sun[i] = 15;
                sunSeeds.Add(i);
            }
        }
        Bfs(light, sun, sunSeeds);

        // Block: one plain flood per color slot over that slot's lamps, then each cell keeps the
        // brightest level and, at a tie, the lowest slot reaching it. SpreadBlockLight's
        // dominant-color rule (brighter wins, equal level goes to the lower slot) is order-free,
        // so its fixed point is exactly this per-slot max.
        var seedsBySlot = new SortedDictionary<int, List<(int Index, byte Level)>>();
        for (var i = 0; i < LightCount; i++)
        {
            var emission = BlockType.Emission[light[i]];
            if (emission == 0) continue;
            var slot = BlockType.LightSlot[light[i]];
            if (!seedsBySlot.TryGetValue(slot, out var list)) seedsBySlot[slot] = list = new();
            list.Add((i, emission));
        }

        var block = new byte[LightCount];
        foreach (var (slot, seeds) in seedsBySlot)
        {
            var levels = new byte[LightCount];
            foreach (var (index, level) in seeds)
                levels[index] = Math.Max(levels[index], level);
            Bfs(light, levels, seeds.Select(s => s.Index));

            for (var i = 0; i < LightCount; i++)
            {
                // Slots arrive ascending, so only a strictly brighter level takes the cell over
                if (levels[i] > (block[i] & 0x0F))
                    block[i] = (byte)(levels[i] | slot << 4);
            }
        }

        var anyTinted = seedsBySlot.Keys.Any(slot => slot != 0);
        return (sun, block, anyTinted);
    }

    /// <summary>A plain max flood: a light-passing face neighbor dimmer than one less than the
    /// current cell takes that level and floods on. Coordinates decoded by division.</summary>
    private static void Bfs(byte[] light, byte[] levels, IEnumerable<int> seeds)
    {
        var queue = new Queue<int>(seeds);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            var cur = levels[c];
            if (cur <= 1) continue;
            var next = (byte)(cur - 1);
            var lx = c / LightSq;
            var ly = c / LightSize % LightSize;
            var lz = c % LightSize;
            Step(lx - 1, ly, lz);
            Step(lx + 1, ly, lz);
            Step(lx, ly - 1, lz);
            Step(lx, ly + 1, lz);
            Step(lx, ly, lz - 1);
            Step(lx, ly, lz + 1);

            void Step(int x, int y, int z)
            {
                if (x < 0 || y < 0 || z < 0 || x >= LightSize || y >= LightSize || z >= LightSize) return;
                var n = (x * LightSize + y) * LightSize + z;
                if (!ChunkLighting.PassesLight(light[n]) || levels[n] >= next) return;
                levels[n] = next;
                queue.Enqueue(n);
            }
        }
    }

    private static void AssertSameVolume(string what, byte[] expected, byte[] actual)
    {
        for (var i = 0; i < LightCount; i++)
        {
            if (expected[i] == actual[i]) continue;
            var lx = i / LightSq;
            var ly = i / LightSize % LightSize;
            var lz = i % LightSize;
            Assert.Fail($"{what} light differs at index {i} (lx {lx}, ly {ly}, lz {lz}): " +
                        $"expected {expected[i]}, flood wrote {actual[i]}");
        }
    }

    // An independent height oracle (as in LightVolumeHeightTests), not the build's column fill
    private static int ProbeHeight(int worldX, int worldZ)
    {
        var cx = FloorDiv(worldX, Size);
        var cz = FloorDiv(worldZ, Size);
        return ColumnCache.GetHeights(cx, cz)[((worldX - cx * Size) << Chunk.SizeShift) | (worldZ - cz * Size)];
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    // ---- scenes ----

    private static Chunk?[] BuildScene(string scene) => scene switch
    {
        "SurfaceChunk" => Surface(0, 0),
        "DugPit" => DugPit(),
        "CaveChunk" => Neighborhood(3, -4, -2),
        "OceanFloor" => OceanFloor(),
        "LampLit" => LampLit(),
        "RoofAboveTerrain" => RoofAboveTerrain(),
        "NullCorners" => NullCorners(),
        "HillsideTunnels" => HillsideTunnels(),
        _ => throw new ArgumentOutOfRangeException(nameof(scene)),
    };

    /// <summary>The 3x3x3 chunks around (px, py, pz) in the builder's (dx * 3 + dy) * 3 + dz
    /// slot order, slot 13 the center.</summary>
    private static Chunk?[] Neighborhood(int px, int py, int pz)
    {
        var neighbors = new Chunk?[27];
        for (var dx = 0; dx < 3; dx++)
        for (var dy = 0; dy < 3; dy++)
        for (var dz = 0; dz < 3; dz++)
            neighbors[(dx * 3 + dy) * 3 + dz] =
                new Chunk(null!, new Vector3(px + dx - 1, py + dy - 1, pz + dz - 1), generateBlockData: true);
        return neighbors;
    }

    private static int CenterHeight(int cx, int cz) => ProbeHeight(cx * Size + Size / 2, cz * Size + Size / 2);

    /// <summary>The chunk holding the terrain top over the middle of chunk column (cx, cz).</summary>
    private static Chunk?[] Surface(int cx, int cz) =>
        Neighborhood(cx, FloorDiv(CenterHeight(cx, cz), Size), cz);

    private static Chunk?[] OceanFloor()
    {
        for (var r = 1; r < 400; r++)
        for (var cx = -r; cx <= r; cx += r)
        for (var cz = -r; cz <= r; cz += r)
        {
            var biomes = ColumnCache.GetBiomes(cx, cz);
            if (biomes[(Size / 2 << Chunk.SizeShift) | Size / 2] != Biome.Ocean) continue;
            if (CenterHeight(cx, cz) >= WorldGenerationConfig.SeaLevel) continue;
            return Surface(cx, cz);
        }
        throw new InvalidOperationException("no ocean column found by the scan");
    }

    private static Chunk?[] LampLit()
    {
        var neighbors = DugPit();
        var center = neighbors[Center]!;
        var lamp = Block("lamp");
        var blueLamp = Block("blue_lamp");
        var stone = Block("stone");

        // Two lamps of different slots three cells either side of one cell, in a cleared room
        Fill(center, 8, 14, 10, 13, 2, 12, 0);
        center.SetBlockRaw(10, 11, 4, lamp, 0);
        center.SetBlockRaw(10, 11, 10, blueLamp, 0);
        center.SetBlockRaw(13, 12, 7, blueLamp, 0);

        // A lamp sealed in a stone shell: only its own room lights up
        Fill(center, 1, 5, 4, 8, 1, 5, stone);
        Fill(center, 2, 4, 5, 7, 2, 4, 0);
        center.SetBlockRaw(3, 6, 3, blueLamp, 0);

        // A lamp across the +X face, near the border, so it lights the center through the seam
        var east = neighbors[(2 * 3 + 1) * 3 + 1]!;
        Fill(east, 0, 3, 10, 13, 4, 9, 0);
        east.SetBlockRaw(1, 11, 6, lamp, 0);
        east.SetBlockRaw(2, 12, 8, blueLamp, 0);
        return neighbors;
    }

    /// <summary>A stone slab three cells above the terrain: opaque cells over a column's first
    /// sky cell, which a seed rule must not mistake for more sky.</summary>
    private static Chunk?[] RoofAboveTerrain()
    {
        var neighbors = DugPit();
        var center = neighbors[Center]!;
        var roofY = CenterHeight(0, 0) + 3;
        var dy = FloorDiv(roofY, Size) - (int)center.Position.Y;
        var target = neighbors[(1 * 3 + 1 + dy) * 3 + 1]!;
        var localY = roofY - target.LowerBoundY;
        Fill(target, 3, 12, localY, localY, 3, 12, Block("stone"));
        return neighbors;
    }

    /// <summary>The surface scene with a shaft dug down from the sky and two tunnels off it,
    /// all below the terrain height: light-passing cells that are not sky, which the sun reaches
    /// only by flooding down and sideways from the seeds.</summary>
    private static Chunk?[] DugPit()
    {
        var neighbors = Surface(0, 0);
        var x = Size / 2;
        var z = Size / 2;
        var top = CenterHeight(0, 0);
        for (var d = 0; d <= 8; d++) SetWorld(neighbors, x, top - d, z, 0);
        for (var d = 1; d <= 9; d++) SetWorld(neighbors, x + d, top - 8, z, 0);
        for (var d = 1; d <= 6; d++) SetWorld(neighbors, x, top - 4, z - d, 0);
        return neighbors;
    }

    /// <summary>Tunnels dug sideways into a slope at the foot of each step of three or more blocks:
    /// sky cells beside light-passing cells below the higher column's terrain height, which the
    /// sun reaches only sideways from the open column next to them.</summary>
    private static Chunk?[] HillsideTunnels()
    {
        ReadOnlySpan<(int X, int Z)> directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        for (var r = 0; r < 200; r++)
        for (var cx = -r; cx <= r; cx += Math.Max(1, 2 * r))
        for (var cz = -r; cz <= r; cz++)
        {
            var py = FloorDiv(CenterHeight(cx, cz), Size);
            var steps = new List<(int X, int Y, int Z, int Dx, int Dz)>();
            for (var x = cx * Size + 1; x < cx * Size + Size - 1; x++)
            for (var z = cz * Size + 1; z < cz * Size + Size - 1; z++)
            {
                var low = ProbeHeight(x, z);
                if (low < py * Size || low >= py * Size + Size - 1) continue;
                foreach (var (dx, dz) in directions)
                {
                    // The height is the top solid cell, so the open cells start one above it
                    if (ProbeHeight(x + dx, z + dz) >= low + 3) steps.Add((x, low + 1, z, dx, dz));
                }
            }
            if (steps.Count == 0) continue;

            var neighbors = Neighborhood(cx, py, cz);
            foreach (var (x, y, z, dx, dz) in steps)
            for (var d = 1; d <= 3; d++)
            {
                SetWorld(neighbors, x + dx * d, y, z + dz * d, 0);
                SetWorld(neighbors, x + dx * d, y + 1, z + dz * d, 0);
            }
            return neighbors;
        }
        throw new InvalidOperationException("no slope step found by the scan");
    }

    /// <summary>Writes a world cell into whichever of the 27 chunks holds it.</summary>
    private static void SetWorld(Chunk?[] neighbors, int wx, int wy, int wz, byte id)
    {
        var center = neighbors[Center]!;
        var cx = FloorDiv(wx, Size); var cy = FloorDiv(wy, Size); var cz = FloorDiv(wz, Size);
        var dx = cx - (int)center.Position.X + 1;
        var dy = cy - (int)center.Position.Y + 1;
        var dz = cz - (int)center.Position.Z + 1;
        var chunk = neighbors[(dx * 3 + dy) * 3 + dz]!;
        chunk.SetBlockRaw(wx - cx * Size, wy - cy * Size, wz - cz * Size, id, 0);
    }

    private static Chunk?[] NullCorners()
    {
        var neighbors = Surface(0, 0);
        for (var dx = 0; dx < 3; dx++)
        for (var dy = 0; dy < 3; dy++)
        for (var dz = 0; dz < 3; dz++)
        {
            // Keep the center and its six face neighbors (exactly one axis off center)
            var off = (dx != 1 ? 1 : 0) + (dy != 1 ? 1 : 0) + (dz != 1 ? 1 : 0);
            if (off >= 2) neighbors[(dx * 3 + dy) * 3 + dz] = null;
        }
        return neighbors;
    }

    private static void Fill(Chunk chunk, int x0, int x1, int y0, int y1, int z0, int z1, byte id)
    {
        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        for (var z = z0; z <= z1; z++)
            chunk.SetBlockRaw(x, y, z, id, 0);
    }
}
