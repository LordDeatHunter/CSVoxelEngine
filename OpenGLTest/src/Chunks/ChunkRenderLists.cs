using System.Collections.Generic;

namespace VoxelEngine.Chunks;

/// <summary>The loaded chunks that have something to draw, kept in step with their meshes by swap-remove, and <see cref="MeshVersion"/>, which moves whenever the loaded, meshed set changes. Owned by <see cref="ChunkManager"/>, which forwards the four public members; main thread only.</summary>
internal sealed class ChunkRenderLists
{
    /// <summary>
    /// Every loaded chunk that currently has an opaque or a translucent mesh. The render
    /// passes walk this flat list instead of <see cref="ChunkManager.Chunks"/>: enumerating the dictionary
    /// by <c>Values</c> takes a locked full-table copy and then hands the passes every loaded
    /// chunk, the large majority of which have nothing to draw. Order is irrelevant — each
    /// pass sorts what it draws — so entries are swap-removed.
    /// Main thread only: it is maintained by <see cref="ChunkManager.Update(System.Numerics.Vector3[])"/> and
    /// <see cref="ChunkManager.ImmediateRemesh"/>, the only places a chunk's mesh appears or disappears.
    /// </summary>
    public readonly List<Chunk> Renderable = new(4096);

    /// <summary>
    /// The <see cref="Renderable"/> subset with a translucent mesh (water, glass), so
    /// <c>CullWater</c> walks hundreds of candidates instead of every meshed chunk.
    /// Same swap-remove contract (<see cref="Chunk.TranslucentRenderIndex"/>), same thread.
    /// </summary>
    public readonly List<Chunk> TranslucentRenderable = new(512);

    /// <summary>
    /// Every loaded chunk whose mesh recorded a chest cell (<see cref="Chunk.ChestCells"/>), which the chest pass
    /// walks. Kept apart from <see cref="Renderable"/> because a chest draws no chunk geometry: a chunk holding
    /// nothing but a chest has no mesh and never enters that list. Same swap-remove contract
    /// (<see cref="Chunk.ChestRenderIndex"/>), same thread.
    /// </summary>
    public readonly List<Chunk> ChestChunks = new(64);

    /// <summary>
    /// Bumped whenever the set of loaded, meshed chunks changes: a chunk enters or leaves
    /// <see cref="Renderable"/>, a chunk is meshed for the first time (even with an empty mesh,
    /// which never enters <see cref="Renderable"/>), or a chunk is unloaded. A consumer that
    /// derives something from which chunks are loaded and drawn (the LOD buried flags) compares
    /// it to skip the recompute when nothing moved. Main thread only.
    /// </summary>
    public int MeshVersion { get; set; }

    /// <summary>
    /// Adds <paramref name="chunk"/> to <see cref="Renderable"/> or drops it, to match whether
    /// it currently has anything to draw — a rebuild can also empty a chunk that had a mesh
    /// (the last block mined out of it). Main thread only.
    /// </summary>
    internal void SyncRenderable(Chunk chunk)
    {
        SyncChests(chunk);
        var wants = chunk.HasMesh || chunk.HasTranslucentMesh;

        if (!wants)
        {
            RemoveRenderable(chunk);
            return;
        }

        if (chunk.RenderIndex < 0)
        {
            chunk.RenderIndex = Renderable.Count;
            Renderable.Add(chunk);
            MeshVersion++;
        }

        SyncTranslucent(chunk);
    }

    /// <summary>
    /// Keeps <see cref="TranslucentRenderable"/> matching the chunk's translucent mesh.
    /// No <see cref="MeshVersion"/> bump: its only consumer is the LOD cover gate, which
    /// reads <c>IsMeshed</c>, never slices.
    /// </summary>
    private void SyncTranslucent(Chunk chunk)
    {
        if (chunk.HasTranslucentMesh)
        {
            if (chunk.TranslucentRenderIndex >= 0) return;
            chunk.TranslucentRenderIndex = TranslucentRenderable.Count;
            TranslucentRenderable.Add(chunk);
            return;
        }

        RemoveTranslucent(chunk);
    }

    // Keeps ChestChunks matching whether the chunk's last mesh recorded a chest cell
    private void SyncChests(Chunk chunk)
    {
        if (chunk.ChestCells == null)
        {
            RemoveChests(chunk);
            return;
        }

        if (chunk.ChestRenderIndex >= 0) return;
        chunk.ChestRenderIndex = ChestChunks.Count;
        ChestChunks.Add(chunk);
    }

    private void RemoveChests(Chunk chunk)
    {
        var index = chunk.ChestRenderIndex;
        if (index < 0) return;

        var last = ChestChunks.Count - 1;
        if (index != last)
        {
            var moved = ChestChunks[last];
            ChestChunks[index] = moved;
            moved.ChestRenderIndex = index;
        }

        ChestChunks.RemoveAt(last);
        chunk.ChestRenderIndex = -1;
    }

    private void RemoveTranslucent(Chunk chunk)
    {
        var index = chunk.TranslucentRenderIndex;
        if (index < 0) return;

        var last = TranslucentRenderable.Count - 1;
        if (index != last)
        {
            var moved = TranslucentRenderable[last];
            TranslucentRenderable[index] = moved;
            moved.TranslucentRenderIndex = index;
        }

        TranslucentRenderable.RemoveAt(last);
        chunk.TranslucentRenderIndex = -1;
    }

    /// <summary>
    /// Swap-removes a chunk from <see cref="Renderable"/>; a no-op for a chunk that is not in
    /// the list. Must run before <see cref="Chunk.Dispose"/>, which nulls the slices this
    /// bookkeeping is keyed on. Main thread only.
    /// </summary>
    internal void RemoveRenderable(Chunk chunk)
    {
        // Unload calls this while slices are still set (Dispose nulls them after), so the
        // translucent membership is dropped here too rather than trusting the caller.
        RemoveTranslucent(chunk);
        RemoveChests(chunk);

        var index = chunk.RenderIndex;
        if (index < 0) return;

        var last = Renderable.Count - 1;
        if (index != last)
        {
            var moved = Renderable[last];
            Renderable[index] = moved;
            moved.RenderIndex = index;
        }

        Renderable.RemoveAt(last);
        chunk.RenderIndex = -1;
        MeshVersion++;
    }
}
