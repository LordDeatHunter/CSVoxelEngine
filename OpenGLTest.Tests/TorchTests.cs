using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The torch is a placeable light source that needs the block it stands on: its light spreads
/// from its own cell, and it pops with its loot when that block goes.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TorchTests : IDisposable
{
    private const int FloorY = 4;

    private readonly byte _torch;
    private readonly byte _stone;
    private SupportTestWorld? _world;

    public TorchTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
        _torch = ContentRegistry.RequireBlock("torch").Id;
        _stone = ContentRegistry.RequireBlock("stone").Id;
    }

    public void Dispose()
    {
        _world?.Dispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Vector3 WorldCentre(Chunk chunk, int x, int y, int z) =>
        chunk.Position * Chunk.Size + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);

    // Block light at three cells along the floor row, nearest the torch's column first
    private (float Near, float Mid, float Far) FloorRowLight(bool withTorch)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, FloorY, z, _stone, 0);
        if (withTorch)
            center.SetBlockRaw(2, FloorY + 1, 8, _torch, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        center.PublishLight(mesh!.Light);

        return (EntityLight.Sample(manager, WorldCentre(center, 3, FloorY + 1, 8)).Block,
            EntityLight.Sample(manager, WorldCentre(center, 8, FloorY + 1, 8)).Block,
            EntityLight.Sample(manager, WorldCentre(center, 14, FloorY + 1, 8)).Block);
    }

    [Fact]
    public void ATorchLightsTheCellsAroundItAndTheLightFallsOff()
    {
        var dark = FloorRowLight(withTorch: false);
        Assert.Equal(0f, dark.Near);

        var lit = FloorRowLight(withTorch: true);
        Assert.True(lit.Near > 0f, "no block light beside the torch");
        Assert.True(lit.Near > lit.Mid && lit.Mid >= lit.Far, "the torch light does not fall off with distance");
    }

    [Fact]
    public void BreakingTheBlockUnderATorchPopsIt()
    {
        _world = new SupportTestWorld();
        var (x, z) = (4, 4);
        var ground = _world.Top;
        _world.Load(x, ground, z);

        Assert.True(_world.Place(_torch, new Vector3(x, ground, z), Vector3.UnitY));
        Assert.Equal(_torch, _world.IdAt(x, ground + 1, z));

        // Mining a neighbouring ground block leaves it standing
        Assert.True(_world.Break(x + 1, ground, z));
        Assert.Equal(_torch, _world.IdAt(x, ground + 1, z));

        // The block it stands on takes it down
        Assert.True(_world.Break(x, ground, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ground + 1, z));
    }
}
