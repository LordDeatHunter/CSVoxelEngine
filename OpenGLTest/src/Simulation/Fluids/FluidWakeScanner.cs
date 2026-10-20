using System;
using VoxelEngine.Chunks;
using VoxelEngine.Data;

namespace VoxelEngine.Simulation.Fluids;

/// <summary>Wakes the water of newly loaded chunks: schedules each chunk's worker-found frontier and scans the faces it shares with its loaded neighbors, so a breached sea pours in and restored poured water picks up where it left off. Reads chunk blocks directly, never through the tick's cache. Owned by <see cref="FluidManager"/>; main thread only.</summary>
internal sealed class FluidWakeScanner
{
    private readonly FluidSchedule _schedule;

    internal FluidWakeScanner(FluidSchedule schedule)
    {
        _schedule = schedule;
    }

    /// <summary>
    /// Schedules the frontier of every chunk that finished generating (or was restored from a
    /// save) since the last call, so a sea breached by a cave or an entrance starts pouring in
    /// the moment it loads instead of standing there as a wall, and water a player poured before
    /// the chunk unloaded picks up where it left off. Generated water only wakes against flow
    /// targets at or below their column's terrain top, so open air above the terrain is never
    /// woken (see <see cref="FluidWakeRules.IsCarvedFlowTarget"/>); a mid-flow cell, or a source above both its
    /// column's terrain top and water level (only ever poured), wakes against any replaceable
    /// neighbor (see <see cref="FluidWakeRules.IsWakeTarget"/>).
    /// Interior frontiers were found on the generation worker (<see cref="Chunk.FluidFrontier"/>);
    /// the borders need both chunks loaded, so they are scanned here — always for a restored
    /// chunk and for a chunk loading beside one, since poured water can sit at any height. At most
    /// <see cref="SimulationConfig.MaxWakeChunksPerFrame"/> chunks are woken per call; the
    /// rest wait in the queue for the next frame.
    ///
    /// Main thread, called once a frame right before <see cref="FluidManager.Update"/> so the cells it
    /// schedules ride the same tick as this frame's block edits.
    /// </summary>
    internal void WakeLoadedChunks(ChunkManager chunks)
    {
        var woken = 0;
        var backlog = SimulationConfig.MaxFluidBacklogCells;
        while (woken < SimulationConfig.MaxWakeChunksPerFrame
               && _schedule.Count < backlog
               && chunks.TryDequeueLoaded(out var chunk))
        {
            woken++;
            // An unload can race the queue: only the chunk still registered at its position
            // owns those cells, and a stale one's blocks are nobody's business
            if (!chunks.Chunks.TryGetValue(chunk.Position, out var live) || live != chunk) continue;

            var frontier = chunk.FluidFrontier;
            if (frontier != null)
            {
                foreach (var index in frontier)
                {
                    // Scheduling the fluid cell alone is enough: its tick runs Spread, and Flow
                    // reschedules the neighborhood of everything it writes into
                    _schedule.ScheduleLocal(chunk,
                        index >> (2 * Chunk.SizeShift),
                        (index >> Chunk.SizeShift) & Chunk.SizeMask,
                        index & Chunk.SizeMask);
                }
            }

            // Generated water never sits above its column's own level, so a generated chunk
            // entirely above every level it could share can neither hold water nor border any
            // generated water — the block edit paths cover water placed while it is loaded.
            // Saved water breaks that: a restored chunk can hold poured water anywhere, and a
            // generated chunk loading beside one must scan the shared face too, or water on that
            // border would never wake when the generated side streams in last.
            // Note this is not the same test as an empty frontier: a chunk can be solid water
            // inside and still face air across a border.
            var column = chunks.WorldSource.GetColumn((int)chunk.Position.X, (int)chunk.Position.Z);
            if (!chunk.IsRestored && !HasRestoredFaceNeighbor(chunk, chunks)
                && chunk.LowerBoundY > Math.Max(column.MaxWaterLevel, WorldGenerationConfig.SeaLevel))
                continue;

            WakeBorders(chunk, chunks);
        }
    }

