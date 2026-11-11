using System;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Simulation;
using VoxelEngine.Status;
using VoxelEngine.WorldGen.Hell;
using Xunit;

namespace VoxelEngine.Tests.Entities.HellMobs;

/// <summary>
/// The hell mobs' own rules, driven headless: both kinds' shipped models satisfy their contracts
/// (and a model that does not names its file), the layer difficulty never eases with depth and a
/// deeper mob is tougher, the brute's blow lands only at its clip's impact on a target still in reach,
/// the caster casts only with a clear line and launches its bolt only at the clip's release, and a
/// bolt that touches a target hurts it and sets it burning.
/// </summary>
public sealed class HellMobTests
{
    public HellMobTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static EntityType Brute => EntityRegistry.Require(Hellbrute.EntityName);
    private static EntityType Caster => EntityRegistry.Require(Cinderwitch.EntityName);

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // Body center of a mob of the given height standing on the y = 0 floor
    private static Vector3 OnFloor(float x, float z, float height) => new(x, 1f + height * 0.5f, z);

    private static StubTarget PlayerAt(float x, float z) => new(StubTarget.EyeAt(new Vector3(x, 1f, z)));

    [Fact]
    public void ShippedModelsSatisfyTheirContractsWithHeadAndArmsUnderTheTorso()
    {
        Hellbrute.ValidateContent(Brute);
        Cinderwitch.ValidateContent(Caster);
        foreach (var type in new[] { Brute, Caster })
        {
            foreach (var child in new[] { "Head", "ArmL", "ArmR" })
                Assert.Equal("Torso", type.Model.Parts[child].Parent);
            Assert.NotNull(type.Model.TexturePixels);
        }
    }

    [Theory]
    [InlineData(Hellbrute.EntityName)]
    [InlineData(Cinderwitch.EntityName)]
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

        Action validate = kind == Hellbrute.EntityName
            ? () => Hellbrute.ValidateContent(type)
            : () => Cinderwitch.ValidateContent(type);
        var error = Assert.Throws<InvalidOperationException>(validate);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void DifficultyNeverEasesWithDepthAndStaysCapped()
    {
        var previous = LayerDifficulty.Multiplier(0);
        Assert.Equal(1f, previous);
        for (var layer = 1; layer < 64; layer++)
        {
            var multiplier = LayerDifficulty.Multiplier(layer);
            Assert.True(multiplier >= previous, $"layer {layer} is easier than the one above");
            Assert.True(multiplier <= MobConfig.LayerDifficultyMaxMultiplier);
            Assert.True(LayerDifficulty.Scale(10, layer) >= LayerDifficulty.Scale(10, layer - 1));
            Assert.True(LayerDifficulty.Cooldown(2f, layer) <= LayerDifficulty.Cooldown(2f, layer - 1));
            Assert.InRange(LayerDifficulty.PackSize(layer), LayerDifficulty.PackSize(layer - 1), MobConfig.HellPackMaxSize);
            previous = multiplier;
        }
        Assert.True(LayerDifficulty.Multiplier(4) > LayerDifficulty.Multiplier(0));
    }

    [Fact]
    public void ADeeperMobIsTougherAndHitsHarder()
    {
        var topY = WorldGenerationConfig.HellTopY - 10f;
        var deepY = topY - 4 * WorldGenerationConfig.HellLayerPeriodBlocks;
        Assert.True(HellGenerator.LayerIndex((int)deepY) > HellGenerator.LayerIndex((int)topY));

        var shallowBrute = new Hellbrute(Brute, new Vector3(0f, topY, 0f));
        var deepBrute = new Hellbrute(Brute, new Vector3(0f, deepY, 0f));
        Assert.True(deepBrute.MaxHealth > shallowBrute.MaxHealth);
        Assert.Equal(deepBrute.MaxHealth, deepBrute.Health);
        Assert.True(deepBrute.MeleeDamage > shallowBrute.MeleeDamage);

        var shallowCaster = new Cinderwitch(Caster, new Vector3(0f, topY, 0f));
        var deepCaster = new Cinderwitch(Caster, new Vector3(0f, deepY, 0f));
        Assert.True(deepCaster.MaxHealth > shallowCaster.MaxHealth);
        Assert.True(deepCaster.BoltDamage > shallowCaster.BoltDamage);
    }

    [Fact]
    public void TheBrutesBlowLandsOnlyAtItsImpactOnATargetStillInReach()
    {
        var chunks = StoneFloor();
        var brute = new Hellbrute(Brute, OnFloor(2.5f, 8.5f, MobConfig.HellbruteHeight));
        var target = PlayerAt(8.5f, 8.5f);

        var impacts = 0;
        for (var i = 0; i < (int)(10f / Dt) && impacts < 2; i++)
        {
            brute.Update(Dt, chunks, target);
            var swinging = brute.Animation.IsPlaying(Hellbrute.MeleeClip);
            if (!brute.TryTakeMeleeImpact()) continue;
            impacts++;
            Assert.True(swinging, "the blow landed after the melee clip had ended");
            Assert.True(Hellbrute.InMeleeReach(brute.Position, target.Eye), "swung from out of reach");
        }
        Assert.Equal(2, impacts);

        // Through the manager: a target that backs off during the wind-up takes nothing
        var manager = new HellMobManager(Brute, null);
        var managed = (Hellbrute)manager.Spawn(Hellbrute.EntityName, OnFloor(8.5f, 8.5f, MobConfig.HellbruteHeight),
            spawnIn: false)!;
        var close = PlayerAt(8.5f + MobConfig.HellbruteMeleeReach * 0.5f, 8.5f);
        var targets = new IMobTarget[] { close };
        var drops = new DropManager();
        for (var i = 0; i < (int)(3f / Dt) && !managed.Animation.IsPlaying(Hellbrute.MeleeClip); i++)
            manager.Update(Dt, chunks, drops, targets);
        Assert.True(managed.Animation.IsPlaying(Hellbrute.MeleeClip), "the brute never swung at a target in reach");

        close.Eye += new Vector3(MobConfig.HellbruteMeleeReach * 4f, 0f, 0f);
        for (var i = 0; i < (int)(1.5f / Dt); i++)
            manager.Update(Dt, chunks, drops, targets);
        Assert.Equal(0, close.DamageTaken);
    }

