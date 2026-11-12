using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Interaction;
using VoxelEngine.Lod;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.Status;
using Xunit;

namespace VoxelEngine.Tests.Status;

/// <summary>
/// The status effects' block interaction: the pure rule (<see cref="StatusEffectBlockRule"/>) reads
/// only the block flags and the action flags, never effect names; the writer
/// (<see cref="StatusEffectBlockWriter"/>) runs every edit hook, never empties a cell that holds
/// something up, never freezes the body in, skips unloaded cells and stays inside its caps.
/// </summary>
[Collection("LOD column caps")]
public sealed class StatusEffectBlockTests
{
    public StatusEffectBlockTests() => TestContent.EnsureLoaded();

    private static ushort Id(string name) => ContentRegistry.RequireBlock(name).Id;

    private sealed class Rig
    {
        public required ChunkManager Chunks;
        public required Chunk Chunk;
        public required FluidManager Fluids;
        public required LeafDecayManager LeafDecay;
        public required FallingBlockManager Falling;
        public required StatusEffectBlockWriter Writer;
        public readonly List<(int X, int Y, int Z)> LodEdits = [];
    }

    // One loaded chunk at the origin with a stone floor at y = 0, and a writer over it
    private static Rig Setup()
    {
        var chunks = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        chunks.Chunks[Vector3.Zero] = chunk;
        var stone = Id("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);

        var fluids = new FluidManager();
        var leafDecay = new LeafDecayManager();
        var falling = new FallingBlockManager();
        var lod = new LodManager(null!);
        Rig? rig = null;
        var writer = new StatusEffectBlockWriter(chunks, fluids, leafDecay, falling, lod,
            (x, y, z) => rig!.LodEdits.Add((x, y, z)));
        rig = new Rig
        {
            Chunks = chunks, Chunk = chunk, Fluids = fluids, LeafDecay = leafDecay, Falling = falling, Writer = writer,
        };
        return rig;
    }

    [Fact]
    public void TheRuleBurnsFreezesAndThawsOnlyWhatItsFlagsAllow()
    {
        var leaf = Id("oak_leaves");
        var water = Id("water");
        var ice = Id("ice");
        var stone = Id("stone");
        Assert.True(BlockType.FlammableById[leaf]);
        Assert.False(BlockType.FlammableById[ice]);
        Assert.False(BlockType.FlammableById[stone] || BlockType.FreezesToById[stone] != 0 || BlockType.MeltsToById[stone] != 0);
        const StatusEffectBlockAction all = StatusEffectBlockAction.Ignite | StatusEffectBlockAction.Freeze
            | StatusEffectBlockAction.Thaw;

        Assert.True(StatusEffectBlockRule.TryConvert(leaf, 0, StatusEffectBlockAction.Ignite, true, out var id, out var state));
        Assert.Equal(BlockType.Air, id);
        Assert.Equal(0, state);
        Assert.False(StatusEffectBlockRule.TryConvert(leaf, 0,
            StatusEffectBlockAction.Freeze | StatusEffectBlockAction.Thaw, true, out _, out _));

        Assert.True(StatusEffectBlockRule.TryConvert(water, FluidState.Source, StatusEffectBlockAction.Freeze, true,
            out id, out state));
        Assert.Equal(BlockType.FreezesToById[water], id);
        Assert.False(BlockType.IsFluid(id));
        Assert.Equal(0, state);
        Assert.False(StatusEffectBlockRule.TryConvert(water, FluidState.Source,
            StatusEffectBlockAction.Ignite | StatusEffectBlockAction.Thaw, true, out _, out _));

        Assert.True(StatusEffectBlockRule.TryConvert(ice, 0, StatusEffectBlockAction.Thaw, false, out id, out state));
        Assert.Equal(BlockType.MeltsToById[ice], id);
        Assert.True(BlockType.IsFluid(id));
        Assert.True(FluidState.IsSource(state));
        Assert.False(StatusEffectBlockRule.TryConvert(ice, 0,
            StatusEffectBlockAction.Ignite | StatusEffectBlockAction.Freeze, true, out _, out _));

        Assert.False(StatusEffectBlockRule.TryConvert(stone, 0, all, true, out _, out _));
        Assert.False(StatusEffectBlockRule.TryConvert(BlockType.Air, 0, all, true, out _, out _));
        foreach (var block in new[] { leaf, water, ice })
            Assert.False(StatusEffectBlockRule.TryConvert(block, 0, StatusEffectBlockAction.None, true, out _, out _));
    }

    [Fact]
    public void FrostNeverFreezesTheCellsTheBodyStandsIn()
    {
        var water = Id("water");
        // Only a source below the feet freezes: never a cell the body reaches into, never a flowing cell
        Assert.False(StatusEffectBlockRule.TryConvert(water, FluidState.Source, StatusEffectBlockAction.Freeze, false,
            out _, out _));
        Assert.False(StatusEffectBlockRule.TryConvert(water, 3, StatusEffectBlockAction.Freeze, true, out _, out _));
        Assert.False(StatusEffectBlockRule.TryConvert(water, FluidState.Falling, StatusEffectBlockAction.Freeze, true,
            out _, out _));

        // A body floating in a pool three deep: everything it can reach is water, but only the
        // layer wholly under its feet may freeze
        var rig = Setup();
        for (var x = 3; x <= 7; x++)
        for (var z = 3; z <= 7; z++)
        for (var y = 1; y <= 3; y++)
            rig.Chunk.SetBlockRaw(x, y, z, water, FluidState.Source);
        var feet = new Vector3(5.5f, 2.3f, 5.5f);
        var written = 0;
        for (var i = 0; i < 20; i++)
        {
            rig.Writer.BeginFrame();
            written += rig.Writer.Apply(feet, StatusEffectBlockAction.Freeze);
        }

        Assert.True(written > 0);
        var frozen = BlockType.FreezesToById[water];
        for (var x = 3; x <= 7; x++)
        for (var z = 3; z <= 7; z++)
        {
            for (var y = 2; y <= 3; y++)
                Assert.Equal(water, rig.Chunks.GetBlockGlobal(x, y, z));
        }
        var frozenBelow = 0;
        for (var x = 4; x <= 6; x++)
        for (var z = 4; z <= 6; z++)
            if (rig.Chunks.GetBlockGlobal(x, 1, z) == frozen)
                frozenBelow++;
        Assert.Equal(written, frozenBelow);
    }

    [Fact]
    public void ABurnedLeafRunsTheFluidLeafDecayAndLodHooks()
    {
        var rig = Setup();
        var leaf = Id("oak_leaves");
        var water = Id("water");
        rig.Chunk.SetBlockRaw(5, 1, 5, leaf, 0);
        rig.Chunk.SetBlockRaw(6, 1, 5, water, FluidState.Source);
        rig.Chunk.TakeSnapshot(null);
        Assert.False(rig.Chunk.ModifiedSinceSave);
        Assert.Equal(0, rig.Fluids.PendingCells);
        Assert.Equal(0, rig.LeafDecay.PendingCells);

        rig.Writer.BeginFrame();
        Assert.Equal(1, rig.Writer.Apply(new Vector3(5.5f, 1f, 5.5f), StatusEffectBlockAction.Ignite));

        Assert.Equal(BlockType.Air, rig.Chunks.GetBlockGlobal(5, 1, 5));
        Assert.Equal(water, rig.Chunks.GetBlockGlobal(6, 1, 5));
        Assert.True(rig.Chunk.ModifiedSinceSave);
        Assert.True(rig.Fluids.PendingCells > 0);
        Assert.True(rig.LeafDecay.PendingCells > 0);
        Assert.Contains((5, 1, 5), rig.LodEdits);
    }

    [Fact]
    public void ThawedIceUnderSandLetsItFall()
    {
        var rig = Setup();
        var ice = Id("ice");
        var sand = Id("sand");
        rig.Chunk.SetBlockRaw(5, 1, 5, ice, 0);
        rig.Chunk.SetBlockRaw(5, 2, 5, sand, 0);
        Assert.Empty(rig.Falling.Falling);

        rig.Writer.BeginFrame();
        Assert.Equal(1, rig.Writer.Apply(new Vector3(6.5f, 1f, 5.5f), StatusEffectBlockAction.Thaw));

        Assert.True(BlockType.IsFluid(rig.Chunks.GetBlockGlobal(5, 1, 5)));
        Assert.True(rig.Fluids.PendingCells > 0);
        Assert.NotEmpty(rig.Falling.Falling);
    }

    [Fact]
    public void ABlockHoldingSomethingUpNeverBurns()
    {
        var rig = Setup();
        var leaf = Id("oak_leaves");
        var torch = Id("wall_torch");
        var held = new Vector3(5, 2, 5);
        var torchCell = held + Chunk.CubeFaces[0].normal;
        rig.Chunk.SetBlockRaw(5, 2, 5, leaf, 0);
        rig.Chunk.SetBlockRaw((int)torchCell.X, (int)torchCell.Y, (int)torchCell.Z, torch, 0);
        rig.Chunk.SetBlockRaw(6, 2, 6, leaf, 0);
        Assert.True(BlockSupport.IsHeldUpBy(torch, 0, torchCell, held));

        rig.Writer.BeginFrame();
        Assert.Equal(1, rig.Writer.Apply(new Vector3(5.5f, 2f, 5.5f), StatusEffectBlockAction.Ignite));

        Assert.Equal(leaf, rig.Chunks.GetBlockGlobal(5, 2, 5));
        Assert.Equal(torch, rig.Chunks.GetBlockGlobal((int)torchCell.X, (int)torchCell.Y, (int)torchCell.Z));
        Assert.Equal(BlockType.Air, rig.Chunks.GetBlockGlobal(6, 2, 6));
    }

    [Fact]
    public void WritesStayInsideThePulseAndFrameCaps()
    {
        var rig = Setup();
        var leaf = Id("oak_leaves");
        for (var x = 4; x <= 6; x++)
        for (var y = 1; y <= 3; y++)
        for (var z = 4; z <= 6; z++)
            rig.Chunk.SetBlockRaw(x, y, z, leaf, 0);
        var feet = new Vector3(5.5f, 2f, 5.5f);
        // More leaves in reach than one frame may burn
        Assert.True(27 > StatusEffectConfig.MaxBlockWritesPerFrame);

        rig.Writer.BeginFrame();
        var frame = 0;
        for (var i = 0; i < 20; i++)
        {
            var pulse = rig.Writer.Apply(feet, StatusEffectBlockAction.Ignite);
            Assert.InRange(pulse, 0, StatusEffectConfig.MaxBlockWritesPerPulse);
            frame += pulse;
        }
        Assert.Equal(StatusEffectConfig.MaxBlockWritesPerFrame, frame);

        // A new frame burns on
        rig.Writer.BeginFrame();
        Assert.True(rig.Writer.Apply(feet, StatusEffectBlockAction.Ignite) > 0);
    }

    [Fact]
    public void AnImpactFreezeSetsSourcesAtOrBelowTheImpactOnly()
    {
        var rig = Setup();
        var water = Id("water");
        for (var x = 3; x <= 7; x++)
        for (var z = 3; z <= 7; z++)
        for (var y = 1; y <= 3; y++)
            rig.Chunk.SetBlockRaw(x, y, z, water, FluidState.Source);
        var impact = new Vector3(5, 2, 5);
        var written = 0;
        for (var i = 0; i < 20; i++)
        {
            rig.Writer.BeginFrame();
            written += rig.Writer.ApplyAt(impact, 1, StatusEffectBlockAction.Freeze);
        }

        var frozen = BlockType.FreezesToById[water];
        Assert.True(written > 0);
        Assert.Equal(frozen, rig.Chunks.GetBlockGlobal(5, 2, 5));
        Assert.Equal(frozen, rig.Chunks.GetBlockGlobal(5, 1, 5));
        for (var x = 3; x <= 7; x++)
        for (var z = 3; z <= 7; z++)
            Assert.Equal(water, rig.Chunks.GetBlockGlobal(x, 3, z));
    }

    [Fact]
    public void AnImpactFreezeHardensALavaSource()
    {
        var rig = Setup();
        var lava = Id("lava");
        rig.Chunk.SetBlockRaw(5, 1, 5, lava, FluidState.Source);
        var hardened = BlockType.FreezesToById[lava];
        Assert.NotEqual(BlockType.Air, hardened);
        Assert.False(BlockType.IsFluid(hardened));

        rig.Writer.BeginFrame();
        Assert.Equal(1, rig.Writer.ApplyAt(new Vector3(5, 1, 5), 1, StatusEffectBlockAction.Freeze));
        Assert.Equal(hardened, rig.Chunks.GetBlockGlobal(5, 1, 5));
        Assert.True(rig.Fluids.PendingCells > 0);
    }

    [Fact]
    public void AnImpactIgniteBurnsAFlammableBlockWithItsHooks()
    {
        var rig = Setup();
        var leaf = Id("oak_leaves");
        rig.Chunk.SetBlockRaw(5, 2, 5, leaf, 0);
        rig.Chunk.TakeSnapshot(null);

        rig.Writer.BeginFrame();
        Assert.Equal(1, rig.Writer.ApplyAt(new Vector3(5, 1, 5), 1, StatusEffectBlockAction.Ignite));

        Assert.Equal(BlockType.Air, rig.Chunks.GetBlockGlobal(5, 2, 5));
        Assert.Equal(Id("stone"), rig.Chunks.GetBlockGlobal(5, 0, 5));
        Assert.True(rig.Chunk.ModifiedSinceSave);
        Assert.True(rig.LeafDecay.PendingCells > 0);
        Assert.Contains((5, 2, 5), rig.LodEdits);
    }

    [Fact]
    public void AnUnloadedCellIsSkipped()
    {
        var rig = Setup();
        var leaf = Id("oak_leaves");
        // A neighbor chunk whose data is not ready reads as unloaded
        var unready = new Chunk(null!, new Vector3(-1, 0, 0), generateBlockData: false);
        rig.Chunks.Chunks[new Vector3(-1, 0, 0)] = unready;
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
            unready.SetBlockRaw(Chunk.Size - 1, y, z, leaf, 0);
        rig.Chunk.SetBlockRaw(0, 1, 1, leaf, 0);

        rig.Writer.BeginFrame();
        var written = 0;
        for (var i = 0; i < 5; i++)
            written += rig.Writer.Apply(new Vector3(0.5f, 1f, 1.5f), StatusEffectBlockAction.Ignite);

        Assert.Equal(1, written);
        Assert.Equal(BlockType.Air, rig.Chunks.GetBlockGlobal(0, 1, 1));
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
            Assert.Equal(leaf, unready.GetLocalBlock(Chunk.Size - 1, y, z));
    }
}
