using System;
using System.IO;
using VoxelEngine.Content.Spells;
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
    public void InvalidPartNamesItsFile(string kindFolder, string json) => InTempDir(dir =>
    {
        var path = WritePart(dir, kindFolder, "bad", json);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => SpellPartLoader.ReadParts(dir)).Message);
    });

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
