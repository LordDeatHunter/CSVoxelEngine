using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The kilns are multiblocks made entirely of content: the 2x2x2 mud brick kiln and the 3x3x3
/// alloy furnace, each two blocks, a loot table and one model. These pin the structure flags the
/// engine reads off that content, each model's opening against its <see cref="KilnLayout"/>, and
/// the split the mesher makes between the anchor (draws the whole volume) and a part (draws
/// nothing).
/// </summary>
public sealed class KilnContentTests
{
    private const float Epsilon = 1e-4f;

    public KilnContentTests() => TestContent.EnsureLoaded();

    private static List<ushort> RollOnce(LootTable table)
    {
        var into = new List<ushort>();
        table.Roll(new LootContext(0, 0, 0), into);
        return into;
    }

    /// <summary>Every kiln: its anchor, its part and the block it is built from.</summary>
    public static TheoryData<string, string, string> Kilns => new()
    {
        { "mud_brick_kiln", "mud_brick_kiln_part", "mud_bricks" },
        { "alloy_furnace", "alloy_furnace_part", "alloy_bricks" },
    };

    /// <summary>Every kiln anchor.</summary>
    public static TheoryData<string> KilnAnchors => new() { "mud_brick_kiln", "alloy_furnace" };

    [Theory]
    [MemberData(nameof(Kilns))]
    public void AnchorAndPartDeclareTheStructureFlags(string anchorName, string partName, string sourceName)
    {
        var anchor = ContentRegistry.RequireBlock(anchorName);
        var part = ContentRegistry.RequireBlock(partName);
        var bricks = ContentRegistry.RequireBlock(sourceName);

        foreach (var cell in new[] { anchor, part })
        {
            Assert.True(cell.Solid);
            // A structure is built from its source block, never held
            Assert.False(cell.HasItem);
            Assert.Null(ItemRegistry.TryGet(cell.Id));
            // The shape stands inside its cells, so no cell may hide a neighbor's face or darken the
            // ground around it; light must still reach the anchor cell, which is where every quad of
            // the model is lit from
            Assert.False(cell.OccludesNeighbors);
            Assert.True(cell.PassesLight);
            Assert.False(cell.CastsAmbientOcclusion);
            // ...and a body meets the shape, not the cell
            Assert.True(cell.CollidesByHitbox);
            // Neither renderer is the plain model renderer, so neither joins the greedy pass
            Assert.False(BlockType.IsGreedy(cell.Id));
            Assert.Equal(new[] { bricks.Id }, RollOnce(cell.LootTable!));
        }

        Assert.NotNull(anchor.Kiln);
        Assert.Null(part.Kiln);
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

    [Theory]
    [MemberData(nameof(KilnAnchors))]
    public void KilnModelSpansTheVolumeWithAnOpeningOnTheNorthFace(string anchorName)
    {
        var anchor = ContentRegistry.RequireBlock(anchorName);
        var layout = anchor.Kiln!.Layout;
        var north = anchor.RotatedModels![0];

        foreach (var quad in north.Quads)
        {
            foreach (var position in quad.Positions)
            {
                Assert.InRange(position.X, -Epsilon, anchor.MultiblockSize + Epsilon);
                Assert.InRange(position.Y, -Epsilon, anchor.MultiblockSize + Epsilon);
                Assert.InRange(position.Z, -Epsilon, anchor.MultiblockSize + Epsilon);
            }

            // A cullface would be tested against the structure's own part cells, and a full
            // face would take the per-corner AO path rotation cannot index
            Assert.Equal(-1, quad.CullFace);
            Assert.False(quad.IsFullFace);
        }

        // Looking into the opening you see a back wall at the layout's depth
        var middle = new Vector2((layout.OpeningMinX + layout.OpeningMaxX) * 0.5f,
            (layout.OpeningFloorY + layout.OpeningCeilingY) * 0.5f);
        Assert.Contains(north.Quads, q => q.Face == 0
            && MathF.Abs(Range(q, p => p.Z).Min - layout.OpeningDepthZ) <= Epsilon
            && Covers(Range(q, p => p.X), middle.X) && Covers(Range(q, p => p.Y), middle.Y));

        // ...because nothing covers the opening on the front plane itself
        foreach (var quad in north.Quads)
        {
            if (quad.Face != 0 || MathF.Abs(Range(quad, p => p.Z).Max) > Epsilon) continue;
            var x = Range(quad, p => p.X);
            var y = Range(quad, p => p.Y);
            var blocksX = x.Max > layout.OpeningMinX + Epsilon && x.Min < layout.OpeningMaxX - Epsilon;
            var blocksY = y.Max > layout.OpeningFloorY + Epsilon && y.Min < layout.OpeningCeilingY - Epsilon;
            Assert.False(blocksX && blocksY, "a front quad covers the kiln's opening");
        }

        // Rotating to facing 1 turns the opening onto +Z, so the back wall moves with it
        Assert.Contains(anchor.RotatedModels[1].Quads, q => q.Face == 1
            && MathF.Abs(Range(q, p => p.Z).Max - (anchor.MultiblockSize - layout.OpeningDepthZ)) <= Epsilon);
    }

    private static bool Covers((float Min, float Max) range, float value) =>
        range.Min <= value + Epsilon && range.Max >= value - Epsilon;

    /// <summary>
    /// What a kiln holds has to stand on the model and in free air: the bowl, every fuel unit and the ember bed
    /// each rest on an up-facing surface of the model at their own height, and no part of the model cuts through
    /// the space the bowl, the waiting charge or a fuel unit is drawn in.
    /// </summary>
    [Theory]
    [MemberData(nameof(KilnAnchors))]
    public void WhatAKilnHoldsStandsOnTheModelAndClearOfIt(string anchorName)
    {
        var anchor = ContentRegistry.RequireBlock(anchorName);
        var profile = anchor.Kiln!;
        var layout = profile.Layout;
        var model = anchor.RotatedModels![0];

        AssertStandsOn(model, layout.CrucibleLocal, "the crucible");
        AssertStandsOn(model, layout.EmberLocal with { Y = layout.OpeningFloorY }, "the ember bed");

        var bowlHalf = new Vector3(KilnRenderer.CrucibleOuterHalf, 0f, KilnRenderer.CrucibleOuterHalf);
        AssertClear(model, layout.CrucibleLocal - bowlHalf,
            layout.CrucibleLocal + bowlHalf + new Vector3(0f, KilnRenderer.CrucibleTopY, 0f), "the crucible");

        var lumpHalf = new Vector3(KilnRenderer.PendingSize * 0.5f, 0f, KilnRenderer.PendingSize * 0.5f);
        AssertClear(model, layout.PendingLocal - lumpHalf,
            layout.PendingLocal + lumpHalf + new Vector3(0f, KilnRenderer.PendingSize, 0f), "the waiting charge");

        var fuelHalf = new Vector3(KilnRenderer.FuelSize * 0.5f, 0f, KilnRenderer.FuelSize * 0.5f);
        for (var i = 0; i < profile.FuelCap; i++)
        {
            var slot = layout.FuelSlotLocal(i, profile.FuelCap);
            AssertStandsOn(model, slot, $"fuel unit {i}");
            AssertClear(model, slot - fuelHalf,
                slot + fuelHalf + new Vector3(0f, 2f * SpritePose.RestingLift(KilnRenderer.FuelSize), 0f),
                $"fuel unit {i}");
            // ...and it lies beside the bowl, never under it
            var apart = Vector3.Abs(slot - layout.CrucibleLocal);
            Assert.True(apart.X >= KilnRenderer.CrucibleOuterHalf + fuelHalf.X - Epsilon
                        || apart.Z >= KilnRenderer.CrucibleOuterHalf + fuelHalf.Z - Epsilon,
                $"fuel unit {i} lies under the crucible");
        }
    }

    // Some up-facing quad of the model lies at the point's height and under the point
    private static void AssertStandsOn(BakedModel model, Vector3 foot, string what) =>
        Assert.True(model.Quads.Any(q => q.Face == 4
                && MathF.Abs(Range(q, p => p.Y).Min - foot.Y) <= Epsilon
                && Covers(Range(q, p => p.X), foot.X) && Covers(Range(q, p => p.Z), foot.Z)),
            $"{what} at {foot} stands on nothing");

    // No quad of the model reaches into the open inside of the box
    private static void AssertClear(BakedModel model, Vector3 min, Vector3 max, string what)
    {
        const float skin = 1e-3f;
        foreach (var quad in model.Quads)
        {
            var x = Range(quad, p => p.X);
            var y = Range(quad, p => p.Y);
            var z = Range(quad, p => p.Z);
            var inside = x.Max > min.X + skin && x.Min < max.X - skin
                         && y.Max > min.Y + skin && y.Min < max.Y - skin
                         && z.Max > min.Z + skin && z.Min < max.Z - skin;
            Assert.False(inside, $"the model cuts through {what}");
        }
    }

    [Theory]
    [MemberData(nameof(KilnAnchors))]
    public void PartEmitsNothingAndAnchorEmitsItsQuads(string anchorName)
    {
        var anchor = ContentRegistry.RequireBlock(anchorName);
        var part = BlockType.ById[anchor.MultiblockPartId]!;
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);

        // A part is solid and occludes, but the anchor's model already covers its cell
        // (state byte: facing 2 at offset dx = 1, the encoding Multiblock formalizes)
        center.SetBlockRaw(8, 8, 8, part.Id, (byte)(2 | (1 << 2)));
        Assert.Empty(center.GenerateMeshData(manager)!.Vertices);

        // The anchor draws the whole model, every quad of it (none is culled: a
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
