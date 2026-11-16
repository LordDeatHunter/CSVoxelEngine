using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// A shaped multiblock (<c>multiblock.boxes</c>) answers the raycast, the outline and body collision with its
/// shape, cut up per cell: these hold the cut to the declared boxes on every facing, the shipped structures'
/// boxes to their models, and a body to the shape rather than the cube.
/// </summary>
public sealed class MultiblockBoxesTests
{
    private const float Epsilon = 1e-4f;

    public MultiblockBoxesTests() => TestContent.EnsureLoaded();

    /// <summary>Every shipped multiblock anchor.</summary>
    private static IEnumerable<BlockType> Anchors() =>
        BlockType.ById.Where(t => t is { Renderer: BlockRenderer.Multiblock }).Select(t => t!);

    private static bool Inside(BlockHitbox box, Vector3 point, float slack) =>
        point.X >= box.Min.X - slack && point.X <= box.Max.X + slack
        && point.Y >= box.Min.Y - slack && point.Y <= box.Max.Y + slack
        && point.Z >= box.Min.Z - slack && point.Z <= box.Max.Z + slack;

    private static float Volume(BlockHitbox box) => box.Size.X * box.Size.Y * box.Size.Z;

    /// <summary>
    /// Cutting a shape up loses and adds nothing: the pieces of every cell lie inside that cell, their volumes
    /// sum to the declared box's on every facing, and a point inside the box at north is inside the piece of the
    /// cell its rotated twin falls in.
    /// </summary>
    [Fact]
    public void TheCutKeepsTheShapeOnEveryFacing()
    {
        const int size = 3;
        var box = new BlockHitbox(new Vector3(0.25f, 0f, 0.5f), new Vector3(2.5f, 1.75f, 3f));
        // A full floor under it, so no cell is left without a piece
        var floor = new BlockHitbox(Vector3.Zero, new Vector3(size, 0.125f, size));
        var upper = new BlockHitbox(new Vector3(0f, 0.125f, 0f), new Vector3(size, size, size));
        var table = MultiblockBoxes.Build([floor, upper], size);
        var shaped = MultiblockBoxes.Build([floor, upper, box], size);
        var probe = new Vector3(0.3f, 1.7f, 0.6f);

        for (var facing = 0; facing < 4; facing++)
        {
            var total = 0f;
            foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
            {
                var pieces = table[facing][MultiblockBoxes.CellIndex(dx, dy, dz, size)];
                Assert.NotEmpty(pieces);
                foreach (var piece in pieces)
                {
                    Assert.True(Inside(BlockHitbox.FullCube, piece.Min, Epsilon));
                    Assert.True(Inside(BlockHitbox.FullCube, piece.Max, Epsilon));
                    total += Volume(piece);
                }
            }
            Assert.Equal(Volume(floor) + Volume(upper), total, precision: 3);

            var turned = MultiblockShape.RotatePoint(probe, facing, size);
            var cell = new Vector3(MathF.Floor(turned.X), MathF.Floor(turned.Y), MathF.Floor(turned.Z));
            var inCell = shaped[facing][MultiblockBoxes.CellIndex((int)cell.X, (int)cell.Y, (int)cell.Z, size)];
            // The third declared box is the only one reaching the probe's height in that corner
            Assert.Contains(inCell, piece => Inside(piece, turned - cell, Epsilon) && piece.Max.Y - piece.Min.Y < 1f - Epsilon);
        }
    }

    [Fact]
    public void AShapeThatLeavesACellEmptyOrTheVolumeIsRefused()
    {
        // Half the volume: the cells of the other half could not be aimed at
        Assert.Throws<ArgumentException>(() =>
            MultiblockBoxes.Build([new BlockHitbox(Vector3.Zero, new Vector3(1f, 2f, 2f))], 2));
        Assert.Throws<ArgumentException>(() =>
            MultiblockBoxes.Build([new BlockHitbox(Vector3.Zero, new Vector3(2f, 2f, 2.5f))], 2));
    }

