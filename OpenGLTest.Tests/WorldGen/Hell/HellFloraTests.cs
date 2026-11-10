using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Tests.Interaction;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Hell;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Hell;

/// <summary>
/// Hell's flora (plans/LAYERS.md L-9): the vines and ground plants are part of hell's one pure rule,
/// so the stamp equals the point query with them in place, every vine cell hangs from a solid cell or
/// the vine over it, every plant stands on its own biome's surface, and none stands anywhere but in
/// pocket air; the trees root on their biome's surface, grow only into pocket air inside their own
/// cell, and come out the same whichever chunk stamps them and across a cache reset; and a vine falls
/// whole when the cell holding it goes, mined or blasted.
/// </summary>
[Collection("Worldgen seed")]
public sealed class HellFloraTests : IDisposable
{
    private const int Period = WorldGenerationConfig.HellLayerPeriodBlocks;
    private const int Volume = Chunk.Size * Chunk.Size * Chunk.Size;

    private static readonly HellBiome[] AllBiomes = Enum.GetValues<HellBiome>();

    private SupportTestWorld? _world;

    public HellFloraTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
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

    private static int LayerTop(int layer) => WorldGenerationConfig.HellTopY - layer * Period;

    private static bool IsPlant(ushort block) =>
        block != BlockType.Air && AllBiomes.Any(b => HellBiomes.Palette(b).GroundPlant == block);

    private static bool IsFlora(ushort block) => block == HellFlora.VineBlock || IsPlant(block);

    /// <summary>A chunk column wholly in <paramref name="biome"/> (at its corners) in <paramref name="layer"/>.</summary>
    private static (int Cx, int Cz) ColumnIn(HellBiome biome, int layer, int skip = 0)
    {
        for (var cx = -160; cx <= 160; cx += 5)
        for (var cz = -160; cz <= 160; cz += 7)
        {
            if (HellBiomes.AtLayer(cx * Chunk.Size, layer, cz * Chunk.Size) != biome) continue;
            if (HellBiomes.AtLayer(cx * Chunk.Size + Chunk.Size - 1, layer, cz * Chunk.Size + Chunk.Size - 1) != biome)
                continue;
            if (skip-- > 0) continue;
            return (cx, cz);
        }

        throw new InvalidOperationException($"no {biome} chunk column in layer {layer}");
    }

    /// <summary>The pocket of <paramref name="layer"/> over chunk column (cx, cz), stamped chunk by
    /// chunk into one tall array indexed [x, y - <c>Y0</c>, z].</summary>
    private sealed class PocketColumn
    {
        public PocketColumn(int cx, int cz, int layer)
        {
            var top = LayerTop(layer);
            var bottom = top - HellGenerator.PocketHeight + 1;
            Cy0 = bottom >> Chunk.SizeShift;
            var cy1 = top >> Chunk.SizeShift;
            X0 = cx * Chunk.Size;
            Z0 = cz * Chunk.Size;
            Y0 = Cy0 * Chunk.Size;
            Height = (cy1 - Cy0 + 1) * Chunk.Size;
            Blocks = new ushort[Chunk.Size * Height * Chunk.Size];

            var chunk = new ushort[Volume];
            for (var cy = Cy0; cy <= cy1; cy++)
            {
                Array.Clear(chunk);
                HellGenerator.Stamp(chunk, X0, cy * Chunk.Size, Z0);
                for (var x = 0; x < Chunk.Size; x++)
                for (var y = 0; y < Chunk.Size; y++)
                for (var z = 0; z < Chunk.Size; z++)
                    Blocks[Index(x, (cy - Cy0) * Chunk.Size + y, z)] = chunk[Chunk.Idx(x, y, z)];
            }
        }

        public int X0 { get; }
        public int Y0 { get; }
        public int Z0 { get; }
        public int Cy0 { get; }
        public int Height { get; }
        public ushort[] Blocks { get; }

        public int Index(int x, int y, int z) => (x * Height + y) * Chunk.Size + z;

        public ushort At(int x, int y, int z) => Blocks[Index(x, y, z)];
    }

