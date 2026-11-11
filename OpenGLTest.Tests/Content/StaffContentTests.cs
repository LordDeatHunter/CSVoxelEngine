using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>The staff JSON rules and the shipped staffs: shape validation, the Tools pool, tiers and recipes.</summary>
public class StaffContentTests
{
    private const string Path = "assets/data/items/test_staff.json";

    public StaffContentTests() => TestContent.EnsureLoaded();

    private static ItemDefinition StaffDefinition(int? slots = 2, int? tier = 1, string[]? tags = null) =>
        new() { Texture = "t", Staff = new ItemDefinition.StaffDefinition { Slots = slots, Tier = tier, Tags = tags ?? ["basic"] } };

    private static IEnumerable<ItemDefinition> BadStaffs()
    {
        yield return StaffDefinition(slots: 0);
        yield return StaffDefinition(slots: SpellConfig.StaffMaxSlots + 1);
        yield return StaffDefinition(slots: null);
        yield return StaffDefinition(tier: 0);
        yield return StaffDefinition(tier: null);
        yield return StaffDefinition(tags: []);
        yield return StaffDefinition(tags: ["basic", " "]);
        yield return StaffDefinition(tags: ["basic", "basic"]);

        var crucible = StaffDefinition();
        crucible.Crucible = new ItemDefinition.CrucibleDefinition { CapacityMl = 400 };
        yield return crucible;

        var use = StaffDefinition();
        use.Use = new ItemDefinition.UseDefinition { Action = "heal" };
        yield return use;
    }

    [Fact]
    public void ParseStaffRefusesABadStaffNamingTheFile()
    {
        foreach (var definition in BadStaffs())
        {
            var error = Assert.Throws<InvalidOperationException>(() => ItemLoader.ParseStaff(definition, Path));
            Assert.Contains(Path, error.Message);
        }
    }

    [Fact]
    public void ParseStaffKeepsSlotsTierAndTags()
    {
        Assert.Null(ItemLoader.ParseStaff(new ItemDefinition { Texture = "t" }, Path));

        var parsed = ItemLoader.ParseStaff(StaffDefinition(slots: 3, tier: 2, tags: ["basic", "advanced"]), Path);
        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.Value.Slots);
        Assert.Equal(2, parsed.Value.Tier);
        Assert.True(parsed.Value.Tags.SetEquals(["basic", "advanced"]));
    }

    [Fact]
    public void AStaffSpecBuildsAToolsPoolStaff()
    {
        var tags = new HashSet<string>(StringComparer.Ordinal) { "basic" };
        var item = ItemFactory.Create("x", Spec(slots: 3, tier: 1, tags));

        var staff = Assert.IsType<StaffItem>(item);
        Assert.Equal(InventoryCategory.Tools, staff.StorageCategory);
        Assert.Equal(3, staff.Slots);
        Assert.True(staff.AllowedTags.SetEquals(tags));
    }

    [Fact]
    public void EveryStaffIsCraftedNeverLootOnly()
    {
        var staffs = ItemRegistry.All.OfType<StaffItem>().ToList();
        Assert.True(staffs.Count >= 2);
        foreach (var staff in staffs)
            Assert.Contains(ContentRegistry.CraftingRecipes, recipe => recipe.ResultId == staff.Id);
    }

    [Fact]
    public void AHigherTierStaffHasMoreSlotsAndAllowsEverythingALowerTierDoes()
    {
        var staffs = ItemRegistry.All.OfType<StaffItem>().ToList();
        Assert.Contains(staffs, a => staffs.Any(b => a.Tier < b.Tier));
        foreach (var a in staffs)
        foreach (var b in staffs.Where(b => a.Tier < b.Tier))
        {
            Assert.True(b.Slots > a.Slots, $"{b.Name} (tier {b.Tier}) has no more slots than {a.Name} (tier {a.Tier}).");
            Assert.True(b.AllowedTags.IsSupersetOf(a.AllowedTags),
                $"{b.Name} (tier {b.Tier}) does not allow every tag {a.Name} (tier {a.Tier}) does.");
        }
    }

    [Fact]
    public void EveryStaffAllowsSomeShippedPart()
    {
        foreach (var staff in ItemRegistry.All.OfType<StaffItem>())
            Assert.Contains(SpellPartRegistry.All, part => part.IsAllowedBy(staff.AllowedTags));
    }

    internal static ItemFactory.ItemSpec Spec(int slots, int tier, IReadOnlySet<string> tags) =>
        new(0, "Staff", "t", ItemUseAction.None, 0, 0, 0,
            1f, 0, 0f, 1, 1, 1, ItemAnimationMode.None, 8f,
            Vector3.Zero, Vector3.Zero, 1f, false, 0.5f, 300f, AttackPattern.Punch, 0.5f, 1f, null,
            "staff", StaffSlots: slots, StaffTier: tier, StaffTags: tags);
}
