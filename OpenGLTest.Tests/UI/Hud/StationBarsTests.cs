using System;
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
    public void AQuernShowsItsRotationOnlyWhileTheTopTurns()
    {
        var quern = new QuernManager.QuernState();
        Assert.Null(StationBars.Quern(quern));
        quern.Spinning = true;
        quern.Angle = MathF.PI;
        Assert.Equal(0.5f, Assert.Single(StationBars.Quern(quern)!).Fraction, 3);
    }

    [Fact]
    public void NothingIsShownForAMissingStation()
    {
        Assert.Null(StationBars.Kiln(null));
        Assert.Null(StationBars.Bloomery(null));
        Assert.Null(StationBars.Anvil(null));
        Assert.Null(StationBars.Mold(BlockType.Air, null));
        Assert.Null(StationBars.Quern(null));
    }

    [Fact]
    public void AnEmptyMoldShowsEachMetalItCastsAtZeroOfWhatItNeeds()
    {
        var moldId = ContentRegistry.RequireBlock("fired_mold_pickaxe").Id;
        var recipes = ContentRegistry.CastRecipesList.Where(recipe => recipe.MoldId == moldId).ToList();
        var bars = StationBars.Mold(moldId, null)!;

        Assert.Equal(recipes.Count, bars.Count);
        foreach (var recipe in recipes)
            Assert.Contains(bars, bar => bar.Label.EndsWith($"0/{recipe.AmountMl}ml", StringComparison.Ordinal));
        Assert.All(bars, bar => Assert.Equal(0f, bar.Fraction));
    }

    [Fact]
    public void APartlyPouredMoldShowsWhatIsContainedOfWhatItNeeds()
    {
        var moldId = ContentRegistry.RequireBlock("fired_mold_pickaxe").Id;
        var recipe = ContentRegistry.CastRecipesList.First(recipe => recipe.MoldId == moldId);
        var contained = recipe.AmountMl / 2;
        var cast = new CastingManager.CastState(recipe) { FilledMl = contained };

        var bar = Assert.Single(StationBars.Mold(moldId, cast)!);
        Assert.EndsWith($"{contained}/{recipe.AmountMl}ml", bar.Label, StringComparison.Ordinal);
        Assert.Equal(contained / (float)recipe.AmountMl, bar.Fraction, 3);
    }
}
