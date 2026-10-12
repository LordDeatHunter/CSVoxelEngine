using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.ItemDebugger;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.ItemDebugger;

/// <summary>
/// The item debugger's TREES tab state: deterministic scrubbing (same species, seed and step give
/// the same cells, in any visiting order), the step clamp, the mature step being the worldgen
/// tree, features from maturity on, a new seed leaving the step alone, framing that contains the
/// tree and fits the view, the ground plane, name lookup and the rebuild version. Behavior only:
/// no step counts, cell counts or species literals beyond what the registry holds.
/// </summary>
public sealed class TreePreviewStateTests
{
    // The debugger's projection: 70 degree vertical fov over its 1600x900 window.
    private const float FovY = 70f * MathF.PI / 180f;
    private const float Aspect = 1600f / 900f;

    public TreePreviewStateTests() => TestContent.EnsureLoaded();

    private static IEnumerable<int> SpeciesIndices(TreePreviewState state) => Enumerable.Range(0, state.Entries.Count);

    private static VirtualTreeSpace FlatSpace() => new(static (_, _) => TreePreviewState.GroundY);

    private static bool Inside(TreeCell c, (Vector3 Min, Vector3 Max) box) =>
        c.X >= box.Min.X && c.Y >= box.Min.Y && c.Z >= box.Min.Z &&
        c.X + 1 <= box.Max.X && c.Y + 1 <= box.Max.Y && c.Z + 1 <= box.Max.Z;

