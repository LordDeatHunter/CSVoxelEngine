using System.Text.Json;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking;

/// <summary>
/// The wire form of <see cref="NetMessage.QuestState"/>: the same JSON shape <see cref="QuestSaveData"/> has in
/// <c>player.json</c> (quests by id, never by index), without the book view, which is the client's own. The
/// client imports it into its mirror log and toasts the quests it did not have complete yet.
/// </summary>
public static class QuestStateCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] Encode(QuestSaveData data) =>
        JsonSerializer.SerializeToUtf8Bytes(new QuestSaveData { Completed = data.Completed, Progress = data.Progress }, Options);

    public static QuestSaveData Decode(byte[] payload) =>
        JsonSerializer.Deserialize<QuestSaveData>(payload, Options) ?? new QuestSaveData();
}
