using System;

namespace silkgltriangle;

/// <summary>
/// The live-world tree space: whether reactive growth may write into a cell of the loaded world,
/// read only through <see cref="ChunkManager.ChunkAt"/> and the chunk's local block and state
/// (never <see cref="ChunkManager.GetBlockGlobal"/>, which reports unloaded space as air).
/// <para>A cell is free when it is air or a non-persistent leaf of this species (growth may
/// write only there; every other block is an obstacle), is not <c>reserved</c> (a cell a live
/// falling block owns), and is not the missing parent of an orphan: no non-detached face
/// neighbour of the species' wood names it as its parent cell, because filling that cell would
/// re-attach a cut branch. An unloaded cell is never free, and a cell beside one is refused too,
/// since the orphan rule cannot be checked there.</para>
/// <para>A space lives for one main-thread growth call, during which no chunk unloads, so it
/// caches the last chunk it read through. It is not thread-safe.</para>
/// </summary>
public sealed class WorldTreeSpace : ITreeSpace
{
    private readonly ChunkManager _chunks;
    private readonly TreeSpecies _species;
    private readonly Func<int, int, int, bool>? _reserved;

    // Single-slot chunk cache: neighbouring probes almost always share a chunk.
    private Chunk? _cached;
    private int _cachedCx, _cachedCy, _cachedCz;

    /// <param name="chunks">The live chunks the tree grows in.</param>
    /// <param name="species">The growing species: its leaves are free, its wood decides the orphan rule.</param>
    /// <param name="reserved">(x, y, z) -> whether the cell is held by something growth must not
    /// fill (World passes <c>FallingBlockManager.OverlapsAny</c>); null means nothing is.</param>
    public WorldTreeSpace(ChunkManager chunks, TreeSpecies species, Func<int, int, int, bool>? reserved = null)
    {
        _chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        _species = species ?? throw new ArgumentNullException(nameof(species));
        _reserved = reserved;
    }

    /// <inheritdoc />
    public bool IsFree(int x, int y, int z) => Check(x, y, z, false);

    /// <summary>
    /// Whether a sapling's root may go into the cell: everything <see cref="IsFree"/> accepts, plus
    /// a non-solid placement-replaceable cover such as a partial snow layer, which the root takes
    /// over (see <see cref="BlockType.IsReplaceableByPlacement"/>).
    /// </summary>
    public bool IsPlantable(int x, int y, int z) => Check(x, y, z, true);

    private bool Check(int x, int y, int z, bool allowCover)
    {
        if (!TryGetCell(x, y, z, out var id, out var state))
            return false;
        if (_reserved?.Invoke(x, y, z) == true)
            return false;

        var writable = id == 0
                       || (_species.LeavesId != 0 && id == _species.LeavesId && !LeafState.IsPersistent(state))
                       || (allowCover && BlockType.IsReplaceableByPlacement(id, state));
        if (!writable)
            return false;

        for (var dir = 0; dir < 6; dir++)
        {
            int nx = x + TreeDirections.Dx[dir], ny = y + TreeDirections.Dy[dir], nz = z + TreeDirections.Dz[dir];
            if (!TryGetCell(nx, ny, nz, out var nid, out var nstate))
                return false;
            if (nid != _species.WoodId || WoodState.IsDetached(nstate))
                continue;
            // A wood cell whose parent is this one: the cell is a cut limb's missing parent
            if (WoodState.TryParentOffset(nstate, out var px, out var py, out var pz)
                && nx + px == x && ny + py == y && nz + pz == z)
                return false;
        }

        return true;
    }

    private bool TryGetCell(int x, int y, int z, out byte id, out byte state)
    {
        int cx = x >> Chunk.SizeShift, cy = y >> Chunk.SizeShift, cz = z >> Chunk.SizeShift;
        var chunk = _cached;
        if (chunk == null || cx != _cachedCx || cy != _cachedCy || cz != _cachedCz)
        {
            chunk = _chunks.ChunkAt(x, y, z);
            if (chunk == null)
            {
                id = 0;
                state = 0;
                return false;
            }

            _cached = chunk;
            _cachedCx = cx;
            _cachedCy = cy;
            _cachedCz = cz;
        }

        int lx = x & Chunk.SizeMask, ly = y & Chunk.SizeMask, lz = z & Chunk.SizeMask;
        id = chunk.GetLocalBlock(lx, ly, lz);
        state = chunk.GetLocalState(lx, ly, lz);
        return true;
    }
}
