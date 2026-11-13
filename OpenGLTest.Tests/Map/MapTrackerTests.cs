using System.Linq;
using System.Numerics;
using VoxelEngine.Map;
using Xunit;

namespace VoxelEngine.Tests.Map;

public sealed class MapTrackerTests
{
    private readonly ushort _stone;
    private readonly ushort _dirt;

    public MapTrackerTests()
    {
        TestContent.EnsureLoaded();
        _stone = ContentRegistry.RequireBlock("stone").Id;
        _dirt = ContentRegistry.RequireBlock("dirt").Id;
    }

    // Flat stone ground at y 0..10 over chunk columns -1..1, all of it loaded, the player standing on it
    private FakeMapCells Ground()
    {
        var cells = new FakeMapCells();
        for (var cx = -1; cx <= 1; cx++)
        for (var cz = -1; cz <= 1; cz++)
        for (var cy = -1; cy <= 12; cy++)
            cells.Load(cx, cy, cz);
        for (var x = -16; x < 32; x++)
        for (var z = -16; z < 32; z++)
            cells.Column(x, z, 0, 10, _stone);
        return cells;
    }

    private static readonly Vector3 Eye = new(8.5f, 12.6f, 8.5f);

    // Enough updates to scan every column the tracker queued around the player
    private static void Settle(MapTracker tracker)
    {
        const int r = MapConfig.ScanRadiusChunks;
        var updates = (2 * r + 1) * (2 * r + 1) / MapConfig.TilesPerFrame + 1;
        for (var i = 0; i < updates; i++)
            tracker.Update(Eye, 0.016f);
    }

    [Fact]
    public void EveryLoadedColumnAroundThePlayerGetsATileAndNoOtherDoes()
    {
        var tracker = new MapTracker(Ground());
        Settle(tracker);

        for (var cx = -1; cx <= 1; cx++)
        for (var cz = -1; cz <= 1; cz++)
            Assert.True(tracker.TryGetTile(cx, cz, out _), $"no tile at {cx}, {cz}");
        Assert.False(tracker.TryGetTile(2, 0, out _));
        Assert.Equal(9, tracker.TileCount);
    }

    [Fact]
    public void ARemeshedColumnIsRescannedAndShowsTheEdit()
    {
        var cells = Ground();
        var tracker = new MapTracker(cells);
        Settle(tracker);
        tracker.TryGetTile(1, 0, out var before);
        var texelBefore = before.Texels[MapTile.IndexOf(4, 5)];

        // A block placed on the ground in chunk column (1, 0): its chunk remeshes, which marks the column
        cells.Set(16 + 4, 11, 5, _dirt);
        tracker.MarkColumnDirty(1, 0);
        tracker.Update(Eye, 0.016f);

        var changed = Assert.Single(tracker.Changed);
        Assert.Equal((1, 0), (changed.ChunkX, changed.ChunkZ));
        tracker.TryGetTile(1, 0, out var after);
        Assert.NotEqual(texelBefore, after.Texels[MapTile.IndexOf(4, 5)]);
        Assert.Equal(11, after.Heights[MapTile.IndexOf(4, 5)]);
    }

    [Fact]
    public void ARescanThatFindsNothingKeepsTheExploredTile()
    {
        var cells = Ground();
        var tracker = new MapTracker(cells);
        Settle(tracker);
        tracker.TryGetTile(1, 1, out var explored);
        var texels = explored.Texels.ToArray();

        for (var cy = -1; cy <= 12; cy++) cells.Unload(1, cy, 1);
        tracker.MarkColumnDirty(1, 1);
        tracker.Update(Eye, 0.016f);

        Assert.Empty(tracker.Changed);
        Assert.True(tracker.TryGetTile(1, 1, out var kept));
        Assert.Equal(texels, kept.Texels);
    }

    [Fact]
    public void ARoofOverTheEyeMeansTheCaveSliceAndOpenSkyTheSurface()
    {
        var cells = Ground();
        Assert.False(MapTracker.IsRoofed(cells, Eye));

        cells.Set(8, 20, 8, _stone);
        Assert.True(MapTracker.IsRoofed(cells, Eye));

        // A roof beyond the probe is open sky as far as the map is concerned
        cells.Set(8, 20, 8, BlockType.Air);
        cells.Set(8, (int)Eye.Y + MapConfig.RoofProbeCells + 2, 8, _stone);
        Assert.False(MapTracker.IsRoofed(cells, Eye));
    }

    [Fact]
    public void LeavingACaveSliceDropsItsTilesButKeepsTheSurfaceOnes()
    {
        var cells = Ground();
        var tracker = new MapTracker(cells);
        Settle(tracker);
        var surfaceTiles = tracker.TileCount;

        cells.Set(8, 20, 8, _stone);
        tracker.Update(Eye, MapConfig.LayerSwitchSeconds * 2f);
        Assert.Equal(MapLayerKind.Cave, tracker.Layer.Kind);
        Settle(tracker);
        Assert.True(tracker.TileCount > surfaceTiles);

        cells.Set(8, 20, 8, BlockType.Air);
        tracker.Update(Eye, MapConfig.LayerSwitchSeconds * 2f);
        Assert.Equal(MapLayerKind.Surface, tracker.Layer.Kind);
        Assert.Equal(surfaceTiles, tracker.TileCount);
    }
}
