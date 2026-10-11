using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A settling sand cube's dust: the rule sizing it by the fall (<see cref="LandingDust.FallingBlockCount"/>),
/// the landings <see cref="FallingBlockManager"/> reports, and the skirt of chips
/// <see cref="GroundDust.LandBlock"/> throws.
/// </summary>
[Collection("LOD column caps")]
public sealed class FallingBlockDustTests
{
    public FallingBlockDustTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ALipSlideRaisesNoDustAndTheDustNeverShrinksAsTheFallGrows()
    {
        Assert.Equal(0, LandingDust.FallingBlockCount(0f));
        Assert.Equal(0, LandingDust.FallingBlockCount(WorldGenerationConfig.FallingBlockDustMinFallBlocks * 0.5f));
        Assert.True(LandingDust.FallingBlockCount(WorldGenerationConfig.FallingBlockDustMinFallBlocks) > 0);

        var lastCount = 0;
        var lastSpeed = 0f;
        for (var fall = 0f; fall <= WorldGenerationConfig.FallingBlockDustFullFallBlocks * 2f; fall += 0.25f)
        {
            var count = LandingDust.FallingBlockCount(fall);
            var speed = LandingDust.FallingBlockSpeed(fall);
            Assert.True(count >= lastCount, $"fewer chips at a {fall}-block fall");
            Assert.True(speed >= lastSpeed, $"slower chips at a {fall}-block fall");
            lastCount = count;
            lastSpeed = speed;
        }
    }

    [Fact]
    public void ASettledCubeReportsItsLandingAndALongerDropReportsALongerFall()
    {
        var low = DropAndCollect(3);
        var high = DropAndCollect(8);

        var lowLanding = Assert.Single(low);
        var highLanding = Assert.Single(high);
        Assert.Equal(new Vector3(1, 1, 1), lowLanding.Cell);
        Assert.False(lowLanding.Submerged);
        Assert.True(highLanding.FallBlocks > lowLanding.FallBlocks);
    }

    [Fact]
    public void ASubmergedLandingRaisesNoDust()
    {
        var debris = new BlockDebrisSystem();
        var sand = ContentRegistry.RequireBlock("sand").Id;
        Assert.Equal(0, GroundDust.LandBlock(new Vector3(4, 4, 4), sand, 6f, submerged: true, debris));
        Assert.Empty(debris.Debris);
    }

    [Fact]
    public void TheSkirtStartsOutsideTheSettledBlock()
    {
        var debris = new BlockDebrisSystem();
        var sand = ContentRegistry.RequireBlock("sand").Id;
        var cell = new Vector3(4, 4, 4);
        var spawned = GroundDust.LandBlock(cell, sand, 6f, submerged: false, debris, random: new System.Random(7));

        Assert.True(spawned > 0);
        Assert.Equal(spawned, debris.Debris.Count);
        foreach (var chip in debris.Debris)
        {
            var half = chip.Size * 0.5f;
            var insideX = chip.Position.X + half > cell.X && chip.Position.X - half < cell.X + 1f;
            var insideZ = chip.Position.Z + half > cell.Z && chip.Position.Z - half < cell.Z + 1f;
            Assert.False(insideX && insideZ, $"a chip at {chip.Position} starts inside the block");
        }
    }

    // Drops one sand cube from height y onto a stone floor at y 0 and returns every landing reported
    private static List<FallingBlockLanding> DropAndCollect(int y)
    {
        var chunks = new ChunkManager(null!);
        chunks.Chunks[Vector3.Zero] = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunks.Chunks[Vector3.Zero].IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var sand = ContentRegistry.RequireBlock("sand").Id;
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var x = 0; x < 3; x++)
        for (var z = 0; z < 3; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        chunk.SetBlockRaw(1, y, 1, sand, 0);

        var falling = new FallingBlockManager();
        var fluids = new FluidManager();
        var drops = new DropManager();
        Assert.True(falling.TryTopple(chunks, new LodManager(null!), fluids, 1, y, 1));
        var landings = new List<FallingBlockLanding>();
        for (var i = 0; i < 600 && falling.Falling.Count > 0; i++)
        {
            falling.Update(1f / 60f, chunks, drops, fluids);
            landings.AddRange(falling.Landings);
        }
        return landings;
    }
}
