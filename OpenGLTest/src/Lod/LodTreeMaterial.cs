using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Effects.Climate;

namespace VoxelEngine.Lod;

/// <summary>
/// Six base/tinted face pairs in Chunk.CubeFaces order. Tile values are local atlas layers,
/// not block IDs; a wire decoder must remap each non-sentinel tile before GPU upload.
/// Eight 32-bit words also form a 32-byte std430-compatible material record.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = ByteSize)]
internal readonly struct LodTreeMaterial
{
    internal const int ByteSize = 32;
    internal const ushort NoTile = BiomeTintFaces.NoTile;
    private const uint Grass = 1;
    private const uint Foliage = 2;
    private const uint HasTint = 1u << 8;
    private const uint HasOverlay = 1u << 9;
    private const uint KnownBits = 3 | HasTint | HasOverlay;

    [FieldOffset(0)] private readonly uint _face0;
    [FieldOffset(4)] private readonly uint _face1;
    [FieldOffset(8)] private readonly uint _face2;
    [FieldOffset(12)] private readonly uint _face3;
    [FieldOffset(16)] private readonly uint _face4;
    [FieldOffset(20)] private readonly uint _face5;
    [FieldOffset(24)] private readonly uint _paletteFlags;
    [FieldOffset(28)] private readonly uint _reserved;

    private LodTreeMaterial(ReadOnlySpan<uint> faces, uint flags)
    {
        _face0 = faces[0]; _face1 = faces[1]; _face2 = faces[2];
        _face3 = faces[3]; _face4 = faces[4]; _face5 = faces[5];
        _paletteFlags = flags;
        _reserved = 0;
    }

    internal ushort BaseAt(int face) => (ushort)Face(face);
    internal ushort TintedAt(int face) => (ushort)(Face(face) >> 16);
    internal bool HasOverlayAt(int face) => BaseAt(face) != NoTile && TintedAt(face) != NoTile;
    internal BiomeTintPalette? Palette => (_paletteFlags & 3) switch
    {
        Grass => BiomeTintPalette.Grass,
        Foliage => BiomeTintPalette.Foliage,
        _ => null
    };

    private uint Face(int face) => face switch
    {
        0 => _face0, 1 => _face1, 2 => _face2, 3 => _face3,
        4 => _face4, 5 => _face5,
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    };

    internal static bool TryCreate(ReadOnlySpan<ushort> bases, ReadOnlySpan<ushort> tinted,
        BiomeTintPalette? palette, out LodTreeMaterial value)
    {
        value = default;
        if (bases.Length != 6 || tinted.Length != 6 ||
            (palette is { } p && !Enum.IsDefined(p))) return false;
        Span<uint> faces = stackalloc uint[6];
        uint flags = 0;
        for (var i = 0; i < 6; i++)
        {
            if (tinted[i] != NoTile)
            {
                if (palette == null) return false;
                flags |= HasTint;
                if (bases[i] != NoTile) flags |= HasOverlay;
            }
            faces[i] = bases[i] | ((uint)tinted[i] << 16);
        }
        flags |= palette switch
        {
            BiomeTintPalette.Grass => Grass,
            BiomeTintPalette.Foliage => Foliage,
            _ => 0
        };
        if ((flags & HasTint) == 0 && palette != null) return false;
        value = new LodTreeMaterial(faces, flags);
        return true;
    }

    /// <summary>Resolve the exact base/overlay faces currently emitted by LodFaceEmitter.</summary>
    internal static bool TryFromBlock(BlockType block, out LodTreeMaterial value)
    {
        if (block.BiomeTint is { } bt)
            return TryCreate(bt.Base, bt.Tinted, bt.Palette, out value);
        Span<ushort> absent = stackalloc ushort[6];
        absent.Fill(NoTile);
        return TryCreate(block.FaceTiles, absent, null, out value);
    }

    /// <summary>Remap every present atlas layer and keep the NoTile sentinel unchanged.</summary>
    internal bool TryRemap(Func<ushort, ushort> map, out LodTreeMaterial value)
    {
        value = default;
        if (map == null) return false;
        Span<ushort> bases = stackalloc ushort[6];
        Span<ushort> tinted = stackalloc ushort[6];
        for (var i = 0; i < 6; i++)
        {
            var a = BaseAt(i);
            var b = TintedAt(i);
            bases[i] = a == NoTile ? NoTile : map(a);
            tinted[i] = b == NoTile ? NoTile : map(b);
            if ((a != NoTile && bases[i] == NoTile) || (b != NoTile && tinted[i] == NoTile))
                return false;
        }
        return TryCreate(bases, tinted, Palette, out value);
    }

    internal void WriteLittleEndian(Span<byte> destination)
    {
        if (destination.Length < ByteSize) throw new ArgumentException("A tree material needs 32 bytes.", nameof(destination));
        Span<uint> words = stackalloc uint[8]
        { _face0, _face1, _face2, _face3, _face4, _face5, _paletteFlags, _reserved };
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(i * sizeof(uint), sizeof(uint)), words[i]);
    }

    internal static bool TryReadLittleEndian(ReadOnlySpan<byte> source, out LodTreeMaterial value)
    {
        value = default;
        if (source.Length != ByteSize) return false;
        Span<uint> w = stackalloc uint[8];
        for (var i = 0; i < w.Length; i++)
            w[i] = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(i * sizeof(uint), sizeof(uint)));
        if (w[7] != 0 || (w[6] & ~KnownBits) != 0) return false;
        Span<ushort> bases = stackalloc ushort[6];
        Span<ushort> tinted = stackalloc ushort[6];
        for (var i = 0; i < 6; i++)
        {
            bases[i] = (ushort)w[i];
            tinted[i] = (ushort)(w[i] >> 16);
        }
        BiomeTintPalette? palette = (w[6] & 3) switch
        {
            Grass => BiomeTintPalette.Grass,
            Foliage => BiomeTintPalette.Foliage,
            0 => null,
            _ => null
        };
        if ((w[6] & 3) == 3 || !TryCreate(bases, tinted, palette, out value)) return false;
        Span<byte> canonical = stackalloc byte[ByteSize];
        value.WriteLittleEndian(canonical);
        return source.SequenceEqual(canonical);
    }
}
