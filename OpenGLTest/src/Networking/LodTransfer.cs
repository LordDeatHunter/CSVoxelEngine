using System;
using System.IO;
using System.Security.Cryptography;

namespace VoxelEngine.Networking;

/// <summary>Checked 64 KiB pages for one at-most-12 MiB logical LOD region.</summary>
internal static class LodTransfer
{
    internal const int PageBytes = 65_536;
    internal const int MaxPages = LodNetCodec.MaxLogicalBytes / PageBytes;
    private const int DigestBytes = 32;
    internal const byte Applied = 1, Rejected = 2, Canceled = 3;

    internal readonly record struct Key(int Level, int X, int Z);
    internal readonly record struct Begin(int Nonce, Key Region, int Generation,
        int Length, int Pages, byte[] Digest);

    internal static int PagesFor(int length) => checked((length + PageBytes - 1) / PageBytes);

    internal static Begin MakeBegin(int nonce, Key key, byte[] logical, int generation = 0)
    {
        if (nonce <= 0 || logical.Length < 72 || logical.Length > LodNetCodec.MaxLogicalBytes)
            throw new InvalidDataException("LOD transfer is outside its logical bound.");
        return new Begin(nonce, key, generation, logical.Length, PagesFor(logical.Length),
            SHA256.HashData(logical));
    }

    internal static Begin ReadBegin(PacketReader reader)
    {
        var nonce = reader.ReadInt();
        var key = new Key(reader.ReadInt(), reader.ReadInt(), reader.ReadInt());
        var generation = reader.ReadInt();
        var length = reader.ReadInt();
        var pages = reader.ReadInt();
        var digest = reader.ReadBytes(DigestBytes);
        if (nonce <= 0 || key.Level < 0 || key.Level >= Lod.LodRegion.Levels.Length ||
            generation < 0 || length < 72 || length > LodNetCodec.MaxLogicalBytes ||
            pages != PagesFor(length) || pages > MaxPages ||
            digest.Length != DigestBytes)
            throw new InvalidDataException("LOD begin has an invalid identity or length.");
        return new Begin(nonce, key, generation, length, pages, digest);
    }

    internal static void WriteBegin(PacketWriter writer, Begin begin) =>
        writer.Write(begin.Nonce).Write(begin.Region.Level).Write(begin.Region.X)
            .Write(begin.Region.Z).Write(begin.Generation).Write(begin.Length).Write(begin.Pages)
            .WriteBytes(begin.Digest);

    internal static void WritePage(PacketWriter writer, int nonce, int ordinal,
        byte[] logical, int generation = 0)
    {
        var at = checked(ordinal * PageBytes);
        if (nonce <= 0 || ordinal < 0 || at >= logical.Length)
            throw new InvalidDataException("LOD page ordinal is invalid.");
        var length = Math.Min(PageBytes, logical.Length - at);
        var page = new byte[length];
        Buffer.BlockCopy(logical, at, page, 0, length);
        writer.Write(nonce).Write(generation).Write(ordinal).WriteBytes(page);
    }

    internal sealed class Assembly
    {
        internal Begin Header { get; }
        private readonly byte[] _bytes;
        private int _next;
        internal int ReceivedPages => _next;

        internal Assembly(Begin begin)
        {
            Header = begin;
            _bytes = new byte[begin.Length];
        }

        internal bool Add(int nonce, int ordinal, byte[] page, int generation = 0)
        {
            if (nonce != Header.Nonce || generation != Header.Generation ||
                ordinal != _next || _next >= Header.Pages)
                return false;
            var at = ordinal * PageBytes;
            if (page.Length != Math.Min(PageBytes, Header.Length - at))
                return false;
            Buffer.BlockCopy(page, 0, _bytes, at, page.Length);
            _next++;
            return true;
        }

        internal byte[] Finish(int nonce)
        {
            if (nonce != Header.Nonce || _next != Header.Pages ||
                !SHA256.HashData(_bytes).AsSpan().SequenceEqual(Header.Digest))
                throw new InvalidDataException("LOD pages are missing or corrupted.");
            return _bytes;
        }
    }
}
