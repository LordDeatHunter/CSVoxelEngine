using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Lod;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

[Collection("LOD column caps")]
public sealed class FallingBlockTests
{
    public FallingBlockTests() => TestContent.EnsureLoaded();

    private static (ChunkManager Chunks, LodManager Lod, FluidManager Fluids, DropManager Drops,
        FallingBlockManager Falling, byte Stone, byte Sand) Setup()
    {
        var chunks = new ChunkManager(null!);
        chunks.Chunks[Vector3.Zero] = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunks.Chunks[Vector3.Zero].IsBlockDataReady = true;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var sand = ContentRegistry.RequireBlock("sand").Id;
        return (chunks, new LodManager(null!), new FluidManager(), new DropManager(),
            new FallingBlockManager(), stone, sand);
    }

    private static void SettleAll(FallingBlockManager falling, ChunkManager chunks,
        DropManager drops, FluidManager fluids)
    {
        for (var i = 0; i < 600 && falling.Falling.Count > 0; i++)
            falling.Update(1f / 60f, chunks, drops, fluids);
    }

    [Fact]
    public void ToppledSandFallsAndLandsOnTheFloor()
    {
        var (chunks, lod, fluids, drops, falling, stone, sand) = Setup();
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var x = 0; x < 3; x++)
        for (var z = 0; z < 3; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        chunk.SetBlockRaw(1, 3, 1, sand, 0);

        Assert.True(falling.TryTopple(chunks, lod, fluids, 1, 3, 1));
        Assert.Equal(BlockType.Air, chunks.GetBlockGlobal(1, 3, 1));
        Assert.Single(falling.Falling);

        SettleAll(falling, chunks, drops, fluids);

        Assert.Empty(falling.Falling);
        Assert.Equal(sand, chunks.GetBlockGlobal(1, 1, 1));
    }

    [Fact]
    public void SandSlidesOffALonePillar()
    {
        var (chunks, lod, fluids, drops, falling, stone, sand) = Setup();
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var x = 0; x < 5; x++)
        for (var z = 0; z < 5; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        chunk.SetBlockRaw(2, 1, 2, stone, 0);
        chunk.SetBlockRaw(2, 3, 2, sand, 0);

        Assert.True(falling.TryTopple(chunks, lod, fluids, 2, 3, 2));

        SettleAll(falling, chunks, drops, fluids);

        // A lone pillar always slides (0 of 8 neighbors): the cube must end up beside the
        // pillar on the floor, on an orthogonal side or a corner
        Assert.Empty(falling.Falling);
        Assert.Equal(BlockType.Air, chunks.GetBlockGlobal(2, 3, 2));
        var landed = chunks.GetBlockGlobal(1, 1, 2) == sand ? (1, 1, 2)
            : chunks.GetBlockGlobal(3, 1, 2) == sand ? (3, 1, 2)
            : chunks.GetBlockGlobal(2, 1, 1) == sand ? (2, 1, 1)
            : chunks.GetBlockGlobal(2, 1, 3) == sand ? (2, 1, 3)
            : chunks.GetBlockGlobal(1, 1, 1) == sand ? (1, 1, 1)
            : chunks.GetBlockGlobal(3, 1, 1) == sand ? (3, 1, 1)
            : chunks.GetBlockGlobal(1, 1, 3) == sand ? (1, 1, 3)
            : (3, 1, 3);
        Assert.Equal(sand, chunks.GetBlockGlobal(landed.Item1, landed.Item2, landed.Item3));
    }

    [Fact]
    public void ToppledTowerConservesEveryCube()
    {
        var (chunks, lod, fluids, drops, falling, stone, sand) = Setup();
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var x = 0; x < 7; x++)
        for (var z = 0; z < 7; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        chunk.SetBlockRaw(3, 1, 3, sand, 0);
        chunk.SetBlockRaw(3, 2, 3, sand, 0);
        chunk.SetBlockRaw(3, 3, 3, sand, 0);

        // Mine the bottom cube out from under the tower, like RemoveBlock does
        Assert.True(chunk.RemoveBlockAt(new Vector3(3, 1, 3), chunks));
        Assert.True(falling.TryTopple(chunks, lod, fluids, 3, 2, 3));
        Assert.Equal(2, falling.Falling.Count);

        SettleAll(falling, chunks, drops, fluids);

        // Two cubes toppled together: the batch is flagged a chain collapse, so the upper
        // stacks on the sand base and the tower rebuilds exactly with nothing lost
        Assert.Empty(falling.Falling);
        Assert.Empty(drops.Drops);
        Assert.Equal(sand, chunks.GetBlockGlobal(3, 1, 3));
        Assert.Equal(sand, chunks.GetBlockGlobal(3, 2, 3));
        var placed = 0;
        for (var x = 0; x < 7; x++)
        for (var y = 1; y < 6; y++)
        for (var z = 0; z < 7; z++)
        {
            if (chunks.GetBlockGlobal(x, y, z) != sand) continue;
            placed++;
            Assert.True(BlockType.IsSolid(chunks.GetBlockGlobal(x, y - 1, z)),
                $"sand at ({x},{y},{z}) stands on air");
        }
        Assert.Equal(2, placed);
    }

