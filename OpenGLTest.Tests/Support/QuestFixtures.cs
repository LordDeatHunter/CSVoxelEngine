using System;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;

namespace VoxelEngine.Tests.Support;

/// <summary>Hand-built quest graphs for the quest tests, compiled through the real loader against the shipped blocks,
/// items and entities (so <see cref="TestContent"/> must be loaded).</summary>
internal static class QuestFixtures
{
    internal static QuestGraphDefinition.QuestDefinition Quest(string id, string[]? parents,
        params QuestGraphDefinition.RequirementDefinition[] requirements) =>
        new() { Id = id, Icon = "stone", Pos = [0, 0], Parents = parents, Requirements = [.. requirements] };

    internal static QuestGraphDefinition.RequirementDefinition Obtain(string item, int count = 1) =>
        new() { Type = "obtain", Item = item, Count = count };

    internal static QuestGraphDefinition.RequirementDefinition Break(string block, int count = 1) =>
        new() { Type = "break", Block = block, Count = count };

    internal static QuestGraphDefinition.RequirementDefinition Craft(string item, int count = 1) =>
        new() { Type = "craft", Item = item, Count = count };

    internal static QuestGraphDefinition.RequirementDefinition Kill(string entity, int count = 1) =>
        new() { Type = "kill", Entity = entity, Count = count };

    /// <summary>One graph file holding <paramref name="quests"/>, compiled.</summary>
    internal static QuestBook Book(params QuestGraphDefinition.QuestDefinition[] quests)
    {
        TestContent.EnsureLoaded();
        return QuestLoader.Compile([("test", new QuestGraphDefinition { Quests = [.. quests] }, "test.json")]);
    }

    internal static QuestType Of(this QuestBook book, string id) => book.Quests.Single(q => q.Id == id);

    internal static ushort Id(string name) =>
        ContentFiles.TryResolve(name, out var id) ? id : throw new ArgumentException($"no content named {name}");
}
