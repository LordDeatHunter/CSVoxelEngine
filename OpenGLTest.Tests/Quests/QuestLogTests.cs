using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Networking;
using VoxelEngine.Quests;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;
using Xunit;
using static VoxelEngine.Tests.Support.QuestFixtures;

namespace VoxelEngine.Tests.Quests;

/// <summary>The quest log's rules: unlocking by parents, progress only while unlocked, obtain read on a gain (never
/// polled) and once on unlock, completion raised once, and the by-id save and wire round trips.</summary>
public class QuestLogTests
{
    [Fact]
    public void AChildStaysLockedUntilEveryParentIsComplete()
    {
        var book = Book(Quest("a", null, Break("stone")), Quest("b", null, Break("dirt")),
            Quest("child", ["a", "b"], Break("sand")));
        var log = new QuestLog(book);
        var child = book.Of("child");

        Assert.False(log.IsVisible(child));
        log.OnBroke(Id("stone"));
        Assert.False(log.IsUnlocked(child));
        log.OnBroke(Id("dirt"));
        Assert.True(log.IsUnlocked(child));
        Assert.True(log.IsVisible(child));
    }

    [Fact]
    public void ALockedQuestMakesNoProgress()
    {
        var book = Book(Quest("root", null, Break("dirt")), Quest("child", ["root"], Break("stone", 2)));
        var log = new QuestLog(book);
        log.OnBroke(Id("stone"));
        log.OnBroke(Id("stone"));
        log.OnBroke(Id("dirt"));

        Assert.True(log.IsUnlocked(book.Of("child")));
        Assert.Equal(0, log.ProgressOf(book.Of("child"), 0));
    }

    [Fact]
    public void OneEventNeverAlsoCountsForTheQuestItUnlocks()
    {
        var book = Book(Quest("root", null, Break("stone")), Quest("child", ["root"], Break("stone")));
        var log = new QuestLog(book);
        log.OnBroke(Id("stone"));

        Assert.True(log.IsComplete(book.Of("root")));
        Assert.False(log.IsComplete(book.Of("child")));
    }

    [Fact]
    public void ObtainReadsTheHeldStackWhenAnItemIsGained()
    {
        var book = Book(Quest("q", null, Obtain("item/flint", 3)));
        var inventory = Inventory.Empty();
        var log = new QuestLog(book);
        log.Bind(inventory);

        inventory.Add(Id("item/flint"), 2);
        Assert.Equal(2, log.ProgressOf(book.Of("q"), 0));
        Assert.False(log.IsComplete(book.Of("q")));
        inventory.Add(Id("item/flint"));
        Assert.True(log.IsComplete(book.Of("q")));
    }

    [Fact]
    public void WhatWasHeldBeforeBindingCountsOnlyAtTheNextGain()
    {
        var book = Book(Quest("q", null, Obtain("item/flint", 3)));
        var inventory = Inventory.Empty();
        inventory.Add(Id("item/flint"), 5);
        var log = new QuestLog(book);
        log.Bind(inventory);

        Assert.False(log.IsComplete(book.Of("q")));
        inventory.Add(Id("item/flint"));
        Assert.True(log.IsComplete(book.Of("q")));
    }

    [Fact]
    public void AQuestThatUnlocksCountsWhatIsAlreadyHeldAndCanCascade()
    {
        var book = Book(Quest("root", null, Break("dirt")), Quest("child", ["root"], Obtain("item/flint", 2)),
            Quest("grandchild", ["child"], Obtain("item/flint")));
        var inventory = Inventory.Empty();
        var log = new QuestLog(book);
        log.Bind(inventory);
        inventory.Add(Id("item/flint"), 2);
        var completed = new List<string>();
        log.Completed += q => completed.Add(q.Id);

        log.OnBroke(Id("dirt"));

        Assert.Equal(["root", "child", "grandchild"], completed);
    }

    [Fact]
    public void CompletedIsRaisedOncePerQuest()
    {
        var book = Book(Quest("q", null, Break("stone")));
        var log = new QuestLog(book);
        var raised = 0;
        log.Completed += _ => raised++;

        log.OnBroke(Id("stone"));
        log.OnBroke(Id("stone"));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void EveryRequirementMustBeMet()
    {
        var book = Book(Quest("q", null, Break("stone"), Kill("slime", 2)));
        var log = new QuestLog(book);
        log.OnBroke(Id("stone"));
        log.OnKilled("slime");
        Assert.False(log.IsComplete(book.Of("q")));
        log.OnKilled("goblin");
        Assert.False(log.IsComplete(book.Of("q")));
        log.OnKilled("slime");
        Assert.True(log.IsComplete(book.Of("q")));
    }

    [Fact]
    public void ACraftThroughTheBoundInventoryCountsItsResults()
    {
        TestContent.EnsureLoaded();
        var recipe = ContentRegistry.CraftingRecipes.First(r => r.Ingredients.All(i => !i.IsTag));
        var result = SaveNames.NameOf(recipe.ResultId)!;
        var book = Book(Quest("q", null, Craft(result, recipe.ResultCount)));
        var inventory = Inventory.Empty();
        var log = new QuestLog(book);
        log.Bind(inventory);
        foreach (var ingredient in recipe.Ingredients)
            inventory.Add(ingredient.Id, ingredient.Count);

        Assert.True(inventory.TryCraft(recipe));
        Assert.True(log.IsComplete(book.Of("q")));
    }

    [Fact]
    public void AnUnboundInventorysCraftsCountForNothing()
    {
        var book = Book(Quest("q", null, Craft("torch")));
        var log = new QuestLog(book);
        Inventory.Empty().NoteCrafted(Id("torch"), 4);
        Assert.False(log.IsComplete(book.Of("q")));
    }

    [Fact]
    public void ProgressRoundTripsByIdAndDropsUnknownQuests()
    {
        var book = Book(Quest("done", null, Break("dirt")), Quest("open", ["done"], Break("stone", 5)));
        var log = new QuestLog(book);
        log.OnBroke(Id("dirt"));
        log.OnBroke(Id("stone"));
        log.OnBroke(Id("stone"));
        var saved = log.Export();
        saved.Completed!.Add("removed_quest");
        saved.Progress!["also_removed"] = [3];

        var restored = new QuestLog(book);
        var raised = 0;
        restored.Completed += _ => raised++;
        restored.Import(saved);

        Assert.True(restored.IsComplete(book.Of("done")));
        Assert.Equal(2, restored.ProgressOf(book.Of("open"), 0));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void TheWireSnapshotCarriesProgressAndTellsAMirrorWhatIsNewlyComplete()
    {
        var book = Book(Quest("a", null, Break("dirt")), Quest("b", ["a"], Break("stone", 3)));
        var server = new QuestLog(book);
        var mirror = new QuestLog(book);
        mirror.Import(QuestStateCodec.Decode(QuestStateCodec.Encode(server.Export())));

        server.OnBroke(Id("dirt"));
        server.OnBroke(Id("stone"));
        var snapshot = QuestStateCodec.Decode(QuestStateCodec.Encode(server.Export()));

        Assert.Equal(["a"], mirror.NewlyCompletedIn(snapshot).Select(q => q.Id));
        mirror.Import(snapshot);
        Assert.Empty(mirror.NewlyCompletedIn(snapshot));
        Assert.Equal(1, mirror.ProgressOf(book.Of("b"), 0));
    }
}
