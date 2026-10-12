using System;
using System.Collections.Generic;

namespace VoxelEngine.WorldGen.Trees.Shape;

/// <summary>
/// Turns a grown <see cref="TreeGraph"/> into the cells it writes: wood with its
/// <see cref="WoodState"/> (thickness code from the displayed diameter, parent direction from
/// the graph, <see cref="WoodState.ParentRoot"/> on the root), the shells of a wide trunk, and
/// leaves from <see cref="LeafCluster"/> with their <see cref="LeafState"/> hydration. Pure and
/// stateless.
/// <para>
/// <b>Wide trunks.</b> A trunk-chain node (<see cref="TrunkChain"/>: the root, then straight up)
/// whose diameter gives a <see cref="BlockRadius"/> of at least 1 becomes a <b>core</b> (the graph
/// node itself, a full cube) plus <b>shell</b> cells around it at the same Y, laid out by the
/// species' <see cref="TrunkShape"/> (<see cref="InFootprint"/>). A shell is a full cube with the
/// <see cref="WoodState"/> shell bit set and its parent one step toward the core
/// (<see cref="ShellParentDir"/>), so a diagonal shell chains through an edge shell. Only the
/// trunk chain grows shells: every core sits at its own Y in one column, so two footprints never
/// overlap and the shells are a function of the node set, never of index order. A wide limb
/// elsewhere renders as a 16 px full cube.
/// </para>
/// <para>
/// <b>Output order</b> (load-bearing): <c>cells[0..graph.Count)</c> are the graph nodes in index
/// order (growth nodes, then feature nodes such as roots), then the shells, which end the wood at
/// <c>woodCount</c>, then the leaves. A species that never grows past 16 px emits no shell and
/// rasterises exactly as before shells existed.
/// </para>
/// </summary>
public static class TreeRasterizer
{
    // Initialisation order matters: MaxShellRadius is computed from the unclamped helper before
    // the offset tables (built in the static ctor) read it.

    /// <summary>The largest shell radius any species can reach: <see cref="BlockRadius"/> of the compiler's trunk cap.</summary>
    public static readonly int MaxShellRadius = RawBlockRadius(TreeSpeciesCompiler.MaxTrunkPx);

    // [shape][r] -> offsets, r = 1..MaxShellRadius (index 0 unused)
    private static readonly ShellOffset[][][] Tables;

    static TreeRasterizer()
    {
        var shapes = Enum.GetValues<TrunkShape>();
        var max = 0;
        foreach (var s in shapes)
            max = Math.Max(max, (int)s);
        Tables = new ShellOffset[max + 1][][];
        foreach (var s in shapes)
        {
            var perRadius = new ShellOffset[MaxShellRadius + 1][];
            perRadius[0] = Array.Empty<ShellOffset>();
            for (var r = 1; r <= MaxShellRadius; r++)
            {
                var list = new List<ShellOffset>();
                for (var dx = -r; dx <= r; dx++)
                for (var dz = -r; dz <= r; dz++)
                {
                    if ((dx == 0 && dz == 0) || !InFootprint(s, r, dx, dz))
                        continue;
                    list.Add(new ShellOffset(dx, dz, ShellParentDir(dx, dz)));
                }
                // Parent before child: a parent is one Manhattan step closer to the core
                list.Sort(static (a, b) =>
                {
                    var c = (Math.Abs(a.Dx) + Math.Abs(a.Dz)).CompareTo(Math.Abs(b.Dx) + Math.Abs(b.Dz));
                    if (c != 0) return c;
                    c = a.Dx.CompareTo(b.Dx);
                    return c != 0 ? c : a.Dz.CompareTo(b.Dz);
                });
                perRadius[r] = list.ToArray();
            }
            Tables[(int)s] = perRadius;
        }
    }

    /// <summary>
    /// Thickness code of a diameter: <c>ceil(px) - 1</c>, clamped to 0..<see cref="WoodState.FullCube"/>.
    /// Anything over 16 px is a full cube; a wider trunk-chain node becomes a core plus shells
    /// (<see cref="BlockRadius"/>). Monotone in <paramref name="px"/>.
    /// </summary>
    public static int ThicknessCode(float px) => Math.Clamp((int)MathF.Ceiling(px) - 1, 0, WoodState.FullCube);

    /// <summary>The wood state byte of a node (never a shell: shells are not graph nodes).</summary>
    public static byte WoodStateOf(in TreeGraph.Node n) =>
        WoodState.Encode(ThicknessCode(n.ThicknessPx), n.Parent < 0 ? WoodState.ParentRoot : n.ParentDir, false);

