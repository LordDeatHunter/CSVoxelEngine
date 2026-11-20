using System;
using System.IO;
using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>The bounded multipart exchange rejects gaps, corruption, and oversized sections.</summary>
public sealed class LodTransferTests
{
    [Fact]
    public void OneRegionRequiresOrderedCompletePagesAndMatchingDigest()
    {
        var logical = new byte[LodTransfer.PageBytes + 19];
        logical[0] = 17;
        logical[^1] = 91;
        var begin = LodTransfer.MakeBegin(7, new LodTransfer.Key(0, -2, 3), logical);
        using var writer = new PacketWriter(NetMessage.LodRegionBegin);
        LodTransfer.WriteBegin(writer, begin);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.LodRegionBegin, reader.Message);
        var assembly = new LodTransfer.Assembly(LodTransfer.ReadBegin(reader));
        Assert.False(assembly.Add(7, 1, logical.AsSpan(LodTransfer.PageBytes).ToArray()));
        Assert.True(assembly.Add(7, 0, logical.AsSpan(0, LodTransfer.PageBytes).ToArray()));
        Assert.False(assembly.Add(7, 0, logical.AsSpan(0, LodTransfer.PageBytes).ToArray()));
        Assert.Throws<InvalidDataException>(() => assembly.Finish(7));
        Assert.True(assembly.Add(7, 1, logical.AsSpan(LodTransfer.PageBytes).ToArray()));
        Assert.Equal(logical, assembly.Finish(7));

        var changed = (byte[])logical.Clone();
        changed[^1]++;
        var corrupt = new LodTransfer.Assembly(begin);
        Assert.True(corrupt.Add(7, 0, changed.AsSpan(0, LodTransfer.PageBytes).ToArray()));
        Assert.True(corrupt.Add(7, 1, changed.AsSpan(LodTransfer.PageBytes).ToArray()));
        Assert.Throws<InvalidDataException>(() => corrupt.Finish(7));
    }

    [Fact]
    public void BeginRejectsOversizedLogicalRegionBeforeAssemblyAllocation()
    {
        using var writer = new PacketWriter(NetMessage.LodRegionBegin);
        writer.Write(1).Write(0).Write(0).Write(0)
            .Write(LodNetCodec.MaxLogicalBytes + 1).Write(LodTransfer.MaxPages + 1)
            .WriteBytes(new byte[32]);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.LodRegionBegin, reader.Message);
        Assert.Throws<InvalidDataException>(() => LodTransfer.ReadBegin(reader));
    }
}
