using VoxelEngine.Map;
using Xunit;

namespace VoxelEngine.Tests.Map;

public sealed class MapLayerTests
{
    [Fact]
    public void HellIsAlwaysACaveSliceEvenWithNothingOverhead()
    {
        Assert.Equal(MapLayerKind.Cave, MapLayer.For(WorldGenerationConfig.HellTopY - 20f, roofed: false).Kind);
        Assert.Equal(MapLayerKind.Cave, MapLayer.For(WorldGenerationConfig.HellTopY - 20f, roofed: true).Kind);
    }

    [Fact]
    public void ARoofOverTheHeadTurnsTheOverworldIntoACaveSlice()
    {
        Assert.Equal(MapLayerKind.Surface, MapLayer.For(70f, roofed: false).Kind);
        Assert.Equal(MapLayerKind.Cave, MapLayer.For(70f, roofed: true).Kind);
        Assert.Equal(MapLayerKind.Cave, MapLayer.For(-40f, roofed: true).Kind);
    }

    [Fact]
    public void AmongTheSkyIslandsTheMapShowsTheSkyBandRoofedOrNot()
    {
        var eye = WorldGenerationConfig.SkyIslandBottomY + 10f;
        Assert.Equal(MapLayer.Sky, MapLayer.For(eye, roofed: false));
        Assert.Equal(MapLayer.Sky, MapLayer.For(eye, roofed: true));
    }

    [Fact]
    public void TheSurfaceWindowStopsBelowTheCloudSlabAndTheSkyWindowAboveIt()
    {
        Assert.True(MapLayer.Surface.TopY < WorldGenerationConfig.CloudSlabBottomY);
        Assert.True(MapLayer.Sky.BottomY > WorldGenerationConfig.CloudSlabTopY);
        Assert.True(MapLayer.Sky.TopY >= WorldGenerationConfig.SkyIslandTopY);
    }

    [Fact]
    public void ACaveSliceStartsAboveTheHeadAndOnlyMovesInWholeSteps()
    {
        for (var eye = -60f; eye < 60f; eye += 0.37f)
        {
            var slice = MapLayer.CaveAt(eye);
            Assert.True(slice.TopY > eye, $"slice top {slice.TopY} under eye {eye}");
            Assert.True(slice.TopY - eye <= MapConfig.CaveSliceHeadroom + MapConfig.CaveSliceStep);
            Assert.Equal(0, MathUtils.Mod(slice.TopY, MapConfig.CaveSliceStep));
            Assert.True(slice.BottomY < slice.TopY);
        }
    }

    [Fact]
    public void ADifferentLayerKindTakesOverOnlyAfterTheSwitchDelay()
    {
        var selector = new MapLayerSelector();
        selector.Update(MapLayer.Surface, 0f);

        var cave = MapLayer.CaveAt(30f);
        var step = MapConfig.LayerSwitchSeconds / 4f;
        for (var i = 0; i < 3; i++)
            Assert.False(selector.Update(cave, step));
        Assert.Equal(MapLayer.Surface, selector.Current);

        // A frame back on the surface (a doorway) restarts the hold
        Assert.False(selector.Update(MapLayer.Surface, step));
        for (var i = 0; i < 3; i++)
            Assert.False(selector.Update(cave, step));

        Assert.True(selector.Update(cave, step * 1.5f));
        Assert.Equal(cave, selector.Current);
    }

    [Fact]
    public void ACaveSliceThatOnlyMovedFollowsAtOnce()
    {
        var selector = new MapLayerSelector();
        selector.Update(MapLayer.CaveAt(30f), 0f);

        var deeper = MapLayer.CaveAt(10f);
        Assert.True(selector.Update(deeper, 0f));
        Assert.Equal(deeper, selector.Current);
    }
}
