using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// A felled tree: the subtree of a mined tree-wood cell (see <see cref="Collect"/>) and the
/// canopy around it, swinging from upright to flat around the cut and popping out item drops
/// after coming to rest. The mesh is baked
/// once from the real chunk mesher (correct node and tube boxes, AO and crop UVs) rebased to
/// pivot-relative coords; only the GPU upload waits for a GL context on first render.
/// Everything here runs on the main thread; the block set is fixed at fell time, so chunk
/// streaming and later edits cannot disturb the fall.
/// </summary>
public class TopplingTree : IDisposable
{
    /// <summary>One collected block: integer world coords plus its id and state byte (the
    /// <see cref="WoodState"/> or <see cref="LeafState"/> it stood with, which the falling mesh
    /// and the loot roll both read).</summary>
    public readonly record struct FellBlock(Vector3 Cell, byte Id, byte State);

    private readonly List<FellBlock> _blocks;
    private readonly Vector3 _pivot;
    private readonly Vector3 _axis;
    private readonly bool _drops;
    private float _elapsed;
    private bool _dropsTaken;

    /// <summary>CPU-side mesh, pivot-relative, daylight-baked. Built in the constructor.</summary>
    public ChunkMeshData MeshData { get; }

    /// <summary>GPU mesh, uploaded lazily by <see cref="EnsureMesh"/> on first render.</summary>
    public Mesh? Mesh { get; private set; }

    /// <summary>True once the tree has lain still for <see cref="WorldGenerationConfig.TreeFellRestSeconds"/>.</summary>
    public bool IsDone { get; private set; }

