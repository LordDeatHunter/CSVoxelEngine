using System;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;
using VoxelEngine.Quests;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.Content;

/// <summary>The quest loader's graph rules: every quest of the shipped graphs can unlock, and a broken graph is refused
/// at load naming its file.</summary>
public class QuestLoaderTests
{
    [Fact]
    public void EveryShippedQuestCanEventuallyUnlockAndComplete()
    {
        TestContent.EnsureLoaded();
        var book = QuestRegistry.Book;
        Assert.NotEmpty(book.Quests);
        var log = new QuestLog(book);
        // Completing whatever is unlocked, round after round, must reach every quest: no parent cycle, no orphan
        for (var round = 0; round <= book.Quests.Count; round++)
            foreach (var quest in book.Quests)
                log.ForceComplete(quest);
        Assert.All(book.Quests, quest => Assert.True(log.IsComplete(quest), quest.Id));
    }

    public static TheoryData<string, QuestGraphDefinition.QuestDefinition[]> BrokenGraphs => new()
    {
        { "unknown parent", [Quest("a", ["nope"], Break("stone"))] },
        { "self parent", [Quest("a", ["a"], Break("stone"))] },
        { "cycle", [Quest("root", null, Break("stone")), Quest("a", ["root", "b"], Break("stone")), Quest("b", ["a"], Break("stone"))] },
        { "duplicate id", [Quest("a", null, Break("stone")), Quest("a", null, Break("stone"))] },
        { "unknown item", [Quest("a", null, Obtain("item/not_an_item"))] },
        { "unknown block", [Quest("a", null, Break("not_a_block"))] },
        { "unknown entity", [Quest("a", null, Kill("not_a_mob"))] },
        { "zero count", [Quest("a", null, Break("stone", 0))] },
        { "no requirements", [Quest("a", null)] },
        { "bad type", [Quest("a", null, new QuestGraphDefinition.RequirementDefinition { Type = "dance", Item = "stone" })] },
    };

    [Theory]
    [MemberData(nameof(BrokenGraphs))]
    public void ABrokenGraphIsRefusedNamingItsFile(string _, QuestGraphDefinition.QuestDefinition[] quests)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Book(quests));
        Assert.Contains("test.json", error.Message);
    }

    [Fact]
    public void ARootDefaultsToAHexAndAChildToACircleAndTheOriginOffsetsEveryNode()
    {
        TestContent.EnsureLoaded();
        var root = Quest("root", null, Break("stone"));
        root.Pos = [1, 2];
        var child = Quest("child", ["root"], Break("stone"));
        child.Pos = [3, 2];
        var book = QuestLoader.Compile([("g", new QuestGraphDefinition { Origin = [10, -5], Quests = [root, child] }, "g.json")]);

        Assert.Equal(QuestShape.Hex, book.Of("root").Shape);
        Assert.Equal(QuestShape.Circle, book.Of("child").Shape);
        Assert.Equal(new System.Numerics.Vector2(2, 0), book.Of("child").Position - book.Of("root").Position);
        Assert.Equal(new System.Numerics.Vector2(11, -3), book.Of("root").Position);
        Assert.Equal([book.Of("child").Index], book.Of("root").Children.ToArray());
    }
}
