using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Hot-fluid vents are the ember/smoke spawn points over exposed lava: a glowing fluid cell
/// with open air above it. The predicate is data-driven (emission, not a name), and the mesher
/// must publish exactly the exposed surface of a pool.
/// </summary>
public sealed class LavaVentTests
{
    public LavaVentTests() => TestContent.EnsureLoaded();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void ExposedLavaIsAVent()
    {
        Assert.True(Chunk.IsHotFluidVent(Block("lava"), 0));
    }

    [Fact]
    public void CoveredLavaIsNotAVent()
    {
        Assert.False(Chunk.IsHotFluidVent(Block("lava"), Block("stone")));
    }

    [Fact]
    public void ColdFluidIsNeverAVent()
    {
        Assert.False(Chunk.IsHotFluidVent(Block("water"), 0));
        // Every fluid that does not glow stays out, whatever sits above it
        foreach (var type in BlockType.Types.Values.Where(t => t.Fluid && BlockType.Emission[t.Id] == 0))
            Assert.False(Chunk.IsHotFluidVent(type.Id, 0), $"{type.Name} is cold and must not vent");
    }

    [Fact]
    public void OnlyTheTopOfAColumnVents()
    {
        var lava = Block("lava");
        // The lower cell of a two-deep column has lava above it, the upper one air
        Assert.False(Chunk.IsHotFluidVent(lava, lava));
        Assert.True(Chunk.IsHotFluidVent(lava, 0));
    }

    [Fact]
    public void MeshBuildPublishesThePoolSurface()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        var lava = Block("lava");

        // A 3x3 floor carrying a two-deep 3x3 lava pool, open to the air above
        var expected = new System.Collections.Generic.HashSet<Vector3>();
        for (var x = 4; x < 7; x++)
        for (var z = 4; z < 7; z++)
        {
            center.SetBlockRaw(x, 3, z, stone, 0);
            center.SetBlockRaw(x, 4, z, lava, FluidState.Source);
            center.SetBlockRaw(x, 5, z, lava, FluidState.Source);
            expected.Add(new Vector3(x, 5, z));
        }
        // One pool cell capped by stone: covered lava never vents
        center.SetBlockRaw(6, 6, 6, stone, 0);
        expected.Remove(new Vector3(6, 5, 6));

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        Assert.NotNull(mesh.HotFluidVents);

        var decoded = mesh.HotFluidVents.Select(i => Chunk.LocalOfIdx(i)).ToList();
        Assert.Equal(decoded.Count, decoded.Distinct().Count());
        Assert.True(expected.SetEquals(decoded));
    }
}
