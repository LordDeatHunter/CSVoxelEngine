using System;
using System.Numerics;
using VoxelEngine.Particles;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.Effects.Sky;

/// <summary>
/// How the cave biome at the eye colors the fog, the sky palette and the ambient light, GL-free
/// (plans/CAVE_BIOMES.md CB-3). Each <see cref="CaveBiome"/> names a target <see cref="CaveAtmosphere"/>
/// (<see cref="Target"/>; plain caves have none), which <c>FrameLighting</c> eases toward
/// (<see cref="Ease"/>) beside the realm layer amounts of <see cref="LayerClimate"/> and applies once a
/// frame to the sRGB palette before it is decoded (<see cref="Apply"/>), so no shader changes: the dry
/// world fog reads the palette through every <c>SkySample</c> copy. It also pulls the fog in
/// (<see cref="FogNear"/>, <see cref="FogFar"/>), tints the ambient (<see cref="AmbientFactor"/>) and
/// names the drifting motes (<see cref="Motes"/>).
/// </summary>
public static class CaveClimate
{
    // At or below this strength the hue is taken outright, so a fade-in from neutral never drifts
    // through the hue of a biome left long ago
    private const float SnapStrength = 1e-3f;

    private static readonly CaveAtmosphere[] Targets = BuildTargets();

    private static CaveAtmosphere[] BuildTargets()
    {
        var targets = new CaveAtmosphere[Enum.GetValues<CaveBiome>().Length];
        targets[(int)CaveBiome.Plain] = CaveAtmosphere.None;
        targets[(int)CaveBiome.FrozenEcho] = Full(SkyConfig.CaveFogStrength, SkyConfig.CaveFogFrozenEcho, SkyConfig.CaveAmbientFrozenEcho);
        targets[(int)CaveBiome.DesertEcho] = Full(SkyConfig.CaveFogStrength, SkyConfig.CaveFogDesertEcho, SkyConfig.CaveAmbientDesertEcho);
        targets[(int)CaveBiome.Crystal] = Full(SkyConfig.CaveFogStrength, SkyConfig.CaveFogCrystal, SkyConfig.CaveAmbientCrystal);
        targets[(int)CaveBiome.Fungal] = Full(SkyConfig.CaveFogStrength, SkyConfig.CaveFogFungal, SkyConfig.CaveAmbientFungal);
        targets[(int)CaveBiome.MagmaDeep] =
            Full(SkyConfig.CaveFogStrengthMagmaDeep, SkyConfig.CaveFogMagmaDeep, SkyConfig.CaveAmbientMagmaDeep);
        return targets;
    }

    private static CaveAtmosphere Full(float strength, Vector3 fog, Vector3 ambient) =>
        new(strength, fog, SkyConfig.CaveFogNearBlocks, SkyConfig.CaveFogFarBlocks, ambient);

    /// <summary>The atmosphere an eye in <paramref name="biome"/> should see; <see cref="CaveAtmosphere.None"/>
    /// for a plain cave (and so for the surface, the echo cover, hell and the sky, all plain).</summary>
    public static CaveAtmosphere Target(CaveBiome biome)
    {
        var i = (int)biome;
        return (uint)i < (uint)Targets.Length ? Targets[i] : CaveAtmosphere.None;
    }

    /// <summary>
    /// Eases <paramref name="current"/> toward <paramref name="target"/>'s atmosphere over
    /// <paramref name="dt"/> seconds at <see cref="SkyConfig.CaveFogEaseRate"/>, never past it. Fading out
    /// toward a plain cave keeps the current hue and only lowers the strength; fading in from (almost)
    /// nothing takes the target's full values outright; otherwise every full value eases toward the
    /// target's at the same rate as the strength.
    /// </summary>
    public static CaveAtmosphere Ease(CaveAtmosphere current, CaveBiome target, float dt)
    {
        var t = Target(target);
        var strength = MathUtils.Damp(current.Strength, t.Strength, SkyConfig.CaveFogEaseRate, dt);
        if (t.Strength <= 0f) return current with { Strength = strength };
        if (current.Strength <= SnapStrength) return t with { Strength = strength };

        // The fraction of the way one Damp step covers, shared by every full value
        var k = MathUtils.Damp(0f, 1f, SkyConfig.CaveFogEaseRate, dt);
        return new CaveAtmosphere(
            strength,
            Vector3.Lerp(current.FogColorSrgb, t.FogColorSrgb, k),
            MathUtils.Lerp(current.FogNearBlocks, t.FogNearBlocks, k),
            MathUtils.Lerp(current.FogFarBlocks, t.FogFarBlocks, k),
            Vector3.Lerp(current.AmbientTint, t.AmbientTint, k));
    }

    /// <summary>The sRGB <paramref name="palette"/> with zenith and horizon pulled toward the cave's fog
    /// color and the sunset glow faded out, both by the strength; strength 0 returns it unchanged.</summary>
    public static SkyPalette Apply(SkyPalette palette, in CaveAtmosphere a)
    {
        var s = MathUtils.Clamp01(a.Strength);
        if (s <= 0f) return palette;
        return new SkyPalette(
            Vector3.Lerp(palette.Zenith, a.FogColorSrgb, s),
            Vector3.Lerp(palette.Horizon, a.FogColorSrgb, s),
            palette.Glow * (1f - s));
    }

    /// <summary>The fog's near distance: <paramref name="dryNear"/> moved toward the cave's by the strength.</summary>
    public static float FogNear(float dryNear, in CaveAtmosphere a)
    {
        var s = MathUtils.Clamp01(a.Strength);
        return s <= 0f ? dryNear : MathUtils.Lerp(dryNear, a.FogNearBlocks, s);
    }

    /// <summary>The fog's far distance: <paramref name="dryFar"/> moved toward the cave's by the strength.</summary>
    public static float FogFar(float dryFar, in CaveAtmosphere a)
    {
        var s = MathUtils.Clamp01(a.Strength);
        return s <= 0f ? dryFar : MathUtils.Lerp(dryFar, a.FogFarBlocks, s);
    }

    /// <summary>The per-channel factor the ambient light is multiplied by: one, moved toward the cave's
    /// tint by the strength.</summary>
    public static Vector3 AmbientFactor(in CaveAtmosphere a)
    {
        var s = MathUtils.Clamp01(a.Strength);
        return s <= 0f ? Vector3.One : Vector3.Lerp(Vector3.One, a.AmbientTint, s);
    }

    /// <summary>Which particle drifts around an eye in <paramref name="biome"/> and how many a second;
    /// none in a plain cave.</summary>
    public static (FireParticleKind? Kind, float PerSecond) Motes(CaveBiome biome) => biome switch
    {
        CaveBiome.FrozenEcho => (FireParticleKind.Steam, ParticleConfig.CaveMoteFrozenEchoPerSecond),
        CaveBiome.DesertEcho => (FireParticleKind.Smoke, ParticleConfig.CaveMoteDesertEchoPerSecond),
        CaveBiome.Crystal => (FireParticleKind.Spark, ParticleConfig.CaveMoteCrystalPerSecond),
        CaveBiome.Fungal => (FireParticleKind.Spark, ParticleConfig.CaveMoteFungalPerSecond),
        CaveBiome.MagmaDeep => (FireParticleKind.Ember, ParticleConfig.CaveMoteMagmaDeepPerSecond),
        _ => (null, 0f),
    };
}
