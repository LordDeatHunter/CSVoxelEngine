using System;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class QuernTests
{
    public QuernTests() => TestContent.EnsureLoaded();

    private static byte MalachiteChunk => ContentRegistry.RequireItem("malachite_ore").Id;
    private static byte CrushedCopper => ContentRegistry.RequireItem("crushed_copper").Id;
    private static byte Quern => ContentRegistry.RequireBlock("quern").Id;
    private static byte Cobblestone => ContentRegistry.RequireBlock("cobblestone").Id;
    private static byte Branch => ContentRegistry.RequireItem("branch").Id;

    private static (ChunkManager Manager, QuernManager Querns, DropManager Drops) QuernSetup()
    {
        var (manager, center) = MeshTestWorld.WalledArea(Vector3.Zero);
        center.SetBlockRaw(0, 0, 0, Quern, 0);
        var querns = new QuernManager();
        var drops = new DropManager();
        querns.Ensure(0, 0, 0);
        return (manager, querns, drops);
    }

    [Fact]
    public void MalachiteChunkCrushesIntoCrushedCopper()
    {
        var recipe = ContentRegistry.CrushRecipeForInput(MalachiteChunk);
        Assert.NotNull(recipe);
        Assert.Equal(CrushedCopper, recipe.ResultId);
        Assert.Equal(1, recipe.ResultCount);
    }

    [Fact]
    public void NonCrushableHasNoRecipe()
    {
        Assert.Null(ContentRegistry.CrushRecipeForInput(Branch));
    }

    [Fact]
    public void QuernCraftsFromTwentyCobbleAndBranch()
    {
        var recipe = ContentRegistry.RecipeByName("quern");
        Assert.NotNull(recipe);
        Assert.Equal(Quern, recipe.ResultId);
        Assert.Equal(1, recipe.ResultCount);
        Assert.Equal(2, recipe.Ingredients.Length);
        Assert.Contains(recipe.Ingredients, i => i.Id == Cobblestone && i.Count == 20 && !i.IsTag);
        Assert.Contains(recipe.Ingredients, i => i.Id == Branch && i.Count == 1 && !i.IsTag);
    }

    [Fact]
    public void QuernCraftConsumesPools()
    {
        var recipe = ContentRegistry.RecipeByName("quern");
        Assert.NotNull(recipe);
        var inventory = new Inventory();
        var quernBefore = inventory.CountFor(Quern);
        var cobbleBefore = inventory.CountFor(Cobblestone);
        Assert.True(inventory.Add(Cobblestone, 20));
        Assert.True(inventory.Add(Branch, 1));
        Assert.True(inventory.TryCraft(recipe));
        Assert.Equal(quernBefore + 1, inventory.CountFor(Quern));
        Assert.Equal(cobbleBefore, inventory.CountFor(Cobblestone));
        Assert.Equal(0, inventory.CountFor(Branch));
    }

    [Fact]
    public void QuernBlockFlags()
    {
        var quern = ContentRegistry.RequireBlock("quern");
        Assert.Equal(BlockRenderer.Quern, quern.Renderer);
        Assert.True(quern.Solid);
        Assert.True(quern.HasItem);
        Assert.False(quern.BakedModel!.IsFullCube);
        Assert.False(BlockType.IsGreedy(quern.Id));
        Assert.False(BlockType.Occludes(quern.Id));
        Assert.True(quern.PassesLight);
    }

    [Fact]
    public void QuernModelSplitsIntoBaseAndTop()
    {
        var model = ContentRegistry.RequireBlock("quern").BakedModel!;
        // Three parts: base, plate and handle, six quads each.
        Assert.Equal(18, model.Quads.Length);
        Assert.Equal(12, model.Quads.Count(QuernRenderer.IsTopQuad));
        foreach (var quad in model.Quads)
        {
            var below = 0;
            foreach (var position in quad.Positions)
                if (position.Y < QuernRenderer.SplitY)
                    below++;
            Assert.True(below == 0 || below == 4);
        }
    }

    [Fact]
    public void QuernChunkHoldsBaseOnly()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        center.SetBlockRaw(8, 8, 8, Quern, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);
        Assert.Empty(mesh.TranslucentVertices);
        foreach (var vertex in mesh.Vertices)
            Assert.True(vertex.Position.Y - 8f <= 6f / 16f + 1e-4f);
    }

    [Fact]
    public void InsertRefusesNonCrushable()
    {
        var (_, querns, _) = QuernSetup();
        Assert.False(querns.TryInsert(0, 0, 0, Branch));
        Assert.False(querns.StateAt(0, 0, 0)!.HasInput);
    }

    [Fact]
    public void InsertTakesOneUnit()
    {
        var (_, querns, _) = QuernSetup();
        Assert.True(querns.TryInsert(0, 0, 0, MalachiteChunk));
        Assert.Equal(MalachiteChunk, querns.StateAt(0, 0, 0)!.InputId);
        Assert.False(querns.TryInsert(0, 0, 0, MalachiteChunk));
    }

    [Fact]
    public void SpinCompletesOneRotationInTwoSeconds()
    {
        var (manager, querns, drops) = QuernSetup();
        querns.StartSpin(0, 0, 0);
        Assert.True(querns.IsSpinning(0, 0, 0));
        querns.Update(1f, manager, drops);
        Assert.True(querns.IsSpinning(0, 0, 0));
        Assert.Equal(MathF.PI, querns.StateAt(0, 0, 0)!.Angle, precision: 4);
        querns.Update(1f, manager, drops);
        Assert.False(querns.IsSpinning(0, 0, 0));
    }

    [Fact]
    public void StartSpinWhileSpinningKeepsOneRotation()
    {
        var (manager, querns, drops) = QuernSetup();
        querns.StartSpin(0, 0, 0);
        querns.StartSpin(0, 0, 0);
        querns.Update(QuernManager.FullRotationSeconds, manager, drops);
        Assert.False(querns.IsSpinning(0, 0, 0));
    }

    [Fact]
    public void CompletedSpinKeepsTopVisible()
    {
        var (manager, querns, drops) = QuernSetup();
        querns.StartSpin(0, 0, 0);
        querns.Update(QuernManager.FullRotationSeconds, manager, drops);
        Assert.False(querns.IsSpinning(0, 0, 0));
        Assert.NotNull(querns.StateAt(0, 0, 0));
        Assert.Equal(0f, querns.StateAt(0, 0, 0)!.Angle, precision: 5);
        Assert.Single(querns.QuernView());
    }

    [Fact]
    public void FullRotationEventuallyCrushes()
    {
        var (manager, querns, drops) = QuernSetup();
        Assert.True(querns.TryInsert(0, 0, 0, MalachiteChunk));
        for (var i = 0; i < 40 && querns.StateAt(0, 0, 0)?.HasInput == true; i++)
        {
            querns.StartSpin(0, 0, 0);
            querns.Update(QuernManager.FullRotationSeconds, manager, drops);
        }
        Assert.False(querns.StateAt(0, 0, 0)?.HasInput == true);
        Assert.Contains(drops.Drops, d => d.BlockId == CrushedCopper);
        Assert.NotNull(querns.StateAt(0, 0, 0));
        Assert.False(querns.StateAt(0, 0, 0)!.HasInput);
        Assert.Single(querns.QuernView());
    }

    [Fact]
    public void BrokenQuernDropsWaitingInput()
    {
        var (_, querns, drops) = QuernSetup();
        Assert.True(querns.TryInsert(0, 0, 0, MalachiteChunk));
        querns.NotifyBlockRemoved(0, 0, 0, drops);
        Assert.Null(querns.StateAt(0, 0, 0));
        Assert.Contains(drops.Drops, d => d.BlockId == MalachiteChunk);
    }
}
