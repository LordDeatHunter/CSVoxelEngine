using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The landing dust: the rule that sizes a puff from the fall (<see cref="LandingDust"/>), the
/// surface it is taken from (<see cref="GroundDust.TryFindSurface"/>) and the chips it spawns, plus
/// the landings a falling mob notes for it.
/// </summary>
public sealed class LandingDustTests
{
    public LandingDustTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    private static byte Id(string block) => ContentRegistry.RequireBlock(block).Id;

    // Feet of a body standing on MobTestWorld.Floor's y = 0 layer.
    private static readonly Vector3 FloorFeet = new(8.5f, 1f, 8.5f);

    [Fact]
    public void AFallUnderTheMinimumKicksUpNothing()
    {
        for (var fall = 0f; fall < WorldGenerationConfig.LandingDustMinFallBlocks; fall += 0.05f)
        {
            Assert.Equal(0, LandingDust.Count(fall));
            Assert.Equal(0f, LandingDust.Speed(fall));
        }

        var chunks = MobTestWorld.Floor(Id("sand"));
        var debris = new BlockDebrisSystem();
        var shortFall = WorldGenerationConfig.LandingDustMinFallBlocks * 0.9f;
        Assert.Equal(0, GroundDust.Land(FloorFeet, shortFall, 0.3f, chunks, debris, new Random(1)));
        Assert.Empty(debris.Debris);
    }

    [Fact]
    public void CountAndSpeedNeverDecreaseAsTheFallGrows()
    {
        var lastCount = 0;
        var lastSpeed = 0f;
        for (var fall = 0f; fall <= 80f; fall += 0.05f)
        {
            var count = LandingDust.Count(fall);
            var speed = LandingDust.Speed(fall);
            Assert.True(count >= lastCount, $"count fell to {count} at a fall of {fall}");
            Assert.True(speed >= lastSpeed, $"speed fell to {speed} at a fall of {fall}");
            lastCount = count;
            lastSpeed = speed;
        }

        // Anti-vacuity: a landing at the minimum already puffs, and a long drop throws more, faster dust
        var atMinimum = WorldGenerationConfig.LandingDustMinFallBlocks;
        Assert.True(LandingDust.Count(atMinimum) > 0);
        Assert.True(LandingDust.Count(atMinimum * 4f) > LandingDust.Count(atMinimum));
        Assert.True(LandingDust.Speed(atMinimum * 4f) > LandingDust.Speed(atMinimum));
    }

    [Fact]
    public void CountAndSpeedStayUnderTheirCaps()
    {
        foreach (var fall in new[] { 5f, 20f, 100f, 1000f, float.MaxValue })
        {
            Assert.InRange(LandingDust.Count(fall), 0, WorldGenerationConfig.LandingDustMaxCount);
            Assert.InRange(LandingDust.Speed(fall), 0f, WorldGenerationConfig.LandingDustMaxSpeed);
        }

        // A non-number fall (a broken tracker) is no fall at all
        Assert.Equal(0, LandingDust.Count(float.NaN));
    }

    [Fact]
    public void ALandingPuffsChipsOfTheBlockLandedOn()
    {
        var chunks = MobTestWorld.Floor(Id("sand"));
        var debris = new BlockDebrisSystem();
        var fall = WorldGenerationConfig.LandingDustMinFallBlocks * 3f;

        var spawned = GroundDust.Land(FloorFeet, fall, 0.3f, chunks, debris, new Random(2));

        Assert.Equal(LandingDust.Count(fall), spawned);
        Assert.Equal(spawned, debris.Debris.Count);
        Assert.All(debris.Debris, chip => Assert.Equal(Id("sand"), chip.BlockId));
        // They start at the feet, above the ground they came off
        Assert.All(debris.Debris, chip => Assert.True(chip.Position.Y > FloorFeet.Y));
    }

    [Fact]
    public void GrassDustWearsTheBiomeTintOfTheCellLandedOn()
    {
        var chunks = MobTestWorld.Floor(Id("grass"));
        var debris = new BlockDebrisSystem();

        GroundDust.Land(FloorFeet, 10f, 0.3f, chunks, debris, new Random(3));

        var expected = BiomeTint.CellTint(BiomeTintPalette.Grass, (int)FloorFeet.X, (int)FloorFeet.Z);
        Assert.NotEmpty(debris.Debris);
        Assert.All(debris.Debris, chip => Assert.Equal(expected, chip.Tint));
        Assert.NotEqual(Vector3.One, expected);
    }

