using System;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The timed use (use.seconds): holding the button charges a use pose and the use runs only when
/// the hold completes; letting go early cancels it and spends nothing.
/// </summary>
[Collection("Worldgen seed")]
public sealed class UseTimeTests : IDisposable
{
    private const float Dt = 1f / 60f;

    private World? _world;

    public UseTimeTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    // A client-mode world: its own inventory and health mirror are the local ones, and
    // ApplyNetworkHealth is the seam that hurts it without a server
    private World NewClientWorld(out Item bandage)
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        _world.GetChunkManager().Headless = true;
        _world.ApplyNetworkHealth(40);

        bandage = ContentRegistry.RequireItem("bandage");
        Assert.True(_world.Inventory.Add(bandage.Id));
        // Add equips a fresh find into the first free slot; put that slot in hand
        _world.Inventory.Select(0);
        Assert.Equal(bandage.Id, _world.Inventory.SelectedBlockId);
        return _world;
    }

    [Fact]
    public void ACompletedHoldRunsTheUseAndSpendsOne()
    {
        var world = NewClientWorld(out var bandage);
        Assert.True(bandage.UseSeconds > 0f, "the bandage declares no use.seconds");

        Assert.True(world.TryBeginUseCharge());
        Assert.True(world.IsUseCharging);

        // Holding for less than the item's seconds does nothing yet
        for (var t = 0f; t < bandage.UseSeconds - 0.15f; t += Dt)
            world.UpdateUseCharge(Dt);
        Assert.Equal(40, world.Health);
        Assert.True(world.IsUseCharging);

        // Completing the hold runs the use: healed, one bandage spent
        for (var t = 0f; t < 0.4f; t += Dt)
            world.UpdateUseCharge(Dt);
        Assert.False(world.IsUseCharging);
        Assert.Equal(40 + WorldGenerationConfig.BandageHealAmount, world.Health);
        Assert.Equal(0, world.Inventory.CountFor(bandage.Id));
    }

    [Fact]
    public void ReleasingEarlyCancelsAndSpendsNothing()
    {
        var world = NewClientWorld(out var bandage);
        Assert.True(world.TryBeginUseCharge());
        world.UpdateUseCharge(Dt * 5);
        world.CancelUseCharge();

        Assert.False(world.IsUseCharging);
        Assert.Equal(40, world.Health);
        Assert.Equal(1, world.Inventory.CountFor(bandage.Id));
    }

    [Fact]
    public void AnItemWithoutAUseTimeIsNotCharged()
    {
        var world = NewClientWorld(out var bandage);
        var cotton = ContentRegistry.RequireItem("cotton");
        Assert.True(world.Inventory.Add(cotton.Id));
        // The bandage took slot 0; the cotton went to the next free slot
        world.Inventory.Select(1);
        Assert.Equal(cotton.Id, world.Inventory.SelectedBlockId);
        Assert.False(cotton.UseSeconds > 0f);
        Assert.False(world.TryBeginUseCharge());
        Assert.False(world.IsUseCharging);
        _ = bandage;
    }
}
