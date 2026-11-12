using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Simulation.Waystones;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Waystones;

/// <summary>
/// Where a waystone traveler lands: the nearest free two-cell spot on solid ground beside the waystone, a step on its
/// own floor beating a climb onto a wall, never in a fluid or the waystone's own column, facing away from the waystone; on top of
/// the waystone when nothing nearby is free; and no answer at all while any cell it reads is unloaded.
/// </summary>
public sealed class WaystoneArrivalTests
{
    private static readonly (int X, int Y, int Z) Stone = (10, 5, -3);

    /// <summary>A hand-built world: solid at or below <c>floorY</c>, open above, with single-cell overrides.</summary>
    private sealed class Cells(int floorY) : IArrivalCells
    {
        public readonly Dictionary<(int, int, int), ArrivalCell> Overrides = new();
        public Func<int, int, int, bool>? Unloaded { get; init; }

        public ArrivalCell At(int x, int y, int z)
        {
            if (Unloaded?.Invoke(x, y, z) == true) return ArrivalCell.Unloaded;
            if (Overrides.TryGetValue((x, y, z), out var cell)) return cell;
            return y <= floorY ? ArrivalCell.Solid : ArrivalCell.Open;
        }
    }

    private static Cells FlatAroundTheStone()
    {
        var cells = new Cells(Stone.Y - 1);
        for (var dy = 0; dy < WaystoneArrival.StoneHeight; dy++)
            cells.Overrides[(Stone.X, Stone.Y + dy, Stone.Z)] = ArrivalCell.Solid;
        return cells;
    }

    [Fact]
    public void OnFlatGroundTheTravelerStandsBesideTheWaystoneOnItsFloorFacingAway()
    {
        var spot = WaystoneArrival.Find(FlatAroundTheStone(), Stone);

        Assert.Equal(WaystoneArrivalStatus.Found, spot.Status);
        Assert.Equal(Stone.Y, spot.Feet.Y);
        var offset = new Vector2(spot.Feet.X - (Stone.X + 0.5f), spot.Feet.Z - (Stone.Z + 0.5f));
        Assert.Equal(1f, offset.Length(), 3);
        var facing = Vector3Utils.FromYaw(spot.Yaw!.Value);
        Assert.True(Vector2.Dot(Vector2.Normalize(offset), new Vector2(facing.X, facing.Z)) > 0.99f,
            "the traveler does not face away from the waystone");
    }

    [Fact]
    public void ANearLevelSpotBeatsAClimbOntoTheWallAroundTheStone()
    {
        var cells = FlatAroundTheStone();
        // A wall three cells high rings the stone; standing on it would be a climb of three
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            for (var dy = 0; dy < 3; dy++)
                cells.Overrides[(Stone.X + dx, Stone.Y + dy, Stone.Z + dz)] = ArrivalCell.Solid;
        }

        var spot = WaystoneArrival.Find(cells, Stone);

        Assert.Equal(WaystoneArrivalStatus.Found, spot.Status);
        Assert.Equal(Stone.Y, spot.Feet.Y);
        Assert.Equal(2, Math.Max(Math.Abs((int)MathF.Floor(spot.Feet.X) - Stone.X),
            Math.Abs((int)MathF.Floor(spot.Feet.Z) - Stone.Z)));
    }

    [Fact]
    public void NoSpotIsInAFluidOrUnderALowCeiling()
    {
        var cells = new Cells(Stone.Y - 1);
        cells.Overrides[Stone] = ArrivalCell.Solid;
        for (var dx = -WaystoneArrival.SearchRadius; dx <= WaystoneArrival.SearchRadius; dx++)
        for (var dz = -WaystoneArrival.SearchRadius; dz <= WaystoneArrival.SearchRadius; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            // Water on the floor level, and a roof right over it
            cells.Overrides[(Stone.X + dx, Stone.Y, Stone.Z + dz)] = ArrivalCell.Blocked;
            cells.Overrides[(Stone.X + dx, Stone.Y + 1, Stone.Z + dz)] = ArrivalCell.Solid;
        }
        // The one dry cell is under a roof with no head room
        cells.Overrides[(Stone.X + 1, Stone.Y, Stone.Z)] = ArrivalCell.Open;

        var spot = WaystoneArrival.Find(cells, Stone);

        Assert.NotEqual((Stone.X + 1, Stone.Y), ((int)MathF.Floor(spot.Feet.X), (int)spot.Feet.Y));
        Assert.True(spot.Status != WaystoneArrivalStatus.Found || spot.Feet.Y > Stone.Y + 1,
            "the traveler landed in water or under a roof");
    }

    [Fact]
    public void WithNothingFreeTheTravelerStandsOnTopOfTheWaystone()
    {
        var solid = new Cells(int.MaxValue);

        var spot = WaystoneArrival.Find(solid, Stone);

        Assert.Equal(WaystoneArrivalStatus.OnTop, spot.Status);
        // Over the waystone's whole height, its upper half included
        Assert.Equal(new Vector3(Stone.X + 0.5f, Stone.Y + WaystoneArrival.StoneHeight, Stone.Z + 0.5f), spot.Feet);
        Assert.Null(spot.Yaw);
    }

    [Fact]
    public void AnUnloadedCellAnywhereTheSearchReadsHoldsTheAnswer()
    {
        var corner = (X: Stone.X + WaystoneArrival.SearchRadius, Y: Stone.Y - WaystoneArrival.SearchHeight - 1,
            Z: Stone.Z - WaystoneArrival.SearchRadius);
        var cells = new Cells(Stone.Y - 1) { Unloaded = (x, y, z) => (x, y, z) == corner };

        Assert.Equal(WaystoneArrivalStatus.Unloaded, WaystoneArrival.Find(cells, Stone).Status);
    }
}
