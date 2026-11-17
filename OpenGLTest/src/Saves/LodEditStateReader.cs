namespace VoxelEngine.Saves;

/// <summary>Reads saved LOD edits and marks rejected bytes for preservation before replacement.</summary>
internal static class LodEditStateReader
{
    internal static LodEditState? Read(ChunkSaveStore? store)
    {
        if (store == null)
            return null;

        var state = LodEditState.TryParse(store.ReadFile(LodEditState.FileName), out var rejected);
        if (rejected)
            store.MarkUnreadableFile(LodEditState.FileName);
        return state;
    }
}
