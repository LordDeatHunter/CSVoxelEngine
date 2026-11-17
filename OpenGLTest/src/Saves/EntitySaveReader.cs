namespace VoxelEngine.Saves;

/// <summary>Reads entity state and reports actual codec rejections to its save store.</summary>
internal static class EntitySaveReader
{
    public static EntitySaveData? Read(ChunkSaveStore? store)
    {
        if (store == null)
            return null;

        var entities = EntitySaveCodec.TryParse(store.ReadFile(EntitySaveCodec.FileName), out var rejectedJson);
        if (rejectedJson)
            store.MarkUnreadableFile(EntitySaveCodec.FileName);
        return entities;
    }
}
