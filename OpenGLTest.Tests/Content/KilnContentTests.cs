using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The mud brick kiln is the first multiblock, and it is entirely content: two blocks, a loot
/// table and one 2x2x2 model. These pin the structure flags the engine reads off that content,
/// the model's opening, and the split the mesher makes between the anchor (draws the whole
/// volume) and a part (draws nothing).
/// </summary>
public sealed class KilnContentTests
{
    private const float Epsilon = 1e-4f;

    public KilnContentTests() => TestContent.EnsureLoaded();

    private static List<byte> RollOnce(LootTable table)
    {
        var into = new List<byte>();
        table.Roll(new LootContext(0, 0, 0), into);
        return into;
    }

    [Fact]
    public void AnchorAndPartDeclareTheStructureFlags()
    {
        var anchor = ContentRegistry.RequireBlock("mud_brick_kiln");
        var part = ContentRegistry.RequireBlock("mud_brick_kiln_part");
        var bricks = ContentRegistry.RequireBlock("mud_bricks");

        foreach (var cell in new[] { anchor, part })
        {
            Assert.True(cell.Solid);
            // A structure is built from its source block, never held
            Assert.False(cell.HasItem);
            Assert.Null(ItemRegistry.TryGet(cell.Id));
            // The surface really covers its neighbors, but light must still reach the anchor
            // cell, which is where every quad of the model is lit from
            Assert.True(cell.OccludesNeighbours);
            Assert.True(cell.PassesLight);
            Assert.True(cell.CastsAmbientOcclusion);
            // Neither renderer is the plain model renderer, so neither joins the greedy pass
            Assert.False(BlockType.IsGreedy(cell.Id));
            Assert.Equal(new[] { bricks.Id }, RollOnce(cell.LootTable!));
        }

        Assert.True(anchor.Kiln);
        Assert.False(part.Kiln);
        Assert.Equal(BlockRenderer.Multiblock, anchor.Renderer);
        Assert.Equal(BlockRenderer.MultiblockPart, part.Renderer);
        Assert.Equal(part.Id, anchor.MultiblockPartId);
        Assert.Equal(bricks.Id, anchor.MultiblockSourceId);
        Assert.NotNull(anchor.RotatedModels);
        Assert.Equal(4, anchor.RotatedModels!.Length);
    }

    private static (float Min, float Max) Range(BakedQuad quad, Func<Vector3, float> axis)
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var position in quad.Positions)
        {
            var value = axis(position);
            min = MathF.Min(min, value);
            max = MathF.Max(max, value);
        }
        return (min, max);
    }

    [Fact]
    public void KilnModelSpansTheVolumeWithAnOpeningOnTheNorthFace()
    {
        var anchor = ContentRegistry.RequireBlock("mud_brick_kiln");
        var north = anchor.RotatedModels![0];

        foreach (var quad in north.Quads)
        {
            foreach (var position in quad.Positions)
            {
                Assert.InRange(position.X, -Epsilon, MultiblockShape.Size + Epsilon);
                Assert.InRange(position.Y, -Epsilon, MultiblockShape.Size + Epsilon);
                Assert.InRange(position.Z, -Epsilon, MultiblockShape.Size + Epsilon);
            }

            // A cullface would be tested against the structure's own part cells, and a full
            // face would take the per-corner AO path rotation cannot index
            Assert.Equal(-1, quad.CullFace);
            Assert.False(quad.IsFullFace);
        }

        // The hearth's back wall is the only thing you see looking into the opening
        var backWalls = north.Quads
            .Where(q => q.Face == 0 && MathF.Abs(Range(q, p => p.Z).Min - KilnRenderer.OpeningDepthZ) <= Epsilon)
            .ToList();
        var backWall = Assert.Single(backWalls);
        var wallX = Range(backWall, p => p.X);
        var wallY = Range(backWall, p => p.Y);
        Assert.Equal(KilnRenderer.OpeningMinX, wallX.Min, 3);
        Assert.Equal(KilnRenderer.OpeningMaxX, wallX.Max, 3);
        Assert.Equal(KilnRenderer.OpeningFloorY, wallY.Min, 3);
        Assert.Equal(KilnRenderer.OpeningCeilingY, wallY.Max, 3);

        // ...because nothing covers the opening on the front plane itself
        foreach (var quad in north.Quads)
        {
            if (quad.Face != 0 || MathF.Abs(Range(quad, p => p.Z).Max) > Epsilon) continue;
            var x = Range(quad, p => p.X);
            var y = Range(quad, p => p.Y);
            var blocksX = x.Max > KilnRenderer.OpeningMinX + Epsilon && x.Min < KilnRenderer.OpeningMaxX - Epsilon;
            var blocksY = y.Max > KilnRenderer.OpeningFloorY + Epsilon && y.Min < KilnRenderer.OpeningCeilingY - Epsilon;
            Assert.False(blocksX && blocksY, "a front quad covers the kiln's opening");
        }

        // Rotating to facing 1 turns the opening onto +Z, so the back wall moves with it
        var rotatedWalls = anchor.RotatedModels[1].Quads
            .Where(q => q.Face == 1 &&
                        MathF.Abs(Range(q, p => p.Z).Max - (MultiblockShape.Size - KilnRenderer.OpeningDepthZ)) <= Epsilon)
            .ToList();
        var rotatedWall = Assert.Single(rotatedWalls);
        Assert.Equal(MultiblockShape.Size - KilnRenderer.OpeningDepthZ, Range(rotatedWall, p => p.Z).Min, 3);
    }

    [Fact]
    public void PartEmitsNothingAndAnchorEmitsItsQuads()
    {
        var anchor = ContentRegistry.RequireBlock("mud_brick_kiln");
        var part = ContentRegistry.RequireBlock("mud_brick_kiln_part");
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);

        // A part is solid and occludes, but the anchor's model already covers its cell
        // (state byte: facing 2 at offset dx = 1, the encoding Multiblock formalises)
        center.SetBlockRaw(8, 8, 8, part.Id, (byte)(2 | (1 << 2)));
        Assert.Empty(center.GenerateMeshData(manager)!.Vertices);

        // The anchor draws the whole 2x2x2 model, every quad of it (none is culled: a
        // multiblock quad carries no cullface)
        center.SetBlockRaw(8, 8, 8, anchor.Id, 0);
        var north = center.GenerateMeshData(manager)!;
        Assert.Equal(4 * anchor.RotatedModels![0].Quads.Length, north.Vertices.Count);

        // ...pre-rotated to the facing its state byte carries
        center.SetBlockRaw(8, 8, 8, anchor.Id, 2);
        var west = center.GenerateMeshData(manager)!;
        Assert.Equal(4 * anchor.RotatedModels[2].Quads.Length, west.Vertices.Count);

        var cell = new Vector3(8, 8, 8);
        var (emittedMin, emittedMax) = Bounds(west.Vertices.Select(v => v.Position - cell));
        var (modelMin, modelMax) = Bounds(anchor.RotatedModels[2].Quads.SelectMany(q => q.Positions));
        Assert.Equal(modelMin.X, emittedMin.X, 3);
        Assert.Equal(modelMax.X, emittedMax.X, 3);
        Assert.Equal(modelMin.Z, emittedMin.Z, 3);
        Assert.Equal(modelMax.Z, emittedMax.Z, 3);
    }

    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> points)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var point in points)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        return (min, max);
    }
}
