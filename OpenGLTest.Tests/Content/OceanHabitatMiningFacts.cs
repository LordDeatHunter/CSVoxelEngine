using System.Collections.Generic;
using VoxelEngine.Content.Loot;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>Registered ocean floor blocks use the ordinary mining gate and drop path.</summary>
public sealed class OceanHabitatMiningFacts
{
    public OceanHabitatMiningFacts() => TestContent.EnsureLoaded();

    [Fact]
    public void FiveOceanBlocksHaveTheirOwnMineAndDropRules()
    {
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe");
        var axe = ContentRegistry.RequireItem("primitive_axe");
        string[] corals = ["coral_amber", "coral_rose", "coral_blue"];
        string[] mosses = ["sea_moss", "glow_moss"];

        foreach (var name in corals)
        {
            var block = ContentRegistry.RequireBlock(name);
            Assert.Equal(4f, Mining.HardnessOf(block.Id, 0));
            Assert.False(Mining.CanMine(block.Id, null));
            Assert.Equal(0f, Mining.HitDamage(block.Id, null));
            Assert.False(Mining.CanMine(block.Id, axe));
            Assert.Equal(0f, Mining.HitDamage(block.Id, axe));
            Assert.True(Mining.CanMine(block.Id, pickaxe));
            Assert.True(Mining.HitDamage(block.Id, pickaxe) > 0f);
            AssertOwnDrop(name, block.Id);
        }

        foreach (var name in mosses)
        {
            var block = ContentRegistry.RequireBlock(name);
            Assert.Equal(2f, Mining.HardnessOf(block.Id, 0));
            Assert.True(Mining.CanMine(block.Id, null));
            Assert.True(Mining.HitDamage(block.Id, null) > 0f);
            AssertOwnDrop(name, block.Id);
        }
    }

    private static void AssertOwnDrop(string name, ushort blockId)
    {
        Assert.Equal(blockId, ContentRegistry.Require($"block/{name}"));
        Assert.IsType<BlockItem>(ItemRegistry.TryGet(blockId));
        var drops = new List<ushort>(1);
        DropManager.RollLoot(blockId, new LootContext(17, -12, 29), drops);
        Assert.Equal([blockId], drops);
    }
}
