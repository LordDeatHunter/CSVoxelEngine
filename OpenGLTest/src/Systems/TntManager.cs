using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns the live primed TNT: spawns cubes where TNT blocks finish mining, ticks their fuses,
/// and detonates them. A blast clears a sphere of blocks (dropping nothing) and primes any TNT
/// caught in it, so blasts chain. The player itself is never harmed. Main thread only.
/// </summary>
public class TntManager
{
    /// <summary>Blast radius in blocks, measured from the primed cube's centre.</summary>
    public const float BlastRadius = 4f;

    private readonly List<PrimedTnt> _primed = [];
    // Spawned while iterating _primed (chained TNT); drained after the tick
    private readonly List<Vector3> _pendingSpawns = [];

    public IReadOnlyList<PrimedTnt> Primed => _primed;

    /// <summary>Primes a cube at the centre of the cell the block occupied.</summary>
    public void Spawn(Vector3 blockPosition)
    {
        _primed.Add(new PrimedTnt(blockPosition + new Vector3(0.5f)));
    }

    public void Update(float deltaTime, ChunkManager chunks, LodManager lod, DropManager drops)
    {
        foreach (var tnt in _primed)
            tnt.Update(deltaTime, chunks);

        // Detonate after every fuse has ticked so a chained spawn never ticks twice in one frame
        for (var i = _primed.Count - 1; i >= 0; i--)
        {
            if (!_primed[i].IsDue) continue;
            var at = _primed[i].Position;
            _primed.RemoveAt(i);
            Detonate(at, chunks, lod, drops);
        }

        foreach (var spawn in _pendingSpawns)
            _primed.Add(new PrimedTnt(spawn));
        _pendingSpawns.Clear();
    }

    /// <summary>
    /// Clears a sphere of blocks around <paramref name="center"/> (TNT blocks are primed instead
    /// of destroyed, so blasts chain; everything else drops like a mined block), remeshing each
    /// touched chunk once, then lowers the buried LOD under every blasted column.
    /// </summary>
    private void Detonate(Vector3 center, ChunkManager chunks, LodManager lod, DropManager drops)
    {
        var radius = BlastRadius;
        var radiusSq = radius * radius;
        var touchedChunks = new HashSet<Vector3>();
        // Lowest destroyed y per (x, z), so each blasted column lowers the LOD cap once
        var blastedColumns = new Dictionary<(int X, int Z), int>();

        var min = (center - new Vector3(radius)).Floor();
        var max = (center + new Vector3(radius)).Floor();

        for (var x = (int)min.X; x <= (int)max.X; x++)
        for (var y = (int)min.Y; y <= (int)max.Y; y++)
        for (var z = (int)min.Z; z <= (int)max.Z; z++)
        {
            var cellCenter = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            if (Vector3.DistanceSquared(cellCenter, center) > radiusSq) continue;
            if (chunks.GetBlockGlobal(x, y, z) == 0) continue;

            var chunkPos = new Vector3(
                MathF.Floor(x / (float)Chunk.Size),
                MathF.Floor(y / (float)Chunk.Size),
                MathF.Floor(z / (float)Chunk.Size));
            if (!chunks.Chunks.TryGetValue(chunkPos, out var chunk)) continue;

            var removed = chunk.ClearBlockAt(new Vector3(x, y, z));
            if (removed == 0) continue;

            // A blasted player build stops counting as placed with the block gone
            chunks.UnmarkPlayerPlaced(x, y, z);
            touchedChunks.Add(chunkPos);

            // A blasted canopy must not linger in the far LOD (its rebuild is already
            // triggered per blasted column below, which then reads the poison)
            if (TreeGenerator.WoodLevel(removed) >= 0 || removed == BlockType.Leaves)
                TreeGenerator.PoisonCanopy(x, z);

            if (removed == BlockType.Tnt)
            {
                // Deferred: the live list is being iterated further up the call stack
                _pendingSpawns.Add(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
            }
            else if (removed == BlockType.Leaves)
            {
                // Same odds as a mined leaves block (see DropManager.SpawnLeafDrops)
                drops.SpawnLeafDrops(new Vector3(x, y, z));
            }
            else
            {
                // Same drop a mined block would leave (stone drops cobblestone)
                drops.Spawn(BlockType.DropFor(removed), new Vector3(x, y, z));
            }

            // The cell is air now either way, so the buried LOD must duck below it
            if (!blastedColumns.TryGetValue((x, z), out var lowest) || y < lowest)
            {
                blastedColumns[(x, z)] = y;
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
    }
}
