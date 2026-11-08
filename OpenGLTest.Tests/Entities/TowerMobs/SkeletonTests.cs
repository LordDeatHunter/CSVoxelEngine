using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.TowerMobs;
using Xunit;

namespace VoxelEngine.Tests.Entities.TowerMobs;

/// <summary>
/// The skeleton's own rules, driven headless: its shipped model satisfies its contract (and a model that does
/// not names its file), the tower difficulty never eases with the floor tier and stays capped, a higher-tier
/// skeleton is tougher and hits harder, it shoots only inside its bow band with a clear line (and rakes a target
/// in reach instead), each clip's event is taken once per attack, the band stays inside the arrow's range, the
/// replicated attack count names the clip it started, and it is undead.
/// </summary>
public sealed class SkeletonTests
{
    public SkeletonTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static EntityType Type => EntityRegistry.Require(Skeleton.EntityName);

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    // Body center of a skeleton standing on the y = 0 floor
    private static Vector3 OnFloor(float x, float z) => new(x, 1f + MobConfig.SkeletonHeight * 0.5f, z);

    private static StubTarget PlayerAt(float x, float z) => new(StubTarget.EyeAt(new Vector3(x, 1f, z)));

    [Fact]
    public void ShippedModelSatisfiesItsContractWithHeadAndArmsUnderTheTorso()
    {
        Skeleton.ValidateContent(Type);
        foreach (var child in new[] { "Head", "ArmL", "ArmR" })
            Assert.Equal("Torso", Type.Model.Parts[child].Parent);
        Assert.NotNull(Type.Model.TexturePixels);
    }

