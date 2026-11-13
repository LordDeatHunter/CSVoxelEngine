using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Quests;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using Xunit;

namespace VoxelEngine.Tests.Networking.ServerSide;

/// <summary>A connected player's quests on the server: the newcomer's starter kit completes nothing, a later gain and a
/// craft replayed from the client's intent both advance its own log, and the progress rides its save.</summary>
public sealed class ServerPlayerQuestTests
{
    public ServerPlayerQuestTests() => TestContent.EnsureLoaded();

    [Fact]
    public void TheStarterKitCountsForNothingButLaterGainsDo()
    {
        var player = new ServerPlayer(1, "quester", Vector3.Zero);
        player.Restore(null, allowCreative: false);
        var saved = player.Quests.Export();
        Assert.Empty(saved.Completed!);
        Assert.Empty(saved.Progress!);

        var (quest, r) = QuestRegistry.Book.Quests
            .Where(q => q.Parents.Count == 0)
            .SelectMany(q => q.Requirements.Select((req, i) => (q, i)))
            .First(p => p.q.Requirements[p.i].Kind == QuestRequirementKind.Obtain);
        player.Inventory.Add(quest.Requirements[r].Id);

        Assert.True(player.Quests.ProgressOf(quest, r) > 0);
        Assert.True(player.Quests.Changed);
        var restored = new ServerPlayer(2, "quester", Vector3.Zero);
        restored.Restore(player.Capture(), allowCreative: false);
        Assert.Equal(player.Quests.ProgressOf(quest, r), restored.Quests.ProgressOf(quest, r));
    }

    [Fact]
    public void ACraftIntentAdvancesThePlayersOwnLog()
    {
        var player = new ServerPlayer(1, "crafter", Vector3.Zero);
        player.Restore(null, allowCreative: false);
        // Unlock the roots' children, then take any open craft requirement whose recipe has no tag ingredient
        foreach (var root in QuestRegistry.Book.Quests.Where(q => q.Parents.Count == 0))
            player.Quests.ForceComplete(root);
        var (quest, r) = QuestRegistry.Book.Quests
            .Where(q => player.Quests.IsUnlocked(q) && !player.Quests.IsComplete(q))
            .SelectMany(q => q.Requirements.Select((req, i) => (q, i)))
            .First(p => p.q.Requirements[p.i].Kind == QuestRequirementKind.Craft
                        && ContentRegistry.RecipeForResult(p.q.Requirements[p.i].Id) is { } candidate
                        && candidate.Ingredients.All(i => !i.IsTag));
        var recipe = ContentRegistry.RecipeForResult(quest.Requirements[r].Id)!;
        foreach (var ingredient in recipe.Ingredients)
            player.Inventory.Add(ingredient.Id, ingredient.Count);

        Assert.True(InventoryIntentRules.Apply(player.Inventory,
            InventoryIntent.Craft(recipe.Name, 1, [.. recipe.Ingredients.Select(i => i.Id)]), creative: false));
        Assert.True(player.Quests.ProgressOf(quest, r) > 0);
    }
}
