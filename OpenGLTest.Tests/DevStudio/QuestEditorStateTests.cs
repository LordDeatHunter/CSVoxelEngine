using System;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;
using VoxelEngine.DevStudio;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.DevStudio;

/// <summary>The QUESTS tab's edit rules: links stay consistent through deletes and renames, a link that could never
/// unlock is refused, a broken quest is reported, not thrown, and never saved, a quest file follows its id only on save,
/// a quest saves or reverts on its own while every other edit stays pending, and undo steps back through the edits.</summary>
public sealed class QuestEditorStateTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dev-studio-quests").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    // Writes each quest to its own <id>.json in the test folder
    private void Write(params QuestDefinition[] quests)
    {
        foreach (var quest in quests) QuestWriter.Save(PathOf(quest.Id!), quest);
    }

    // The editor over the test folder, after writing the given quests into it
    private QuestEditorState Editor(params QuestDefinition[] quests)
    {
        Write(quests);
        TestContent.EnsureLoaded();
        return QuestEditorState.Load(_folder);
    }

    private string PathOf(string id) => QuestDocument.PathOf(_folder, id);

    private string? TitleOnDisk(string id) => QuestDocument.Load(PathOf(id)).Quest.Title;

    private static string? TitleOf(QuestEditorState editor, string id) => editor.Find(id)?.Quest.Title;

    private static QuestDefinition At(QuestDefinition quest, float x, float y)
    {
        quest.Pos = [x, y];
        return quest;
    }

    [Fact]
    public void DeletingAQuestRemovesEveryLinkToItButItsFileOnlyOnSave()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")),
            Quest("b", ["root", "a"], Break("stone")));

        editor.DeleteQuest("a");

        Assert.Null(editor.Find("a"));
        Assert.Equal(["root"], editor.Find("b")!.Quest.Parents);
        Assert.True(editor.IsQuestDirty("b"));
        Assert.True(File.Exists(PathOf("a")));
        Assert.Null(editor.Error);

        editor.Save();
        Assert.False(File.Exists(PathOf("a")));
        Assert.False(editor.Dirty);
    }

    [Fact]
    public void ALinkToItselfOrOneClosingACycleIsRefusedAndChangesNothing()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")), Quest("b", ["a"], Break("stone")));

        Assert.NotNull(editor.AddParent("a", "a"));
        Assert.NotNull(editor.AddParent("root", "b"));
        Assert.Null(editor.Find("root")!.Quest.Parents);
        Assert.False(editor.Dirty);
        Assert.Null(editor.Error);
    }

    [Fact]
    public void ALinkAddedThenRemovedComesAndGoesFromThePreview()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", null, Break("stone")));

        Assert.Null(editor.AddParent("a", "root"));
        Assert.Equal(["root"], editor.Find("a")!.Quest.Parents);
        Assert.Contains(editor.Preview.Of("root").Index, editor.Preview.Of("a").Parents);

        Assert.Null(editor.RemoveLink("a", "root"));
        Assert.Empty(editor.Preview.Of("a").Parents);
    }

    [Fact]
    public void AddingAParentRefusesADuplicateOrACycleAndOtherwiseLinks()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")), Quest("b", null, Break("stone")));

        Assert.NotNull(editor.AddParent("a", "root"));
        Assert.NotNull(editor.AddParent("root", "a"));
        Assert.False(editor.Dirty);

        Assert.Null(editor.AddParent("b", "a"));
        Assert.Equal(["a"], editor.Find("b")!.Quest.Parents);
    }

    [Fact]
    public void SwappingALinkTurnsItAroundUnlessAnotherPathWouldMakeACycle()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")),
            Quest("b", ["a", "root"], Break("stone")));

        Assert.NotNull(editor.SwapLink("b", "root"));
        Assert.Equal(["a", "root"], editor.Find("b")!.Quest.Parents);
        Assert.False(editor.Dirty);

        Assert.Null(editor.SwapLink("b", "a"));
        Assert.Equal(["root"], editor.Find("b")!.Quest.Parents);
        Assert.Contains("b", editor.Find("a")!.Quest.Parents!);
        Assert.Equal(("a", "b"), editor.SelectedLink);
        Assert.Null(editor.Error);
    }

    [Fact]
    public void ARemovedOrRenamedLinkKeepsTheLinkSelectionTruthful()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        editor.SelectLink("a", "root");

        editor.RenameQuest("root", "start");
        Assert.Equal(("a", "start"), editor.SelectedLink);

        Assert.Null(editor.RemoveLink("a", "start"));
        Assert.Null(editor.SelectedLink);
        Assert.NotNull(editor.RemoveLink("a", "start"));
    }

    [Fact]
    public void AQuestAddedFromAParentDependsOnItAndGetsItsOwnFileOnSave()
    {
        var editor = Editor(Quest("far", null, Break("stone")));

        var added = editor.AddQuest(new Vector2(25f, 1f), parent: "far");

        Assert.Equal(["far"], editor.Find(added)!.Quest.Parents);
        Assert.Null(editor.Error);
        editor.Save();
        Assert.True(File.Exists(PathOf(added)));
    }

    [Fact]
    public void RenamingAQuestFollowsItsChildrenAndRefusesATakenOrFileUnsafeId()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")), Quest("b", ["root"], Break("stone")));
        editor.Select("root");

        Assert.NotNull(editor.RenameQuest("root", "a"));
        Assert.NotNull(editor.RenameQuest("root", "bad/name"));
        Assert.Null(editor.RenameQuest("root", "start"));

        Assert.Equal("start", editor.SelectedQuestId);
        Assert.All(new[] { "a", "b" }, id => Assert.Equal(["start"], editor.Find(id)!.Quest.Parents));
        Assert.Null(editor.Error);
    }

    [Fact]
    public void ARenamedQuestsFileMovesOnSave()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        editor.RenameQuest("root", "start");
        Assert.True(File.Exists(PathOf("root")));

        editor.Save();

        Assert.False(File.Exists(PathOf("root")));
        Assert.True(File.Exists(PathOf("start")));
        Assert.Equal(["start"], QuestDocument.Load(PathOf("a")).Quest.Parents);
        Assert.False(editor.Dirty);
    }

    [Fact]
    public void AMovedQuestIsSnappedUnlessAsked()
    {
        var editor = Editor(Quest("root", null, Break("stone")));

        editor.MoveQuest("root", new Vector2(12.3f, -4.1f), snap: true);
        var snapped = editor.Preview.Of("root").Position;
        Assert.Equal(Vector2.Zero, snapped / QuestEditorState.SnapStep - Vector2.Round(snapped / QuestEditorState.SnapStep));
        Assert.True(Vector2.Distance(snapped, new Vector2(12.3f, -4.1f)) <= QuestEditorState.SnapStep);

        editor.MoveQuest("root", new Vector2(12.3f, -4.1f), snap: false);
        Assert.Equal(new Vector2(12.3f, -4.1f), editor.Preview.Of("root").Position);
    }

    [Fact]
    public void ABrokenEditIsReportedWithoutThrowingAndIsNeverSaved()
    {
        var editor = Editor(Quest("root", null, Break("stone")));

        editor.SetRequirementTarget("root", 0, "not_a_block");

        Assert.NotNull(editor.Error);
        Assert.Equal("root", editor.ErrorQuestId);
        Assert.Single(editor.Preview.Quests);
        var before = File.ReadAllText(PathOf("root"));
        editor.Save();
        editor.SaveQuest("root");
        Assert.Equal(before, File.ReadAllText(PathOf("root")));

        editor.SetRequirementTarget("root", 0, "block/stone");
        editor.Save();
        Assert.NotEqual(before, File.ReadAllText(PathOf("root")));
        Assert.False(editor.Dirty);
    }

    [Fact]
    public void EveryRequirementTypeKeepsExactlyTheOneTargetFieldItReads()
    {
        var editor = Editor(Quest("root", null, Obtain("stone")));
        var requirement = editor.Find("root")!.Quest.Requirements![0];

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
        var editor = Editor(At(Quest("root", null, Break("stone")), 1, 2), Quest("a", ["root"], Obtain("stone", 3)));

        Assert.False(editor.Dirty);
        Assert.All(editor.Preview.Quests, q => Assert.False(editor.IsQuestDirty(q.Id), q.Id));
    }

    [Fact]
    public void SavingOneQuestWritesOnlyItsFileAndLeavesTheOthersPending()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        editor.SetTitle("root", "Edited root");
        editor.SetTitle("a", "Edited a");

        editor.SaveQuest("a");

        Assert.Equal("Edited a", TitleOnDisk("a"));
        Assert.NotEqual("Edited root", TitleOnDisk("root"));
        Assert.False(editor.IsQuestDirty("a"));
        Assert.True(editor.IsQuestDirty("root"));
        Assert.True(editor.Dirty);
    }

    [Fact]
    public void AQuestSaveTheFilesCouldNotLoadIsRefused()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        editor.RenameQuest("root", "start");

        // On disk, a still names root: saving the rename alone would orphan it
        editor.SaveQuest("start");

        Assert.True(File.Exists(PathOf("root")));
        Assert.False(File.Exists(PathOf("start")));
        Assert.True(editor.IsQuestDirty("start"));
    }

    [Fact]
    public void RevertingAQuestRestoresItsSavedSelfAndTheLinksToIt()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        var savedPosition = editor.Preview.Of("root").Position;
        editor.RenameQuest("root", "start");
        editor.SetTitle("start", "Edited");
        editor.MoveQuest("start", savedPosition + new Vector2(3f, 2f), snap: true);
        editor.SetTitle("a", "Kept");

        editor.RevertQuest("start");

        Assert.Null(editor.Find("start"));
        Assert.False(editor.IsQuestDirty("root"));
        Assert.Equal(savedPosition, editor.Preview.Of("root").Position);
        Assert.Equal(["root"], editor.Find("a")!.Quest.Parents);
        Assert.Equal("Kept", editor.Find("a")!.Quest.Title);
        Assert.Null(editor.Error);
    }

    [Fact]
    public void RevertingAQuestThatWasNeverSavedRemovesIt()
    {
        var editor = Editor(Quest("root", null, Break("stone")));
        var added = editor.AddQuest(new Vector2(2f, 0f));
        editor.AddParent(added, "root");

        editor.RevertQuest(added);

        Assert.Null(editor.Find(added));
        Assert.False(editor.Dirty);
    }

    [Fact]
    public void RevertingAllBringsBackDeletedQuestsAndDropsUnsavedOnes()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        editor.DeleteQuest("a");
        editor.SetTitle("root", "Edited");
        editor.AddQuest(new Vector2(-8f, 0f));

        editor.RevertAll();

        Assert.False(editor.Dirty);
        Assert.Equal(2, editor.Documents.Count);
        Assert.NotNull(editor.Find("a"));
        Assert.NotEqual("Edited", TitleOf(editor, "root"));
    }

    [Fact]
    public void UndoStepsBackThroughEditsAndRedoStepsForwardAgain()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
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
        var editor = Editor(Quest("root", null, Break("stone")));
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
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
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
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        editor.DeleteQuest("root");
        Assert.True(editor.Undo());
        Assert.Equal(["root"], editor.Find("a")!.Quest.Parents);
        Assert.False(editor.Dirty);

        editor.SetTitle("a", "Edited");
        editor.RevertAll();
        Assert.True(editor.Undo());
        Assert.Equal("Edited", TitleOf(editor, "a"));
    }

    [Fact]
    public void UndoingAnEditThatWasSavedLeavesTheFileAndMarksTheQuestUnsaved()
    {
        var editor = Editor(Quest("root", null, Break("stone")));
        editor.SetTitle("root", "Saved title");
        editor.SaveQuest("root");

        Assert.True(editor.Undo());

        Assert.Equal("Saved title", TitleOnDisk("root"));
        Assert.True(editor.IsQuestDirty("root"));
        Assert.True(editor.Redo());
        Assert.False(editor.IsQuestDirty("root"));
    }

    [Fact]
    public void UndoingASavedAddLeavesItsFileForTheNextSaveToDelete()
    {
        var editor = Editor(Quest("root", null, Break("stone")));
        var added = editor.AddQuest(new Vector2(2f, 0f));
        editor.Save();

        Assert.True(editor.Undo());

        Assert.Null(editor.Find(added));
        Assert.True(editor.Dirty);
        Assert.True(File.Exists(PathOf(added)));
        editor.Save();
        Assert.False(File.Exists(PathOf(added)));
    }

    [Fact]
    public void UndoRestoresTheSelectionTheChangeWasMadeUnder()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
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
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")),
            Quest("b", ["a"], Break("stone")), Quest("c", ["a", "root"], Break("stone")));
        editor.SelectMany(["a", "b"]);

        Assert.Equal(2, editor.DeleteQuests(editor.SelectedQuestIds.ToList()));

        Assert.Null(editor.Find("a"));
        Assert.Null(editor.Find("b"));
        Assert.Equal(["root"], editor.Find("c")!.Quest.Parents);
        Assert.Empty(editor.SelectedQuestIds);
        Assert.Null(editor.Error);

        Assert.True(editor.Undo());
        Assert.NotNull(editor.Find("a"));
        Assert.NotNull(editor.Find("b"));
        Assert.Equal(["a", "root"], editor.Find("c")!.Quest.Parents);
        Assert.Equal(["a", "b"], editor.SelectedQuestIds);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void TogglingBuildsAndShrinksTheSelectionAndOnlyOneSelectedQuestIsEdited()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
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
        var editor = Editor(Quest("root", null, Break("stone")), At(Quest("a", null, Break("stone")), 1.25f, -2),
            At(Quest("b", null, Break("stone")), 4, -0.25f), At(Quest("c", null, Break("stone")), 10, 10));
        var grabbed = new[] { "a", "b", "c" }.ToDictionary(id => id, id => editor.Preview.Of(id).Position);
        var offsets = grabbed.ToDictionary(kv => kv.Key, kv => kv.Value - grabbed["a"]);
        var rootBefore = editor.Preview.Of("root").Position;

        editor.Gesture = "drag";
        editor.MoveQuests("a", grabbed["a"] + new Vector2(1.1f, 0.4f), grabbed, snap: true);
        editor.MoveQuests("a", grabbed["a"] + new Vector2(2.3f, 1.2f), grabbed, snap: true);
        editor.Gesture = null;

        var lead = editor.Preview.Of("a").Position;
        Assert.Equal(Vector2.Zero, lead / QuestEditorState.SnapStep - Vector2.Round(lead / QuestEditorState.SnapStep));
        foreach (var (id, offset) in offsets)
            Assert.True(Vector2.Distance(offset, editor.Preview.Of(id).Position - lead) < 1e-4f, id);
        Assert.Equal(rootBefore, editor.Preview.Of("root").Position);

        Assert.True(editor.Undo());
        Assert.All(grabbed, kv => Assert.Equal(kv.Value, editor.Preview.Of(kv.Key).Position));
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void AComponentFollowsLinksBothWaysAndLeavesOrphansOut()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")),
            Quest("b", ["root"], Break("stone")), Quest("far", ["b"], Break("stone")), Quest("orphan", null, Break("stone")));

        Assert.Equal(new[] { "a", "b", "far", "root" }, editor.ComponentOf("a").Order());
        Assert.Equal(["orphan"], editor.ComponentOf("orphan"));
    }

    [Fact]
    public void ARenamedQuestStaysSelected()
    {
        var editor = Editor(Quest("root", null, Break("stone")), Quest("a", ["root"], Break("stone")));
        editor.SelectMany(["root", "a"]);

        editor.RenameQuest("root", "start");

        Assert.Equal(["start", "a"], editor.SelectedQuestIds);
    }

    private string LabelPath(string name) => QuestLabelDocument.PathOf(_folder, name);

    [Fact]
    public void ALabelIsAddedEditedAndSavedToItsOwnFileWithItsLineBreaks()
    {
        var editor = Editor(Quest("root", null, Break("stone")));

        var name = editor.AddLabel(new Vector2(3.2f, -1.1f));
        Assert.Equal(name, editor.SelectedLabel);
        Assert.Null(editor.SelectedQuestId);
        editor.SetLabelText(name, "Two\nlines");
        editor.SetLabelSize(name, (int)QuestLabelSize.Large);
        editor.SetLabelColor(name, (int)QuestLabelColor.Gold);
        editor.MoveLabel(name, new Vector2(5.1f, 0.2f), snap: true);
        Assert.True(editor.IsLabelDirty(name));

        editor.Save();

        var saved = QuestLabelDocument.Load(LabelPath(name)).Label;
        Assert.Equal("Two\nlines", saved.Text);
        Assert.Equal(("large", "gold"), (saved.Size, saved.Color));
        Assert.Equal(new[] { 5f, 0f }, saved.Pos);
        Assert.False(editor.Dirty);
    }

    [Fact]
    public void ADefaultLabelSizeOrColorIsLeftOutOfItsFile()
    {
        var editor = Editor(Quest("root", null, Break("stone")));
        var name = editor.AddLabel(Vector2.Zero);
        editor.SetLabelColor(name, (int)QuestLabelColor.Red);
        editor.SetLabelColor(name, (int)QuestLabelColor.Gray);
        editor.SetLabelSize(name, (int)QuestLabelSize.Medium);

        editor.Save();

        var text = File.ReadAllText(LabelPath(name));
        Assert.DoesNotContain("\"size\"", text);
        Assert.DoesNotContain("\"color\"", text);
    }

    [Fact]
    public void ADeletedLabelLeavesItsFileUntilSaveAndUndoBringsItBack()
    {
        var editor = Editor(Quest("root", null, Break("stone")));
        var name = editor.AddLabel(Vector2.Zero);
        editor.Save();

        editor.DeleteLabel(name);
        Assert.Null(editor.FindLabel(name));
        Assert.True(File.Exists(LabelPath(name)));
        Assert.True(editor.Dirty);

        Assert.True(editor.Undo());
        Assert.NotNull(editor.FindLabel(name));
        Assert.False(editor.Dirty);

        editor.DeleteLabel(name);
        editor.Save();
        Assert.False(File.Exists(LabelPath(name)));
    }

    [Fact]
    public void RevertingALabelPutsBackItsFileOrRemovesAnUnsavedOne()
    {
        var editor = Editor(Quest("root", null, Break("stone")));
        var saved = editor.AddLabel(Vector2.Zero, "Kept");
        editor.Save();
        editor.SetLabelText(saved, "Changed");
        var unsaved = editor.AddLabel(Vector2.One);

        editor.RevertLabel(saved);
        editor.RevertLabel(unsaved);

        Assert.Equal("Kept", editor.FindLabel(saved)!.Label.Text);
        Assert.Null(editor.FindLabel(unsaved));
        Assert.False(editor.Dirty);
    }

    [Fact]
    public void SelectingAQuestOrALabelClearsTheOther()
    {
        var editor = Editor(Quest("root", null, Break("stone")));
        var name = editor.AddLabel(Vector2.Zero);

        editor.Select("root");
        Assert.Null(editor.SelectedLabel);

        editor.SelectLabel(name);
        Assert.Empty(editor.SelectedQuestIds);
        Assert.Equal(name, editor.SelectedLabel);
    }

    [Theory]
    [InlineData("obtain", IconKind.Item, true)]
    [InlineData("craft", IconKind.Block, true)]
    [InlineData("obtain", IconKind.Entity, false)]
    [InlineData("break", IconKind.Block, true)]
    [InlineData("place", IconKind.Item, false)]
    [InlineData("kill", IconKind.Entity, true)]
    [InlineData("kill", IconKind.Block, false)]
    public void ARequirementTakesOnlyTheKindsItsTypeCanName(string type, IconKind kind, bool accepts) =>
        Assert.Equal(accepts, QuestEditorState.Accepts(type, kind));

    [Fact]
    public void ADroppedEntryAddsARequirementItsOwnTypeAcceptsAsOneStep()
    {
        var editor = Editor(Quest("root", null, Break("stone")));

        foreach (var (kind, target) in new[] { (IconKind.Item, "item/flint"), (IconKind.Block, "dirt"), (IconKind.Entity, "slime") })
        {
            var type = QuestEditorState.RequirementTypeFor(kind);
            Assert.True(QuestEditorState.Accepts(type, kind));
            editor.AddRequirement("root", type, target);
            Assert.Equal(target, QuestEditorState.TargetOf(editor.Find("root")!.Quest.Requirements![^1]));
        }

        Assert.Equal(4, editor.Find("root")!.Quest.Requirements!.Count);
        Assert.Null(editor.Error);
        Assert.True(editor.Undo());
        Assert.Equal(3, editor.Find("root")!.Quest.Requirements!.Count);
    }

    [Fact]
    public void AQuestAddedFromAnEntityAsksToKillItAndCompiles()
    {
        var editor = Editor(Quest("root", null, Break("stone")));

        var added = editor.AddQuest(Vector2.One, title: "Slime", requirement: QuestEditorState.MakeRequirement("kill", "slime"));

        var requirement = Assert.Single(editor.Find(added)!.Quest.Requirements!);
        Assert.Equal(("kill", "slime"), (requirement.Type, requirement.Entity));
        Assert.Null(editor.Error);
    }

    [Fact]
    public void AnAddedQuestCompilesAsItIsWithAFreshIdThatNoPendingFileHolds()
    {
        var editor = Editor(Quest("quest_1", null, Break("stone")));
        editor.DeleteQuest("quest_1");

        var first = editor.AddQuest(new Vector2(3f, 1f));
        var second = editor.AddQuest(new Vector2(4f, 1f));

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("quest_1", new[] { first, second });
        Assert.Equal(2, editor.Preview.Quests.Count);
        Assert.Null(editor.Error);
    }
}