    /// <summary>
    /// Schedules the fluid side of every fluid/wake-target pair across the faces this chunk
    /// shares with its loaded neighbors, in whichever chunk the fluid actually lives. The pair
    /// is judged by <see cref="FluidWakeRules.IsWakeTarget"/>, with the fluid-side values read from the column
    /// of the chunk that holds the fluid: a generated sea keeps the carved rule (only a neighbor
    /// at or below its column's terrain top counts), so it does not wake into open air above a
    /// ravine, beach or hillside, while a fluid above both its column's terrain top and water
    /// level (only ever poured, brought back by a restored chunk) wakes against any replaceable
    /// neighbor. Blocks are read straight off both chunks (they are loaded, so their arrays are
    /// there) rather than through <c>GetBlockGlobal</c>, which would call an unloaded chunk air.
    /// </summary>
    private void WakeBorders(Chunk chunk, ChunkManager chunks)
    {
        var pos = chunk.Position;
        var ownColumn = chunks.WorldSource.GetColumn((int)pos.X, (int)pos.Z);
        var ownHeights = ownColumn.Heights;
        var ownWater = ownColumn.WaterLevel;

        // Floored once for the six chunk-map probes below. Every one of them is a distinct key,
        // so ChunkMap's int overload packs straight from these, where its Vector3 overload would
        // floor and pack again per probe.
        var cx = MathUtils.FloorToInt(pos.X);
        var cy = MathUtils.FloorToInt(pos.Y);
        var cz = MathUtils.FloorToInt(pos.Z);

        foreach (var (dx, _, dz) in FluidNeighborhood.Horizontals)
        {
            if (!TryGetLoadedNeighbor(chunks, cx, cy, cz, dx, 0, dz, out var neighbor)) continue;

            // The two 16x16 planes that touch: this chunk's face cells sit at `near` on the
            // axis the face cuts, the neighbor's directly across at `far`
            var near = dx + dz > 0 ? Chunk.SizeMask : 0;
            var far = Chunk.SizeMask - near;
            var neighborColumn = chunks.WorldSource.GetColumn((int)neighbor.Position.X, (int)neighbor.Position.Z);
            var neighborHeights = neighborColumn.Heights;
            var neighborWater = neighborColumn.WaterLevel;

            for (var ly = 0; ly < Chunk.Size; ly++)
            {
                var worldY = chunk.LowerBoundY + ly;
                for (var t = 0; t < Chunk.Size; t++)
                {
                    // t sweeps the horizontal axis the face does not cut
                    var hx = dx != 0 ? near : t;
                    var hz = dx != 0 ? t : near;
                    var nx = dx != 0 ? far : t;
                    var nz = dx != 0 ? t : far;

                    var here = chunk.GetLocalBlock(hx, ly, hz);
                    var there = neighbor.GetLocalBlock(nx, ly, nz);
                    var hi = (hx << Chunk.SizeShift) | hz;
                    var ni = (nx << Chunk.SizeShift) | nz;

                    // The fluid side is judged against the column of the chunk that holds it
                    if (BlockType.IsFluid(here)
                        && FluidWakeRules.IsWakeTarget(there, worldY, neighborHeights[ni],
                            worldY, ownHeights[hi], ownWater[hi]))
                        _schedule.ScheduleLocal(chunk, hx, ly, hz);
                    else if (BlockType.IsFluid(there)
                        && FluidWakeRules.IsWakeTarget(here, worldY, ownHeights[hi],
                            worldY, neighborHeights[ni], neighborWater[ni]))
                        _schedule.ScheduleLocal(neighbor, nx, ly, nz);
                }
            }
        }

        // Water only ever falls through a vertical face, so each of these schedules one side:
        // the fluid sitting over the hole, never the air sitting over the fluid.
        if (TryGetLoadedNeighbor(chunks, cx, cy, cz, 0, -1, 0, out var below))
        {
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                // Same column for the hole below and the fluid over it
                var i = (lx << Chunk.SizeShift) | lz;
                if (BlockType.IsFluid(chunk.GetLocalBlock(lx, 0, lz))
                    && FluidWakeRules.IsWakeTarget(below.GetLocalBlock(lx, Chunk.SizeMask, lz),
                        chunk.LowerBoundY - 1, ownHeights[i],
                        chunk.LowerBoundY, ownHeights[i], ownWater[i]))
                    _schedule.ScheduleLocal(chunk, lx, 0, lz);
            }
        }

        if (TryGetLoadedNeighbor(chunks, cx, cy, cz, 0, 1, 0, out var above))
        {
            for (var lx = 0; lx < Chunk.Size; lx++)
            for (var lz = 0; lz < Chunk.Size; lz++)
            {
                // The chunk above shares this column, so its column data is ownColumn too
                var i = (lx << Chunk.SizeShift) | lz;
                if (BlockType.IsFluid(above.GetLocalBlock(lx, 0, lz))
                    && FluidWakeRules.IsWakeTarget(chunk.GetLocalBlock(lx, Chunk.SizeMask, lz),
                        chunk.LowerBoundY + Chunk.SizeMask, ownHeights[i],
                        chunk.LowerBoundY + Chunk.Size, ownHeights[i], ownWater[i]))
                    _schedule.ScheduleLocal(above, lx, 0, lz);
            }
        }
    }

    /// <summary>Whether any of the chunk's six loaded face neighbors was restored from a save.</summary>
    private static bool HasRestoredFaceNeighbor(Chunk chunk, ChunkManager chunks)
    {
        var cx = MathUtils.FloorToInt(chunk.Position.X);
        var cy = MathUtils.FloorToInt(chunk.Position.Y);
        var cz = MathUtils.FloorToInt(chunk.Position.Z);
        foreach (var d in Vector3Directions.CardinalOffsets)
        {
            if (TryGetLoadedNeighbor(chunks, cx, cy, cz, d.X, d.Y, d.Z, out var neighbor)
                && neighbor.IsRestored)
                return true;
        }

        return false;
    }

    /// <summary>The face neighbor at the offset, when it exists and its blocks are generated.</summary>
    private static bool TryGetLoadedNeighbor(ChunkManager chunks, int cx, int cy, int cz,
        int dx, int dy, int dz, out Chunk neighbor)
    {
        return chunks.Chunks.TryGetValue(cx + dx, cy + dy, cz + dz, out neighbor)
               && neighbor.IsBlockDataReady;
    }
}
