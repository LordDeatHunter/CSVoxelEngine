using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.WorldGen.Caves;

/// <summary>Finds real generated fixtures for the issue #10 visual scenario and checks that a
/// lake is reproducible and a modified chunk restores its full snapshot despite procedural changes.</summary>
[Collection("Worldgen seed")]
public sealed class CaveRevampSceneTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(-128, -64)]
    [InlineData(-448, -256)]
    public void ARealPrefilledLakeIsReproducibleAndModifiedSnapshotsKeepTheirTerrain(int minY, int maxY)
    {
        TestContent.EnsureLoaded();
        Chunk? lake = null;
        var surface = Vector3.Zero;
        for (var x0 = -512; x0 < 512 && lake is null; x0 += Chunk.Size)
        for (var y0 = minY; y0 < maxY && lake is null; y0 += Chunk.Size)
        for (var z0 = -512; z0 < 512 && lake is null; z0 += Chunk.Size)
        {
            if (!CaveLavaLakes.TrySite(x0, y0, z0, out _, out _, out _)) continue;
            var candidate = new Chunk(null!, new Vector3(x0 / Chunk.Size, y0 / Chunk.Size, z0 / Chunk.Size), true);
            if (candidate.Storage.ToDense().Count(id => id == LavaGenerator.LavaBlock) < 12) continue;
            if (!TryPoolSurface(candidate, out surface)) continue;
            lake = candidate;
        }
        Assert.NotNull(lake);
        AssertStillPool(lake);
        var original = lake.Storage.ToDense();
        var regenerated = new Chunk(null!, lake.Position, true);
        Assert.Equal(original, regenerated.Storage.ToDense());
        var hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(original.AsSpan())));
        var eye = lake.Position * Chunk.Size + surface;
        var lavaCells = (from x in Enumerable.Range(0, Chunk.Size)
                         from y in Enumerable.Range(0, Chunk.Size)
                         from z in Enumerable.Range(0, Chunk.Size)
                         where lake.GetLocalBlock(x, y, z) == LavaGenerator.LavaBlock
                         select new Vector3(x, y, z)).ToArray();
        var center = lake.Position * Chunk.Size + new Vector3(lavaCells.Average(p => p.X) + 0.5f,
            lavaCells.Max(p => p.Y) + 1.5f, lavaCells.Average(p => p.Z) + 0.5f);
        output.WriteLine($"pool center floor-level camera={center}");
        output.WriteLine($"seed={WorldGenerationConfig.Seed} lake eye={eye} chunk={lake.Position} ids-sha256={hash}");
        lake.SetBlockRaw(1, 1, 1, ContentRegistry.RequireBlock("lamp").Id, 0);
        var snapshot = lake.TakeSnapshot(null);
        try
        {
            WorldSeed.Apply(WorldSeed.Default + 7919);
            var restored = new Chunk(null!, lake.Position, false);
            restored.RestoreFrom(snapshot);
            Assert.Equal(lake.Storage.ToDense(), restored.Storage.ToDense());
            Assert.True(restored.IsRestored);
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    private static bool TryPoolSurface(Chunk chunk, out Vector3 surface)
    {
        surface = default;
        var found = false;
        for (var x = 1; x < Chunk.Size - 1; x++)
        for (var z = 1; z < Chunk.Size - 1; z++)
        for (var y = 1; y < Chunk.Size - 1; y++)
        {
            if (chunk.GetLocalBlock(x, y, z) != LavaGenerator.LavaBlock
                || chunk.GetLocalBlock(x, y + 1, z) != BlockType.Air) continue;
            surface = new Vector3(x + 0.5f, y + 3.5f, z + 0.5f);
            found = true;
        }
        return found;
    }

    private static void AssertStillPool(Chunk lake)
    {
        var manager = new ChunkManager(null!);
        manager.Chunks[lake.Position] = lake;
        var fluids = new FluidManager();
        var origin = lake.Position * Chunk.Size;
        int? surfaceY = null;
        for (var x = 1; x < Chunk.Size - 1; x++)
        for (var y = 1; y < Chunk.Size - 1; y++)
        for (var z = 1; z < Chunk.Size - 1; z++)
        {
            if (lake.GetLocalBlock(x, y, z) != LavaGenerator.LavaBlock) continue;
            Assert.True(FluidState.IsSource(lake.GetLocalState(x, y, z)));
            foreach (var (dx, dy, dz) in new[] { (-1, 0, 0), (1, 0, 0), (0, 0, -1), (0, 0, 1), (0, -1, 0) })
            {
                var adjacent = lake.GetLocalBlock(x + dx, y + dy, z + dz);
                Assert.True(adjacent == LavaGenerator.LavaBlock || BlockType.IsSolid(adjacent), "generated pool leaks");
            }
            if (lake.GetLocalBlock(x, y + 1, z) == BlockType.Air)
            {
                surfaceY ??= y;
                Assert.Equal(surfaceY.Value, y);
            }
            fluids.ScheduleAround((int)origin.X + x, (int)origin.Y + y, (int)origin.Z + z);
        }
        Assert.NotNull(surfaceY);
        var original = lake.Storage.ToDense();
        var drops = new DropManager();
        for (var tick = 0; tick < 32; tick++) fluids.Update(SimulationConfig.FluidTickSeconds, manager, drops);
        Assert.Equal(original, lake.Storage.ToDense());
        Assert.False(lake.ModifiedSinceSave);
    }

    [Fact]
    public void TaigaHasFrozenRockAndOpenCavesBelowTheOriginalEchoBand()
    {
        TestContent.EnsureLoaded();
        // Choose a broad taiga patch so neighboring Frozen sites cannot supply the expansion.
        for (var x0 = -4096; x0 <= 4096; x0 += 128)
        for (var z0 = -4096; z0 <= 4096; z0 += 128)
        {
            if (!IsTaigaPatch(x0, z0)) continue;
            var column = ColumnCache.Get(x0 / Chunk.Size, z0 / Chunk.Size);
            for (var y0 = -256; y0 > WorldGenerationConfig.CaveEchoVolumeBottomY; y0 -= Chunk.Size)
            {
                var chunk = new Chunk(null!, new Vector3(x0 / Chunk.Size, y0 / Chunk.Size, z0 / Chunk.Size), true);
                if (!TryOpenFloor(chunk, column, out var x, out var y, out var z)) continue;
                var eye = chunk.Position * Chunk.Size + new Vector3(x + 0.5f, y + 1 + Camera.EyeHeight, z + 0.5f);
                output.WriteLine($"taiga seed={WorldGenerationConfig.Seed} eye={eye} floor={chunk.GetLocalBlock(x, y, z)}");
                AssertMapEqualsPoint(column, x0, y0, z0);
                var ci = VoxelCoords.ColumnIndex(x, z);
                Assert.Equal(CaveBiome.FrozenEcho, CaveBiomes.At(x0 + x, y0 + y, z0 + z, column.Biomes[ci], column.Heights[ci]));
                // Ores retain their ids; prove the surrounding generated rock uses the frozen palette.
                Assert.Contains(CaveBiomes.BlockFor(CaveBiome.FrozenEcho, CaveCellRole.Interior, x0 + x, y0 + y, z0 + z),
                    chunk.Storage.ToDense());
                return;
            }
        }
        Assert.Fail("no open deep taiga cave in the bounded search");
    }

    private static bool IsTaigaPatch(int x0, int z0)
    {
        for (var dx = -256; dx <= 256; dx += 64)
        for (var dz = -256; dz <= 256; dz += 64)
            if (TerrainGenerator.BiomeAt(x0 + dx, z0 + dz) != Biome.Taiga) return false;
        return true;
    }

    private static bool TryOpenFloor(Chunk chunk, ColumnData column, out int lx, out int ly, out int lz)
    {
        for (var x = 3; x < Chunk.Size - 3; x++)
        for (var y = 1; y < Chunk.Size - 3; y++)
        for (var z = 3; z < Chunk.Size - 3; z++)
        {
            if (!BlockType.IsSolid(chunk.GetLocalBlock(x, y, z))
                || chunk.GetLocalBlock(x, y + 1, z) != BlockType.Air
                || chunk.GetLocalBlock(x, y + 2, z) != BlockType.Air
                || OpenCellsOver(chunk, x, y, z) < 100) continue;
            var i = VoxelCoords.ColumnIndex(x, z);
            var origin = chunk.Position * Chunk.Size;
            if (CaveBiomes.At((int)origin.X + x, (int)origin.Y + y, (int)origin.Z + z,
                    column.Biomes[i], column.Heights[i]) != CaveBiome.FrozenEcho) continue;
            (lx, ly, lz) = (x, y, z);
            return true;
        }
        (lx, ly, lz) = (0, 0, 0);
        return false;
    }

    private static int OpenCellsOver(Chunk chunk, int x, int y, int z)
    {
        var open = 0;
        for (var dx = -3; dx <= 3; dx++)
        for (var dz = -3; dz <= 3; dz++)
        for (var dy = 1; dy <= 3; dy++)
            if (chunk.GetLocalBlock(x + dx, y + dy, z + dz) == BlockType.Air) open++;
        return open;
    }

    private static void AssertMapEqualsPoint(ColumnData column, int x0, int y0, int z0)
    {
        var map = new CaveBiomeMap();
        CaveBiomes.Fill(map, column, x0, y0, z0);
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var i = VoxelCoords.ColumnIndex(x, z);
            Assert.Equal(CaveBiomes.At(x0 + x, y0 + y, z0 + z, column.Biomes[i], column.Heights[i]),
                (CaveBiome)map.Cells[Chunk.Idx(x, y, z)]);
        }
    }

    [Fact]
    public void DesertHasDistinctWideGeneratedPocketInteriors()
    {
        TestContent.EnsureLoaded();
        var found = 0;
        for (var cx = -8; cx <= 16; cx++)
        for (var cz = -8; cz <= 16; cz++)
        for (var cy = -3; cy <= -2; cy++)
        {
            if (!TryWideInterior(cx, cy, cz, out var eye)) continue;
            output.WriteLine($"desert room {++found} seed={WorldGenerationConfig.Seed} eye={eye} owner=({cx},{cy},{cz})");
            if (found == 2) return;
        }
        Assert.Fail("no two wide generated desert rooms found");
    }

    private static bool TryWideInterior(int cx, int cy, int cz, out Vector3 eye)
    {
        eye = default;
        var cell = WorldGenerationConfig.DesertPocketCellBlocks;
        var height = WorldGenerationConfig.DesertPocketCellHeight;
        var y = cy * height + height / 2;
        for (var x = cx * cell + cell / 4; x < cx * cell + 3 * cell / 4; x += 4)
        for (var z = cz * cell + cell / 4; z < cz * cell + 3 * cell / 4; z += 4)
        {
            if (!DesertCaves.InPocket(x, y, z, 340, 1)) continue;
            var column = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift);
            var i = VoxelCoords.ColumnIndex(x & Chunk.SizeMask, z & Chunk.SizeMask);
            if (CaveBiomes.At(x, y, z, column.Biomes[i], column.Heights[i]) != CaveBiome.DesertEcho
                || !IsWidePocket(x, y, z)) continue;
            var chunk = new Chunk(null!, VoxelCoords.ChunkPosOf(x, y, z), true);
            Assert.Equal(BlockType.Air, chunk.GetLocalBlock(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask));
            eye = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            return true;
        }
        return false;
    }

    private static bool IsWidePocket(int x, int y, int z)
    {
        for (var dx = -12; dx <= 12; dx += 4)
        for (var dz = -12; dz <= 12; dz += 4)
        {
            var wx = x + dx;
            var wz = z + dz;
            var column = ColumnCache.Get(wx >> Chunk.SizeShift, wz >> Chunk.SizeShift);
            var i = VoxelCoords.ColumnIndex(wx & Chunk.SizeMask, wz & Chunk.SizeMask);
            if (CaveBiomes.At(wx, y, wz, column.Biomes[i], column.Heights[i]) != CaveBiome.DesertEcho
                || !DesertCaves.InPocket(wx, y, wz, column.Heights[i] - y, 1)) return false;
        }
        return true;
    }

    [Fact]
    public void RegularCavesContainAnUnminedStandingWalkway()
    {
        TestContent.EnsureLoaded();
        for (var cx = -5; cx <= 5; cx++)
        for (var cz = -5; cz <= 5; cz++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, -5, cz), true);
            for (var x = 1; x < Chunk.Size - 7; x++)
            for (var z = 1; z < Chunk.Size - 2; z++)
            for (var y = 1; y < Chunk.Size - 3; y++)
            {
                var clear = true;
                for (var dx = 0; dx <= 6 && clear; dx++)
                for (var dz = 0; dz <= 1 && clear; dz++)
                    clear = BlockType.IsSolid(chunk.GetLocalBlock(x + dx, y, z + dz))
                        && chunk.GetLocalBlock(x + dx, y + 1, z + dz) == BlockType.Air
                        && chunk.GetLocalBlock(x + dx, y + 2, z + dz) == BlockType.Air;
                if (!clear) continue;
                var eye = chunk.Position * Chunk.Size + new Vector3(x + 0.5f, y + 1 + Camera.EyeHeight, z + 0.5f);
                output.WriteLine($"seed={WorldGenerationConfig.Seed} walkway eye={eye} yaw=0 clear length=7 width=2");
                Assert.True(clear);
                return;
            }
        }
        Assert.Fail("no standing walkway found in the bounded generated region");
    }
}
