using System;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;
using VoxelEngine.Quests;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.Content;

/// <summary>The quest loader's rules: every shipped quest can unlock, a broken quest is refused at load naming its file,
/// and a quest file's name is its id.</summary>
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

    public static TheoryData<string, QuestDefinition[]> BrokenGraphs => new()
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
        { "bad type", [Quest("a", null, new QuestRequirementDefinition { Type = "dance", Item = "stone" })] },
        { "bad reveal", [Revealing(Quest("a", null, Break("stone")), "sometimes")] },
        { "numeric reveal", [Revealing(Quest("a", null, Break("stone")), "1")] },
    };

    private static QuestDefinition Revealing(QuestDefinition quest, string reveal)
    {
        quest.Reveal = reveal;
        return quest;
    }

    [Theory]
    [MemberData(nameof(BrokenGraphs))]
    public void ABrokenGraphIsRefusedNamingItsFile(string _, QuestDefinition[] quests)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Book(quests));
        Assert.Contains(quests, q => error.Message.Contains(q.Id + ".json"));
    }

    [Fact]
    public void ARootDefaultsToAHexAndAChildToACircleAtTheirOwnPositions()
    {
        var root = Quest("root", null, Break("stone"));
        root.Pos = [1, 2];
        var child = Quest("child", ["root"], Break("stone"));
        child.Pos = [3, 2];
        var book = Book(root, child);

        Assert.Equal(QuestShape.Hex, book.Of("root").Shape);
        Assert.Equal(QuestShape.Circle, book.Of("child").Shape);
        Assert.Equal(new System.Numerics.Vector2(1, 2), book.Of("root").Position);
        Assert.Equal([book.Of("child").Index], book.Of("root").Children.ToArray());
    }

    private static QuestLabelDefinition Label(string? size = null, string? color = null, string? text = "Hello") =>
        new() { Id = "note", Text = text, Pos = [2, -1], Size = size, Color = color };

    [Fact]
    public void ALabelDefaultsToMediumGrayAndKeepsItsLineBreaks()
    {
        var label = QuestLoader.CompileLabel(Label(text: "Two\nlines"), "note.json");

        Assert.Equal(QuestLabelSize.Medium, label.Size);
        Assert.Equal(QuestLabelColor.Gray, label.Color);
        Assert.Equal(2, label.Text.Split('\n').Length);
        Assert.Equal(QuestLabelSize.Large, QuestLoader.CompileLabel(Label(size: "LARGE"), "note.json").Size);
    }

    [Theory]
    [InlineData("huge", null, "Hello")]
    [InlineData(null, "purple", "Hello")]
    [InlineData(null, null, " ")]
    public void ABrokenLabelIsRefusedNamingItsFile(string? size, string? color, string text)
    {
        var error = Assert.Throws<InvalidOperationException>(() => QuestLoader.CompileLabel(Label(size, color, text), "note.json"));
        Assert.Contains("note.json", error.Message);
    }

    [Fact]
    public void LabelsInTheLabelsFolderAreNotQuests()
    {
        TestContent.EnsureLoaded();
        var folder = System.IO.Directory.CreateTempSubdirectory("quest-labels").FullName;
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "only.json"),
                """{ "icon": "stone", "pos": [0, 0], "requirements": [{ "type": "break", "block": "stone" }] }""");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(folder, QuestLoader.LabelFolder));
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, QuestLoader.LabelFolder, "title.json"),
                """{ "text": "Survival", "pos": [0, -1], "color": "gold" }""");

            var book = QuestLoader.ReadQuests(folder);

            Assert.Equal(["only"], book.Quests.Select(q => q.Id));
            Assert.Equal(["title"], book.Labels.Select(l => l.Id));
            Assert.Equal(QuestLabelColor.Gold, book.Labels[0].Color);
        }
        finally
        {
            System.IO.Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AQuestFileIsNamedByItsIdWhateverItsJsonSays()
    {
        TestContent.EnsureLoaded();
        var folder = System.IO.Directory.CreateTempSubdirectory("quest-loader").FullName;
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "first.json"),
                """{ "id": "ignored", "icon": "stone", "pos": [0, 0], "requirements": [{ "type": "break", "block": "stone" }] }""");
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "second.json"),
                """{ "icon": "stone", "pos": [1, 0], "parents": ["first"], "requirements": [{ "type": "break", "block": "stone" }] }""");

            var book = QuestLoader.ReadQuests(folder);

            Assert.Equal(["first", "second"], book.Quests.Select(q => q.Id));
            Assert.Equal([book.Of("first").Index], book.Of("second").Parents);
        }
        finally
        {
            System.IO.Directory.Delete(folder, true);
        }
    }
}
