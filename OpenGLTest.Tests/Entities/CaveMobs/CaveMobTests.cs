using System;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;
using VoxelEngine.Entities.CaveMobs;
using Xunit;

namespace VoxelEngine.Tests.Entities.CaveMobs;

/// <summary>
/// The cave mobs' own rules, driven headless: the three kinds' shipped models satisfy their contracts (and a model
/// that does not names its file), the depth tier never falls with depth and the difficulty never eases with the tier,
/// a higher tier mob is tougher while its pool ignores where it stands, the shardling casts only with a clear line and
/// releases once per cast, and the sporeling's burst and the magmaborn's blow land only at their clips' impact.
/// </summary>
public sealed class CaveMobTests
{
    public CaveMobTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static EntityType ShardlingType => EntityRegistry.Require(Shardling.EntityName);
    private static EntityType SporelingType => EntityRegistry.Require(Sporeling.EntityName);
    private static EntityType MagmabornType => EntityRegistry.Require(Magmaborn.EntityName);

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // Body center of a mob of the given height standing on the y = 0 floor
    private static Vector3 OnFloor(float x, float z, float height) => new(x, 1f + height * 0.5f, z);

    private static StubTarget PlayerAt(float x, float z) => new(StubTarget.EyeAt(new Vector3(x, 1f, z)));

    private static CaveMob Build(string kind, Vector3 position, int tier) => kind switch
    {
        Shardling.EntityName => new Shardling(ShardlingType, position, tier, spawnIn: false),
        Sporeling.EntityName => new Sporeling(SporelingType, position, tier, spawnIn: false),
        _ => new Magmaborn(MagmabornType, position, tier, spawnIn: false),
    };

    [Fact]
    public void ShippedModelsSatisfyTheirContracts()
    {
        Shardling.ValidateContent(ShardlingType);
        Sporeling.ValidateContent(SporelingType);
        Magmaborn.ValidateContent(MagmabornType);
        foreach (var type in new[] { ShardlingType, SporelingType, MagmabornType })
        {
            foreach (var child in new[] { "Head", "ArmL", "ArmR" })
                Assert.Equal("Torso", type.Model.Parts[child].Parent);
            Assert.NotNull(type.Model.TexturePixels);
        }
    }

