using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// A felled tree: the connected wood and canopy above a mined base log, swinging from upright
/// to flat around its base and popping out item drops after coming to rest. The mesh is baked
/// once from the real chunk mesher (correct taper boxes, caps, AO and crop UVs) rebased to
/// pivot-relative coords; only the GPU upload waits for a GL context on first render.
/// Everything here runs on the main thread; the block set is fixed at fell time, so chunk
/// streaming and later edits cannot disturb the fall.
/// </summary>
public class TopplingTree : IDisposable
{
    /// <summary>One collected block: integer world coords plus its id.</summary>
    public readonly record struct FellBlock(Vector3 Cell, byte Id);

    private readonly List<FellBlock> _blocks;
    private readonly Vector3 _pivot;
    private readonly Vector3 _axis;
    private float _elapsed;
    private bool _dropsTaken;

    /// <summary>CPU-side mesh, pivot-relative, daylight-baked. Built in the constructor.</summary>
    public ChunkMeshData MeshData { get; }

    /// <summary>GPU mesh, uploaded lazily by <see cref="EnsureMesh"/> on first render.</summary>
    public Mesh? Mesh { get; private set; }

    /// <summary>True once the tree has lain still for <see cref="WorldGenerationConfig.TreeFellRestSeconds"/>.</summary>
    public bool IsDone { get; private set; }