    [Fact]
    public void SevenTowerChainRebuildsFullyWithNoDrops()
    {
        var (chunks, lod, fluids, drops, falling, stone, sand) = Setup();
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var x = 0; x < 9; x++)
        for (var z = 0; z < 9; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        for (var y = 1; y <= 7; y++)
            chunk.SetBlockRaw(4, y, 4, sand, 0);

        // Mine the base: the six cubes above come down as one flagged batch, so every one
        // stacks and the column rebuilds at y 1..6 with nothing avalanching or popping out
        Assert.True(chunk.RemoveBlockAt(new Vector3(4, 1, 4), chunks));
        Assert.True(falling.TryTopple(chunks, lod, fluids, 4, 2, 4));
        Assert.Equal(6, falling.Falling.Count);
        Assert.All(falling.Falling, cube => Assert.True(cube.ChainCollapse));

        SettleAll(falling, chunks, drops, fluids);

        Assert.Empty(falling.Falling);
        Assert.Empty(drops.Drops);
        for (var y = 1; y <= 6; y++)
            Assert.Equal(sand, chunks.GetBlockGlobal(4, y, 4));
        Assert.Equal(BlockType.Air, chunks.GetBlockGlobal(4, 7, 4));
    }

    [Fact]
    public void SandSlidesOffSandPillarLikeStone()
    {
        var (chunks, lod, fluids, drops, falling, stone, sand) = Setup();
        var chunk = chunks.Chunks[Vector3.Zero];
        for (var x = 0; x < 5; x++)
        for (var z = 0; z < 5; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        chunk.SetBlockRaw(2, 1, 2, sand, 0);
        chunk.SetBlockRaw(2, 2, 2, sand, 0);
        chunk.SetBlockRaw(2, 3, 2, sand, 0);

        // Mine the middle out: the top cube drops onto a lone sand support ringed by
        // air, which always slides — sand slumps like stone instead of stacking
        Assert.True(chunk.RemoveBlockAt(new Vector3(2, 2, 2), chunks));
        Assert.True(falling.TryTopple(chunks, lod, fluids, 2, 3, 2));

        // A lone grain is not a chain collapse, so it must slide rather than stack
        var lone = Assert.Single(falling.Falling);
        Assert.False(lone.ChainCollapse);

        SettleAll(falling, chunks, drops, fluids);

        Assert.Empty(falling.Falling);
        Assert.Empty(drops.Drops);
        Assert.Equal(sand, chunks.GetBlockGlobal(2, 1, 2));
        Assert.Equal(BlockType.Air, chunks.GetBlockGlobal(2, 2, 2));
        var landed = chunks.GetBlockGlobal(1, 1, 2) == sand ? (1, 1, 2)
            : chunks.GetBlockGlobal(3, 1, 2) == sand ? (3, 1, 2)
            : chunks.GetBlockGlobal(2, 1, 1) == sand ? (2, 1, 1)
            : chunks.GetBlockGlobal(2, 1, 3) == sand ? (2, 1, 3)
            : chunks.GetBlockGlobal(1, 1, 1) == sand ? (1, 1, 1)
            : chunks.GetBlockGlobal(3, 1, 1) == sand ? (3, 1, 1)
            : chunks.GetBlockGlobal(1, 1, 3) == sand ? (1, 1, 3)
            : (3, 1, 3);
        Assert.Equal(sand, chunks.GetBlockGlobal(landed.Item1, landed.Item2, landed.Item3));
    }

    [Fact]
    public void SupportedSandNeverTopples()
    {
        var (chunks, lod, fluids, _, falling, stone, sand) = Setup();
        var chunk = chunks.Chunks[Vector3.Zero];
        chunk.SetBlockRaw(1, 0, 1, stone, 0);
        chunk.SetBlockRaw(1, 1, 1, sand, 0);

        Assert.False(falling.TryTopple(chunks, lod, fluids, 1, 1, 1));
        Assert.Empty(falling.Falling);
        Assert.Equal(sand, chunks.GetBlockGlobal(1, 1, 1));
    }

    [Fact]
    public void LiveCubeBlocksItsCellForPlacement()
    {
        var (_, _, _, _, falling, _, sand) = Setup();
        falling.Spawn(new Vector3(2, 2, 2), sand);

        Assert.True(falling.OverlapsAny(2, 2, 2));
        Assert.False(falling.OverlapsAny(5, 5, 5));
    }

    [Fact]
    public void FallingCubeShovesPlayerAsideOneWay()
    {
        var (chunks, _, _, _, _, _, sand) = Setup();
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(1.5f, 1f, 1.5f), yaw: 0f, pitch: 0f);
        var cube = new FallingBlock(new Vector3(1.5f, 0.5f, 1.5f), sand);

        camera.PushOutOf(cube.Position, FallingBlock.HalfExtent, chunks);

        // Eye tied with the cube centre goes -X by the full expulsion, clearing the body
        Assert.True(camera.Position.X < 1.5f);
        Assert.True(camera.Position.X + 0.3f <= 1.5f - FallingBlock.HalfExtent + 1e-4f);
        Assert.Equal(new Vector3(1.5f, 0.5f, 1.5f), cube.Position);
    }