    /// <summary>
    /// Collision and look agree for every shipped structure: every corner of the anchor's model lies inside one
    /// of its declared boxes (give or take a trim standing a pixel proud), on every facing, so nothing drawn can
    /// be walked or aimed through; and every cell holds a piece.
    /// </summary>
    [Fact]
    public void EveryShippedStructuresBoxesHoldItsModel()
    {
        const float trim = 1f / 16f + Epsilon;
        Assert.NotEmpty(Anchors());
        foreach (var anchor in Anchors())
        {
            var table = anchor.MultiblockCellBoxes;
            Assert.True(table != null, $"{anchor.Name} declares no multiblock.boxes");
            var size = anchor.MultiblockSize;

            for (var facing = 0; facing < 4; facing++)
            {
                var world = new List<BlockHitbox>();
                foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
                {
                    var offset = new Vector3(dx, dy, dz);
                    var pieces = table![facing][MultiblockBoxes.CellIndex(dx, dy, dz, size)];
                    Assert.NotEmpty(pieces);
                    world.AddRange(pieces.Select(p => new BlockHitbox(p.Min + offset, p.Max + offset)));
                }

                foreach (var quad in anchor.RotatedModels![facing].Quads)
                foreach (var corner in quad.Positions)
                    Assert.True(world.Any(box => Inside(box, corner, trim)),
                        $"{anchor.Name} facing {facing}: model corner {corner} lies outside every box");
            }
        }
    }

    /// <summary>
    /// A converted structure in a world: each of its cells resolves to its own piece of the shape, a body
    /// standing in the air beside a tier that steps in is free where the full cube would have blocked it, and a
    /// body inside the shape still collides. A part whose state names a cell outside any structure stays a cube.
    /// </summary>
    [Fact]
    public void ABodyMeetsTheShapeNotTheCube()
    {
        foreach (var anchor in Anchors())
        {
            var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
            var origin = center.Position * Chunk.Size + new Vector3(6, 6, 6);
            var size = anchor.MultiblockSize;
            Multiblock.Convert(manager, origin, 0, anchor, []);

            foreach (var (dx, dy, dz) in Multiblock.Offsets(size))
            {
                var cell = origin + new Vector3(dx, dy, dz);
                var id = manager.GetBlockGlobal((int)cell.X, (int)cell.Y, (int)cell.Z);
                var boxes = BlockShape.BoxesAt(id, manager, (int)cell.X, (int)cell.Y, (int)cell.Z);
                Assert.Same(anchor.MultiblockCellBoxes![0][MultiblockBoxes.CellIndex(dx, dy, dz, size)], boxes);
            }

            // The top corner cell: find a spot of it the shape leaves empty, and one it fills
            var top = origin + new Vector3(0, size - 1, 0);
            var corner = anchor.MultiblockCellBoxes![0][MultiblockBoxes.CellIndex(0, size - 1, 0, size)];
            var reach = corner.Max(b => b.Max.Y);
            Assert.True(reach < 1f - Epsilon || corner.Min(b => b.Min.X) > Epsilon,
                $"{anchor.Name}'s top corner cell is a full cube; the test needs a stepped shape");

            // Feet a hair inside the cell's outer top corner, where every shipped shape has stepped in
            var feet = top + new Vector3(0.02f - PlayerPhysics.PlayerRadius, 0.99f, 0.02f - PlayerPhysics.PlayerRadius);
            Assert.False(BodyCollision.BodyCollides(feet + new Vector3(0f, Camera.EyeHeight, 0f), manager),
                $"a body beside {anchor.Name}'s stepped top collides with thin air");

            // ...and the middle of the structure's floor is solid
            var inside = origin + new Vector3(size * 0.5f, 0.05f, size * 0.5f);
            Assert.True(BodyCollision.BodyCollides(inside + new Vector3(0f, Camera.EyeHeight, 0f), manager),
                $"a body inside {anchor.Name} does not collide with it");
        }

        var kiln = ContentRegistry.RequireBlock("mud_brick_kiln");
        var part = BlockType.ById[kiln.MultiblockPartId]!;
        Assert.Null(MultiblockBoxes.At(part, Multiblock.EncodeState(0, 3, 0, 0)));
    }
}