    [Fact]
    public void EntriesAreTheRegisteredSpecies()
    {
        var state = new TreePreviewState();
        Assert.NotEmpty(state.Entries);
        Assert.Equal(
            TreeSpeciesRegistry.ByName.Keys.OrderBy(n => n, StringComparer.Ordinal),
            state.Entries.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void TheSameSpeciesSeedAndStepAlwaysGiveTheSameCells()
    {
        var probe = new TreePreviewState();
        foreach (var i in SpeciesIndices(probe))
        {
            probe.Select(i);
            foreach (var step in new[] { probe.MaxStep / 2, probe.MaxStep })
            {
                var a = new TreePreviewState();
                var b = new TreePreviewState();
                foreach (var s in new[] { a, b })
                {
                    s.Select(i);
                    s.SetSeed(4242);
                    s.SetStep(step);
                }
                Assert.Equal(a.Cells, b.Cells);
                Assert.Equal(a.WoodCount, b.WoodCount);
            }
        }
    }

    [Fact]
    public void ScrubbingBackThenForwardReproducesTheTree()
    {
        var state = new TreePreviewState();
        foreach (var i in SpeciesIndices(state))
        {
            state.Select(i);
            int hi = state.MaxStep, lo = state.MaxStep / 3;
            foreach (var (a, b) in new[] { (hi, lo), (lo, hi) })
            {
                state.SetStep(a);
                var recorded = state.Cells.ToList();
                state.SetStep(b);
                state.SetStep(a);
                Assert.Equal(recorded, state.Cells);

                // A fresh state that went straight to the step agrees with the scrubbed one
                var fresh = new TreePreviewState();
                fresh.Select(i);
                fresh.SetStep(a);
                Assert.Equal(recorded, fresh.Cells);
            }
        }
    }

    [Fact]
    public void TheStepIsClamped()
    {
        var state = new TreePreviewState();
        state.SetStep(-5);
        Assert.Equal(0, state.Step);
        state.SetStep(state.MaxStep + 5);
        Assert.Equal(state.MaxStep, state.Step);

        state.StepBy(3);
        Assert.Equal(state.MaxStep, state.Step);
        state.SetStep(0);
        state.StepBy(-3);
        Assert.Equal(0, state.Step);
    }

    [Fact]
    public void SelectingShowsTheMatureWorldgenTree()
    {
        var state = new TreePreviewState();
        foreach (var i in SpeciesIndices(state))
        {
            state.SetStep(0);
            state.Select(i);
            var species = state.Selected!;
            Assert.Equal(state.MaturitySteps, state.Step);
            Assert.True(state.FeaturesApplied);

            var space = FlatSpace();
            var graph = TreeGrower.GrowToMaturity(species, TreePreviewState.RootX, TreePreviewState.GroundY + 1,
                TreePreviewState.RootZ, space, state.Seed);
            Assert.Equal(TreeRasterizer.Rasterize(graph, species, space), state.Cells);
        }
    }

    [Fact]
    public void FeaturesApplyExactlyFromMaturity()
    {
        var state = new TreePreviewState();
        foreach (var i in SpeciesIndices(state))
        {
            state.Select(i);
            if (state.MaturitySteps <= 0) continue;
            state.SetStep(state.MaturitySteps - 1);
            Assert.False(state.FeaturesApplied);
            state.SetStep(state.MaturitySteps);
            Assert.True(state.FeaturesApplied);
        }
    }

    [Fact]
    public void ANewSeedChangesNothingAboutTheStepOrSelection()
    {
        var state = new TreePreviewState();
        var anyDiffers = false;
        foreach (var i in SpeciesIndices(state))
        {
            state.Select(i);
            state.SetStep(state.MaxStep / 2);
            var (step, index, maxStep, seed) = (state.Step, state.SelectedIndex, state.MaxStep, state.Seed);
            var before = state.Cells.ToList();

            state.Regrow();

            Assert.Equal(step, state.Step);
            Assert.Equal(index, state.SelectedIndex);
            Assert.Equal(maxStep, state.MaxStep);
            Assert.NotEqual(seed, state.Seed);
            anyDiffers |= !before.SequenceEqual(state.Cells);
        }
        Assert.True(anyDiffers, "a new seed grew the same tree for every species");
    }

    [Fact]
    public void BoundsContainEveryCell()
    {
        var state = new TreePreviewState();
        foreach (var i in SpeciesIndices(state))
        {
            state.Select(i);
            foreach (var step in new[] { 0, state.MaxStep / 3, state.MaturitySteps, state.MaxStep })
            {
                state.SetStep(step);
                var bounds = state.Bounds;
                var framing = state.FramingBounds;
                Assert.All(state.Cells, c => Assert.True(Inside(c, bounds), $"{c} outside {bounds}"));
                Assert.True(Vector3.Min(bounds.Min, framing.Min) == framing.Min &&
                            Vector3.Max(bounds.Max, framing.Max) == framing.Max,
                    $"{bounds} not inside {framing}");
            }
        }
    }

    [Fact]
    public void GroundCoversTheFramingFootprintWithoutTouchingTheTree()
    {
        var state = new TreePreviewState();
        foreach (var i in SpeciesIndices(state))
        {
            state.Select(i);
            foreach (var step in new[] { 0, state.MaxStep })
            {
                state.SetStep(step);
                var ground = state.GroundCells;
                Assert.All(ground, g => Assert.Equal(TreePreviewState.GroundY, g.Y));
                var columns = ground.Select(g => (g.X, g.Z)).ToHashSet();
                var (min, max) = state.FramingBounds;
                for (var x = (int)min.X; x < (int)max.X; x++)
                for (var z = (int)min.Z; z < (int)max.Z; z++)
                    Assert.Contains((x, z), columns);

                var groundCells = ground.Select(g => (g.X, g.Y, g.Z)).ToHashSet();
                Assert.DoesNotContain(state.Cells, c => groundCells.Contains((c.X, c.Y, c.Z)));

                // The mesh list is exactly the ground followed by the tree
                Assert.Equal(ground.Concat(state.Cells), state.MeshCells);
            }
        }
    }

    [Fact]
    public void FramingFitsTheView()
    {
        var state = new TreePreviewState();
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, 0.05f, 10000f);
        var directions = new[]
        {
            Vector3.Normalize(new Vector3(0f, 0.3f, 1f)),
            Vector3.Normalize(new Vector3(1f, 0.2f, 0.4f)),
            Vector3.Normalize(new Vector3(-0.7f, 0.8f, -0.6f)),
            Vector3.Normalize(new Vector3(0.1f, -0.2f, -1f)),
        };
        foreach (var i in SpeciesIndices(state))
        {
            state.Select(i);
            var (min, max) = state.FramingBounds;
            var center = state.FramingCenter;
            var distance = TreePreviewState.FitDistance(state.FramingRadius, FovY, Aspect);
            foreach (var dir in directions)
            {
                var view = Matrix4x4.CreateLookAt(center + dir * distance, center, Vector3.UnitY);
                var viewProj = view * projection;
                for (var corner = 0; corner < 8; corner++)
                {
                    var p = new Vector3((corner & 1) != 0 ? max.X : min.X,
                        (corner & 2) != 0 ? max.Y : min.Y,
                        (corner & 4) != 0 ? max.Z : min.Z);
                    var clip = Vector4.Transform(new Vector4(p, 1f), viewProj);
                    Assert.True(clip.W > 0f);
                    Assert.InRange(clip.X / clip.W, -1f, 1f);
                    Assert.InRange(clip.Y / clip.W, -1f, 1f);
                }
            }
        }
    }

    [Fact]
    public void SelectByNameAcceptsThePrefixAndRejectsUnknownNames()
    {
        var plain = new TreePreviewState();
        var prefixed = new TreePreviewState();
        foreach (var species in plain.Entries)
        {
            Assert.True(plain.SelectByName(species.Name));
            Assert.True(prefixed.SelectByName(TreePreviewState.NamePrefix + species.Name.ToUpperInvariant()));
            Assert.Same(species, plain.Selected);
            Assert.Same(plain.Selected, prefixed.Selected);
        }

        plain.SetStep(plain.MaxStep / 2);
        var (index, step, version) = (plain.SelectedIndex, plain.Step, plain.Version);
        Assert.False(plain.SelectByName("no_such_tree_species"));
        Assert.False(plain.SelectByName(TreePreviewState.NamePrefix + "no_such_tree_species"));
        Assert.Equal(index, plain.SelectedIndex);
        Assert.Equal(step, plain.Step);
        Assert.Equal(version, plain.Version);
    }

    [Fact]
    public void VersionMovesOnlyWhenTheShownTreeChanges()
    {
        var state = new TreePreviewState();
        var species = state.Entries.First(s => s.MaturityStepsMax > 0);
        Assert.True(state.SelectByName(species.Name));

        state.SetStep(0);
        var v = state.Version;
        state.SetStep(1);
        Assert.True(state.Version > v);

        v = state.Version;
        state.SetStep(1);
        Assert.Equal(v, state.Version);

        state.SetStep(state.MaxStep);
        v = state.Version;
        state.SetStep(state.MaxStep + 10);
        state.StepBy(1);
        Assert.Equal(v, state.Version);

        v = state.Version;
        state.Select(state.SelectedIndex);
        Assert.True(state.Version > v);

        v = state.Version;
        state.Regrow();
        Assert.True(state.Version > v);

        v = state.Version;
        state.SetSeed(state.Seed + 7);
        Assert.True(state.Version > v);
    }
}
