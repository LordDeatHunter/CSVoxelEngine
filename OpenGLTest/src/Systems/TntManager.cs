using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns the live primed TNT: spawns cubes where firestarter strikes and blasts light TNT,
/// ticks their fuses, and detonates them. A blast clears a sphere of blocks (dropping nothing) and primes any dry TNT
/// caught in it, so blasts chain; submerged TNT just clears. The player itself is never harmed.
/// Main thread only.
/// </summary>
public class TntManager
{
    /// <summary>Blast radius in blocks, measured from the primed cube's centre.</summary>
    public const float BlastRadius = 4f;

    /// <summary>Fuse for a cube lit by hand (firestarter).</summary>
    public const float PlayerFuseSeconds = 2f;

    /// <summary>Fuse for a cube lit by another blast, so chains go off promptly.</summary>
    public const float ChainFuseSeconds = 0.5f;

    private readonly List<PrimedTnt> _primed = [];
    // Spawned while iterating _primed (chained TNT); drained after the tick
    private readonly List<(Vector3 Position, byte BlockId)> _pendingSpawns = [];

    public IReadOnlyList<PrimedTnt> Primed => _primed;

    /// <summary>
    /// Primes a cube at the centre of the cell the block occupied. The cube carries
    /// <paramref name="blockId"/> so it renders as the block that primed, and burns for
    /// <paramref name="fuseSeconds"/> (<see cref="PlayerFuseSeconds"/> when lit by hand,
    /// <see cref="ChainFuseSeconds"/> when lit by a blast).
    /// </summary>
    public void Spawn(Vector3 blockPosition, byte blockId, float fuseSeconds)
    {
        _primed.Add(new PrimedTnt(blockPosition + new Vector3(0.5f), blockId, fuseSeconds));
    }

    /// <summary>
    /// Whether the cell touches fluid on any face, in which case its fuse is wet and it
    /// clears instead of priming. Unloaded neighbours read as absent, never as contact.
    /// </summary>
    public static bool IsSubmerged(ChunkManager chunks, int x, int y, int z)
    {
        return (chunks.TryGetCell(x + 1, y, z, out var id, out _) && BlockType.IsFluid(id))
            || (chunks.TryGetCell(x - 1, y, z, out id, out _) && BlockType.IsFluid(id))
            || (chunks.TryGetCell(x, y + 1, z, out id, out _) && BlockType.IsFluid(id))
            || (chunks.TryGetCell(x, y - 1, z, out id, out _) && BlockType.IsFluid(id))
            || (chunks.TryGetCell(x, y, z + 1, out id, out _) && BlockType.IsFluid(id))
            || (chunks.TryGetCell(x, y, z - 1, out id, out _) && BlockType.IsFluid(id));
    }

    public void Update(float deltaTime, ChunkManager chunks, LodManager lod, DropManager drops,
        FluidManager fluids, FallingBlockManager falling, LeafParticleSystem? leaves = null,
        LeafDecayManager? leafDecay = null)
    {
        foreach (var tnt in _primed)
            tnt.Update(deltaTime, chunks);

        // Detonate after every fuse has ticked so a chained spawn never ticks twice in one frame
        for (var i = _primed.Count - 1; i >= 0; i--)
        {
            if (!_primed[i].IsDue) continue;
            var at = _primed[i].Position;
            _primed.RemoveAt(i);
            Detonate(at, chunks, lod, drops, fluids, falling, leaves, leafDecay);
        }

        foreach (var (position, blockId) in _pendingSpawns)
            _primed.Add(new PrimedTnt(position, blockId, ChainFuseSeconds));
        _pendingSpawns.Clear();
    }

