using System;
using System.IO;
using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The cell-change codec the server writes every BlockChanges and BlockCorrection cell through and the client reads
/// them back with: the highest id and state come back exactly and leave the frame aligned for what follows, and a
/// change cut short is a short frame, never a guessed cell.
/// </summary>
public sealed class NetCellChangeCodecTests
{
    private static byte[] Frame(in NetCellChange change)
    {
        using var writer = new PacketWriter(NetMessage.BlockChanges);
        NetCellChangeCodec.Write(writer, change);
        writer.Write(int.MinValue);
        return writer.ToArray();
    }

    [Fact]
    public void ACellChangeRoundTripsTheHighestIdAndState()
    {
        var change = new NetCellChange(int.MaxValue, -1_000_000, int.MinValue, ushort.MaxValue, byte.MaxValue);
        using var reader = new PacketReader(Frame(change));
        Assert.Equal(NetMessage.BlockChanges, reader.Message);

        Assert.Equal(change, NetCellChangeCodec.Read(reader));
        Assert.Equal(int.MinValue, reader.ReadInt());
    }

    [Fact]
    public void ATruncatedChangeThrowsEndOfStream()
    {
        var full = Frame(new NetCellChange(3, 70, -9, ushort.MaxValue, 2));
        // Drop the trailing sentinel and the change's last byte, its state
        var cut = full.AsSpan(0, full.Length - sizeof(int) - 1).ToArray();
        using var reader = new PacketReader(cut);
        _ = reader.Message;
        Assert.Throws<EndOfStreamException>(() => NetCellChangeCodec.Read(reader));
    }
}
