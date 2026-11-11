using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Graphics;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Chests;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The server's name tables in its hello and the client's translation through them: every numbered space crosses the
/// wire by name, so a server whose content is ordered differently (another file added, renamed or removed) still
/// means the same block, item, metal, effect and texture to the client. Content the client lacks refuses the
/// connection; a local id the server lacks reaches the server as an id it refuses; mob data in an entity's item field
/// is never mistaken for an id.
/// </summary>
public sealed class WireIdsTests
{
    public WireIdsTests() => TestContent.EnsureLoaded();

    /// <summary>This run's names for each space, in id order, as a server with identical content would send them.</summary>
    private sealed record Tables(List<string> Content, List<string> Materials, List<string> Effects, List<string> Tiles)
    {
        internal static Tables Local() => new(
            Enumerable.Range(0, BlockTables.IdCount).Select(id => SaveNames.NameOf((ushort)id) ?? "").ToList(),
            ContentRegistry.MoltenMaterials.Select(m => m.Name).ToList(),
            StatusEffectRegistry.All.Select(e => e.Name).ToList(),
            TextureAtlas.TileNames.ToList());

        /// <summary>The same names with every space but air's slot reversed: a server that numbers everything differently.</summary>
        internal Tables Reordered() => new(
            [Content[0], .. Content.Skip(1).Reverse()],
            Enumerable.Reverse(Materials).ToList(),
            Enumerable.Reverse(Effects).ToList(),
            Enumerable.Reverse(Tiles).ToList());

        internal byte[] Hello()
        {
            using var writer = new PacketWriter(NetMessage.ServerHello);
            foreach (var space in new[] { Content, Materials, Effects, Tiles })
            {
                writer.Write(space.Count);
                foreach (var name in space)
                    writer.Write(name);
            }
            return writer.ToArray();
        }
    }

    private static WireIds Read(byte[] hello)
    {
        using var reader = new PacketReader(hello);
        Assert.Equal(NetMessage.ServerHello, reader.Message);
        return WireIds.Read(reader);
    }

    private static PacketReader ReaderOf(PacketWriter writer, WireIds wire)
    {
        var reader = new PacketReader(writer.ToArray(), wire);
        _ = reader.Message;
        return reader;
    }

    [Fact]
    public void AServerWithIdenticalContentTranslatesEveryIdToItself()
    {
        using var hello = new PacketWriter(NetMessage.ServerHello);
        WireIds.WriteTables(hello);
        var wire = Read(hello.ToArray());

        Assert.Empty(wire.Missing);
        for (var id = 0; id < BlockTables.IdCount; id++)
        {
            Assert.Equal((ushort)id, wire.ContentToLocal((ushort)id));
            Assert.Equal((ushort)id, wire.ContentToServer((ushort)id));
        }
    }

    [Fact]
    public void AnIdFromAReorderedServerReadsAsTheLocalIdOfTheSameName()
    {
        var local = Tables.Local();
        var server = local.Reordered();
        var wire = Read(server.Hello());
        Assert.Empty(wire.Missing);

        for (var serverId = 1; serverId < server.Content.Count; serverId++)
        {
            var name = server.Content[serverId];
            if (name.Length == 0) continue;

            using var frame = new PacketWriter(NetMessage.BlockChanges);
            frame.WriteId((ushort)serverId); // the server writes its own numbering
            using var reader = ReaderOf(frame, wire);

            var localId = reader.ReadId();
            Assert.Equal(name, SaveNames.NameOf(localId));
        }
    }

    [Fact]
    public void AnIdTheClientSendsGoesOutInTheServersNumbering()
    {
        var server = Tables.Local().Reordered();
        var wire = Read(server.Hello());

        for (var localId = 1; localId < BlockTables.IdCount; localId++)
        {
            var name = SaveNames.NameOf((ushort)localId);
            if (name == null) continue;

            using var frame = new PacketWriter(NetMessage.WorldIntent, wire);
            frame.WriteId((ushort)localId);
            using var reader = new PacketReader(frame.ToArray()); // the server reads with no table
            _ = reader.Message;

            Assert.Equal(name, server.Content[reader.ReadId()]);
        }
    }

