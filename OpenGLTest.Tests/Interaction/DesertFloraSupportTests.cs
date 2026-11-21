using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Entities;
using VoxelEngine.Interaction;
using VoxelEngine.Networking;
using VoxelEngine.Player;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

[Collection("Worldgen seed")]
public sealed class DesertFloraSupportTests : IDisposable
{
    private SupportTestWorld? _world;

    public DesertFloraSupportTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        _world?.Dispose();
        WorldSeed.Apply(WorldSeed.Default);
    }

    private static ushort Id(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void NamedCactusFlowerHolderIsExclusiveAndOrdinaryFlowersKeepSolidSupport()
    {
        var blossom = Id("cactus_flower");
        var cactus = Id("small_cactus");
        Assert.True(BlockSupport.CanHold(blossom, cactus, 0));
        Assert.False(BlockSupport.CanHold(blossom, BlockType.Air, 0));
        Assert.False(BlockSupport.CanHold(blossom, Id("stone"), 0));
        Assert.False(BlockSupport.CanHold(blossom, Id("dead_shrub"), 0));
        Assert.True(BlockSupport.CanHold(Id("flower"), Id("stone"), 0));
        Assert.False(BlockSupport.CanHold(Id("flower"), cactus, 0));
    }

    [Fact]
    public void CactusFlowerRefusesWrongSolidPlacementAndPopsWithItsRealHolderAndLoot()
    {
        _world = new SupportTestWorld();
        var cactus = Id("small_cactus");
        var blossom = Id("cactus_flower");
        var x = 4;
        var z = 4;
        var ground = _world.Top;
        _world.Load(x, ground + 2, z);

        Assert.False(_world.Place(blossom, new Vector3(x, ground, z), Vector3.UnitY));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ground + 1, z));
        Assert.True(_world.Place(cactus, new Vector3(x, ground, z), Vector3.UnitY));
        Assert.True(_world.Place(blossom, new Vector3(x, ground + 1, z), Vector3.UnitY));
        Assert.Equal(blossom, _world.IdAt(x, ground + 2, z));

        var dropsBefore = _world.World.Drops.Count(d => d.BlockId == blossom);
        Assert.True(_world.Break(x, ground + 1, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ground + 2, z));
        Assert.Equal(dropsBefore + 1, _world.World.Drops.Count(d => d.BlockId == blossom));
    }

    [Fact]
    public void NonSolidSmallCactusStillUsesSharedContactDamageRule()
    {
        var cactus = Id("small_cactus");
        Assert.False(BlockType.IsSolid(cactus));
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var cell = new Vector3(6, 4, 6);
        center.SetBlockRaw((int)cell.X, (int)cell.Y, (int)cell.Z, cactus, 0);
        var beside = new Vector3(6.5f, MeshTestWorld.HighAbove.Y * Chunk.Size + 4f, 6.5f);
        Assert.True(ContactDamage.Touches(manager, beside, PlayerPhysics.PlayerRadius,
            PlayerPhysics.PlayerHeight, out var damage));
        Assert.True(damage > 0);
    }

    [Fact]
    public void InvalidNamedSupportLinksAreRejectedAtContentResolution()
    {
        var owner = Id("flower");
        const string path = "data/blocks/invalid_flower.json";
        foreach (var target in new[] { "missing_flora_holder", "flower", "coal" })
        {
            var links = new BlockLinkResolver();
            links.Queue(owner, new BlockDefinition { SupportBlock = target }, path);
            Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
                links.Resolve(new Dictionary<ushort, BlockType>(BlockType.Types))).Message);
        }

        var fullFace = new BlockLinkResolver();
        fullFace.Queue(Id("torch"), new BlockDefinition { SupportBlock = "small_cactus" }, path);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            fullFace.Resolve(new Dictionary<ushort, BlockType>(BlockType.Types))).Message);

        var wall = new BlockLinkResolver();
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            wall.Queue(owner, new BlockDefinition { SupportBlock = "small_cactus", WallBlock = "torch" }, path)).Message);
    }

    [Fact]
    public void FlowingWaterGoesAroundTheFloweredCactusWithoutStrandingItsBlossom()
    {
        var (chunks, center) = MeshTestWorld.WalledArea();
        var stone = Id("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, stone, 0);

        var water = Id("water");
        var cactus = Id("small_cactus");
        var blossom = Id("cactus_flower");
        center.SetBlockRaw(8, 1, 8, water, FluidState.Source);
        center.SetBlockRaw(9, 1, 8, cactus, 0);
        center.SetBlockRaw(9, 2, 8, blossom, 0);
        Assert.False(FluidWakeRules.IsFlowTarget(cactus));

        var fluids = new FluidManager();
        fluids.ScheduleAround(8, 1, 8);
        for (var i = 0; i < 8; i++)
            fluids.Update(1f, chunks, new DropManager());

        Assert.Equal(water, chunks.GetBlockGlobal(7, 1, 8));
        Assert.Equal(cactus, chunks.GetBlockGlobal(9, 1, 8));
        Assert.Equal(blossom, chunks.GetBlockGlobal(9, 2, 8));
    }

    [Fact]
    public void NewBlockNamesRoundTripThroughSaveAndHelloTables()
    {
        using var hello = new PacketWriter(NetMessage.ServerHello);
        WireIds.WriteTables(hello);
        using var reader = new PacketReader(hello.ToArray());
        Assert.Equal(NetMessage.ServerHello, reader.Message);
        var wire = WireIds.Read(reader);
        Assert.Empty(wire.Missing);
        foreach (var name in new[] { "small_cactus", "cactus_flower", "desert_flower" })
        {
            var id = Id(name);
            var saved = SaveNames.NameOf(id);
            Assert.Equal(SaveNames.BlockPrefix + name, saved);
            Assert.True(SaveNames.TryResolve(saved, out var restored));
            Assert.Equal(id, restored);
            Assert.Equal(id, wire.ContentToLocal(wire.ContentToServer(id)));
        }
    }
}
