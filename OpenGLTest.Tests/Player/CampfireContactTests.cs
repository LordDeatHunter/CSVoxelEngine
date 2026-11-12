using System.Numerics;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>
/// A body standing in a lit campfire catches burning: the pile in the cell its feet are in, under its footprint, and
/// not a pile beside it, a block below it, or a body that is immune.
/// </summary>
public sealed class CampfireContactTests
{
    public CampfireContactTests() => TestContent.EnsureLoaded();

    // A body whose feet rest at feetY with its center over (x, z)
    private static Vector3 Eye(float x, float feetY, float z) => new(x, feetY + Camera.EyeHeight, z);

    [Fact]
    public void AFireUnderTheFeetBurnsTheBody()
    {
        var campfires = new CampfireManager();
        campfires.Ignite(5, 10, 5);

        Assert.True(PlayerBodyProbe.OnLitCampfire(Eye(5.5f, 10f, 5.5f), campfires));

        var health = new PlayerHealth();
        health.UpdateCampfire(onFire: true, immune: false);
        Assert.True(health.Effects.IsActive(StatusEffectRegistry.Require("burning")));
    }

    [Fact]
    public void AFireBesideOrBelowTheBodyDoesNotBurnIt()
    {
        var campfires = new CampfireManager();
        campfires.Ignite(5, 10, 5);

        Assert.False(PlayerBodyProbe.OnLitCampfire(Eye(8.5f, 10f, 5.5f), campfires));
        Assert.False(PlayerBodyProbe.OnLitCampfire(Eye(5.5f, 11f, 5.5f), campfires));
    }

    [Fact]
    public void AnUnlitPileOrAnImmuneBodyIsNotBurned()
    {
        var campfires = new CampfireManager();
        Assert.False(PlayerBodyProbe.OnLitCampfire(Eye(5.5f, 10f, 5.5f), campfires));

        campfires.Ignite(5, 10, 5);
        var health = new PlayerHealth();
        health.UpdateCampfire(onFire: true, immune: true);
        Assert.False(health.Effects.IsActive(StatusEffectRegistry.Require("burning")));
    }
}
