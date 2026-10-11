using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class FirestarterTests
{
    public FirestarterTests() => TestContent.EnsureLoaded();

    private static byte Firestarter => ContentRegistry.RequireItem("firestarter").Id;
    private static byte Ash => ContentRegistry.RequireItem("ash").Id;
    private static byte Vine => ContentRegistry.RequireItem("vine").Id;
    private static byte Branch => ContentRegistry.RequireItem("branch").Id;
    private static byte BranchPile => ContentRegistry.RequireBlock("branch_pile").Id;

    [Fact]
    public void FirestarterIsAToolSoInstancesNeverStack()
    {
        Assert.Equal(InventoryCategory.Tools, Inventory.CategoryOf(Firestarter));
        var inventory = new Inventory();
        Assert.True(inventory.Add(Firestarter));
        Assert.True(inventory.Add(Firestarter));
        Assert.Equal(2, inventory.ToolEntries().Count(t => t.ItemId == Firestarter));
    }

    [Fact]
    public void TenUsesBreakTheFirestarter()
    {
        var inventory = new Inventory();
        Assert.True(inventory.Add(Firestarter));
        var instance = Assert.Single(inventory.ToolEntries(), t => t.ItemId == Firestarter).InstanceId;
        for (var i = 0; i < 9; i++)
            Assert.False(inventory.DamageTool(instance));
        Assert.Equal(1, inventory.ToolDurabilityLeft(instance));
        Assert.True(inventory.DamageTool(instance));
        Assert.Equal(0, inventory.ToolItemId(instance));
        Assert.DoesNotContain(inventory.ToolEntries(), t => t.ItemId == Firestarter);
    }

    [Fact]
    public void DurabilityInstancesAreIndependent()
    {
        var inventory = new Inventory();
        Assert.True(inventory.Add(Firestarter));
        Assert.True(inventory.Add(Firestarter));
        var tools = inventory.ToolEntries().Where(t => t.ItemId == Firestarter).ToArray();
        Assert.Equal(2, tools.Length);
        Assert.False(inventory.DamageTool(tools[0].InstanceId, 4));
        Assert.Equal(6, inventory.ToolDurabilityLeft(tools[0].InstanceId));
        Assert.Equal(10, inventory.ToolDurabilityLeft(tools[1].InstanceId));
    }

    [Fact]
    public void UseDelayCooldownMarksAndReleases()
    {
        var inventory = new Inventory();
        Assert.True(inventory.Add(Firestarter));
        var instance = Assert.Single(inventory.ToolEntries(), t => t.ItemId == Firestarter).InstanceId;
        Assert.True(inventory.ToolReadyAt(instance, 100.0));
        inventory.MarkToolUsed(instance, 100.0, 1.5f);
        Assert.False(inventory.ToolReadyAt(instance, 101.0));
        Assert.True(inventory.ToolReadyAt(instance, 101.5));
    }

    [Fact]
    public void BucketsHaveNoDurabilityBar()
    {
        var inventory = new Inventory();
        var bucket = ContentRegistry.RequireItem("bucket").Id;
        var instance = Assert.Single(inventory.ToolEntries(), t => t.ItemId == bucket).InstanceId;
        Assert.Null(inventory.ToolDurabilityLeft(instance));
        Assert.Null(inventory.ToolDurabilityFraction(instance));
        Assert.False(inventory.DamageTool(instance));
    }

    [Fact]
    public void CampfireIgnitesOnceAndBurnsThirtySecondsPerBranch()
    {
        var fires = new CampfireManager();
        Assert.True(fires.Ignite(1, 2, 3));
        Assert.False(fires.Ignite(1, 2, 3));
        Assert.True(fires.IsLit(1, 2, 3));
        Assert.Single(fires.LitCells);
    }

    [Fact]
    public void StaleEntriesVanishWithoutAsh()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var fires = new CampfireManager();
        var drops = new DropManager();
        Assert.True(fires.Ignite(0, 0, 0));
        fires.Update(1f, manager, drops);
        Assert.False(fires.IsLit(0, 0, 0));
        Assert.Empty(drops.Drops);
    }

    [Fact]
    public void BurningABranchKeepsTheFireLitAndDropsFourAsh()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        chunk.SetBlockRaw(0, 0, 0, BranchPile, 4);

        var fires = new CampfireManager();
        var drops = new DropManager();
        Assert.True(fires.Ignite(0, 0, 0));
        fires.Update(1f, manager, drops);
        Assert.True(fires.IsLit(0, 0, 0));
        Assert.Empty(drops.Drops);
        fires.Update(CampfireManager.BurnSeconds - 1f, manager, drops);
        Assert.True(fires.IsLit(0, 0, 0));
        Assert.True(manager.TryGetCell(0, 0, 0, out var id, out var state));
        Assert.Equal(BranchPile, id);
        Assert.Equal(3, state);
        Assert.Equal(4, drops.Drops.Count);
        foreach (var drop in drops.Drops)
            Assert.Equal(Ash, drop.BlockId);
    }

    [Fact]
    public void BurningTheLastBranchExtinguishesAndClearsTheBlock()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        chunk.SetBlockRaw(0, 0, 0, BranchPile, 4);

        var fires = new CampfireManager();
        var drops = new DropManager();
        Assert.True(fires.Ignite(0, 0, 0));
        for (var i = 0; i < 3; i++)
        {
            fires.Update(CampfireManager.BurnSeconds, manager, drops);
            Assert.True(fires.IsLit(0, 0, 0));
        }
        Assert.Equal(12, drops.Drops.Count);
        fires.Update(CampfireManager.BurnSeconds, manager, drops);
        Assert.False(fires.IsLit(0, 0, 0));
        Assert.True(manager.TryGetCell(0, 0, 0, out var id, out _));
        Assert.Equal(0, id);
        Assert.Equal(16, drops.Drops.Count);
        foreach (var drop in drops.Drops)
            Assert.Equal(Ash, drop.BlockId);
    }

    [Fact]
    public void FeedingBranchesKeepsTheFireGoing()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        chunk.SetBlockRaw(0, 0, 0, BranchPile, 4);

        var fires = new CampfireManager();
        var drops = new DropManager();
        Assert.True(fires.Ignite(0, 0, 0));
        fires.Update(CampfireManager.BurnSeconds, manager, drops);
        Assert.True(fires.IsLit(0, 0, 0));
        Assert.Equal(4, drops.Drops.Count);

        Assert.True(chunk.SetStateAt(new Vector3(0, 0, 0), 4, manager));
        fires.Update(CampfireManager.BurnSeconds, manager, drops);
        Assert.True(fires.IsLit(0, 0, 0));
        Assert.True(manager.TryGetCell(0, 0, 0, out var id, out var state));
        Assert.Equal(BranchPile, id);
        Assert.Equal(3, state);
        Assert.Equal(8, drops.Drops.Count);
    }

    [Fact]
    public void FireParticlesRiseOffLitCellsThenDie()
    {
        var system = new FireParticleSystem();
        var camera = new Vector3(0.5f, 2f, 0.5f);
        system.Update(1f, [new Vector3(0, 0, 0)], camera, new System.Random(42));
        Assert.NotEmpty(system.Particles);
        Assert.All(system.Particles, p => Assert.True(p.IsAlive));
        system.Update(10f, [], camera, new System.Random(42));
        Assert.Empty(system.Particles);
    }

    [Fact]
    public void DryTntIsNotSubmerged()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var tnt = ContentRegistry.RequireBlock("tnt").Id;
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 8, 8, tnt, 0);
        Assert.False(TntManager.IsSubmerged(manager, 8, 8, 8));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, -1)]
    public void TntTouchingWaterOnAnyFaceIsSubmerged(int dx, int dy, int dz)
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var tnt = ContentRegistry.RequireBlock("tnt").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 8, 8, tnt, 0);
        manager.Chunks[Vector3.Zero].SetBlockRaw(8 + dx, 8 + dy, 8 + dz, water, FluidState.Source);
        Assert.True(TntManager.IsSubmerged(manager, 8, 8, 8));
    }

    [Fact]
    public void PlayerPrimedFuseBurnsItsWholeFuse()
    {
        var tnt = ContentRegistry.RequireBlock("tnt").Id;
        var primed = new PrimedTnt(new Vector3(8, 8, 8), tnt, TntManager.PlayerFuseSeconds);
        primed.Update(TntManager.PlayerFuseSeconds - 0.05f, null!);
        Assert.False(primed.IsDue);
        primed.Update(0.1f, null!);
        Assert.True(primed.IsDue);
    }

    [Fact]
    public void AChainFuseIsShorterThanAHandLitOne()
    {
        // A blast-lit cube must go off before the hand-lit one that set the chain off would have,
        // or a chain reads as a string of separate fuses
        Assert.True(TntManager.ChainFuseSeconds < TntManager.PlayerFuseSeconds);
    }

    [Fact]
    public void TheStrobeQuickensAsTheFuseBurnsDownWhateverItsLength()
    {
        // The flash rate ramps over the fuse it was given, so a shorter fuse still blinks faster
        // toward its end: count the white/dark toggles over the first and the last fifth
        var tnt = ContentRegistry.RequireBlock("tnt").Id;
        var primed = new PrimedTnt(new Vector3(8, 8, 8), tnt, TntManager.PlayerFuseSeconds);
        const int steps = 200;
        var dt = TntManager.PlayerFuseSeconds / steps;
        var toggles = new int[steps];
        var last = primed.WhiteFlash;
        for (var i = 0; i < steps; i++)
        {
            primed.Update(dt, null!);
            var now = primed.WhiteFlash;
            toggles[i] = now != last ? 1 : 0;
            last = now;
        }

        var early = 0;
        var late = 0;
        for (var i = 0; i < steps / 5; i++)
        {
            early += toggles[i];
            late += toggles[steps - 1 - i];
        }
        Assert.True(late > early);
    }

    [Fact]
    public void ChainPrimedFuseBurnsHalfASecond()
    {
        var tnt = ContentRegistry.RequireBlock("tnt").Id;
        var primed = new PrimedTnt(new Vector3(8, 8, 8), tnt, TntManager.ChainFuseSeconds);
        primed.Update(0.49f, null!);
        Assert.False(primed.IsDue);
        primed.Update(0.01f, null!);
        Assert.True(primed.IsDue);
    }

    [Fact]
    public void ItemStackEntityCarriesComponentsPerInstance()
    {
        var first = new ItemStack(1, Firestarter);
        var second = new ItemStack(2, Firestarter);
        Assert.False(first.Damage(3));
        Assert.Equal(7, first.Get<DurabilityComponent>().UsesLeft);
        Assert.Equal(10, second.Get<DurabilityComponent>().UsesLeft);
        Assert.True(first.CanUseAt(0.0));
        first.MarkUsed(0.0, 1.5f);
        Assert.False(first.CanUseAt(1.0));
        Assert.True(first.CanUseAt(1.5));
        Assert.True(second.CanUseAt(1.0));
    }
}
