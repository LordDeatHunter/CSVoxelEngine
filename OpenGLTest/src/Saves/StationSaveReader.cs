namespace VoxelEngine.Saves;

/// <summary>Reads station state and reports actual codec rejections to its save store.</summary>
internal static class StationSaveReader
{
    public static StationSaveData? Read(ChunkSaveStore? store)
    {
        if (store == null)
            return null;

        var stations = StationSaveCodec.TryParse(store.ReadFile(StationSaveCodec.FileName), out var rejectedJson);
        if (rejectedJson)
            store.MarkUnreadableFile(StationSaveCodec.FileName);
        return stations;
    }
}
