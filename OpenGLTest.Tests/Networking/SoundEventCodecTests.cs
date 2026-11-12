using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The world sounds on the wire (<see cref="SoundEventCodec"/>): a frame round-trips each group and position (never the
/// excepted player, which only picks who gets it), a player is sent only the sounds in earshot that were not its own,
/// and a frame with too many sounds, an overlong group or a non-finite position is malformed.
/// </summary>
public sealed class SoundEventCodecTests
{
    private static List<SoundEvent> RoundTrip(IReadOnlyList<SoundEvent> sounds)
    {
        using var writer = new PacketWriter(NetMessage.SoundEvents);
        SoundEventCodec.Write(writer, sounds);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.SoundEvents, reader.Message);
        var back = new List<SoundEvent>();
        SoundEventCodec.Read(reader, back);
        return back;
    }

    private static PacketReader RawFrame(int count, string group, Vector3 position)
    {
        using var writer = new PacketWriter(NetMessage.SoundEvents);
        writer.Write(count);
        writer.Write(group);
        writer.Write(position);
        var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.SoundEvents, reader.Message);
        return reader;
    }

    [Fact]
    public void AFrameRoundTripsEachSoundsGroupAndPosition()
    {
        var sounds = new[]
        {
            new SoundEvent(SfxLibrary.SlimeJumpGroup, new Vector3(1.5f, 64f, -3.25f)),
            new SoundEvent(SfxLibrary.StoneGroup, new Vector3(-100.5f, 12f, 7f), ExceptPlayerId: 3),
        };

        Assert.Equal([sounds[0], sounds[1] with { ExceptPlayerId = 0 }], RoundTrip(sounds));
    }

    [Fact]
    public void APlayerIsSentTheSoundsInEarshotThatWereNotItsOwn()
    {
        var listener = new Vector3(0f, 64f, 0f);
        var near = new SoundEvent(SfxLibrary.SlimeJumpGroup, listener + new Vector3(5f, 0f, 0f));
        var own = new SoundEvent(SfxLibrary.StoneGroup, listener + new Vector3(1f, 0f, 0f), ExceptPlayerId: 2);
        var far = new SoundEvent(SfxLibrary.SlimeLandGroup, listener + new Vector3(AudioConfig.SfxRangeBlocks + 5f, 0f, 0f));
        var into = new List<SoundEvent>();

        SoundEventCodec.SelectFor([near, own, far], playerId: 2, listener, into);
        Assert.Equal([near], into);

        SoundEventCodec.SelectFor([near, own, far], playerId: 7, listener, into);
        Assert.Equal([near, own], into);
    }

    [Fact]
    public void AMalformedFrameIsRefused()
    {
        var into = new List<SoundEvent>();
        Assert.Throws<InvalidDataException>(() =>
            SoundEventCodec.Read(RawFrame(NetworkConfig.NetSoundEventsPerBroadcast + 1, "x", Vector3.Zero), into));
        Assert.Throws<InvalidDataException>(() =>
            SoundEventCodec.Read(RawFrame(-1, "x", Vector3.Zero), into));
        Assert.Throws<InvalidDataException>(() =>
            SoundEventCodec.Read(RawFrame(1, new string('a', SoundEventCodec.MaxGroupLength + 1), Vector3.Zero), into));
        Assert.Throws<InvalidDataException>(() =>
            SoundEventCodec.Read(RawFrame(1, "x", new Vector3(float.NaN, 0f, 0f)), into));
    }
}
