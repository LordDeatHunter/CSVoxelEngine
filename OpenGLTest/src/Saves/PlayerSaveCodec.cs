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
/// <para>Non-finite saved position, look, respawn or lighting values are ignored field by field.</para>
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
            var saved = JsonSerializer.Deserialize<PlayerSaveData>(bytes, Options);
            if (saved == null)
                return null;

            var ignoredNonFiniteFields = false;
            if (HasNonFiniteComponent(saved.Position))
            {
                saved.Position = null;
                ignoredNonFiniteFields = true;
            }
            if (HasNonFiniteComponent(saved.RespawnEye))
            {
                saved.RespawnEye = null;
                ignoredNonFiniteFields = true;
            }
            if (saved.Yaw is { } yaw && !float.IsFinite(yaw))
            {
                saved.Yaw = null;
                ignoredNonFiniteFields = true;
            }
            if (saved.Pitch is { } pitch && !float.IsFinite(pitch))
            {
                saved.Pitch = null;
                ignoredNonFiniteFields = true;
            }
            if (saved.LightingClock is { } clock && !double.IsFinite(clock))
            {
                saved.LightingClock = null;
                ignoredNonFiniteFields = true;
            }
            if (ignoredNonFiniteFields)
                Log.Warn($"Player save: ignoring non-finite pose, respawn or lighting fields in {FileName}");

            return saved;
        }
        catch (JsonException e)
        {
            Log.Warn($"Player save: ignoring {FileName}: {e.Message}");
            return null;
        }
    }

    private static bool HasNonFiniteComponent(float[]? values)
    {
        if (values == null)
            return false;
        foreach (var value in values)
            if (!float.IsFinite(value))
                return true;
        return false;
    }
}
