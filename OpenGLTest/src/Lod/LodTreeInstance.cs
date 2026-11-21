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
    // Region-local floats retain block precision here; world XZ stays signed integer until
    // the region origin is subtracted. These are representation limits, not worldgen tuning.
    internal const int MaxLocalCoordinate = 1 << 20;
    internal const int MaxWorldY = 1 << 20;
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
        var woodOnly = !added && leafMaterial == NoMaterial && tree.CrownBottom == tree.CrownTop;
        if (materialCount < 0 || tree.MinX >= tree.MaxX || tree.MinZ >= tree.MaxZ ||
            tree.CrownBottom > tree.CrownTop || tree.TrunkWidth < 1 ||
            !Enum.IsDefined(tree.Profile) ||
            tree.BaseY > tree.CrownBottom ||
            (tree.CrownBottom == tree.CrownTop && !woodOnly) ||
            (tree.CrownBottom < tree.CrownTop && leafMaterial == NoMaterial) ||
            (tree.LeavesId == 0 && !woodOnly) ||
            !InWorldY(tree.BaseY) || !InWorldY(tree.CrownBottom) || !InWorldY(tree.CrownTop) ||
            tree.BaseY == MaxWorldY ||
            (long)tree.MaxX - tree.MinX > MaxLocalCoordinate ||
            (long)tree.MaxZ - tree.MinZ > MaxLocalCoordinate ||
            (long)tree.CrownTop - tree.BaseY > MaxLocalCoordinate ||
            tree.TrunkWidth > MaxLocalCoordinate ||
            (!woodOnly && (leafMaterial < 0 || leafMaterial >= materialCount)) ||
            (woodOnly && tree.LeavesId != 0) ||
            (added && (tree.Profile != LodCrownProfile.Column ||
                (long)tree.CrownTop - tree.CrownBottom != 2)) ||
            (!added && (woodMaterial < 0 || woodMaterial >= materialCount)) ||
            (added && woodMaterial != NoMaterial))
            return false;
        value = new LodTreeInstance(tree, woodMaterial, leafMaterial, added);
        return true;
    }

    /// <summary>Subtract in 64-bit integer space before any XZ value becomes a float.</summary>
    internal bool TryRegionProxy(int originX, int originZ, out LodTreeProxy proxy)
    {
        proxy = default;
        if (!Relative(RootX, originX, out var rootX) || !Relative(RootZ, originZ, out var rootZ) ||
            !Relative(MinX, originX, out var minX) || !Relative(MaxX, originX, out var maxX) ||
            !Relative(MinZ, originZ, out var minZ) || !Relative(MaxZ, originZ, out var maxZ))
            return false;
        if (HasTrunk && ((long)Math.Abs(rootX) + TrunkWidth > MaxLocalCoordinate ||
                         (long)Math.Abs(rootZ) + TrunkWidth > MaxLocalCoordinate)) return false;
        proxy = new LodTreeProxy(rootX, rootZ, BaseY, CrownBottom, CrownTop,
            minX, maxX, minZ, maxZ, TrunkWidth, 0, HasCrown ? (ushort)1 : (ushort)0,
            (LodCrownProfile)Profile);
        return true;
    }

    private static bool InWorldY(int y) => y >= -MaxWorldY && y <= MaxWorldY;

    private static bool Relative(int world, int origin, out int local)
    {
        var difference = (long)world - origin;
        local = 0;
        if (difference < -MaxLocalCoordinate || difference > MaxLocalCoordinate) return false;
        local = (int)difference;
        return true;
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

    /// <summary>GPU copy: region-local signed XZ, with the canonical wire instance unchanged.</summary>
    internal void WriteRegionLittleEndian(int originX, int originZ, Span<byte> destination)
    {
        if (!TryRegionProxy(originX, originZ, out _))
            throw new InvalidOperationException("Tree exceeds the region-local GPU coordinate bound.");
        WriteLittleEndian(destination);
        BinaryPrimitives.WriteInt32LittleEndian(destination[0..4], (int)((long)RootX - originX));
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..8], (int)((long)RootZ - originZ));
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..24], (int)((long)MinX - originX));
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..28], (int)((long)MaxX - originX));
        BinaryPrimitives.WriteInt32LittleEndian(destination[28..32], (int)((long)MinZ - originZ));
        BinaryPrimitives.WriteInt32LittleEndian(destination[32..36], (int)((long)MaxZ - originZ));
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
            w[12] is < 0 or > (int)LodCrownProfile.Column ||
            w[5] >= w[6] || w[7] >= w[8] ||
            w[3] > w[4] || w[9] < 1 ||
            ((w[14] & AddedCap) != 0 ? w[10] != NoMaterial : w[10] < 0 || w[10] >= materialCount) ||
            w[13] != ((w[0] ^ w[1]) & 1))
            return false;
        var tree = new LodTreeProxy(w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], w[8],
            w[9], 0, w[11] == NoMaterial ? (ushort)0 : (ushort)1, (LodCrownProfile)w[12]);
        return TryCreate(tree, w[10], w[11], materialCount, (w[14] & AddedCap) != 0, out value);
    }
}
