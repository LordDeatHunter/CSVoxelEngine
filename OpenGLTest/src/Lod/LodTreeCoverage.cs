using System;

namespace VoxelEngine.Lod;

/// <summary>
/// Pure 16-block tile visibility for render-only trees. Source-owned tiles never draw
/// generated proxies. A coarse tile stays visible until its replacement is resident and
/// drawable (an empty but complete finer representation qualifies), or, on level zero,
/// all full-detail chunk Ys spanning its proxy bounds are meshed. Ground and water burial
/// use their existing independent rules.
/// </summary>
internal readonly struct LodTreeCoverage
{
    internal const int TileBlocks = 16;
    private readonly ulong[] _visible;
    internal int TilesPerAxis { get; }

    private LodTreeCoverage(int tilesPerAxis, ulong[] visible)
    {
        TilesPerAxis = tilesPerAxis;
        _visible = visible;
    }

    internal bool Visible(int tileX, int tileZ)
    {
        if ((uint)tileX >= (uint)TilesPerAxis || (uint)tileZ >= (uint)TilesPerAxis)
            return false;
        var i = tileX * TilesPerAxis + tileZ;
        return (_visible[i >> 6] & (1UL << (i & 63))) != 0;
    }

    internal ReadOnlySpan<ulong> Words => _visible;

    /// <summary>Inputs are one entry per owned tile; replacementReady includes availability and upload readiness.</summary>
    internal static LodTreeCoverage Build(int regionBlocks, int level,
        ReadOnlySpan<bool> supplied, ReadOnlySpan<bool> replacementReady,
        ReadOnlySpan<bool> fullDetailYReady)
    {
        if (regionBlocks <= 0 || regionBlocks > 256 || regionBlocks % TileBlocks != 0 || level < 0)
            throw new ArgumentOutOfRangeException(nameof(regionBlocks));
        var side = regionBlocks / TileBlocks;
        var count = side * side;
        if (supplied.Length != count || replacementReady.Length != count || fullDetailYReady.Length != count)
            throw new ArgumentException("Each coverage input must have one entry per 16-block tile.");
        var visible = new ulong[(count + 63) / 64];
        for (var i = 0; i < count; i++)
        {
            if (supplied[i] || (level == 0 ? fullDetailYReady[i] : replacementReady[i]))
                continue;
            visible[i >> 6] |= 1UL << (i & 63);
        }
        return new LodTreeCoverage(side, visible);
    }

    /// <summary>Translate one uploaded finer child, including a valid empty child, into tile readiness.</summary>
    internal static void MarkChildReady(int regionBlocks, int childX, int childZ, Span<bool> ready)
    {
        if (regionBlocks <= 0 || regionBlocks > 256 || regionBlocks % (2 * TileBlocks) != 0 ||
            childX is < 0 or > 1 || childZ is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(regionBlocks));
        var side = regionBlocks / TileBlocks;
        if (ready.Length != side * side)
            throw new ArgumentException("Child readiness requires one entry per tile.", nameof(ready));
        for (var x = childX * (side / 2); x < (childX + 1) * (side / 2); x++)
        for (var z = childZ * (side / 2); z < (childZ + 1) * (side / 2); z++)
            ready[x * side + z] = true;
    }

    /// <summary>Half-open world XZ ownership; long subtraction makes negative/large seams safe.</summary>
    internal int TileIndexAt(int worldX, int worldZ, int regionOriginX, int regionOriginZ)
    {
        var x = (long)worldX - regionOriginX;
        var z = (long)worldZ - regionOriginZ;
        var side = (long)TilesPerAxis * TileBlocks;
        if (x < 0 || z < 0 || x >= side || z >= side) return -1;
        return (int)(x / TileBlocks) * TilesPerAxis + (int)(z / TileBlocks);
    }

    internal bool VisibleAt(int worldX, int worldZ, int regionOriginX, int regionOriginZ)
    {
        var index = TileIndexAt(worldX, worldZ, regionOriginX, regionOriginZ);
        return index >= 0 && (_visible[index >> 6] & (1UL << (index & 63))) != 0;
    }
}
