using System;
using System.Collections.Generic;
using System.Numerics;
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
/// canopy around it, falling as one rigid body (<see cref="FellBody"/>: gravity, a tip over the
/// cut, terrain contacts, a tumble and a settle) and popping out item drops after coming to rest. The mesh is baked
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
    public readonly record struct FellBlock(Vector3 Cell, ushort Id, byte State);

    private readonly List<FellBlock> _blocks;
    private readonly Vector3 _pivot;
    private readonly Vector3 _axis;
    private readonly bool _drops;
    private bool _dropsTaken;
    // The physics: the tree is one rigid body (see FellBody), pinned to the stump at the cut while
    // it tips over like a trunk, or simply dropped when it is a limb. A side branch that hits the
    // ground snaps off into a part of its own (see FellBranches), so there can be several.
    private readonly List<FellPart> _parts = [];
    private readonly FellPart?[] _partOf;
    private readonly FellBranches _branches;
    private readonly Vector3[] _centers;
    private readonly Vector3[][] _contacts;
    private readonly float[] _masses;
    private readonly FellTerrain _terrain;
    // Seconds every part has been at rest
    private float _restElapsed;
    // Set by the update the swing reaches flat, cleared by TryTakeLanding
    private bool _landingPending;
    private bool _landed;
    private float _sinceLanding;
    private readonly float[] _hitHalf;
    private readonly Dictionary<object, float> _struck = [];

    /// <summary>The rigid pieces the tree is in now: one at first, more as branches snap off. Each
    /// carries its own meshes and model matrix.</summary>
    public IReadOnlyList<FellPart> Parts => _parts;

    /// <summary>True once the tree has lain still for <see cref="SimulationConfig.TreeFellRestSeconds"/>.</summary>
    public bool IsDone { get; private set; }

    /// <param name="isSolid">Plain ground: whether the world cell at (x, y, z) stops a falling tree
    /// and holds its pin, with nothing to snag on (see <see cref="FellTerrain.Ground"/>).</param>
    /// <param name="terrain">The world as the tree feels it (the felled cells are already gone from
    /// it); wins over <paramref name="isSolid"/>. Without either nothing stops the tree.</param>
    public TopplingTree(List<FellBlock> blocks, Vector3 pivot, Vector3 fallDirection, bool drops = true,
        Func<int, int, int, bool>? isSolid = null, FellTerrain? terrain = null)
    {
        terrain ??= isSolid != null ? FellTerrain.Ground(isSolid) : null;
        _blocks = blocks;
        _pivot = pivot;
        _drops = drops;

        var flat = fallDirection.NormalizeHorizontalOr(Vector3.UnitX);
        // Tipping the +Y axis toward the fall direction rotates around Y x fallDir
        _axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, flat));

        _terrain = terrain ?? FellTerrain.Ground((_, _, _) => false);
        _centers = new Vector3[blocks.Count];
        _contacts = FellContacts.Build(blocks);
        // How far a wood block reaches from its center when it hits something; leaves are soft
        _hitHalf = new float[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            _hitHalf[i] = BlockType.IsTreeWood(blocks[i].Id)
                ? MathF.Max(WoodState.DiameterPx(blocks[i].State) / (2f * WoodState.PixelsPerBlock), 0.2f)
                : 0f;
        }
        _masses = new float[blocks.Count];
        var top = pivot.Y + 1f;
        foreach (var block in blocks)
            top = MathF.Max(top, block.Cell.Y + 1f);
        for (var i = 0; i < blocks.Count; i++)
        {
            _centers[i] = blocks[i].Cell + new Vector3(0.5f);
            _masses[i] = BlockMass(blocks[i], pivot.Y, top);
        }
        // Still attached when the cut stands on ground and the wood mostly rises above it; a limb
        // or a set of branches has no such base and just falls
        var pinned = terrain != null
                     && terrain.HoldsPin((int)MathF.Floor(pivot.X), (int)MathF.Floor(pivot.Y) - 1, (int)MathF.Floor(pivot.Z))
                     && StandsAboveCut(blocks, pivot);

        var all = new List<int>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
            all.Add(i);
        var body = NewBody(all, pinned, null);
        _partOf = new FellPart?[blocks.Count];
        _branches = new FellBranches(blocks, pivot);
        var part = new FellPart(pivot, all, body, BuildMeshData(blocks, pivot));
        _parts.Add(part);
        foreach (var i in all)
            _partOf[i] = part;
    }

    /// <summary>
    /// A block's mass: leaves are light, wood is heavier toward the root (1 at the crown, up to
    /// 1 + <see cref="SimulationConfig.TreeFellRootWeightBoost"/> at the cut) so the base of a
    /// falling tree outweighs its branches.
    /// </summary>
    private static float BlockMass(FellBlock block, float baseY, float topY)
    {
        if (TreeGenerator.IsLeaf(block.Id))
            return SimulationConfig.TreeFellLeafMass;
        var up = MathUtils.Clamp01((block.Cell.Y + 0.5f - baseY) / MathF.Max(topY - baseY, 1f));
        return 1f + SimulationConfig.TreeFellRootWeightBoost * (1f - up);
    }

    private FellBody NewBody(List<int> indices, bool hinged, FellBody? inherit)
    {
        var centers = new Vector3[indices.Count];
        var masses = new float[indices.Count];
        var contacts = new Vector3[indices.Count][];
        for (var k = 0; k < indices.Count; k++)
        {
            centers[k] = _centers[indices[k]];
            masses[k] = _masses[indices[k]];
            contacts[k] = _contacts[indices[k]];
        }
        return new FellBody(centers, masses, contacts, _pivot, _axis, hinged, _terrain.Stops, inherit, _terrain.Snags);
    }

    /// <summary>
    /// Whether the wood is mostly above the cut and not leaning far out sideways: the tree is
    /// still standing on its stump (an upright trunk) rather than a limb sticking out of it.
    /// </summary>
    private static bool StandsAboveCut(List<FellBlock> blocks, Vector3 pivot)
    {
        // Measured from the center of the cut cell (the pivot is its underside), so a limb that
        // leaves the cut level reads as level
        var origin = pivot + new Vector3(0f, 0.5f, 0f);
        var sum = Vector3.Zero;
        var count = 0;
        foreach (var block in blocks)
        {
            if (!BlockType.IsTreeWood(block.Id)) continue;
            sum += block.Cell + new Vector3(0.5f) - origin;
            count++;
        }
        if (count == 0) return false;

        var center = sum / count;
        if (center.Y <= 0f) return false;
        var sideways = new Vector3(center.X, 0f, center.Z).Length();
        return MathF.Atan2(center.Y, sideways) >= SimulationConfig.TreeFlatSnapRadians;
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
    public static List<FellBlock> Collect(ChunkManager chunks, Vector3 breakCell, ushort brokenId, byte brokenState)
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

    /// <summary>Advances the fall; <see cref="IsDone"/> fires once every piece has lain still for
    /// <see cref="SimulationConfig.TreeFellRestSeconds"/>.</summary>
    public void Update(float deltaTime)
    {
        if (IsDone) return;

        foreach (var victim in new List<object>(_struck.Keys))
        {
            var left = _struck[victim] - deltaTime;
            if (left <= 0f) _struck.Remove(victim);
            else _struck[victim] = left;
        }

        var impact = false;
        var atRest = true;
        // Pieces added by a snap-off during this loop are advanced from the next update
        var count = _parts.Count;
        for (var i = 0; i < count; i++)
        {
            var part = _parts[i];
            part.Body.Advance(deltaTime);
            impact |= part.Body.LastImpactSpeed >= SimulationConfig.TreeFellImpactSpeed;
            if (i == 0)
                SnapOffBranches(part);
        }
        // Whatever still shifts a while after the first hard impact is frozen where it lies
        if (_landed)
            _sinceLanding += deltaTime;
        if (_sinceLanding >= SimulationConfig.TreeFellSettleCapSeconds)
        {
            foreach (var part in _parts)
                part.Body.Freeze();
        }
        foreach (var part in _parts)
            atRest &= part.Body.AtRest;

        if (!_landed && (impact || atRest))
        {
            _landed = true;
            _landingPending = true;
        }
        if (!atRest) return;

        _restElapsed += deltaTime;
        if (_restElapsed >= SimulationConfig.TreeFellRestSeconds)
            IsDone = true;
    }

    /// <summary>A blow the falling tree deals: which way it was moving (horizontally) and how fast.</summary>
    public readonly record struct Strike(Vector3 Direction, float Speed);

    /// <summary>
    /// Whether moving wood of the tree overlaps the box <paramref name="min"/>..<paramref name="max"/>
    /// fast enough to hurt (<see cref="SimulationConfig.TreeHitMinSpeed"/>), and the fastest such blow.
    /// A <paramref name="victim"/> that was just hit is skipped until
    /// <see cref="SimulationConfig.TreeHitCooldownSeconds"/> has passed, so one fall hits once per
    /// swing. Pieces at rest hurt nobody.
    /// </summary>
    public bool TryStrike(object victim, Vector3 min, Vector3 max, out Strike strike)
    {
        strike = default;
        if (IsDone || _struck.ContainsKey(victim)) return false;

        var middle = (min + max) * 0.5f;
        var best = SimulationConfig.TreeHitMinSpeed;
        var found = false;
        foreach (var part in _parts)
        {
            var body = part.Body;
            if (body.AtRest) continue;
            var nearest = Vector3.Clamp(body.Center, min, max);
            if (Vector3.DistanceSquared(nearest, body.Center) > body.Radius * body.Radius) continue;

            for (var k = 0; k < part.Blocks.Count; k++)
            {
                var half = _hitHalf[part.Blocks[k]];
                if (half <= 0f) continue;
                var at = body.PointNow(k);
                if (at.X < min.X - half || at.X > max.X + half || at.Y < min.Y - half || at.Y > max.Y + half
                    || at.Z < min.Z - half || at.Z > max.Z + half)
                    continue;

                var velocity = body.VelocityAt(at);
                var speed = velocity.Length();
                if (speed < best) continue;
                best = speed;
                found = true;
                var sideways = new Vector3(velocity.X, 0f, velocity.Z);
                if (sideways.LengthSquared() < 1e-4f)
                    sideways = new Vector3(middle.X - at.X, 0f, middle.Z - at.Z);
                strike = new Strike(sideways.NormalizeHorizontalOr(Vector3.UnitX), speed);
            }
        }

        if (found)
            _struck[victim] = SimulationConfig.TreeHitCooldownSeconds;
        return found;
    }

    /// <summary>Health a blow of <paramref name="speed"/> (blocks/s) takes off: grows with speed up to
    /// <see cref="SimulationConfig.TreeHitMaxDamage"/>, 0 below the minimum speed.</summary>
    public static int StrikeDamage(float speed) =>
        speed < SimulationConfig.TreeHitMinSpeed
            ? 0
            : Math.Min(SimulationConfig.TreeHitMaxDamage, (int)MathF.Ceiling(speed * SimulationConfig.TreeHitDamagePerSpeed));

    // A side branch that struck the ground hard breaks off whole and carries on as its own body
    // with the speed it had; the rest of the tree is rebuilt without it
    private void SnapOffBranches(FellPart part)
    {
        if (part.Body.HardImpacts.Count == 0) return;

        var roots = new List<int>();
        foreach (var local in part.Body.HardImpacts)
        {
            var root = _branches.BranchRootOf(part.Blocks[local]);
            if (root >= 0 && !roots.Contains(root) && _partOf[root] == part)
                roots.Add(root);
        }
        if (roots.Count == 0) return;

        var parentBody = part.Body;
        var remaining = part.Blocks;
        var broken = new List<List<int>>();
        foreach (var root in roots)
        {
            var members = new List<int>();
            _branches.Members(root, members);
            members.RemoveAll(i => _partOf[i] != part);
            if (members.Count == 0) continue;
            foreach (var i in members)
                _partOf[i] = null;
            broken.Add(members);
        }
        if (broken.Count == 0) return;

        remaining = remaining.FindAll(i => _partOf[i] == part);
        if (remaining.Count == 0)
        {
            foreach (var members in broken)
                foreach (var i in members)
                    _partOf[i] = part;
            return;
        }

        foreach (var members in broken)
        {
            var child = new FellPart(_pivot, members, NewBody(members, false, parentBody), MeshOf(members));
            _parts.Add(child);
            foreach (var i in members)
                _partOf[i] = child;
        }
        part.Rebuild(remaining, NewBody(remaining, parentBody.Hinged, parentBody), MeshOf(remaining));
    }

    private List<ChunkMeshData> MeshOf(List<int> indices)
    {
        var blocks = new List<FellBlock>(indices.Count);
        foreach (var i in indices)
            blocks.Add(_blocks[i]);
        return BuildMeshData(blocks, _pivot);
    }

    /// <summary>True exactly once: on the first call after the update in which the tree first hit the
    /// ground hard (or came to rest without having), the world's thud: shake, leaf burst and dust.</summary>
    public bool TryTakeLanding()
    {
        if (!_landingPending) return false;
        _landingPending = false;
        return true;
    }

    /// <summary>
    /// Appends every collected block to <paramref name="into"/> at the cell it lies in now (at rest,
    /// the cells <see cref="TakeDrops"/> drops its loot at), keeping its id and state, so the thud
    /// knows where the trunk and the canopy came down.
    /// </summary>
    public void GatherLanded(List<FellBlock> into)
    {
        foreach (var part in _parts)
        {
            for (var k = 0; k < part.Blocks.Count; k++)
                into.Add(_blocks[part.Blocks[k]] with { Cell = RestCell(part, k) });
        }
    }

    // The floored cell the part's k-th block has its center in now
    private static Vector3 RestCell(FellPart part, int k) => part.Body.PointNow(k).Floor();

    /// <summary>
    /// Item drops for the landed tree, each at the floored cell its block came to rest in.
    /// Every block rolls its own loot table through <see cref="DropManager.RollLoot"/>, so a
    /// felled canopy yields exactly what mining or blasting it would. Single-use: the second
    /// call returns nothing. A tree felled in creative mode yields nothing.
    /// </summary>
    public List<(ushort Id, Vector3 Cell)> TakeDrops()
    {
        var drops = new List<(ushort, Vector3)>();
        if (_dropsTaken)
            return drops;
        _dropsTaken = true;

        if (!_drops)
            return drops;

        var rolled = new List<ushort>();
        foreach (var part in _parts)
        {
            for (var k = 0; k < part.Blocks.Count; k++)
            {
                var block = _blocks[part.Blocks[k]];
                var cell = RestCell(part, k);

                // The resting cell is the loot context too, so position-dependent rolls see where
                // the block landed rather than where it grew
                rolled.Clear();
                DropManager.RollLoot(block.Id, new LootContext((int)cell.X, (int)cell.Y, (int)cell.Z, State: block.State), rolled);

                foreach (var id in rolled)
                    drops.Add((id, cell));
            }
        }

        return drops;
    }

    /// <summary>Uploads the baked meshes of every piece on first render (GL is only legal on the main thread).</summary>
    public void EnsureMesh(GL gl)
    {
        foreach (var part in _parts)
            part.EnsureMesh(gl);
    }

    public void Dispose()
    {
        foreach (var part in _parts)
            part.Dispose();
        GC.SuppressFinalize(this);
    }
}
