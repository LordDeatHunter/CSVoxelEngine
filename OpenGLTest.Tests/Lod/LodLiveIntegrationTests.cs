using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Content-free contracts of the live codec, cover, GPU pack and transfer owners.</summary>
public sealed class LodLiveIntegrationTests
{
    [Fact]
    public void SupportedDenseTreeAndFullUshortMeshRoundTrip()
    {
        var trees = Trees(0, 0, 0, 2049, 0);
        var vertex = new Vertex(new Vector3(1, 2, 3), 255, 255, 255, 255,
            Vector2.Zero, 1);
        var vertices = new List<Vertex>(65_536);
        for (var i = 0; i < 65_536; i++) vertices.Add(vertex);
        var indices = new List<ushort>(98_304) { ushort.MaxValue };
        for (var i = 1; i < 98_304; i++) indices.Add((ushort)(i & 0xffff));
        var data = new LodMeshData(0, 0, 0, vertices, indices, [], [],
            0, 64, new int[16], new int[16]) { Trees = trees };
        var decoded = LodNetCodec.Decode(LodNetCodec.Encode(data));
        Assert.Equal(65_536, decoded.Vertices.Count);
        Assert.Equal(ushort.MaxValue, decoded.Indices[0]);
        Assert.Equal(2049, decoded.Trees.Count);
    }

    [Fact]
    public void ProductionCoverWaitsForFadeAndPropagatesPerTileReadiness()
    {
        var regions = new LodRegionSet(new ConcurrentDictionary<(int Level, int X, int Z), LodRegion>(),
            new List<LodRegion>());
        var coarse = HeadlessRegion(1, 0, 0, 0, 4, 32, 36);
        var child = HeadlessRegion(0, 0, 0, 0);
        regions.AddRegion((1, 0, 0), coarse);
        regions.AddRegion((0, 0, 0), child);
        var cover = new LodTreeCoverPass(regions);
        cover.Update(static (_, _, _, _) => false);
        Assert.True(coarse.TreeVisibleAt(0, 0));
        Assert.True(coarse.TreeVisibleAt(4, 0));
        child.Lifetime = LodRegion.MAX_LIFETIME;
        cover.Update(static (_, _, _, _) => false);
        Assert.False(coarse.TreeVisibleAt(0, 0));
        Assert.True(coarse.TreeVisibleAt(4, 0));
        foreach (var (x, z, tile) in new[] { (1, 0, 0), (0, 1, 0), (1, 1, 0) })
        {
            var next = HeadlessRegion(0, x, z, tile);
            next.Lifetime = LodRegion.MAX_LIFETIME;
            regions.AddRegion((0, x, z), next);
        }
        cover.Update(static (_, _, _, _) => false);
        Assert.False(coarse.TreeVisibleAt(4, 0));
        Assert.False(coarse.TreeVisibleAt(0, 4));
        Assert.False(coarse.TreeVisibleAt(4, 4));
        child.Lifetime = 0;
        cover.Update(static (_, _, _, _) => true);
        Assert.False(coarse.TreeVisibleAt(0, 0)); // hidden child is covered by full detail
        regions.Regions.TryRemove((0, 0, 0), out _);
        regions.RemoveRegion(child);
        cover.Update(static (_, _, _, _) => false);
        Assert.True(coarse.TreeVisibleAt(0, 0));
        var empty = new LodRegion(null, new LodMeshData(0, 0, 0, [], [], [], [],
            0, 0, new int[16], new int[16]));
        regions.AddRegion((0, 0, 0), empty);
        cover.Update(static (_, _, _, _) => false);
        Assert.False(coarse.TreeVisibleAt(0, 0));
        Assert.False(coarse.TreeVisibleAt(-1, 0));
    }

