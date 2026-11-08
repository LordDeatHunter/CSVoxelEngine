using System.Collections.Generic;
using System.IO;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The waystone messages on the wire: a rename, a player's rows and an activation answer round-trip their fields; an
/// overlong rename, a bad row count or a name no waystone would keep is a malformed frame; and the client's mirror lists
/// what the newest rows say, offers each activation answer once and lands only an accepted trip.
/// </summary>
public sealed class WaystoneNetCodecTests
{
    private static PacketReader ReaderOf(PacketWriter writer, NetMessage message)
    {
        var reader = new PacketReader(writer.ToArray());
        Assert.Equal(message, reader.Message);
        return reader;
    }

    [Fact]
    public void ARenameRoundTripsItsCellAndTheRawName()
    {
        using var writer = new PacketWriter(NetMessage.WaystoneRename);
        WaystoneNetCodec.WriteRename(writer, (-12, -330, 4096), "  Home  sweet ");
        using var reader = ReaderOf(writer, NetMessage.WaystoneRename);

        var (cell, name) = WaystoneNetCodec.ReadRename(reader);

        Assert.Equal((-12, -330, 4096), cell);
        // The server sanitizes when it applies the rename, so the text arrives as it was typed
        Assert.Equal("  Home  sweet ", name);
    }

    [Fact]
    public void AnOverlongRenameIsAMalformedFrame()
    {
        using var writer = new PacketWriter(NetMessage.WaystoneRename);
        WaystoneNetCodec.WriteRename(writer, (0, 0, 0), new string('a', WaystoneNetCodec.MaxRenameLength + 1));
        using var reader = ReaderOf(writer, NetMessage.WaystoneRename);

        Assert.Throws<InvalidDataException>(() => WaystoneNetCodec.ReadRename(reader));
    }

    [Fact]
    public void RowsRoundTripEveryField()
    {
        var rows = new List<WaystoneNetRow>
        {
            new(10, 64, -3, "Kafuziro", Lost: false, MayRename: true),
            new(-200, -400, 7, "Hell gate", Lost: true, MayRename: false),
        };
        using var writer = new PacketWriter(NetMessage.WaystoneRows);
        WaystoneNetCodec.WriteRows(writer, rows);
        using var reader = ReaderOf(writer, NetMessage.WaystoneRows);

        var back = new List<WaystoneNetRow> { new(1, 1, 1, "stale", false, false) };
        WaystoneNetCodec.ReadRows(reader, back);

        Assert.Equal(rows, back);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(WaystoneNetCodec.MaxRows + 1)]
    public void ARowCountOutOfRangeIsAMalformedFrame(int count)
    {
        using var writer = new PacketWriter(NetMessage.WaystoneRows);
        writer.Write(count);
        using var reader = ReaderOf(writer, NetMessage.WaystoneRows);

        Assert.Throws<InvalidDataException>(() => WaystoneNetCodec.ReadRows(reader, []));
    }

    [Fact]
    public void ANameNoWaystoneWouldKeepIsAMalformedFrame()
    {
        using var writer = new PacketWriter(NetMessage.WaystoneOpened);
        WaystoneNetCodec.WriteOpened(writer, new WaystoneOpened(1, 2, 3, "   ", MayRename: false, NewToPlayer: true));
        using var reader = ReaderOf(writer, NetMessage.WaystoneOpened);

        Assert.Throws<InvalidDataException>(() => WaystoneNetCodec.ReadOpened(reader));
    }

    [Fact]
    public void AnActivationAnswerRoundTripsItsFields()
    {
        var opened = new WaystoneOpened(5, 320, -9, "Tobeluma", MayRename: true, NewToPlayer: false);
        using var writer = new PacketWriter(NetMessage.WaystoneOpened);
        WaystoneNetCodec.WriteOpened(writer, opened);
        using var reader = ReaderOf(writer, NetMessage.WaystoneOpened);

        Assert.Equal(opened, WaystoneNetCodec.ReadOpened(reader));
    }

    [Fact]
    public void TheMirrorListsTheNewestRowsAndALostWaystoneIsNeverRenamable()
    {
        var mirror = new WaystoneMirror();
        mirror.Replace([new WaystoneNetRow(1, 2, 3, "Old", false, true)]);
        var version = mirror.Version;

        mirror.Replace([new WaystoneNetRow(1, 2, 3, "New", true, true), new WaystoneNetRow(4, 5, 6, "Other", false, false)]);

        Assert.NotEqual(version, mirror.Version);
        Assert.Equal("New", mirror.Peek(1, 2, 3)!.Name);
        Assert.True(mirror.Peek(1, 2, 3)!.Lost);
        Assert.False(mirror.MayRename(1, 2, 3));
        Assert.False(mirror.MayRename(4, 5, 6));
        Assert.Null(mirror.Peek(7, 8, 9));
    }

    [Fact]
    public void AnActivationAnswerIsTakenOnce()
    {
        var mirror = new WaystoneMirror();
        Assert.False(mirror.TryTakeOpened(out _));

        mirror.Offer(new WaystoneOpened(1, 2, 3, "Here", true, true));

        Assert.True(mirror.TryTakeOpened(out var opened));
        Assert.Equal((1, 2, 3), opened.Cell);
        Assert.False(mirror.TryTakeOpened(out _));
    }

    [Fact]
    public void OnlyAnAcceptedTripLandsOnTheNextCorrection()
    {
        var mirror = new WaystoneMirror();
        mirror.NoteTripSent(7, (100, 64, 0));
        // A correction before the server answered the trip is not its landing
        Assert.False(mirror.TryTakeLanding(out _));
        // Another intent's answer leaves the trip alone
        Assert.False(mirror.NoteResult(6, accepted: true));
        Assert.True(mirror.NoteResult(7, accepted: true));

        Assert.True(mirror.TryTakeLanding(out var destination));
        Assert.Equal((100, 64, 0), destination);
        Assert.False(mirror.TryTakeLanding(out _));

        mirror.NoteTripSent(8, (5, 5, 5));
        Assert.True(mirror.NoteResult(8, accepted: false));
        Assert.False(mirror.TryTakeLanding(out _));
    }
}
