using System.Collections.Generic;
using VoxelEngine.Chunks.Meshing;

namespace VoxelEngine.Saves;

/// <summary>Reads wood damage and marks parser-rejected bytes for preservation on a later replacement.</summary>
internal static class WoodDamageSaveReader
{
    internal static IReadOnlyList<(long Key, WoodDamage.Entry Entry)>? Read(ChunkSaveStore? store)
    {
        if (store == null) return null;
        var entries = WoodDamageCodec.TryParse(store.ReadFile(WoodDamageCodec.FileName), out var rejected);
        if (rejected) store.MarkUnreadableFile(WoodDamageCodec.FileName);
        return entries;
    }
}