    [Fact]
    public void PackedGpuPayloadRetainsMaskHighBitsAndLocalBounds()
    {
        var trees = Trees(2, 100_000, -100_000, 1, 255);
        var expected = new ulong[]
        {
            (1UL << 31) | (1UL << 32) | (1UL << 63),
            1UL | (1UL << 63), 1UL, 1UL << 63
        };
        expected.CopyTo(trees.GeneratedTiles, 0);
        var payload = LodTreeGpuPayload.Pack(trees, 100_000, -100_000);
        var b = payload.Bytes;
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(64)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(68)));
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], BinaryPrimitives.ReadUInt64LittleEndian(
                b.AsSpan(payload.MaskOffset + i * 8)));
        var (vertices, indices) = LodTreeTemplate.Build();
        Assert.Equal(144, vertices.Length);
        Assert.Equal(216, indices.Length);
    }

    [Fact]
    public void TransferCreditCancelTimeoutAndErrorsKeepBoundedOwnership()
    {
        var credit = new LodTransferCredit();
        for (var i = 0; i < 64; i++) Assert.True(credit.Request((0, i, 0), i + 1, 64));
        Assert.False(credit.Request((0, 65, 0), 66, 64));
        Assert.True(credit.CancelQueued((0, 0, 0), 1));
        Assert.True(credit.Available);
        Assert.True(credit.Request((0, 65, 0), 66, 64));
        Assert.True(credit.Begin((0, 1, 0), 2, 9, 1));
        Assert.False(credit.Acknowledge((0, 1, 0), 2, 8, LodTransfer.Applied));
        Assert.False(credit.Expire(2, 3));
        Assert.True(credit.Expire(5, 3));
        Assert.False(credit.Available);
        Assert.False(credit.Begin((0, 2, 0), 3, 10, 6));
        Assert.True(credit.Acknowledge((0, 1, 0), 2, 9, LodTransfer.Rejected));
        Assert.True(credit.Begin((0, 2, 0), 3, 10, 6));
        Assert.True(credit.Acknowledge((0, 2, 0), 3, 10, LodTransfer.Rejected));
        credit.Clear();
        Assert.Empty(credit.Queued);
        Assert.Empty(credit.Nonces);
    }

    [Fact]
    public void TimedOutPeerKeepsCreditUntilMatchingCancel()
    {
        var credit = new LodTransferCredit();
        var first = (Level: 0, Rx: 3, Rz: -2);
        var second = (Level: 0, Rx: 4, Rz: -2);
        Assert.True(credit.Request(first, 11, 64));
        Assert.True(credit.Request(second, 12, 64));
        Assert.True(credit.Begin(first, 11, 6, 0));
        credit.Progress(2);
        Assert.False(credit.Expire(4, 3));
        Assert.True(credit.Expire(6, 3));
        Assert.False(credit.OwnsRawBytes);
        Assert.False(credit.Available);
        Assert.False(credit.Expire(20, 3)); // raw release happens only once
        Assert.False(credit.Begin(second, 12, 7, 20));
        Assert.False(credit.Acknowledge(first, 10, 0, LodTransfer.Canceled));
        Assert.False(credit.Acknowledge(second, 12, 0, LodTransfer.Canceled));
        Assert.False(credit.Available);
        Assert.True(credit.Acknowledge(first, 11, 0, LodTransfer.Canceled));
        Assert.True(credit.Available);
        Assert.True(credit.Begin(second, 12, 7, 21));
        credit.Clear(); // disconnect releases the active identity and queued metadata
        Assert.True(credit.Available);
        Assert.Null(credit.AwaitingAck);
        Assert.Empty(credit.Queued);
        Assert.Empty(credit.Nonces);
    }

    [Fact]
    public void EditGenerationsInvalidateStaleTransfersWithoutRetainedHistory()
    {
        var edits = new LodEditInvalidations();
        edits.AddColumn(-1, -1, [44, 60, 60]);
        Assert.True(edits.Affects(new LodTransfer.Key(0, -1, -1)));
        Assert.True(edits.Generation > 0);
        var sent = 0;
        edits.Drain((_, _) => sent++);
        Assert.True(sent > 0 && sent <= LodEditInvalidations.MaxPending);
        Assert.Equal(0, edits.Count);

        var lod = new LodManager(null!);
        var region = new LodMeshData(0, 0, 0, [], [], [], [], 0, 0,
            new int[16], new int[16]);
        lod.AddStreamedRegion(region);
        var client = new ClientLodStreamer(lod, static (_, _, _, _) => { },
            static (_, _, _, _) => { });
        for (var i = 0; i < 128; i++) client.MarkRegionEdited(0, 0, 0);
        Assert.Equal(1, client.RetainedRevisions);
        Assert.True(lod.Regions.TryRemove((0, 0, 0), out var resident));
        lod.RemoveRegion(resident!);
        resident!.Dispose();
        client.Update(Vector3.Zero, 0);
        Assert.Equal(0, client.RetainedRevisions);
    }

    internal static LodRegion HeadlessRegion(int level, int x, int z, params int[] tileIndices)
    {
        var trees = Trees(level, x * LodRegion.Levels[level].RegionBlocks,
            z * LodRegion.Levels[level].RegionBlocks, 1, tileIndices);
        var side = LodRegion.Levels[level].RegionBlocks / 16;
        return new LodRegion(null, new LodMeshData(level, x, z, [], [], [], [],
            0, 64, new int[side * side], new int[side * side]) { Trees = trees });
    }

    private static LodTreeBuild Trees(int level, int originX, int originZ, int count,
        params int[] tileIndices)
    {
        var side = LodRegion.Levels[level].RegionBlocks / 16;
        var proxy = new LodTreeProxy(originX + 2, originZ + 3, 32, 40, 52,
            originX, originX + Math.Min(80, side * 16), originZ,
            originZ + Math.Min(80, side * 16), 1, 11, 12, LodCrownProfile.Dome);
        Assert.True(LodTreeInstance.TryCreate(proxy, 0, 1, 2, false, out var instance));
        ushort[] face = [1, 1, 1, 1, 1, 1];
        ushort[] absent = [LodTreeMaterial.NoTile, LodTreeMaterial.NoTile,
            LodTreeMaterial.NoTile, LodTreeMaterial.NoTile, LodTreeMaterial.NoTile,
            LodTreeMaterial.NoTile];
        Assert.True(LodTreeMaterial.TryCreate(face, absent, null, out var material));
        var min = new int[side * side]; var max = new int[min.Length];
        Array.Fill(min, int.MaxValue); Array.Fill(max, int.MinValue);
        var mask = new ulong[(min.Length + 63) / 64];
        foreach (var index in tileIndices)
        { min[index] = 32; max[index] = 52; mask[index >> 6] |= 1UL << (index & 63); }
        var instances = new LodTreeInstance[count];
        Array.Fill(instances, instance);
        return new LodTreeBuild(instances, [material, material],
            new LodTreeTintLattice(side, new Vector3[(side + 1) * (side + 1)],
                new Vector3[(side + 1) * (side + 1)]), mask, min, max);
    }
}
