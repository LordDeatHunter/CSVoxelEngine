using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The biome colour grade (<see cref="BiomeGrade"/>): neutral in the temperate lands, warm only where
/// it is warm and dry, cool where it is cold, always faint, and eased back to neutral when the eye's
/// climate is unknown.
/// </summary>
public sealed class BiomeGradeTests
{
    [Fact]
    public void TheTemperateLandsAreLeftExactlyAlone()
    {
        // The climate's warmth is exactly 0.5 across the temperate band, whatever the wetness
        for (var wet = 0f; wet <= 1f; wet += 0.05f)
            Assert.Equal(Vector3.One, BiomeGrade.Tint(0.5f, wet));
    }

    [Fact]
    public void TheDesertIsGradedWarmAndTheSnowCool()
    {
        var desert = BiomeGrade.Tint(1f, 0f);
        Assert.True(desert.X > 1f && desert.Z < 1f, "the desert grade is not warm");
        var snow = BiomeGrade.Tint(0f, 0.6f);
        Assert.True(snow.Z > 1f && snow.X < 1f, "the snow grade is not cool");
    }

    [Fact]
    public void WarmthAloneDoesNotTintItTakesWarmAndDry()
    {
        Assert.Equal(Vector3.One, BiomeGrade.Tint(1f, 1f));
        var previous = BiomeGrade.Tint(1f, 1f).X;
        for (var wet = 1f; wet >= 0f; wet -= 0.05f)
        {
            var red = BiomeGrade.Tint(1f, wet).X;
            Assert.True(red >= previous - 1e-6f, "a drier desert graded less warm");
            previous = red;
        }
    }

    [Fact]
    public void TheGradeIsAlwaysFaint()
    {
        var cap = WorldGenerationConfig.BiomeGradeStrength + 1e-6f;
        for (var warmth = 0f; warmth <= 1f; warmth += 0.02f)
        for (var wet = 0f; wet <= 1f; wet += 0.02f)
        {
            var g = BiomeGrade.Tint(warmth, wet);
            Assert.InRange(g.X, 1f - cap, 1f + cap);
            Assert.InRange(g.Y, 1f - cap, 1f + cap);
            Assert.InRange(g.Z, 1f - cap, 1f + cap);
        }
    }

    [Fact]
    public void AnUnknownColumnEasesTheGradeBackToNeutral()
    {
        Assert.Equal(Vector3.One, BiomeGrade.Target(false, 1f, 0f));
        var grade = BiomeGrade.Tint(1f, 0f);
        var previousDistance = Vector3.Distance(grade, Vector3.One);
        for (var i = 0; i < 600; i++)
        {
            grade = BiomeGrade.Ease(grade, BiomeGrade.Target(false, 1f, 0f), 1f / 60f);
            var distance = Vector3.Distance(grade, Vector3.One);
            Assert.True(distance <= previousDistance, "the grade moved away from neutral");
            previousDistance = distance;
        }

        Assert.True(previousDistance < 0.01f * WorldGenerationConfig.BiomeGradeStrength + 1e-4f);
    }
}