    [Fact]
    public void DustChipsWearTheSurfaceOfTheBlockAndAGrassChipIsTintedAllRound()
    {
        var chunks = MobTestWorld.Floor(Id("grass"));
        var debris = new BlockDebrisSystem();
        GroundDust.Land(FloorFeet, 10f, 0.3f, chunks, debris, new Random(6));
        Assert.All(debris.Debris, chip => Assert.Equal(DebrisLook.Surface, chip.Look));

        // Every face of a surface chip is the block's top: grass's tinted top, never the dirt sides
        var grass = ContentRegistry.RequireBlock("grass");
        var (verts, _) = BlockCubeCache.BuildSurfaceCube(grass);
        Assert.NotEmpty(verts);
        Assert.All(verts, v => Assert.True(v.IsBiomeTinted));
        Assert.All(verts, v => Assert.Equal(grass.BiomeTint!.Tinted[4], v.Layer));
        for (var face = 0; face < 6; face++)
            Assert.Contains(verts, v => (v.Normal & 7) == face);

        // An untinted block's chip is its plain top all round
        var sandstone = ContentRegistry.RequireBlock("sandstone");
        var (stoneVerts, _) = BlockCubeCache.BuildSurfaceCube(sandstone);
        Assert.All(stoneVerts, v => Assert.False(v.IsBiomeTinted));
        Assert.All(stoneVerts, v => Assert.Equal(sandstone.Top, v.Layer));
    }

    [Fact]
    public void TheChipsFlyOutwardAndFasterFromALongerFall()
    {
        float MeanSpread(float fall)
        {
            var chunks = MobTestWorld.Floor(Id("stone"));
            var debris = new BlockDebrisSystem();
            GroundDust.Land(FloorFeet, fall, 0.3f, chunks, debris, new Random(4));
            for (var i = 0; i < 12; i++)
                debris.Update(Dt, chunks);
            var sum = 0f;
            foreach (var chip in debris.Debris)
                sum += new Vector2(chip.Position.X - FloorFeet.X, chip.Position.Z - FloorFeet.Z).Length();
            return sum / debris.Debris.Count;
        }

        var shortSpread = MeanSpread(WorldGenerationConfig.LandingDustMinFallBlocks);
        Assert.True(shortSpread > 0.05f, $"chips stayed at the feet: {shortSpread}");
        Assert.True(MeanSpread(WorldGenerationConfig.LandingDustFullFallBlocks) > shortSpread);
    }

    [Fact]
    public void ASnowDriftTheFeetStandInIsTheSurfaceAndAPlantIsLookedThrough()
    {
        var chunks = MobTestWorld.Floor(Id("stone"));
        var chunk = chunks.Chunks[Vector3.Zero];

        chunk.SetBlockRaw(8, 1, 8, Id("snow"), 3);
        Assert.True(GroundDust.TryFindSurface(chunks, FloorFeet, 0.3f, out var cell, out var id));
        Assert.Equal(Id("snow"), id);
        Assert.Equal(new Vector3(8, 1, 8), cell);

        chunk.SetBlockRaw(8, 1, 8, Id("tall_grass"), 0);
        Assert.True(GroundDust.TryFindSurface(chunks, FloorFeet, 0.3f, out cell, out id));
        Assert.Equal(Id("stone"), id);
        Assert.Equal(new Vector3(8, 0, 8), cell);
    }

    [Fact]
    public void ABodyOverALedgeStillFindsTheLedgeAndNothingUnderfootFindsNothing()
    {
        var chunks = MobTestWorld.Floor(Id("stone"));
        var chunk = chunks.Chunks[Vector3.Zero];
        chunk.SetBlockRaw(8, 0, 8, BlockType.Air, 0);

        // The centre hangs over the hole, a corner of the body is still over the floor
        Assert.True(GroundDust.TryFindSurface(chunks, new Vector3(8.15f, 1f, 8.5f), 0.3f, out var cell, out _));
        Assert.Equal(7f, cell.X);

        // High in the air nothing is underfoot
        Assert.False(GroundDust.TryFindSurface(chunks, new Vector3(8.5f, 9f, 8.5f), 0.3f, out _, out _));
    }

