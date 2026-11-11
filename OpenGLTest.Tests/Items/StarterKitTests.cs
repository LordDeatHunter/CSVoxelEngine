using System;
using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The starter kit (<see cref="StarterKit"/>): only a player with no saved known-part list (a newcomer, or a
/// save from before spell parts could be learned) needs it, and it is one hint scroll on the hotbar whose
/// parts, once learned, make a spell a shipped staff can bind.
/// </summary>
public sealed class StarterKitTests
{
    public StarterKitTests() => TestContent.EnsureLoaded();

    [Fact]
    public void OnlyANewOrPreKnowledgeSaveNeedsTheStarterKit()
    {
        Assert.True(StarterKit.NeededBy(null));
        Assert.True(StarterKit.NeededBy(new PlayerSaveData()));

        var preKnowledge = Inventory.Empty().Export();
        preKnowledge.KnownParts = null;
        Assert.True(StarterKit.NeededBy(new PlayerSaveData { Inventory = preKnowledge }));

        // A returning player who knows nothing (the scroll used up or never read) gets no second one
        Assert.False(StarterKit.NeededBy(new PlayerSaveData { Inventory = Inventory.Empty().Export() }));
    }

    [Fact]
    public void TheStarterKitIsOneScrollTeachingACastableRecipe()
    {
        var inventory = Inventory.Empty();
        StarterKit.GiveTo(inventory);

        var owned = Enum.GetValues<InventoryCategory>().Sum(inventory.UsedOf);
        Assert.Equal(1, owned);
        var scroll = Assert.IsType<LearnPartsItem>(inventory.HeldItem);
        Assert.Equal(0, inventory.KnownPartCount);

        var parts = scroll.Teaches.Select(k => SpellPartRegistry.TryGetByKey(k)!).ToList();
        Assert.Contains(ItemRegistry.All.OfType<StaffItem>(),
            staff => new SpellRecipe(parts).Compile(staff.AllowedTags).IsValid);

        var world = new StubItemUseWorld { Inventory = inventory };
        Assert.True(scroll.OnUse(new ItemUseContext(world, 4f, 0), null).Applied);
        Assert.All(scroll.Teaches, k => Assert.True(inventory.KnowsPart(k)));
    }
}
