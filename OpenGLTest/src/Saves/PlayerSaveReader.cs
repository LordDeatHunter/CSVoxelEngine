namespace VoxelEngine.Saves;

/// <summary>Reads a player sidecar and reports actual codec rejections to its save store.</summary>
internal static class PlayerSaveReader
{
    public static PlayerSaveData? Read(ChunkSaveStore? store, string fileName)
    {
        if (store == null)
            return null;

        var player = PlayerSaveCodec.TryParse(store.ReadFile(fileName), out var rejectedJson);
        if (rejectedJson)
            store.MarkUnreadableFile(fileName);
        return player;
    }
}
