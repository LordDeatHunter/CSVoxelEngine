using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using VoxelEngine.Content;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Chunks;

/// <summary>
/// An <see cref="IWorldSource"/> that reads a pre-built map instead of generating it: a folder
/// (<c>&lt;world&gt;/import/</c> on a dedicated server, <see cref="DirectoryName"/>) holding
/// <c>columns/&lt;cx&gt;_&lt;cz&gt;.col</c> (<see cref="ColumnFileCodec"/>) and
/// <c>chunks/&lt;cx&gt;_&lt;cy&gt;_&lt;cz&gt;.bin</c> (<see cref="ChunkSaveCodec"/>), as
/// <see cref="ImportedMapWriter"/> writes them. A column with a file is supplied: its
/// <see cref="ColumnData"/> comes from that file (complete by construction: heights, bed, water
/// levels, biomes, dirt depth and the corner climate), and every chunk of its stack comes from its
/// chunk file, or is all air when there is none. Every other column, and its chunks, comes from
/// the wrapped fallback (worldgen by default), so terrain generated beside the map ignores it:
/// seams at the map's edge are accepted (see <see cref="ImportedMapWriter"/> on padding).
/// </summary>
/// <remarks>
/// Thread-safe and deterministic: the column table and the chunk-file index are built in the
/// constructor and never modified afterwards (concurrent reads of an unmodified
/// <see cref="Dictionary{TKey, TValue}"/> or <see cref="HashSet{T}"/> are safe), and every chunk
/// read opens its own stream, so the generation workers, the pool mesh builds, the LodGen threads
/// and the main thread can all call it at once. It never writes into <see cref="ColumnCache"/> or
/// any other <see cref="GenerationalCache{TKey, TValue}"/>. A supplied chunk is restored
/// (<see cref="Chunk.IsRestored"/>) and not <see cref="Chunk.ModifiedSinceSave"/> until edited, so
/// an untouched imported chunk is never written to the save, and a saved one wins over the map
/// (<see cref="ChunkManager.CreateChunk"/>). Player-placed cells in a chunk file are ignored.
/// Every column file is loaded at construction (about 3.5 KB of decoded arrays per column plus
/// the <see cref="ColumnData"/> itself), a known limit for very large maps.
/// </remarks>
public sealed class ImportedMapSource : IWorldSource
{
    /// <summary>The map folder's name inside a world folder.</summary>
    public const string DirectoryName = "import";

    /// <summary>The column files' folder inside the map folder.</summary>
    public const string ColumnsDirectoryName = "columns";

    /// <summary>The chunk files' folder inside the map folder.</summary>
    public const string ChunksDirectoryName = "chunks";

    // The blocks of a supplied chunk without a file; RestoreFrom only copies from it
    private static readonly ChunkSnapshot Empty = new(new byte[Chunk.BlockCount], state: null, playerPlaced: null);

    private readonly IWorldSource _fallback;
    private readonly Dictionary<(int X, int Z), ColumnData> _columns = new();
    private readonly HashSet<long> _chunkFiles = new();
    private readonly string _chunksDirectory;