    [Fact]
    public void TheCasterCastsOnlyWithAClearLineAndReleasesOncePerCast()
    {
        var chunks = StoneFloor();
        var band = (MobConfig.HellCasterMinRange + MobConfig.HellCasterMaxRange) * 0.5f;
        var caster = new Cinderwitch(Caster, OnFloor(1.5f, 8.5f, MobConfig.HellCasterHeight), spawnIn: false);
        var target = PlayerAt(1.5f + band, 8.5f);

        var starts = 0;
        var releases = 0;
        var wasCasting = false;
        for (var i = 0; i < (int)(15f / Dt) && releases < 3; i++)
        {
            caster.Update(Dt, chunks, target);
            var casting = caster.Animation.IsPlaying(Cinderwitch.CastClip);
            if (casting && !wasCasting) starts++;
            wasCasting = casting;
            if (!caster.TryTakeCastRelease(out _, out var velocity)) continue;
            releases++;
            Assert.True(casting, "the bolt left the hand after the cast clip had ended");
            Assert.True(velocity.X > 0f, "the bolt flew away from its target");
        }
        Assert.Equal(3, releases);
        Assert.Equal(releases, starts);

        // A wall too tall to jump between them: no cast ever starts
        var walled = StoneFloor();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var y = 1; y <= 4; y++)
        for (var z = 0; z < Chunk.Size; z++)
            walled.Chunks[Vector3.Zero].SetBlockRaw(8, y, z, stone, 0);
        var blind = new Cinderwitch(Caster, OnFloor(2.5f, 8.5f, MobConfig.HellCasterHeight), spawnIn: false);
        var hidden = PlayerAt(14.5f, 8.5f);
        for (var i = 0; i < (int)(6f / Dt); i++)
        {
            blind.Update(Dt, walled, hidden);
            Assert.False(blind.Animation.IsPlaying(Cinderwitch.CastClip), $"cast through the wall at frame {i}");
            Assert.False(blind.TryTakeCastRelease(out _, out _));
        }
    }

    [Fact]
    public void TheCasterStillCastsWhileATargetHugsItInsideItsMinRange()
    {
        var chunks = StoneFloor();
        var caster = new Cinderwitch(Caster, OnFloor(4.5f, 8.5f, MobConfig.HellCasterHeight), spawnIn: false);
        var target = PlayerAt(4.5f - MobConfig.HellCasterMinRange * 0.4f, 8.5f);

        var starts = 0;
        var releases = 0;
        var wasCasting = false;
        for (var i = 0; i < (int)(4f / Dt) && releases < 1; i++)
        {
            // The player keeps hugging it from behind as it backs away, so it never reaches its band
            target.Eye = StubTarget.EyeAt(new Vector3(caster.Position.X - MobConfig.HellCasterMinRange * 0.4f, 1f, 8.5f));
            caster.Update(Dt, chunks, target);
            var casting = caster.Animation.IsPlaying(Cinderwitch.CastClip);
            if (casting && !wasCasting) starts++;
            wasCasting = casting;
            if (!caster.TryTakeCastRelease(out _, out var velocity)) continue;
            releases++;
            Assert.True(velocity.X < 0f, "the bolt flew away from its target");
        }

        Assert.True(starts >= 1, "the caster never cast at a target inside its minimum range");
        Assert.True(releases >= 1, "the caster never released a bolt at a close target");
    }

    [Fact]
    public void ABoltThatTouchesATargetBurnsItButSparesAnImmuneOne()
    {
        var chunks = StoneFloor();
        var burning = StatusEffectRegistry.Require("burning");
        var manager = new HellMobManager(null, Caster);
        var drops = new DropManager();

        var target = new StubTarget(StubTarget.EyeAt(new Vector3(10.5f, 1f, 8.5f))) { Effects = new StatusEffectSet() };
        var chest = HellMob.ChestOf(target.Eye);
        manager.TestLaunchBolt(chest - new Vector3(4f, 0f, 0f), new Vector3(20f, 3f, 0f), damage: 5);
        for (var i = 0; i < 60 && manager.Bolts.Count > 0; i++)
            manager.Update(Dt, chunks, drops, [target]);

        Assert.Empty(manager.Bolts);
        Assert.Equal(5, target.DamageTaken);
        Assert.True(target.Effects.IsActive(burning));

        var flier = new StubTarget(target.Eye) { Effects = new StatusEffectSet(), IsFlying = true };
        manager.TestLaunchBolt(chest - new Vector3(4f, 0f, 0f), new Vector3(20f, 3f, 0f), damage: 5);
        for (var i = 0; i < 60 && manager.Bolts.Count > 0; i++)
            manager.Update(Dt, chunks, drops, [flier]);
        Assert.Equal(0, flier.DamageTaken);
        Assert.False(flier.Effects.IsActive(burning));
    }

    [Fact]
    public void TheCastersBandStaysInsideItsBoltsLevelRange()
    {
        var levelRange = MobConfig.HellBoltSpeed * MobConfig.HellBoltSpeed / ThrownProjectile.Gravity;
        Assert.True(MobConfig.HellCasterMaxRange < levelRange);
        Assert.True(MobConfig.HellCasterMinRange < MobConfig.HellCasterMaxRange);
    }
}
