using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Meshes an arbitrary set of cells (a grown tree, its ground) with the real chunk mesher, so
/// tree-wood tubes, wide-trunk shells and their pixel rim, leaves and the biome tint all come out
/// exactly as they do in game. <see cref="TopplingTree.BuildMeshData"/> meshes one 16^3 scratch
/// window and silently drops whatever falls outside it, which a felled crown tolerates but an
/// item-debugger tree spanning several chunks does not. Here every chunk the cells touch is a
/// scratch chunk at its <b>real</b> chunk position, inside a throwaway manager whose whole 3D box
/// is padded by a one-chunk ring of empty, ready chunks (edge and corner diagonals included, since
/// the bevelled-shell cull reads edge neighbors). Chunks therefore see each other across every
/// border, nothing past the set is anything but air, and each chunk's own
/// <see cref="BiomeTint.FieldsFor"/> is the true tint field at those cells.
/// No GL; single-threaded (the mesher's scratch is thread-static).
/// </summary>
public static class CellBatchMesher
{
    /// <summary>
    /// One mesh per chunk that received a cell (indices are 16-bit, so the meshes are never
    /// merged), in ascending chunk (x, y, z) order, with positions in world cell coordinates and
    /// full daylight baked in (the scratch has no real sky). Translucent lists are kept as the
    /// mesher produced them. Chunks whose build came out empty are skipped.
    /// </summary>
    public static List<ChunkMeshData> Build(IReadOnlyList<TreeCell> cells)
    {
        var result = new List<ChunkMeshData>();
        if (cells.Count == 0) return result;

        // Arithmetic shifts floor negative coordinates, exactly like VoxelCoords.ChunkPosOf.
        int minCx = int.MaxValue, minCy = int.MaxValue, minCz = int.MaxValue;
        int maxCx = int.MinValue, maxCy = int.MinValue, maxCz = int.MinValue;
        foreach (var cell in cells)
        {
            int cx = cell.X >> Chunk.SizeShift, cy = cell.Y >> Chunk.SizeShift, cz = cell.Z >> Chunk.SizeShift;
            if (cx < minCx) minCx = cx;
            if (cy < minCy) minCy = cy;
            if (cz < minCz) minCz = cz;
            if (cx > maxCx) maxCx = cx;
            if (cy > maxCy) maxCy = cy;
            if (cz > maxCz) maxCz = cz;
        }

        var manager = new ChunkManager(null!);
        var chunks = new Dictionary<(int, int, int), Chunk>();
        for (var cx = minCx - 1; cx <= maxCx + 1; cx++)
        for (var cy = minCy - 1; cy <= maxCy + 1; cy++)
        for (var cz = minCz - 1; cz <= maxCz + 1; cz++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false)
            {
                IsBlockDataReady = true
            };
            manager.Chunks[chunk.Position] = chunk;
            chunks[(cx, cy, cz)] = chunk;
        }

        // Raw writes keep each cell's own state byte (wood thickness, connections, shell bit).
        var touched = new SortedSet<(int, int, int)>();
        foreach (var cell in cells)
        {
            var key = (cell.X >> Chunk.SizeShift, cell.Y >> Chunk.SizeShift, cell.Z >> Chunk.SizeShift);
            chunks[key].SetBlockRaw(cell.X & Chunk.SizeMask, cell.Y & Chunk.SizeMask, cell.Z & Chunk.SizeMask,
                cell.Block, cell.State);
            touched.Add(key);
        }

        foreach (var key in touched)
        {
            var chunk = chunks[key];
            // tintFields stays null: the chunk stands where its cells really are.
            var mesh = chunk.GenerateMeshData(manager, greedy: true);
            if (mesh == null || (mesh.Vertices.Count == 0 && mesh.TranslucentVertices.Count == 0))
                continue;

            var offset = chunk.Position * Chunk.Size;
            Rebase(mesh.Vertices, offset);
            Rebase(mesh.TranslucentVertices, offset);
            result.Add(mesh);
        }

        return result;
    }

    // Chunk-local -> world cell coordinates, and full daylight like the felled tree's mesh.
    private static void Rebase(List<Vertex> vertices, Vector3 offset)
    {
        for (var i = 0; i < vertices.Count; i++)
        {
            var vertex = vertices[i];
            vertex.Position += offset;
            vertex.Sun = 255;
            vertex.Block = 0;
            vertices[i] = vertex;
        }
    }
}
