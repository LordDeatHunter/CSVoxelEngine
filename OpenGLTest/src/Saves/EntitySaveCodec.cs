using System.Text.Json;

namespace VoxelEngine.Saves;

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
    public static EntitySaveData? TryParse(byte[]? bytes) => TryParse(bytes, out _);

    /// <summary>
    /// Parses entity bytes and reports only a <see cref="JsonException"/> rejection. Missing bytes
    /// and a valid JSON <c>null</c> still return null without setting <paramref name="rejectedJson"/>.
    /// </summary>
    public static EntitySaveData? TryParse(byte[]? bytes, out bool rejectedJson)
    {
        rejectedJson = false;
        if (bytes == null)
            return null;
        try
        {
            var saved = JsonSerializer.Deserialize<EntitySaveData>(bytes, PlayerSaveCodec.Options);
            if (saved == null)
                return null;
            if (IgnoreNonFiniteFields(saved))
                Log.Warn($"Entity save: ignoring non-finite numeric fields in {FileName}");
            return saved;
        }
        catch (JsonException e)
        {
            rejectedJson = true;
            Log.Warn($"Entity save: ignoring {FileName}: {e.Message}");
            return null;
        }
    }

    private static bool IgnoreNonFiniteFields(EntitySaveData saved)
    {
        var ignored = false;
        if (saved.Mobs != null)
            foreach (var mob in saved.Mobs)
            {
                if (mob == null)
                    continue;
                if (HasNonFinite(mob.Position))
                {
                    mob.Position = null;
                    ignored = true;
                }
                if (HasNonFinite(mob.Velocity))
                {
                    mob.Velocity = null;
                    ignored = true;
                }
                if (mob.Facing is { } facing && !float.IsFinite(facing))
                {
                    mob.Facing = null;
                    ignored = true;
                }
            }

        if (saved.Drops != null)
            foreach (var drop in saved.Drops)
            {
                if (drop == null)
                    continue;
                if (HasNonFinite(drop.Position))
                {
                    drop.Position = null;
                    ignored = true;
                }
                if (HasNonFinite(drop.Velocity))
                {
                    drop.Velocity = null;
                    ignored = true;
                }
                if (drop.Age is { } age && !float.IsFinite(age))
                {
                    drop.Age = null;
                    ignored = true;
                }
                if (HasNonFinite(drop.Stuck))
                {
                    drop.Stuck = null;
                    ignored = true;
                }
            }

        return ignored;
    }

    private static bool HasNonFinite(float[]? values)
    {
        if (values == null)
            return false;
        foreach (var value in values)
            if (!float.IsFinite(value))
                return true;
        return false;
    }
}
