using System;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Map;
using VoxelEngine.Networking.Client;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.Simulation.Waystones;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.Map;

public sealed class MapPinsTests
{
    private static EntityType Kind(EntityAttitude attitude) => new()
    {
        Name = "test", DisplayName = "Test", MaxHealth = 1, ModelPath = "", Model = null!, Attitude = attitude,
    };

    [Fact]
    public void AMobsPinColorComesFromItsKindsAttitude()
    {
        Assert.Equal(MapPinKind.Hostile, MapPins.KindOf(Kind(EntityAttitude.Hostile)));
        Assert.Equal(MapPinKind.Neutral, MapPins.KindOf(Kind(EntityAttitude.Neutral)));
    }

    [Fact]
    public void AMarkedTowerIsAPinUntilItsMarkGoes()
    {
        var pins = new MapPins();
        var towers = new TowerMarks();
        var waystones = new DiscoveredWaystones();
        void Collect() => pins.Collect(MapLayer.Surface, Vector3.Zero, 0f, Array.Empty<RemotePlayer>(), Array.Empty<Mob>(),
            waystones, towers);

        Collect();
        Assert.DoesNotContain(pins.Pins, p => p.Kind == MapPinKind.Tower);

        towers.Add(640, -1280);
        Collect();
        var tower = Assert.Single(pins.Pins, p => p.Kind == MapPinKind.Tower);
        Assert.Equal(new Vector2(640.5f, -1279.5f), tower.Position);
        Assert.NotNull(tower.Label);

        towers.Remove(640, -1280);
        Collect();
        Assert.DoesNotContain(pins.Pins, p => p.Kind == MapPinKind.Tower);
        // The local player's own arrow is always there, drawn last
        Assert.Equal(MapPinKind.Self, pins.Pins[^1].Kind);
    }

    [Fact]
    public void ACaveSliceShowsOnlyTheMobsInItsHeightBand()
    {
        var slice = MapLayer.CaveAt(-40f);
        Assert.True(MapPins.ShowsMobAt(slice, -40f));
        Assert.False(MapPins.ShowsMobAt(slice, 70f));
        Assert.False(MapPins.ShowsMobAt(slice, slice.BottomY - 10f));
        // Over the surface every mob shows, however high or low
        Assert.True(MapPins.ShowsMobAt(MapLayer.Surface, 70f));
        Assert.True(MapPins.ShowsMobAt(MapLayer.Surface, -40f));
    }

    [Fact]
    public void AMobsArrowPointsWhereItsBodyFaces()
    {
        // A mob turns to face a point with Facing = atan2(-dx, -dz) (the model faces -Z at 0), as Slime does
        foreach (var (dx, dz) in new[] { (0f, -1f), (1f, 0f), (0f, 1f), (-1f, 0f), (1f, -1f), (-3f, 2f) })
        {
            var facing = MathF.Atan2(-dx, -dz);
            var expected = Compass.BearingTo(0f, 0f, dx, dz);
            Assert.True(MathF.Abs(Compass.WrapDelta(MapPins.HeadingOfFacing(facing) - expected)) < 0.01f,
                $"toward ({dx}, {dz})");
            var heading = MapPins.HeadingOfFacing(facing);
            Assert.InRange(heading, 0f, 360f);
        }
    }
}
