using System.Linq;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// Rules of the grown-crown table (<see cref="TreeGenerator.SetCanopyAdded"/>): an added crown
/// answers the far-LOD canopy reads on every ring, wins over a disturbed marker, is taken away by a
/// later poison, reports a repeated record as unchanged, survives an export and import without
/// beating a live record, and is forgotten by a world reset. In the LOD column caps collection,
/// beside the other tests that read or write the process-global canopy tables.
/// </summary>
[Collection("LOD column caps")]
public sealed class CanopyAddedTests
{
    // Far from every other test's columns
    private const int X = 934_567;
    private const int Z = -865_432;

    private static ushort Leaf => ContentRegistry.RequireBlock("oak_leaves").Id;

    public CanopyAddedTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AnAddedCrownAnswersOverADisturbedColumnOnEveryRing()
    {
        try
        {
            TreeCanopy.ResetCanopyDisturbed();
            TreeGenerator.PoisonCanopy(X, Z, 0);
            Assert.False(TreeGenerator.TryGetCanopy(X, Z, proxy: false, out _, out _));

            Assert.True(TreeGenerator.SetCanopyAdded(X, Z, 77, Leaf));

            foreach (var proxy in new[] { false, true })
            {
                Assert.True(TreeGenerator.TryGetCanopy(X, Z, proxy, out var top, out var leaf));
                Assert.Equal(77, top);
                Assert.Equal(Leaf, leaf);
            }

            Assert.Equal(77, TreeGenerator.CanopyTop(X, Z));
            Assert.Equal(Leaf, TreeGenerator.CanopyLeafBlock(X, Z));
        }
        finally
        {
            TreeCanopy.ResetCanopyDisturbed();
        }
    }

    [Fact]
    public void APoisonTakesAnAddedCrownAway()
    {
        try
        {
            TreeCanopy.ResetCanopyDisturbed();
            TreeGenerator.SetCanopyAdded(X, Z, 77, Leaf);
            TreeGenerator.SetCanopyAdded(X + 1, Z, 80, Leaf);

            TreeGenerator.PoisonCanopy(X, Z, 0);

            Assert.False(TreeGenerator.TryGetCanopy(X, Z, proxy: false, out _, out _));
            Assert.Equal(0, TreeGenerator.CanopyTop(X, Z));
            // Only the poisoned column lost its crown
            Assert.True(TreeGenerator.TryGetCanopy(X + 1, Z, proxy: false, out var top, out _));
            Assert.Equal(80, top);
        }
        finally
        {
            TreeCanopy.ResetCanopyDisturbed();
        }
    }

    [Fact]
    public void ARepeatedRecordIsNotAChange()
    {
        try
        {
            TreeCanopy.ResetCanopyDisturbed();
            Assert.True(TreeGenerator.SetCanopyAdded(X, Z, 77, Leaf));
            var version = TreeCanopy.CanopyAddedVersion;

            Assert.False(TreeGenerator.SetCanopyAdded(X, Z, 77, Leaf));
            Assert.Equal(version, TreeCanopy.CanopyAddedVersion);

            Assert.True(TreeGenerator.SetCanopyAdded(X, Z, 78, Leaf));
            Assert.NotEqual(version, TreeCanopy.CanopyAddedVersion);
        }
        finally
        {
            TreeCanopy.ResetCanopyDisturbed();
        }
    }

    [Fact]
    public void AnImportKeepsALiveRecordAndAddsTheRest()
    {
        try
        {
            TreeCanopy.ResetCanopyDisturbed();
            TreeGenerator.SetCanopyAdded(X, Z, 77, Leaf);
            TreeGenerator.SetCanopyAdded(X + 2, Z, 90, Leaf);
            var saved = TreeCanopy.ExportCanopyAdded();
            Assert.NotEmpty(saved);

            TreeCanopy.ResetCanopyDisturbed();
            Assert.False(TreeGenerator.TryGetCanopy(X, Z, proxy: false, out _, out _));

            // A grow made before the import is newer than the file
            TreeGenerator.SetCanopyAdded(X, Z, 99, Leaf);
            TreeCanopy.ImportCanopyAdded(saved);

            Assert.True(TreeGenerator.TryGetCanopy(X, Z, proxy: false, out var live, out _));
            Assert.Equal(99, live);
            Assert.True(TreeGenerator.TryGetCanopy(X + 2, Z, proxy: false, out var imported, out _));
            Assert.Equal(90, imported);

            TreeCanopy.ResetCanopyDisturbed();
            Assert.False(TreeGenerator.TryGetCanopy(X + 2, Z, proxy: false, out _, out _));
            Assert.DoesNotContain(TreeCanopy.ExportCanopyAdded().SelectMany(b => b.Values),
                v => v != VoxelEngine.Lod.LodSurface.None);
        }
        finally
        {
            TreeCanopy.ResetCanopyDisturbed();
        }
    }
}