    [Fact]
    public void WedgedCubeBreaksIntoDropsInsteadOfHovering()
    {
        var (chunks, _, fluids, drops, falling, stone, sand) = Setup();
        chunks.Chunks[Vector3.Zero].SetBlockRaw(1, 1, 1, stone, 0);
        falling.Spawn(new Vector3(1, 1, 1), sand);

        for (var i = 0; i < 400 && falling.Falling.Count > 0; i++)
            falling.Update(1f / 60f, chunks, drops, fluids);

        Assert.Empty(falling.Falling);
        var drop = Assert.Single(drops.Drops);
        Assert.Equal(sand, drop.BlockId);
    }

    [Fact]
    public void BeachBandRingsSeaLevel()
    {
        var sea = WorldGenerationConfig.SeaLevel;
        Assert.True(TerrainGenerator.IsBeachHeight(sea));
        Assert.True(TerrainGenerator.IsBeachHeight(sea + WorldGenerationConfig.BeachAboveSeaLevel));
        Assert.True(TerrainGenerator.IsBeachHeight(sea - WorldGenerationConfig.BeachBelowSeaLevel));
        Assert.False(TerrainGenerator.IsBeachHeight(sea + WorldGenerationConfig.BeachAboveSeaLevel + 1));
        Assert.False(TerrainGenerator.IsBeachHeight(sea - WorldGenerationConfig.BeachBelowSeaLevel - 1));
    }

    // Pure picker tests: a fake solidity grid around rest (0,2,0) on support (0,1,0),
    // so direction choice and clearance rules are pinned without running any physics.
    private static System.Func<int, int, int, bool> Grid(
        System.Collections.Generic.HashSet<(int X, int Y, int Z)> solid) =>
        (x, y, z) => solid.Contains((x, y, z));

    [Fact]
    public void BuriedSupportNeverSlides()
    {
        var solid = new System.Collections.Generic.HashSet<(int, int, int)>();
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
            solid.Add((dx, 1, dz));
        Assert.Null(FallingBlock.PickSlideDirection(0, 2, 0, null, Grid(solid), new System.Random(1)));
    }

    [Fact]
    public void CommittedDirectionIsKeptWhileClear()
    {
        var solid = new System.Collections.Generic.HashSet<(int, int, int)> { (0, 1, 0), (0, 0, 0) };
        Assert.Equal((1, 0), FallingBlock.PickSlideDirection(
            0, 2, 0, (1, 0), Grid(solid), new System.Random(1)));
    }

    [Fact]
    public void CommittedDiagonalIsKeptWhileClear()
    {
        var solid = new System.Collections.Generic.HashSet<(int, int, int)> { (0, 1, 0), (0, 0, 0) };
        Assert.Equal((1, 1), FallingBlock.PickSlideDirection(
            0, 2, 0, (1, 1), Grid(solid), new System.Random(1)));
    }

    [Fact]
    public void HalfChanceSlidesAboutHalfTheTime()
    {
        // Four solid ring neighbors: every pick is a 50/50 roll, and every success lands on
        // a corner (the orthogonal below-sides are the solid ring itself)
        var solid = new System.Collections.Generic.HashSet<(int, int, int)>
            { (0, 1, 0), (1, 1, 0), (-1, 1, 0), (0, 1, 1), (0, 1, -1) };
        var rng = new System.Random(42);
        var slides = 0;
        for (var i = 0; i < 2000; i++)
        {
            var pick = FallingBlock.PickSlideDirection(0, 2, 0, null, Grid(solid), rng);
            if (pick.HasValue)
            {
                slides++;
                Assert.True(pick.Value.X != 0 && pick.Value.Z != 0);
            }
        }
        Assert.InRange(slides, 850, 1150);
    }

    [Fact]
    public void DiagonalNeedsItsNeighbouringSidesClear()
    {
        // Only the (1,1) corner has an open target, but its neighbouring side is walled and
        // every other side is shut: nothing may move
        var solid = new System.Collections.Generic.HashSet<(int, int, int)>
        {
            (0, 1, 0),
            (1, 2, 0), (-1, 2, 0), (0, 2, 1), (0, 2, -1), // orthogonal targets shut
            (-1, 2, 1), (-1, 2, -1), (1, 2, -1),          // other corner targets shut
        };
        Assert.Null(FallingBlock.PickSlideDirection(0, 2, 0, null, Grid(solid), new System.Random(1)));
    }

    [Fact]
    public void DiagonalNeedsHeadroomAboveTheMove()
    {
        // (1,1) is the only open corner target with clear sides, but a block hangs over it
        var solid = new System.Collections.Generic.HashSet<(int, int, int)>
        {
            (0, 1, 0),
            (1, 2, 0), (-1, 2, 0), (0, 2, 1), (0, 2, -1),
            (-1, 2, 1), (-1, 2, -1), (1, 2, -1),
            (1, 3, 1),
        };
        Assert.Null(FallingBlock.PickSlideDirection(0, 2, 0, null, Grid(solid), new System.Random(1)));
    }
}
