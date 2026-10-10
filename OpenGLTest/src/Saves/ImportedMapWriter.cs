using System;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Writes the folder an <see cref="ImportedMapSource"/> reads: <c>columns/&lt;cx&gt;_&lt;cz&gt;.col</c>
/// (<see cref="ColumnFileCodec"/>) and <c>chunks/&lt;cx&gt;_&lt;cy&gt;_&lt;cz&gt;.bin</c>
/// (<see cref="ChunkSaveCodec"/>, the save store's own block-name-paletted format, so block ids
/// never leak into a map). A column with a file is supplied by the map, together with its whole
/// chunk stack; a chunk of a supplied column without a file is all air, so a map writes every
/// non-empty chunk of every column it supplies (<see cref="ExportGenerated"/> does).
/// </summary>
/// <remarks>
/// Tool and test use only, on whichever thread calls it: nothing in the running game writes a map.
/// Each file goes to <c>&lt;name&gt;.tmp</c> first and is moved over the final name, so a reader
/// never sees half a file. Terrain worldgen builds beside a map ignores it, so a map should cover
/// whole 64x64 tree cells or be padded by <see cref="TreeGenerator.ReachChunkRadius"/> chunks on
/// every side to keep worldgen's trees off the seam.
/// </remarks>
public static class ImportedMapWriter
{
    /// <summary>The extension of a column file inside the columns folder.</summary>
    public const string ColumnFileExtension = ".col";

    private const string TempExtension = ".tmp";

    /// <summary>The file name one chunk column is stored under inside the columns folder.</summary>
    public static string ColumnFileName(int cx, int cz) =>
        string.Create(CultureInfo.InvariantCulture, $"{cx}_{cz}{ColumnFileExtension}");

    /// <summary>Parses a <see cref="ColumnFileName"/> back into its chunk column; any other name (a
    /// temp file, a stray file, the wrong number of parts) returns false.</summary>
    public static bool TryParseColumnFileName(string fileName, out int cx, out int cz)
    {
        cx = cz = 0;
        if (!fileName.EndsWith(ColumnFileExtension, StringComparison.Ordinal))
            return false;

        var parts = fileName[..^ColumnFileExtension.Length].Split('_');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out cx)
            && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out cz);
    }

    /// <summary>Writes chunk column (<paramref name="cx"/>, <paramref name="cz"/>)'s
    /// <paramref name="column"/> into the map, which makes the map supply that column.</summary>
    public static void WriteColumn(string mapDirectory, int cx, int cz, ColumnData column)
    {
        ArgumentNullException.ThrowIfNull(mapDirectory);
        ArgumentNullException.ThrowIfNull(column);

        using var encoded = new MemoryStream();
        ColumnFileCodec.Write(encoded, column);
        WriteFile(Path.Combine(mapDirectory, ImportedMapSource.ColumnsDirectoryName, ColumnFileName(cx, cz)),
            encoded.GetBuffer().AsSpan(0, (int)encoded.Length));
    }

    /// <summary>Writes the blocks and state of chunk (<paramref name="cx"/>, <paramref name="cy"/>,
    /// <paramref name="cz"/>) into the map. Its player-placed cells are written as given, but an
    /// <see cref="ImportedMapSource"/> ignores them.</summary>
    public static void WriteChunk(string mapDirectory, int cx, int cy, int cz, ChunkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(mapDirectory);
        ArgumentNullException.ThrowIfNull(snapshot);

        using var encoded = new MemoryStream();
        // The save store's own naming, so a map reads back through the same palette rules
        ChunkSaveCodec.Write(encoded, snapshot, static id => BlockType.ById[id]?.Name);
        WriteFile(Path.Combine(mapDirectory, ImportedMapSource.ChunksDirectoryName, WorldStore.ChunkFileName(cx, cy, cz)),
            encoded.GetBuffer().AsSpan(0, (int)encoded.Length));
    }

    /// <summary>
    /// Exports what <paramref name="source"/> holds over chunk columns
    /// [<paramref name="cxMin"/>, <paramref name="cxMax"/>] x [<paramref name="czMin"/>,
    /// <paramref name="czMax"/>]: every column's <see cref="IWorldSource.GetColumn"/>, and every chunk
    /// from <paramref name="cyMin"/> to <paramref name="cyMax"/> that is not all air, filled through
    /// <see cref="IWorldSource.Fill"/>. Bounds are inclusive. Over <see cref="GeneratedWorldSource"/>
    /// this seeds a map with worldgen's terrain under the current <see cref="WorldSeed"/>, which is
    /// how a map is started and how the tests prove an imported region fills exactly like the
    /// original. Builds one chunk per position on the calling thread, so a large region is slow.
    /// Returns the number of chunk files written.
    /// </summary>
    public static int ExportGenerated(string mapDirectory, IWorldSource source,
        int cxMin, int cxMax, int cyMin, int cyMax, int czMin, int czMax)
    {
        ArgumentNullException.ThrowIfNull(mapDirectory);
        ArgumentNullException.ThrowIfNull(source);
        if (cxMin > cxMax || cyMin > cyMax || czMin > czMax)
            throw new ArgumentException("Every minimum must be at most its maximum.");

        var written = 0;
        for (var cx = cxMin; cx <= cxMax; cx++)
        for (var cz = czMin; cz <= czMax; cz++)
        {
            WriteColumn(mapDirectory, cx, cz, source.GetColumn(cx, cz));

            for (var cy = cyMin; cy <= cyMax; cy++)
            {
                var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
                source.Fill(chunk);
                var snapshot = chunk.CaptureForNetwork();
                // An absent chunk of a supplied column reads back as air, so air needs no file
                if (snapshot.Blocks.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                    continue;

                WriteChunk(mapDirectory, cx, cy, cz, snapshot);
                written++;
            }
        }

        return written;
    }

    // Writes <path>.tmp and moves it over <path>, creating the folder on demand.
    private static void WriteFile(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + TempExtension;
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            stream.Write(bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
