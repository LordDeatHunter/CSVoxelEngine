using System;
using System.Numerics;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.Spiders;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Caves;
using VoxelEngine.Status;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.Entities.Spiders;

/// <summary>
/// The spiders' rules, driven headless: both shipped models keep the leg contract and rest within their own reach,
/// a solved leg puts its foot on the point it was given, a spider climbs the cliff between it and its target and
/// rounds a ledge down to one below without ever letting go, a blow knocks it off a wall and the fall never hurts
/// it, its feet take hold of the blocks around it, its stance survives the wire, spiders share every cave with the
/// biome's own kind, and a bite poisons.
/// </summary>
public sealed class SpiderTests
{
    public SpiderTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    // The plateau the climbing tests use: every column from PlateauX on is solid up to PlateauTop
    private const int PlateauX = 8;
    private const int PlateauTop = 4;

    private static EntityType TypeOf(bool broodmother) =>
        EntityRegistry.Require(broodmother ? Spider.BroodmotherEntityName : Spider.EntityName);

    private static Spider NewSpider(Vector3 center, bool broodmother = false) =>
        new(TypeOf(broodmother), center, tier: 0, broodmother, spawnIn: false);

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // A stone floor with a cliff: a plateau PlateauTop cells high over the far half of the chunk
    private static ChunkManager Cliff()
    {
        var chunks = StoneFloor();
        var chunk = chunks.Chunks[Vector3.Zero];
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = PlateauX; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 1; y <= PlateauTop; y++)
            chunk.SetBlockRaw(x, y, z, stone, 0);
        return chunks;
    }

    private static Vector3 OnFloor(float x, float z, float floorTop = 1f) =>
        new(x, floorTop + SpiderConfig.SpiderBodySize * 0.5f, z);

    private static StubTarget PlayerOn(float x, float z, float floorTop) =>
        new(StubTarget.EyeAt(new Vector3(x, floorTop, z))) { Effects = new StatusEffectSet() };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShippedModelsKeepTheLegContractAndRestWithinReach(bool broodmother)
    {
        var type = TypeOf(broodmother);
        Spider.ValidateContent(type);

        foreach (var leg in SpiderRig.For(type).Legs)
        {
            Assert.True(Vector3.Distance(leg.Hip, leg.Rest) < leg.Reach, "a resting foot is out of its leg's reach");
            // The right legs rest on the model's right, the left on its left
            Assert.True(leg.Rest.X * leg.Side > 0f);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASolvedLegPutsItsFootOnTheTargetWithItsKneeUp(bool broodmother)
    {
        foreach (var leg in SpiderRig.For(TypeOf(broodmother)).Legs)
        {
            foreach (var nudge in new[] { Vector3.Zero, new Vector3(0f, 0.2f, 0.15f) * leg.Reach, new Vector3(0f, -0.2f, -0.1f) * leg.Reach })
            {
                var foot = leg.Rest + nudge;
                LegIk.Solve(leg.Hip, foot, leg.Side, leg.UpperLength, leg.LowerLength, Vector3.UnitY,
                    out var upper, out var lowerLocal, out var knee);

                // The segments rest along the leg's side of X; the lower one turns in the upper one's frame
                var kneeFromBones = leg.Hip + Vector3.Transform(new Vector3(leg.Side * leg.UpperLength, 0f, 0f), upper);
                var tip = kneeFromBones
                          + Vector3.Transform(new Vector3(leg.Side * leg.LowerLength, 0f, 0f), upper * lowerLocal);
                Assert.True(Vector3.Distance(knee, kneeFromBones) < 1e-3f);
                Assert.True(Vector3.Distance(tip, foot) < 1e-3f, $"the foot missed its target by {Vector3.Distance(tip, foot)}");
                // The knee bends up, away from the straight line between hip and foot
                var line = Vector3.Normalize(foot - leg.Hip);
                var onLine = leg.Hip + line * Vector3.Dot(knee - leg.Hip, line);
                Assert.True(knee.Y > onLine.Y, "the knee folded under the leg");
            }
        }
    }

    [Fact]
    public void AFootOutOfReachIsAimedAtWithoutStretchingTheLeg()
    {
        var leg = SpiderRig.For(TypeOf(false)).Legs[0];
        var far = leg.Hip + Vector3.Normalize(leg.Rest - leg.Hip) * leg.Reach * 3f;
        LegIk.Solve(leg.Hip, far, leg.Side, leg.UpperLength, leg.LowerLength, Vector3.UnitY, out var upper,
            out var lowerLocal, out var knee);

        var tip = knee + Vector3.Transform(new Vector3(leg.Side * leg.LowerLength, 0f, 0f), upper * lowerLocal);
        Assert.True(Vector3.Distance(leg.Hip, tip) <= leg.Reach + 1e-3f);
        Assert.True(Vector3.Dot(Vector3.Normalize(tip - leg.Hip), Vector3.Normalize(far - leg.Hip)) > 0.999f);
    }

    [Fact]
    public void ASpiderClimbsTheCliffBetweenItAndItsTarget()
    {
        var chunks = Cliff();
        var spider = NewSpider(OnFloor(3.5f, 8.5f));
        var target = PlayerOn(12.5f, 8.5f, PlateauTop + 1f);

        var climbed = false;
        for (var i = 0; i < (int)(20f / Dt) && !spider.InBiteReach(spider.Position, target.Eye); i++)
        {
            spider.Update(Dt, chunks, target);
            climbed |= spider.IsClinging && MathF.Abs(spider.SurfaceNormal.Y) < 0.5f;
        }

        Assert.True(climbed, "the spider never took to the cliff face");
        Assert.True(spider.InBiteReach(spider.Position, target.Eye), "the spider never reached the target on the cliff");
        Assert.True(spider.Position.Y > PlateauTop + 1f, "the spider is not up on the plateau");
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0f, -1f)]
    public void ASpiderTurnsToATargetOnAnySideOfIt(float dx, float dz)
    {
        // Straight ahead, straight behind and to either side of the way a fresh spider faces
        var chunks = StoneFloor();
        var start = OnFloor(8.5f, 8.5f);
        var spider = NewSpider(start);
        var target = PlayerOn(8.5f + dx * 5f, 8.5f + dz * 5f, 1f);

        for (var i = 0; i < (int)(6f / Dt) && !spider.InBiteReach(spider.Position, target.Eye); i++)
            spider.Update(Dt, chunks, target);

        Assert.True(spider.InBiteReach(spider.Position, target.Eye), "the spider never closed on its target");
    }

    [Fact]
    public void ASpiderRoundsTheLedgeDownToATargetBelowWithoutLettingGo()
    {
        var chunks = Cliff();
        var spider = NewSpider(OnFloor(12.5f, 8.5f, PlateauTop + 1f));
        var target = PlayerOn(2.5f, 8.5f, 1f);

        var onFace = false;
        // The first update takes hold of the plateau; from then on it must never fall
        spider.Update(Dt, chunks, target);
        for (var i = 0; i < (int)(20f / Dt) && !spider.InBiteReach(spider.Position, target.Eye); i++)
        {
            spider.Update(Dt, chunks, target);
            Assert.True(spider.IsClinging, "the spider let go of the ledge");
            onFace |= MathF.Abs(spider.SurfaceNormal.Y) < 0.5f;
        }

        Assert.True(onFace, "the spider never walked the cliff face down");
        Assert.True(spider.InBiteReach(spider.Position, target.Eye), "the spider never reached the target below");
    }

    [Fact]
    public void ABlowKnocksASpiderOffAWallAndTheFallNeverHurtsIt()
    {
        // A cliff as tall as the chunk allows, so the drop is far past any walker's safe fall
        var chunks = StoneFloor();
        var chunk = chunks.Chunks[Vector3.Zero];
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var x = PlateauX; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        for (var y = 1; y < Chunk.Size; y++)
            chunk.SetBlockRaw(x, y, z, stone, 0);

        var spider = NewSpider(OnFloor(6.5f, 8.5f));
        var target = PlayerOn(12.5f, 8.5f, Chunk.Size);
        for (var i = 0; i < (int)(20f / Dt) && spider.Position.Y < 11f; i++)
            spider.Update(Dt, chunks, target);
        Assert.True(spider.Position.Y >= 11f && spider.IsClinging, "the spider did not climb the wall");

        spider.Shove(-Vector3.UnitX, 4f);
        Assert.False(spider.IsClinging);

        for (var i = 0; i < (int)(5f / Dt) && !spider.IsGrounded; i++)
            spider.Update(Dt, chunks, null);
        Assert.True(spider.IsGrounded, "the spider never landed");
        Assert.Equal(spider.MaxHealth, spider.Health);
    }

    [Fact]
    public void TheFeetTakeHoldOfTheFloorAndOfAWallBesideTheBody()
    {
        var chunks = Cliff();
        // Close enough to the cliff that the legs on that side cannot rest on the floor
        var spider = NewSpider(OnFloor(PlateauX - 0.5f, 8.5f));
        spider.Update(Dt, chunks, null);
        for (var i = 0; i < 120; i++)
            spider.AnimateCosmetics(Dt, chunks);

        // A spider standing still settles: no foot keeps stepping in place
        for (var leg = 0; leg < SpiderRig.LegCount; leg++)
            Assert.False(spider.Legs.IsStepping(leg), "a foot is still stepping under a body at rest");

        var onFloor = 0;
        var onWall = 0;
        for (var leg = 0; leg < SpiderRig.LegCount; leg++)
        {
            var foot = spider.Legs.Foot(leg);
            if (MathF.Abs(foot.Y - 1f) < 1e-2f) onFloor++;
            else if (MathF.Abs(foot.X - PlateauX) < 1e-2f) onWall++;
        }

        Assert.Equal(SpiderRig.LegCount, onFloor + onWall);
        Assert.True(onFloor > 0 && onWall > 0, $"feet on the floor: {onFloor}, on the wall: {onWall}");
    }

    [Fact]
    public void TheStanceSurvivesTheWire()
    {
        // Every face and a spread of headings: the angle and the face index give the heading back
        for (var index = 1; index <= 6; index++)
        {
            var normal = CrawlFrame.NormalOf(index);
            Assert.Equal(index, CrawlFrame.IndexOf(normal));
            for (var angle = -3f; angle <= 3f; angle += 0.5f)
            {
                var heading = CrawlFrame.HeadingOf(normal, angle);
                Assert.True(MathF.Abs(Vector3.Dot(heading, normal)) < 1e-5f, "a heading left its face");
                Assert.True(Vector3.Distance(heading, CrawlFrame.HeadingOf(normal, CrawlFrame.AngleOf(normal, heading))) < 1e-4f);
            }
        }

        // A mirror told the server's surface and facing stands as the server's spider does
        var chunks = Cliff();
        var spider = new Spider(TypeOf(false), OnFloor(6.5f, 8.5f), tier: 2, broodmother: false, spawnIn: false);
        var target = PlayerOn(12.5f, 8.5f, PlateauTop + 1f);
        for (var i = 0; i < (int)(20f / Dt) && MathF.Abs(spider.SurfaceNormal.Y) > 0.5f; i++)
            spider.Update(Dt, chunks, target);
        Assert.True(MathF.Abs(spider.SurfaceNormal.Y) < 0.5f, "the spider never reached the cliff face");

        var packed = spider.PackStance();
        Assert.Equal(spider.Tier, CaveDifficulty.Clamp((byte)packed));
        var mirror = NewSpider(spider.Position);
        mirror.NetworkSetState(spider.Position, spider.Facing, spider.Health, spider.Velocity);
        mirror.MirrorSurface(Spider.SurfaceOf(packed));
        Assert.Equal(spider.SurfaceNormal, mirror.SurfaceNormal);
        Assert.Equal(spider.SurfaceIndex, mirror.SurfaceIndex);
    }

    [Fact]
    public void SpidersTakeAShareOfEveryCaveAndLeaveTheRestToItsOwnKind()
    {
        foreach (var biome in Enum.GetValues<CaveBiome>())
        {
            var own = CaveSpawnRules.CaveMobKindFor(biome);
            Assert.Equal(own, CaveSpawnRules.KindFor(biome, 0f, spiders: false));
            Assert.Equal(Spider.EntityName, CaveSpawnRules.KindFor(biome, 0f, spiders: true));
            // The top of the roll is never a spider's: the biome's own kind, or nothing where it has none
            Assert.Equal(own, CaveSpawnRules.KindFor(biome, 0.9999f, spiders: true));
        }
    }

    [Fact]
    public void ABitePoisonsItsTarget()
    {
        var chunks = StoneFloor();
        var venom = StatusEffectRegistry.Require("venom");
        var manager = new CaveMobManager(null, null, null, null, TypeOf(false), TypeOf(true));
        var drops = new DropManager();
        Assert.NotNull(manager.Spawn(Spider.EntityName, OnFloor(3.5f, 8.5f), 0, spawnIn: false));

        var target = PlayerOn(8.5f, 8.5f, 1f);
        for (var i = 0; i < (int)(10f / Dt) && target.DamageTaken == 0; i++)
            manager.Update(Dt, chunks, drops, [target]);

        Assert.True(target.DamageTaken > 0, "the spider never landed its bite");
        Assert.True(target.Effects!.IsActive(venom));
    }
}