    // Unclamped radius: the (2R+1)-block footprint never exceeds the diameter, and nothing at or
    // under one block has shells.
    private static int RawBlockRadius(float d) =>
        d <= WoodState.PixelsPerBlock ? 0 : Math.Max(0, (int)MathF.Floor((d / WoodState.PixelsPerBlock - 1f) / 2f));

    /// <summary>
    /// The shell radius in blocks of a core of diameter <paramref name="d"/> px: the largest R
    /// whose (2R+1)-block footprint fits inside the diameter, clamped to <see cref="MaxShellRadius"/>.
    /// 0 means no shells. Monotone in <paramref name="d"/>.
    /// </summary>
    public static int BlockRadius(float d) => Math.Min(RawBlockRadius(d), MaxShellRadius);

    /// <summary>The smallest diameter in pixels whose <see cref="BlockRadius"/> is <paramref name="r"/>.</summary>
    public static float MinDiameterPx(int r) => (2 * r + 1) * WoodState.PixelsPerBlock;

    /// <summary>
    /// Whether the offset from a core lies in its footprint of radius <paramref name="r"/>: a
    /// Chebyshev square for <see cref="TrunkShape.Square"/>, the block-resolution circle
    /// <c>dx² + dz² &lt;= r² + r</c> for <see cref="TrunkShape.Circle"/>.
    /// </summary>
    public static bool InFootprint(TrunkShape s, int r, int dx, int dz) => s == TrunkShape.Square
        ? Math.Max(Math.Abs(dx), Math.Abs(dz)) <= r
        : dx * dx + dz * dz <= r * r + r;

    /// <summary>
    /// The parent direction of a shell at offset (<paramref name="dx"/>, <paramref name="dz"/>)
    /// from its core: one step along X toward the core when <c>|dx| &gt;= |dz|</c>, otherwise one
    /// step along Z. Not defined for the core itself.
    /// </summary>
    public static int ShellParentDir(int dx, int dz)
    {
        int sx = 0, sz = 0;
        if (Math.Abs(dx) >= Math.Abs(dz))
            sx = -Math.Sign(dx);
        else
            sz = -Math.Sign(dz);
        for (var dir = 0; dir < TreeDirections.Count; dir++)
        {
            if (TreeDirections.Dx[dir] == sx && TreeDirections.Dy[dir] == 0 && TreeDirections.Dz[dir] == sz)
                return dir;
        }
        throw new ArgumentException($"Offset ({dx}, {dz}) is the core itself and has no shell parent.");
    }

    /// <summary>
    /// Every footprint offset of radius <paramref name="r"/> except the core, sorted by Manhattan
    /// length, then dx, then dz, so a shell's parent always comes before it. Empty for r &lt;= 0.
    /// </summary>
    public static ReadOnlySpan<ShellOffset> ShellOffsets(TrunkShape s, int r)
    {
        if (r <= 0)
            return ReadOnlySpan<ShellOffset>.Empty;
        return Tables[(int)s][Math.Min(r, MaxShellRadius)];
    }

    /// <summary>
    /// The trunk chain into <paramref name="dst"/> (cleared first): the root, then the child one
    /// step up of each, while there is one. The only nodes that grow shells.
    /// </summary>
    public static void TrunkChain(TreeGraph graph, List<int> dst)
    {
        dst.Clear();
        if (graph.Count == 0)
            return;
        for (var node = TreeGraph.Root; node >= 0; node = graph.ChildIn(node, TreeDirections.Up))
            dst.Add(node);
    }

    /// <summary>Rasterises the tree; see <see cref="Rasterize(TreeGraph, TreeSpecies, ITreeSpace, out int)"/>.</summary>
    public static List<TreeCell> Rasterize(TreeGraph graph, TreeSpecies species, ITreeSpace space)
        => Rasterize(graph, species, space, out _);

