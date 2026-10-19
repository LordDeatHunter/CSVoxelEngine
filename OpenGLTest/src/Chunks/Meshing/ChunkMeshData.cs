using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Data;

namespace VoxelEngine.Chunks.Meshing;

/// <summary>
/// CPU-side mesh of one chunk, produced off the main thread. The opaque lists become the
/// chunk's normal mesh; the translucent ones its fluid mesh, drawn blended after everything
/// opaque so the sea floor shows through the water. <paramref name="HasWater"/> says whether
/// any of those translucent quads are fluid — glass lands in the same lists.
/// </summary>
public record ChunkMeshData(List<Vertex> Vertices, List<ushort> Indices,
    List<Vertex> TranslucentVertices, List<ushort> TranslucentIndices, bool HasWater)
{
    /// <summary>
    /// The chunk's own 16^3 cells of the light flood this mesh was built with, packed
    /// <c>sun &lt;&lt; 4 | block</c> (levels 0..15) in <see cref="Chunk.Idx"/> order, or null
    /// when the build skipped the light flood (all-air or fully enclosed).
    /// </summary>
    public byte[]? Light { get; init; }

    /// <summary>
    /// Local <see cref="Chunk.Idx"/> indices of the chunk's exposed hot-fluid cells
    /// (<see cref="Chunk.IsHotFluidVent"/>), or null when there are none.
    /// </summary>
    public ushort[]? HotFluidVents { get; init; }

    /// <summary>Local <see cref="Chunk.Idx"/> indices of the chunk's hot-spring water tops (their
    /// vents steam), or null when there are none.</summary>
    public ushort[]? SpringVents { get; init; }

    /// <summary>
    /// Chunk-local points of the flames of the blocks that burn one (<see cref="BlockType.FlamePoints"/>,
    /// already turned to each cell's facing), or null when there are none.
    /// </summary>
    public Vector3[]? FlamePoints { get; init; }
}
