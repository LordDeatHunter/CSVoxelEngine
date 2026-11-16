using System;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Quests;

namespace VoxelEngine.Tests.Support;

/// <summary>Hand-built quest graphs for the quest tests, compiled through the real loader against the shipped blocks,
/// items and entities (so <see cref="TestContent"/> must be loaded).</summary>
internal static class QuestFixtures
{
    internal static QuestDefinition Quest(string id, string[]? parents,
        params QuestRequirementDefinition[] requirements) =>
        new() { Id = id, Icon = "stone", Pos = [0, 0], Parents = parents, Requirements = [.. requirements] };

    internal static QuestDefinition AnyOfQuest(string id, string[]? parents,
        params QuestRequirementDefinition[] requirements)
    {
        var quest = Quest(id, parents, requirements);
        quest.AnyOf = true;
        return quest;
    }

    internal static QuestRequirementDefinition Obtain(string item, int count = 1) =>
        new() { Type = "obtain", Item = item, Count = count };

    internal static QuestRequirementDefinition ObtainTag(string tag, int count = 1) =>
        new() { Type = "obtain", Tag = tag, Count = count };

    internal static QuestRequirementDefinition Break(string block, int count = 1) =>
        new() { Type = "break", Block = block, Count = count };

    internal static QuestRequirementDefinition Craft(string item, int count = 1) =>
        new() { Type = "craft", Item = item, Count = count };

    internal static QuestRequirementDefinition Kill(string entity, int count = 1) =>
        new() { Type = "kill", Entity = entity, Count = count };

    /// <summary><paramref name="quests"/>, each as its own <c>&lt;id&gt;.json</c>, compiled.</summary>
    internal static QuestBook Book(params QuestDefinition[] quests)
    {
        TestContent.EnsureLoaded();
        return QuestLoader.Compile([.. quests.Select(q => (q, q.Id + ".json"))]);
    }

    internal static QuestType Of(this QuestBook book, string id) => book.Quests.Single(q => q.Id == id);

    internal static ushort Id(string name) =>
        ContentFiles.TryResolve(name, out var id) ? id : throw new ArgumentException($"no content named {name}");
}
