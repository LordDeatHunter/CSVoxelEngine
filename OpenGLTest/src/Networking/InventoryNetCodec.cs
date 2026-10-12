using System.Text.Json;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking;

/// <summary>
/// The inventory's wire form: the same JSON shape <see cref="PlayerSaveData.InventoryData"/> uses in
/// <c>player.json</c>, so a server can send the whole inventory to a client and the client imports
/// it unchanged. JSON keeps the content-name references the save format already guarantees.
/// </summary>
public static class InventoryNetCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] Encode(PlayerSaveData.InventoryData data) =>
        JsonSerializer.SerializeToUtf8Bytes(data, Options);

    public static PlayerSaveData.InventoryData Decode(byte[] payload) =>
        JsonSerializer.Deserialize<PlayerSaveData.InventoryData>(payload, Options) ?? new PlayerSaveData.InventoryData();
}
