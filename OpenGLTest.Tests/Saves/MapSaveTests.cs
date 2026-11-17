using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VoxelEngine.Map;
using VoxelEngine.Saves;
using VoxelEngine.Tests.Map;
using Xunit;

namespace VoxelEngine.Tests.Saves;

/// <summary>
/// The explored map's save (<see cref="MapSaveCodec"/>, <see cref="MapTracker.ExportExplored"/> and
/// <see cref="MapTracker.Import"/>): the surface and sky tiles round-trip exactly and a cave slice is never saved, a
/// damaged file reads as an unexplored map, only an explored-map change asks for a write, and a world that closes
/// hands its map to the next world opened over the same folder.
/// </summary>
[Collection("Map scanner")]
public sealed class MapSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private World? _world;

    public MapSaveTests() => TestContent.EnsureLoaded();

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

        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // A tile whose every texel and height is distinct, so a swapped or shifted array shows
    private static MapTile Painted(int chunkX, int chunkZ, uint seed)
    {
        var tile = new MapTile(chunkX, chunkZ);
        for (var i = 0; i < MapTile.TexelCount; i++)
        {
            tile.Texels[i] = 0xFF000000u | (seed * 7919u + (uint)i);
            tile.Heights[i] = (short)(i - 100 + (int)seed);
        }
        return tile;
    }

    [Fact]
    public void SurfaceAndSkyTilesRoundTripExactly()
    {
        var tiles = new[] { (MapLayerKind.Surface, Painted(-3, 7, 1)), (MapLayerKind.Sky, Painted(12, -40, 2)) };

        var back = MapSaveCodec.TryParse(MapSaveCodec.ToBytes(tiles));

        Assert.NotNull(back);
        Assert.Equal(tiles.Length, back!.Count);
        for (var i = 0; i < tiles.Length; i++)
        {
            Assert.Equal(tiles[i].Item1, back[i].Kind);
            Assert.Equal((tiles[i].Item2.ChunkX, tiles[i].Item2.ChunkZ), (back[i].Tile.ChunkX, back[i].Tile.ChunkZ));
            Assert.Equal(tiles[i].Item2.Texels, back[i].Tile.Texels);
            Assert.Equal(tiles[i].Item2.Heights, back[i].Tile.Heights);
        }
    }

    [Fact]
    public void ADamagedOrForeignFileReadsAsAnUnexploredMap()
    {
        var bytes = MapSaveCodec.ToBytes([(MapLayerKind.Surface, Painted(0, 0, 3))]);

        Assert.Null(MapSaveCodec.TryParse(bytes[..(bytes.Length / 2)]));
        Assert.Null(MapSaveCodec.TryParse("CVWD\u0001garbage"u8.ToArray()));
        Assert.Null(MapSaveCodec.TryParse(null));
    }

    [Fact]
    public void OnlyTheExploredMapIsSavedAndOnlyItsChangesAskForAWrite()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var cells = new FakeMapCells();
        for (var cy = -1; cy <= 12; cy++) cells.Load(0, cy, 0);
        for (var x = 0; x < 16; x++)
        for (var z = 0; z < 16; z++)
            cells.Column(x, z, 0, 10, stone);
        var tracker = new MapTracker(cells);
        var eye = new Vector3(8.5f, 12.6f, 8.5f);

        tracker.Update(eye, 0.016f);
        var surfaceVersion = tracker.ExploredVersion;
        Assert.True(surfaceVersion > 0);

        // A roof over the head: the cave slice's scans are no part of the explored map
        cells.Set(8, 20, 8, stone);
        tracker.Update(eye, MapConfig.LayerSwitchSeconds * 2f);
        tracker.Update(eye, 0.016f);
        Assert.Equal(MapLayerKind.Cave, tracker.Layer.Kind);
        Assert.Equal(surfaceVersion, tracker.ExploredVersion);

        var saved = tracker.ExportExplored();
        Assert.Equal(MapLayerKind.Surface, Assert.Single(saved).Kind);
        // A copy: later scans reuse the tracker's tile objects, and the save may encode it off the main thread
        Assert.DoesNotContain(saved, s => tracker.ExportExplored().Any(t => ReferenceEquals(t.Tile, s.Tile)));
    }

    [Fact]
    public void AClosingWorldHandsItsMapToTheNextOneOpenedOverTheSameFolder()
    {
        var tile = Painted(5, -9, 4);

        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir));
        _world.Renderer.EnsureMap().Import([(MapLayerKind.Surface, tile)]);
        _world.SaveAndDispose();
        _world = null;

        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir));
        Assert.True(_world.Renderer.EnsureMap().TryGetTile(5, -9, out var back));
        Assert.Equal(tile.Texels, back.Texels);
        Assert.Equal(tile.Heights, back.Heights);
    }

    public static IEnumerable<object[]> RejectedMapFiles()
    {
        var tile = Painted(5, -9, 4);
        var currentFormat = MapSaveCodec.ToBytes([(MapLayerKind.Surface, tile)]);
        yield return new object[] { "truncated-body", currentFormat[..(currentFormat.Length / 2)] };
        yield return new object[] { "short-header", "CV"u8.ToArray() };
        yield return new object[] { "foreign-magic", "CVWD\u0001garbage"u8.ToArray() };
        yield return new object[]
        {
            "unsupported-version",
            new byte[] { (byte)'C', (byte)'V', (byte)'M', (byte)'P', (byte)(MapSaveCodec.Version + 1), 0x42 }
        };
    }

    [Theory]
    [MemberData(nameof(RejectedMapFiles))]
    public void RejectedMapIsPreservedWhenAnImportedExploredMapReplacesIt(string _, byte[] rejected)
    {
        Directory.CreateDirectory(_dir);
        var mapPath = Path.Combine(_dir, MapSaveCodec.FileName);
        var badPath = mapPath + ".bad";
        File.WriteAllBytes(mapPath, rejected);

        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir));
        var map = _world.Renderer.EnsureMap();
        Assert.Empty(map.ExportExplored());
        var oldVersion = map.ExploredVersion;
        var tile = Painted(5, -9, 4);
        map.Import([(MapLayerKind.Surface, tile)]);
        Assert.True(map.ExploredVersion > oldVersion);

        Assert.True(_world.SaveAndDispose());
        _world = null;

        var archiveExists = File.Exists(badPath);
        var archived = archiveExists ? File.ReadAllBytes(badPath) : [];
        var replacement = File.Exists(mapPath) ? MapSaveCodec.TryParse(File.ReadAllBytes(mapPath)) : null;
        Assert.True(archiveExists && archived.SequenceEqual(rejected),
            $"archiveExists={archiveExists}, archivedBytesMatch={archived.SequenceEqual(rejected)}, replacementParses={replacement != null}");
        var restored = Assert.Single(replacement!);
        Assert.Equal((MapLayerKind.Surface, tile.ChunkX, tile.ChunkZ), (restored.Kind, restored.Tile.ChunkX, restored.Tile.ChunkZ));
        Assert.Equal(tile.Texels, restored.Tile.Texels);
        Assert.Equal(tile.Heights, restored.Tile.Heights);
    }

    [Theory]
    [MemberData(nameof(RejectedMapFiles))]
    public void RejectedMapIsUntouchedUntilAnExploredChangeIsSaved(string _, byte[] rejected)
    {
        Directory.CreateDirectory(_dir);
        var mapPath = Path.Combine(_dir, MapSaveCodec.FileName);
        var badPath = mapPath + ".bad";
        File.WriteAllBytes(mapPath, rejected);

        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir));
        Assert.Empty(_world.Renderer.EnsureMap().ExportExplored());
        Assert.True(_world.SaveAndDispose());
        _world = null;

        Assert.False(File.Exists(badPath));
        Assert.Equal(rejected, File.ReadAllBytes(mapPath));
    }

    [Fact]
    public void AnUnopenedMapFileIsUntouchedWhenTheWorldCloses()
    {
        Directory.CreateDirectory(_dir);
        var mapPath = Path.Combine(_dir, MapSaveCodec.FileName);
        var bytes = "CVMP\u0001"u8.ToArray();
        File.WriteAllBytes(mapPath, bytes);

        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir));
        Assert.True(_world.SaveAndDispose());
        _world = null;

        Assert.Equal(bytes, File.ReadAllBytes(mapPath));
        Assert.False(File.Exists(mapPath + ".bad"));
    }

    [Fact]
    public void AMissingMapRemainsAbsentWithoutExploration()
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir));
        Assert.Empty(_world.Renderer.EnsureMap().ExportExplored());
        Assert.True(_world.SaveAndDispose());
        _world = null;

        Assert.False(File.Exists(Path.Combine(_dir, MapSaveCodec.FileName)));
        Assert.False(File.Exists(Path.Combine(_dir, MapSaveCodec.FileName + ".bad")));
    }

    public static IEnumerable<object[]> ValidMapFiles()
    {
        yield return new object[] { "empty", MapSaveCodec.ToBytes([]), Array.Empty<(MapLayerKind, MapTile)>() };
        var tile = Painted(5, -9, 4);
        yield return new object[] { "tile", MapSaveCodec.ToBytes([(MapLayerKind.Surface, tile)]), new[] { (MapLayerKind.Surface, tile) } };
    }

    [Theory]
    [MemberData(nameof(ValidMapFiles))]
    public void ValidMapFilesRemainUnarchivedAndKeepTheirTiles(string _, byte[] valid, (MapLayerKind Kind, MapTile Tile)[] expected)
    {
        Directory.CreateDirectory(_dir);
        var mapPath = Path.Combine(_dir, MapSaveCodec.FileName);
        File.WriteAllBytes(mapPath, valid);

        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, new ChunkSaveStore(_dir));
        Assert.Equal(expected.Length, _world.Renderer.EnsureMap().ExportExplored().Count);
        Assert.True(_world.SaveAndDispose());
        _world = null;

        Assert.False(File.Exists(mapPath + ".bad"));
        var actual = MapSaveCodec.TryParse(File.ReadAllBytes(mapPath));
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual!.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Kind, actual[i].Kind);
            Assert.Equal(expected[i].Tile.ChunkX, actual[i].Tile.ChunkX);
            Assert.Equal(expected[i].Tile.ChunkZ, actual[i].Tile.ChunkZ);
            Assert.Equal(expected[i].Tile.Texels, actual[i].Tile.Texels);
            Assert.Equal(expected[i].Tile.Heights, actual[i].Tile.Heights);
        }
    }
}
