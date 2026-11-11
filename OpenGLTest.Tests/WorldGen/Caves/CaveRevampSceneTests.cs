using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VoxelEngine.Player;
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
        var lakeLevel = 0;
        for (var x0 = -128; x0 < 128 && lake is null; x0 += Chunk.Size)
        for (var y0 = minY; y0 < maxY && lake is null; y0 += Chunk.Size)
        for (var z0 = -128; z0 < 128 && lake is null; z0 += Chunk.Size)
        {
            if (!CaveLavaLakes.TrySite(x0, y0, z0, out var level, out _, out _)) continue;
            var candidate = new Chunk(null!, new Vector3(x0 / Chunk.Size, y0 / Chunk.Size, z0 / Chunk.Size), true);
            if (candidate.GetLocalBlock(8, level, 8) != LavaGenerator.LavaBlock) continue;
            if (candidate.GetLocalBlock(8, level + 1, 8) != BlockType.Air) continue;
            lake = candidate;
            lakeLevel = level;
        }
        Assert.NotNull(lake);
        var original = lake.Storage.ToDense();
        var regenerated = new Chunk(null!, lake.Position, true);
        Assert.Equal(original, regenerated.Storage.ToDense());
        var hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(original.AsSpan())));
        var eye = lake.Position * Chunk.Size + new Vector3(8.5f, lakeLevel + 3.5f, 8.5f);
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
