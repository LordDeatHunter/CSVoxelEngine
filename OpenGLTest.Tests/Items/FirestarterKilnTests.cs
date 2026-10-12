using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The firestarter's kiln branch: a fueled kiln is a target like a firepit pile, an unfueled one
/// is not a target at all, so striking it spends neither durability nor the use delay.
/// </summary>
public sealed class FirestarterKilnTests
{
    public FirestarterKilnTests() => TestContent.EnsureLoaded();

    private static Item Firestarter()
    {
        var spec = new ItemFactory.ItemSpec(
            ContentRegistry.RequireItem("firestarter").Id, "Firestarter", "t",
            ItemUseAction.IgniteCampfire,
            0, 0, ContentRegistry.RequireBlock("branch_pile").Id,
            1f, 10, 0.5f, 1, 1, 1, ItemAnimationMode.None, 8f,
            Vector3.Zero, Vector3.Zero, 1f, false, 0.5f, 300f, AttackPattern.Punch, 0.5f, 1f, null,
            "firestarter");
        return ItemFactory.Create("firestarter", spec);
    }

    private static StubItemUseWorld WorldAiming(Vector3 cell, byte id)
    {
        var world = new StubItemUseWorld { RaycastHits = true, RaycastHit = cell, RaycastNormal = Vector3.UnitY };
        world.Cells[((int)cell.X, (int)cell.Y, (int)cell.Z)] = (id, 0);
        return world;
    }

    [Fact]
    public void FirestarterLightsAKilnThroughItsWorldHooks()
    {
        var item = Firestarter();
        var cell = new Vector3(4, 5, 6);

        var world = WorldAiming(cell, ContentRegistry.RequireBlock("mud_brick_kiln").Id);
        world.CanIgniteKilnResult = true;

        var result = item.OnUse(new ItemUseContext(world, 5f, 1), null);
        Assert.True(result.Applied);
        Assert.True(result.Punch);
        Assert.Single(world.ToolUsedDelays);
        Assert.Equal(1, world.DamageCalls);
        Assert.Equal(((int)cell.X, (int)cell.Y, (int)cell.Z), Assert.Single(world.IgnitedKilns));
    }

    [Fact]
    public void AStrikeThatFindsNoKilnCostsNothing()
    {
        var item = Firestarter();
        var cell = new Vector3(4, 5, 6);

        var world = WorldAiming(cell, ContentRegistry.RequireBlock("stone").Id);
        world.CanIgniteKilnResult = false;

        var result = item.OnUse(new ItemUseContext(world, 5f, 1), null);
        Assert.False(result.Applied);
        Assert.Empty(world.ToolUsedDelays);
        Assert.Equal(0, world.DamageCalls);
        Assert.Empty(world.IgnitedKilns);
    }
}
