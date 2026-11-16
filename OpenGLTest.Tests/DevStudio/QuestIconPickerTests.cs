using System.Linq;
using VoxelEngine.DevStudio;
using Xunit;

namespace VoxelEngine.Tests.DevStudio;

/// <summary>The QUESTS tab's icon grid filter: a query matches the display or quest name ignoring case, and a kind
/// toggled off drops out whatever the query.</summary>
public class QuestIconPickerTests
{
    private static QuestIconPicker Picker() => new([
        new IconEntry(IconKind.Item, 1, "Slimeball", "item/slimeball"),
        new IconEntry(IconKind.Block, 2, "Oak Log", "oak_log"),
        new IconEntry(IconKind.Entity, 0, "Slime", "slime"),
        new IconEntry(IconKind.Entity, 0, "Goblin", "goblin"),
    ]);

    [Fact]
    public void AQueryMatchesTheDisplayOrTheQuestNameIgnoringCase()
    {
        var picker = Picker();

        picker.SetQuery("SLIME");
        Assert.Equal(["Slimeball", "Slime"], picker.Visible.Select(e => e.Name));

        picker.SetQuery("oak_");
        Assert.Equal(["Oak Log"], picker.Visible.Select(e => e.Name));

        picker.SetQuery("  ");
        Assert.Equal(picker.All.Count, picker.Visible.Count);
    }

    [Fact]
    public void AKindToggledOffDropsOutAndBackIn()
    {
        var picker = Picker();
        picker.SetQuery("slime");

        picker.Toggle(IconKind.Entity);
        Assert.Equal(["Slimeball"], picker.Visible.Select(e => e.Name));
        Assert.False(picker.Shows(IconKind.Entity));

        picker.Toggle(IconKind.Item);
        Assert.Empty(picker.Visible);

        picker.Toggle(IconKind.Entity);
        Assert.Equal(["Slime"], picker.Visible.Select(e => e.Name));
    }

    [Fact]
    public void TheContentGridOffersEveryRegisteredEntityByItsFileName()
    {
        TestContent.EnsureLoaded();
        var picker = QuestIconPicker.FromContent([]);

        Assert.Equal(VoxelEngine.Entities.EntityRegistry.ByName.Keys.Order(), picker.All.Select(e => e.Key).Order());
        Assert.All(picker.All, e => Assert.Equal(IconKind.Entity, e.Kind));
    }
}
