using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Interaction;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>The named two-cell fern and independently grounded bush use real edit rules.</summary>
[Collection("Worldgen seed")]
public sealed class GroundCoverLifecycleTests : IDisposable
{
    private SupportTestWorld? _world;

    public GroundCoverLifecycleTests()
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
    public void TallFernTopAcceptsOnlyItsNamedBaseAndMiningBasePopsTopOnce()
    {
        var baseId = Id("tall_fern");
        var topId = Id("tall_fern_top");
        Assert.True(BlockSupport.CanHold(topId, baseId, 0));
        Assert.False(BlockSupport.CanHold(topId, Id("stone"), 0));
        Assert.False(BlockSupport.CanHold(topId, Id("bush"), 0));
        Assert.False(BlockSupport.CanHold(topId, BlockType.Air, 0));

        _world = new SupportTestWorld();
        const int x = 6, z = 6;
        var y = _world.Top + 1;
        _world.Load(x, y + 1, z);
        _world.Set(x, y, z, baseId);
        _world.Set(x, y + 1, z, topId);
        var before = _world.World.Drops.Count;
        Assert.True(_world.Break(x, y, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, y, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, y + 1, z));
        Assert.DoesNotContain(_world.World.Drops.Skip(before), d => d.BlockId == topId);
    }

    [Fact]
    public void FlowWashingTallFernBaseClearsItsLoadedTopAndKeepsOtherPlantsFlowable()
    {
        var (chunks, center) = MeshTestWorld.WalledArea();
        var stone = Id("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, 0, z, stone, 0);
        var water = Id("water");
        var baseId = Id("tall_fern");
        var topId = Id("tall_fern_top");
        center.SetBlockRaw(8, 1, 8, water, FluidState.Source);
        center.SetBlockRaw(9, 1, 8, baseId, 0);
        center.SetBlockRaw(9, 2, 8, topId, 0);
        Assert.True(FluidWakeRules.IsFlowTarget(baseId));
        Assert.True(FluidWakeRules.IsFlowTarget(Id("bush")));
        Assert.True(FluidWakeRules.IsFlowTarget(Id("fern")));

        var fluid = new FluidManager();
        fluid.ScheduleAround(8, 1, 8);
        fluid.Update(1f, chunks, new DropManager());
        Assert.Equal(water, chunks.GetBlockGlobal(9, 1, 8));
        Assert.Equal(BlockType.Air, chunks.GetBlockGlobal(9, 2, 8));
    }

    [Fact]
    public void WaterBucketOnNamedFernBaseDoesNotStrandItsTop()
    {
        _world = new SupportTestWorld();
        const int x = 7, z = 8;
        var y = _world.Top + 1;
        _world.Load(x, y + 1, z);
        _world.Set(x, y, z, Id("tall_fern"));
        _world.Set(x, y + 1, z, Id("tall_fern_top"));
        var player = _world.Player;
        var bucket = ContentRegistry.RequireItem("water_bucket").Id;
        Assert.True(player.Inventory.Add(bucket));
        Assert.True(player.Inventory.PickBlock(bucket));
        var target = new Vector3(x + 0.5f, _world.Top + 0.9f, z + 0.5f);
        player.Aim(player.Context.Eye, Vector3.Normalize(target - player.Context.Eye));
        Assert.True(_world.World.TryUseSelected(World.InteractionReach, player.Context, out _));
        Assert.Equal(Id("water"), _world.IdAt(x, y, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, y + 1, z));
    }

    [Fact]
    public void GroundCoverNamesUseExistingSaveAndHelloNameTables()
    {
        using var hello = new PacketWriter(NetMessage.ServerHello);
        WireIds.WriteTables(hello);
        using var reader = new PacketReader(hello.ToArray());
        var wire = WireIds.Read(reader);
        Assert.Empty(wire.Missing);
        foreach (var name in new[] { "fern", "tall_fern", "tall_fern_top", "bush" })
        {
            var id = Id(name);
            var saved = SaveNames.NameOf(id);
            Assert.True(SaveNames.TryResolve(saved, out var restored));
            Assert.Equal(id, restored);
            Assert.Equal(id, wire.ContentToLocal(wire.ContentToServer(id)));
        }
    }
}