    [Theory]
    [InlineData(Shardling.EntityName)]
    [InlineData(Sporeling.EntityName)]
    [InlineData(Magmaborn.EntityName)]
    public void ValidateContentNamesTheModelThatLacksItsClips(string kind)
    {
        const string path = "assets/models/entity/clipless.bbmodel";
        var model = new BbmodelData();
        foreach (var name in EntityContract.HumanoidParts)
        {
            var part = new BbmodelPart();
            part.Cubes.Add(new BbmodelCube { Min = Vector3.Zero, Max = Vector3.One });
            model.Parts.Add(name, part);
        }
        var type = new EntityType { Name = kind, DisplayName = kind, MaxHealth = 1, ModelPath = path, Model = model };

        Action validate = kind switch
        {
            Shardling.EntityName => () => Shardling.ValidateContent(type),
            Sporeling.EntityName => () => Sporeling.ValidateContent(type),
            _ => () => Magmaborn.ValidateContent(type),
        };
        var error = Assert.Throws<InvalidOperationException>(validate);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void TheTierNeverFallsWithDepthAndIsZeroAboveTheEchoBottom()
    {
        var bottom = WorldGenerationConfig.CaveEchoBottomY;
        for (var y = 100; y >= bottom; y--)
            Assert.Equal(0, CaveDifficulty.TierAt(y));

        var previous = 0;
        for (var y = bottom; y >= WorldGenerationConfig.BarrierBottomY - 200; y--)
        {
            var tier = CaveDifficulty.TierAt(y);
            Assert.True(tier >= previous, $"y {y} is a lower tier than the cell above it");
            Assert.Equal(CaveDifficulty.Clamp(tier), tier);
            previous = tier;
        }
        Assert.True(previous > 0, "no depth ever raises the tier");
    }

    [Fact]
    public void DifficultyNeverEasesWithTierAndIsCapped()
    {
        var previous = CaveDifficulty.Multiplier(0);
        Assert.Equal(1f, previous);
        for (var tier = 1; tier < 32; tier++)
        {
            var multiplier = CaveDifficulty.Multiplier(tier);
            Assert.True(multiplier >= previous, $"tier {tier} is easier than the one before");
            Assert.True(multiplier <= MobConfig.CaveDifficultyMaxMultiplier);
            Assert.True(CaveDifficulty.Scale(10, tier) >= CaveDifficulty.Scale(10, tier - 1));
            previous = multiplier;
        }
        Assert.True(CaveDifficulty.Multiplier(MobConfig.CaveMaxTier) > CaveDifficulty.Multiplier(0));
    }

    [Theory]
    [InlineData(Shardling.EntityName)]
    [InlineData(Sporeling.EntityName)]
    [InlineData(Magmaborn.EntityName)]
    public void AHigherTierMobIsTougherAndItsPoolIgnoresWhereItStands(string kind)
    {
        for (var tier = 0; tier < MobConfig.CaveMaxTier; tier++)
        {
            var lower = Build(kind, new Vector3(0f, -100f, 0f), tier);
            var higher = Build(kind, new Vector3(0f, -100f, 0f), tier + 1);
            Assert.True(higher.MaxHealth >= lower.MaxHealth, $"tier {tier + 1} has a smaller pool than tier {tier}");
            Assert.Equal(higher.MaxHealth, higher.Health);
        }
        Assert.True(Build(kind, Vector3.Zero, MobConfig.CaveMaxTier).MaxHealth > Build(kind, Vector3.Zero, 0).MaxHealth);

        // One tier at two very different heights: the pool follows the tier, never the height
        var shallow = Build(kind, new Vector3(0f, 50f, 0f), 2);
        var deep = Build(kind, new Vector3(0f, WorldGenerationConfig.BarrierTopY + 5f, 0f), 2);
        Assert.Equal(shallow.Tier, deep.Tier);
        Assert.Equal(shallow.MaxHealth, deep.MaxHealth);
    }

    [Fact]
    public void ADeeperTierHitsHarder()
    {
        var shallow = Vector3.Zero;
        var top = MobConfig.CaveMaxTier;
        Assert.True(new Shardling(ShardlingType, shallow, top).ShardDamage > new Shardling(ShardlingType, shallow, 0).ShardDamage);
        Assert.True(new Sporeling(SporelingType, shallow, top).BurstDamage > new Sporeling(SporelingType, shallow, 0).BurstDamage);
        Assert.True(new Magmaborn(MagmabornType, shallow, top).MeleeDamage > new Magmaborn(MagmabornType, shallow, 0).MeleeDamage);
    }

    [Fact]
    public void TheShardBandFitsInsideTheShardsLevelRange()
    {
        var levelRange = MobConfig.CaveShardSpeed * MobConfig.CaveShardSpeed / ThrownProjectile.Gravity;
        Assert.True(MobConfig.CaveShardlingMaxRange < levelRange);
        Assert.True(MobConfig.CaveShardlingMinRange < MobConfig.CaveShardlingMaxRange);
    }

    [Fact]
    public void AShardlingReleasesOnlyAtItsReleaseEventWithAClearLine()
    {
        var chunks = StoneFloor();
        var band = (MobConfig.CaveShardlingMinRange + MobConfig.CaveShardlingMaxRange) * 0.5f;
        var caster = new Shardling(ShardlingType, OnFloor(1.5f, 8.5f, MobConfig.CaveShardlingHeight), 0, spawnIn: false);
        var target = PlayerAt(1.5f + band, 8.5f);

        var starts = 0;
        var releases = 0;
        var wasCasting = false;
        for (var i = 0; i < (int)(15f / Dt) && releases < 3; i++)
        {
            caster.Update(Dt, chunks, target);
            var casting = caster.Animation.IsPlaying(Shardling.ThrowClip);
            if (casting && !wasCasting) starts++;
            wasCasting = casting;
            if (!caster.TryTakeShotRelease(out _, out var velocity)) continue;
            releases++;
            Assert.True(casting, "the shard left the hand after the cast clip had ended");
            Assert.True(velocity.X > 0f, "the shard flew away from its target");
        }
        Assert.Equal(3, releases);
        Assert.Equal(releases, starts);

        // A wall too tall to jump between them: no cast ever starts
        var walled = StoneFloor();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var y = 1; y <= 4; y++)
        for (var z = 0; z < Chunk.Size; z++)
            walled.Chunks[Vector3.Zero].SetBlockRaw(8, y, z, stone, 0);
        var blind = new Shardling(ShardlingType, OnFloor(2.5f, 8.5f, MobConfig.CaveShardlingHeight), 0, spawnIn: false);
        var hidden = PlayerAt(14.5f, 8.5f);
        for (var i = 0; i < (int)(6f / Dt); i++)
        {
            blind.Update(Dt, walled, hidden);
            Assert.False(blind.Animation.IsPlaying(Shardling.ThrowClip), $"cast through the wall at frame {i}");
            Assert.False(blind.TryTakeShotRelease(out _, out _));
        }
    }

