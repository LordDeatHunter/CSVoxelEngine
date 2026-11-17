using System;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Core;

/// <summary>Real store-backed world lifecycle coverage for the LOD edit sidecar.</summary>
[Collection("Worldgen seed")]
public sealed class LodSaveLifecycleTests : IDisposable
{
    private const float High = 1e6f;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private World? _world;

    public LodSaveLifecycleTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
            // A teardown timeout has already left the seed locked and is reported by the test.
        }

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public void RejectedLodEditSaveIsArchivedBeforeWorldReplacement(bool serverMode, int rejectedCase)
    {
        WorldSeed.Apply(WorldSeed.Default);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, LodEditState.FileName);
        var valid = new LodEditState([], [], []).ToBytes();
        var original = rejectedCase switch
        {
            0 => new byte[] { (byte)'C', (byte)'V', (byte)'X', (byte)'L' }, // short header
            1 => new byte[] { (byte)'B', (byte)'A', (byte)'D', (byte)'!', LodEditState.Version },
            2 => SetLodVersion(valid, LodEditState.Version - 1),
            3 => SetLodVersion(valid, LodEditState.Version + 1),
            _ => valid[..5], // valid v3 header with a truncated body
        };
        File.WriteAllBytes(path, original);

        CreateWorld(serverMode, new ChunkSaveStore(_directory));
        Assert.True(_world!.SaveAndDispose());

        var archivePath = path + ".bad";
        var archiveExists = File.Exists(archivePath);
        var archived = archiveExists ? File.ReadAllBytes(archivePath) : null;
        var reopened = new ChunkSaveStore(_directory);
        var replacement = LodEditState.TryParse(reopened.ReadFile(LodEditState.FileName));
        var replacementParses = replacement != null;
        Assert.True(reopened.Shutdown(TimeSpan.FromSeconds(10)));

        var archiveMatches = archived != null && archived.SequenceEqual(original);
        Assert.True(archiveMatches && replacementParses,
            $"serverMode={serverMode}; rejectedCase={rejectedCase}; .bad exists={archiveExists}; archived bytes match={archiveMatches}; replacement parses={replacementParses}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AcceptedLodEditSaveIsNotArchived(int acceptedCase)
    {
        WorldSeed.Apply(WorldSeed.Default);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, LodEditState.FileName);
        byte[]? original = acceptedCase == 0 ? null : AcceptedLodPayload(acceptedCase);
        if (original != null) File.WriteAllBytes(path, original);

        CreateWorld(true, new ChunkSaveStore(_directory));
        Assert.True(_world!.SaveAndDispose());

        Assert.False(File.Exists(path + ".bad"), $"acceptedCase={acceptedCase} unexpectedly created .bad");
        var parsed = LodEditState.TryParse(File.ReadAllBytes(path));
        Assert.NotNull(parsed);
        if (acceptedCase == 2)
            Assert.Contains((12, -7), parsed!.Disturbed);
        if (acceptedCase == 3)
            Assert.Equal((42, BlockType.Air), ReadOverride(parsed!, 0));
    }

    private void CreateWorld(bool serverMode, ChunkSaveStore store) =>
        _world = new World(null!, null!, null!, null!, null!, null!, null, store,
            serverMode: serverMode, spawn: new Vector3(0f, High, 0f));

    private static byte[] SetLodVersion(byte[] bytes, byte version)
    {
        var result = bytes.ToArray();
        result[4] = version;
        return result;
    }

    private static byte[] AcceptedLodPayload(int acceptedCase)
    {
        if (acceptedCase == 1) return new LodEditState([], [], []).ToBytes();
        if (acceptedCase == 2) return new LodEditState([], [], [(12, -7)]).ToBytes();

        var values = Enumerable.Repeat(LodSurface.None, Chunk.Size * Chunk.Size).ToArray();
        values[0] = LodSurface.Pack(42, 123);
        var state = new LodEditState([], [(0, 0, values)], []);
        using var stream = new MemoryStream();
        state.Write(stream, _ => "block/not_registered_for_lod_test");
        return stream.ToArray();
    }

    private static (int Top, ushort Block) ReadOverride(LodEditState state, int index)
    {
        var value = state.Overrides.Single().Values[index];
        LodSurface.Unpack(value, out var top, out var block);
        return (top, block);
    }
}
