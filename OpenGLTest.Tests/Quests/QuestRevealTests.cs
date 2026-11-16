using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;
using VoxelEngine.Quests;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.Quests;

/// <summary>When a quest shows in the book before it unlocks (its reveal mode), and that a locked preview never makes
/// progress. The fixture chain: root, mid (needs root) and late (needs mid), all with the default mode.</summary>
public class QuestRevealTests
{
    private static QuestGraphDefinition.QuestDefinition Revealed(string id, string reveal, params string[] parents)
    {
        var quest = Quest(id, parents, Break("stone"));
        quest.Reveal = reveal;
        return quest;
    }

    private static (QuestBook Book, QuestLog Log) Chain(QuestGraphDefinition.QuestDefinition probe)
    {
        var book = Book(Quest("root", null, Break("dirt")), Quest("mid", ["root"], Break("dirt")),
            Quest("late", ["mid"], Break("dirt")), probe);
        return (book, new QuestLog(book));
    }

    private static void Finish(QuestBook book, QuestLog log, string id) => Assert.True(log.ForceComplete(book.Of(id)));

    [Fact]
    public void ByDefaultAQuestShowsOnlyOnceEveryParentIsDone()
    {
        var (book, log) = Chain(Quest("probe", ["root"], Break("stone")));
        Assert.False(log.IsVisible(book.Of("probe")));
        Assert.False(log.IsVisible(book.Of("mid")));

        Finish(book, log, "root");
        Assert.True(log.IsVisible(book.Of("probe")));
        Assert.False(log.IsPreview(book.Of("probe")));
    }

    [Fact]
    public void AlwaysShowsFromTheStartAsALockedPreview()
    {
        var (book, log) = Chain(Revealed("probe", "always", "late"));
        Assert.True(log.IsVisible(book.Of("probe")));
        Assert.True(log.IsPreview(book.Of("probe")));
    }

    [Fact]
    public void AnyParentShownWaitsForOneShownParent()
    {
        var (book, log) = Chain(Revealed("probe", "anyParentShown", "mid", "late"));
        Assert.False(log.IsVisible(book.Of("probe")));

        Finish(book, log, "root");
        Assert.True(log.IsPreview(book.Of("probe")));
    }

    [Fact]
    public void AllParentsShownWaitsForEveryParentToShow()
    {
        var (book, log) = Chain(Revealed("probe", "allParentsShown", "root", "late"));
        Finish(book, log, "root");
        Assert.False(log.IsVisible(book.Of("probe")));

        Finish(book, log, "mid");
        Assert.True(log.IsPreview(book.Of("probe")));
    }

    [Fact]
    public void AnyParentDoneWaitsForOneFinishedParentWhateverTheOthersShow()
    {
        var (book, log) = Chain(Revealed("probe", "anyParentDone", "root", "late"));
        Assert.False(log.IsVisible(book.Of("probe")));

        Finish(book, log, "root");
        Assert.False(log.IsVisible(book.Of("late")));
        Assert.True(log.IsPreview(book.Of("probe")));
    }

    [Fact]
    public void AnyParentDoneAllShownAlsoWaitsForTheOtherParentsToShow()
    {
        var (book, log) = Chain(Revealed("probe", "anyParentDoneAllShown", "root", "late"));
        Finish(book, log, "root");
        Assert.False(log.IsVisible(book.Of("probe")));

        Finish(book, log, "mid");
        Assert.True(log.IsPreview(book.Of("probe")));
    }

    [Fact]
    public void ARevealedQuestTracksTheParentsItWatchesAsTheyChange()
    {
        var (book, log) = Chain(Revealed("probe", "anyParentShown", "late"));
        Assert.False(log.IsVisible(book.Of("probe")));

        Finish(book, log, "root");
        Finish(book, log, "mid");
        Assert.True(log.IsVisible(book.Of("probe")));
    }

    [Fact]
    public void ALockedPreviewMakesNoProgressUntilItUnlocks()
    {
        var (book, log) = Chain(Revealed("probe", "always", "root"));
        var probe = book.Of("probe");

        log.OnBroke(Id("stone"));
        Assert.Equal(0, log.ProgressOf(probe, 0));
        Assert.False(log.ForceComplete(probe));

        Finish(book, log, "root");
        Assert.False(log.IsPreview(probe));
        log.OnBroke(Id("stone"));
        Assert.True(log.IsComplete(probe));
    }
}
