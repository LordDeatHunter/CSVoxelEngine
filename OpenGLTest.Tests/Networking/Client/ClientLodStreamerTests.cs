using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking;
using Xunit;

namespace VoxelEngine.Tests.Networking.Client;

/// <summary>
/// The client's far-LOD request pacing: never more than the in-flight cap outstanding, no region
/// asked twice while its request is still young, and an unanswered request asked again once the
/// resend age has passed.
/// </summary>
public sealed class ClientLodStreamerTests
{
    public ClientLodStreamerTests() => TestContent.EnsureLoaded();

    [Fact]
    public void OutstandingRequestsNeverExceedTheInFlightCap()
    {
        var asked = new List<(int, int, int)>();
        var streamer = new ClientLodStreamer(new LodManager(null!), (l, x, z) => asked.Add((l, x, z)));

        // Many frames, no answers, well inside the resend age
        for (var i = 0; i < 200; i++)
            streamer.Update(Vector3.Zero, 0.016);

        Assert.NotEmpty(asked);
        Assert.True(asked.Count <= NetworkConfig.NetLodInFlightMax,
            $"{asked.Count} requests outstanding, cap {NetworkConfig.NetLodInFlightMax}");
        Assert.True(streamer.InFlight <= NetworkConfig.NetLodInFlightMax);
        Assert.Equal(asked.Count, asked.Distinct().Count());
    }

    [Fact]
    public void AnUnansweredRequestIsAskedAgainOnlyAfterTheResendAge()
    {
        var asked = new List<(int, int, int)>();
        var streamer = new ClientLodStreamer(new LodManager(null!), (l, x, z) => asked.Add((l, x, z)));
        streamer.Update(Vector3.Zero, 0.016);
        var first = asked[0];

        // Just short of the resend age: still outstanding, never re-sent
        var step = NetworkConfig.NetLodResendSeconds / 10.0;
        for (var i = 0; i < 9; i++)
            streamer.Update(Vector3.Zero, step * 0.99);
        Assert.Equal(1, asked.Count(k => k == first));

        // Past it: the request expired and goes out again
        streamer.Update(Vector3.Zero, step * 2);
        Assert.Equal(2, asked.Count(k => k == first));
    }

    [Fact]
    public void EditedRegionRejectsEarlierCompleteReplyAndRequestsReplacement()
    {
        var asks = new List<(int Level, int X, int Z, int Nonce)>();
        var acks = new List<byte>();
        var lod = new LodManager(null!);
        var streamer = new ClientLodStreamer(lod,
            (l, x, z, n) => asks.Add((l, x, z, n)),
            (_, _, status) => acks.Add(status));
        streamer.Update(Vector3.Zero, 0.01);
        var first = asks[0];
        var data = new LodMeshData(first.Level, first.X, first.Z, [], [], [], [],
            0, 0, new int[16], new int[16]);
        var bytes = LodNetCodec.Encode(data);
        var begin = LodTransfer.MakeBegin(first.Nonce,
            new LodTransfer.Key(first.Level, first.X, first.Z), bytes);
        using (var packet = new PacketWriter(NetMessage.LodRegionBegin))
        {
            LodTransfer.WriteBegin(packet, begin);
            using var reader = new PacketReader(packet.ToArray());
            Assert.Equal(NetMessage.LodRegionBegin, reader.Message);
            streamer.Begin(reader);
        }
        streamer.MarkEditedColumn(first.X * LodRegion.Levels[first.Level].RegionBlocks,
            first.Z * LodRegion.Levels[first.Level].RegionBlocks);
        for (var ordinal = 0; ordinal < begin.Pages; ordinal++)
        {
            using var packet = new PacketWriter(NetMessage.LodRegionPage);
            LodTransfer.WritePage(packet, first.Nonce, ordinal, bytes);
            using var reader = new PacketReader(packet.ToArray());
            Assert.Equal(NetMessage.LodRegionPage, reader.Message);
            streamer.Page(reader);
        }
        using (var packet = new PacketWriter(NetMessage.LodRegionComplete))
        {
            packet.Write(first.Nonce).Write(first.Level).Write(first.X).Write(first.Z)
                .Write(begin.Generation)
                .Write(LodTransfer.Applied);
            using var reader = new PacketReader(packet.ToArray());
            Assert.Equal(NetMessage.LodRegionComplete, reader.Message);
            streamer.Complete(reader);
        }
        Assert.Contains(LodTransfer.Canceled, acks);
        Assert.DoesNotContain(LodTransfer.Applied, acks);
        Assert.False(lod.Regions.ContainsKey((first.Level, first.X, first.Z)));
        streamer.Update(Vector3.Zero, 0.01);
        Assert.Contains(asks, request => request.Level == first.Level &&
            request.X == first.X && request.Z == first.Z && request.Nonce != first.Nonce);
    }
}
