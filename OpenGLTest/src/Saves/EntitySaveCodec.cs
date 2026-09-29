using System.Text.Json;

namespace silkgltriangle;

/// <summary>
/// Reads and writes a world's <see cref="FileName"/> (<see cref="EntitySaveData"/> as indented
/// JSON, with the options every save side-car shares, <see cref="PlayerSaveCodec.Options"/>).
/// GL-free and content-free: names are resolved later, by whoever restores the entities.
/// <para>
/// A missing, corrupt or truncated file parses to null with a warning, and the world then loads
/// with no saved mobs or drops instead of failing.
/// </para>
/// </summary>
public static class EntitySaveCodec
{
    public const string FileName = "entities.json";

    public static byte[] ToBytes(EntitySaveData data) =>
        JsonSerializer.SerializeToUtf8Bytes(data, PlayerSaveCodec.Options);

    /// <summary>The parsed file, or null for no file, the JSON <c>null</c> literal or anything
    /// that is not valid JSON of this shape (logged as a warning).</summary>
    public static EntitySaveData? TryParse(byte[]? bytes)
    {
        if (bytes == null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<EntitySaveData>(bytes, PlayerSaveCodec.Options);
        }
        catch (JsonException e)
        {
            Log.Warn($"Entity save: ignoring {FileName}: {e.Message}");
            return null;
        }
    }
}
