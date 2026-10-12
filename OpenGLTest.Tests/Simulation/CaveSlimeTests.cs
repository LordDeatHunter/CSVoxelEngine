using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Cave slimes (<see cref="MobSpawner.TryFindCaveSpawn"/>): slimes spawn on dark, dry cave floors well
/// under the surface around a player in the cave, never where sunlight or a torch reaches, and the
/// size roll treats them as underground.
/// </summary>
public sealed class CaveSlimeTests
{
    public CaveSlimeTests() => TestContent.EnsureLoaded();

    private const int Floor = -25;

    // Solid stone chunks over x and z -48..47 and y -32..-17, with a cave three cells tall
    // (y = Floor + 1 .. Floor + 3) carved across all of it, far under the generated terrain.
    // Every chunk publishes the given sun and block light (0..15) for every cell.
    private static ChunkManager Cave(int sun, int block)
    {
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var light = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        Array.Fill(light, (byte)((sun << 4) | block));
        for (var cx = -3; cx <= 2; cx++)
        for (var cz = -3; cz <= 2; cz++)
        {
            var pos = new Vector3(cx, -2, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var worldY = -2 * Chunk.Size + y;
                var open = worldY > Floor && worldY <= Floor + 3;
                chunk.SetBlockRaw(x, y, z, open ? (byte)0 : stone, 0);
            }
            chunk.PublishLight(light);
            chunks.Chunks[pos] = chunk;
        }
        return chunks;
    }

    // Standing on the cave floor at the origin
    private static readonly Vector3 PlayerInCave = new(0.5f, Floor + 1 + Camera.EyeHeight, 0.5f);

    private static bool Search(ChunkManager chunks, out int x, out int groundY, out int z) =>
        MobSpawner.TryFindCaveSpawn(chunks, PlayerInCave,
            WorldGenerationConfig.SlimeSpawnMinRadiusBlocks, WorldGenerationConfig.SlimeSpawnMaxRadiusBlocks,
            WorldGenerationConfig.SlimeCaveSpawnTries, WorldGenerationConfig.SlimeCaveSearchHalfHeight,
            WorldGenerationConfig.SlimeCaveScanDepth, WorldGenerationConfig.SlimeCaveMaxSkyLight,
            out x, out groundY, out z);

    [Fact]
    public void ADarkCaveFloorAroundThePlayerTakesASpawn()
    {
        var chunks = Cave(sun: 0, block: 0);
        var found = 0;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!Search(chunks, out var x, out var groundY, out var z)) continue;
            found++;
            Assert.Equal(Floor, groundY);
            var ring = MathF.Sqrt((x + 0.5f - PlayerInCave.X) * (x + 0.5f - PlayerInCave.X)
                                  + (z + 0.5f - PlayerInCave.Z) * (z + 0.5f - PlayerInCave.Z));
            Assert.InRange(ring, WorldGenerationConfig.SlimeSpawnMinRadiusBlocks - 2f,
                WorldGenerationConfig.SlimeSpawnMaxRadiusBlocks + 2f);
        }
        Assert.True(found > 0, "no cave spawn was ever found");
    }

    [Theory]
    [InlineData(15, 0)]
    [InlineData(0, 15)]
    public void ASunlitOrTorchLitCaveTakesNone(int sun, int block)
    {
        var chunks = Cave(sun, block);
        for (var attempt = 0; attempt < 30; attempt++)
            Assert.False(Search(chunks, out _, out _, out _));
    }

    [Fact]
    public void ASlimeRingAroundAPlayerInTheCaveSpawnsOnItsFloorAsAnUndergroundSlime()
    {
        var chunks = Cave(sun: 0, block: 0);
        // A roll that grows a slime under the night (underground) chance but not under the day one
        var roll = (WorldGenerationConfig.SlimeLargeChanceDay + WorldGenerationConfig.SlimeLargeChanceNight) * 0.5f;
        Assert.Equal(SlimeSize.Min, SlimeSize.Roll(night: 0f, underground: false, roll));

        var found = false;
        for (var attempt = 0; attempt < 30 && !found; attempt++)
        {
            if (!SlimeManager.TryFindSpawn(chunks, PlayerInCave, night: 0f, roll, out var position, out var size))
                continue;
            found = true;
            Assert.True(size > SlimeSize.Min, "a cave slime rolled as if it stood on the surface by day");
            Assert.Equal(Floor + 1f, position.Y - Slime.HalfExtentFor(size), 3);
            Assert.False(Slime.IsColliding(position, Slime.HalfExtentFor(size), chunks));
        }
        Assert.True(found, "the slime ring never found the cave floor");
    }
}
