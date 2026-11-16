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
/// unlock is refused, positions are stored the way the loader reads them, a broken graph is reported, not thrown, and
/// never saved, and a quest saves or reverts on its own while every other edit stays pending.</summary>
public sealed class QuestEditorStateTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dev-studio-quests").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    // Writes a graph file into the test folder; returns its path
    private string Graph(string name, float[]? origin, params QuestGraphDefinition.QuestDefinition[] quests)
    {
        var path = Path.Combine(_folder, name + ".json");
        QuestGraphWriter.Save(path, new QuestGraphDefinition { Title = name, Origin = origin, Quests = [.. quests] }, []);
        return path;
    }

    // The editor over the files the arguments wrote
    private QuestEditorState Editor(params string[] _)
    {
        TestContent.EnsureLoaded();
        return QuestEditorState.Load(_folder);
    }

    private static string? TitleOnDisk(string path, string id) =>
        QuestGraphDocument.Load(path).Quests.Single(q => q.Id == id).Title;

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
        Assert.All(editor.Documents, d => Assert.True(d.Dirty, d.Name));
        Assert.Null(editor.Error);
    }

    [Fact]
    public void ALinkToItselfOrOneClosingACycleIsRefusedAndChangesNothing()
    {
        var editor = Editor(Graph("g", null,
            Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")), Quest("b", ["a"], Break("stone"))));

        Assert.NotNull(editor.AddParent("a", "a"));
        Assert.NotNull(editor.AddParent("root", "b"));
        Assert.Null(editor.Find("root")!.Value.Quest.Parents);
        Assert.False(editor.Dirty);
        Assert.Null(editor.Error);
    }

    [Fact]
    public void ALinkAddedThenRemovedComesAndGoesFromThePreview()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", null, Break("stone"))));

        Assert.Null(editor.AddParent("a", "root"));
        Assert.Equal(["root"], editor.Find("a")!.Value.Quest.Parents);
        Assert.Contains(editor.Preview.Of("root").Index, editor.Preview.Of("a").Parents);

        Assert.Null(editor.RemoveLink("a", "root"));
        Assert.Empty(editor.Preview.Of("a").Parents);
    }

    [Fact]
    public void AddingAParentRefusesADuplicateOrACycleAndOtherwiseLinks()
    {
        var editor = Editor(Graph("g", null,
            Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")), Quest("b", null, Break("stone"))));

        Assert.NotNull(editor.AddParent("a", "root"));
        Assert.NotNull(editor.AddParent("root", "a"));
        Assert.False(editor.Dirty);

        Assert.Null(editor.AddParent("b", "a"));
        Assert.Equal(["a"], editor.Find("b")!.Value.Quest.Parents);
    }

    [Fact]
    public void SwappingALinkTurnsItAroundUnlessAnotherPathWouldMakeACycle()
    {
        var editor = Editor(Graph("g", null,
            Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")), Quest("b", ["a", "root"], Break("stone"))));

        Assert.NotNull(editor.SwapLink("b", "root"));
        Assert.Equal(["a", "root"], editor.Find("b")!.Value.Quest.Parents);
        Assert.False(editor.Dirty);

        Assert.Null(editor.SwapLink("b", "a"));
        Assert.Equal(["root"], editor.Find("b")!.Value.Quest.Parents);
        Assert.Contains("b", editor.Find("a")!.Value.Quest.Parents!);
        Assert.Equal(("a", "b"), editor.SelectedLink);
        Assert.Null(editor.Error);
    }

    [Fact]
    public void ARemovedOrRenamedLinkKeepsTheLinkSelectionTruthful()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone"))));
        editor.SelectLink("a", "root");

        editor.RenameQuest("root", "start");
        Assert.Equal(("a", "start"), editor.SelectedLink);

        Assert.Null(editor.RemoveLink("a", "start"));
        Assert.Null(editor.SelectedLink);
        Assert.NotNull(editor.RemoveLink("a", "start"));
    }

    [Fact]
    public void AQuestAddedFromAParentJoinsItsGraphDependingOnIt()
    {
        Graph("first", null, Quest("root", null, Break("stone")));
        var editor = Editor(Graph("second", [20, 0], Quest("far", null, Break("stone"))));
        editor.Select("root");

        var added = editor.AddQuest(new Vector2(25f, 1f), parent: "far")!;

        Assert.Equal("second", editor.Find(added)!.Value.Document.Name);
        Assert.Equal(["far"], editor.Find(added)!.Value.Quest.Parents);
        Assert.Null(editor.Error);
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
        var before = File.ReadAllText(graph);
        editor.Save();
        editor.SaveQuest("root");
        Assert.Equal(before, File.ReadAllText(graph));

        editor.SetRequirementTarget("root", 0, "block/stone");
        editor.Save();
        Assert.NotEqual(before, File.ReadAllText(graph));
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
    public void AFreshlyLoadedEditorHasNothingUnsaved()
    {
        var editor = Editor(Graph("g", [1, 2], Quest("root", null, Break("stone")), Quest("a", ["root"], Obtain("stone", 3))));

        Assert.False(editor.Dirty);
        Assert.All(editor.Preview.Quests, q => Assert.False(editor.IsQuestDirty(q.Id), q.Id));
    }

    [Fact]
    public void SavingOneQuestWritesOnlyItsEditAndLeavesTheOthersPending()
    {
        var path = Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        var editor = Editor(path);
        editor.SetTitle("root", "Edited root");
        editor.SetTitle("a", "Edited a");

        editor.SaveQuest("a");

        Assert.Equal("Edited a", TitleOnDisk(path, "a"));
        Assert.NotEqual("Edited root", TitleOnDisk(path, "root"));
        Assert.False(editor.IsQuestDirty("a"));
        Assert.True(editor.IsQuestDirty("root"));
        Assert.True(editor.Dirty);
    }

    [Fact]
    public void AQuestSaveTheFilesCouldNotLoadIsRefused()
    {
        var path = Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        var editor = Editor(path);
        var before = File.ReadAllText(path);
        editor.RenameQuest("root", "start");

        // On disk, a still names root: saving the rename alone would orphan it
        editor.SaveQuest("start");

        Assert.Equal(before, File.ReadAllText(path));
        Assert.True(editor.IsQuestDirty("start"));
    }

    [Fact]
    public void RevertingAQuestRestoresItsSavedSelfAndTheLinksToIt()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone"))));
        var savedPosition = editor.Preview.Of("root").Position;
        editor.RenameQuest("root", "start");
        editor.SetTitle("start", "Edited");
        editor.MoveQuest("start", savedPosition + new Vector2(3f, 2f), snap: true);
        editor.SetTitle("a", "Kept");

        editor.RevertQuest("start");

        Assert.Null(editor.Find("start"));
        Assert.False(editor.IsQuestDirty("root"));
        Assert.Equal(savedPosition, editor.Preview.Of("root").Position);
        Assert.Equal(["root"], editor.Find("a")!.Value.Quest.Parents);
        Assert.Equal("Kept", editor.Find("a")!.Value.Quest.Title);
        Assert.Null(editor.Error);
    }

    [Fact]
    public void RevertingAQuestThatWasNeverSavedRemovesIt()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone"))));
        var added = editor.AddQuest(new Vector2(2f, 0f))!;
        editor.AddParent(added, "root");

        editor.RevertQuest(added);

        Assert.Null(editor.Find(added));
        Assert.False(editor.Dirty);
    }

    [Fact]
    public void RevertingAllRestoresTheFilesAndDropsGraphsNeverSaved()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone"))));
        editor.DeleteQuest("a");
        editor.SetTitle("root", "Edited");
        editor.AddGraph(new Vector2(-8f, 0f));

        editor.RevertAll();

        Assert.False(editor.Dirty);
        Assert.Single(editor.Documents);
        Assert.NotNull(editor.Find("a"));
        Assert.NotEqual("Edited", editor.Find("root")!.Value.Quest.Title);
    }

    private static string? TitleOf(QuestEditorState editor, string id) => editor.Find(id)?.Quest.Title;

    [Fact]
    public void UndoStepsBackThroughEditsAndRedoStepsForwardAgain()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone"))));
        var original = (TitleOf(editor, "root"), TitleOf(editor, "a"));
        editor.SetTitle("root", "First");
        editor.SetTitle("a", "Second");

        Assert.True(editor.Undo());
        Assert.Equal(("First", original.Item2), (TitleOf(editor, "root"), TitleOf(editor, "a")));
        Assert.True(editor.Undo());
        Assert.Equal(original, (TitleOf(editor, "root"), TitleOf(editor, "a")));
        Assert.False(editor.Undo());
        Assert.False(editor.Dirty);

        Assert.True(editor.Redo());
        Assert.True(editor.Redo());
        Assert.Equal(("First", "Second"), (TitleOf(editor, "root"), TitleOf(editor, "a")));
        Assert.False(editor.Redo());
    }

    [Fact]
    public void AnEditAfterAnUndoDropsEverythingUndone()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone"))));
        editor.SetTitle("root", "First");
        editor.SetTitle("root", "Second");
        editor.Undo();

        editor.SetTitle("root", "Third");

        Assert.False(editor.CanRedo);
        Assert.False(editor.Redo());
        Assert.Equal("Third", TitleOf(editor, "root"));
    }

    [Fact]
    public void AGestureIsOneStepAndARefusedEditIsNone()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone"))));
        var original = TitleOf(editor, "root");

        Assert.NotNull(editor.AddParent("a", "root"));
        Assert.False(editor.CanUndo);

        editor.Gesture = "typing";
        foreach (var title in new[] { "T", "Ti", "Tit" }) editor.SetTitle("root", title);
        editor.Gesture = null;

        Assert.True(editor.Undo());
        Assert.Equal(original, TitleOf(editor, "root"));
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void UndoBringsBackDeletedQuestsAndLinksAndRevertAllIsAStepToo()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone"))));
        editor.DeleteQuest("root");
        Assert.True(editor.Undo());
        Assert.Equal(["root"], editor.Find("a")!.Value.Quest.Parents);

        editor.SetTitle("a", "Edited");
        editor.RevertAll();
        Assert.True(editor.Undo());
        Assert.Equal("Edited", TitleOf(editor, "a"));
    }

    [Fact]
    public void UndoingAnEditThatWasSavedLeavesTheFileAndMarksTheQuestUnsaved()
    {
        var path = Graph("g", null, Quest("root", null, Break("stone")));
        var editor = Editor(path);
        editor.SetTitle("root", "Saved title");
        editor.SaveQuest("root");

        Assert.True(editor.Undo());

        Assert.Equal("Saved title", TitleOnDisk(path, "root"));
        Assert.True(editor.IsQuestDirty("root"));
        Assert.True(editor.Redo());
        Assert.False(editor.IsQuestDirty("root"));
    }

    [Fact]
    public void UndoRestoresTheSelectionTheChangeWasMadeUnder()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone"))));
        editor.Select("a");
        editor.SetTitle("a", "Edited");
        editor.Select("root");
        editor.SetTitle("root", "Edited too");

        editor.Undo();

        Assert.Equal("root", editor.SelectedQuestId);
        editor.Undo();
        Assert.Equal("a", editor.SelectedQuestId);
    }

    [Fact]
    public void DeletingSeveralQuestsIsOneStepThatUndoesWhole()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")),
            Quest("b", ["a"], Break("stone")), Quest("c", ["a", "root"], Break("stone"))));
        editor.SelectMany(["a", "b"]);

        Assert.Equal(2, editor.DeleteQuests(editor.SelectedQuestIds.ToList()));

        Assert.Null(editor.Find("a"));
        Assert.Null(editor.Find("b"));
        Assert.Equal(["root"], editor.Find("c")!.Value.Quest.Parents);
        Assert.Empty(editor.SelectedQuestIds);
        Assert.Null(editor.Error);

        Assert.True(editor.Undo());
        Assert.NotNull(editor.Find("a"));
        Assert.NotNull(editor.Find("b"));
        Assert.Equal(["a", "root"], editor.Find("c")!.Value.Quest.Parents);
        Assert.Equal(["a", "b"], editor.SelectedQuestIds);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void TogglingBuildsAndShrinksTheSelectionAndOnlyOneSelectedQuestIsEdited()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone"))));
        editor.Select("root");
        Assert.Equal("root", editor.SelectedQuestId);

        editor.ToggleSelected("a");
        Assert.Equal(["root", "a"], editor.SelectedQuestIds);
        Assert.Null(editor.SelectedQuestId);

        editor.ToggleSelected("root");
        Assert.Equal("a", editor.SelectedQuestId);
        editor.ToggleSelected("nope");
        Assert.Equal(["a"], editor.SelectedQuestIds);
    }

    [Fact]
    public void AGroupMoveKeepsTheLayoutAndLandsTheDraggedQuestOnTheGrid()
    {
        var a = Quest("a", null, Break("stone"));
        a.Pos = [0.25f, 0];
        var b = Quest("b", null, Break("stone"));
        b.Pos = [3, 1.75f];
        Graph("other", [10, 10], Quest("c", null, Break("stone")));
        var editor = Editor(Graph("g", [1, -2], Quest("root", null, Break("stone")), a, b));
        var grabbed = new[] { "a", "b", "c" }.ToDictionary(id => id, id => editor.Preview.Of(id).Position);
        var offsets = grabbed.ToDictionary(kv => kv.Key, kv => kv.Value - grabbed["a"]);
        var rootBefore = editor.Preview.Of("root").Position;

        editor.Gesture = "drag";
        editor.MoveQuests("a", grabbed["a"] + new Vector2(1.1f, 0.4f), grabbed, snap: true);
        editor.MoveQuests("a", grabbed["a"] + new Vector2(2.3f, 1.2f), grabbed, snap: true);
        editor.Gesture = null;

        var lead = editor.Preview.Of("a").Position - new Vector2(1, -2);
        Assert.Equal(Vector2.Zero, lead / QuestEditorState.SnapStep - Vector2.Round(lead / QuestEditorState.SnapStep));
        foreach (var (id, offset) in offsets)
            Assert.True(Vector2.Distance(offset, editor.Preview.Of(id).Position - editor.Preview.Of("a").Position) < 1e-4f, id);
        Assert.Equal(rootBefore, editor.Preview.Of("root").Position);

        Assert.True(editor.Undo());
        Assert.All(grabbed, kv => Assert.Equal(kv.Value, editor.Preview.Of(kv.Key).Position));
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void ARenamedQuestStaysSelected()
    {
        var editor = Editor(Graph("g", null, Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone"))));
        editor.SelectMany(["root", "a"]);

        editor.RenameQuest("root", "start");

        Assert.Equal(["start", "a"], editor.SelectedQuestIds);
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
