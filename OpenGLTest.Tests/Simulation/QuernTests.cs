using System;
using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
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
    public void NonCrushableHasNoRecipe()
    {
        Assert.Null(ContentRegistry.CrushRecipeForInput(Branch));
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
