using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.Lod;

/// <summary>
/// Canonical, region-independent tree description for a future shared-template draw.
/// Sixteen signed 32-bit words also form four std430 ivec4s. No atlas layer or block ID is
/// implicit in a material index; material tables are remapped separately on a client.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = ByteSize)]
internal readonly struct LodTreeInstance
{
    internal const int ByteSize = 64;
    internal const int NoMaterial = -1;
    internal const int AddedCap = 1;
    private const int KnownFlags = AddedCap;

    [FieldOffset(0)] internal readonly int RootX;
    [FieldOffset(4)] internal readonly int RootZ;
    [FieldOffset(8)] internal readonly int BaseY;
    [FieldOffset(12)] internal readonly int CrownBottom;
    [FieldOffset(16)] internal readonly int CrownTop;
    [FieldOffset(20)] internal readonly int MinX;
    [FieldOffset(24)] internal readonly int MaxX;
    [FieldOffset(28)] internal readonly int MinZ;
    [FieldOffset(32)] internal readonly int MaxZ;
    [FieldOffset(36)] internal readonly int TrunkWidth;
    [FieldOffset(40)] internal readonly int WoodMaterial;
    [FieldOffset(44)] internal readonly int LeafMaterial;
    [FieldOffset(48)] internal readonly int Profile;
    [FieldOffset(52)] internal readonly int Variant;
    [FieldOffset(56)] internal readonly int Flags;
    [FieldOffset(60)] internal readonly int Reserved;

    private LodTreeInstance(LodTreeProxy tree, int woodMaterial, int leafMaterial, bool added)
    {
        RootX = tree.RootX;
        RootZ = tree.RootZ;
        BaseY = tree.BaseY;
        CrownBottom = tree.CrownBottom;
        CrownTop = tree.CrownTop;
        MinX = tree.MinX;
        MaxX = tree.MaxX;
        MinZ = tree.MinZ;
        MaxZ = tree.MaxZ;
        TrunkWidth = tree.TrunkWidth;
        WoodMaterial = woodMaterial;
        LeafMaterial = leafMaterial;
        Profile = (int)tree.Profile;
        Variant = (tree.RootX ^ tree.RootZ) & 1;
        Flags = added ? AddedCap : 0;
        Reserved = 0;
    }

    internal bool HasTrunk => (Flags & AddedCap) == 0;
    internal bool HasCrown => LeafMaterial != NoMaterial && CrownTop > CrownBottom;

    internal static bool TryCreate(LodTreeProxy tree, int woodMaterial, int leafMaterial,
        int materialCount, bool added, out LodTreeInstance value)
    {
        value = default;
        if (materialCount < 0 || tree.MinX >= tree.MaxX || tree.MinZ >= tree.MaxZ ||
            tree.CrownBottom > tree.CrownTop || tree.TrunkWidth < 1 ||
            !Enum.IsDefined(tree.Profile) ||
            leafMaterial < 0 || leafMaterial >= materialCount ||
            (!added && (woodMaterial < 0 || woodMaterial >= materialCount)) ||
            (added && woodMaterial != NoMaterial))
            return false;
        value = new LodTreeInstance(tree, woodMaterial, leafMaterial, added);
        return true;
    }

    /// <summary>Subtract in 64-bit integer space before the proxy converts XZ to floats.</summary>
    internal LodTreeProxy ToRegionProxy(int originX, int originZ) => new(
        Relative(RootX, originX), Relative(RootZ, originZ), BaseY, CrownBottom, CrownTop,
        Relative(MinX, originX), Relative(MaxX, originX), Relative(MinZ, originZ),
        Relative(MaxZ, originZ), TrunkWidth, 0, HasCrown ? (ushort)1 : (ushort)0,
        (LodCrownProfile)Profile);

    private static int Relative(int world, int origin)
    {
        var difference = (long)world - origin;
        if (difference < int.MinValue || difference > int.MaxValue)
            throw new OverflowException("Tree proxy is too far from its region origin.");
        return (int)difference;
    }

    internal void WriteLittleEndian(Span<byte> destination)
    {
        if (destination.Length < ByteSize) throw new ArgumentException("A tree instance needs 64 bytes.", nameof(destination));
        Span<int> words = stackalloc int[16]
        {
            RootX, RootZ, BaseY, CrownBottom, CrownTop, MinX, MaxX, MinZ, MaxZ,
            TrunkWidth, WoodMaterial, LeafMaterial, Profile, Variant, Flags, Reserved
        };
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(i * sizeof(int), sizeof(int)), words[i]);
    }

    internal static bool TryReadLittleEndian(ReadOnlySpan<byte> source, int materialCount,
        out LodTreeInstance value)
    {
        value = default;
        if (source.Length != ByteSize || materialCount < 0) return false;
        Span<int> w = stackalloc int[16];
        for (var i = 0; i < w.Length; i++)
            w[i] = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * sizeof(int), sizeof(int)));
        if (w[15] != 0 || (w[14] & ~KnownFlags) != 0 || w[13] is < 0 or > 1 ||
            !Enum.IsDefined((LodCrownProfile)w[12]) || w[5] >= w[6] || w[7] >= w[8] ||
            w[3] > w[4] || w[9] < 1 || w[11] < 0 || w[11] >= materialCount ||
            ((w[14] & AddedCap) != 0 ? w[10] != NoMaterial : w[10] < 0 || w[10] >= materialCount) ||
            w[13] != ((w[0] ^ w[1]) & 1))
            return false;
        var tree = new LodTreeProxy(w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], w[8],
            w[9], 0, 1, (LodCrownProfile)w[12]);
        return TryCreate(tree, w[10], w[11], materialCount, (w[14] & AddedCap) != 0, out value);
    }
}
