using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Graphics;

namespace VoxelEngine.Simulation;

/// <summary>
/// One rigid piece of a felled <see cref="TopplingTree"/>: a set of its blocks, the body they fall
/// as and the meshes that draw them. A tree starts as one part; a branch that hits the ground snaps
/// off into a part of its own and the rest is rebuilt without it.
/// </summary>
public sealed class FellPart : IDisposable
{
    private readonly Vector3 _pivot;
    private IReadOnlyList<ChunkMeshData> _meshData;
    private readonly List<Mesh> _meshes = [];
    private bool _uploaded;

    internal List<int> Blocks { get; private set; }
    internal FellBody Body { get; private set; }

    internal FellPart(Vector3 pivot, List<int> blocks, FellBody body, IReadOnlyList<ChunkMeshData> meshData)
    {
        _pivot = pivot;
        Blocks = blocks;
        Body = body;
        _meshData = meshData;
    }

    /// <summary>CPU-side meshes, pivot-relative, daylight-baked.</summary>
    public IReadOnlyList<ChunkMeshData> MeshData => _meshData;

    /// <summary>GPU meshes, uploaded lazily by <see cref="EnsureMesh"/> on first render; empty until then.</summary>
    public IReadOnlyList<Mesh> Meshes => _meshes;

    /// <summary>Model matrix of the part's pivot-relative mesh (row-vector convention like the other
    /// world draws, see SelectionPasses.RenderBlockPop).</summary>
    public Matrix4x4 Model => Body.ModelFor(_pivot);

    // The part lost blocks to a snapped-off branch: new body, new mesh
    internal void Rebuild(List<int> blocks, FellBody body, IReadOnlyList<ChunkMeshData> meshData)
    {
        DisposeMeshes();
        Blocks = blocks;
        Body = body;
        _meshData = meshData;
        _uploaded = false;
    }

    /// <summary>Uploads the baked meshes on first render (GL is only legal on the main thread).</summary>
    public void EnsureMesh(GL gl)
    {
        if (_uploaded) return;
        _uploaded = true;

        foreach (var data in _meshData)
        {
            if (data.Vertices.Count == 0) continue;
            _meshes.Add(new Mesh(gl,
                CollectionsMarshal.AsSpan(data.Vertices),
                CollectionsMarshal.AsSpan(data.Indices)));
        }
    }

    private void DisposeMeshes()
    {
        foreach (var mesh in _meshes)
            mesh.Dispose();
        _meshes.Clear();
    }

    public void Dispose()
    {
        DisposeMeshes();
        GC.SuppressFinalize(this);
    }
}
