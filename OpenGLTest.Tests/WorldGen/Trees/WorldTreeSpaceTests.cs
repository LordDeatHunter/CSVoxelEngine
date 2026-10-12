using System;
using System.Linq;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>
/// Freeness rules of <see cref="WorldTreeSpace"/>: growth may write only into air or the species'
/// own non-persistent leaves, never into any other block, a reserved cell, an unloaded cell or the
/// missing parent of a cut branch; a sapling root may also take over placement-replaceable cover.
/// </summary>
public sealed class WorldTreeSpaceTests
{
    private const int X = 8, Y = 8, Z = 8;

    public WorldTreeSpaceTests() => TestContent.EnsureLoaded();

    private static TreeSpecies Leafy => StampedTree.WorldgenSpecies.First(s => s.HasLeaves);

    /// <summary>A fresh manager with the 3x3x3 chunks around the probe cell loaded, all air.</summary>
    private static ChunkManager Loaded()
    {
        var chunks = new ChunkManager(null!);
        StampedTree.LoadAround(chunks, new[] { (X, Y, Z) }, Chunk.Size);
        return chunks;
    }

    private static void Write(ChunkManager chunks, (int X, int Y, int Z) cell, byte id, byte state = 0) =>
        StampedTree.WriteCell(chunks, cell, id, state);

    [Fact]
    public void AirAndOwnLeavesAreFree()
    {
        var species = Leafy;
        var chunks = Loaded();
        var space = new WorldTreeSpace(chunks, species);
        Assert.True(space.IsFree(X, Y, Z));

        Write(chunks, (X, Y, Z), species.LeavesId, LeafState.Encode(LeafState.MaxHydration, false));
        Assert.True(new WorldTreeSpace(chunks, species).IsFree(X, Y, Z));
    }

    [Fact]
    public void PersistentOwnLeavesAreNotFree()
    {
        var species = Leafy;
        var chunks = Loaded();
        Write(chunks, (X, Y, Z), species.LeavesId, LeafState.Encode(LeafState.MaxHydration, true));
        Assert.False(new WorldTreeSpace(chunks, species).IsFree(X, Y, Z));
    }

    [Fact]
    public void EveryOtherBlockIsNotFree()
    {
        var species = Leafy;
        var other = StampedTree.WorldgenSpecies.First(s => s.HasLeaves && s.LeavesId != species.LeavesId);
        var fluid = (byte)Enumerable.Range(1, 255).First(id => BlockType.ById[id] != null && BlockType.IsFluid((byte)id));

        foreach (var (id, state) in new[]
                 {
                     (other.LeavesId, LeafState.Encode(LeafState.MaxHydration, false)),
                     (species.WoodId, WoodState.Encode(0, WoodState.ParentRoot, false)),
                     (TerrainGenerator.RockBlock, (byte)0),
                     (fluid, (byte)0),
                 })
        {
            var chunks = Loaded();
            Write(chunks, (X, Y, Z), id, state);
            Assert.False(new WorldTreeSpace(chunks, species).IsFree(X, Y, Z),
                $"block {BlockType.ById[id]!.Name} (state {state}) read as free");
        }
    }

    [Fact]
    public void AnUnloadedCellIsNotFree()
    {
        var species = Leafy;
        var chunks = new ChunkManager(null!);
        StampedTree.EnsureChunk(chunks, 0, 0, 0);
        var space = new WorldTreeSpace(chunks, species);

        var outside = Chunk.Size + X;
        Assert.False(space.IsFree(outside, Y, Z));
        Assert.False(space.IsPlantable(outside, Y, Z));
    }

    [Fact]
    public void AReservedCellIsNotFree()
    {
        var species = Leafy;
        var chunks = Loaded();
        var space = new WorldTreeSpace(chunks, species, (x, y, z) => x == X && y == Y && z == Z);

        Assert.False(space.IsFree(X, Y, Z));
        Assert.True(space.IsFree(X + 1, Y, Z));
        Assert.True(space.IsFree(X, Y + 2, Z));
    }

    [Fact]
    public void AnOrphansMissingParentIsNotFree()
    {
        var species = Leafy;
        var pointsDown = WoodState.Encode(0, TreeDirections.Down, false);

        var orphaned = Loaded();
        Write(orphaned, (X, Y + 1, Z), species.WoodId, pointsDown);
        var space = new WorldTreeSpace(orphaned, species);
        Assert.False(space.IsFree(X, Y, Z));
        // Only the named parent is refused: the cell on the other side of the orphan stays free
        Assert.True(space.IsFree(X, Y + 2, Z));

        var detached = Loaded();
        Write(detached, (X, Y + 1, Z), species.WoodId, WoodState.WithParent(pointsDown, WoodState.ParentDetached));
        Assert.True(new WorldTreeSpace(detached, species).IsFree(X, Y, Z));
    }

    [Fact]
    public void CoverIsPlantableButNotFree()
    {
        var species = Leafy;
        var cover = Enumerable.Range(1, 255)
            .Where(id => BlockType.ById[id] != null)
            .SelectMany(id => Enumerable.Range(0, 256).Select(state => (Id: (byte)id, State: (byte)state)))
            .First(c => BlockType.IsReplaceableByPlacement(c.Id, c.State));

        var chunks = Loaded();
        Write(chunks, (X, Y, Z), cover.Id, cover.State);
        var space = new WorldTreeSpace(chunks, species);
        Assert.True(space.IsPlantable(X, Y, Z));
        Assert.False(space.IsFree(X, Y, Z));

        Assert.True(space.IsFree(X, Y + 2, Z));
        Assert.True(space.IsPlantable(X, Y + 2, Z));
    }
}
