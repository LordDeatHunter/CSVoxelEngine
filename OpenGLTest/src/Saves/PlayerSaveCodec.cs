using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoxelEngine.Saves;

/// <summary>
/// Reads and writes a world's <see cref="FileName"/> (<see cref="PlayerSaveData"/> as indented
/// JSON). GL-free and content-free: names are resolved later, by whoever applies the data.
/// <para>
/// A missing, corrupt or truncated file parses to null with a warning, and the world then starts
/// as if the file were absent (a surface spawn with the spawn stock) instead of failing the load.
/// </para>
/// </summary>
public static class PlayerSaveCodec
{
    public const string FileName = "player.json";

    /// <summary>Shared by every JSON side-car of a save: hand-editable (comments and trailing
    /// commas tolerated, names case-insensitive) and without null noise.</summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] ToBytes(PlayerSaveData data) => JsonSerializer.SerializeToUtf8Bytes(data, Options);

    /// <summary>The parsed file, or null for no file, the JSON <c>null</c> literal or anything
    /// that is not valid JSON of this shape (logged as a warning).</summary>
    public static PlayerSaveData? TryParse(byte[]? bytes)
    {
        if (bytes == null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<PlayerSaveData>(bytes, Options);
        }
        catch (JsonException e)
        {
            Log.Warn($"Player save: ignoring {FileName}: {e.Message}");
            return null;
        }
    }
}