    [Fact]
    public void ASporelingBurstLandsOnlyAtItsImpactEvent()
    {
        var chunks = StoneFloor();
        var sporeling = new Sporeling(SporelingType, OnFloor(6.5f, 8.5f, MobConfig.CaveSporelingHeight), 0, spawnIn: false);
        var target = PlayerAt(6.5f + MobConfig.CaveSporelingTriggerReach * 0.6f, 8.5f);

        var impacts = 0;
        for (var i = 0; i < (int)(10f / Dt) && impacts < 2; i++)
        {
            sporeling.Update(Dt, chunks, target);
            var bursting = sporeling.Animation.IsPlaying(Sporeling.MeleeClip);
            if (!sporeling.TryTakeBurstImpact()) continue;
            impacts++;
            Assert.True(bursting, "the burst went out after the melee clip had ended");
            Assert.True(Sporeling.InBurst(sporeling.Feet, target.Eye), "a target in trigger reach stood outside the burst");
        }
        Assert.Equal(2, impacts);

        // A target out of reach: the clip never starts, so no burst ever lands
        var lonely = new Sporeling(SporelingType, OnFloor(2.5f, 8.5f, MobConfig.CaveSporelingHeight), 0, spawnIn: false);
        var far = PlayerAt(2.5f, 8.5f + 40f);
        for (var i = 0; i < (int)(1f / Dt); i++)
        {
            lonely.Update(Dt, chunks, far);
            Assert.False(lonely.TryTakeBurstImpact(), $"a burst landed with nobody in reach at frame {i}");
        }
    }

    [Fact]
    public void AMagmabornBlowLandsOnlyAtItsImpactEvent()
    {
        var chunks = StoneFloor();
        var brute = new Magmaborn(MagmabornType, OnFloor(2.5f, 8.5f, MobConfig.CaveMagmabornHeight), 0, spawnIn: false);
        var target = PlayerAt(8.5f, 8.5f);

        var impacts = 0;
        for (var i = 0; i < (int)(10f / Dt) && impacts < 2; i++)
        {
            brute.Update(Dt, chunks, target);
            var swinging = brute.Animation.IsPlaying(Magmaborn.MeleeClip);
            if (!brute.TryTakeMeleeImpact()) continue;
            impacts++;
            Assert.True(swinging, "the blow landed after the melee clip had ended");
            Assert.True(Magmaborn.InMeleeReach(brute.Position, target.Eye), "swung from out of reach");
        }
        Assert.Equal(2, impacts);
    }
}
