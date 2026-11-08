using System.Linq;
using System.Numerics;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Lod;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.Status;
using VoxelEngine.UI.Hud;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// An <c>unbreakable</c> block survives every removal path: mining (survival and creative), the
/// server's break judgement, the chunk's own clearing writers, placement, a blast, a bucket, a
/// status effect or spell impact, flowing water and a felled tree, while an ordinary block beside
/// it goes the usual way. Only <see cref="Chunk.CollapseWriteAt"/> writes over it.
/// </summary>
[Collection("LOD column caps")]
public sealed class UnbreakableTests
{
    public UnbreakableTests() => TestContent.EnsureLoaded();

    private static byte Id(string name) => ContentRegistry.RequireBlock(name).Id;

    private static byte Brick => BlockType.Types.Values.First(t => t.Unbreakable).Id;

    private static byte Control => Id("stone_bricks");

    private const StatusEffectBlockAction AllActions =
        StatusEffectBlockAction.Ignite | StatusEffectBlockAction.Freeze | StatusEffectBlockAction.Thaw;

    // One loaded chunk at the origin with a stone floor at y = 0
    private static (ChunkManager Chunks, Chunk Chunk) FlooredChunk()
    {
        var chunks = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        chunks.Chunks[Vector3.Zero] = chunk;
        var stone = Id("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        return (chunks, chunk);
    }

    [Fact]
    public void NoToolMinesItAndTheLabelReadsItUnmineableEvenInCreative()
    {
        var strongest = ItemRegistry.All.Where(i => ContentRegistry.IsInTag("pickaxe", i.Id))
            .OrderByDescending(i => i.ToolPower).First();
        Assert.True(Mining.CanMine(Control, strongest));
        Assert.True(Mining.HitDamage(Control, strongest) > 0f);

        foreach (var held in new Item?[] { null, strongest })
        {
            Assert.False(Mining.CanMine(Brick, held));
            Assert.Equal(0f, Mining.HitDamage(Brick, held));
        }

        var label = LookLabel.For(Brick, creative: true, _ => true, strongest);
        Assert.NotEmpty(label.Text);
        Assert.True(label.Known);
        Assert.False(label.Mineable);
        Assert.True(LookLabel.For(Control, creative: true, _ => true, null).Mineable);
    }

    [Fact]
    public void TheServerNeverJudgesItsBreakComplete()
    {
        var held = ItemRegistry.All.Where(i => ContentRegistry.IsInTag("pickaxe", i.Id))
            .OrderByDescending(i => i.ToolPower).First();

        var progress = new BreakProgress();
        progress.Start(1, 2, 3, Brick, 0, held, held.Id, 0, now: 0);
        Assert.Equal(BreakVerdict.Unbreakable, progress.Complete(1, 2, 3, Brick, held.Id, 0, false, 1e6, 0));

        progress.Start(1, 2, 3, Brick, 0, held, held.Id, 0, now: 0);
        Assert.Equal(BreakVerdict.Unbreakable, progress.Complete(1, 2, 3, Brick, held.Id, 0, true, 1e6, 0));
        // Creative never needed a start, but still breaks no unbreakable block
        Assert.Equal(BreakVerdict.Unbreakable, new BreakProgress().Complete(1, 2, 3, Brick, held.Id, 0, true, 0, 0));
        Assert.Equal(BreakVerdict.Break, new BreakProgress().Complete(1, 2, 3, Control, held.Id, 0, true, 0, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BreakingItForAPlayerLeavesItStanding(bool creative)
    {
        using var world = new SupportTestWorld();
        int x = 8, y = world.Top + 1, z = 8;
        world.Load(x, y, z);
        world.Set(x, y, z, Brick);
        world.Set(x + 1, y, z, Control);
        world.Player.Creative = creative;
        world.Player.Context.IsCreative = creative;

        Assert.False(world.Break(x, y, z));
        Assert.Equal(Brick, world.IdAt(x, y, z));
        Assert.True(world.Break(x + 1, y, z));
        Assert.Equal(BlockType.Air, world.IdAt(x + 1, y, z));
    }

    [Fact]
    public void APlacementNeverWritesOverIt()
    {
        Assert.False(Placement.CanOccupy(Brick, 0));
        using var world = new SupportTestWorld();
        int x = 8, y = world.Top + 1, z = 8;
        world.Load(x, y, z);
        world.Set(x, y, z, Brick);

        Assert.False(world.Place(world.Stone, new Vector3(x, y - 1, z), Vector3.UnitY));
        Assert.Equal(Brick, world.IdAt(x, y, z));
    }

    [Fact]
    public void TheChunkClearingWritersRefuseItAndOnlyTheCollapseWriteReplacesIt()
    {
        var (chunks, chunk) = FlooredChunk();
        var cell = new Vector3(5, 1, 5);
        chunk.SetBlockRaw(5, 1, 5, Brick, 0);

        Assert.False(chunk.RemoveBlockAt(cell, chunks));
        Assert.Equal(0, chunk.ClearBlockAt(cell));
        Assert.Equal(Brick, chunks.GetBlockGlobal(5, 1, 5));

        Assert.Equal(Brick, chunk.CollapseWriteAt(cell, Control, 0));
        Assert.Equal(Control, chunks.GetBlockGlobal(5, 1, 5));
        // Once it is an ordinary block again, the clearing writers take it as usual
        Assert.True(chunk.RemoveBlockAt(cell, chunks));
    }

    [Fact]
    public void ABlastLeavesItStanding()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(9, 8, 8, Brick, 0);
        center.SetBlockRaw(7, 8, 8, Control, 0);
        var tnt = new TntManager();
        tnt.Spawn(new Vector3(8, 9, 8), Id("tnt"), 0f);

        tnt.Update(1e-4f, manager, new LodManager(null!), new DropManager(), new FluidManager(), new FallingBlockManager());

        Assert.Single(tnt.Blasts);
        Assert.Equal(Brick, manager.GetBlockGlobal(9, 8, 8));
        Assert.Equal(BlockType.Air, manager.GetBlockGlobal(7, 8, 8));
    }

    [Fact]
    public void ABucketNeitherPoursOverNorScoopsIt()
    {
        var (chunks, chunk) = FlooredChunk();
        var water = Id("water");
        chunk.SetBlockRaw(5, 1, 5, Brick, 0);
        var fluids = new FluidManager();

        Assert.False(fluids.SetCellImmediate(new Vector3(5, 1, 5), water, FluidState.Source, chunks));
        Assert.False(fluids.SetCellImmediate(new Vector3(5, 1, 5), 0, 0, chunks));
        Assert.Equal(Brick, chunks.GetBlockGlobal(5, 1, 5));

        Assert.True(fluids.SetCellImmediate(new Vector3(6, 1, 5), water, FluidState.Source, chunks));
        Assert.Equal(water, chunks.GetBlockGlobal(6, 1, 5));
    }

    [Fact]
    public void StatusEffectsAndSpellImpactsLeaveIt()
    {
        Assert.False(StatusEffectBlockRule.TryConvert(Brick, 0, AllActions, true, out _, out _));

        var (chunks, chunk) = FlooredChunk();
        var leaf = Id("oak_leaves");
        chunk.SetBlockRaw(5, 0, 5, Brick, 0);
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            chunk.SetBlockRaw(5 + dx, 1, 5 + dz, Brick, 0);
        chunk.SetBlockRaw(6, 1, 6, leaf, 0);
        var writer = new StatusEffectBlockWriter(chunks, new FluidManager(), new LeafDecayManager(),
            new FallingBlockManager(), new LodManager(null!), (_, _, _) => { });

        for (var i = 0; i < 5; i++)
        {
            writer.BeginFrame();
            writer.Apply(new Vector3(5.5f, 1f, 5.5f), AllActions);
            writer.ApplyAt(new Vector3(5, 1, 5), 1, AllActions);
        }

        Assert.Equal(BlockType.Air, chunks.GetBlockGlobal(6, 1, 6));
        Assert.Equal(Brick, chunks.GetBlockGlobal(5, 0, 5));
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            Assert.Equal(Brick, chunks.GetBlockGlobal(5 + dx, 1, 5 + dz));
    }

    [Fact]
    public void FlowingWaterGoesAroundIt()
    {
        var (chunks, chunk) = FlooredChunk();
        var water = Id("water");
        chunk.SetBlockRaw(9, 1, 8, Brick, 0);
        chunk.SetBlockRaw(8, 1, 8, water, FluidState.Source);
        var fluids = new FluidManager();
        var drops = new DropManager();
        fluids.ScheduleAround(8, 1, 8);

        for (var i = 0; i < 8; i++)
            fluids.Update(1f, chunks, drops);

        Assert.Equal(Brick, chunks.GetBlockGlobal(9, 1, 8));
        Assert.Equal(water, chunks.GetBlockGlobal(7, 1, 8));
        Assert.Equal(water, chunks.GetBlockGlobal(9, 1, 9));
    }

    [Fact]
    public void AFelledTreeLeavesItStanding()
    {
        using var world = new SupportTestWorld();
        var oak = TreeSpeciesRegistry.Require("oak");
        int x = 8, y = world.Top + 1, z = 8;
        world.Load(x, y + 2, z);
        world.Set(x, y, z, oak.WoodId, WoodState.Encode(3, WoodState.ParentRoot, false));
        for (var dy = 1; dy <= 3; dy++)
            world.Set(x, y + dy, z, oak.WoodId, WoodState.Encode(3, TreeDirections.Down, false));
        world.Set(x + 1, y + 3, z, oak.LeavesId, LeafState.Encode(LeafState.MaxHydration, false));
        // Beside the trunk, inside the reach the fell sweeps for wood and canopy
        world.Set(x + 1, y + 2, z, Brick);

        Assert.True(world.Break(x, y, z));

        for (var dy = 0; dy <= 3; dy++)
            Assert.Equal(BlockType.Air, world.IdAt(x, y + dy, z));
        Assert.Equal(BlockType.Air, world.IdAt(x + 1, y + 3, z));
        Assert.Equal(Brick, world.IdAt(x + 1, y + 2, z));
    }
}