    [Fact]
    public void ContentOrAMaterialTheClientLacksRefusesTheConnection()
    {
        var local = Tables.Local();
        var server = local with
        {
            Content = [.. local.Content, SaveNames.BlockPrefix + "only_on_the_server"],
            Materials = [.. local.Materials, "unobtainium"],
        };

        var wire = Read(server.Hello());

        Assert.Contains(SaveNames.BlockPrefix + "only_on_the_server", wire.Missing);
        Assert.Contains(wire.Missing, name => name.Contains("unobtainium", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEffectOrTextureTheClientLacksIsOnlyDropped()
    {
        var local = Tables.Local();
        var server = local with
        {
            Effects = [.. local.Effects.Take(7), "only_on_the_server"],
            Tiles = [.. local.Tiles, "only_on_the_server"],
        };

        var wire = Read(server.Hello());

        Assert.Empty(wire.Missing);
        var unknownBit = (byte)(1 << (server.Effects.Count - 1));
        Assert.Equal(0, wire.EffectMaskToLocal(unknownBit));
        Assert.Equal(0, wire.TileToLocal((ushort)(server.Tiles.Count - 1)));
    }

    [Fact]
    public void ALocalIdTheServerLacksReachesItAsAnIdItRefuses()
    {
        var local = Tables.Local();
        // A server missing the client's last id: the client may still hold that content and name it in an intent
        var server = local with { Content = local.Content.Take(local.Content.Count - 1).ToList() };
        var wire = Read(server.Hello());
        var clientOnly = (ushort)(local.Content.Count - 1);

        var move = ChestMove.DepositStack(clientOnly, 1);
        using var frame = new PacketWriter(NetMessage.ChestMove, wire);
        ChestNetCodec.WriteMove(frame, move);
        using var reader = new PacketReader(frame.ToArray());
        _ = reader.Message;

        var received = ChestNetCodec.ReadMove(reader);
        Assert.Equal(WireIds.NotOnServer, received.Id);
        Assert.False(BlockTables.IsAssignedId(received.Id));
    }

    [Fact]
    public void AnEffectMaskFromAReorderedServerNamesTheSameEffects()
    {
        var local = Tables.Local();
        Assert.True(local.Effects.Count >= 2, "a reorder of fewer than two effects proves nothing");
        var server = local.Reordered();
        var wire = Read(server.Hello());

        for (var serverBit = 0; serverBit < server.Effects.Count; serverBit++)
        {
            Assert.True(StatusEffectRegistry.TryGet(server.Effects[serverBit], out var effect));
            Assert.Equal(effect.Bit, wire.EffectMaskToLocal((byte)(1 << serverBit)));
        }
    }

    [Fact]
    public void AMaterialAndATextureFromAReorderedServerNameTheSameOnes()
    {
        var local = Tables.Local();
        var server = local.Reordered();
        var wire = Read(server.Hello());

        for (var serverId = 1; serverId <= server.Materials.Count; serverId++)
        {
            Assert.True(SaveNames.TryResolveMaterial(server.Materials[serverId - 1], out var localId));
            Assert.Equal(localId, wire.MaterialToLocal((byte)serverId));
            Assert.Equal((byte)serverId, wire.MaterialToServer(localId));
        }

        for (var serverLayer = 0; serverLayer < server.Tiles.Count; serverLayer++)
            Assert.Equal(server.Tiles[serverLayer], local.Tiles[wire.TileToLocal((ushort)serverLayer)]);
    }

    [Theory]
    [InlineData(1)]   // Uniform
    [InlineData(40)]  // Indexed
    [InlineData(300)] // Direct, past the palette, when the content has that many ids
    public void AChunkFromAReorderedServerHoldsTheSameBlocks(int distinct)
    {
        var local = Tables.Local();
        var server = local.Reordered();
        var wire = Read(server.Hello());
        var named = Enumerable.Range(1, server.Content.Count - 1).Where(id => server.Content[id].Length > 0).ToArray();
        distinct = Math.Min(distinct, named.Length);

        // The chunk as the server stores it: its own numbering
        var serverIds = new ushort[Chunk.BlockCount];
        for (var i = 0; i < serverIds.Length; i++)
            serverIds[i] = (ushort)named[i % distinct];

        var decoded = ChunkNetCodec.Decode(ChunkNetCodec.Encode(new ChunkSnapshot(serverIds, null, null)), wire);

        var cells = decoded.Storage.ToDense();
        for (var i = 0; i < cells.Length; i++)
            Assert.Equal(server.Content[serverIds[i]], SaveNames.NameOf(cells[i]));
    }

    [Fact]
    public void AMobsItemFieldIsDataAndOnlyContentKindsAreTranslated()
    {
        var server = Tables.Local().Reordered();
        var wire = Read(server.Hello());
        var serverDropId = (ushort)Array.FindIndex(server.Content.ToArray(), n => n.Length > 0);

        using var frame = new PacketWriter(NetMessage.EntityList);
        NetEntityCodec.Write(frame, new NetEntity(NetEntityKind.Skeleton, 1, default, 0f, 1, 0, 3, default));
        NetEntityCodec.Write(frame, new NetEntity(NetEntityKind.Drop, 2, default, 0f, 1, 0, serverDropId, default));
        using var reader = ReaderOf(frame, wire);

        Assert.Equal(3, NetEntityCodec.Read(reader).ItemId);
        Assert.Equal(server.Content[serverDropId], SaveNames.NameOf(NetEntityCodec.Read(reader).ItemId));
    }
}
