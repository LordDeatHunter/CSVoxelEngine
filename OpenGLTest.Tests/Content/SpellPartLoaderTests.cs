using System;
using System.IO;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The spell part loader's folder and field rules. Every case reads a temp folder through the pure
/// <see cref="SpellPartLoader.ReadParts"/>, never replacing the published registry other tests read.
/// </summary>
public class SpellPartLoaderTests
{
    private const string ValidPart = """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"] }""";

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "voxel-spell-parts-" + Guid.NewGuid().ToString("N"));

    private static string WritePart(string dir, string kindFolder, string name, string json)
    {
        var folder = Path.Combine(dir, kindFolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".json");
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
    public void AMissingSpellFolderIsNoParts() => Assert.Empty(SpellPartLoader.ReadParts(NewDir()));

    [Fact]
    public void AbsentKindFoldersAreNoParts() => InTempDir(dir =>
    {
        WritePart(dir, "element", "x", ValidPart);
        var parts = SpellPartLoader.ReadParts(dir);
        var part = Assert.Single(parts.Values);
        Assert.Equal(SpellPartKind.Element, part.Kind);
        Assert.Same(part, parts[SpellPart.KeyOf(SpellPartKind.Element, "x")]);
    });

    [Fact]
    public void PartsOfDifferentKindsMayShareAName() => InTempDir(dir =>
    {
        WritePart(dir, "element", "a", ValidPart);
        WritePart(dir, "modifier", "a", ValidPart);
        var parts = SpellPartLoader.ReadParts(dir);
        Assert.Equal(2, parts.Count);
        Assert.Equal(SpellPartKind.Element, parts[SpellPart.KeyOf(SpellPartKind.Element, "a")].Kind);
        Assert.Equal(SpellPartKind.Modifier, parts[SpellPart.KeyOf(SpellPartKind.Modifier, "a")].Kind);
    });

    [Fact]
    public void AnUnknownKindFolderNamesItself() => InTempDir(dir =>
    {
        var bogus = Path.Combine(dir, "bogus");
        Directory.CreateDirectory(bogus);
        Assert.Contains(bogus, Assert.Throws<InvalidOperationException>(() => SpellPartLoader.ReadParts(dir)).Message);
    });

    [Fact]
    public void ALooseFileNamesItself() => InTempDir(dir =>
    {
        var path = Path.Combine(dir, "x.json");
        File.WriteAllText(path, ValidPart);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => SpellPartLoader.ReadParts(dir)).Message);
    });

    [Fact]
    public void MalformedJsonNamesItsFile() => InTempDir(dir =>
    {
        var path = WritePart(dir, "element", "bad", "{ not json");
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => SpellPartLoader.ReadParts(dir)).Message);
    });

    [Theory]
    [InlineData("element", """{ "complexity": 1, "tags": ["basic"] }""")]
    [InlineData("element", """{ "costShare": 0, "complexity": 1, "tags": ["basic"] }""")]
    [InlineData("element", """{ "costShare": 1.5, "complexity": 1, "tags": ["basic"] }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": -1, "tags": ["basic"] }""")]
    [InlineData("element", """{ "costShare": 0.1, "tags": ["basic"] }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": [] }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": [""] }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic", "basic"] }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "damageFactor": 2 }""")]
    [InlineData("shape", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "rangeFactor": 2 }""")]
    [InlineData("modifier", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "damageFactor": 0 }""")]
    [InlineData("modifier", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "rangeFactor": -1 }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "effect": "unknown" }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "effect": " " }""")]
    [InlineData("modifier", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "effect": "known" }""")]
    [InlineData("shape", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "effect": "known" }""")]
    public void InvalidPartNamesItsFile(string kindFolder, string json) => InTempDir(dir =>
    {
        var path = WritePart(dir, kindFolder, "bad", json);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => SpellPartLoader.ReadParts(dir, KnownEffect)).Message);
    });

    /// <summary>The effect lookup the temp-folder cases are checked against: only <c>known</c> exists.</summary>
    private static bool KnownEffect(string name) => name == "known";

    [Fact]
    public void AnElementKeepsTheEffectItNames() => InTempDir(dir =>
    {
        WritePart(dir, "element", "x", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "effect": "known" }""");
        WritePart(dir, "element", "y", ValidPart);
        var parts = SpellPartLoader.ReadParts(dir, KnownEffect);
        Assert.Equal("known", parts[SpellPart.KeyOf(SpellPartKind.Element, "x")].Effect);
        Assert.Null(parts[SpellPart.KeyOf(SpellPartKind.Element, "y")].Effect);
    });

    /// <summary>The species lookup the temp-folder cases are checked against: only <c>tree</c> exists.</summary>
    private static bool KnownSpecies(string name) => name == "tree";

    private static SpellPart CompileOne(string kindFolder, string json)
    {
        SpellPart? part = null;
        InTempDir(dir =>
        {
            WritePart(dir, kindFolder, "x", json);
            part = Assert.Single(SpellPartLoader.ReadParts(dir, KnownEffect, KnownSpecies).Values);
        });
        return part!;
    }

    [Theory]
    [InlineData("shape", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "impact": ["ignite"] }""")]
    [InlineData("modifier", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "impact": [] }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "delivery": "self" }""")]
    public void AnImpactOutsideAnElementIsRefused(string kindFolder, string json) => InTempDir(dir =>
    {
        var path = WritePart(dir, kindFolder, "bad", json);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => SpellPartLoader.ReadParts(dir, KnownEffect, KnownSpecies)).Message);
    });

    [Theory]
    [InlineData("""{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "impact": ["explode"] }""")]
    [InlineData("""{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "impact": ["Ignite"] }""")]
    [InlineData("""{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "impact": ["freeze", "freeze"] }""")]
    public void AnUnknownOrRepeatedImpactIsRefused(string json) => InTempDir(dir =>
    {
        var path = WritePart(dir, "element", "bad", json);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => SpellPartLoader.ReadParts(dir, KnownEffect, KnownSpecies)).Message);
    });

    [Theory]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "plant": "tree" }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "impact": ["ignite"], "plant": "tree" }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "impact": ["grow"], "plant": "unknown" }""")]
    [InlineData("element", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "impact": ["grow"], "plant": " " }""")]
    [InlineData("shape", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "plant": "tree" }""")]
    public void APlantNeedsGrowAndAKnownSpecies(string kindFolder, string json) => InTempDir(dir =>
    {
        var path = WritePart(dir, kindFolder, "bad", json);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => SpellPartLoader.ReadParts(dir, KnownEffect, KnownSpecies)).Message);
    });

    [Theory]
    [InlineData("modifier", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "delivery": "projectile" }""")]
    [InlineData("shape", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "delivery": "beam" }""")]
    [InlineData("shape", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "delivery": "Self" }""")]
    public void ADeliveryOutsideAShapeOrUnknownIsRefused(string kindFolder, string json) => InTempDir(dir =>
    {
        var path = WritePart(dir, kindFolder, "bad", json);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => SpellPartLoader.ReadParts(dir, KnownEffect, KnownSpecies)).Message);
    });

    [Fact]
    public void AShapeWithoutDeliveryIsAProjectile()
    {
        Assert.Equal(SpellDelivery.Projectile, CompileOne("shape", ValidPart).Delivery);
        Assert.Equal(SpellDelivery.Self,
            CompileOne("shape", """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "delivery": "self" }""").Delivery);
    }

    [Fact]
    public void ImpactNamesCompileToTheirFlags()
    {
        var part = CompileOne("element",
            """{ "costShare": 0.1, "complexity": 1, "tags": ["basic"], "impact": ["ignite", "freeze", "grow"], "plant": "tree" }""");
        Assert.True(part.Impact.HasFlag(SpellImpact.Ignite));
        Assert.True(part.Impact.HasFlag(SpellImpact.Freeze));
        Assert.True(part.Impact.HasFlag(SpellImpact.Grow));
        Assert.False(part.Impact.HasFlag(SpellImpact.Prime));
        Assert.Equal("tree", part.Plant);
        Assert.Equal(SpellImpact.None, CompileOne("element", ValidPart).Impact);
    }

    [Fact]
    public void EveryShippedElementEffectResolves()
    {
        TestContent.EnsureLoaded();
        var withoutEffect = 0;
        foreach (var part in SpellPartRegistry.All)
        {
            if (part.Effect == null)
            {
                if (part.Kind == SpellPartKind.Element) withoutEffect++;
                continue;
            }
            Assert.Equal(SpellPartKind.Element, part.Kind);
            Assert.True(StatusEffectRegistry.TryGet(part.Effect, out _));
        }
        Assert.True(withoutEffect > 0);
    }

    [Fact]
    public void RequireNamesAMissingPart()
    {
        TestContent.EnsureLoaded();
        Assert.Null(SpellPartRegistry.TryGet(SpellPartKind.Trigger, "no_such_part"));
        var error = Assert.Throws<InvalidOperationException>(() => SpellPartRegistry.Require(SpellPartKind.Trigger, "no_such_part"));
        Assert.Contains("no_such_part", error.Message);
    }

    [Fact]
    public void ARegisteredPartIsFoundByItsKindAndName()
    {
        TestContent.EnsureLoaded();
        foreach (var part in SpellPartRegistry.All)
        {
            Assert.Same(part, SpellPartRegistry.TryGet(part.Kind, part.Name));
            Assert.Same(part, SpellPartRegistry.Require(part.Kind, part.Name));
        }
    }
}