    [Fact]
    public void VinesHangFromSolidOrVineAndPlantsStandOnTheirBiomesSurfaceInPocketAirOnly()
    {
        var vines = new Dictionary<HellBiome, int>();
        var plants = new Dictionary<HellBiome, int>();
        var longVine = false;

        foreach (var biome in AllBiomes)
        for (var layer = 0; layer < 2; layer++)
        {
            var (cx, cz) = ColumnIn(biome, layer);
            var column = new PocketColumn(cx, cz, layer);
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var run = 0;
                for (var y = column.Height - 2; y >= 1; y--)
                {
                    var block = column.At(x, y, z);
                    if (!IsFlora(block) || !HellGenerator.InHell(column.Y0 + y))
                    {
                        run = 0;
                        continue;
                    }

                    int wx = column.X0 + x, wy = column.Y0 + y, wz = column.Z0 + z;
                    // Flora takes pocket air only: never rock, a pool cell or lava
                    Assert.True(HellGenerator.IsOpen(wx, wy, wz) && !HellGenerator.InPool(wy),
                        $"flora at ({wx}, {wy}, {wz}) outside the pocket's air");
                    var palette = HellBiomes.Palette(HellBiomes.At(wx, wy, wz));

                    if (block == HellFlora.VineBlock)
                    {
                        Assert.True(palette.VineRate > 0, $"a vine in a vineless biome at ({wx}, {wy}, {wz})");
                        var above = column.At(x, y + 1, z);
                        Assert.True(above == HellFlora.VineBlock || BlockType.IsSolid(above),
                            $"a vine at ({wx}, {wy}, {wz}) hangs from {BlockType.ById[above]?.Name ?? "air"}");
                        run++;
                        longVine |= run > 1;
                        vines[biome] = vines.GetValueOrDefault(biome) + 1;
                        continue;
                    }

                    run = 0;
                    Assert.Equal(palette.GroundPlant, block);
                    Assert.Equal(palette.Surface, column.At(x, y - 1, z));
                    plants[biome] = plants.GetValueOrDefault(biome) + 1;
                }
            }
        }

