using VoxelEngine.Chunks.Meshing;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>
/// Layered snow hides the faces its neighbors cover: a side against an opaque cube or a snow cell at least as deep, and
/// the top of a full stack under an opaque cube or more snow. A side against shallower snow stays, since the part
/// above the neighbor is open.
/// </summary>
public sealed class SnowLayerCullingTests
{
    public SnowLayerCullingTests() => TestContent.EnsureLoaded();

    private const int Max = 16;

    [Fact]
    public void ASideFacingSnowAtLeastAsDeepIsHidden()
    {
        Assert.True(StackEmitter.SnowFaceHidden(0, 8, Max, neighborOccludes: false, neighborCount: 8));
        Assert.True(StackEmitter.SnowFaceHidden(0, 8, Max, neighborOccludes: false, neighborCount: 12));
        Assert.False(StackEmitter.SnowFaceHidden(0, 8, Max, neighborOccludes: false, neighborCount: 4));
        Assert.False(StackEmitter.SnowFaceHidden(0, 8, Max, neighborOccludes: false, neighborCount: 0));
        Assert.True(StackEmitter.SnowFaceHidden(0, 8, Max, neighborOccludes: true, neighborCount: 0));
    }

    [Fact]
    public void OnlyAFullStacksTopIsCovered()
    {
        Assert.True(StackEmitter.SnowFaceHidden(4, Max, Max, neighborOccludes: false, neighborCount: 1));
        Assert.True(StackEmitter.SnowFaceHidden(4, Max, Max, neighborOccludes: true, neighborCount: 0));
        Assert.False(StackEmitter.SnowFaceHidden(4, Max - 1, Max, neighborOccludes: true, neighborCount: 1));
        Assert.False(StackEmitter.SnowFaceHidden(4, Max, Max, neighborOccludes: false, neighborCount: 0));
    }

    [Fact]
    public void TwoEqualDriftsShareNoWall()
    {
        var snow = ContentRegistry.RequireBlock("snow").Id;

        var (aloneManager, alone) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        alone.SetBlockRaw(5, 8, 5, snow, 8);
        var single = alone.GenerateMeshData(aloneManager)!.Vertices.Count;

        var (pairManager, pair) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        pair.SetBlockRaw(5, 8, 5, snow, 8);
        pair.SetBlockRaw(6, 8, 5, snow, 8);
        var both = pair.GenerateMeshData(pairManager)!.Vertices.Count;

        // Two lone cells less the two walls they press together, one quad each
        Assert.Equal(2 * single - 2 * 4, both);
    }
}
