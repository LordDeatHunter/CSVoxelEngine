using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The ash fertilizer (<see cref="ItemUseAction.GrowTree"/> / <see cref="GrowTreeItem"/>): the
/// aimed cell's wood or leaves name the species, the use spends one unit, and anything that is
/// not a tree refuses it.
/// </summary>
public sealed class GrowTreeItemTests
{
    public GrowTreeItemTests() => TestContent.EnsureLoaded();

    private static byte Ash => ContentRegistry.RequireItem("ash").Id;

    [Fact]
    public void AshGrowsTheAimedTreeAndSpendsOne()
    {
        var world = new StubItemUseWorld { RaycastHits = true, RaycastHit = new Vector3(4, 5, 6) };
        world.TreeCells.Add((4, 5, 6));
        var ash = ContentRegistry.RequireItem("ash");
        var result = ash.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.True(result.Applied);
        Assert.Equal(1, world.Consumed);
        Assert.Equal((4, 5, 6), Assert.Single(world.GrowTreeCalls));
    }

    [Fact]
    public void AshOnAnythingElseIsRefusedAndSpendsNothing()
    {
        var world = new StubItemUseWorld { RaycastHits = true, RaycastHit = new Vector3(4, 5, 6) };
        var ash = ContentRegistry.RequireItem("ash");
        var result = ash.OnUse(new ItemUseContext(world, 4f, 0), null);

        Assert.False(result.Applied);
        Assert.Equal(0, world.Consumed);
        // The aim still reached the world: the refusal came from the cell holding no tree
        Assert.Single(world.GrowTreeCalls);

        // A miss refuses before asking at all
        world.RaycastHits = false;
        Assert.False(ash.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.Single(world.GrowTreeCalls);
    }

    [Fact]
    public void EverySpeciesBlockResolvesToItsSpeciesOrItsWoodsLeafyOwner()
    {
        foreach (var species in TreeSpeciesRegistry.All)
        {
            // Leaves are never shared, so they always name their species
            if (species.LeavesId != 0)
                Assert.Equal(species.Name, TreeSpeciesRegistry.ForBlock(species.LeavesId)?.Name);

            // A wood resolves to its species, or to the leafy species whose wood it borrows (the
            // dead tree and the oak): growing the column's tree never turns an oak into a snag
            var owner = TreeSpeciesRegistry.ForBlock(species.WoodId);
            Assert.NotNull(owner);
            Assert.True(owner == species || (owner.HasLeaves && owner.WoodId == species.WoodId),
                $"'{species.Name}' wood resolves to '{owner.Name}'");
        }

        Assert.Null(TreeSpeciesRegistry.ForBlock(ContentRegistry.RequireBlock("stone").Id));
        Assert.Null(TreeSpeciesRegistry.ForBlock(BlockType.Air));
    }
}