        // Every biome with a vine rate or a plant shows them, and the others never do
        foreach (var biome in AllBiomes)
        {
            var palette = HellBiomes.Palette(biome);
            Assert.Equal(palette.VineRate > 0, vines.GetValueOrDefault(biome) > 0);
            Assert.Equal(palette.GroundPlant != BlockType.Air, plants.GetValueOrDefault(biome) > 0);
        }
        Assert.True(longVine, "no vine is longer than one cell");
    }

    [Fact]
    public void TheStampEqualsThePointQueryWithTheFloraInPlace()
    {
        var blocks = new ushort[Volume];
        var floraSeen = new HashSet<ushort>();
        foreach (var biome in AllBiomes)
        {
            var (cx, cz) = ColumnIn(biome, 0, skip: 1);
            var top = LayerTop(0);
            var compared = 0;
            for (var cy = (top - HellGenerator.PocketHeight + 1) >> Chunk.SizeShift; cy <= top >> Chunk.SizeShift && compared < 2; cy++)
            {
                int x0 = cx * Chunk.Size, y0 = cy * Chunk.Size, z0 = cz * Chunk.Size;
                HellGenerator.Stamp(blocks, x0, y0, z0);
                if (!blocks.Any(IsFlora) && HellBiomes.Palette(biome).VineRate > 0) continue;
                compared++;

                for (var x = 0; x < Chunk.Size; x++)
                for (var y = 0; y < Chunk.Size; y++)
                for (var z = 0; z < Chunk.Size; z++)
                {
                    var block = blocks[Chunk.Idx(x, y, z)];
                    Assert.Equal(HellGenerator.BlockAt(x0 + x, y0 + y, z0 + z), block);
                    if (IsFlora(block)) floraSeen.Add(block);
                }
            }
        }

        Assert.Contains(HellFlora.VineBlock, floraSeen);
        Assert.True(floraSeen.Count(IsPlant) >= 2, "the compared chunks hold fewer than two kinds of plant");
    }

    [Fact]
    public void TheFloraIsAPureFunctionOfPositionAndSeed()
    {
        var (cx, cz) = ColumnIn(HellBiome.FungalGrove, 0);
        var first = Flora();
        Assert.NotEmpty(first);
        Assert.Equal(first, Flora());
        try
        {
            WorldSeed.Apply(WorldSeed.Default + 11);
            Assert.NotEqual(first, Flora());
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        Assert.Equal(first, Flora());

        List<(int Index, ushort Block)> Flora()
        {
            var column = new PocketColumn(cx, cz, 0);
            var list = new List<(int, ushort)>();
            for (var i = 0; i < column.Blocks.Length; i++)
                if (IsFlora(column.Blocks[i])) list.Add((i, column.Blocks[i]));
            return list;
        }
    }

    /// <summary>Every hell tree rooted over the given chunk columns' tree cells in the first layers.</summary>
    private static List<(TreeBatches.TreeShape Tree, int CellX, int CellZ, int Layer)> TreesOver(HellBiome biome, int columns)
    {
        var trees = new List<(TreeBatches.TreeShape, int, int, int)>();
        for (var layer = 0; layer < 2; layer++)
        for (var c = 0; c < columns; c++)
        {
            var (cx, cz) = ColumnIn(biome, layer, c);
            for (var tx = HellTrees.CellOf(cx * Chunk.Size); tx <= HellTrees.CellOf(cx * Chunk.Size + Chunk.Size - 1); tx++)
            for (var tz = HellTrees.CellOf(cz * Chunk.Size); tz <= HellTrees.CellOf(cz * Chunk.Size + Chunk.Size - 1); tz++)
                if (HellTrees.TreeOf(tx, tz, layer) is { } tree)
                    trees.Add((tree, tx, tz, layer));
        }
        return trees;
    }

    [Fact]
    public void TreesRootOnTheirBiomesSurfaceAndGrowOnlyIntoPocketAirInsideTheirCell()
    {
        const int cell = WorldGenerationConfig.HellTreeCellBlocks;
        var leaves = 0;
        foreach (var biome in AllBiomes)
        {
            var palette = HellBiomes.Palette(biome);
            var trees = TreesOver(biome, 4);
            Assert.Equal(palette.Tree != null, trees.Count > 0);

            foreach (var (tree, tx, tz, layer) in trees)
            {
                Assert.Same(HellBiomes.Palette(HellBiomes.AtLayer(tree.X, layer, tree.Z)).Tree, tree.Species);
                Assert.Equal(palette.Surface, HellGenerator.BlockAt(tree.X, tree.BaseY - 1, tree.Z));
                Assert.Equal(layer, HellGenerator.LayerIndex(tree.BaseY));
                Assert.True(tree.Cells.Length > 1, "a tree is a lone cell");
                foreach (var c in tree.Cells)
                {
                    Assert.InRange(c.X, tx * cell, tx * cell + cell - 1);
                    Assert.InRange(c.Z, tz * cell, tz * cell + cell - 1);
                    Assert.True(HellGenerator.IsOpen(c.X, c.Y, c.Z) && !HellGenerator.InPool(c.Y),
                        $"a {tree.Species.Name} cell at ({c.X}, {c.Y}, {c.Z}) outside the pocket's air");
                    if (c.Block == tree.Species.LeavesId) leaves++;
                }
            }
        }

        Assert.True(leaves > 0, "no hell tree grew a single leaf");
    }

    [Fact]
    public void ATreeIsTheSameWhicheverChunkStampsItAndAcrossACacheReset()
    {
        // A tree crossing a chunk seam vertically: its cells come from two or more chunks
        var (tree, tx, tz, layer) = TreesOver(HellBiome.FungalGrove, 6)
            .First(t => t.Tree.MinY >> Chunk.SizeShift != t.Tree.MaxY >> Chunk.SizeShift);
        var expected = tree.Cells.ToDictionary(c => (c.X, c.Y, c.Z), c => (c.Block, c.State));

        var stamped = new Dictionary<(int, int, int), (ushort, byte)>();
        var x0 = tree.X >> Chunk.SizeShift << Chunk.SizeShift;
        var z0 = tree.Z >> Chunk.SizeShift << Chunk.SizeShift;
        for (var cy = tree.MinY >> Chunk.SizeShift; cy <= tree.MaxY >> Chunk.SizeShift; cy++)
        {
            var chunk = new Chunk(null!, new Vector3(x0 >> Chunk.SizeShift, cy, z0 >> Chunk.SizeShift), generateBlockData: false);
            ChunkTerrainFill.GenerateBlockData(chunk);
            var state = chunk.StateArray;
            Assert.NotNull(state);
            foreach (var c in tree.Cells)
            {
                if (c.Y >> Chunk.SizeShift != cy) continue;
                var index = Chunk.Idx(c.X - x0, c.Y - cy * Chunk.Size, c.Z - z0);
                stamped[(c.X, c.Y, c.Z)] = (chunk.Blocks[index], state[index]);
            }
        }

        Assert.Equal(expected.OrderBy(e => e.Key), stamped.OrderBy(e => e.Key));

        // A pure function of its cell: regrown from nothing after the cache is dropped, it matches
        WorldSeed.Apply(WorldSeed.Default);
        var regrown = HellTrees.TreeOf(tx, tz, layer);
        Assert.NotNull(regrown);
        Assert.NotSame(tree, regrown);
        Assert.Equal(tree.Cells, regrown.Cells);
    }

    [Fact]
    public void MiningAVineCellOrItsCeilingDropsTheVineBelowIt()
    {
        _world = new SupportTestWorld();
        var vine = HellFlora.VineBlock;
        var ground = _world.Top;
        var (x, z) = (5, 5);
        var ceiling = ground + 7;
        _world.Load(x, ceiling, z);
        _world.Set(x, ceiling, z, _world.Stone);
        for (var y = ceiling - 1; y >= ceiling - 5; y--)
            _world.Set(x, y, z, vine);

        // A cut in the middle drops everything under it and leaves the part above hanging
        Assert.True(_world.Break(x, ceiling - 3, z));
        for (var y = ceiling - 5; y <= ceiling - 3; y++)
            Assert.Equal(BlockType.Air, _world.IdAt(x, y, z));
        Assert.Equal(vine, _world.IdAt(x, ceiling - 1, z));
        Assert.Equal(vine, _world.IdAt(x, ceiling - 2, z));

        // The ceiling takes the rest
        Assert.True(_world.Break(x, ceiling, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ceiling - 1, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ceiling - 2, z));
    }

    [Fact]
    public void ABlastThatTakesAVinesCeilingDropsTheWholeVineEvenPastItsReach()
    {
        var vine = HellFlora.VineBlock;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var origin = center.Position * Chunk.Size;
        ushort At(int x, int y, int z) => manager.GetBlockGlobal((int)origin.X + x, (int)origin.Y + y, (int)origin.Z + z);

        // A ceiling cell inside the blast, its vine hanging down through the blast's edge and on below
        // the box the support scan covers; a second vine under a ceiling the blast never reaches
        center.SetBlockRaw(9, 9, 9, stone, 0);
        for (var y = 8; y >= 2; y--)
            center.SetBlockRaw(9, y, 9, vine, 0);
        center.SetBlockRaw(1, 9, 1, stone, 0);
        center.SetBlockRaw(1, 8, 1, vine, 0);
        center.SetBlockRaw(1, 7, 1, vine, 0);

        var tnt = new TntManager();
        tnt.Spawn(origin + new Vector3(7, 11, 9), ContentRegistry.RequireBlock("tnt").Id, 0f);
        tnt.Update(1e-4f, manager, new LodManager(null!), new DropManager(), new FluidManager(), new FallingBlockManager());

        Assert.Equal(BlockType.Air, At(9, 9, 9));
        for (var y = 8; y >= 2; y--)
            Assert.Equal(BlockType.Air, At(9, y, 9));
        Assert.Equal(vine, At(1, 8, 1));
        Assert.Equal(vine, At(1, 7, 1));
    }
}
