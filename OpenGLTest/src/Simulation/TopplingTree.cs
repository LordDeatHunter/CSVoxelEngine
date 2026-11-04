using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Loot;
using VoxelEngine.Data;
using VoxelEngine.Graphics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.Simulation;

/// <summary>
/// A felled tree: the wood a mined tree-wood cell held up (see <see cref="Collect"/>) and the
/// canopy around it, swinging from upright to flat around the cut and popping out item drops
/// after coming to rest. The mesh is baked
/// once from the real chunk mesher (correct node and tube boxes, AO and crop UVs), one mesh per
/// chunk the tree's cells span, rebased to pivot-relative coords; only the GPU upload waits for
/// a GL context on first render.
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
    // Set by the update the swing reaches flat, cleared by TryTakeLanding
    private bool _landingPending;

    /// <summary>CPU-side meshes, one per chunk the tree's cells touch, pivot-relative, daylight-baked. Built in the constructor.</summary>
    public IReadOnlyList<ChunkMeshData> MeshData { get; }

    /// <summary>GPU meshes, uploaded lazily by <see cref="EnsureMesh"/> on first render; empty until then.</summary>
    public IReadOnlyList<Mesh> Meshes => _meshes;

    private readonly List<Mesh> _meshes = [];
    private bool _meshUploaded;

    /// <summary>True once the tree has lain still for <see cref="SimulationConfig.TreeFellRestSeconds"/>.</summary>
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
    /// Collects what falls when a tree-wood cell is mined: every component of tree wood that
    /// loses every path to an anchor once the cut cell is gone (<see cref="TreeSupport"/>'s rule,
    /// shared with the orphan crumble: paths over parent edges plus vertical shell-to-shell
    /// adjacency, anchors are roots and shells on real ground). On a thin tree that is exactly
    /// the subtree walk: cutting a limb takes only its descendants, so the trunk and every other
    /// limb stay standing, and cutting the root takes the whole tree, root-walk cells included
    /// (they point up at the base). A wide trunk falls only once a whole slice is cut: while any
    /// shell of the slice still stands, the ring above links down through it. There is no height
    /// limit: a drooping limb's subtree may dip below the cut. Canopy leaves within Chebyshev 2
    /// of the falling wood come along, each with its own state byte; shells add no sweep of their
    /// own, since the nodes they ring already cover the canopy around them. Mining anything that
    /// is not tree wood, or detached wood, collects just that cell. An unloaded chunk reads as no
    /// link. Bounded by the fell caps: a component that would pass
    /// <see cref="SimulationConfig.TreeFellWoodCap"/>, or whose search hits it, stays
    /// standing (and still drops normally when mined). Pure CPU over live chunk data, main
    /// thread, and safe whether or not the broken cell was already cleared (it is seeded from
    /// the passed id and state, and the search reads it as absent).
    /// </summary>
    public static List<FellBlock> Collect(ChunkManager chunks, Vector3 breakCell, byte brokenId, byte brokenState)
    {
        if (!BlockType.IsTreeWood(brokenId) || WoodState.IsDetached(brokenState))
            return [new FellBlock(breakCell, brokenId, brokenState)];

        var cx = (int)breakCell.X;
        var cy = (int)breakCell.Y;
        var cz = (int)breakCell.Z;
        var fallingWood = new List<FellBlock> { new(breakCell, brokenId, brokenState) };
        var taken = new HashSet<long> { CellKey.Pack(cx, cy, cz) };
        var standing = new HashSet<long>();
        var support = new TreeSupport(SimulationConfig.TreeFellWoodCap);

        // Each neighbor the cut cell linked to heads a candidate component: it falls exactly
        // when the search from it, with the cut cell gone, finds no anchor
        for (var dir = 0; dir < TreeDirections.Count; dir++)
        {
            var nx = cx + TreeDirections.Dx[dir];
            var ny = cy + TreeDirections.Dy[dir];
            var nz = cz + TreeDirections.Dz[dir];
            if (!chunks.TryGetCell(nx, ny, nz, out var nid, out var nstate)
                || !TreeSupport.Links(brokenId, brokenState, nid, nstate, dir))
                continue;
            var key = CellKey.Pack(nx, ny, nz);
            if (standing.Contains(key) || taken.Contains(key))
                continue;

            var result = support.Search(chunks.TryGetCell, nx, ny, nz,
                hasExcluded: true, cx, cy, cz, unloadedIsUnknown: false);
            if (result == TreeSupportResult.Unsupported
                && fallingWood.Count + support.Component.Count <= SimulationConfig.TreeFellWoodCap)
            {
                foreach (var (x, y, z, state) in support.Component)
                {
                    taken.Add(CellKey.Pack(x, y, z));
                    fallingWood.Add(new FellBlock(new Vector3(x, y, z), nid, state));
                }
            }
            else
            {
                foreach (var visited in support.VisitedKeys)
                    standing.Add(visited);
            }
        }

        // The canopy comes along without chain-felling neighbors: leaves near falling wood
        // join (the leaf clusters hug the wood that feeds them), but the walk never goes
        // leaf-to-leaf into the next tree. The wood count is fixed first: leaves append to
        // collected, which must not be enumerated while it grows.
        var collected = new List<FellBlock>(fallingWood.Count + 64);
        foreach (var wood in fallingWood)
        {
            if (collected.Count >= SimulationConfig.TreeFellTotalCap)
                return collected;
            collected.Add(wood);
        }

        foreach (var wood in fallingWood)
        {
            // A wide trunk's shells sit inside the sweep of the node they ring
            if (WoodState.IsShell(wood.State))
                continue;

            var wx = (int)wood.Cell.X;
            var wy = (int)wood.Cell.Y;
            var wz = (int)wood.Cell.Z;
            for (var dx = -2; dx <= 2; dx++)
            for (var dy = -2; dy <= 2; dy++)
            for (var dz = -2; dz <= 2; dz++)
            {
                if (collected.Count >= SimulationConfig.TreeFellTotalCap)
                    return collected;

                int lx = wx + dx, ly = wy + dy, lz = wz + dz;
                if (!taken.Add(CellKey.Pack(lx, ly, lz)))
                    continue;
                if (!chunks.TryGetCell(lx, ly, lz, out var leafId, out var leafState)
                    || !TreeGenerator.IsLeaf(leafId))
                    continue;

                collected.Add(new FellBlock(new Vector3(lx, ly, lz), leafId, leafState));
            }
        }

        return collected;
    }

    /// <summary>
    /// Meshes collected blocks with the real chunk mesher (node and tube boxes, AO, crop UVs)
    /// through <see cref="CellBatchMesher.Build"/>: one mesh per chunk the cells touch, each
    /// chunk at its real position inside a ring of empty ones, so a crown of any size is meshed
    /// whole, severed ends cap themselves against air and the biome tint is the field the tree
    /// stood in. Positions are then rebased to pivot-relative; full daylight is already baked in
    /// (a falling tree is out in the open; sampling its old nook's light would freeze stale cave
    /// shade onto it).
    /// </summary>
    public static List<ChunkMeshData> BuildMeshData(List<FellBlock> blocks, Vector3 pivot)
    {
        // Each block keeps its own state byte: the wood keeps its thickness and connections, so
        // the falling tree draws the shape it stood with
        var cells = new List<TreeCell>(blocks.Count);
        foreach (var block in blocks)
            cells.Add(new TreeCell((int)block.Cell.X, (int)block.Cell.Y, (int)block.Cell.Z, block.Id, block.State));

        var meshes = CellBatchMesher.Build(cells);
        foreach (var mesh in meshes)
        {
            RebaseToPivot(mesh.Vertices, pivot);
            RebaseToPivot(mesh.TranslucentVertices, pivot);
        }

        return meshes;
    }

    private static void RebaseToPivot(List<Vertex> vertices, Vector3 pivot)
    {
        for (var i = 0; i < vertices.Count; i++)
        {
            var vertex = vertices[i];
            vertex.Position -= pivot;
            vertices[i] = vertex;
        }
    }

    /// <summary>Current swing angle: cubic ease-in from upright to flat over the topple time.</summary>
    public float AngleNow
    {
        get
        {
            var t = MathUtils.Clamp01(_elapsed / SimulationConfig.TreeToppleSeconds);
            return MathF.PI * 0.5f * MathUtils.EaseInCubic(t);
        }
    }

    /// <summary>Model matrix swinging the pivot-relative mesh around its base. Row-vector
    /// convention like the other world draws (see SelectionPasses.RenderBlockPop): rotate first,
    /// then move to the pivot.</summary>
    public Matrix4x4 Model =>
        Matrix4x4.CreateFromAxisAngle(_axis, AngleNow) * Matrix4x4.CreateTranslation(_pivot);

    /// <summary>Advances the swing, then the rest timer; <see cref="IsDone"/> fires once the
    /// tree has lain still for <see cref="SimulationConfig.TreeFellRestSeconds"/>.</summary>
    public void Update(float deltaTime)
    {
        if (IsDone) return;

        var falling = _elapsed < SimulationConfig.TreeToppleSeconds;
        _elapsed += deltaTime;
        if (falling && _elapsed >= SimulationConfig.TreeToppleSeconds)
            _landingPending = true;
        if (_elapsed >= SimulationConfig.TreeToppleSeconds + SimulationConfig.TreeFellRestSeconds)
            IsDone = true;
    }

    /// <summary>True exactly once: on the first call after the update that swung the tree flat, the
    /// moment it hits the ground (the world's thud: shake, leaf burst and dust).</summary>
    public bool TryTakeLanding()
    {
        if (!_landingPending) return false;
        _landingPending = false;
        return true;
    }

    /// <summary>
    /// Appends every collected block to <paramref name="into"/> at the cell it lies in once the tree
    /// is flat (the same rest cells <see cref="TakeDrops"/> drops its loot at), keeping its id and
    /// state, so the thud knows where the trunk and the canopy came down.
    /// </summary>
    public void GatherLanded(List<FellBlock> into)
    {
        var landed = Matrix4x4.CreateFromAxisAngle(_axis, MathF.PI * 0.5f);
        foreach (var block in _blocks)
            into.Add(block with { Cell = RestCell(block, landed) });
    }

    // The floored cell a block's center comes to rest in after the swing
    private Vector3 RestCell(FellBlock block, Matrix4x4 landed) =>
        (_pivot + Vector3.Transform(block.Cell + new Vector3(0.5f) - _pivot, landed)).Floor();

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
            var cell = RestCell(block, landed);

            // The resting cell is the loot context too, so position-dependent rolls see where
            // the block landed rather than where it grew
            rolled.Clear();
            DropManager.RollLoot(block.Id, new LootContext((int)cell.X, (int)cell.Y, (int)cell.Z, State: block.State), rolled);

            foreach (var id in rolled)
                drops.Add((id, cell));
        }

        return drops;
    }

    /// <summary>Uploads the baked meshes on first render (GL is only legal on the main thread).</summary>
    public void EnsureMesh(GL gl)
    {
        if (_meshUploaded)
            return;
        _meshUploaded = true;

        foreach (var data in MeshData)
        {
            if (data.Vertices.Count == 0)
                continue;
            _meshes.Add(new Mesh(gl,
                CollectionsMarshal.AsSpan(data.Vertices),
                CollectionsMarshal.AsSpan(data.Indices)));
        }
    }

    public void Dispose()
    {
        foreach (var mesh in _meshes)
            mesh.Dispose();
        _meshes.Clear();
        GC.SuppressFinalize(this);
    }
}