    public TopplingTree(List<FellBlock> blocks, Vector3 pivot, Vector3 fallDirection)
    {
        _blocks = blocks;
        _pivot = pivot;

        var flat = new Vector3(fallDirection.X, 0f, fallDirection.Z);
        if (flat.LengthSquared() < 1e-6f)
            flat = Vector3.UnitX;
        flat = Vector3.Normalize(flat);
        // Tipping the +Y axis toward the fall direction rotates around Y x fallDir
        _axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, flat));

        MeshData = BuildMeshData(blocks, pivot);
    }

    /// <summary>
    /// Collects the severed part of a tree above a mined wood block: the broken block itself
    /// plus every connected wood block at or above its height that lost its support through
    /// the break. The stump below stays standing, and so does any limb still rooted through
    /// standing wood — cutting one branch no longer fells the crown. Canopy leaves within
    /// two blocks of the falling wood come along. Bounded by the fell caps; anything past
    /// them stays standing. Pure CPU over live chunk data — safe to call from the main
    /// thread on break, whether or not the broken cell itself was already cleared.
    /// </summary>
    public static List<FellBlock> Collect(ChunkManager chunks, Vector3 breakCell, byte brokenId)
    {
        var baseY = (int)breakCell.Y;

        // Flood first: every wood block connected to the break at or above its height, as if
        // the break cell were still there (it is seeded either way, so a cleared cell reads
        // the passed id instead of air).
        var woodIds = new Dictionary<Vector3, byte>();
        var seen = new HashSet<Vector3> { breakCell };
        if (TreeGenerator.WoodLevel(brokenId) >= 0)
            woodIds[breakCell] = brokenId;

        var queue = new Queue<Vector3>();
        queue.Enqueue(breakCell);
        while (queue.Count > 0
               && woodIds.Count < WorldGenerationConfig.TreeFellWoodCap)
        {
            var cell = queue.Dequeue();
            foreach (var offset in Vector3Directions.CardinalDirections)
            {
                var next = cell + offset;
                if ((int)next.Y < baseY || !seen.Add(next))
                    continue;
                var id = chunks.GetBlockGlobal((int)next.X, (int)next.Y, (int)next.Z);
                if (TreeGenerator.WoodLevel(id) < 0)
                    continue;

                woodIds[next] = id;
                queue.Enqueue(next);
            }
        }

        // Support split: with the break cell gone, whatever is still connected through wood
        // down to live wood below the break stays up; the rest (the break plus its children)
        // falls. A floating remnant with no live wood underneath has no seeds, so it all falls.
        var standing = new HashSet<Vector3>();
        var supportQueue = new Queue<Vector3>();
        foreach (var cell in woodIds.Keys)
        {
            if (cell == breakCell)
                continue;
            foreach (var offset in Vector3Directions.CardinalDirections)
            {
                var next = cell + offset;
                if ((int)next.Y >= baseY)
                    continue;
                if (TreeGenerator.WoodLevel(chunks.GetBlockGlobal((int)next.X, (int)next.Y, (int)next.Z)) < 0)
                    continue;

                standing.Add(cell);
                supportQueue.Enqueue(cell);
                break;
            }
        }

        while (supportQueue.Count > 0)
        {
            var cell = supportQueue.Dequeue();
            foreach (var offset in Vector3Directions.CardinalDirections)
            {
                var next = cell + offset;
                if (next == breakCell || !woodIds.ContainsKey(next) || !standing.Add(next))
                    continue;
                supportQueue.Enqueue(next);
            }
        }

        // The canopy comes along without chain-felling neighbours: leaves near falling wood
        // join (the stamper keeps every blob cell within Chebyshev 2 of wood), but the flood
        // never walks leaf-to-leaf into the next tree. The falling wood is snapshotted first:
        // leaves append to collected, which must not be enumerated while it grows.
        var fallingWood = new List<Vector3>(woodIds.Count);
        foreach (var cell in woodIds.Keys)
        {
            if (cell != breakCell && standing.Contains(cell))
                continue;
            fallingWood.Add(cell);
        }

        var collected = new List<FellBlock>(fallingWood.Count + 16);
        var taken = new HashSet<Vector3>();
        foreach (var cell in fallingWood)
        {
            if (collected.Count >= WorldGenerationConfig.TreeFellTotalCap)
                return collected;

            collected.Add(new FellBlock(cell, woodIds[cell]));
            taken.Add(cell);
        }

        foreach (var cell in fallingWood)
            for (var dx = -2; dx <= 2; dx++)
            for (var dy = -2; dy <= 2; dy++)
            for (var dz = -2; dz <= 2; dz++)
            {
                if (collected.Count >= WorldGenerationConfig.TreeFellTotalCap)
                    return collected;

                var leaf = cell + new Vector3(dx, dy, dz);
                if ((int)leaf.Y < baseY || !taken.Add(leaf))
                    continue;
                if (chunks.GetBlockGlobal((int)leaf.X, (int)leaf.Y, (int)leaf.Z) != BlockType.Leaves)
                    continue;

                collected.Add(new FellBlock(leaf, BlockType.Leaves));
            }

        return collected;
    }

    /// <summary>
    /// Meshes collected blocks with the real chunk mesher (taper boxes, joint caps, AO, crop
    /// UVs) inside one scratch chunk, then rebases positions to pivot-relative and bakes
    /// full daylight (a falling tree is out in the open; sampling its old nook's light would
    /// freeze stale cave shade onto it). The scratch chunk is never registered anywhere, so
    /// its missing neighbours read as air and severed ends cap themselves.
    /// </summary>
    public static ChunkMeshData BuildMeshData(List<FellBlock> blocks, Vector3 pivot)
    {
        // Real trees fit a chunk (limbs reach ~6, crowns ~13 tall); anything past that is a
        // player mega-build, whose overflow cells still drop but skip the falling mesh.
        var min = new Vector3(float.MaxValue);
        foreach (var block in blocks)
            min = Vector3.Min(min, block.Cell);

        var scratchManager = new ChunkManager(null!);
        var scratch = new Chunk(null!, new Vector3(256, 0, 256), generateBlockData: false);
        foreach (var block in blocks)
        {
            var local = block.Cell - min;
            if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= Chunk.Size || local.Y >= Chunk.Size || local.Z >= Chunk.Size)
                continue;

            var worldPos = new Vector3(256 * Chunk.Size, 0, 256 * Chunk.Size) + local;
            scratch.PlaceBlockAt(worldPos, block.Id, scratchManager);
        }

        var meshData = scratch.GenerateMeshData(scratchManager);

        var rebase = min - pivot;
        for (var i = 0; i < meshData.Vertices.Count; i++)
        {
            var vertex = meshData.Vertices[i];
            vertex.Position += rebase;
            vertex.Sun = 1f;
            vertex.Block = 0f;
            meshData.Vertices[i] = vertex;
        }

        return meshData;
    }

    /// <summary>Current swing angle: cubic ease-in from upright to flat over the topple time.</summary>
    public float AngleNow
    {
        get
        {
            var t = MathUtils.Clamp01(_elapsed / WorldGenerationConfig.TreeToppleSeconds);
            return MathF.PI * 0.5f * MathUtils.EaseInCubic(t);
        }
    }

    /// <summary>Model matrix swinging the pivot-relative mesh around its base. Row-vector
    /// convention like the other world draws (see RenderBlockPop): rotate first, then move
    /// to the pivot.</summary>
    public Matrix4x4 Model =>
        Matrix4x4.CreateFromAxisAngle(_axis, AngleNow) * Matrix4x4.CreateTranslation(_pivot);

    /// <summary>Advances the swing, then the rest timer; <see cref="IsDone"/> fires once the
    /// tree has lain still for <see cref="WorldGenerationConfig.TreeFellRestSeconds"/>.</summary>
    public void Update(float deltaTime)
    {
        if (IsDone) return;

        _elapsed += deltaTime;
        if (_elapsed >= WorldGenerationConfig.TreeToppleSeconds + WorldGenerationConfig.TreeFellRestSeconds)
            IsDone = true;
    }

    /// <summary>
    /// Item drops for the landed tree, each at the floored cell its block came to rest in.
    /// Every block rolls its own loot table through <see cref="DropManager.RollLoot"/>, so a
    /// felled canopy yields exactly what mining or blasting it would. Single-use: the second
    /// call returns nothing.
    /// </summary>
    public List<(byte Id, Vector3 Cell)> TakeDrops()
    {
        var drops = new List<(byte, Vector3)>();
        if (_dropsTaken)
            return drops;
        _dropsTaken = true;

        var landed = Matrix4x4.CreateFromAxisAngle(_axis, MathF.PI * 0.5f);
        var rolled = new List<byte>();
        foreach (var block in _blocks)
        {
            var rest = _pivot + Vector3.Transform(block.Cell + new Vector3(0.5f) - _pivot, landed);
            var cell = new Vector3(MathF.Floor(rest.X), MathF.Floor(rest.Y), MathF.Floor(rest.Z));

            // The resting cell is the loot context too, so position-dependent rolls see where
            // the block landed rather than where it grew
            rolled.Clear();
            DropManager.RollLoot(block.Id, new LootContext((int)cell.X, (int)cell.Y, (int)cell.Z), rolled);

            foreach (var id in rolled)
                drops.Add((id, cell));
        }

        return drops;
    }

    /// <summary>Uploads the baked mesh on first render (GL is only legal on the main thread).</summary>
    public void EnsureMesh(GL gl)
    {
        if (Mesh != null || MeshData.Vertices.Count == 0)
            return;

        Mesh = new Mesh(gl,
            CollectionsMarshal.AsSpan(MeshData.Vertices),
            CollectionsMarshal.AsSpan(MeshData.Indices));
    }

    public void Dispose()
    {
        Mesh?.Dispose();
        Mesh = null;
        GC.SuppressFinalize(this);
    }
}