    [Theory]
    [InlineData(Skeleton.MeleeClip, null)]
    [InlineData(Skeleton.ShootClip, null)]
    [InlineData(null, Skeleton.ImpactEvent)]
    [InlineData(null, Skeleton.ReleaseEvent)]
    public void ValidateContentNamesTheModelThatLacksAClipOrEvent(string? missingClip, string? missingEvent)
    {
        const string path = "assets/models/entity/broken_skeleton.bbmodel";
        var model = new BbmodelData();
        foreach (var name in EntityContract.HumanoidParts)
        {
            var part = new BbmodelPart();
            part.Cubes.Add(new BbmodelCube { Min = Vector3.Zero, Max = Vector3.One });
            model.Parts.Add(name, part);
        }

        var clips = new (string Clip, string? Event)[]
        {
            (EntityAnimator.IdleClip, null), (EntityAnimator.WalkClip, null),
            (Skeleton.MeleeClip, Skeleton.ImpactEvent), (Skeleton.ShootClip, Skeleton.ReleaseEvent),
        };
        foreach (var (clip, eventName) in clips)
        {
            if (clip == missingClip) continue;
            var events = new List<AnimationClip.Event>();
            if (eventName != null && eventName != missingEvent) events.Add(new AnimationClip.Event(0.2f, eventName));
            model.Animations.Add(new AnimationClip(clip, 1f, AnimationClip.LoopMode.Once,
                new Dictionary<string, AnimationClip.BoneTrack>(), events));
        }
        var type = new EntityType { Name = "skeleton", DisplayName = "Skeleton", MaxHealth = 1, ModelPath = path, Model = model };

        var error = Assert.Throws<InvalidOperationException>(() => Skeleton.ValidateContent(type));
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void TowerDifficultyNeverEasesWithTierAndStaysCapped()
    {
        Assert.Equal(1f, TowerDifficulty.Multiplier(0));
        for (var tier = 1; tier <= MobConfig.TowerMaxTier; tier++)
        {
            Assert.True(TowerDifficulty.Multiplier(tier) >= TowerDifficulty.Multiplier(tier - 1), $"tier {tier} eases");
            Assert.True(TowerDifficulty.Multiplier(tier) <= MobConfig.TowerDifficultyMaxMultiplier);
            Assert.True(TowerDifficulty.PackSize(tier) >= TowerDifficulty.PackSize(tier - 1));
            Assert.True(TowerDifficulty.PackSize(tier) <= MobConfig.TowerPackMaxSize);
            Assert.True(TowerDifficulty.Scale(10, tier) >= TowerDifficulty.Scale(10, tier - 1));
            Assert.True(TowerDifficulty.Scale(10, tier) >= 10);
        }
        Assert.True(TowerDifficulty.Multiplier(MobConfig.TowerMaxTier) > TowerDifficulty.Multiplier(0));

        // Out of range clamps to the end floors
        Assert.Equal(0, TowerDifficulty.Clamp(-3));
        Assert.Equal(MobConfig.TowerMaxTier, TowerDifficulty.Clamp(MobConfig.TowerMaxTier + 9));
        Assert.Equal(TowerDifficulty.Multiplier(0), TowerDifficulty.Multiplier(-3));
        Assert.Equal(TowerDifficulty.Multiplier(MobConfig.TowerMaxTier), TowerDifficulty.Multiplier(MobConfig.TowerMaxTier + 9));
        Assert.Equal(TowerDifficulty.PackSize(MobConfig.TowerMaxTier), TowerDifficulty.PackSize(MobConfig.TowerMaxTier + 9));
    }

    [Fact]
    public void AHigherTierSkeletonIsTougherAndHitsHarder()
    {
        Skeleton? previous = null;
        for (var tier = 0; tier <= MobConfig.TowerMaxTier; tier++)
        {
            var skeleton = new Skeleton(Type, Vector3.Zero, tier, spawnIn: false);
            Assert.Equal(skeleton.MaxHealth, skeleton.Health);
            if (previous != null)
            {
                Assert.True(skeleton.MaxHealth >= previous.MaxHealth);
                Assert.True(skeleton.MeleeDamage >= previous.MeleeDamage);
                Assert.True(skeleton.ArrowDamage >= previous.ArrowDamage);
            }
            previous = skeleton;
        }

        var ground = new Skeleton(Type, Vector3.Zero, 0, spawnIn: false);
        var top = new Skeleton(Type, Vector3.Zero, MobConfig.TowerMaxTier, spawnIn: false);
        Assert.True(top.MaxHealth > ground.MaxHealth);
        Assert.True(top.MeleeDamage > ground.MeleeDamage);
        Assert.True(top.ArrowDamage > ground.ArrowDamage);
        Assert.Equal(MobConfig.TowerMaxTier, new Skeleton(Type, Vector3.Zero, MobConfig.TowerMaxTier + 5).Tier);
    }

    [Fact]
    public void TheSkeletonShootsOnlyInsideItsBandWithAClearLineAndReleasesOncePerShot()
    {
        var chunks = StoneFloor();
        var band = (MobConfig.SkeletonBowMinRange + MobConfig.SkeletonBowMaxRange) * 0.5f;
        var skeleton = new Skeleton(Type, OnFloor(1.5f, 8.5f), 0, spawnIn: false);
        var target = PlayerAt(1.5f + band, 8.5f);

        var starts = 0;
        var releases = 0;
        var wasShooting = false;
        for (var i = 0; i < (int)(15f / Dt) && releases < 3; i++)
        {
            skeleton.Update(Dt, chunks, target);
            var shooting = skeleton.Animation.IsPlaying(Skeleton.ShootClip);
            Assert.False(skeleton.Animation.IsPlaying(Skeleton.MeleeClip), "raked a target out of reach");
            if (shooting && !wasShooting) starts++;
            wasShooting = shooting;
            if (!skeleton.TryTakeShotRelease(out _, out var velocity)) continue;
            releases++;
            Assert.True(shooting, "the arrow left the bow after the shoot clip had ended");
            Assert.True(velocity.X > 0f, "the arrow flew away from its target");
        }
        Assert.Equal(3, releases);
        Assert.Equal(releases, starts);

        // A wall too tall to jump between them: no shot ever starts
        var walled = StoneFloor();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var y = 1; y <= 4; y++)
        for (var z = 0; z < Chunk.Size; z++)
            walled.Chunks[Vector3.Zero].SetBlockRaw(8, y, z, stone, 0);
        var blind = new Skeleton(Type, OnFloor(2.5f, 8.5f), 0, spawnIn: false);
        var hidden = PlayerAt(14.5f, 8.5f);
        for (var i = 0; i < (int)(6f / Dt); i++)
        {
            blind.Update(Dt, walled, hidden);
            Assert.False(blind.Animation.IsPlaying(Skeleton.ShootClip), $"shot through the wall at frame {i}");
            Assert.False(blind.TryTakeShotRelease(out _, out _));
        }

        // A target in melee reach is raked, never shot
        var close = new Skeleton(Type, OnFloor(6.5f, 8.5f), 0, spawnIn: false);
        var beside = PlayerAt(6.5f + MobConfig.SkeletonMeleeReach * 0.5f, 8.5f);
        var raked = false;
        for (var i = 0; i < (int)(4f / Dt); i++)
        {
            close.Update(Dt, chunks, beside);
            Assert.False(close.Animation.IsPlaying(Skeleton.ShootClip), $"shot a target in reach at frame {i}");
            raked |= close.Animation.IsPlaying(Skeleton.MeleeClip);
        }
        Assert.True(raked, "never raked a target in reach");
    }

