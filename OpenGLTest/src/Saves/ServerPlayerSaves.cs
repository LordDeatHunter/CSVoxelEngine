using VoxelEngine.Networking;

namespace VoxelEngine.Saves;

/// <summary>
/// Where a hosted world keeps each multiplayer player: one <see cref="PlayerSaveCodec"/>-shaped
/// file per player under <see cref="WorldStore.PlayersDirectoryName"/>, keyed by the slug of the
/// player's name (until players authenticate, the name is the identity). Reads and writes go
/// through the world's <see cref="ChunkSaveStore"/>, so they share its writer thread, its pending
/// map and its session lock; a null store (a test server) persists nothing.
/// </summary>
public static class ServerPlayerSaves
{
    /// <summary>The store-relative file of the player called <paramref name="name"/>.</summary>
    public static string FileNameFor(string name) =>
        $"{WorldStore.PlayersDirectoryName}/{WorldNames.Slug(name)}.json";

    /// <summary>The saved player, or null when there is none (or no store).</summary>
    public static PlayerSaveData? Load(ChunkSaveStore? store, string name) =>
        store == null ? null : PlayerSaveCodec.TryParse(store.ReadFile(FileNameFor(name)));

    /// <summary>Queues the player's file; does nothing without a store.</summary>
    public static void Save(ChunkSaveStore? store, ServerPlayer player)
    {
        if (store == null)
            return;
        store.EnqueueFile(FileNameFor(player.Name), PlayerSaveCodec.ToBytes(player.Capture()));
    }
}
