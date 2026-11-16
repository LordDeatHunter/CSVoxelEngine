using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;
using VoxelEngine.Quests;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.Quests;

/// <summary>When a quest unlocks (shows its details and makes progress) by its unlock mode. The fixture: two roots,
/// left and right, and a probe that depends on both.</summary>
public class QuestUnlockTests
{
    private static (QuestBook Book, QuestLog Log) Pair(string? unlock)
    {
        var probe = Quest("probe", ["left", "right"], Break("stone"));
        probe.Unlock = unlock;
        var book = Book(Quest("left", null, Break("dirt")), Quest("right", null, Break("dirt")), probe);
        return (book, new QuestLog(book));
    }

    [Fact]
    public void ByDefaultAQuestUnlocksOnlyOnceEveryParentIsDone()
    {
        var (book, log) = Pair(null);
        Assert.True(log.ForceComplete(book.Of("left")));
        Assert.False(log.IsUnlocked(book.Of("probe")));

        Assert.True(log.ForceComplete(book.Of("right")));
        Assert.True(log.IsUnlocked(book.Of("probe")));
    }

    [Fact]
    public void AnyParentDoneUnlocksOnTheFirstFinishedParentAndThenMakesProgress()
    {
        var (book, log) = Pair("anyParentDone");
        var probe = book.Of("probe");
        log.OnBroke(Id("stone"));
        Assert.Equal(0, log.ProgressOf(probe, 0));

        Assert.True(log.ForceComplete(book.Of("right")));

        Assert.True(log.IsUnlocked(probe));
        Assert.False(log.IsPreview(probe));
        log.OnBroke(Id("stone"));
        Assert.True(log.IsComplete(probe));
    }

    [Fact]
    public void AnyParentDoneChecksWhatIsAlreadyHeldWhenItUnlocks()
    {
        var probe = Quest("probe", ["left", "right"], Obtain("stone"));
        probe.Unlock = "anyParentDone";
        var book = Book(Quest("left", null, Break("dirt")), Quest("right", null, Break("dirt")), probe);
        var log = new QuestLog(book);
        var inventory = VoxelEngine.UI.InventoryPanel.Inventory.Empty();
        inventory.Add(Id("stone"));
        log.Bind(inventory);

        log.OnBroke(Id("dirt"));

        Assert.True(log.IsComplete(book.Of("probe")));
    }

    [Fact]
    public void AlwaysMakesProgressFromTheStart()
    {
        var (book, log) = Pair("always");

        log.OnBroke(Id("stone"));

        Assert.True(log.IsComplete(book.Of("probe")));
        Assert.False(log.IsComplete(book.Of("left")));
    }
}
