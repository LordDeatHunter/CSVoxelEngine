using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Player;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Caves;

/// <summary>
/// Every cave biome is reachable in generated terrain: under the default seed, a scan outward from the origin
/// finds, for each one, an open cave whose floor lies in it. The spots it prints (the eye of a player standing
/// on that floor) are the ones <c>visual-examples/cave-biomes.json</c> films. In the "Worldgen seed"
/// collection because it generates chunks under the shared seed.
/// </summary>
[Collection("Worldgen seed")]
public sealed class CaveBiomeSpotTests
{
    // Ring scan of chunk columns: echo biomes in steps of this many chunk columns (a frozen or desert
    // column can lie thousands of blocks out), the sub-biomes in single steps near the origin
    private const int EchoStepChunks = 4;
    private const int EchoMaxRings = 120;
    private const int SubMaxRings = 24;
    private const int MaxGeneratedChunks = 900;

    // A floor counts only under a room: this many open cells in the 7x3x7 box over it
    private const int MinOpenCells = 100;
    private const int RoomHalf = 3;

    private readonly ITestOutputHelper _output;

    public CaveBiomeSpotTests(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    [Fact]
    public void EveryCaveBiomeHasAnOpenCaveSpot()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var found = new Dictionary<CaveBiome, Vector3>();
        var generated = 0;

        // The sub-biomes: any land column near the origin, in the band between the echo and the barrier
        foreach (var (cx, cz) in Ring(SubMaxRings, 1))
        {
            if (generated >= MaxGeneratedChunks || HasAllSubBiomes(found)) break;
            var column = ColumnCache.Get(cx, cz);
            if (column.Biomes[(8 << Chunk.SizeShift) | 8] == Biome.Ocean) continue;
            for (var cy = -14; cy >= -18; cy--)
            {
                generated++;
                Search(new Vector3(cx, cy, cz), column, found);
            }
        }

        // The echoes: chunk columns wholly frozen or wholly desert (so a view of the cave stays inside the
        // echo), a few chunks under their lowest ground
        foreach (var (cx, cz) in Ring(EchoMaxRings, EchoStepChunks))
        {
            if (generated >= MaxGeneratedChunks
                || (found.ContainsKey(CaveBiome.FrozenEcho) && found.ContainsKey(CaveBiome.DesertEcho))) break;

            var surface = TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8);
            var echo = surface switch
            {
                Biome.Frozen => CaveBiome.FrozenEcho,
                Biome.Desert => CaveBiome.DesertEcho,
                _ => CaveBiome.Plain,
            };
            if (echo == CaveBiome.Plain || found.ContainsKey(echo)) continue;

            var column = ColumnCache.Get(cx, cz);
            if (Array.Exists(column.Biomes, b => b != surface)) continue;
            var top = column.MinHeight >> Chunk.SizeShift;
            for (var k = 2; k <= 5; k++)
            {
                generated++;
                Search(new Vector3(cx, top - k, cz), column, found);
            }
        }

        foreach (var (biome, eye) in found)
            _output.WriteLine($"{biome}: eye {eye.X:0.0#},{eye.Y:0.0#},{eye.Z:0.0#}");
        _output.WriteLine($"({generated} chunks generated)");

        foreach (var biome in Enum.GetValues<CaveBiome>())
        {
            if (biome == CaveBiome.Plain) continue;
            Assert.True(found.ContainsKey(biome), $"no open cave found in {biome}");
        }
    }

    private static bool HasAllSubBiomes(Dictionary<CaveBiome, Vector3> found) =>
        found.ContainsKey(CaveBiome.Crystal) && found.ContainsKey(CaveBiome.Fungal)
                                             && found.ContainsKey(CaveBiome.MagmaDeep);

    /// <summary>
    /// Generates the chunk at <paramref name="pos"/> and records, for every cave biome not found yet, the
    /// eye over the first solid floor in it that has two air cells over it and a room around them.
    /// </summary>
    private static void Search(Vector3 pos, ColumnData column, Dictionary<CaveBiome, Vector3> found)
    {
        var chunk = new Chunk(null!, pos, generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(chunk);
        int x0 = (int)pos.X * Chunk.Size, y0 = (int)pos.Y * Chunk.Size, z0 = (int)pos.Z * Chunk.Size;

        for (var x = RoomHalf; x < Chunk.Size - RoomHalf; x++)
        for (var z = RoomHalf; z < Chunk.Size - RoomHalf; z++)
        for (var y = 0; y < Chunk.Size - 3; y++)
        {
            var floor = chunk.Blocks[Chunk.Idx(x, y, z)];
            if (!BlockType.IsSolid(floor) || !BlockType.Occludes(floor)) continue;
            if (chunk.Blocks[Chunk.Idx(x, y + 1, z)] != BlockType.Air
                || chunk.Blocks[Chunk.Idx(x, y + 2, z)] != BlockType.Air) continue;

            var i = (x << Chunk.SizeShift) | z;
            var biome = CaveBiomes.At(x0 + x, y0 + y, z0 + z, column.Biomes[i], column.Heights[i]);
            if (biome == CaveBiome.Plain || found.ContainsKey(biome)) continue;
            if (OpenCellsOver(chunk.Blocks, x, y, z) < MinOpenCells) continue;

            found[biome] = new Vector3(x0 + x + 0.5f, y0 + y + 1 + Camera.EyeHeight, z0 + z + 0.5f);
        }
    }

    /// <summary>The non-occluding cells in the 7x3x7 box over floor cell (x, y, z), inside the chunk.</summary>
    private static int OpenCellsOver(ushort[] blocks, int x, int y, int z)
    {
        var open = 0;
        for (var dx = -RoomHalf; dx <= RoomHalf; dx++)
        for (var dz = -RoomHalf; dz <= RoomHalf; dz++)
        for (var dy = 1; dy <= 3; dy++)
        {
            if (!BlockType.Occludes(blocks[Chunk.Idx(x + dx, y + dy, z + dz)])) open++;
        }

        return open;
    }

    /// <summary>The chunk columns of square rings around the origin, <paramref name="step"/> apart, nearest first.</summary>
    private static IEnumerable<(int Cx, int Cz)> Ring(int maxRings, int step)
    {
        yield return (0, 0);
        for (var ring = 1; ring <= maxRings; ring++)
        for (var i = -ring; i < ring; i++)
        {
            yield return (i * step, -ring * step);
            yield return (ring * step, i * step);
            yield return (-i * step, ring * step);
            yield return (-ring * step, -i * step);
        }
    }
}
