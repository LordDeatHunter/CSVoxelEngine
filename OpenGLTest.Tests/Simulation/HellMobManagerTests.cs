using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen.Hell;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// Hell's spawning (<see cref="MobSpawner.TryFindHellSpawn"/>, <see cref="HellMobManager"/>): a ring
/// spawn only ever lands on an open, dim cavern floor in hell, never in the overworld or in a cave whose
/// headroom reaches out of hell, and never past the shared natural cap; a forced spawn passes the natural
/// cap but no hell mob passes the hard cap.
/// </summary>
public sealed class HellMobManagerTests
{
    public HellMobManagerTests() => TestContent.EnsureLoaded();

    private const int HellFloor = WorldGenerationConfig.HellTopY - 40;
    private const int OverworldFloor = -25;

    // Solid stone chunks over x and z -48..47 and the chunk layer holding the floor, with a pocket three
    // cells tall carved across all of it; every cell publishes the given block light (no skylight)
    private static ChunkManager Pocket(int floor, int block = 0)
    {
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var light = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        Array.Fill(light, (byte)block);
        for (var cy = floor >> Chunk.SizeShift; cy <= (floor + 3) >> Chunk.SizeShift; cy++)
        for (var cx = -3; cx <= 2; cx++)
        for (var cz = -3; cz <= 2; cz++)
        {
            var pos = new Vector3(cx, cy, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var worldY = cy * Chunk.Size + y;
                chunk.SetBlockRaw(x, y, z, worldY > floor && worldY <= floor + 3 ? BlockType.Air : stone, 0);
            }
            chunk.PublishLight(light);
            chunks.Chunks[pos] = chunk;
        }
        return chunks;
    }

    private static Vector3 EyeOnFloor(int floor) => new(0.5f, floor + 1 + Camera.EyeHeight, 0.5f);

    private static bool Search(ChunkManager chunks, int floor, out int groundY) =>
        MobSpawner.TryFindHellSpawn(chunks, EyeOnFloor(floor), MobConfig.HellSpawnMinRadiusBlocks,
            MobConfig.HellSpawnMaxRadiusBlocks, 64, MobConfig.HellSpawnSearchHalfHeight,
            MobConfig.HellSpawnScanDepth, out _, out groundY, out _);

    private static HellMobManager NewManager(MobCaps? caps = null) =>
        new(EntityRegistry.Require(Hellbrute.EntityName), EntityRegistry.Require(Cinderwitch.EntityName), caps);

    [Fact]
    public void ADimHellFloorTakesASpawnAndTheOverworldNever()
    {
        Assert.True(Search(Pocket(HellFloor), HellFloor, out var groundY));
        Assert.Equal(HellFloor, groundY);
        Assert.True(HellGenerator.InHell(groundY + 2));

        for (var i = 0; i < 20; i++)
            Assert.False(Search(Pocket(OverworldFloor), OverworldFloor, out _));
    }

    [Fact]
    public void APocketWhoseHeadroomLeavesHellOrALitFloorTakesNone()
    {
        // The floor is hell's top cell, so the cells a mob would stand in are the barrier band's
        const int edge = WorldGenerationConfig.HellTopY;
        for (var i = 0; i < 20; i++)
        {
            Assert.False(Search(Pocket(edge), edge, out _));
            Assert.False(Search(Pocket(HellFloor, block: MobConfig.HellSpawnMaxBlockLight + 1), HellFloor, out _));
        }
    }

    [Fact]
    public void TheRingSpawnerFillsHellUpToTheNaturalCapAndNoFurther()
    {
        var chunks = Pocket(HellFloor);
        var caps = new MobCaps();
        var manager = NewManager(caps);
        var drops = new DropManager();
        var target = new StubTarget(EyeOnFloor(HellFloor)) { IsFlying = true };
        IMobTarget[] targets = [target];

        for (var i = 0; i < 800; i++)
        {
            manager.Update(0.5f, chunks, drops, targets);
            Assert.True(manager.LivingCount <= MobConfig.MobNaturalCap,
                $"{manager.LivingCount} hell mobs passed the natural cap");
        }
        Assert.True(manager.LivingCount > 0, "nothing spawned in hell");
        foreach (var mob in manager.Mobs)
            Assert.True(HellGenerator.InHell(MathUtils.FloorToInt(mob.Feet.Y)));

        // A target in the overworld is never spawned around
        var overworld = NewManager();
        var surfaceChunks = Pocket(OverworldFloor);
        IMobTarget[] above = [new StubTarget(EyeOnFloor(OverworldFloor)) { IsFlying = true }];
        for (var i = 0; i < 40; i++)
            overworld.Update(MobConfig.HellSpawnIntervalSeconds, surfaceChunks, drops, above);
        Assert.Equal(0, overworld.LivingCount);
    }

    [Fact]
    public void AForcedSpawnPassesTheNaturalCapButNotTheHardCap()
    {
        var caps = new MobCaps();
        var manager = NewManager(caps);
        var at = new Vector3(0.5f, HellFloor + 2f, 0.5f);
        for (var i = 0; i < MobConfig.MobNaturalCap; i++)
            Assert.NotNull(manager.Spawn(Hellbrute.EntityName, at));
        Assert.False(caps.AllowsNaturalSpawn);

        while (caps.ForcedRoom > 0)
            Assert.NotNull(manager.Spawn(Cinderwitch.EntityName, at));
        Assert.Null(manager.Spawn(Hellbrute.EntityName, at));
        Assert.Null(manager.Spawn(Cinderwitch.EntityName, at));
        Assert.Equal(MobConfig.MobHardCap, caps.Living);
    }
}