    [Fact]
    public void TheMeleeImpactIsTakenOnlyAtItsEventOncePerSwing()
    {
        var chunks = StoneFloor();
        var skeleton = new Skeleton(Type, OnFloor(6.5f, 8.5f), 0, spawnIn: false);
        var target = PlayerAt(6.5f + MobConfig.SkeletonMeleeReach * 0.5f, 8.5f);

        var swings = 0;
        var impacts = 0;
        var wasSwinging = false;
        for (var i = 0; i < (int)(10f / Dt) && impacts < 3; i++)
        {
            skeleton.Update(Dt, chunks, target);
            var swinging = skeleton.Animation.IsPlaying(Skeleton.MeleeClip);
            if (swinging && !wasSwinging) swings++;
            wasSwinging = swinging;
            if (!skeleton.TryTakeMeleeImpact()) continue;
            impacts++;
            Assert.True(swinging, "the rake landed after the melee clip had ended");
            Assert.False(skeleton.TryTakeMeleeImpact(), "one swing gave two impacts");
        }
        Assert.Equal(3, impacts);
        Assert.Equal(impacts, swings);
    }

    [Fact]
    public void TheBowBandStaysInsideItsArrowsLevelRange()
    {
        var levelRange = MobConfig.SkeletonArrowSpeed * MobConfig.SkeletonArrowSpeed / ThrownProjectile.Gravity;
        Assert.True(MobConfig.SkeletonBowMaxRange < levelRange * 0.9f);
        Assert.True(MobConfig.SkeletonBowMinRange < MobConfig.SkeletonBowMaxRange);
        Assert.True(MobConfig.SkeletonMeleeReach < MobConfig.SkeletonBowMinRange);

        var skeleton = new Skeleton(Type, OnFloor(0.5f, 0.5f), 0, spawnIn: false);
        var chest = RealmMob.ChestOf(StubTarget.EyeAt(new Vector3(0.5f + MobConfig.SkeletonBowMaxRange, 1f, 0.5f)));
        Assert.True(GoblinAim.TryLaunchVelocity(skeleton.ShotOrigin, chest, MobConfig.SkeletonArrowSpeed,
            ThrownProjectile.Gravity, out _), "an arrow cannot reach a chest at the band's far edge");
    }

    [Fact]
    public void TheAttackCountsRemainderNamesTheClipItStarted()
    {
        var chunks = StoneFloor();

        // A shot, then a rake once the target steps into reach
        var band = (MobConfig.SkeletonBowMinRange + MobConfig.SkeletonBowMaxRange) * 0.5f;
        var skeleton = new Skeleton(Type, OnFloor(1.5f, 8.5f), 0, spawnIn: false);
        var target = PlayerAt(1.5f + band, 8.5f);
        for (var i = 0; i < (int)(6f / Dt) && !skeleton.Animation.IsPlaying(Skeleton.ShootClip); i++)
            skeleton.Update(Dt, chunks, target);
        Assert.True(skeleton.Animation.IsPlaying(Skeleton.ShootClip), "never shot");
        Assert.Equal(Skeleton.ShootClip, skeleton.AttackClipFor(skeleton.AttackCount));

        var afterShot = skeleton.AttackCount;
        target.Eye = StubTarget.EyeAt(new Vector3(skeleton.Position.X + MobConfig.SkeletonMeleeReach * 0.5f, 1f,
            skeleton.Position.Z));
        for (var i = 0; i < (int)(6f / Dt) && !skeleton.Animation.IsPlaying(Skeleton.MeleeClip); i++)
            skeleton.Update(Dt, chunks, target);
        Assert.True(skeleton.Animation.IsPlaying(Skeleton.MeleeClip), "never raked");
        Assert.True(skeleton.AttackCount > afterShot);
        Assert.Equal(Skeleton.MeleeClip, skeleton.AttackClipFor(skeleton.AttackCount));
    }

    [Fact]
    public void ASkeletonIsUndead()
    {
        Assert.True(new Skeleton(Type, Vector3.Zero, 0, spawnIn: false).IsUndead);
    }
}
