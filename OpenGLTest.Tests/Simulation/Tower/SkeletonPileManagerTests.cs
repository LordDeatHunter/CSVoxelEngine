using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The bone piles (<see cref="SkeletonPileManager"/>): each pile of a spot raises exactly one skeleton, bound to the
/// floor, once a non-immune target is in range, and is then gone; an immune or distant target, a full hard cap and an
/// unloaded chunk raise nothing; an owed skeleton lies as a pile where it stood and rises again from there.
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

    private static ChunkManager World() => StoneFloor();

    private static int PileCount => TowerDifficulty.PilesPerSpot(Tier);

    [Fact]
    public void AFloorLaysOutOnePileForEachSkeletonItsSpotHolds()
    {
        var floor = FloorWithPile(LoadedPile);

        Assert.Equal(PileCount, floor.Piles.Length);
        Assert.Equal(PileCount, floor.CountPilesLeft());
        Assert.Equal(floor.Piles.Length, floor.Piles.Select(p => p.Spot).Distinct().Count());
    }

    [Fact]
    public void PilesLieAtFreePositionsNotOnTheBlockGrid()
    {
        var floor = FloorWithPile(LoadedPile);

        Assert.Contains(floor.Piles, p => p.Spot.X % 1f != 0.5f || p.Spot.Z % 1f != 0.5f);
        Assert.All(floor.Piles, p => Assert.Equal(LoadedPile.Y, p.Spot.Y));
    }

    [Fact]
    public void ATargetInRangeRaisesOneSkeletonPerPileTaggedWithTheFloor()
    {
        var chunks = World();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);

        var raised = new SkeletonPileManager().Update(floor, chunks, skeletons, [PlayerAt(8.5f, 11.5f)]);

        Assert.Equal(System.Math.Min(PileCount, TowerDifficulty.PileNearLimit(Tier)), raised);
        Assert.Equal(raised, skeletons.Mobs.Count);
        Assert.All(skeletons.Mobs, s => Assert.Equal(floor.Ref, s.Floor));
        Assert.Equal(PileCount - raised, floor.CountPilesLeft());
    }

    [Fact]
    public void ARaisedPileNeverRaisesAgain()
    {
        var chunks = World();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);
        var piles = new SkeletonPileManager();
        IMobTarget[] targets = [PlayerAt(8.5f, 11.5f)];

        var first = piles.Update(floor, chunks, skeletons, targets);
        foreach (var skeleton in skeletons.Mobs) skeleton.TakeDamage(skeleton.Health);
        skeletons.Update(5f, chunks, new DropManager(), targets);
        var later = 0;
        for (var i = 0; i < 20; i++) later += piles.Update(floor, chunks, skeletons, targets);

        Assert.Equal(PileCount, first + later + floor.CountPilesLeft());
        Assert.Equal(0, floor.Owed);
    }

    [Fact]
    public void AnImmuneOrDistantTargetRaisesNothing()
    {
        var chunks = World();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);
        var piles = new SkeletonPileManager();
        var immune = new StubTarget(StubTarget.EyeAt(new Vector3(8.5f, 1f, 9.5f))) { IsFlying = true };
        var distant = PlayerAt(8.5f, 8.5f + MobConfig.SkeletonPileTriggerBlocks * 2f);

        for (var i = 0; i < 10; i++)
            piles.Update(floor, chunks, skeletons, [immune, distant]);

        Assert.Empty(skeletons.Mobs);
        Assert.Equal(PileCount, floor.CountPilesLeft());
    }

    [Fact]
    public void AFullHardCapKeepsThePiles()
    {
        var chunks = World();
        var caps = new MobCaps();
        var skeletons = new SkeletonManager(Type, caps);
        var floor = FloorWithPile(LoadedPile);
        while (caps.ForcedRoom > 0)
            Assert.NotNull(skeletons.Spawn(new Vector3(2.5f, 2f, 2.5f), spawnIn: false));

        new SkeletonPileManager().Update(floor, chunks, skeletons, [PlayerAt(8.5f, 11.5f)]);

        Assert.Equal(MobConfig.MobHardCap, skeletons.Mobs.Count);
        Assert.Equal(PileCount, floor.CountPilesLeft());
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
            piles.Update(floor, chunks, skeletons, [PlayerAt(8.5f, unloaded.Z + 2.5f)]);

        Assert.Empty(skeletons.Mobs);
        Assert.Equal(PileCount, floor.CountPilesLeft());
    }

    // Fills the pile's surroundings with `count` living skeletons, as earlier piles would have left them
    private static void CrowdPile(SkeletonManager skeletons, int count)
    {
        for (var i = 0; i < count; i++)
            Assert.NotNull(skeletons.Spawn(new Vector3(LoadedPile.X + 0.5f, 1.95f, LoadedPile.Z + 3.5f)));
    }

    [Fact]
    public void PilesRaiseOnlyUpToTheRoomLeftUnderTheNearPileLimit()
    {
        var chunks = World();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);
        var limit = TowerDifficulty.PileNearLimit(Tier);
        CrowdPile(skeletons, limit - 1);

        new SkeletonPileManager().Update(floor, chunks, skeletons, [PlayerAt(8.5f, 11.5f)]);

        Assert.Equal(limit, skeletons.LivingCountNear(new Vector3(8.5f, 1.5f, 8.5f), MobConfig.SkeletonPileNearRadiusBlocks));
    }

    [Fact]
    public void AnAlarmedPileRaisesAtOnceDespiteItsLimitAndNoTargetInRange()
    {
        var chunks = World();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);
        var pile = floor.Piles[0];
        CrowdPile(skeletons, TowerDifficulty.PileNearLimit(Tier));
        var crowd = skeletons.Mobs.Count;
        pile.Alarmed = true;

        new SkeletonPileManager().Update(floor, chunks, skeletons, [PlayerAt(200f, 200f)]);

        Assert.Equal(crowd + 1, skeletons.Mobs.Count);
        Assert.False(pile.Alarmed);
        Assert.True(pile.Raised);
    }

    [Fact]
    public void AHigherFloorAllowsMoreSkeletonsNearItsPiles()
    {
        for (var tier = 0; tier < MobConfig.TowerMaxTier; tier++)
            Assert.True(TowerDifficulty.PileNearLimit(tier + 1) > TowerDifficulty.PileNearLimit(tier));
    }

    [Fact]
    public void AnOwedSkeletonLiesAsAPileWhereItStoodAndRisesFromThere()
    {
        var chunks = World();
        var skeletons = new SkeletonManager(Type);
        var floor = FloorWithPile(LoadedPile);
        foreach (var pile in floor.Piles) pile.Raised = true;
        var spot = new Vector3(8.3f, 1f, 8.7f);
        floor.OwedPiles.Add(spot);
        var visible = new List<Vector3>();
        floor.CollectSpots(visible);
        Assert.Equal([spot], visible);

        new SkeletonPileManager().Update(floor, chunks, skeletons, [PlayerAt(8.5f, 11.5f)]);

        Assert.Equal(0, floor.Owed);
        var risen = Assert.Single(skeletons.Mobs);
        Assert.Equal(floor.Ref, risen.Floor);
        Assert.InRange(risen.Position.X, spot.X - 0.5f, spot.X + 0.5f);
    }
}
