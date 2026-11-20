using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using VoxelEngine.Data;
using VoxelEngine.Lod;

namespace VoxelEngine.Networking;

/// <summary>
/// Canonical checked protocol-69 logical region. A single raw array is divided into bounded
/// pages by the transport; no compressed, unbounded decode stream or full-region ToArray copy.
/// Every count and product is checked before the first list or GPU allocation.
/// </summary>
public static class LodNetCodec
{
    private const int Magic = 0x39444f4c; // LOD9, little endian
    private const int HeaderWords = 18;
    private const int HeaderBytes = HeaderWords * sizeof(int);
    public const int MaxLogicalBytes = 12 * 1024 * 1024;
    private const int MaxTrees = 2048; // 1024 added caps + at most the finite 64-block root neighborhood

    public static byte[] Encode(LodMeshData data)
    {
        if (data.Level < 0 || data.Level >= LodRegion.Levels.Length ||
            !float.IsFinite(data.YMin) || !float.IsFinite(data.YMax) || data.YMin > data.YMax ||
            data.Vertices.Count > ushort.MaxValue || data.Indices.Count > ushort.MaxValue * 3 ||
            data.WaterVertices.Count > 4096 || data.WaterIndices.Count > 6144 ||
            data.Trees.Count > MaxTrees)
            throw new InvalidDataException("Unsupported LOD region.");
        var tiles = data.Trees.ProxyYMin.Length;
        var side = data.Trees.Tint?.LatticeSide ?? 0;
        var total = Size(data.Vertices.Count, data.Indices.Count,
            data.WaterVertices.Count, data.WaterIndices.Count, data.ColumnCyMin.Length,
            data.Trees.Count, data.Trees.Materials.Length, side,
            data.Trees.GeneratedTiles.Length, tiles);
        var owned = LodRegion.Levels[data.Level].RegionBlocks / 16;
        if (data.ColumnCyMin.Length != data.ColumnCyMax.Length ||
            data.ColumnCyMin.Length != owned * owned ||
            (data.Trees.Count == 0 ? tiles != 0 || side != 0 :
                tiles != owned * owned || side != owned + 1 ||
                data.Trees.GeneratedTiles.Length != (tiles + 63) / 64) ||
            total < 0 ||
            total > MaxLogicalBytes)
            throw new InvalidDataException("LOD region length exceeds its logical bound.");
        var bytes = new byte[total];
        Span<int> h = stackalloc int[HeaderWords]
        {
            Magic, data.Level, data.X, data.Z,
            BitConverter.SingleToInt32Bits(data.YMin), BitConverter.SingleToInt32Bits(data.YMax),
            data.Vertices.Count, data.Indices.Count, data.WaterVertices.Count,
            data.WaterIndices.Count, data.ColumnCyMin.Length, data.Trees.Count,
            data.Trees.Materials.Length, side, data.Trees.GeneratedTiles.Length, tiles, 0, 0
        };
        for (var i = 0; i < h.Length; i++) PutInt(bytes, i * 4, h[i]);
        var at = HeaderBytes;
        WriteVertices(data.Vertices); WriteIndices(data.Indices);
        WriteVertices(data.WaterVertices); WriteIndices(data.WaterIndices);
        void WriteVertices(List<Vertex> list)
        {
            var span = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(list));
            span.CopyTo(bytes.AsSpan(at));
            at += span.Length;
        }
        void WriteIndices(List<ushort> list)
        {
            var span = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(list));
            span.CopyTo(bytes.AsSpan(at));
            at += span.Length;
        }
        // The two Y arrays are separate and have identical checked lengths.
        foreach (var value in data.ColumnCyMin) { PutInt(bytes, at, value); at += 4; }
        foreach (var value in data.ColumnCyMax) { PutInt(bytes, at, value); at += 4; }
        foreach (var instance in data.Trees.Instances)
        {
            instance.WriteLittleEndian(bytes.AsSpan(at, LodTreeInstance.ByteSize));
            at += LodTreeInstance.ByteSize;
        }
        foreach (var material in data.Trees.Materials)
        {
            material.WriteLittleEndian(bytes.AsSpan(at, LodTreeMaterial.ByteSize));
            at += LodTreeMaterial.ByteSize;
        }
        if (data.Trees.Tint is { } tint)
        {
            var grass = MemoryMarshal.AsBytes(tint.Grass);
            var foliage = MemoryMarshal.AsBytes(tint.Foliage);
            grass.CopyTo(bytes.AsSpan(at)); at += grass.Length;
            foliage.CopyTo(bytes.AsSpan(at)); at += foliage.Length;
        }
        foreach (var bits in data.Trees.GeneratedTiles)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), bits);
            at += 8;
        }
        foreach (var value in data.Trees.ProxyYMin) { PutInt(bytes, at, value); at += 4; }
        foreach (var value in data.Trees.ProxyYMax) { PutInt(bytes, at, value); at += 4; }
        if (at != bytes.Length) throw new InvalidDataException("LOD size calculation drifted.");
        return bytes;
    }

    /// <summary>Validate an entire logical region and remap every present terrain and tree tile.</summary>
    public static LodMeshData Decode(byte[] payload, WireIds? wire = null)
    {
        if (payload.Length < HeaderBytes || payload.Length > MaxLogicalBytes)
            throw new InvalidDataException("LOD logical payload length is invalid.");
        int H(int i) => GetInt(payload, i * 4);
        if (H(0) != Magic || H(16) != 0 || H(17) != 0)
            throw new InvalidDataException("LOD schema is invalid.");
        var level = H(1);
        if (level < 0 || level >= LodRegion.Levels.Length)
            throw new InvalidDataException("LOD level is invalid.");
        var yMin = BitConverter.Int32BitsToSingle(H(4));
        var yMax = BitConverter.Int32BitsToSingle(H(5));
        if (!float.IsFinite(yMin) || !float.IsFinite(yMax) || yMin > yMax)
            throw new InvalidDataException("LOD bounds are invalid.");
        var nv = H(6); var ni = H(7); var nwv = H(8); var nwi = H(9);
        var nc = H(10); var nt = H(11); var nm = H(12); var side = H(13);
        var masks = H(14); var tiles = H(15);
        var owned = LodRegion.Levels[level].RegionBlocks / 16;
        if (nv < 0 || nv > ushort.MaxValue || ni < 0 || ni > ushort.MaxValue * 3 ||
            nwv < 0 || nwv > 4096 || nwi < 0 || nwi > 6144 ||
            nc != owned * owned || nt < 0 || nt > MaxTrees ||
            nm < 0 || nm > ushort.MaxValue + 1 ||
            (nt == 0 ? nm != 0 || side != 0 || masks != 0 || tiles != 0 :
                nm == 0 || side != owned + 1 || tiles != owned * owned ||
                masks != (tiles + 63) / 64) ||
            Size(nv, ni, nwv, nwi, nc, nt, nm, side, masks, tiles) != payload.Length)
            throw new InvalidDataException("LOD section lengths or counts are invalid.");
        var at = HeaderBytes;
        var vertices = ReadVertices(nv);
        var indices = ReadIndices(ni, nv);
        var waterVertices = ReadVertices(nwv);
        var waterIndices = ReadIndices(nwi, nwv);
        var cyMin = ReadInts(nc); var cyMax = ReadInts(nc);
        for (var i = 0; i < nc; i++)
            if (cyMin[i] > cyMax[i] || (long)cyMax[i] - cyMin[i] > 128)
                throw new InvalidDataException("LOD chunk-Y interval is invalid.");
        var originX = (long)H(2) * LodRegion.Levels[level].RegionBlocks;
        var originZ = (long)H(3) * LodRegion.Levels[level].RegionBlocks;
        if (originX < int.MinValue || originX > int.MaxValue ||
            originZ < int.MinValue || originZ > int.MaxValue)
            throw new InvalidDataException("LOD region origin is outside representable world space.");
        var instances = new LodTreeInstance[nt];
        for (var i = 0; i < nt; i++)
        {
            if (!LodTreeInstance.TryReadLittleEndian(payload.AsSpan(at, LodTreeInstance.ByteSize), nm,
                    out instances[i]) ||
                !instances[i].TryRegionProxy((int)originX, (int)originZ, out _))
                throw new InvalidDataException("LOD tree instance is invalid.");
            at += LodTreeInstance.ByteSize;
        }
        var materials = new LodTreeMaterial[nm];
        for (var i = 0; i < nm; i++)
        {
            if (!LodTreeMaterial.TryReadLittleEndian(payload.AsSpan(at, LodTreeMaterial.ByteSize), out var material) ||
                wire is not null && !material.TryRemap(wire.TileToLocal, out material))
                throw new InvalidDataException("LOD tree material is invalid.");
            materials[i] = material;
            at += LodTreeMaterial.ByteSize;
        }
        LodTreeTintLattice? tint = null;
        if (side > 0)
        {
            var colors = side * side;
            var grass = new Vector3[colors];
            var foliage = new Vector3[colors];
            for (var palette = 0; palette < 2; palette++)
            for (var i = 0; i < colors; i++)
            {
                var v = MemoryMarshal.Read<Vector4>(payload.AsSpan(at, 16));
                if (v.W != 0 || !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
                    throw new InvalidDataException("LOD tint corner is invalid.");
                (palette == 0 ? grass : foliage)[i] = new Vector3(v.X, v.Y, v.Z);
                at += 16;
            }
            tint = new LodTreeTintLattice(owned, grass, foliage);
        }
        var generated = new ulong[masks];
        for (var i = 0; i < masks; i++)
        {
            generated[i] = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(at, 8));
            at += 8;
        }
        var proxyMin = ReadInts(tiles); var proxyMax = ReadInts(tiles);
        for (var i = 0; i < tiles; i++)
        {
            var empty = proxyMin[i] == int.MaxValue && proxyMax[i] == int.MinValue;
            if (!empty && (proxyMin[i] >= proxyMax[i] ||
                proxyMin[i] < -LodTreeInstance.MaxWorldY ||
                proxyMax[i] > LodTreeInstance.MaxWorldY))
                throw new InvalidDataException("LOD proxy Y interval is invalid.");
            if ((generated[i >> 6] & (1UL << (i & 63))) == 0 && !empty)
                throw new InvalidDataException("Supplied LOD tile owns a generated tree.");
        }
        if (at != payload.Length) throw new InvalidDataException("LOD tail length is invalid.");
        var trees = nt == 0 ? LodTreeBuild.Empty :
            new LodTreeBuild(instances, materials, tint, generated, proxyMin, proxyMax);
        return new LodMeshData(level, H(2), H(3), vertices, indices, waterVertices, waterIndices,
            yMin, yMax, cyMin, cyMax) { Trees = trees };

        List<Vertex> ReadVertices(int count)
        {
            var data = MemoryMarshal.Cast<byte, Vertex>(payload.AsSpan(at, count * (int)Vertex.Size));
            var list = new List<Vertex>(count);
            foreach (var vertex in data)
            {
                var v = vertex;
                if (!float.IsFinite(v.Position.X) || !float.IsFinite(v.Position.Y) ||
                    !float.IsFinite(v.Position.Z) || !float.IsFinite(v.Uv.X) || !float.IsFinite(v.Uv.Y))
                    throw new InvalidDataException("LOD vertex is nonfinite.");
                if (wire is not null) v.Layer = wire.TileToLocal(v.Layer);
                list.Add(v);
            }
            at += count * (int)Vertex.Size;
            return list;
        }
        List<ushort> ReadIndices(int count, int vertexCount)
        {
            var data = MemoryMarshal.Cast<byte, ushort>(payload.AsSpan(at, count * 2));
            var list = new List<ushort>(count);
            foreach (var index in data)
            {
                if (index >= vertexCount) throw new InvalidDataException("LOD index is outside its vertex section.");
                list.Add(index);
            }
            at += count * 2;
            return list;
        }
        int[] ReadInts(int count)
        {
            var values = new int[count];
            for (var i = 0; i < count; i++) { values[i] = GetInt(payload, at); at += 4; }
            return values;
        }
    }

    private static int Size(int nv, int ni, int nwv, int nwi, int nc, int nt,
        int nm, int side, int masks, int tiles)
    {
        if (nv < 0 || ni < 0 || nwv < 0 || nwi < 0 || nc < 0 || nt < 0 ||
            nm < 0 || side < 0 || masks < 0 || tiles < 0) return -1;
        var bytes = (long)HeaderBytes + ((long)nv + nwv) * Vertex.Size +
            ((long)ni + nwi) * 2 + (long)nc * 8 +
            (long)nt * LodTreeInstance.ByteSize + (long)nm * LodTreeMaterial.ByteSize +
            2L * side * side * 16 + (long)masks * 8 + (long)tiles * 8;
        return bytes > MaxLogicalBytes ? -1 : (int)bytes;
    }

    private static int GetInt(ReadOnlySpan<byte> bytes, int at) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at, 4));
    private static void PutInt(Span<byte> bytes, int at, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(at, 4), value);
}