    public TopplingTree(List<FellBlock> blocks, Vector3 pivot, Vector3 fallDirection, bool drops = true)
    {
        _blocks = blocks;
        _pivot = pivot;
        _drops = drops;

        var flat = fallDirection.NormalizeHorizontalOr(Vector3.UnitX);
        // Tipping the +Y axis toward the fall direction rotates around Y x fallDir
        _axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, flat));

        MeshData = BuildMeshData(blocks, pivot);
    }

    /// <summary>
    /// Collects what falls when a tree-wood cell is mined: the cell plus its whole subtree in the
    /// tree graph, walked over child edges only (a same-block, non-detached neighbour whose
    /// <see cref="WoodState"/> parent pointer lands on the current cell). Cutting the root takes
    /// the whole tree, root-walk cells included (they point up at the base); cutting a limb takes
    /// only its descendants, so the trunk and every other limb stay standing; detached
    /// (player-placed) wood is never anyone's child. There is no height limit: a drooping limb's
    /// subtree may dip below the cut. Canopy leaves within Chebyshev 2 of the falling wood come
    /// along, each with its own state byte. Mining anything that is not tree wood, or detached
    /// wood, collects just that cell. An unloaded chunk reads as nothing. Bounded by the fell
    /// caps; anything past them stays standing. Pure CPU over live chunk data, main thread, and
    /// safe whether or not the broken cell was already cleared (it is seeded from the passed id
    /// and state).
    /// </summary>
    public static List<FellBlock> Collect(ChunkManager chunks, Vector3 breakCell, byte brokenId, byte brokenState)
    {
        if (!BlockType.IsTreeWood(brokenId) || WoodState.IsDetached(brokenState))
            return [new FellBlock(breakCell, brokenId, brokenState)];

        var fallingWood = new List<FellBlock> { new(breakCell, brokenId, brokenState) };
        var taken = new HashSet<Vector3> { breakCell };

        // Breadth-first over child edges: the list doubles as the queue, so the wood comes out
        // in walk order (the cut cell first, then outward through the subtree)
        for (var head = 0;
             head < fallingWood.Count && fallingWood.Count < WorldGenerationConfig.TreeFellWoodCap;
             head++)
        {
            var current = fallingWood[head].Cell;
            for (var dir = 0; dir < TreeDirections.Count; dir++)
            {
                if (fallingWood.Count >= WorldGenerationConfig.TreeFellWoodCap)
                    break;

                var nx = (int)current.X + TreeDirections.Dx[dir];
                var ny = (int)current.Y + TreeDirections.Dy[dir];
                var nz = (int)current.Z + TreeDirections.Dz[dir];
                if (!chunks.TryGetCell(nx, ny, nz, out var id, out var state)
                    || id != brokenId
                    || WoodState.IsDetached(state)
                    || !WoodState.TryParentOffset(state, out var px, out var py, out var pz)
                    || nx + px != (int)current.X || ny + py != (int)current.Y || nz + pz != (int)current.Z)
                    continue;

                var next = new Vector3(nx, ny, nz);
                if (!taken.Add(next))
                    continue;
                fallingWood.Add(new FellBlock(next, id, state));
            }
        }

        // The canopy comes along without chain-felling neighbours: leaves near falling wood
        // join (the leaf clusters hug the wood that feeds them), but the walk never goes
        // leaf-to-leaf into the next tree. The wood count is fixed first: leaves append to
        // collected, which must not be enumerated while it grows.
        var collected = new List<FellBlock>(fallingWood.Count + 64);
        foreach (var wood in fallingWood)
        {
            if (collected.Count >= WorldGenerationConfig.TreeFellTotalCap)
                return collected;
            collected.Add(wood);
        }

        foreach (var wood in fallingWood)
            for (var dx = -2; dx <= 2; dx++)
            for (var dy = -2; dy <= 2; dy++)
            for (var dz = -2; dz <= 2; dz++)
            {
                if (collected.Count >= WorldGenerationConfig.TreeFellTotalCap)
                    return collected;

                var leaf = wood.Cell + new Vector3(dx, dy, dz);
                if (!taken.Add(leaf))
                    continue;
                if (!chunks.TryGetCell((int)leaf.X, (int)leaf.Y, (int)leaf.Z, out var leafId, out var leafState)
                    || !TreeGenerator.IsLeaf(leafId))
                    continue;

                collected.Add(new FellBlock(leaf, leafId, leafState));
            }

        return collected;
    }

    /// <summary>
    /// Meshes collected blocks with the real chunk mesher (node and tube boxes, AO, crop
    /// UVs) inside one scratch chunk, then rebases positions to pivot-relative and bakes
    /// full daylight (a falling tree is out in the open; sampling its old nook's light would
    /// freeze stale cave shade onto it). The scratch chunk sits in a throwaway manager holding
    /// nothing but empty neighbours, so everything around it reads as air and severed ends cap
    /// themselves. Its local 0..16 is one origin computed once: horizontally centred on the
    /// pivot's column (<see cref="ScratchOrigin"/>), vertically from the lowest collected cell,
    /// so a crown spreads evenly around its trunk; cells outside it still drop their loot but
    /// skip the falling mesh. The biome tint is the field over that same footprint
    /// (<see cref="BiomeTint.FieldsAt"/> at the origin), not the scratch chunk's, so a felled
    /// canopy keeps the colour it stood in.
    /// </summary>
    public static ChunkMeshData BuildMeshData(List<FellBlock> blocks, Vector3 pivot)
    {
        var minY = float.MaxValue;
        foreach (var block in blocks)
            minY = MathF.Min(minY, block.Cell.Y);
        var origin = ScratchOrigin(pivot, blocks.Count == 0 ? pivot.Y : minY);

        // The scratch chunk stands at (256, 0, 256) but its local 0..16 is the footprint from
        // origin, so the tint field has to be the one over that footprint, or a felled canopy
        // would wear the climate of world (4096, 4096).
        var tintFields = BiomeTint.FieldsAt((int)origin.X, (int)origin.Z);

        var scratchManager = new ChunkManager(null!);
        var scratch = new Chunk(null!, new Vector3(256, 0, 256), generateBlockData: false);
        foreach (var block in blocks)
        {
            var local = block.Cell - origin;
            if (local.X < 0 || local.Y < 0 || local.Z < 0 || local.X >= Chunk.Size || local.Y >= Chunk.Size || local.Z >= Chunk.Size)
                continue;

            // Raw write with the cell's own state byte: the wood keeps its thickness and
            // connections, so the falling tree draws the shape it stood with
            scratch.SetBlockRaw((int)local.X, (int)local.Y, (int)local.Z, block.Id, block.State);
        }

        // GenerateMeshData refuses to build without its six face neighbours (a real chunk
        // meshed across an unloaded one bakes border artifacts), so the scratch chunk gets six
        // empty ones. All air, which is exactly the "missing neighbours" the severed ends want
        // to cap themselves against.
        foreach (var offset in Vector3Directions.CardinalDirections)
        {
            var emptyNeighbour = new Chunk(null!, scratch.Position + offset, generateBlockData: false)
            {
                IsBlockDataReady = true
            };
            scratchManager.Chunks[emptyNeighbour.Position] = emptyNeighbour;
        }

        var meshData = scratch.GenerateMeshData(scratchManager, greedy: true, tintFields) ?? new ChunkMeshData([], [], [], [], false);

        var rebase = origin - pivot;
        for (var i = 0; i < meshData.Vertices.Count; i++)
        {
            var vertex = meshData.Vertices[i];
            vertex.Position += rebase;
            vertex.Sun = 255;
            vertex.Block = 0;
            meshData.Vertices[i] = vertex;
        }

        return meshData;
    }

    /// <summary>
    /// World cell the felled mesh's scratch chunk maps to its local (0, 0, 0): the pivot's
    /// column minus half a chunk on X and Z, so the trunk sits in the middle and a crown up to
    /// a chunk wide fits around it, and <paramref name="minY"/> (the lowest collected cell) on Y.
    /// </summary>
    public static Vector3 ScratchOrigin(Vector3 pivot, float minY) =>
        new(MathF.Floor(pivot.X) - Chunk.Size / 2, minY, MathF.Floor(pivot.Z) - Chunk.Size / 2);

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
    /// call returns nothing. A tree felled in creative mode yields nothing.
    /// </summary>
    public List<(byte Id, Vector3 Cell)> TakeDrops()
    {
        var drops = new List<(byte, Vector3)>();
        if (_dropsTaken)
            return drops;
        _dropsTaken = true;

        if (!_drops)
            return drops;

        var landed = Matrix4x4.CreateFromAxisAngle(_axis, MathF.PI * 0.5f);
        var rolled = new List<byte>();
        foreach (var block in _blocks)
        {
            var rest = _pivot + Vector3.Transform(block.Cell + new Vector3(0.5f) - _pivot, landed);
            var cell = rest.Floor();

            // The resting cell is the loot context too, so position-dependent rolls see where
            // the block landed rather than where it grew
            rolled.Clear();
            DropManager.RollLoot(block.Id, new LootContext((int)cell.X, (int)cell.Y, (int)cell.Z, State: block.State), rolled);

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
