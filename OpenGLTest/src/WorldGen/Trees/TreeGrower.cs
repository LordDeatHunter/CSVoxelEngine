using System;

namespace silkgltriangle;

/// <summary>
/// The headless growth engine (Dynamic Trees style). A <see cref="Pulse(TreeGraph, TreeSpecies,
/// ITreeSpace, SplitMix64, int)"/> walks one signal up from the root, choosing a direction per
/// step from weighted faces the species' <see cref="IGrowthLogic"/> reshapes, follows an existing
/// child or grows exactly one new face-adjacent node into a free cell, and re-thickens the path
/// back down with the pipe model. <see cref="GrowToMaturity(TreeSpecies, int, int, int,
/// ITreeSpace, int)"/> seeds the stream from the root cell, the world seed and the species and
/// runs the pulses, the final thickening and the species' features. Pure: no static mutable
/// state, every random draw comes from the <see cref="SplitMix64"/> passed in.
/// </summary>
public static class TreeGrower
{
    /// <summary>
    /// How strongly a pulse follows an existing limb: a child's weight grows by its
    /// <see cref="TreeGraph.Node.PipePx"/> times this. It scales the girth-free pipe value, so
    /// the girth profile never steers growth. A tuning knob for every species at once, never
    /// per species.
    /// </summary>
    private const float FollowScale = 0.25f;

    /// <summary>
    /// The stream seed of the tree rooted at the cell: position, world seed and an FNV-1a hash of
    /// the species name, so two species at one cell grow differently.
    /// </summary>
    public static ulong SeedFor(int x, int y, int z, int worldSeed, TreeSpecies species) =>
        SplitMix64.Mix(SplitMix64.Hash(x, z, worldSeed)
                       + SplitMix64.Mix(((ulong)(uint)y << 32) | WorldSeedParser.Fnv1a(species.Name)));

    /// <summary>One pulse starting with the species' full <see cref="TreeSpecies.SignalEnergy"/>.</summary>
    public static bool Pulse(TreeGraph graph, TreeSpecies species, ITreeSpace space, SplitMix64 rng) =>
        Pulse(graph, species, space, rng, species.SignalEnergy);

