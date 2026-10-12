using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The "Inventory full" warning's rule (<see cref="DropManager.BlockedCollectors"/>): a drop within
/// reach that the pool refuses is reported for that collector, a drop out of reach or one the pool
/// accepts is not, and a goblin's own refusal never counts as the player's.
/// </summary>
public sealed class InventoryFullWarningTests
{
    public InventoryFullWarningTests() => TestContent.EnsureLoaded();

    private static byte Stone => ContentRegistry.RequireBlock("stone").Id;

    [Fact]
    public void ADropInReachThePoolRefusesIsReported()
    {
        var chunks = MobTestWorld.Floor(Stone);
        var drops = new DropManager();
        var spot = new Vector3(8.5f, 1.5f, 8.5f);
        drops.SpawnRestored(Stone, spot, Vector3.Zero, age: 5f);

        var eye = spot + Vector3.UnitY * DropManager.CollectHeightOffset;
        var collectors = new[] { new DropCollector(eye, _ => false) };
        drops.Update(1f / 60, collectors, chunks);
        Assert.True(drops.PickupBlocked, "a refused drop in reach was not reported");
        Assert.Equal([0], drops.BlockedCollectors);

        // With room again the report clears, and a drop out of reach never reports
        var accepting = new[] { new DropCollector(eye, _ => true) };
        drops.Update(1f / 60, accepting, chunks);
        Assert.False(drops.PickupBlocked);

        var far = new[] { new DropCollector(spot + new Vector3(40f, 0f, 0f), _ => false) };
        drops.Update(1f / 60, far, chunks);
        Assert.False(drops.PickupBlocked);
    }

    [Fact]
    public void AGoblinsRefusalIsNotThePlayersWarning()
    {
        var chunks = MobTestWorld.Floor(Stone);
        var drops = new DropManager();
        var spot = new Vector3(8.5f, 1.5f, 8.5f);
        drops.SpawnRestored(Stone, spot, Vector3.Zero, age: 5f);

        // The player is far away; a goblin standing on the drop refuses it
        var eye = spot + Vector3.UnitY * DropManager.CollectHeightOffset;
        var collectors = new[]
        {
            new DropCollector(spot + new Vector3(60f, 0f, 0f), _ => true),
            new DropCollector(eye, _ => false),
        };
        drops.Update(1f / 60, collectors, chunks);
        Assert.False(drops.PickupBlocked, "a goblin's refusal warned the player");
        Assert.Equal([1], drops.BlockedCollectors);
    }
}
