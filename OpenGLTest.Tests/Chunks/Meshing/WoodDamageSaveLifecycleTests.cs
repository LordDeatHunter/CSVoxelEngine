using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>Wood-damage sidecar preservation across an actual world save lifecycle.</summary>
[Collection("Worldgen seed")]
public sealed class WoodDamageSaveLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private World? _world;

    public WoodDamageSaveLifecycleTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }

        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string SidecarPath => Path.Combine(_directory, WoodDamageCodec.FileName);

    private void OpenWorld(byte[]? sidecar = null)
    {
        Directory.CreateDirectory(_directory);
        if (sidecar != null)
            File.WriteAllBytes(SidecarPath, sidecar);
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_directory));
    }

    public static IEnumerable<object[]> RejectedFiles()
    {
        yield return new object[] { "short-header", new byte[] { (byte)'C', (byte)'V' } };
        yield return new object[] { "bad-magic", "BAD!\u0001"u8.ToArray() };
        yield return new object[]
        {
            "unsupported-version",
            new byte[] { (byte)'C', (byte)'V', (byte)'W', (byte)'D', (byte)(WoodDamageCodec.Version + 1), 0x42 }
        };
        var valid = WoodDamageCodec.ToBytes([(CellKey.Pack(1, 2, 3), new WoodDamage.Entry(1.25f, 2))]);
        yield return new object[] { "truncated-body", valid[..(valid.Length / 2)] };
    }

    [Theory]
    [MemberData(nameof(RejectedFiles))]
    public void ARejectedSidecarIsArchivedBeforeSupportedDamageReplacesIt(string _, byte[] rejected)
    {
        Assert.Null(WoodDamageCodec.TryParse(rejected));
        OpenWorld(rejected);

        _world!.GetChunkManager().ChopDamage.Set(12, 70, -4, 2.75f, face: 2);
        Assert.True(_world.SaveAndDispose());
        _world = null;

        var archivedPath = SidecarPath + ".bad";
        var archiveExists = File.Exists(archivedPath);
        var archivedMatches = archiveExists && File.ReadAllBytes(archivedPath).SequenceEqual(rejected);
        var replacement = File.Exists(SidecarPath) ? WoodDamageCodec.TryParse(File.ReadAllBytes(SidecarPath)) : null;
        Assert.True(archiveExists && archivedMatches && replacement != null,
            $"archiveExists={archiveExists}, archivedMatches={archivedMatches}, replacementParses={replacement != null}");
        var entry = Assert.Single(replacement!);
        CellKey.Unpack(entry.Key, out var x, out var y, out var z);
        Assert.Equal((12, 70, -4), (x, y, z));
        Assert.Equal((2.75f, 2), (entry.Entry.Damage, entry.Entry.Face));
    }

    [Theory]
    [MemberData(nameof(RejectedFiles))]
    public void ARejectedSidecarIsUntouchedWhenDamageDoesNotChange(string _, byte[] rejected)
    {
        Assert.Null(WoodDamageCodec.TryParse(rejected));
        OpenWorld(rejected);

        Assert.True(_world!.SaveAndDispose());
        _world = null;

        Assert.False(File.Exists(SidecarPath + ".bad"));
        Assert.Equal(rejected, File.ReadAllBytes(SidecarPath));
    }

    [Fact]
    public void MissingSidecarStaysAbsentWhenDamageDoesNotChange()
    {
        OpenWorld();

        Assert.True(_world!.SaveAndDispose());
        _world = null;

        Assert.False(File.Exists(SidecarPath));
        Assert.False(File.Exists(SidecarPath + ".bad"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedEmptyAndStoredSidecarsStayUnarchived(bool hasStoredEntry)
    {
        var entries = hasStoredEntry
            ? new[] { (CellKey.Pack(3, 40, 8), new WoodDamage.Entry(1.5f, 1)) }
            : Array.Empty<(long Key, WoodDamage.Entry Entry)>();
        var bytes = WoodDamageCodec.ToBytes(entries);
        OpenWorld(bytes);

        var damage = _world!.GetChunkManager().ChopDamage;
        Assert.Equal(hasStoredEntry, damage.TryGet(3, 40, 8, out _));
        Assert.True(_world.SaveAndDispose());
        _world = null;

        Assert.False(File.Exists(SidecarPath + ".bad"));
        var parsed = WoodDamageCodec.TryParse(File.ReadAllBytes(SidecarPath));
        Assert.NotNull(parsed);
        Assert.Equal(entries.Length, parsed!.Count);
    }
}