    /// <summary>
    /// Sends one growth signal with <paramref name="energy"/> up from the root. Each step costs
    /// one energy (a turn may cost more, see <see cref="IGrowthLogic.EnergyAfterTurn"/>); the
    /// signal follows an existing child or grows one new node and stops. Returns whether a node
    /// was added. A pulse adds at most one node, and a non-feature node's step distance from the
    /// root never exceeds the starting energy, which bounds the tree's reach.
    /// </summary>
    public static bool Pulse(TreeGraph graph, TreeSpecies species, ITreeSpace space, SplitMix64 rng, int energy)
    {
        var node = TreeGraph.Root;
        var incoming = TreeDirections.Up;
        var turns = 0;
        Span<float> w = stackalloc float[TreeDirections.Count];

        while (energy > 0)
        {
            int nx, ny, nz;
            {
                ref readonly var n = ref graph[node];
                nx = n.X;
                ny = n.Y;
                nz = n.Z;
            }
            var signal = new GrowthSignal(graph, species, space, node, nx, ny, nz, incoming, energy, turns);

            FillBaseWeights(in signal, w);
            species.Logic.AdjustWeights(in signal, w);
            MaskBlocked(in signal, w);

            var dir = PickWeighted(w, rng);
            if (dir < 0)
                return false;

            energy--;
            if (dir != incoming)
            {
                turns++;
                // A turn may cost energy but never refund it: a logic's floor (Max(1, ...)) at 0
                // energy would otherwise let a later pulse grow past the starting energy.
                energy = Math.Clamp(species.Logic.EnergyAfterTurn(in signal, dir, energy), 0, energy);
            }

            var child = graph.ChildIn(node, dir);
            if (child >= 0)
            {
                node = child;
                incoming = dir;
                continue;
            }

            graph.AddChild(node, dir, species.TipPx);
            ThickenPath(graph, species, node);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The base weights before the logic: sides 1, up <see cref="TreeSpecies.UpProbability"/>,
    /// down 0, the travel direction reinforced, existing children favoured by their pipe
    /// diameter (Dynamic Trees' probabilityForBlock), and no forks off the trunk below
    /// <see cref="TreeSpecies.LowestBranchHeight"/>.
    /// </summary>
    private static void FillBaseWeights(in GrowthSignal s, Span<float> w)
    {
        for (var d = 0; d < TreeDirections.Up; d++)
            w[d] = 1f;
        w[TreeDirections.Up] = s.Species.UpProbability;
        w[TreeDirections.Down] = 0f;
        w[s.IncomingDir] += 1f;

        for (var d = 0; d < TreeDirections.Count; d++)
        {
            var child = s.Graph.ChildIn(s.Node, d);
            if (child >= 0)
                w[d] += s.Graph[child].PipePx * FollowScale;
        }

        if (s.IsInTrunk && s.Height < s.Species.LowestBranchHeight)
        {
            for (var d = 0; d < TreeDirections.Up; d++)
                w[d] = 0f;
        }
    }

    /// <summary>
    /// Zeroes every direction the signal may not take, after the logic ran: down (in phase 2 a
    /// signal never descends; features grow downward), straight back, and every new cell the
    /// graph already holds or the space reports blocked. Negative weights clamp to 0.
    /// </summary>
    private static void MaskBlocked(in GrowthSignal s, Span<float> w)
    {
        w[TreeDirections.Down] = 0f;
        w[TreeDirections.Opposite(s.IncomingDir)] = 0f;

        for (var d = 0; d < TreeDirections.Count; d++)
        {
            if (!(w[d] > 0f))
            {
                w[d] = 0f;
                continue;
            }
            if (s.Graph.ChildIn(s.Node, d) >= 0)
                continue;

            var x = s.NodeX + TreeDirections.Dx[d];
            var y = s.NodeY + TreeDirections.Dy[d];
            var z = s.NodeZ + TreeDirections.Dz[d];
            if (s.Graph.Occupies(x, y, z) || !s.Space.IsFree(x, y, z))
                w[d] = 0f;
        }
    }

    /// <summary>
    /// Draws an index with probability proportional to its weight, or -1 (without drawing) when
    /// every weight is 0. Exactly one <see cref="SplitMix64.NextFloat()"/> per successful call.
    /// </summary>
    internal static int PickWeighted(ReadOnlySpan<float> w, SplitMix64 rng)
    {
        var total = 0f;
        for (var i = 0; i < w.Length; i++)
            total += w[i];
        if (!(total > 0f))
            return -1;

        var r = rng.NextFloat() * total;
        var acc = 0f;
        var last = -1;
        for (var i = 0; i < w.Length; i++)
        {
            if (!(w[i] > 0f))
                continue;
            acc += w[i];
            if (acc > r)
                return i;
            last = i;
        }
        // Float rounding can leave r at the very top of the range.
        return last;
    }

    /// <summary>
    /// The species' girth multiplier at a relative height, piecewise-linear over
    /// <see cref="TreeSpecies.GirthProfile"/> and clamped to its first/last multiplier outside it.
    /// </summary>
    public static float Girth(TreeSpecies species, float height01)
    {
        var profile = species.GirthProfile;
        if (profile.Count == 0)
            return 1f;
        if (height01 <= profile[0].Height01)
            return profile[0].Multiplier;

        for (var i = 1; i < profile.Count; i++)
        {
            var (h1, m1) = profile[i];
            if (height01 <= h1)
            {
                var (h0, m0) = profile[i - 1];
                var t = (height01 - h0) / (h1 - h0);
                return m0 + (m1 - m0) * t;
            }
        }
        return profile[profile.Count - 1].Multiplier;
    }

    /// <summary>
    /// Recomputes one node's diameters from its children. Three guarantees: the pipe value is
    /// monotone (parent >= child, since a sum of positive powers is at least any one term) and
    /// girth-free, so the girth profile is applied exactly once per node and never compounds up
    /// the trunk; the displayed thickness lies in [<see cref="TreeSpecies.TipPx"/>,
    /// <see cref="TreeSpecies.MaxPx"/>] because the floor comes after the multiply; and the final
    /// max keeps a parent never thinner than any child.
    /// <para>
    /// Feature nodes (roots) keep the thickness their feature gave them and are ignored by their
    /// parent, and the result never drops below the node's <see cref="TreeGraph.Node.FloorPx"/>.
    /// Features apply after <see cref="GrowToMaturity(TreeSpecies, int, int, int, ITreeSpace,
    /// int)"/>'s final <see cref="Thicken"/> and every floor is 0 there, so worldgen output is
    /// bit-identical; both rules only matter for graphs rebuilt from the world.
    /// </para>
    /// </summary>
    internal static void Recompute(TreeGraph graph, TreeSpecies species, int i)
    {
        ref readonly var n = ref graph[i];
        if (n.IsFeature)
            return;

        var floor = n.FloorPx;
        var sumPipe = 0f;
        var maxChildThick = 0f;
        var nonFeatureChildren = 0;
        for (var c = n.FirstChild; c >= 0; c = graph[c].NextSibling)
        {
            ref readonly var child = ref graph[c];
            if (child.IsFeature)
                continue;
            nonFeatureChildren++;
            sumPipe += MathF.Pow(child.PipePx, species.PipeExponent);
            maxChildThick = MathF.Max(maxChildThick, child.ThicknessPx);
        }

        float tip = species.TipPx;
        var pipe = nonFeatureChildren == 0 ? tip : MathF.Max(tip, MathF.Pow(sumPipe, 1f / species.PipeExponent));
        var h01 = Math.Clamp((float)(n.Y - graph.RootY) / Math.Max(1, graph.MaxY - graph.RootY), 0f, 1f);
        var thick = MathF.Min(species.MaxPx, MathF.Max(tip, pipe * Girth(species, h01)));
        thick = MathF.Max(thick, maxChildThick);
        thick = MathF.Max(thick, floor);
        graph.SetThickness(i, pipe, thick);
    }

    /// <summary>Recomputes <paramref name="from"/> and every node up its parent chain to the root.</summary>
    public static void ThickenPath(TreeGraph graph, TreeSpecies species, int from)
    {
        for (var i = from; i >= 0; i = graph[i].Parent)
            Recompute(graph, species, i);
    }

    /// <summary>
    /// Recomputes every node bottom-up: a reverse index sweep is a post-order, because children
    /// always index above their parents.
    /// </summary>
    public static void Thicken(TreeGraph graph, TreeSpecies species)
    {
        for (var i = graph.Count - 1; i >= 0; i--)
            Recompute(graph, species, i);
    }

    /// <summary>Grows a mature tree under the current <see cref="WorldGenerationConfig.Seed"/>.</summary>
    public static TreeGraph GrowToMaturity(TreeSpecies species, int x, int y, int z, ITreeSpace space) =>
        GrowToMaturity(species, x, y, z, space, WorldGenerationConfig.Seed);

    /// <summary>
    /// Grows the tree rooted at (x, y, z) to maturity, deterministically from
    /// <see cref="SeedFor"/>: the step count and the per-tree energy are rolled from that stream,
    /// then the pulses run, one final bottom-up <see cref="Thicken"/> lets the girth profile see
    /// the final height, and the species' features apply last. The caller supplies a free base
    /// cell; it is not checked.
    /// </summary>
    public static TreeGraph GrowToMaturity(TreeSpecies species, int x, int y, int z, ITreeSpace space, int worldSeed)
    {
        var rng = new SplitMix64(SeedFor(x, y, z, worldSeed, species));
        var (steps, energy) = RollMaturity(species, rng);

        var graph = new TreeGraph(x, y, z, species.TipPx);
        for (var s = 0; s < steps; s++)
            Pulse(graph, species, space, rng, energy);

        Thicken(graph, species);

        var features = species.Features;
        for (var f = 0; f < features.Count; f++)
            features[f].Feature.Apply(graph, species, space, features[f].Depth, rng);
        return graph;
    }

    /// <summary>
    /// The first two draws of a tree's stream: the maturity step count, then the per-tree energy
    /// clamped to 1..<see cref="TreeSpecies.SignalEnergy"/>, in exactly that order.
    /// </summary>
    private static (int Steps, int Energy) RollMaturity(TreeSpecies species, SplitMix64 rng)
    {
        var steps = rng.NextInt(species.MaturityStepsMin, species.MaturityStepsMax + 1);
        var energy = Math.Clamp(species.Logic.TreeEnergy(species, rng), 1, species.SignalEnergy);
        return (steps, energy);
    }

    /// <summary>
    /// The per-tree energy a worldgen tree rooted at (x, y, z) grew with: a replay of the energy
    /// roll from <see cref="SeedFor"/>'s stream, so reactive growth keeps an existing tree's
    /// reach and a planted sapling gets the energy a worldgen tree on that cell would have had.
    /// </summary>
    public static int TreeEnergyAt(TreeSpecies species, int x, int y, int z, int worldSeed) =>
        RollMaturity(species, new SplitMix64(SeedFor(x, y, z, worldSeed, species))).Energy;

    /// <summary>
    /// Grows an existing graph further (reactive growth): a <see cref="Thicken"/> first, which
    /// rebuilds the pipe values from the topology for a graph seeded from integer diameters, then
    /// up to <paramref name="steps"/> pulses with <paramref name="energy"/>, stopping once the
    /// graph holds <paramref name="maxNodes"/>, then a final <see cref="Thicken"/>. Only appends
    /// nodes, and runs no features. Returns the number of nodes added.
    /// </summary>
    public static int GrowFurther(TreeGraph graph, TreeSpecies species, ITreeSpace space, SplitMix64 rng,
        int energy, int steps, int maxNodes)
    {
        var countBefore = graph.Count;
        Thicken(graph, species);
        for (var s = 0; s < steps; s++)
        {
            if (graph.Count >= maxNodes)
                break;
            Pulse(graph, species, space, rng, energy);
        }
        Thicken(graph, species);
        return graph.Count - countBefore;
    }
}
