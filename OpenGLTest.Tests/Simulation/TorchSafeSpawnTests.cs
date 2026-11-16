using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Torch-safe ground (<see cref="MobSpawner.IsLitByManMadeLight(ChunkManager, int, int, int, int)"/>): a ring spawn
/// never lands where man-made light (a torch or lamp block, a burning station's point light) reaches the limit,
/// falling off by one per light-passing step and stopped by walls, while natural light (glowing plants, crystals,
/// lava: <c>light.natural</c>) never keeps a mob away, however bright.
/// </summary>
public sealed class TorchSafeSpawnTests
{
    public TorchSafeSpawnTests() => TestContent.EnsureLoaded();

    private static readonly int Limit = MobConfig.MobSpawnBlockLightLimit;

    // A solid full-cube emitter bright enough to reach the limit, man-made or natural as asked
    private static ushort SolidEmitter(bool natural) =>
        BlockType.Types.Values.First(b => b.Solid && b.NaturalLight == natural && b.LightEmission >= Limit + 1).Id;

    private sealed class StubStations(params (int X, int Y, int Z)[] cells) : IManMadePointLights
    {
        public void AppendLitCells(List<(int X, int Y, int Z)> lit) => lit.AddRange(cells);
    }

    [Fact]
    public void ATorchLightsGroundOnlyAsFarAsItsLightStillReachesTheLimit()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var torch = ContentRegistry.RequireBlock("torch").Id;
        chunks.Chunks[Vector3.Zero].SetBlockRaw(2, 1, 8, torch, 0);
        var reach = BlockType.ManMadeEmission[torch] - Limit;

        Assert.True(MobSpawner.IsLitByManMadeLight(chunks, 2 + reach, 1, 8));
        Assert.False(MobSpawner.IsLitByManMadeLight(chunks, 2 + reach + 1, 1, 8));
    }

    [Fact]
    public void AWallKeepsATorchFromLightingTheGroundBehindIt()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var chunk = chunks.Chunks[Vector3.Zero];
        var stone = ContentRegistry.RequireBlock("stone").Id;
        chunk.SetBlockRaw(2, 1, 8, ContentRegistry.RequireBlock("torch").Id, 0);
        // A wall across the whole chunk up to its top; nothing is loaded above it, so light cannot go around
        for (var y = 1; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(3, y, z, stone, 0);

        Assert.True(MobSpawner.IsLitByManMadeLight(chunks, 2, 1, 9));
        Assert.False(MobSpawner.IsLitByManMadeLight(chunks, 4, 1, 8));
    }

    [Fact]
    public void NaturalLightNeverKeepsAMobAwayWhileAManMadeEmitterDoes()
    {
        var chunks = MobTestWorld.Floor(SolidEmitter(natural: true));
        Assert.False(MobSpawner.IsLitByManMadeLight(chunks, 4, 1, 4));

        chunks.Chunks[Vector3.Zero].SetBlockRaw(4, 0, 4, SolidEmitter(natural: false), 0);
        Assert.True(MobSpawner.IsLitByManMadeLight(chunks, 4, 1, 4));
    }

    [Fact]
    public void ABurningStationLightsTheGroundAroundItAsATorchWould()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var reach = MobSpawner.StationLightEmission - Limit;
        chunks.ManMadePointLights = new StubStations((2, 1, 8));

        Assert.True(MobSpawner.IsLitByManMadeLight(chunks, 2 + reach, 1, 8));
        Assert.False(MobSpawner.IsLitByManMadeLight(chunks, 2 + reach + 1, 1, 8));

        chunks.ManMadePointLights = new StubStations();
        Assert.False(MobSpawner.IsLitByManMadeLight(chunks, 2, 1, 8));
    }

    [Fact]
    public void ARingNeverSpawnsOnStationLitGroundButStillDoesOnDarkGround()
    {
        var chunks = MobTestWorld.Surface();
        var player = new Vector3(0.5f, 100f, 0.5f);

        var lit = new List<(int X, int Y, int Z)>();
        for (var cx = -3; cx <= 3; cx++)
        for (var cz = -3; cz <= 3; cz++)
        {
            var heights = ColumnCache.GetHeights(cx, cz);
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
                lit.Add((cx * Chunk.Size + lx, heights[(lx << Chunk.SizeShift) | lz] + 1, cz * Chunk.Size + lz));
        }
        chunks.ManMadePointLights = new StubStations([.. lit]);
        for (var attempt = 0; attempt < 60; attempt++)
            Assert.False(MobSpawner.TryFindSurfaceSpawn(chunks, player, 20f, 40f, 8, out _, out _, out _),
                "a mob spawned on lit ground");

        chunks.ManMadePointLights = null;
        var found = false;
        for (var attempt = 0; attempt < 60 && !found; attempt++)
            found = MobSpawner.TryFindSurfaceSpawn(chunks, player, 20f, 40f, 8, out _, out _, out _);
        Assert.True(found, "no spawn found on unlit ground");
    }
}
