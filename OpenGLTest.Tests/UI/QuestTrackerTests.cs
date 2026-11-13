using VoxelEngine.Quests;
using VoxelEngine.Saves;
using VoxelEngine.UI.Quests;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.UI;

/// <summary>The equipped quest: one at a time, only an open readable quest can be equipped, and finishing it
/// unequips it.</summary>
public class QuestTrackerTests
{
    [Fact]
    public void EquippingASecondQuestReplacesTheFirst()
    {
        var book = Book(Quest("a", null, Break("stone")), Quest("b", null, Break("dirt")));
        var log = new QuestLog(book);
        var view = new QuestViewData();

        QuestTracker.Toggle(log, view, book.Of("a"));
        QuestTracker.Toggle(log, view, book.Of("b"));

        Assert.Same(book.Of("b"), QuestTracker.Current(log, view));
        Assert.False(QuestTracker.IsEquipped(view, book.Of("a")));
    }

    [Fact]
    public void TogglingTheEquippedQuestUnequipsIt()
    {
        var book = Book(Quest("a", null, Break("stone")));
        var log = new QuestLog(book);
        var view = new QuestViewData();

        QuestTracker.Toggle(log, view, book.Of("a"));
        QuestTracker.Toggle(log, view, book.Of("a"));

        Assert.Null(QuestTracker.Current(log, view));
    }

    [Fact]
    public void ALockedQuestCannotBeEquipped()
    {
        var book = Book(Quest("root", null, Break("dirt")), Quest("child", ["root"], Break("stone")));
        var log = new QuestLog(book);
        var view = new QuestViewData();

        QuestTracker.Toggle(log, view, book.Of("child"));

        Assert.Null(view.Equipped);
    }

    [Fact]
    public void CompletingTheEquippedQuestUnequipsIt()
    {
        var book = Book(Quest("a", null, Break("stone")));
        var log = new QuestLog(book);
        var view = new QuestViewData();
        QuestTracker.Toggle(log, view, book.Of("a"));

        log.OnBroke(Id("stone"));

        Assert.Null(QuestTracker.Current(log, view));
        Assert.Null(view.Equipped);
    }

    [Fact]
    public void ASavedIdThatNamesNoQuestShowsNothing()
    {
        var log = new QuestLog(Book(Quest("a", null, Break("stone"))));
        var view = new QuestViewData { Equipped = "gone" };

        Assert.Null(QuestTracker.Current(log, view));
    }
}
