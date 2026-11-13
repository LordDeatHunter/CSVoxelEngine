using VoxelEngine.Map;
using Xunit;

namespace VoxelEngine.Tests.Map;

public sealed class MapColumnScannerTests
{
    private readonly ushort _stone;
    private readonly ushort _water;
    private readonly ushort _plant;

    public MapColumnScannerTests()
    {
        TestContent.EnsureLoaded();
        _stone = ContentRegistry.RequireBlock("stone").Id;
        _water = ContentRegistry.RequireBlock("water").Id;
        _plant = ContentRegistry.RequireBlock("tall_grass").Id;
    }

    private static FakeMapCells LoadedColumn()
    {
        var cells = new FakeMapCells();
        for (var cy = -2; cy <= 12; cy++) cells.Load(0, cy, 0);
        return cells;
    }

    private static MapTile Scan(IMapCells cells, MapLayer layer)
    {
        var tile = new MapTile(0, 0);
        MapColumnScanner.Scan(cells, layer, tile, north: null);
        return tile;
    }

    private static float Brightness(uint texel) => (texel & 0xFF) + ((texel >> 8) & 0xFF) + ((texel >> 16) & 0xFF);

    [Fact]
    public void APlantIsSeenThroughToTheGroundUnderIt()
    {
        var cells = LoadedColumn();
        cells.Column(3, 0, 0, 10, _stone);
        cells.Column(4, 0, 0, 10, _stone);
        cells.Set(4, 11, 0, _plant);

        var tile = Scan(cells, MapLayer.Surface);

        Assert.Equal(tile.Texels[MapTile.IndexOf(3, 0)], tile.Texels[MapTile.IndexOf(4, 0)]);
        Assert.Equal(10, tile.Heights[MapTile.IndexOf(4, 0)]);
    }

    [Fact]
    public void DeeperWaterIsDarker()
    {
        var cells = LoadedColumn();
        cells.Column(3, 0, 0, 9, _stone);
        cells.Set(3, 10, 0, _water);
        cells.Column(4, 0, 0, 4, _stone);
        cells.Column(4, 0, 5, 10, _water);

        var tile = Scan(cells, MapLayer.Surface);

        Assert.True(Brightness(tile.Texels[MapTile.IndexOf(4, 0)]) < Brightness(tile.Texels[MapTile.IndexOf(3, 0)]));
    }

    [Fact]
    public void AColumnWhoseChunksAreUnloadedStaysUnexplored()
    {
        var cells = new FakeMapCells();
        cells.Column(3, 0, 0, 10, _stone);

        var tile = Scan(cells, MapLayer.Surface);

        Assert.False(tile.AnyKnown);
        Assert.Equal(0u, tile.Texels[MapTile.IndexOf(3, 0)] >> 24);
    }

    [Fact]
    public void ACaveSliceStartingInRockReadsAsRockAndAnOpenOneShowsItsFloor()
    {
        var cells = LoadedColumn();
        var slice = MapLayer.CaveAt(20f);
        // Column 1 is solid through the slice's top; column 2 is a cave whose floor lies a few cells down
        cells.Column(1, 0, slice.BottomY, slice.TopY + 4, _stone);
        cells.Column(2, 0, slice.BottomY, slice.TopY - 6, _stone);
        cells.Column(2, 0, slice.TopY + 1, slice.TopY + 4, _stone);

        var tile = Scan(cells, slice);

        Assert.Equal(MapColors.Pack(MapConfig.RockColor), tile.Texels[MapTile.IndexOf(1, 0)]);
        Assert.NotEqual(MapColors.Pack(MapConfig.RockColor), tile.Texels[MapTile.IndexOf(2, 0)]);
        Assert.Equal(slice.TopY - 6, tile.Heights[MapTile.IndexOf(2, 0)]);
    }

    [Fact]
    public void AColumnAboveItsNorthernNeighborIsLitAndOneBelowIsShaded()
    {
        Assert.True(MapColumnScanner.ReliefShade(11, 10) > 1f);
        Assert.True(MapColumnScanner.ReliefShade(9, 10) < 1f);
        Assert.Equal(1f, MapColumnScanner.ReliefShade(10, 10));
        Assert.Equal(1f, MapColumnScanner.ReliefShade(10, MapTile.NoHeight));
        // The shading is capped, so a cliff reads as steep as a tall step, never black
        Assert.Equal(MapColumnScanner.ReliefShade(10, 0), MapColumnScanner.ReliefShade(100, 0));
        Assert.True(MapColumnScanner.ReliefShade(0, 100) > 0f);
    }

    [Fact]
    public void ATerraceStepReadsInTheTileBelowIt()
    {
        var cells = LoadedColumn();
        // Two rows: the northern row stands a cell higher than the row south of it
        cells.Column(5, 0, 0, 11, _stone);
        cells.Column(5, 1, 0, 10, _stone);
        cells.Column(6, 0, 0, 10, _stone);
        cells.Column(6, 1, 0, 10, _stone);

        var tile = Scan(cells, MapLayer.Surface);

        Assert.True(Brightness(tile.Texels[MapTile.IndexOf(5, 1)]) < Brightness(tile.Texels[MapTile.IndexOf(6, 1)]));
    }
}
