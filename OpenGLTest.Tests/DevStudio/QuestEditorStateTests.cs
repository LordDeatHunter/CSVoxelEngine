using System;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Definitions;
using VoxelEngine.DevStudio;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.DevStudio;

/// <summary>The QUESTS tab's edit rules: links stay consistent through deletes and renames, a link that could never
/// unlock is refused, positions are stored the way the loader reads them, and a broken graph is reported, not thrown,
/// and never saved.</summary>
public sealed class QuestEditorStateTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dev-studio-quests").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    private QuestGraphDocument Graph(string name, float[]? origin, params QuestGraphDefinition.QuestDefinition[] quests) =>
        new(Path.Combine(_folder, name + ".json"), new QuestGraphDefinition { Title = name, Origin = origin, Quests = [.. quests] }, []);

    private QuestEditorState Editor(params QuestGraphDocument[] graphs)
    {
        TestContent.EnsureLoaded();
        return new QuestEditorState(_folder, graphs);
    }

    [Fact]
    public void DeletingAQuestRemovesEveryLinkToItInEveryGraph()
    {
        var first = Graph("first", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        var second = Graph("second", null, Quest("b", ["root", "a"], Break("stone")));
        var editor = Editor(first, second);
        Assert.Null(editor.Error);

        editor.DeleteQuest("a");

        Assert.Null(editor.Find("a"));
        Assert.Equal(["root"], editor.Find("b")!.Value.Quest.Parents);
        Assert.True(first.Dirty && second.Dirty);
        Assert.Null(editor.Error);
    }

    [Fact]
    public void ALinkToItselfOrOneClosingACycleIsRefusedAndChangesNothing()
    {
        var editor = Editor(Graph("g", null,
            Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")), Quest("b", ["a"], Break("stone"))));

        Assert.NotNull(editor.ToggleParent("a", "a"));
        Assert.NotNull(editor.ToggleParent("root", "b"));
        Assert.Null(editor.Find("root")!.Value.Quest.Parents);
        Assert.False(editor.Dirty);
        Assert.Null(editor.Error);
    }

    [Fact]
    public void TogglingAParentLinksItThenUnlinksIt()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", null, Break("stone"))));

        Assert.Null(editor.ToggleParent("a", "root"));
        Assert.Equal(["root"], editor.Find("a")!.Value.Quest.Parents);
        Assert.Contains(editor.Preview.Of("root").Index, editor.Preview.Of("a").Parents);

        Assert.Null(editor.ToggleParent("a", "root"));
        Assert.Empty(editor.Preview.Of("a").Parents);
    }

    [Fact]
    public void RenamingAQuestFollowsItsChildrenAndRefusesATakenId()
    {
        var editor = Editor(Graph("g", null,
            Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")), Quest("b", ["root"], Break("stone"))));
        editor.Select("root");

        Assert.NotNull(editor.RenameQuest("root", "a"));
        Assert.Null(editor.RenameQuest("root", "start"));

        Assert.Equal("start", editor.SelectedQuestId);
        Assert.All(new[] { "a", "b" }, id => Assert.Equal(["start"], editor.Find(id)!.Value.Quest.Parents));
        Assert.Null(editor.Error);
    }

    [Fact]
    public void AMovedQuestIsStoredRelativeToItsGraphOriginAndSnappedUnlessAsked()
    {
        var editor = Editor(Graph("g", [10, -5], Quest("root", null, Break("stone"))));
        var quest = editor.Find("root")!.Value.Quest;

        editor.MoveQuest("root", new Vector2(12.3f, -4.1f), snap: true);
        var snapped = new Vector2(quest.Pos![0], quest.Pos[1]);
        Assert.Equal(Vector2.Zero, snapped / QuestEditorState.SnapStep - Vector2.Round(snapped / QuestEditorState.SnapStep));
        Assert.True(Vector2.Distance(editor.Preview.Of("root").Position, new Vector2(12.3f, -4.1f)) <= QuestEditorState.SnapStep);

        editor.MoveQuest("root", new Vector2(12.3f, -4.1f), snap: false);
        Assert.Equal(new Vector2(12.3f, -4.1f), editor.Preview.Of("root").Position);
    }

    [Fact]
    public void ABrokenEditIsReportedWithoutThrowingAndIsNeverSaved()
    {
        var graph = Graph("g", null, Quest("root", null, Break("stone")));
        var editor = Editor(graph);

        editor.SetRequirementTarget("root", 0, "not_a_block");

        Assert.NotNull(editor.Error);
        Assert.Equal("root", editor.ErrorQuestId);
        Assert.Single(editor.Preview.Quests);
        editor.Save();
        Assert.False(File.Exists(graph.Path));

        editor.SetRequirementTarget("root", 0, "stone");
        editor.Save();
        Assert.True(File.Exists(graph.Path));
        Assert.False(editor.Dirty);
    }

    [Fact]
    public void EveryRequirementTypeKeepsExactlyTheOneTargetFieldItReads()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Obtain("stone"))));
        var requirement = editor.Find("root")!.Value.Quest.Requirements![0];

        foreach (var type in QuestEditorState.RequirementTypes)
        {
            editor.SetRequirementType("root", 0, type);
            Assert.Equal(type, requirement.Type);
            var set = new object?[] { requirement.Item, requirement.Tag, requirement.Block, requirement.Entity }.Count(f => f != null);
            Assert.Equal(1, set);
            Assert.Equal(requirement.Type == "kill", requirement.Entity != null);
            Assert.Equal(requirement.Type is "break" or "place", requirement.Block != null);
        }

        editor.SetRequirementTarget("root", 0, QuestEditorState.TagPrefix + "logs");
        Assert.Equal(QuestEditorState.TakesItem(requirement), requirement.Tag != null);
    }

    [Fact]
    public void AnAddedQuestOrGraphCompilesAsItIsWithAFreshId()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone"))));

        var first = editor.AddQuest(new Vector2(3f, 1f));
        var second = editor.AddQuest(new Vector2(4f, 1f));
        var graph = editor.AddGraph(new Vector2(-6f, 2f));

        Assert.NotEqual(first, second);
        Assert.Equal(graph, editor.Graph);
        Assert.Equal(4, editor.Preview.Quests.Count);
        Assert.Null(editor.Error);
    }
}