    /// <summary>
    /// Loads the map at <paramref name="mapDirectory"/>. Throws
    /// <see cref="DirectoryNotFoundException"/> when the folder or its columns folder is missing,
    /// and <see cref="InvalidDataException"/> or <see cref="IOException"/> naming the file when a
    /// column file cannot be read: a map is refused whole rather than hosted with holes. A missing
    /// chunks folder means every supplied chunk is air.
    /// </summary>
    /// <param name="fallback">The source of every column the map does not supply; worldgen
    /// (<see cref="GeneratedWorldSource.Instance"/>) when null.</param>
    public ImportedMapSource(string mapDirectory, IWorldSource? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(mapDirectory);

        MapDirectory = Path.GetFullPath(mapDirectory);
        _fallback = fallback ?? GeneratedWorldSource.Instance;
        if (!Directory.Exists(MapDirectory))
            throw new DirectoryNotFoundException($"The imported map folder '{MapDirectory}' does not exist.");

        var columnsDirectory = Path.Combine(MapDirectory, ColumnsDirectoryName);
        if (!Directory.Exists(columnsDirectory))
            throw new DirectoryNotFoundException(
                $"The imported map folder '{MapDirectory}' has no '{ColumnsDirectoryName}' folder.");

        foreach (var path in Directory.EnumerateFiles(columnsDirectory, "*" + ImportedMapWriter.ColumnFileExtension))
        {
            var fileName = Path.GetFileName(path);
            // The parser also rejects what the Windows wildcard over-matches (".colx"); only the
            // canonical name counts, so no column can be named twice
            if (!ImportedMapWriter.TryParseColumnFileName(fileName, out var cx, out var cz)
                || fileName != ImportedMapWriter.ColumnFileName(cx, cz))
                continue;

            _columns[(cx, cz)] = ReadColumn(path);
        }

        _chunksDirectory = Path.Combine(MapDirectory, ChunksDirectoryName);
        if (Directory.Exists(_chunksDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_chunksDirectory, "*.bin"))
            {
                var fileName = Path.GetFileName(path);
                if (WorldStore.TryParseChunkFileName(fileName, out var cx, out var cy, out var cz)
                    && fileName == WorldStore.ChunkFileName(cx, cy, cz))
                    _chunkFiles.Add(ChunkKey.Pack(cx, cy, cz));
            }
        }

        Log.WriteLine($"Imported map '{MapDirectory}': {_columns.Count} columns, {_chunkFiles.Count} chunk files.");
    }

    /// <summary>The map folder, as a full path.</summary>
    public string MapDirectory { get; }

    /// <summary>How many chunk columns the map supplies.</summary>
    public int ColumnCount => _columns.Count;

    /// <inheritdoc/>
    public bool Supplies(int chunkX, int chunkZ) => _columns.ContainsKey((chunkX, chunkZ));

    /// <inheritdoc/>
    public ColumnData GetColumn(int chunkX, int chunkZ) =>
        _columns.TryGetValue((chunkX, chunkZ), out var column) ? column : _fallback.GetColumn(chunkX, chunkZ);

    /// <inheritdoc/>
    public void Fill(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var cx = MathUtils.FloorToInt(chunk.Position.X);
        var cz = MathUtils.FloorToInt(chunk.Position.Z);
        if (!_columns.TryGetValue((cx, cz), out var column))
        {
            _fallback.Fill(chunk);
            return;
        }

        var cy = MathUtils.FloorToInt(chunk.Position.Y);
        var snapshot = _chunkFiles.Contains(ChunkKey.Pack(cx, cy, cz)) ? ReadChunk(cx, cy, cz) : Empty;
        chunk.RestoreFrom(snapshot, column);
    }

    /// <inheritdoc/>
    public bool IsReady(int chunkX, int chunkZ) => true;

    /// <inheritdoc/>
    public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain)
    {
        var column = GetColumn(chunkX, chunkZ);
        terrain = new ColumnTerrain(column.Heights, column.WaterLevel, column.CornerWarmth, column.CornerWetness);
        return true;
    }

    private static ColumnData ReadColumn(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ColumnFileCodec.Read(stream);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"Imported map column file '{path}' is unreadable: {e.Message}", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not read imported map column file '{path}': {e.Message}", e);
        }
    }

    // A chunk file, read on a generation worker. A read that keeps failing, or a damaged file,
    // degrades to air with a warning rather than throwing: the position must still be filled, or
    // it would stay stuck in the chunk manager's queued set.
    private ChunkSnapshot ReadChunk(int cx, int cy, int cz)
    {
        var path = Path.Combine(_chunksDirectory, WorldStore.ChunkFileName(cx, cy, cz));
        // A transient lock (a scanner, a backup) is retried like the save store's reads
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var snapshot = ChunkSaveCodec.Read(stream,
                    static name => ContentRegistry.TryResolveBlock(name, out var id) ? id : null);
                return snapshot.PlayerPlaced == null
                    ? snapshot
                    : new ChunkSnapshot(snapshot.Blocks, snapshot.State, playerPlaced: null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      && attempt < WorldGenerationConfig.SaveMoveRetries)
            {
                Thread.Sleep(WorldGenerationConfig.SaveMoveRetryDelayMs);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Warn($"Could not read imported map chunk '{path}', loading it as air: {e.Message}");
                return Empty;
            }
        }
    }
}
