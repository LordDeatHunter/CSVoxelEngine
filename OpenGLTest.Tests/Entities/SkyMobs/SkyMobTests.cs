using System;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.SkyMobs;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen.Sky;
using Xunit;

namespace VoxelEngine.Tests.Entities.SkyMobs;

/// <summary>
/// The sky mobs' own rules, driven headless: both kinds' shipped models satisfy their contracts (a walker's
/// model is no flyer's, and a model that falls short names its file), the zone difficulty never eases with the
/// tier and only a big zone takes the bruiser, a mob of a bigger zone is tougher and the golem outclasses a
/// first-layer hellbrute, an idle galeraptor holds its hover band over the island, its strike and the golem's
/// shockwave land only at their clips' impact on targets in reach, and the golem's ledge caution keeps it on
/// its island.
/// </summary>
public sealed class SkyMobTests
{
    public SkyMobTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;
    private const float FloorTop = 1f;

    private static EntityType Raptor => EntityRegistry.Require(Galeraptor.EntityName);
    private static EntityType Golem => EntityRegistry.Require(Cloudgolem.EntityName);

    private static ChunkManager StoneFloor() => MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);

    private static Vector3 OnFloor(float x, float z, float height) => new(x, FloorTop + height * 0.5f, z);

    private static StubTarget PlayerAt(float x, float z) => new(StubTarget.EyeAt(new Vector3(x, FloorTop, z)));

    [Fact]
    public void ShippedModelsSatisfyTheirContractsWithHeadAndArmsUnderTheTorso()
    {
        Galeraptor.ValidateContent(Raptor);
        Cloudgolem.ValidateContent(Golem);
        foreach (var type in new[] { Raptor, Golem })
        {
            foreach (var child in new[] { "Head", "ArmL", "ArmR" })
                Assert.Equal("Torso", type.Model.Parts[child].Parent);
            Assert.NotNull(type.Model.TexturePixels);
        }

        // A walker's rig (walk, no fly) is not a flyer's, and the error names the file
        var error = Assert.Throws<InvalidOperationException>(() => Galeraptor.ValidateContent(Golem));
        Assert.Contains(Golem.ModelPath, error.Message);
    }

    [Theory]
    [InlineData(Galeraptor.EntityName)]
    [InlineData(Cloudgolem.EntityName)]
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

        Action validate = kind == Galeraptor.EntityName
            ? () => Galeraptor.ValidateContent(type)
            : () => Cloudgolem.ValidateContent(type);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(validate).Message);
    }

    [Fact]
    public void DifficultyNeverEasesWithTheTierAndOnlyABigZoneTakesTheBruiser()
    {
        Assert.Equal(1f, SkyDifficulty.Multiplier(0));
        for (var tier = 1; tier <= SkyZone.BigTier; tier++)
        {
            Assert.True(SkyDifficulty.Multiplier(tier) >= SkyDifficulty.Multiplier(tier - 1));
            Assert.True(SkyDifficulty.Scale(10, tier) >= SkyDifficulty.Scale(10, tier - 1));
            Assert.InRange(SkyDifficulty.PackSize(tier), SkyDifficulty.PackSize(tier - 1), MobConfig.SkyPackMaxSize);
        }
        Assert.True(SkyDifficulty.Multiplier(SkyZone.BigTier) > SkyDifficulty.Multiplier(0));

        for (var tier = 0; tier <= SkyZone.BigTier; tier++)
        {
            Assert.Equal(tier == SkyZone.BigTier, SkyDifficulty.AllowsBruiser(tier));
            for (var roll = 0f; roll < 1f; roll += 0.01f)
                if (!SkyDifficulty.AllowsBruiser(tier))
                    Assert.Equal(Galeraptor.EntityName, SkyDifficulty.KindFor(tier, roll));
        }

        // A big zone rolls both kinds
        Assert.Equal(Cloudgolem.EntityName, SkyDifficulty.KindFor(SkyZone.BigTier, 0f));
        Assert.Equal(Galeraptor.EntityName, SkyDifficulty.KindFor(SkyZone.BigTier, 0.999f));
    }

    [Fact]
    public void ABigZoneMobIsTougherAndTheGolemOutclassesAFirstLayerHellbrute()
    {
        var small = SkyZoneFinder.Center(zone => zone.Tier == 0);
        var big = SkyZoneFinder.Center(zone => zone.Tier == SkyZone.BigTier);
        var band = WorldGenerationConfig.SkyIslandBottomY + 30f;

        var smallRaptor = new Galeraptor(Raptor, new Vector3(small.X, band, small.Y));
        var bigRaptor = new Galeraptor(Raptor, new Vector3(big.X, band, big.Y));
        Assert.True(bigRaptor.Tier > smallRaptor.Tier);
        Assert.True(bigRaptor.MaxHealth > smallRaptor.MaxHealth);
        Assert.Equal(bigRaptor.MaxHealth, bigRaptor.Health);
        Assert.True(bigRaptor.DiveDamage >= smallRaptor.DiveDamage);

        // Even in the smallest zone the golem is tougher and hits harder than a brute on hell's first layer
        var golem = new Cloudgolem(Golem, new Vector3(small.X, band, small.Y));
        var brute = new Hellbrute(EntityRegistry.Require(Hellbrute.EntityName),
            new Vector3(0f, WorldGenerationConfig.HellTopY - 10f, 0f));
        Assert.Equal(0, brute.Layer);
        Assert.True(golem.MaxHealth > brute.MaxHealth);
        Assert.True(golem.SlamDamage > brute.MeleeDamage);
        Assert.True(new Cloudgolem(Golem, new Vector3(big.X, band, big.Y)).MaxHealth > golem.MaxHealth);
    }

    [Fact]
    public void AnIdleGaleraptorHoldsItsHoverBandOverTheIsland()
    {
        var chunks = StoneFloor();
        var raptor = new Galeraptor(Raptor, new Vector3(8.5f, FloorTop + 10f, 8.5f), spawnIn: false);
        var half = new Vector3(raptor.BodyHalfWidth, raptor.BodyHalfHeight, raptor.BodyHalfWidth);
        var slack = MobConfig.GaleraptorBobBlocks + 0.25f;

        for (var i = 0; i < (int)(20f / Dt); i++)
        {
            raptor.Update(Dt, chunks, null);
            Assert.False(BoxSweep.IsColliding(raptor.Position, half, chunks), "the galeraptor entered the island");
            if (i < (int)(8f / Dt)) continue;
            Assert.InRange(raptor.Position.Y - raptor.BodyHalfHeight - FloorTop,
                MobConfig.GaleraptorHoverMinBlocks - slack, MobConfig.GaleraptorHoverMaxBlocks + slack);
        }
        Assert.Equal(raptor.MaxHealth, raptor.Health);
    }

    [Fact]
    public void TheGaleraptorsStrikeLandsOnlyAtItsDiveImpactWhileInReach()
    {
        var chunks = StoneFloor();
        var raptor = new Galeraptor(Raptor, new Vector3(8.5f, FloorTop + 6f, 8.5f), spawnIn: false);
        var target = PlayerAt(8.5f, 8.5f);

        var impacts = 0;
        for (var i = 0; i < (int)(12f / Dt) && impacts < 2; i++)
        {
            raptor.Update(Dt, chunks, target);
            var diving = raptor.IsDiving;
            if (!raptor.TryTakeDiveImpact()) continue;
            impacts++;
            Assert.True(diving, "the strike landed after the dive clip had ended");
            Assert.True(Galeraptor.InDiveReach(raptor.Position, target.Eye), "struck from out of reach");
        }
        Assert.Equal(2, impacts);

        // Through the manager: the strike hurts a target under it, and one that leaves during the stoop takes nothing
        var manager = new SkyMobManager(Raptor, null);
        var drops = new DropManager();
        var under = PlayerAt(8.5f, 8.5f);
        manager.Spawn(Galeraptor.EntityName, new Vector3(8.5f, FloorTop + 6f, 8.5f), spawnIn: false);
        for (var i = 0; i < (int)(8f / Dt) && under.DamageTaken == 0; i++)
            manager.Update(Dt, chunks, drops, [under]);
        Assert.True(under.DamageTaken > 0, "the galeraptor never struck the target under it");

        var dodging = new SkyMobManager(Raptor, null);
        var dodger = PlayerAt(8.5f, 8.5f);
        var stooping = (Galeraptor)dodging.Spawn(Galeraptor.EntityName, new Vector3(8.5f, FloorTop + 6f, 8.5f), spawnIn: false)!;
        for (var i = 0; i < (int)(6f / Dt) && !stooping.IsDiving; i++)
            dodging.Update(Dt, chunks, drops, [dodger]);
        Assert.True(stooping.IsDiving, "the galeraptor never dove at a target under it");
        dodger.Eye += new Vector3(30f, 0f, 0f);
        for (var i = 0; i < (int)(1.2f / Dt); i++)
            dodging.Update(Dt, chunks, drops, [dodger]);
        Assert.Equal(0, dodger.DamageTaken);
    }

    [Fact]
    public void TheGolemsShockwaveLandsAtItsImpactOnEveryTargetInsideItAndNoneBeyond()
    {
        var chunks = StoneFloor();
        var manager = new SkyMobManager(null, Golem);
        var drops = new DropManager();
        var golem = (Cloudgolem)manager.Spawn(Cloudgolem.EntityName, OnFloor(4.5f, 8.5f, MobConfig.CloudgolemHeight),
            spawnIn: false)!;
        const float radius = MobConfig.CloudgolemShockwaveRadius;
        var near = PlayerAt(4.5f + MobConfig.CloudgolemSlamTriggerReach * 0.5f, 8.5f);
        var inside = PlayerAt(4.5f - radius + 0.3f, 8.5f);
        var beyond = PlayerAt(4.5f, 8.5f + radius + 0.5f);
        IMobTarget[] targets = [near, inside, beyond];
        Assert.True(Cloudgolem.InShockwave(golem.Feet, inside.Eye));
        Assert.False(Cloudgolem.InShockwave(golem.Feet, beyond.Eye));

        var impactShare = ImpactShare(Golem, Cloudgolem.SlamClip, Cloudgolem.ImpactEvent);
        var lastProgress = 0f;
        for (var i = 0; i < (int)(4f / Dt) && near.DamageTaken == 0; i++)
        {
            manager.Update(Dt, chunks, drops, targets);
            var slamming = golem.Animation.IsPlaying(Cloudgolem.SlamClip);
            if (near.DamageTaken == 0)
            {
                lastProgress = slamming ? golem.Animation.ActionProgress : 0f;
                continue;
            }
            // The hit landed on the frame the slam crossed its impact event, not before
            Assert.True(slamming && golem.Animation.ActionProgress >= impactShare && lastProgress < impactShare,
                "the shockwave landed away from the slam's impact");
        }

        Assert.True(near.DamageTaken > 0, "the golem never slammed a target in reach");
        Assert.Equal(near.DamageTaken, inside.DamageTaken);
        Assert.Equal(0, beyond.DamageTaken);
    }

    [Fact]
    public void LedgeCautionKeepsTheGolemOnItsIsland()
    {
        // An island: the floor only covers x 0..7 of the chunk, and below it nothing is loaded (a bottomless
        // drop to the cloud sea)
        var chunks = StoneFloor();
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var x = 8; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, 0, 0);

        var golem = new Cloudgolem(Golem, OnFloor(4.5f, 8.5f, MobConfig.CloudgolemHeight), spawnIn: false);
        // Level with the golem past the edge, standing on nothing
        var target = PlayerAt(14.5f, 8.5f);
        for (var i = 0; i < (int)(10f / Dt); i++)
            golem.Update(Dt, chunks, target);

        Assert.True(golem.Position.X > 6f, "the golem never walked toward its target");
        Assert.True(golem.IsGrounded, "the golem walked off its island");
        Assert.True(golem.Feet.Y >= FloorTop - 0.01f);
        Assert.True(golem.Position.X - golem.BodyHalfWidth < 8f, "the golem's body left the island");
    }

    // Where in its clip (0..1) an attack's timeline event sits
    private static float ImpactShare(EntityType type, string clipName, string eventName)
    {
        var clip = EntityContract.RequireClip(type, clipName, clipName);
        foreach (var e in clip.Events)
            if (e.Name == eventName)
                return e.Time / clip.Length;
        throw new InvalidOperationException($"no {eventName} on {clipName}");
    }
}
