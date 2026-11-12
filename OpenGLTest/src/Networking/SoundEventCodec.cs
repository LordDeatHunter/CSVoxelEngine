using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VoxelEngine.Audio;

namespace VoxelEngine.Networking;

/// <summary>
/// One <see cref="NetMessage.SoundEvents"/> frame, the only writer and reader of its fields:
/// <c>(int count)</c>, then per sound <c>(string group, Vector3 position)</c>. The excepted player never travels:
/// the server leaves that player out when it picks who gets the frame. The reader refuses a count past
/// <see cref="NetworkConfig.NetSoundEventsPerBroadcast"/>, a group name longer than <see cref="MaxGroupLength"/> and
/// a position that is not finite as <see cref="InvalidDataException"/>; a group the client lacks just plays nothing.
/// </summary>
public static class SoundEventCodec
{
    /// <summary>The longest sound group name a frame may carry.</summary>
    public const int MaxGroupLength = 64;

    public static void Write(PacketWriter writer, IReadOnlyList<SoundEvent> sounds)
    {
        writer.Write(sounds.Count);
        foreach (var sound in sounds)
        {
            writer.Write(sound.Group);
            writer.Write(sound.Position);
        }
    }

    /// <summary>Reads a frame's sounds into <paramref name="into"/> (cleared first).</summary>
    public static void Read(PacketReader reader, List<SoundEvent> into)
    {
        into.Clear();
        var count = reader.ReadInt();
        if (count < 0 || count > NetworkConfig.NetSoundEventsPerBroadcast)
            throw new InvalidDataException($"sound frame of {count} sounds");
        for (var i = 0; i < count; i++)
        {
            var group = reader.ReadString();
            if (group.Length > MaxGroupLength)
                throw new InvalidDataException($"sound group name of {group.Length} characters");
            var position = reader.ReadVector3();
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new InvalidDataException("sound at a non-finite position");
            into.Add(new SoundEvent(group, position));
        }
    }

    /// <summary>
    /// The sounds of <paramref name="queued"/> one player at <paramref name="listener"/> (server id
    /// <paramref name="playerId"/>) should hear: within earshot and not excepted for that player.
    /// </summary>
    public static void SelectFor(IReadOnlyList<SoundEvent> queued, int playerId, Vector3 listener, List<SoundEvent> into)
    {
        into.Clear();
        foreach (var sound in queued)
            if (sound.ExceptPlayerId != playerId && SoundFalloff.InRange(listener, sound.Position))
                into.Add(sound);
    }
}
