using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The skeleton piles (<see cref="SkeletonPileManager"/>): a non-immune target in range raises waves of the floor tier's
/// pack, bound to the floor, until the pile depletes; an immune or distant target, a full hard cap, an unloaded chunk
/// and a pile whose block is gone raise nothing; owed skeletons rise again at a depleted pile.
/// </summary>
public sealed class SkeletonPileManagerTests
{
    public SkeletonPileManagerTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    // The highest tier raises the biggest pack, so a wave is more than one skeleton when the config allows it
    private const int Tier = MobConfig.TowerMaxTier;

    private static readonly (int X, int Y, int Z) LoadedPile = (8, 1, 8);

    private static readonly (int X, int Y, int Z)[] NoCells = [];

    private static EntityType Type => EntityRegistry.Require(Skeleton.EntityName);

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static StubTarget PlayerAt(float x, float z) => new(StubTarget.EyeAt(new Vector3(x, 1f, z)));

    // A tower whose every floor shares the band 0..6; only floor `tier` holds the pile
    private static TowerFloorState FloorWithPile((int X, int Y, int Z) pile, int tier = Tier)
    {
        var floors = new List<(int, int, IReadOnlyList<(int X, int Y, int Z)>, IReadOnlyList<(int X, int Y, int Z)>)>();
        for (var k = 0; k <= tier; k++)
            floors.Add((0, 6, k == tier ? new[] { pile } : NoCells, NoCells));
        return TowerRecord.Build((3, -2), 8, 8, 12, floors, hasSite: false).Floors[tier];
    }

    private static ChunkManager WorldWithPile()
    {
        var chunks = StoneFloor();
        chunks.Chunks[Vector3.Zero].SetBlockRaw(LoadedPile.X, LoadedPile.Y, LoadedPile.Z, TowerPalette.PileBlock, 0);
        return chunks;
    }

    [Fact]
    public void ATargetInRangeRaisesAPackOfTheFloorsTierTaggedWithTheFloor()
    {
        var chunks = WorldWithPile();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);

        new SkeletonPileManager().Update(floor, Dt, chunks, skeletons, [PlayerAt(8.5f, 11.5f)]);

        Assert.Equal(TowerDifficulty.PackSize(Tier), skeletons.Mobs.Count);
        Assert.All(skeletons.Mobs, s =>
        {
            Assert.Equal(floor.Ref, s.Floor);
            Assert.Equal(Tier, s.Tier);
        });
        Assert.Equal(MobConfig.SkeletonPileWaves - 1, floor.Piles[0].WavesLeft);
    }

    [Fact]
    public void APileDepletesAfterItsWaves()
    {
        var chunks = WorldWithPile();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);
        var piles = new SkeletonPileManager();
        IMobTarget[] targets = [PlayerAt(8.5f, 11.5f)];

        for (var i = 0; i < MobConfig.SkeletonPileWaves + 3; i++)
            piles.Update(floor, MobConfig.SkeletonPileWaveSeconds, chunks, skeletons, targets);

        Assert.True(floor.Piles[0].Depleted);
        Assert.Equal(0, floor.CountPilesLeft());
        var expected = System.Math.Min(MobConfig.MobHardCap, MobConfig.SkeletonPileWaves * TowerDifficulty.PackSize(Tier));
        Assert.Equal(expected, skeletons.Mobs.Count);
    }

    [Fact]
    public void AnImmuneOrDistantTargetRaisesNothing()
    {
        var chunks = WorldWithPile();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);
        var piles = new SkeletonPileManager();
        var immune = new StubTarget(StubTarget.EyeAt(new Vector3(8.5f, 1f, 9.5f))) { IsFlying = true };
        var distant = PlayerAt(8.5f, 8.5f + MobConfig.SkeletonPileTriggerBlocks * 2f);

        for (var i = 0; i < 10; i++)
            piles.Update(floor, MobConfig.SkeletonPileWaveSeconds, chunks, skeletons, [immune, distant]);

        Assert.Empty(skeletons.Mobs);
        Assert.Equal(MobConfig.SkeletonPileWaves, floor.Piles[0].WavesLeft);
    }

    [Fact]
    public void AFullHardCapKeepsTheWave()
    {
        var chunks = WorldWithPile();
        var caps = new MobCaps();
        var skeletons = new SkeletonManager(Type, caps);
        var floor = FloorWithPile(LoadedPile);
        while (caps.ForcedRoom > 0)
            Assert.NotNull(skeletons.Spawn(new Vector3(2.5f, 2f, 2.5f), 0, spawnIn: false));

        new SkeletonPileManager().Update(floor, Dt, chunks, skeletons, [PlayerAt(8.5f, 11.5f)]);

        Assert.Equal(MobConfig.MobHardCap, skeletons.Mobs.Count);
        Assert.Equal(MobConfig.SkeletonPileWaves, floor.Piles[0].WavesLeft);
        Assert.DoesNotContain(skeletons.Mobs, s => s.Floor != null);
    }

    [Fact]
    public void APileInAnUnloadedChunkFreezes()
    {
        var chunks = StoneFloor();
        var skeletons = new SkeletonManager(Type);
        // One chunk over on +Z, which is not loaded
        var unloaded = (X: 8, Y: 1, Z: 8 + Chunk.Size);
        Assert.Null(chunks.ChunkAt(unloaded.X, unloaded.Y, unloaded.Z));
        var floor = FloorWithPile(unloaded);
        var piles = new SkeletonPileManager();

        for (var i = 0; i < 10; i++)
            piles.Update(floor, MobConfig.SkeletonPileWaveSeconds, chunks, skeletons, [PlayerAt(8.5f, unloaded.Z + 2.5f)]);

        Assert.Empty(skeletons.Mobs);
        Assert.Equal(MobConfig.SkeletonPileWaves, floor.Piles[0].WavesLeft);
    }

    [Fact]
    public void APileWhoseBlockIsGoneCountsAsDepleted()
    {
        // The floor names a pile at a loaded cell that holds no pile block
        var chunks = StoneFloor();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);

        new SkeletonPileManager().Update(floor, Dt, chunks, skeletons, [PlayerAt(8.5f, 11.5f)]);

        Assert.True(floor.Piles[0].Depleted);
        Assert.Empty(skeletons.Mobs);
    }

    [Fact]
    public void OwedSkeletonsRiseAgainAtADepletedPile()
    {
        var chunks = WorldWithPile();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);
        floor.Piles[0].WavesLeft = 0;
        var pack = TowerDifficulty.PackSize(Tier);
        var owed = pack + 1;
        floor.Owed = owed;
        var piles = new SkeletonPileManager();
        IMobTarget[] targets = [PlayerAt(8.5f, 11.5f)];

        piles.Update(floor, Dt, chunks, skeletons, targets);
        Assert.Equal(pack, skeletons.Mobs.Count);
        Assert.Equal(owed - pack, floor.Owed);

        for (var i = 0; i < 4 && floor.Owed > 0; i++)
            piles.Update(floor, MobConfig.SkeletonPileWaveSeconds, chunks, skeletons, targets);
        Assert.Equal(0, floor.Owed);
        Assert.Equal(owed, skeletons.Mobs.Count);
        Assert.All(skeletons.Mobs, s => Assert.Equal(floor.Ref, s.Floor));
        Assert.True(floor.Piles[0].Depleted);
    }
}
