using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Entities.Sandworms;

/// <summary>
/// Melee against the sandworm through the real punch path: a worm standing clear of the sand is hit where it is,
/// the head for full damage and the body for a share of it, while a worm under the sand cannot be punched even
/// right beside the player (its head center is the position every other mob is aimed at).
/// </summary>
[Collection("Worldgen seed")]
public sealed class SandwormMeleeTests : IDisposable
{
    private const float Dt = 1f / 60f;
    private const float OpenAir = 200f;

    private readonly List<World> _worlds = [];

    public SandwormMeleeTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        foreach (var world in _worlds)
            world.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private (World World, Sandworm Worm) WormInOpenAir()
    {
        WorldSeed.Apply(WorldSeed.Default);
        var world = new World(null!, null!, null!, null!, null!, null!, null, null);
        _worlds.Add(world);
        // Feet one burrow depth over the head: the head hangs in open air, far above any sand
        Assert.True(world.SpawnEntity(Sandworm.EntityName, new Vector3(0.5f, OpenAir, -2f)));
        return (world, Assert.Single(world.Sandworms));
    }

    private static PlayerContext PlayerFacingNorthBeside(Vector3 target) => new(Inventory.Empty())
    {
        Eye = new Vector3(target.X, target.Y, target.Z + 2f),
        Forward = -Vector3.UnitZ,
        ViewDirection = -Vector3.UnitZ,
    };

    [Fact]
    public void AWormClearOfTheSandTakesAPunchAtItsHeadForFullDamage()
    {
        var (world, worm) = WormInOpenAir();
        worm.Update(Dt, null, (_, _) => (10f, true));
        Assert.True(worm.IsExposed(0));
        var before = worm.Health;

        Assert.True(world.PunchFor(PlayerFacingNorthBeside(worm.Position)));

        Assert.True(worm.Health < before);
        Assert.Equal(worm.Health, before - worm.LastHitDamage);
    }

    [Fact]
    public void AWormUnderTheSandCannotBePunchedEvenBesideThePlayer()
    {
        var (world, worm) = WormInOpenAir();
        // The sand lies far above the whole chain: every segment is buried
        worm.Update(Dt, null, (_, _) => (OpenAir + 100f, true));
        Assert.False(worm.IsExposed(0));
        var before = worm.Health;

        Assert.False(world.PunchFor(PlayerFacingNorthBeside(worm.Position)));

        Assert.Equal(before, worm.Health);
    }
}
