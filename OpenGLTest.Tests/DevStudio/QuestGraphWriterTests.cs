using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;
using VoxelEngine.DevStudio;
using Xunit;

namespace VoxelEngine.Tests.DevStudio;

/// <summary>The quest graph writer: what it writes reads back to the same graph, keeps the header comment, and an
/// unedited shipped graph saves back to its own text, so a save shows only the edit in a diff.</summary>
public sealed class QuestGraphWriterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dev-studio-writer").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void AWrittenGraphReadsBackTheSameWithItsHeaderComment()
    {
        var definition = new QuestGraphDefinition
        {
            Title = "Odd \"Quotes\" & Friends",
            Origin = [-2.5f, 3],
            Quests =
            [
                new()
                {
                    Id = "root", Title = "It's here", Description = "Line with a \\ and \"quotes\".", Icon = "item/spear",
                    Pos = [0, 0], Shape = "circle", Readable = false, AnyOf = true,
                    Requirements =
                    [
                        new() { Type = "obtain", Tag = "logs", Label = "Any Log", Count = 4 },
                        new() { Type = "kill", Entity = "slime" },
                    ],
                },
                new() { Id = "next", Icon = "stone", Pos = [1.5f, -0.25f], Parents = ["root"], Requirements = [new() { Type = "break", Block = "stone" }] },
            ],
        };
        var path = Path.Combine(_folder, "g.json");
        QuestGraphWriter.Save(new QuestGraphDocument(path, definition, ["// a header", "// on two lines"]));

        var read = QuestGraphDocument.Load(path);

        Assert.Equal(["// a header", "// on two lines"], read.Header);
        Assert.Equal(JsonSerializer.Serialize(definition), JsonSerializer.Serialize(read.Definition));
    }

    [Fact]
    public void AnUneditedShippedGraphSavesBackToItsOwnText()
    {
        TestContent.EnsureLoaded();
        var folder = Path.Combine(TestContent.FindAssetsRoot(), "data", "quests");
        var editor = QuestEditorState.Load(folder);
        Assert.NotEmpty(editor.Documents);
        Assert.Null(editor.Error);

        foreach (var document in editor.Documents)
            Assert.Equal(Normalize(File.ReadAllText(document.Path)), Normalize(QuestGraphWriter.Format(document)));
    }

    [Fact]
    public void ShippedGraphsCompileToTheSameBookAfterARewrite()
    {
        TestContent.EnsureLoaded();
        var source = QuestEditorState.Load(Path.Combine(TestContent.FindAssetsRoot(), "data", "quests"));
        foreach (var document in source.Documents)
            File.WriteAllText(Path.Combine(_folder, Path.GetFileName(document.Path)), QuestGraphWriter.Format(document));

        var original = QuestLoader.Compile([.. source.Documents.Select(d => (d.Name, d.Definition, d.Path))]);
        var rewritten = QuestLoader.ReadQuests(_folder);

        Assert.Equal(original.Graphs, rewritten.Graphs);
        Assert.Equal(original.Quests.Count, rewritten.Quests.Count);
        foreach (var quest in original.Quests)
        {
            Assert.True(rewritten.TryGet(quest.Id, out var again), quest.Id);
            Assert.Equal((quest.Title, quest.Description, quest.IconId, quest.Position, quest.Shape, quest.Readable, quest.AnyOf),
                (again.Title, again.Description, again.IconId, again.Position, again.Shape, again.Readable, again.AnyOf));
            Assert.Equal(quest.Parents.Select(p => original.Quests[p].Id), again.Parents.Select(p => rewritten.Quests[p].Id));
            Assert.Equal(quest.Requirements.Select(r => (r.Kind, r.Id, r.Entity, r.Count, r.TargetName)),
                again.Requirements.Select(r => (r.Kind, r.Id, r.Entity, r.Count, r.TargetName)));
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
