using System;
using System.Numerics;
using VoxelEngine.Effects.Sky;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>
/// The realm layers' sky and fog (<see cref="LayerClimate"/>): the hell tint is zero everywhere
/// above hell (the spawn, the caves, the barrier band) and grows continuously and monotonically
/// below it to full; the altitude shift is zero below the cloud slab and full at the sky islands;
/// both ease in and out over time without overshooting; and applied to a palette they leave it
/// untouched at zero, turn it to the hell color at full, and deepen the day blue without
/// brightening the night.
/// </summary>
public sealed class LayerClimateTests
{
    private const float HellTop = WorldGenerationConfig.HellTopY + 1f;

    [Fact]
    public void TheHellTintIsZeroAboveHellAndGrowsContinuouslyBelowIt()
    {
        Assert.Equal(0f, LayerClimate.HellTarget(64f));
        Assert.Equal(0f, LayerClimate.HellTarget(WorldGenerationConfig.SeaLevel));
        for (var y = WorldGenerationConfig.BarrierTopY; y >= WorldGenerationConfig.BarrierBottomY; y--)
            Assert.Equal(0f, LayerClimate.HellTarget(y));
        Assert.Equal(0f, LayerClimate.HellTarget(HellTop));

        var previous = LayerClimate.HellTarget(HellTop);
        for (var y = HellTop; y > HellTop - SkyConfig.HellFogRampBlocks * 3f; y -= 0.25f)
        {
            var value = LayerClimate.HellTarget(y);
            Assert.True(value >= previous, $"the tint fell at y {y}");
            Assert.True(value - previous < 0.05f, $"the tint jumped at y {y}");
            previous = value;
        }
        Assert.Equal(1f, LayerClimate.HellTarget(HellTop - SkyConfig.HellFogRampBlocks));
        Assert.Equal(1f, LayerClimate.HellTarget(-100000f));
    }

    [Fact]
    public void TheAltitudeShiftRisesFromTheCloudSlabToTheIslands()
    {
        Assert.Equal(0f, LayerClimate.AltitudeTarget(64f));
        Assert.Equal(0f, LayerClimate.AltitudeTarget(WorldGenerationConfig.CloudSlabTopY));
        Assert.Equal(1f, LayerClimate.AltitudeTarget(WorldGenerationConfig.SkyIslandBottomY));
        Assert.Equal(1f, LayerClimate.AltitudeTarget(WorldGenerationConfig.SkyIslandTopY));
        var mid = (WorldGenerationConfig.CloudSlabTopY + WorldGenerationConfig.SkyIslandBottomY) * 0.5f;
        Assert.InRange(LayerClimate.AltitudeTarget(mid), 0.01f, 0.99f);
        // The two never overlap: no height is both in hell and over the clouds
        Assert.Equal(0f, LayerClimate.AltitudeTarget(HellTop - 1000f));
    }

    [Fact]
    public void TheAmountsEaseInAndOutWithoutOvershooting()
    {
        const float dt = 1f / 60f;
        var amount = 0f;
        var previous = amount;
        for (var i = 0; i < 600; i++)
        {
            amount = LayerClimate.Ease(amount, 1f, dt);
            Assert.InRange(amount, previous, 1f);
            previous = amount;
        }
        Assert.True(amount > 0.99f, $"never eased in: {amount}");
        // One frame moves it only part of the way, so a teleport fades
        Assert.True(LayerClimate.Ease(0f, 1f, dt) < 0.5f);

        for (var i = 0; i < 600; i++)
        {
            amount = LayerClimate.Ease(amount, 0f, dt);
            Assert.InRange(amount, 0f, previous);
            previous = amount;
        }
        Assert.True(amount < 0.01f, $"never eased out: {amount}");
    }

    [Fact]
    public void ThePaletteIsUntouchedAtZeroRedInHellAndDeeperBlueAloft()
    {
        var day = SkyPalette.Compute(1f, 0f);
        var night = SkyPalette.Compute(0f, 0f);
        var dusk = SkyPalette.Compute(0.5f, 1f, 1f);

        Assert.Equal(day, LayerClimate.Apply(day, 0f, 0f));
        Assert.Equal(dusk, LayerClimate.Apply(dusk, 0f, 0f));

        var hell = LayerClimate.Apply(dusk, 0f, 1f);
        Assert.True(Vector3.Distance(SkyConfig.HellFogColor, hell.Zenith) < 1e-5f);
        Assert.True(Vector3.Distance(SkyConfig.HellFogColor, hell.Horizon) < 1e-5f);
        Assert.Equal(Vector3.Zero, hell.Glow);
        Assert.True(hell.Zenith.X > hell.Zenith.Y && hell.Zenith.X > hell.Zenith.Z, "the hell fog is not red");

        // Aloft the day blue deepens (more blue against red) and the night stays as dark
        var aloft = LayerClimate.Apply(day, 1f, 0f);
        Assert.True(aloft.Zenith.Z / aloft.Zenith.X > day.Zenith.Z / day.Zenith.X);
        var aloftNight = LayerClimate.Apply(night, 1f, 0f);
        Assert.True(Luma(aloftNight.Zenith) <= Luma(night.Zenith) * 1.1f);
        // Modest: no channel moves by more than a fifth
        for (var c = 0; c < 3; c++)
            Assert.True(MathF.Abs(aloft.Zenith[c] - day.Zenith[c]) <= 0.2f * day.Zenith[c] + 1e-4f);
    }

    [Fact]
    public void HellLayersCountDownFromTheBarrierWithNoBottom()
    {
        Assert.Equal(-1, LayerClimate.HellLayerIndex(64f));
        Assert.Equal(-1, LayerClimate.HellLayerIndex(WorldGenerationConfig.BarrierBottomY));
        Assert.Equal(0, LayerClimate.HellLayerIndex(HellTop - 0.5f));
        var period = WorldGenerationConfig.HellLayerPeriodBlocks;
        for (var layer = 0; layer < 50; layer++)
        {
            Assert.Equal(layer, LayerClimate.HellLayerIndex(HellTop - layer * period - 0.5f));
            Assert.Equal(layer, LayerClimate.HellLayerIndex(HellTop - (layer + 1) * period + 0.5f));
        }
    }

    private static float Luma(Vector3 c) => 0.3f * c.X + 0.59f * c.Y + 0.11f * c.Z;
}
