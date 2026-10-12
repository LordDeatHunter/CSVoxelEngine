using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.ItemDebugger;

/// <summary>
/// GL-free state behind the item debugger's TREES tab: which <see cref="TreeSpecies"/> is
/// previewed, at which growth step and from which seed. The selected species is grown pulse by
/// pulse (<see cref="TreeGrower.GrowSteps"/>) over flat ground at an explicit seed — never the
/// global <see cref="WorldSeed"/> — so scrubbing the step back and forth always shows the same
/// tree at the same step. Each step is regrown from scratch in a fresh
/// <see cref="VirtualTreeSpace"/> and cached per step, so nothing ever mutates a shared graph.
/// Features (the buttress) apply only from the rolled maturity step on, which makes the mature
/// step exactly the worldgen tree; the roots feature never grows here, since flat ground has no
/// carved cells for it to reach into. Snapshots <see cref="TreeSpeciesRegistry"/> on
/// construction, so build it only after content is loaded.
/// </summary>
public sealed class TreePreviewState
{
    /// <summary>World X of the previewed tree's root column.</summary>
    public const int RootX = 0;

    /// <summary>World Z of the previewed tree's root column.</summary>
    public const int RootZ = 0;

    /// <summary>The flat ground's top solid block; the root stands at <c>GroundY + 1</c>.</summary>
    public const int GroundY = 0;

    /// <summary>Ground cells laid around the framing footprint on every side.</summary>
    public const int GroundMargin = 3;

    /// <summary>Slack multiplied onto the exact fit distance, so the tree never touches the frame.</summary>
    public const float FramingMargin = 1.1f;

    /// <summary>Prefix that pins a name lookup to the species namespace (<c>tree/cactus</c>).</summary>
    public const string NamePrefix = "tree/";

    private sealed record Snapshot(List<TreeCell> Cells, int WoodCount, Vector3 Min, Vector3 Max);

    // Instance state, cleared on every species or seed change; never static, never a
    // GenerationalCache, since the preview owns its own seed.
    private readonly Dictionary<int, Snapshot> _snapshots = new();
    private int _maturitySteps;

    private int _groundVersion = -1;
    private List<TreeCell> _ground = new();
    private List<TreeCell> _meshCells = new();

    public TreePreviewState(int seed = WorldSeed.Default)
    {
        var entries = new List<TreeSpecies>(TreeSpeciesRegistry.All);
        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Entries = entries;
        Seed = seed;
        SelectedIndex = -1;
        if (entries.Count > 0) Select(0);
    }

    /// <summary>Every registered species, sorted by file name.</summary>
    public IReadOnlyList<TreeSpecies> Entries { get; }

    /// <summary>Index into <see cref="Entries"/> of the previewed species; -1 when there is none.</summary>
    public int SelectedIndex { get; private set; }

    /// <summary>The previewed species, or null when no species is registered.</summary>
    public TreeSpecies? Selected => SelectedIndex >= 0 ? Entries[SelectedIndex] : null;

    /// <summary>The seed the tree grows from; instance state only.</summary>
    public int Seed { get; private set; }

    /// <summary>The growth step shown, 0..<see cref="MaxStep"/>.</summary>
    public int Step { get; private set; }

    /// <summary>The last step the slider offers: the species' largest maturity roll.</summary>
    public int MaxStep => Selected?.MaturityStepsMax ?? 0;

    /// <summary>The maturity step the worldgen tree would roll at this root and seed.</summary>
    public int MaturitySteps => _maturitySteps;

    /// <summary>Whether the shown step carries the species' features (from maturity on).</summary>
    public bool FeaturesApplied => Step >= MaturitySteps;

    /// <summary>Moves whenever the shown tree may have changed (species, seed or step); the
    /// renderer rebuilds its meshes when it differs from the one it built.</summary>
    public int Version { get; private set; }

    /// <summary>The rasterized tree at <see cref="Step"/>: wood first, then leaves.</summary>
    public IReadOnlyList<TreeCell> Cells => Current.Cells;

    /// <summary>How many of <see cref="Cells"/> are wood (where the leaves begin).</summary>
    public int WoodCount => Current.WoodCount;

    /// <summary>The box of <see cref="Cells"/>, <c>Max</c> exclusive.</summary>
    public (Vector3 Min, Vector3 Max) Bounds => (Current.Min, Current.Max);

    /// <summary>The union of <see cref="Bounds"/> and the <see cref="MaxStep"/> tree's box, so the
    /// camera stays put while the step is scrubbed.</summary>
    public (Vector3 Min, Vector3 Max) FramingBounds
    {
        get
        {
            var current = Current;
            var max = SnapshotAt(MaxStep);
            return (Vector3.Min(current.Min, max.Min), Vector3.Max(current.Max, max.Max));
        }
    }

    /// <summary>The center of <see cref="FramingBounds"/>.</summary>
    public Vector3 FramingCenter
    {
        get
        {
            var (min, max) = FramingBounds;
            return (min + max) * 0.5f;
        }
    }

    /// <summary>Half the diagonal of <see cref="FramingBounds"/>: the sphere the camera fits.</summary>
    public float FramingRadius
    {
        get
        {
            var (min, max) = FramingBounds;
            return (max - min).Length() * 0.5f;
        }
    }

