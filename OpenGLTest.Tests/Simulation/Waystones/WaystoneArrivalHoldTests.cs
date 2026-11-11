using VoxelEngine.Simulation.Waystones;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Waystones;

/// <summary>
/// The server's hold on a waystone trip: the traveler is kept over the destination and gets no spot while any cell the
/// arrival search reads is unloaded, lands on the very spot the search picks once they are in, and lands on top of the
/// waystone once the hold has run past its timeout.
/// </summary>
public sealed class WaystoneArrivalHoldTests
{
    private static readonly (int X, int Y, int Z) Stone = (-40, 12, 300);

    /// <summary>Flat ground under the waystone, everything unloaded until <see cref="Loaded"/> is set.</summary>
    private sealed class Cells : IArrivalCells
    {
        public bool Loaded;

        public ArrivalCell At(int x, int y, int z)
        {
            if (!Loaded) return ArrivalCell.Unloaded;
            if ((x, y, z) == Stone) return ArrivalCell.Solid;
            return y < Stone.Y ? ArrivalCell.Solid : ArrivalCell.Open;
        }
    }

    [Fact]
    public void TheTravelerWaitsOverTheWaystoneUntilItsCellsLoadThenLandsWhereTheSearchSays()
    {
        var cells = new Cells();
        var hold = new WaystoneArrivalHold(Stone);

        Assert.Null(hold.Step(0.5, cells));
        // Held over the top of the waystone, where its streaming focus pulls the destination in
        var onTop = WaystoneArrival.OnTopFeet(Stone);
        Assert.Equal(onTop.X, hold.HoldEye.X);
        Assert.Equal(onTop.Z, hold.HoldEye.Z);
        Assert.True(hold.HoldEye.Y > onTop.Y);

        cells.Loaded = true;
        var spot = hold.Step(0.5, cells);
        Assert.NotNull(spot);
        Assert.Equal(WaystoneArrival.Find(cells, Stone), spot.Value);
        Assert.Equal(WaystoneArrivalStatus.Found, spot.Value.Status);
    }

    [Fact]
    public void AHoldThatOutlastsTheTimeoutLandsOnTopOfTheWaystone()
    {
        var cells = new Cells();
        var hold = new WaystoneArrivalHold(Stone);
        var step = StreamingConfig.WaystoneArrivalTimeoutSeconds / 4;

        WaystoneArrivalSpot? spot = null;
        for (var i = 0; i < 8 && spot == null; i++)
            spot = hold.Step(step, cells);

        Assert.NotNull(spot);
        Assert.True(hold.Held >= StreamingConfig.WaystoneArrivalTimeoutSeconds);
        Assert.Equal(WaystoneArrivalStatus.OnTop, spot.Value.Status);
        Assert.Equal(WaystoneArrival.OnTopFeet(Stone), spot.Value.Feet);
    }
}
