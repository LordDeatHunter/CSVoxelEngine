using System.Text.Json;

namespace VoxelEngine.Saves;

/// <summary>
/// Reads and writes a world's <see cref="FileName"/> (<see cref="StationSaveData"/> as indented
/// JSON, with the options every save side-car shares, <see cref="PlayerSaveCodec.Options"/>).
/// GL-free and content-free: names are resolved later, by the station managers' restore.
/// <para>
/// A missing, corrupt or truncated file parses to null with a warning, and the world then loads
/// with no saved stations instead of failing.
/// </para>
/// </summary>
public static class StationSaveCodec
{
    public const string FileName = "stations.json";

    public static byte[] ToBytes(StationSaveData data) =>
        JsonSerializer.SerializeToUtf8Bytes(data, PlayerSaveCodec.Options);

    /// <summary>The parsed file, or null for no file, the JSON <c>null</c> literal or anything
    /// that is not valid JSON of this shape (logged as a warning).</summary>
    public static StationSaveData? TryParse(byte[]? bytes)
    {
        if (bytes == null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<StationSaveData>(bytes, PlayerSaveCodec.Options);
        }
        catch (JsonException e)
        {
            Log.Warn($"Station save: ignoring {FileName}: {e.Message}");
            return null;
        }
    }

    /// <summary>A saved cell as a tuple, or false for anything but three ints.</summary>
    internal static bool TryCell(int[]? cell, out (int X, int Y, int Z) key)
    {
        key = default;
        if (cell is not { Length: 3 })
            return false;
        key = (cell[0], cell[1], cell[2]);
        return true;
    }
}
