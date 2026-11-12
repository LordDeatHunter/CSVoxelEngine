using System.Runtime.CompilerServices;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;

namespace VoxelEngine.Simulation.Fluids;

/// <summary>The fluid tick's cell access for one frame: reads and writes through a direct-mapped chunk cache and collects the chunks the writes touch for one MarkDirty batch. Owned by <see cref="FluidManager"/>; main thread only.</summary>
internal sealed class FluidCells
{
    // The tick's own chunk cache and MarkDirty batch; never shared with the leaf decay, which
    // clears and flushes its own at its own point in World.Update
    private readonly ChunkCellCache _cache = new();

    // Where the tick reports a block write (lava crusting to cobblestone, dirt turning to mud)
    // that changes the far LOD's surface; set by Update, null in the headless tests, which
    // records nothing
    private LodManager? _lod;

    /// <summary>The manager this frame's tick reads and writes through.</summary>
    internal ChunkManager Chunks => _cache.Chunks;

    /// <summary>Binds this frame's managers and empties the chunk cache; called by <see cref="FluidManager.Update"/> before any tick runs.</summary>
    internal void BeginFrame(ChunkManager chunks, LodManager? lod)
    {
        // The tick's helpers read through this and its chunk cache, so they need no manager
        // argument of their own
        _cache.BeginFrame(chunks);
        _lod = lod;
    }

    /// <summary>Marks every chunk this frame's writes touched dirty, once each, then forgets them.</summary>
    internal void FlushDirty(ChunkManager chunks) => _cache.FlushDirty(chunks);

    /// <summary>
    /// Reads a world cell's block id and state through the tick's chunk cache. The contract
    /// matches <see cref="ChunkManager.TryGetCell"/>: false means the chunk is not loaded, and
    /// an unloaded cell must never read as air.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetCell(int x, int y, int z, out ushort id, out byte state) => _cache.TryGetCell(x, y, z, out id, out state);

    /// <summary>
    /// Writes a cell's id and state with no remesh of its own, queueing the chunks whose mesh
    /// the change shows up in for the batch at the end of the tick. Fluids pass light and cast
    /// no ambient occlusion, so only the cell's own chunk and the chunks across a border it
    /// touches are affected — never the whole light radius.
    /// </summary>
    internal void Write(int x, int y, int z, ushort id, byte state)
    {
        if (!_cache.Write(x, y, z, id, state, remesh: true)) return;

        // A block write — lava crusting to cobblestone, dirt turning to mud — leaves a real block
        // behind, which can raise or re-tile the far LOD like a placement. Fluid and air writes
        // never count as its surface, so they are skipped rather than read as a sub-surface
        // removal. A write at or below the column's generated water level (mud forming on a lake
        // or sea bed, lava crusting under the sea) never moves the far LOD either, which keeps
        // drawing the generated depth band there; only a block left above the water line can
        // raise or re-tile it.
        if (_lod != null && id != 0 && !BlockType.IsFluid(id) && AboveGeneratedWater(x, y, z)
            && LodSurface.RecordEdit(_cache.Chunks, x, y, z))
            _lod.MarkEditedColumn(x, z);
    }

    /// <summary>Whether world cell (x, y, z) sits above the column's water level from the world
    /// source (<see cref="ChunkManager.WorldSource"/>: worldgen's generated level by default).</summary>
    private bool AboveGeneratedWater(int x, int y, int z) =>
        y > _cache.Chunks.WorldSource.GetColumn(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            .WaterLevel[VoxelCoords.ColumnIndex(x & Chunk.SizeMask, z & Chunk.SizeMask)];
}
