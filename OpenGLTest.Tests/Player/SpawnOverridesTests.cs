using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Player;

public sealed class SpawnOverridesTests
{
    public static IEnumerable<object[]> NonFiniteSpawnComponents()
    {
        var invalid = new[] { "NaN", "Infinity", "-Infinity", "1e999", "-1e999" };
        for (var component = 0; component < 3; component++)
        foreach (var value in invalid)
        {
            var parts = new[] { "1", "2", "3" };
            parts[component] = value;
            yield return [string.Join(",", parts)];
        }
    }

    public static IEnumerable<object[]> NonFiniteLookComponents()
    {
        var invalid = new[] { "NaN", "Infinity", "-Infinity", "1e999", "-1e999" };
        for (var component = 0; component < 2; component++)
        foreach (var value in invalid)
        {
            var parts = new[] { "12", "20" };
            parts[component] = value;
            yield return [string.Join(",", parts)];
        }
    }

    [Theory]
    [MemberData(nameof(NonFiniteSpawnComponents))]
    public void SpawnOverrideRejectsNonFiniteComponents(string value)
    {
        Assert.False(SpawnOverrides.TryParseSpawnOverride(value, out var position));
        Assert.Equal(Vector3.Zero, position);
    }

    [Theory]
    [MemberData(nameof(NonFiniteLookComponents))]
    public void LookOverrideRejectsNonFiniteComponents(string value)
    {
        Assert.False(SpawnOverrides.TryParseLookOverride(value, out var look));
        Assert.Equal(new Vector2(-90f, 0f), look);
    }

    [Fact]
    public void FiniteSpawnAndLookOverridesKeepInvariantParsingAndPitchClamp()
    {
        Assert.True(SpawnOverrides.TryParseSpawnOverride(" 1.25 , -2e1, 3 ", out var position));
        Assert.Equal(new Vector3(1.25f, -20f, 3f), position);

        Assert.True(SpawnOverrides.TryParseLookOverride(" 1.25e1 , 120 ", out var look));
        Assert.Equal(new Vector2(12.5f, 89f), look);

        Assert.True(SpawnOverrides.TryParseLookOverride("0,-120", out look));
        Assert.Equal(new Vector2(0f, -89f), look);
    }
}
