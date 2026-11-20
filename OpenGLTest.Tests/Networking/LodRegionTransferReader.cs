using System;
using System.IO;
using VoxelEngine.Lod;
using VoxelEngine.Networking;

namespace VoxelEngine.Tests.Networking;

/// <summary>Consumes a complete protocol-69 region in loopback tests, including its credit ACK.</summary>
internal sealed class LodRegionTransferReader(Action<byte[]> send)
{
    private LodTransfer.Assembly? _assembly;

    internal LodMeshData? Accept(NetMessage message, PacketReader reader)
    {
        switch (message)
        {
            case NetMessage.LodRegionBegin:
                _assembly = new LodTransfer.Assembly(LodTransfer.ReadBegin(reader));
                break;
            case NetMessage.LodRegionPage:
            {
                var nonce = reader.ReadInt();
                var generation = reader.ReadInt();
                var ordinal = reader.ReadInt();
                if (_assembly is not { } assembly ||
                    !assembly.Add(nonce, ordinal, reader.ReadBytes(LodTransfer.PageBytes), generation))
                    throw new InvalidDataException("Invalid test LOD page.");
                break;
            }
            case NetMessage.LodRegionComplete:
            {
                var nonce = reader.ReadInt();
                var key = new LodTransfer.Key(reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
                var generation = reader.ReadInt();
                if (reader.ReadByte() != LodTransfer.Applied ||
                    _assembly is not { } assembly || assembly.Header.Region != key ||
                    assembly.Header.Generation != generation)
                    throw new InvalidDataException("Invalid test LOD completion.");
                var result = LodNetCodec.Decode(assembly.Finish(nonce));
                using var ack = new PacketWriter(NetMessage.LodRegionAck);
                ack.Write(nonce).Write(key.Level).Write(key.X).Write(key.Z)
                    .Write(generation).Write(LodTransfer.Applied);
                send(ack.ToArray());
                _assembly = null;
                return result;
            }
        }
        return null;
    }
}
