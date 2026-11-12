using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Entities.TowerMobs;

/// <summary>
/// The skeleton's own rules, driven headless: its shipped model satisfies its contract (and a model that does
/// not names its file), it shoots only inside its bow band with a clear line (and rakes a target
/// in reach instead), an archer hidden behind a pillar walks around it to shoot, each clip's event is taken once per attack, the band stays inside the arrow's range, the
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
    public void TheBonePilesRaiseMoreAndTolerateMoreAsTheFloorRises()
    {
        for (var tier = 1; tier <= MobConfig.TowerMaxTier; tier++)
        {
            Assert.True(TowerDifficulty.PackSize(tier) >= TowerDifficulty.PackSize(tier - 1));
            Assert.True(TowerDifficulty.PileNearLimit(tier) > TowerDifficulty.PileNearLimit(tier - 1));
        }

        // Out of range clamps to the end floors
        Assert.Equal(0, TowerDifficulty.Clamp(-3));
        Assert.Equal(MobConfig.TowerMaxTier, TowerDifficulty.Clamp(MobConfig.TowerMaxTier + 9));
        Assert.Equal(TowerDifficulty.PackSize(0), TowerDifficulty.PackSize(-3));
        Assert.Equal(TowerDifficulty.PackSize(MobConfig.TowerMaxTier), TowerDifficulty.PackSize(MobConfig.TowerMaxTier + 9));
        Assert.Equal(TowerDifficulty.PileNearLimit(0), TowerDifficulty.PileNearLimit(-3));
        Assert.Equal(TowerDifficulty.PileNearLimit(MobConfig.TowerMaxTier),
            TowerDifficulty.PileNearLimit(MobConfig.TowerMaxTier + 9));
    }

    [Fact]
    public void TheSkeletonShootsOnlyInsideItsBandWithAClearLineAndReleasesOncePerShot()
    {
        var chunks = StoneFloor();
        var band = (MobConfig.SkeletonBowMinRange + MobConfig.SkeletonBowMaxRange) * 0.5f;
        var skeleton = new Skeleton(Type, OnFloor(1.5f, 8.5f), spawnIn: false);
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
        var blind = new Skeleton(Type, OnFloor(2.5f, 8.5f), spawnIn: false);
        var hidden = PlayerAt(14.5f, 8.5f);
        for (var i = 0; i < (int)(6f / Dt); i++)
        {
            blind.Update(Dt, walled, hidden);
            Assert.False(blind.Animation.IsPlaying(Skeleton.ShootClip), $"shot through the wall at frame {i}");
            Assert.False(blind.TryTakeShotRelease(out _, out _));
        }

        // A target in melee reach is raked, never shot
        var close = new Skeleton(Type, OnFloor(6.5f, 8.5f), spawnIn: false);
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
        var skeleton = new Skeleton(Type, OnFloor(6.5f, 8.5f), spawnIn: false);
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

        var skeleton = new Skeleton(Type, OnFloor(0.5f, 0.5f), spawnIn: false);
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
        var skeleton = new Skeleton(Type, OnFloor(1.5f, 8.5f), spawnIn: false);
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
        Assert.True(new Skeleton(Type, Vector3.Zero, spawnIn: false).IsUndead);
    }

    private static EntityType WarriorType => EntityRegistry.Require(Skeleton.WarriorEntityName);

    [Fact]
    public void AWarriorNeverDrawsABowAndClosesInToStabHarderThanAnArcherPunches()
    {
        Skeleton.ValidateContent(WarriorType);
        var chunks = StoneFloor();
        var band = (MobConfig.SkeletonBowMinRange + MobConfig.SkeletonBowMaxRange) * 0.5f;
        var warrior = new Skeleton(WarriorType, OnFloor(1.5f, 8.5f), spawnIn: false, SkeletonRole.Warrior);
        var target = PlayerAt(1.5f + band, 8.5f);

        var stabbed = false;
        for (var i = 0; i < (int)(8f / Dt) && !stabbed; i++)
        {
            warrior.Update(Dt, chunks, target);
            Assert.False(warrior.Animation.IsPlaying(Skeleton.ShootClip), $"a warrior drew a bow at frame {i}");
            Assert.False(warrior.TryTakeShotRelease(out _, out _));
            stabbed = warrior.Animation.IsPlaying(Skeleton.MeleeClip);
        }
        Assert.True(stabbed, "the warrior never closed in to stab");

        var archer = new Skeleton(Type, Vector3.Zero, spawnIn: false);
        Assert.True(warrior.MeleeDamage > archer.MeleeDamage);
        Assert.True(warrior.MeleeKnockbackSpeed > archer.MeleeKnockbackSpeed);
    }

    [Fact]
    public void AnArcherBacksAwayFromATargetInsideItsBandButOutOfReach()
    {
        var chunks = StoneFloor();
        var archer = new Skeleton(Type, OnFloor(8.5f, 8.5f), spawnIn: false);
        var near = (MobConfig.SkeletonMeleeReach + MobConfig.SkeletonBowMinRange) * 0.5f;
        var target = PlayerAt(8.5f + near, 8.5f);
        var start = archer.Position.X;

        for (var i = 0; i < (int)(1f / Dt); i++)
            archer.Update(Dt, chunks, target);

        Assert.True(archer.Position.X < start - 0.5f, $"the archer did not back away ({start} -> {archer.Position.X})");
        Assert.False(archer.Animation.IsPlaying(Skeleton.MeleeClip), "punched a target out of reach");
    }

    [Fact]
    public void AnArcherBehindAPillarInItsBandWalksAroundItToAClearLineAndShoots()
    {
        // A pillar too tall to jump, wide enough that walking straight at the target only pushes into it
        var chunks = StoneFloor();
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var y = 1; y <= 4; y++)
        for (var z = 6; z <= 10; z++)
            chunks.Chunks[Vector3.Zero].SetBlockRaw(6, y, z, stone, 0);
        var archer = new Skeleton(Type, OnFloor(2.5f, 8.5f), spawnIn: false);
        var target = PlayerAt(12.5f, 8.5f);
        var chest = RealmMob.ChestOf(target.Eye);
        Assert.True(BlockRaycaster.SegmentHitsBlock(archer.ShotOrigin, chest, chunks), "the pillar does not hide the target");

        var shot = false;
        for (var i = 0; i < (int)(6f / Dt) && !shot; i++)
        {
            archer.Update(Dt, chunks, target);
            shot = archer.Animation.IsPlaying(Skeleton.ShootClip);
        }

        Assert.True(shot, $"the archer never found a line around the pillar (stuck at {archer.Position})");
        Assert.True(MathF.Abs(archer.Position.Z - 8.5f) > 1f, "the archer shot without stepping out from behind the pillar");
        Assert.False(BlockRaycaster.SegmentHitsBlock(archer.ShotOrigin, chest, chunks), "drew its bow without a clear line");
        var dist = new Vector2(target.Eye.X - archer.Position.X, target.Eye.Z - archer.Position.Z).Length();
        Assert.InRange(dist, MobConfig.SkeletonBowMinRange, MobConfig.SkeletonBowMaxRange);
    }

    [Fact]
    public void TheLookByteRoundTripsEveryRoleAndOutfit()
    {
        foreach (var role in Enum.GetValues<SkeletonRole>())
        for (var code = 0; code < SkeletonOutfit.Count; code++)
        {
            var outfit = SkeletonOutfit.FromCode(code);
            Skeleton.UnpackLook(Skeleton.PackLook(role, outfit), out var readRole, out var readOutfit);
            Assert.Equal((role, outfit), (readRole, readOutfit));
        }
    }

    [Fact]
    public void ArmorSoftensEveryBlowMoreWithMoreAndHarderPiecesButNeverToNothing()
    {
        int Blow(int code, int amount) =>
            new Skeleton(Type, Vector3.Zero, spawnIn: false, outfit: SkeletonOutfit.FromCode(code)).ScaleDamage(amount, Vector3.Zero);

        var bare = Blow(0, 40);
        var leatherCap = Blow(1, 40);
        var fullLeather = Blow(7, 40);
        var copperCap = Blow(8, 40);
        Assert.Equal(40, bare);
        Assert.True(leatherCap < bare && fullLeather < leatherCap, $"{bare} {leatherCap} {fullLeather}");
        Assert.True(copperCap < leatherCap, "copper softened a blow less than leather in the same slot");
        for (var code = 0; code < SkeletonOutfit.Count; code++)
            Assert.Equal(1, Blow(code, 1));
    }

    [Fact]
    public void AnArcherRollsAtMostOneLeatherPieceWhileAWarriorMayWearCopper()
    {
        static int Pieces(SkeletonOutfit outfit, SkeletonArmor armor)
        {
            var count = 0;
            for (var slot = 0; slot < SkeletonOutfit.SlotCount; slot++)
                if (outfit.At(slot) == armor) count++;
            return count;
        }

        var warriorCopper = false;
        var warriorLeather = false;
        for (var chance = 0f; chance < 1f; chance += 0.01f)
        for (var pick = 0f; pick < 1f; pick += 0.05f)
        {
            var archer = SkeletonOutfit.Roll(SkeletonRole.Archer, chance, pick);
            Assert.Equal(0, Pieces(archer, SkeletonArmor.Copper));
            Assert.InRange(Pieces(archer, SkeletonArmor.Leather), 0, 1);

            var warrior = SkeletonOutfit.Roll(SkeletonRole.Warrior, chance, pick);
            warriorCopper |= Pieces(warrior, SkeletonArmor.Copper) > 0;
            warriorLeather |= Pieces(warrior, SkeletonArmor.Leather) > 0;
        }
        Assert.True(warriorCopper && warriorLeather, "a warrior never rolled copper or leather");
        // Past every share a roll is bare bones
        Assert.Equal(SkeletonOutfit.None, SkeletonOutfit.Roll(SkeletonRole.Warrior, 0.999f, 0.5f));
        Assert.Equal(SkeletonOutfit.None, SkeletonOutfit.Roll(SkeletonRole.Archer, 0.999f, 0.5f));
    }
}
