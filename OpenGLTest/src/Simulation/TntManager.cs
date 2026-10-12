using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Entities;
using VoxelEngine.Interaction;
using VoxelEngine.Juice;
using VoxelEngine.Lod;
using VoxelEngine.Particles;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Simulation;

/// <summary>
/// Owns the live primed TNT: spawns cubes where firestarter strikes and blasts light TNT,
/// ticks their fuses, and detonates them. A blast clears a sphere of blocks (dropping nothing) and primes any dry TNT
/// caught in it, so blasts chain; submerged TNT just clears. The player itself is never harmed, only
/// shoved: everything loose in range is flung away from the center (<see cref="BlastImpulse"/>).
/// Main thread only.
/// </summary>
public class TntManager
{
    /// <summary>Blast radius in blocks, measured from the primed cube's center.</summary>
    public const float BlastRadius = 4f;

    /// <summary>Fuse for a cube lit by hand (firestarter): a second, long enough to step back from,
    /// short enough that a lit cube does not loiter. <see cref="PrimedTnt.WhiteFlash"/> ramps its
    /// strobe over whatever the fuse is, so it still reads at this length.</summary>
    public const float PlayerFuseSeconds = 1f;

    /// <summary>Fuse for a cube lit by another blast, so chains go off promptly.</summary>
    public const float ChainFuseSeconds = 0.5f;

    private readonly List<PrimedTnt> _primed = [];
    // Spawned while iterating _primed (chained TNT), each with the shove of the blast that lit it;
    // drained after the tick
    private readonly List<(Vector3 Position, byte BlockId, Vector3 Impulse)> _pendingSpawns = [];
    // Centers of the blasts of the last Update, for the world's camera shake
    private readonly List<Vector3> _blasts = [];

    public IReadOnlyList<PrimedTnt> Primed => _primed;

    /// <summary>The center of every blast the last <see cref="Update"/> set off, in detonation order;
    /// valid until the next one. The world shakes the local camera by them (<see cref="BlastShake"/>).</summary>
    public IReadOnlyList<Vector3> Blasts => _blasts;

    /// <summary>
    /// Primes a cube at the center of the cell the block occupied. The cube carries
    /// <paramref name="blockId"/> so it renders as the block that primed, and burns for
    /// <paramref name="fuseSeconds"/> (<see cref="PlayerFuseSeconds"/> when lit by hand,
    /// <see cref="ChainFuseSeconds"/> when lit by a blast).
    /// </summary>
    public void Spawn(Vector3 blockPosition, byte blockId, float fuseSeconds)
    {
        _primed.Add(new PrimedTnt(blockPosition + new Vector3(0.5f), blockId, fuseSeconds));
    }

    /// <summary>
    /// A multiplayer client replaces its cubes with the server's (see <see cref="PrimedTnt.Mirror"/>).
    /// Only a client calls this, and it never calls <see cref="Update"/>, so a mirror never falls
    /// or detonates here.
    /// </summary>
    internal void ReplaceMirrors(IEnumerable<PrimedTnt> mirrors)
    {
        _primed.Clear();
        _primed.AddRange(mirrors);
    }

    /// <summary>Burns every mirror's fuse down between server snapshots, so the flash keeps its
    /// pace; no physics and no detonation.</summary>
    internal void AnimateMirrors(float deltaTime)
    {
        foreach (var tnt in _primed)
            tnt.AdvanceFuse(deltaTime);
    }

    /// <summary>
    /// Whether the cell touches fluid on any face, in which case its fuse is wet and it
    /// clears instead of priming. Unloaded neighbors read as absent, never as contact.
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
        _blasts.Clear();
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

        foreach (var (position, blockId, impulse) in _pendingSpawns)
        {
            var chained = new PrimedTnt(position, blockId, ChainFuseSeconds);
            chained.Push(impulse);
            _primed.Add(chained);
        }
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
        _blasts.Add(center);
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
                // Deferred: the live list is being iterated further up the call stack. The cube is
                // flung like everything else the blast reaches
                _pendingSpawns.Add((cellCenter, removed, BlastImpulse.At(center, cellCenter, radius)));
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

        // Pebbles, torches and their kind need support: pop survivors left hanging where the
        // blast took their holder away - the ground under a floor block, the wall behind a wall
        // torch (see BlockSupport). The blast box grown by one holds every cell a blasted cell
        // could have held up, and the scan runs after all clearing, bottom-up, so a block found
        // against air genuinely lost its support (or was already floating).
        if (blastedColumns.Count > 0)
        {
            for (var x = (int)min.X - 1; x <= (int)max.X + 1; x++)
            for (var z = (int)min.Z - 1; z <= (int)max.Z + 1; z++)
            for (var y = (int)min.Y - 1; y <= (int)max.Y + 1; y++)
            {
                var standing = chunks.GetBlockGlobal(x, y, z);
                if (standing == 0) continue;
                var standingState = chunks.GetStateGlobal(x, y, z);
                if (!BlockSupport.TryGetSupportCell(standing, standingState, new Vector3(x, y, z), out var support))
                    continue;

                // Support in an unloaded chunk reads as air: leave those alone.
                int sx = (int)support.X, sy = (int)support.Y, sz = (int)support.Z;
                if (!chunks.Chunks.TryGetValue(VoxelCoords.WorldToChunk(sx), VoxelCoords.WorldToChunk(sy),
                        VoxelCoords.WorldToChunk(sz), out _)) continue;
                if (chunks.GetBlockGlobal(sx, sy, sz) != 0) continue;

                var pebbleChunkPos = VoxelCoords.ChunkPosOf(x, y, z);
                if (!chunks.Chunks.TryGetValue(pebbleChunkPos, out var pebbleChunk)) continue;
                if (pebbleChunk.ClearBlockAt(new Vector3(x, y, z)) == 0) continue;
                chunks.UnmarkPlayerPlaced(x, y, z);
                touchedChunks.Add(pebbleChunkPos);

                drops.SpawnLoot(standing, new Vector3(x, y, z),
                    DropManager.PileCount(standing, x, z, standingState),
                    DropManager.PileSecondary(standing, x, z, standingState),
                    standingState);
            }
        }

        // Everything loose in range is flung away from the center: the other primed TNT, the drops
        // (this blast's own loot included, so it flies out of the crater) and the falling cubes
        // already in the air. Before the sweep below, so sand this blast undermines falls straight
        // and rebuilds instead of scattering. The world shoves the players and mobs (Blasts).
        foreach (var other in _primed)
            other.Push(BlastImpulse.At(center, other.Position, radius));
        drops.ApplyBlastImpulse(center, radius);
        foreach (var block in falling.Falling)
            block.Push(BlastImpulse.At(center, block.Position, radius));

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
