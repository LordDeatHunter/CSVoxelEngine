using System.Linq;
using VoxelEngine.Simulation;
using VoxelEngine.UI.Hud;
using Xunit;

namespace VoxelEngine.Tests.UI.Hud;

public sealed class StationBarsTests
{
    public StationBarsTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AnUnlitPileShowsNoBars()
    {
        Assert.Null(StationBars.Campfire(new CampfireManager(), 0, 0, 0, 4, 4));
    }

    [Fact]
    public void ALitPilesFuelBarFallsAsItBurnsAndOneBarFollowsEachCookingSlot()
    {
        var fires = new CampfireManager();
        Assert.True(fires.Ignite(0, 0, 0));
        var full = StationBars.Campfire(fires, 0, 0, 0, 4, 4)!;
        var fewer = StationBars.Campfire(fires, 0, 0, 0, 2, 4)!;
        Assert.True(fewer[0].Fraction < full[0].Fraction);
        Assert.All(full, bar => Assert.InRange(bar.Fraction, 0f, 1f));

        Assert.True(fires.TryPlaceFood(0, 0, 0, ContentRegistry.RequireItem("mudball").Id));
        var cooking = StationBars.Campfire(fires, 0, 0, 0, 4, 4)!;
        Assert.Equal(StationBarKind.Fuel, cooking[0].Kind);
        Assert.Equal(StationBarKind.Progress, Assert.Single(cooking.Skip(1)).Kind);
    }

    [Fact]
    public void NothingIsShownForAMissingStation()
    {
        Assert.Null(StationBars.Kiln(null));
        Assert.Null(StationBars.Bloomery(null));
        Assert.Null(StationBars.Anvil(null));
        Assert.Null(StationBars.Mold(null));
    }
}
