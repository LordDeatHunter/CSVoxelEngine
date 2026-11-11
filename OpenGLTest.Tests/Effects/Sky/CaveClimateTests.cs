using System;
using System.Linq;
using VoxelEngine.Effects.Sky;
using VoxelEngine.WorldGen.Caves;
using Xunit;

namespace VoxelEngine.Tests.Effects.Sky;

/// <summary>
/// The cave biome atmosphere (<see cref="CaveClimate"/>): a plain cave is exactly neutral (palette, fog,
/// ambient, no motes); every other biome has one that shifts the palette; the eased strength rises toward
/// a biome's without overshooting and fades back out toward a plain cave while the hue holds; and a fade-in
/// from nothing takes the target's hue outright.
/// </summary>
public sealed class CaveClimateTests
{
    private const float Dt = 1f / 60f;

    private static readonly CaveBiome[] Tinted = Enum.GetValues<CaveBiome>().Where(b => b != CaveBiome.Plain).ToArray();

    [Fact]
    public void APlainCaveIsExactlyNeutral()
    {
        var plain = CaveClimate.Target(CaveBiome.Plain);
        var dusk = SkyPalette.Compute(0.5f, 1f, 1f);
        Assert.Equal(dusk, CaveClimate.Apply(dusk, plain));
        Assert.Equal(LightingConfig.FogNearBlocks, CaveClimate.FogNear(LightingConfig.FogNearBlocks, plain));
        Assert.Equal(LightingConfig.FogFarBlocks, CaveClimate.FogFar(LightingConfig.FogFarBlocks, plain));
        Assert.Equal(System.Numerics.Vector3.One, CaveClimate.AmbientFactor(plain));
        Assert.Null(CaveClimate.Motes(CaveBiome.Plain).Kind);
    }

    [Fact]
    public void NoAtmosphereLeavesEveryPaletteUnchanged()
    {
        foreach (var palette in new[] { SkyPalette.Compute(1f, 0f), SkyPalette.Compute(0f, 0f), SkyPalette.Compute(0.5f, 1f, 1f) })
            Assert.Equal(palette, CaveClimate.Apply(palette, CaveAtmosphere.None));
    }

    [Fact]
    public void EveryOtherBiomeHasAnAtmosphereThatShiftsThePalette()
    {
        var day = SkyPalette.Compute(1f, 0f);
        foreach (var biome in Tinted)
        {
            var target = CaveClimate.Target(biome);
            Assert.True(target.Strength > 0f, $"{biome} has no atmosphere");
            var shifted = CaveClimate.Apply(day, target);
            Assert.NotEqual(day.Zenith, shifted.Zenith);
            Assert.NotEqual(day.Horizon, shifted.Horizon);
            Assert.NotNull(CaveClimate.Motes(biome).Kind);
        }
    }

    [Fact]
    public void TheStrengthEasesInWithoutOvershooting()
    {
        foreach (var biome in Tinted)
        {
            var target = CaveClimate.Target(biome).Strength;
            var a = CaveAtmosphere.None;
            var previous = a.Strength;
            for (var i = 0; i < 300; i++)
            {
                a = CaveClimate.Ease(a, biome, Dt);
                Assert.InRange(a.Strength, previous, target);
                previous = a.Strength;
            }
            Assert.True(a.Strength > 0.9f * target, $"{biome} never eased in: {a.Strength}");
        }
    }

    [Fact]
    public void AFadeInFromNothingTakesTheTargetHueAtOnce()
    {
        foreach (var biome in Tinted)
        {
            var eased = CaveClimate.Ease(CaveAtmosphere.None, biome, Dt);
            Assert.Equal(CaveClimate.Target(biome).FogColorSrgb, eased.FogColorSrgb);
            // One frame only starts the fade
            Assert.True(eased.Strength < 0.5f * CaveClimate.Target(biome).Strength);
        }
    }

    [Fact]
    public void FadingOutTowardAPlainCaveLowersOnlyTheStrength()
    {
        foreach (var biome in Tinted)
        {
            var a = CaveClimate.Target(biome);
            var start = a.Strength;
            var previous = start;
            for (var i = 0; i < 300; i++)
            {
                a = CaveClimate.Ease(a, CaveBiome.Plain, Dt);
                Assert.InRange(a.Strength, 0f, previous);
                Assert.Equal(CaveClimate.Target(biome).FogColorSrgb, a.FogColorSrgb);
                previous = a.Strength;
            }
            Assert.True(a.Strength < start);
        }
    }
}
