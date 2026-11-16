using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Models;
using VoxelEngine.Graphics;
using Xunit;

namespace VoxelEngine.Tests.Graphics;

/// <summary>
/// The standalone shape every icon, drop, held item and look-at label draws (<see cref="BlockCubeCache.WholeModel"/>):
/// a block that fills several cells is shown whole, fitted to one cell, and a shape spun in place turns about the
/// middle of what is drawn.
/// </summary>
public sealed class WholeModelTests
{
    public WholeModelTests() => TestContent.EnsureLoaded();

    private static (Vector3 Min, Vector3 Max) Bounds(IReadOnlyList<BakedQuad> quads) =>
        (quads.SelectMany(q => q.Positions).Aggregate(Vector3.Min), quads.SelectMany(q => q.Positions).Aggregate(Vector3.Max));

    private static IEnumerable<BlockType> Baked() => BlockType.Types.Values.Where(t => t.BakedModel is { Quads.Length: > 0 });

    [Fact]
    public void ATwoCellBlockIsDrawnWithBothHalvesAboutItsOwnMiddle()
    {
        var lowers = Baked().Where(t => t.UpperHalfId != 0).ToArray();
        Assert.NotEmpty(lowers);
        foreach (var lower in lowers)
        {
            var whole = BlockCubeCache.WholeModel(lower);
            var upper = BlockType.ById[lower.UpperHalfId]!.BakedModel!;
            Assert.Equal(lower.BakedModel!.Quads.Length + upper.Quads.Length, whole.Quads.Count);

            // Both cells are there, and the pivot is the middle of what is drawn on every axis it spins about
            var (min, max) = Bounds(whole.Quads);
            Assert.True(max.Y - min.Y > 1f, $"{lower.Name} shows one cell of two");
            Assert.Equal((min.X + max.X) * 0.5f, whole.Center.X, 4);
            Assert.Equal((min.Z + max.Z) * 0.5f, whole.Center.Z, 4);
            AssertFitsOneCell(lower, whole.Quads, whole.Size, whole.Center);
        }
    }

    [Fact]
    public void AMultiblockIsDrawnWholeInsideOneCell()
    {
        var anchors = Baked().Where(t => t.MultiblockSize > 1).ToArray();
        Assert.NotEmpty(anchors);
        foreach (var anchor in anchors)
        {
            var whole = BlockCubeCache.WholeModel(anchor);
            Assert.Equal(anchor.BakedModel!.Quads.Length, whole.Quads.Count);
            AssertFitsOneCell(anchor, whole.Quads, whole.Size, whole.Center);
        }
    }

    [Fact]
    public void AShapeSpunInPlaceTurnsAboutTheMiddleOfWhatIsDrawn()
    {
        foreach (var type in Baked().Where(t => t.MultiblockSize <= 1))
        {
            var whole = BlockCubeCache.WholeModel(type, visualCenter: true);
            var (min, max) = Bounds(whole.Quads);
            Assert.Equal((min.X + max.X) * 0.5f, whole.Center.X, 4);
            Assert.Equal((min.Z + max.Z) * 0.5f, whole.Center.Z, 4);
        }
    }

    private static void AssertFitsOneCell(BlockType type, IReadOnlyList<BakedQuad> quads, float size, Vector3 center)
    {
        var (min, max) = Bounds(quads);
        var low = (min - center) * size;
        var high = (max - center) * size;
        foreach (var edge in new[] { low.X, low.Y, low.Z, high.X, high.Y, high.Z })
            Assert.True(edge is >= -0.5001f and <= 0.5001f, $"{type.Name} reaches {edge} past its one-cell footprint");
    }
}