    /// <summary>
    /// Clears a sphere of blocks around <paramref name="center"/> (TNT blocks are primed instead
    /// of destroyed, so blasts chain; everything else drops like a mined block), remeshing each
    /// touched chunk once, then moves the far LOD of every blasted column onto the crater. Fluid cells
    /// are left standing — clearing them would only be undone by the hundreds of sources around
    /// the crater — and the water around the blast is scheduled so it floods the hole instead.
    /// </summary>
    private void Detonate(Vector3 center, ChunkManager chunks, LodManager lod, DropManager drops,
        FluidManager fluids, FallingBlockManager falling, LeafParticleSystem? leaves = null,
        LeafDecayManager? leafDecay = null)
    {
        var radius = BlastRadius;
        var radiusSq = radius * radius;
        var touchedChunks = new HashSet<Vector3>();
        // Per blasted (x, z): the lowest destroyed y and the highest destroyed surface-solid y
        // (int.MinValue while none), so each column updates the far LOD once
        var blastedColumns = new Dictionary<(int X, int Z), (int Lowest, int HighestSolid)>();
        List<Vector3>? blastedLeaves = null;

        var min = (center - new Vector3(radius)).Floor();
        var max = (center + new Vector3(radius)).Floor();

        for (var x = (int)min.X; x <= (int)max.X; x++)
        for (var y = (int)min.Y; y <= (int)max.Y; y++)
        for (var z = (int)min.Z; z <= (int)max.Z; z++)
        {
            var cellCenter = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            if (Vector3.DistanceSquared(cellCenter, center) > radiusSq) continue;
            var existing = chunks.GetBlockGlobal(x, y, z);
            if (existing == 0) continue;

            // A blast cannot clear water: it would refill within a tick or two anyway, and a
            // dry crater under the sea would flood from every side at once
            if (BlockType.IsFluid(existing)) continue;

            var chunkPos = VoxelCoords.ChunkPosOf(x, y, z);
            if (!chunks.Chunks.TryGetValue(chunkPos, out var chunk)) continue;

            // The blast clears the state byte with the block, so a player-built pile's
            // count is read first or its loot falls back to the worldgen hash.
            var removedState = chunks.GetStateGlobal(x, y, z);
            var removed = chunk.ClearBlockAt(new Vector3(x, y, z));
            if (removed == 0) continue;

            // A blasted player build stops counting as placed with the block gone
            chunks.UnmarkPlayerPlaced(x, y, z);
            touchedChunks.Add(chunkPos);

            // A blasted canopy must not linger in the far LOD (its rebuild is already
            // triggered per blasted column below, which then reads the poison)
            if (TreeGenerator.IsTreeBlock(removed))
                TreeGenerator.PoisonCanopy(x, z, TreeGenerator.SingleEditPoisonRadius);

            if (leaves != null && TreeGenerator.IsLeaf(removed))
            {
                blastedLeaves ??= new List<Vector3>();
                blastedLeaves.Add(new Vector3(x, y, z));
            }

            if (BlockType.ById[removed]?.OnBreak == BlockBreakAction.PrimeTnt
                && !IsSubmerged(chunks, x, y, z))
            {
                // Deferred: the live list is being iterated further up the call stack
                _pendingSpawns.Add((new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), removed));
            }
            else
            {
                // Same loot a mined block would leave (stone drops cobblestone)
                drops.SpawnLoot(removed, new Vector3(x, y, z),
                    DropManager.PileCount(removed, x, z, removedState),
                    DropManager.PileSecondary(removed, x, z, removedState),
                    removedState);
            }

            // The cell is air now either way, so the far LOD must follow it down
            var solidY = LodSurface.IsSurfaceSolid(removed) ? y : int.MinValue;
            blastedColumns[(x, z)] = blastedColumns.TryGetValue((x, z), out var span)
                ? (Math.Min(span.Lowest, y), Math.Max(span.HighestSolid, solidY))
                : (y, solidY);
        }

        // Pebbles and their kind need support: pop survivors left floating where the blast
        // took the ground from under them. The scan runs after all clearing, so a block found
        // over air genuinely lost its support (or was already floating).
        foreach (var ((x, z), (lowestY, _)) in blastedColumns)
        {
            for (var y = lowestY; y <= (int)max.Y + 1; y++)
            {
                var standing = chunks.GetBlockGlobal(x, y, z);
                if (BlockType.ById[standing]?.NeedsSupport != true) continue;

                // Support in an unloaded chunk reads as air: leave those alone.
                if (!chunks.Chunks.TryGetValue(VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(y - 1),
                        VoxelCoords.WorldToChunk(z), out _)) continue;
                if (chunks.GetBlockGlobal(x, y - 1, z) != 0) continue;

                var pebbleChunkPos = VoxelCoords.ChunkPosOf(x, y, z);
                if (!chunks.Chunks.TryGetValue(pebbleChunkPos, out var pebbleChunk)) continue;
                var standingState = chunks.GetStateGlobal(x, y, z);
                if (pebbleChunk.ClearBlockAt(new Vector3(x, y, z)) == 0) continue;
                touchedChunks.Add(pebbleChunkPos);

                drops.SpawnLoot(standing, new Vector3(x, y, z),
                    DropManager.PileCount(standing, x, z, standingState),
                    DropManager.PileSecondary(standing, x, z, standingState),
                    standingState);
            }
        }

        foreach (var chunkPos in touchedChunks)
        {
            if (chunks.Chunks.TryGetValue(chunkPos, out var chunk))
                chunk.RemeshAfterBatchEdit(chunks);
        }

        // The highest blasted surface-solid cell first: when it was the column's top, its removal
        // rescans down through the crater to the new floor (a snow layer or pebble blown off above
        // it is no surface, so it is not the cell to start from). The lowest blasted cell then caps
        // a crater that sat below the surface (a buried blast), so the hole never exposes the
        // shell; after a rescan it sits above the new top and records nothing.
        foreach (var ((x, z), (lowestY, highestSolidY)) in blastedColumns)
        {
            var changed = highestSolidY != int.MinValue && LodSurface.RecordEdit(chunks, x, highestSolidY, z);
            if (lowestY != highestSolidY)
                changed |= LodSurface.RecordEdit(chunks, x, lowestY, z);
            if (changed)
                lod.MarkEditedColumn(x, z);
        }

        // Sand above the crater loses its support with it: sweep bottom-up over the blast box
        // plus one layer, so stacked columns come down in order
        for (var x = (int)min.X; x <= (int)max.X; x++)
        for (var z = (int)min.Z; z <= (int)max.Z; z++)
            falling.CheckColumn(chunks, lod, fluids, x, z, (int)min.Y, (int)max.Y + 1);

        // One box for the whole blast, grown by a block so the water just outside it notices
        fluids.ScheduleBox(min - Vector3.One, max + Vector3.One);

        // The leaves of every blasted tip, and the wood orphaned by a blasted trunk: the manager grows
        // the box by its own reach
        leafDecay?.ScheduleBox(min, max);

        if (blastedLeaves != null)
            leaves?.BurstCells(blastedLeaves, WorldGenerationConfig.LeafParticleBurstBlast);
    }
}