    /// <summary>
    /// Rasterises the tree in a fixed order: every node in index order as wood (growth nodes
    /// first, then feature nodes such as roots, since features append after growth), then the
    /// shells of the wide trunk-chain nodes, then the leaves. <paramref name="woodCount"/> is the
    /// number of wood cells (nodes plus shells), where the leaves begin.
    /// <para>
    /// A shell is skipped when the graph holds its cell, when it was already emitted, when its
    /// parent cell is neither the core, an emitted shell nor a full-cube feature node at the
    /// core's Y (a buttress fin), or when the space does not <see cref="ITreeSpace.AcceptsShell"/>.
    /// </para>
    /// </summary>
    public static List<TreeCell> Rasterize(TreeGraph graph, TreeSpecies species, ITreeSpace space, out int woodCount)
    {
        var output = new List<TreeCell>(graph.Count * 8);
        for (int i = 0; i < graph.Count; i++)
        {
            ref readonly var n = ref graph[i];
            output.Add(new TreeCell(n.X, n.Y, n.Z, species.WoodId, WoodStateOf(in n)));
        }

        // Stays null for a tree with no wide core, so thin species pay nothing and emit as before
        HashSet<long>? shellKeys = null;
        if (graph.Count > 0 && RawBlockRadius(species.MaxPx) > 0)
        {
            for (var node = TreeGraph.Root; node >= 0; node = graph.ChildIn(node, TreeDirections.Up))
            {
                ref readonly var core = ref graph[node];
                var r = BlockRadius(core.ThicknessPx);
                if (r <= 0)
                    continue;

                foreach (var o in ShellOffsets(species.TrunkShape, r))
                {
                    int x = core.X + o.Dx, y = core.Y, z = core.Z + o.Dz;
                    if (graph.Occupies(x, y, z))
                        continue;
                    var key = TreeCellKey.Pack(x, y, z);
                    if (shellKeys != null && shellKeys.Contains(key))
                        continue;

                    int px = x + TreeDirections.Dx[o.ParentDir], pz = z + TreeDirections.Dz[o.ParentDir];
                    if (!ParentHolds(graph, shellKeys, in core, px, y, pz))
                        continue;
                    if (!space.AcceptsShell(x, y, z))
                        continue;

                    output.Add(new TreeCell(x, y, z, species.WoodId,
                        WoodState.Encode(WoodState.FullCube, o.ParentDir, true)));
                    (shellKeys ??= new HashSet<long>()).Add(key);
                }
            }
        }

        woodCount = output.Count;
        LeafCluster.Collect(graph, species, space, output, shellKeys);
        return output;
    }

    // Whether a shell's parent cell carries it: the core, an emitted shell, or a full-cube
    // feature node at the core's Y (a buttress fin standing where an on-axis shell would).
    private static bool ParentHolds(TreeGraph graph, HashSet<long>? shellKeys, in TreeGraph.Node core, int px, int py, int pz)
    {
        if (px == core.X && pz == core.Z)
            return true;
        if (shellKeys != null && shellKeys.Contains(TreeCellKey.Pack(px, py, pz)))
            return true;
        var at = graph.NodeAt(px, py, pz);
        if (at < 0)
            return false;
        ref readonly var p = ref graph[at];
        return p.IsFeature && p.Y == core.Y && ThicknessCode(p.ThicknessPx) == WoodState.FullCube;
    }

    /// <summary>
    /// The box every rasterised cell of the species stays inside, relative to the root:
    /// horizontally the signal energy (no growth node is further than that from the root) plus
    /// the larger of the leaf radius and the trunk's <see cref="BlockRadius"/> (shells stand
    /// around the trunk chain only, which never leaves the root column); upward the signal
    /// energy plus the leaf radius; downward the larger of the leaf radius and the roots
    /// feature's depth; a buttress species also covers its fins, which run at most
    /// <see cref="WorldGenerationConfig.TreeButtressMaxReach"/> past the trunk's widest footprint
    /// along the ground. Relies on the grower never descending (<c>TreeGrower.MaskBlocked</c>);
    /// phase 3 reads it for the chunk-border margin.
    /// </summary>
    public static (int Horizontal, int Up, int Down) ReachOf(TreeSpecies species)
    {
        int leaf = species.HasLeaves ? species.LeafRadius : 0;
        int roots = 0;
        bool buttress = false;
        for (int i = 0; i < species.Features.Count; i++)
        {
            if (species.Features[i].Feature.Name == "roots")
                roots = Math.Max(roots, species.Features[i].Depth);
            else if (species.Features[i].Feature.Name == "buttress")
                buttress = true;
        }
        int up = species.SignalEnergy + leaf;
        int horizontal = species.SignalEnergy + Math.Max(leaf, BlockRadius(species.MaxPx));
        if (buttress)
            horizontal = Math.Max(horizontal, BlockRadius(species.MaxPx) + WorldGenerationConfig.TreeButtressMaxReach);
        return (horizontal, up, Math.Max(leaf, roots));
    }
}
