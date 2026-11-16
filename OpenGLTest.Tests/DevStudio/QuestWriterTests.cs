using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;
using VoxelEngine.DevStudio;
using Xunit;

namespace VoxelEngine.Tests.DevStudio;

/// <summary>The quest writer: what it writes reads back to the same quest under the file's name, and an unedited
/// shipped quest saves back to its own text, so a save shows only the edit in a diff.</summary>
public sealed class QuestWriterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dev-studio-writer").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void AWrittenQuestReadsBackTheSameUnderItsFileName()
    {
        var quest = new QuestDefinition
        {
            Id = "root", Title = "It's \"here\"", Description = "Line with a \\ and a tab\t.", Icon = "item/spear",
            Pos = [-2.5f, 0.125f], Shape = "circle", Parents = ["a", "b"], Reveal = "anyParentDone", Readable = false,
            AnyOf = true,
            Requirements =
            [
                new() { Type = "obtain", Tag = "logs", Label = "Any Log", Count = 4 },
                new() { Type = "kill", Entity = "slime" },
            ],
        };
        var path = QuestDocument.PathOf(_folder, "root");
        QuestWriter.Save(path, quest);

        var read = QuestDocument.Load(path).Quest;

        Assert.Equal("root", read.Id);
        Assert.Equal(JsonSerializer.Serialize(quest), JsonSerializer.Serialize(read));
        Assert.DoesNotContain("\"id\"", File.ReadAllText(path));
    }

    [Fact]
    public void AWrittenLabelReadsBackTheSameUnderItsFileName()
    {
        var label = new QuestLabelDefinition { Id = "title", Text = "Line \"one\"\nline two", Pos = [1.5f, -2], Size = "small", Color = "blue" };
        var path = QuestLabelDocument.PathOf(_folder, "title");
        QuestWriter.SaveLabel(path, label);

        var read = QuestLabelDocument.Load(path).Label;

        Assert.Equal("title", read.Id);
        Assert.Equal(JsonSerializer.Serialize(label), JsonSerializer.Serialize(read));
    }

    [Fact]
    public void AnUneditedShippedQuestSavesBackToItsOwnText()
    {
        TestContent.EnsureLoaded();
        var folder = Path.Combine(TestContent.FindAssetsRoot(), "data", "quests");
        var editor = QuestEditorState.Load(folder);
        Assert.NotEmpty(editor.Documents);
        Assert.Null(editor.Error);

        foreach (var document in editor.Documents)
            Assert.Equal(Normalize(File.ReadAllText(QuestDocument.PathOf(folder, document.Id))),
                Normalize(QuestWriter.Format(document.Quest)));
        Assert.NotEmpty(editor.Labels);
        foreach (var document in editor.Labels)
            Assert.Equal(Normalize(File.ReadAllText(QuestLabelDocument.PathOf(folder, document.Id))),
                Normalize(QuestWriter.FormatLabel(document.Label)));
    }

    [Fact]
    public void ShippedQuestsCompileToTheSameBookAfterARewrite()
    {
        TestContent.EnsureLoaded();
        var source = QuestEditorState.Load(Path.Combine(TestContent.FindAssetsRoot(), "data", "quests"));
        foreach (var document in source.Documents)
            QuestWriter.Save(QuestDocument.PathOf(_folder, document.Id), document.Quest);

        var original = QuestLoader.ReadQuests(source.Folder);
        var rewritten = QuestLoader.ReadQuests(_folder);

        Assert.Equal(original.Quests.Count, rewritten.Quests.Count);
        foreach (var quest in original.Quests)
        {
            Assert.True(rewritten.TryGet(quest.Id, out var again), quest.Id);
            Assert.Equal((quest.Title, quest.Description, quest.IconId, quest.Position, quest.Shape, quest.Reveal, quest.Readable, quest.AnyOf),
                (again.Title, again.Description, again.IconId, again.Position, again.Shape, again.Reveal, again.Readable, again.AnyOf));
            Assert.Equal(quest.Parents.Select(p => original.Quests[p].Id), again.Parents.Select(p => rewritten.Quests[p].Id));
            Assert.Equal(quest.Requirements.Select(r => (r.Kind, r.Id, r.Entity, r.Count, r.TargetName)),
                again.Requirements.Select(r => (r.Kind, r.Id, r.Entity, r.Count, r.TargetName)));
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
