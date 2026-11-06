using System;
using System.Linq;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>The notch an axe cuts into a trunk cell: its depth rule and the mesh it draws (<see cref="WoodNotches"/>).</summary>
public sealed class NotchedWoodTests
{
    public NotchedWoodTests() => TestContent.EnsureLoaded();

    [Fact]
    public void TheNotchDeepensWithTheDamageAndNeverPastItsLastStep()
    {
        Assert.Equal(0, WoodNotches.StepFor(0f, 24f));
        Assert.Equal(0, WoodNotches.StepFor(5f, 0f));

        var previous = 0;
        for (var damage = 0f; damage <= 30f; damage += 0.25f)
        {
            var step = WoodNotches.StepFor(damage, 24f);
            Assert.True(step >= previous);
            Assert.InRange(step, 0, WoodNotches.MaxSteps);
            previous = step;
        }
        Assert.Equal(WoodNotches.MaxSteps, previous);
        // The hit before the break already shows a real bite
        Assert.True(WoodNotches.StepFor(23f, 24f) > 0);
    }

    [Fact]
    public void SettingAndClearingANotchReportsChangesOnly()
    {
        var notches = new WoodNotches();
        Assert.True(notches.Set(1, 2, 3, 2, 1));
        Assert.False(notches.Set(1, 2, 3, 2, 1));
        Assert.True(notches.TryGet(1, 2, 3, out var face, out var step));
        Assert.Equal((2, 1), (face, step));
        Assert.True(notches.Set(1, 2, 3, 2, 0));
        Assert.False(notches.TryGet(1, 2, 3, out _, out _));
        Assert.False(notches.Set(1, 2, 3, 2, 0));
    }

    private static (Data.Vertex[] Plain, Data.Vertex[] Notched) Meshes(int face, int step)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        center.SetBlockRaw(8, 8, 8, log, WoodState.Detached);
        var plain = center.GenerateMeshData(manager)!.Vertices.ToArray();
        manager.Notches.Set(8, 8, 8, face, step);
        var notched = center.GenerateMeshData(manager)!.Vertices.ToArray();
        return (plain, notched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ANotchedCubeHasItsBiteTakenOutOfTheChosenSideOnly(int face)
    {
        const int step = 2;
        var (plain, notched) = Meshes(face, step);
        Assert.True(notched.Length > plain.Length);

        // Cell-local position of every vertex; the bite is the open box on the chosen side
        var depth = step * WoodNotches.PixelsPerStep / 16f;
        var cavity = 0;
        foreach (var vertex in notched)
        {
            var p = vertex.Position - new System.Numerics.Vector3(8f, 8f, 8f);
            // Distance in from the notch face and the position across/up it
            var n = face switch { 0 => p.Z, 1 => 1f - p.Z, 2 => p.X, _ => 1f - p.X };
            var u = face < 2 ? p.X : p.Z;
            var insideBite = n < depth - 1e-4f && u > 3f / 16f + 1e-4f && u < 13f / 16f - 1e-4f
                             && p.Y > 4f / 16f + 1e-4f && p.Y < 12f / 16f - 1e-4f;
            Assert.False(insideBite, "a vertex floats inside the bite");
            if (MathF.Abs(n - depth) < 1e-4f) cavity++;
        }
        // The back wall of the bite sits at its depth
        Assert.True(cavity >= 4);

        // The cube's other sides stay whole: its extent on the far side and across is untouched
        Assert.Contains(notched, v => MathF.Abs(v.Position.Y - 8f) < 1e-4f);
        Assert.Contains(notched, v => MathF.Abs(v.Position.Y - 9f) < 1e-4f);
    }

    [Fact]
    public void ACubeWithNoNotchMeshesAsBefore()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        center.SetBlockRaw(8, 8, 8, log, WoodState.Detached);
        var before = center.GenerateMeshData(manager)!.Vertices.Count;
        manager.Notches.Set(8, 8, 8, 0, 3);
        manager.Notches.Set(8, 8, 8, 0, 0);
        Assert.Equal(before, center.GenerateMeshData(manager)!.Vertices.Count);
    }
}
