using VoxelEngine.Content.Quests;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Quests;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.Quests;

/// <summary>The creative earn and unearn: an earn completes the quest and everything it depends on and nothing beside
/// it, an unearn clears the quest and everything that depends on it and nothing above it, and on a server only a
/// creative player's intent changes its own log. The fixture: root, then mid (needs root), then leaf (needs mid), and a
/// side quest that also needs root.</summary>
public class QuestForceTests
{
    private static (QuestBook Book, QuestLog Log) Chain()
    {
        var book = Book(Quest("root", null, Break("dirt")), Quest("mid", ["root"], Break("dirt")),
            Quest("leaf", ["mid"], Break("dirt")), Quest("side", ["root"], Break("dirt")));
        return (book, new QuestLog(book));
    }

    [Fact]
    public void EarningAQuestEarnsEverythingItDependsOnAndNothingBesideIt()
    {
        var (book, log) = Chain();
        var completed = 0;
        log.Completed += _ => completed++;

        Assert.Equal(3, log.ForceEarn(book.Of("leaf")));

        Assert.All(new[] { "root", "mid", "leaf" }, id => Assert.True(log.IsComplete(book.Of(id)), id));
        Assert.False(log.IsComplete(book.Of("side")));
        Assert.Equal(3, completed);
        Assert.True(log.Changed);
        Assert.Equal(0, log.ForceEarn(book.Of("leaf")));
    }

    [Fact]
    public void UnearningAQuestClearsEverythingAfterItAndNothingAboveIt()
    {
        var (book, log) = Chain();
        log.ForceEarn(book.Of("leaf"));
        log.ForceEarn(book.Of("side"));
        log.Changed = false;

        Assert.Equal(2, log.ForceUnearn(book.Of("mid")));

        Assert.True(log.IsComplete(book.Of("root")));
        Assert.True(log.IsComplete(book.Of("side")));
        Assert.False(log.IsComplete(book.Of("mid")));
        Assert.False(log.IsComplete(book.Of("leaf")));
        Assert.Equal(0, log.ProgressOf(book.Of("mid"), 0));
        Assert.True(log.Changed);
        Assert.Equal(0, log.ForceUnearn(book.Of("mid")));
    }

    [Fact]
    public void OnlyACreativePlayersQuestIntentChangesItsLog()
    {
        var (book, log) = Chain();
        var earn = InventoryIntent.CreativeQuest("leaf", earn: true);

        Assert.False(InventoryIntentRules.ApplyQuest(log, earn, creative: false));
        Assert.False(log.IsComplete(book.Of("leaf")));
        Assert.False(InventoryIntentRules.ApplyQuest(log, InventoryIntent.CreativeQuest("nope", true), creative: true));

        Assert.True(InventoryIntentRules.ApplyQuest(log, earn, creative: true));
        Assert.True(log.IsComplete(book.Of("leaf")));
        Assert.True(InventoryIntentRules.ApplyQuest(log, InventoryIntent.CreativeQuest("root", earn: false), creative: true));
        Assert.False(log.IsComplete(book.Of("root")));
    }
}
