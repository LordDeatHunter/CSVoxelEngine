using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Particles.Heave;
using Xunit;

namespace VoxelEngine.Tests.Particles.Heave;

/// <summary>
/// The Skeleton King's slam heave: only the exposed full-cube ground inside its radius heaves (never a pillar's top under
/// another block), the ring ripples outward and stands highest near the strike, every block settles back, and the world
/// itself never changes.
/// </summary>
public sealed class GroundHeaveTests
{
    public GroundHeaveTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ASlamHeavesTheExposedGroundInItsRadiusRipplingOutAndSettlingWithoutAWrite()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var chunks = MobTestWorld.Floor(stone);
        var chunk = chunks.Chunks[Vector3.Zero];
        // A two-block pillar beside the strike: its column is a wall, not ground
        chunk.SetBlockRaw(8, 1, 11, stone, 0);
        chunk.SetBlockRaw(8, 2, 11, stone, 0);
        var impact = new Vector3(8.5f, 1f, 8.5f);

        var heave = new GroundHeave();
        heave.Raise(chunks, impact);

        var blocks = heave.Blocks.ToList();
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            var reach = new Vector2(block.Center.X - impact.X, block.Center.Z - impact.Z).Length();
            Assert.True(reach <= JuiceConfig.SlamHeaveRadiusBlocks, $"a block {reach} blocks out heaved");
            Assert.Equal(0.5f, block.Center.Y);
            Assert.Equal(stone, block.BlockId);
        }
        Assert.DoesNotContain(blocks, b => (int)b.Center.X == 8 && (int)b.Center.Z == 11);

        // Nearer blocks start no later and rise no lower than farther ones
        var byReach = blocks.OrderBy(b => new Vector2(b.Center.X - impact.X, b.Center.Z - impact.Z).Length()).ToList();
        for (var i = 1; i < byReach.Count; i++)
        {
            Assert.True(byReach[i - 1].Start <= byReach[i].Start + 1e-5f);
            Assert.True(byReach[i - 1].Height >= byReach[i].Height - 1e-5f);
        }

        // The nearest block rises and comes back down; then every block has settled
        var nearest = byReach[0];
        var peak = 0f;
        for (var t = 0f; t < JuiceConfig.SlamHeaveSeconds; t += 0.02f)
        {
            heave.Update(0.02f);
            peak = MathF.Max(peak, heave.LiftOf(nearest));
        }
        Assert.True(peak > nearest.Height * 0.8f, $"the nearest block rose only {peak}");
        for (var t = 0f; t < 3f && heave.Blocks.Count > 0; t += 0.02f)
            heave.Update(0.02f);
        Assert.Empty(heave.Blocks);
        Assert.Equal(0f, heave.LiftOf(nearest));

        // Cosmetic only: the ground under it is unchanged
        Assert.Equal(stone, chunk.GetLocalBlock(8, 0, 8));
        Assert.Equal(BlockType.Air, chunk.GetLocalBlock(8, 1, 8));
    }
}