    [Fact]
    public void AGoblinNotesTheLandingOfItsFallOnceAndStandingStillRaisesNoDust()
    {
        var chunks = MobTestWorld.Floor(Id("stone"));
        const float drop = 9f;
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 1f + drop + Goblin.Height * 0.5f, 8.5f), ammo: 0);

        for (var i = 0; i < 600 && !goblin.IsGrounded; i++)
            goblin.Update(Dt, chunks, null);
        Assert.True(goblin.IsGrounded);

        Assert.True(goblin.TryTakeLanding(out var impactSpeed, out var fallBlocks));
        Assert.True(impactSpeed > 0f);
        Assert.InRange(fallBlocks, drop - 1f, drop + 1.5f);
        Assert.True(LandingDust.Count(fallBlocks) > 0);
        Assert.False(goblin.TryTakeLanding(out _, out _));

        // Standing, the body only ever settles the hair it rests above the floor: whatever it notes
        // is far too small a fall to raise dust
        for (var i = 0; i < 120; i++)
        {
            goblin.Update(Dt, chunks, null);
            if (goblin.TryTakeLanding(out _, out var settle))
                Assert.Equal(0, LandingDust.Count(settle));
        }
    }

    [Fact]
    public void ASlimeHopSplatsItsOwnGooAndNeverTheGroundsDust()
    {
        var chunks = MobTestWorld.Floor(Id("sand"));
        var debris = new BlockDebrisSystem();
        var dust = new GroundDust();
        var type = EntityRegistry.Require("slime");
        var slime = new Slime(type, new Vector3(2.5f, 1f + Slime.HalfExtent, 2.5f));
        var player = new Vector3(8.5f, 1.62f, 2.5f);
        Mob[] mobs = [slime];

        var hopped = false;
        for (var i = 0; i < 600 && debris.Debris.Count == 0; i++)
        {
            slime.Update(Dt, chunks, player);
            hopped |= slime.IsAirborne && slime.Position.Y > 1.5f + Slime.HalfExtent;
            dust.TakeMobLandings(mobs, chunks, debris, new Random(7));
        }

        // An ordinary hop is far under the ground dust's minimum fall, yet it splats
        Assert.True(hopped);
        Assert.NotEmpty(debris.Debris);
        var expected = BiomeTint.VertexTint(LandingDust.SkinColour(type.Model, WorldGenerationConfig.SlimeSplatFallbackColor));
        Assert.All(debris.Debris, chip =>
        {
            Assert.Equal(DebrisLook.Colour, chip.Look);
            Assert.Equal(BlockType.Air, chip.BlockId);
            Assert.Equal(expected, chip.Tint);
        });
    }

    [Fact]
    public void ALargerSlimeLandingFromTheSameDropSplatsMoreAndBiggerGoo()
    {
        var chunks = MobTestWorld.Floor(Id("sand"));
        var type = EntityRegistry.Require("slime");
        var player = new Vector3(40.5f, 1.62f, 2.5f);

        var small = DropAndSplat(SlimeSize.Min);
        var large = DropAndSplat(SlimeSize.Max);

        Assert.NotEmpty(small.Debris);
        Assert.True(large.Debris.Count > small.Debris.Count);
        Assert.True(MeanSize(large) > MeanSize(small));

        // Feet four blocks over the floor whatever the body, so both land at the same impact speed
        BlockDebrisSystem DropAndSplat(int size)
        {
            var debris = new BlockDebrisSystem();
            var dust = new GroundDust();
            var slime = new Slime(type, new Vector3(2.5f, 5f + Slime.HalfExtentFor(size), 2.5f), size);
            Mob[] mobs = [slime];
            for (var i = 0; i < 600 && debris.Debris.Count == 0; i++)
            {
                slime.Update(Dt, chunks, player);
                dust.TakeMobLandings(mobs, chunks, debris, new Random(3));
            }
            return debris;
        }

        static float MeanSize(BlockDebrisSystem system)
        {
            var sum = 0f;
            foreach (var chip in system.Debris) sum += chip.Size;
            return sum / system.Debris.Count;
        }
    }

    [Fact]
    public void TheSplatGrowsWithTheImpactNeverUnderTheMinimumAndABiggerSlimeSplatsMore()
    {
        for (var speed = 0f; speed < WorldGenerationConfig.SlimeSplatMinImpact; speed += 0.05f)
        {
            Assert.Equal(0, LandingDust.SplatCount(speed));
            Assert.Equal(0f, LandingDust.SplatSpeed(speed));
        }

        var lastCount = 0;
        var lastSpeed = 0f;
        for (var speed = 0f; speed <= 60f; speed += 0.05f)
        {
            var count = LandingDust.SplatCount(speed);
            var fling = LandingDust.SplatSpeed(speed);
            Assert.True(count >= lastCount, $"count fell to {count} at {speed} b/s");
            Assert.True(fling >= lastSpeed, $"fling fell to {fling} at {speed} b/s");
            Assert.InRange(count, 0, WorldGenerationConfig.SlimeSplatMaxCount);
            Assert.InRange(fling, 0f, WorldGenerationConfig.SlimeSplatMaxLaunch);
            Assert.True(LandingDust.SplatCount(speed, 2f) >= count);
            Assert.True(LandingDust.SplatSpeed(speed, 2f) >= fling);
            lastCount = count;
            lastSpeed = fling;
        }

        var hard = WorldGenerationConfig.SlimeSplatFullImpact;
        Assert.True(LandingDust.SplatCount(hard) > LandingDust.SplatCount(WorldGenerationConfig.SlimeSplatMinImpact));
        Assert.True(LandingDust.SplatCount(hard, 2f) > LandingDust.SplatCount(hard));

        // The scale reaches the spawned chips too: a bigger slime's goo is bigger
        var small = new BlockDebrisSystem();
        var big = new BlockDebrisSystem();
        GroundDust.Splat(FloorFeet, hard, Vector3.One, 0.36f, small, scale: 1f, new Random(8));
        GroundDust.Splat(FloorFeet, hard, Vector3.One, 0.36f, big, scale: 2f, new Random(8));
        Assert.True(big.Debris.Count > small.Debris.Count);
        Assert.True(MeanSize(big) > MeanSize(small));

        static float MeanSize(BlockDebrisSystem system)
        {
            var sum = 0f;
            foreach (var chip in system.Debris) sum += chip.Size;
            return sum / system.Debris.Count;
        }
    }

    [Fact]
    public void TheSplatColourIsTheMeanOfTheOpaqueSkin()
    {
        var fallback = new Vector3(0.1f, 0.2f, 0.3f);
        Assert.Equal(fallback, LandingDust.SkinColour(new BbmodelData(), fallback));

        // Two opaque greens average; a transparent red texel never counts
        var model = new BbmodelData
        {
            TexturePixels = [0, 200, 0, 255, 0, 100, 50, 255, 255, 0, 0, 0],
            TextureWidth = 3,
            TextureHeight = 1,
        };
        var colour = LandingDust.SkinColour(model, fallback);
        Assert.Equal(0f, colour.X, 4);
        Assert.Equal(150f / 255f, colour.Y, 4);
        Assert.Equal(25f / 255f, colour.Z, 4);

        var clear = new BbmodelData { TexturePixels = [255, 0, 0, 0], TextureWidth = 1, TextureHeight = 1 };
        Assert.Equal(fallback, LandingDust.SkinColour(clear, fallback));
    }

    [Fact]
    public void AColourChipIsOneTintedTileOnEveryFace()
    {
        const ushort tile = 3;
        var (verts, _) = BlockCubeCache.BuildColourChip(tile);
        Assert.All(verts, v => Assert.True(v.IsBiomeTinted));
        Assert.All(verts, v => Assert.Equal(tile, v.Layer));
        for (var face = 0; face < 6; face++)
            Assert.Contains(verts, v => (v.Normal & 7) == face);
    }

    [Fact]
    public void AMobLandingFromHighRaisesItsDustThroughGroundDust()
    {
        var chunks = MobTestWorld.Floor(Id("gravel"));
        var debris = new BlockDebrisSystem();
        var dust = new GroundDust();
        var goblin = new Goblin(EntityRegistry.Require("goblin"), new Vector3(8.5f, 11f + Goblin.Height * 0.5f, 8.5f), ammo: 0);
        Mob[] mobs = [goblin];

        for (var i = 0; i < 600 && debris.Debris.Count == 0; i++)
        {
            goblin.Update(Dt, chunks, null);
            dust.TakeMobLandings(mobs, chunks, debris, new Random(5));
        }

        Assert.NotEmpty(debris.Debris);
        Assert.All(debris.Debris, chip => Assert.Equal(Id("gravel"), chip.BlockId));
    }
}
