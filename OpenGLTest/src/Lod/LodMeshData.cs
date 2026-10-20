using System.Collections.Generic;
using VoxelEngine.Data;
using VoxelEngine.Rendering.Geometry;

namespace VoxelEngine.Lod;

/// <summary>
/// One finished region build on its way from the LodGen worker to the main thread.
///
/// The four lists are rented from <see cref="LodManager"/>'s <see cref="MeshBufferPool"/> by
/// <see cref="LodRegion.BuildMeshData"/> and are owned by whoever dequeues this record: the main
/// thread returns them exactly once, either after the GPU upload or when the build is dropped
/// out of range. Nothing may read them after that return, and the worker must never return them
/// itself — a completed build can sit in the upload queue for several frames.
/// </summary>
public record LodMeshData(int Level, int X, int Z, List<Vertex> Vertices, List<ushort> Indices,
    List<Vertex> WaterVertices, List<ushort> WaterIndices, float YMin, float YMax,
    int[] ColumnCyMin, int[] ColumnCyMax);
