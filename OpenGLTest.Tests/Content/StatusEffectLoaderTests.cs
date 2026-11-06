using System;
using System.IO;
using System.Linq;
using VoxelEngine.Content.StatusEffects;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The status effect loader's folder and field rules. Every case reads a temp folder through the pure
/// <see cref="StatusEffectLoader.ReadEffects"/>, never replacing the published registry other tests read.
/// </summary>
public class StatusEffectLoaderTests
{
    private const string ValidEffect = """{ "durationSeconds": 2, "tickSeconds": 1, "damageShare": 0.1 }""";

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "voxel-effects-" + Guid.NewGuid().ToString("N"));

    private static string WriteEffect(string dir, string name, string json)
    {
        var path = Path.Combine(dir, name + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static void InTempDir(Action<string> body)
    {
        var dir = NewDir();
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AMissingFolderIsNoEffects() => Assert.Empty(StatusEffectLoader.ReadEffects(NewDir()));

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "tickSeconds": 1, "damageShare": 0.1 }""")]
    [InlineData("""{ "durationSeconds": 0, "tickSeconds": 1, "damageShare": 0.1 }""")]
    [InlineData("""{ "durationSeconds": 2, "damageShare": 0.1 }""")]
    [InlineData("""{ "durationSeconds": 2, "tickSeconds": 0, "damageShare": 0.1 }""")]
    [InlineData("""{ "durationSeconds": 2, "tickSeconds": 1, "damageShare": 0 }""")]
    [InlineData("""{ "durationSeconds": 2, "tickSeconds": 1, "damageShare": 1.5 }""")]
    [InlineData("""{ "durationSeconds": 2, "moveFactor": 0 }""")]
    [InlineData("""{ "durationSeconds": 2, "attackFactor": 1.5 }""")]
    [InlineData("""{ "durationSeconds": 2, "tickSeconds": 1, "moveFactor": 0.5 }""")]
    [InlineData("""{ "durationSeconds": 2 }""")]
    [InlineData("""{ "durationSeconds": 2, "moveFactor": 1, "attackFactor": 1 }""")]
    [InlineData("""{ "durationSeconds": 2, "blocks": { "ignite": true } }""")]
    [InlineData("""{ "durationSeconds": 2, "moveFactor": 0.5, "tint": [255, 0] }""")]
    [InlineData("""{ "durationSeconds": 2, "moveFactor": 0.5, "tint": [255, 0, 256] }""")]
    [InlineData("""{ "durationSeconds": 2, "moveFactor": 0.5, "particle": "fireworks" }""")]
    [InlineData("""{ "durationSeconds": 2, "moveFactor": 0.5, "particle": "1" }""")]
    [InlineData("""{ "durationSeconds": 2, "moveFactor": 0.5, "icon": " " }""")]
    [InlineData("""{ "durationSeconds": 2, "moveFactor": 0.5, "icon": "assets/textures/ui/x.jpg" }""")]
    [InlineData("""{ "durationSeconds": 2, "moveFactor": 0.5, "deathMessage": " " }""")]
    public void InvalidEffectNamesItsFile(string json) => InTempDir(dir =>
    {
        var path = WriteEffect(dir, "bad", json);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => StatusEffectLoader.ReadEffects(dir)).Message);
    });

    [Fact]
    public void MoreEffectsThanTheWireMaskHoldsAreRefused() => InTempDir(dir =>
    {
        for (var i = 0; i <= StatusEffect.MaxKinds; i++)
            WriteEffect(dir, $"e{i:D2}", ValidEffect);
        Assert.Contains(dir, Assert.Throws<InvalidOperationException>(() => StatusEffectLoader.ReadEffects(dir)).Message);
    });

    [Fact]
    public void AFullFolderStillLoads() => InTempDir(dir =>
    {
        for (var i = 0; i < StatusEffect.MaxKinds; i++)
            WriteEffect(dir, $"e{i:D2}", ValidEffect);
        Assert.Equal(StatusEffect.MaxKinds, StatusEffectLoader.ReadEffects(dir).Count);
    });

    [Fact]
    public void IdsFollowFileNameOrderAndBitsAreDistinct()
    {
        InTempDir(dir =>
        {
            WriteEffect(dir, "zeta", ValidEffect);
            WriteEffect(dir, "alpha", ValidEffect);
            WriteEffect(dir, "mid", ValidEffect);

            var effects = StatusEffectLoader.ReadEffects(dir);
            var sortedNames = effects.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(sortedNames, effects.OrderBy(e => e.Id).Select(e => e.Name).ToList());
            Assert.Equal(effects.Count, effects.Select(e => e.Bit).Distinct().Count());
        });

        TestContent.EnsureLoaded();
        byte seen = 0;
        foreach (var effect in StatusEffectRegistry.All)
        {
            Assert.Equal(effect.Bit, StatusEffectRegistry.KnownMask & effect.Bit);
            Assert.Equal(0, seen & effect.Bit);
            seen |= effect.Bit;
            Assert.Same(effect, StatusEffectRegistry.ById(effect.Id));
            Assert.Same(effect, StatusEffectRegistry.Require(effect.Name));
        }
        Assert.Equal(StatusEffectRegistry.KnownMask, seen);
    }

    [Fact]
    public void RequireNamesAMissingEffect()
    {
        TestContent.EnsureLoaded();
        Assert.False(StatusEffectRegistry.Contains("no_such_effect"));
        Assert.Null(StatusEffectRegistry.ById(StatusEffect.MaxKinds));
        Assert.Contains("no_such_effect",
            Assert.Throws<InvalidOperationException>(() => StatusEffectRegistry.Require("no_such_effect")).Message);
    }
}
