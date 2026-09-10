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

    private readonly List<PrimedTnt> _primed = [];
    // Spawned while iterating _primed (chained TNT); drained after the tick
    private readonly List<(Vector3 Position, byte BlockId)> _pendingSpawns = [];

    public IReadOnlyList<PrimedTnt> Primed => _primed;

    /// <summary>
    /// Primes a cube at the centre of the cell the block occupied. The cube carries
    /// <paramref name="blockId"/> so it renders as the block that primed.
    /// </summary>
    public void Spawn(Vector3 blockPosition, byte blockId)
    {
        _primed.Add(new PrimedTnt(blockPosition + new Vector3(0.5f), blockId));
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
        FluidManager fluids, FallingBlockManager falling, LeafParticleSystem? leaves = null)
    {
        foreach (var tnt in _primed)
            tnt.Update(deltaTime, chunks);

        // Detonate after every fuse has ticked so a chained spawn never ticks twice in one frame
        for (var i = _primed.Count - 1; i >= 0; i--)
        {
            if (!_primed[i].IsDue) continue;
            var at = _primed[i].Position;
            _primed.RemoveAt(i);
            Detonate(at, chunks, lod, drops, fluids, falling, leaves);
        }

        foreach (var (position, blockId) in _pendingSpawns)
            _primed.Add(new PrimedTnt(position, blockId));
        _pendingSpawns.Clear();
    }

    /// <summary>
    /// Clears a sphere of blocks around <paramref name="center"/> (TNT blocks are primed instead
    /// of destroyed, so blasts chain; everything else drops like a mined block), remeshing each
    /// touched chunk once, then lowers the buried LOD under every blasted column. Fluid cells
    /// are left standing — clearing them would only be undone by the hundreds of sources around
    /// the crater — and the water around the blast is scheduled so it floods the hole instead.
    /// </summary>
    private void Detonate(Vector3 center, ChunkManager chunks, LodManager lod, DropManager drops,
        FluidManager fluids, FallingBlockManager falling, LeafParticleSystem? leaves = null)
    {
        var radius = BlastRadius;
        var radiusSq = radius * radius;
        var touchedChunks = new HashSet<Vector3>();
        // Lowest destroyed y per (x, z), so each blasted column lowers the LOD cap once
        var blastedColumns = new Dictionary<(int X, int Z), int>();
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

            var chunkPos = new Vector3(
                MathF.Floor(x / (float)Chunk.Size),
                MathF.Floor(y / (float)Chunk.Size),
                MathF.Floor(z / (float)Chunk.Size));
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
                TreeGenerator.PoisonCanopy(x, z);

            if (leaves != null && removed == TreeGenerator.LeavesBlock)
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
                    DropManager.PileCount(removed, x, z, removedState));
            }

            // The cell is air now either way, so the buried LOD must duck below it
            if (!blastedColumns.TryGetValue((x, z), out var lowest) || y < lowest)
            {
                blastedColumns[(x, z)] = y;
            }
        }

        // Pebbles and their kind need support: pop survivors left floating where the blast
        // took the ground from under them. The scan runs after all clearing, so a block found
        // over air genuinely lost its support (or was already floating).
        foreach (var ((x, z), lowestY) in blastedColumns)
        {
            for (var y = lowestY; y <= (int)max.Y + 1; y++)
            {
                var standing = chunks.GetBlockGlobal(x, y, z);
                if (BlockType.ById[standing]?.NeedsSupport != true) continue;

                // Support in an unloaded chunk reads as air: leave those alone.
                var belowChunkPos = new Vector3(
                    MathF.Floor(x / (float)Chunk.Size),
                    MathF.Floor((y - 1) / (float)Chunk.Size),
                    MathF.Floor(z / (float)Chunk.Size));
                if (!chunks.Chunks.TryGetValue(belowChunkPos, out _)) continue;
                if (chunks.GetBlockGlobal(x, y - 1, z) != 0) continue;

                var pebbleChunkPos = new Vector3(
                    MathF.Floor(x / (float)Chunk.Size),
                    MathF.Floor(y / (float)Chunk.Size),
                    MathF.Floor(z / (float)Chunk.Size));
                if (!chunks.Chunks.TryGetValue(pebbleChunkPos, out var pebbleChunk)) continue;
                var standingState = chunks.GetStateGlobal(x, y, z);
                if (pebbleChunk.ClearBlockAt(new Vector3(x, y, z)) == 0) continue;
                touchedChunks.Add(pebbleChunkPos);

                drops.SpawnLoot(standing, new Vector3(x, y, z),
                    DropManager.PileCount(standing, x, z, standingState));
            }
        }

        foreach (var chunkPos in touchedChunks)
        {
            if (chunks.Chunks.TryGetValue(chunkPos, out var chunk))
                chunk.RemeshAfterBatchEdit(chunks);
        }

        foreach (var ((x, z), lowestY) in blastedColumns)
        {
            if (LodRegion.LowerColumnBelow(x, lowestY, z))
                lod.MarkEditedColumn(x, z);
        }

        // Sand above the crater loses its support with it: sweep bottom-up over the blast box
        // plus one layer, so stacked columns come down in order
        for (var x = (int)min.X; x <= (int)max.X; x++)
        for (var z = (int)min.Z; z <= (int)max.Z; z++)
            falling.CheckColumn(chunks, lod, fluids, x, z, (int)min.Y, (int)max.Y + 1);

        // One box for the whole blast, grown by a block so the water just outside it notices
        fluids.ScheduleBox(min - Vector3.One, max + Vector3.One);

        if (blastedLeaves != null)
            leaves?.BurstCells(blastedLeaves, WorldGenerationConfig.LeafParticleBurstBlast);
    }
}
