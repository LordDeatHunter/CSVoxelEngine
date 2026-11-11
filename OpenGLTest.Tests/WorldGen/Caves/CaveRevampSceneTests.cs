using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen;
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