    /// <summary>Grass at <see cref="GroundY"/> over the framing footprint grown by
    /// <see cref="GroundMargin"/>.</summary>
    public IReadOnlyList<TreeCell> GroundCells
    {
        get
        {
            EnsureMeshCells();
            return _ground;
        }
    }

    /// <summary>The cells the renderer meshes: <see cref="GroundCells"/>, then <see cref="Cells"/>.</summary>
    public IReadOnlyList<TreeCell> MeshCells
    {
        get
        {
            EnsureMeshCells();
            return _meshCells;
        }
    }

    /// <summary>Upper-cased file name with underscores as spaces, for the grid and the title.</summary>
    public static string DisplayNameOf(TreeSpecies species) => species.Name.Replace('_', ' ').ToUpperInvariant();

    /// <summary>
    /// The camera distance at which a sphere of <paramref name="radius"/> fits the view on both
    /// axes, times <see cref="FramingMargin"/>.
    /// </summary>
    public static float FitDistance(float radius, float fovYRadians, float aspect)
    {
        var fovX = 2f * MathF.Atan(MathF.Tan(fovYRadians * 0.5f) * aspect);
        return radius / MathF.Sin(MathF.Min(fovYRadians, fovX) * 0.5f) * FramingMargin;
    }

    /// <summary>Previews the species at <paramref name="index"/> (clamped) at its mature step.</summary>
    public void Select(int index)
    {
        if (Entries.Count == 0) return;
        SelectedIndex = Math.Clamp(index, 0, Entries.Count - 1);
        Reset();
        Step = _maturitySteps;
        Version++;
    }

    /// <summary>
    /// Selects the species with that file name (ignoring case, an optional <see cref="NamePrefix"/>
    /// stripped); returns false and changes nothing when none matches.
    /// </summary>
    public bool SelectByName(string name)
    {
        if (name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase))
            name = name[NamePrefix.Length..];
        for (var i = 0; i < Entries.Count; i++)
        {
            if (!string.Equals(Entries[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            Select(i);
            return true;
        }
        return false;
    }

    /// <summary>Shows <paramref name="step"/>, clamped to 0..<see cref="MaxStep"/>.</summary>
    public void SetStep(int step)
    {
        var clamped = Math.Clamp(step, 0, MaxStep);
        if (clamped == Step) return;
        Step = clamped;
        Version++;
    }

    /// <summary>Moves the step by <paramref name="delta"/>, clamped.</summary>
    public void StepBy(int delta) => SetStep(Step + delta);

    /// <summary>Jumps to the rolled maturity step: the worldgen tree.</summary>
    public void Mature() => SetStep(MaturitySteps);

    /// <summary>Grows from the next seed, keeping the step (clamped).</summary>
    public void Regrow() => SetSeed(unchecked(Seed + 1));

    /// <summary>Grows from <paramref name="seed"/>, keeping the step (clamped).</summary>
    public void SetSeed(int seed)
    {
        Seed = seed;
        Reset();
        Step = Math.Clamp(Step, 0, MaxStep);
        Version++;
    }

    private void Reset()
    {
        _snapshots.Clear();
        var species = Selected;
        _maturitySteps = species == null ? 0 : TreeGrower.MaturityStepsAt(species, RootX, GroundY + 1, RootZ, Seed);
    }

    private Snapshot Current => SnapshotAt(Step);

    private Snapshot SnapshotAt(int step)
    {
        if (_snapshots.TryGetValue(step, out var cached)) return cached;
        var snapshot = Build(step);
        _snapshots[step] = snapshot;
        return snapshot;
    }

    private Snapshot Build(int step)
    {
        var rootMin = new Vector3(RootX, GroundY + 1, RootZ);
        var species = Selected;
        if (species == null) return new Snapshot(new List<TreeCell>(), 0, rootMin, rootMin + Vector3.One);

        // A fresh space per snapshot: the rasterizer and the grower must never see another step's cells
        var space = new VirtualTreeSpace(static (_, _) => GroundY);
        var graph = TreeGrower.GrowSteps(species, RootX, GroundY + 1, RootZ, space, Seed, step,
            applyFeatures: step >= _maturitySteps);
        var cells = TreeRasterizer.Rasterize(graph, species, space, out var wood);
        if (cells.Count == 0) return new Snapshot(cells, wood, rootMin, rootMin + Vector3.One);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var c in cells)
        {
            var p = new Vector3(c.X, c.Y, c.Z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p + Vector3.One);
        }
        return new Snapshot(cells, wood, min, max);
    }

    private void EnsureMeshCells()
    {
        if (_groundVersion == Version) return;
        _groundVersion = Version;

        var ground = new List<TreeCell>();
        if (Selected != null)
        {
            var (min, max) = FramingBounds;
            int x0 = (int)MathF.Floor(min.X) - GroundMargin, x1 = (int)MathF.Ceiling(max.X) - 1 + GroundMargin;
            int z0 = (int)MathF.Floor(min.Z) - GroundMargin, z1 = (int)MathF.Ceiling(max.Z) - 1 + GroundMargin;
            for (var x = x0; x <= x1; x++)
            for (var z = z0; z <= z1; z++)
                ground.Add(new TreeCell(x, GroundY, z, TerrainGenerator.SurfaceBlock, 0));
        }
        _ground = ground;

        var mesh = new List<TreeCell>(ground.Count + Cells.Count);
        mesh.AddRange(ground);
        mesh.AddRange(Cells);
        _meshCells = mesh;
    }
}
