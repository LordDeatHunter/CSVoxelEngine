using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// The hollow fallen logs as content, checked against the data that names them: every biome whose
/// tree species declares a fallen log lays a hollow block that mines in half the time of that
/// species' placed log under the same tool gate, wears its bark outside, drops and is picked as the
/// plain log, never enters the inventory, and is a shell whose hole runs open along the facing's
/// axis while its walls hold a body up.
/// </summary>
public sealed class HollowLogTests
{
    public HollowLogTests() => TestContent.EnsureLoaded();

    /// <summary>Every (hollow log, the species it belongs to) pair the biome data declares.</summary>
    private static List<(ushort Hollow, TreeSpecies Species)> Pairs()
    {
        var pairs = new List<(ushort, TreeSpecies)>();
        foreach (var biome in Enum.GetValues<Biome>())
        {
            var hollow = FallenLogGenerator.HollowLogFor(biome);
            if (hollow == BlockType.Air) continue;
            pairs.Add((hollow, TreeGenerator.SpeciesByBiome[(int)biome]!));
        }

        Assert.NotEmpty(pairs);
        return pairs;
    }

    /// <summary>The tube's axis of a shape index: the model is authored along Z facing north, so
    /// north and south keep it along Z and west and east turn it along X.</summary>
    private static int AxisOf(int facing) => facing < 2 ? 2 : 0;

    /// <summary>Whether the line through the cell center along <paramref name="axis"/> passes
    /// strictly inside a box spanning <paramref name="min"/>..<paramref name="max"/> on both other
    /// axes (a flat extent never contains it).</summary>
    private static bool LineThroughCenterCrosses(Vector3 min, Vector3 max, int axis)
    {
        for (var a = 0; a < 3; a++)
        {
            if (a == axis) continue;
            if (!(min[a] < 0.5f && 0.5f < max[a])) return false;
        }

        return true;
    }

    private static bool AnyBoxCrosses(BlockHitbox[] boxes, int axis) =>
        boxes.Any(b => LineThroughCenterCrosses(b.Min, b.Max, axis));

    private static bool AnyQuadCrosses(BakedModel model, int axis) =>
        model.Quads.Any(q => LineThroughCenterCrosses(
            q.Positions.Aggregate(Vector3.Min), q.Positions.Aggregate(Vector3.Max), axis));

    /// <summary>Whether a shape is open along <paramref name="axis"/> and walled across it: the
    /// center line along the axis meets no box, the other horizontal center line meets one.</summary>
    private static bool IsOpenAlong(BlockHitbox[] boxes, int axis) =>
        !AnyBoxCrosses(boxes, axis) && AnyBoxCrosses(boxes, axis == 0 ? 2 : 0);

    [Fact]
    public void TheHollowLogMinesInHalfTheTimeOfItsSpeciesPlacedLog()
    {
        foreach (var (hollow, species) in Pairs())
        {
            var placedLog = Mining.HardnessOf(species.WoodId, WoodState.Detached);
            for (byte state = 0; state < 4; state++)
                Assert.Equal(placedLog / 2f, Mining.HardnessOf(hollow, state), 1e-4f);

            foreach (var item in ItemRegistry.All.Append(null))
                Assert.True(Mining.CanMine(hollow, item) == Mining.CanMine(species.WoodId, item),
                    $"{BlockType.ById[hollow]!.Name} and {species.Name}'s log disagree on mining with {item?.Name ?? "a bare hand"}");
        }
    }

    [Fact]
    public void TheOutsideIsTheBarkOfTheBiomesLog()
    {
        foreach (var (hollow, species) in Pairs())
        {
            var type = BlockType.ById[hollow]!;
            var bark = BlockType.ById[species.WoodId]!.Side;
            var models = type.RotatedModels!;
            for (var facing = 0; facing < models.Length; facing++)
            {
                var axis = AxisOf(facing);
                var outer = 0;
                foreach (var quad in models[facing].Quads)
                {
                    var faceAxis = quad.Face < 2 ? 2 : quad.Face < 4 ? 0 : 1;
                    if (faceAxis == axis) continue;
                    var plane = quad.Face is 1 or 3 or 4 ? 1f : 0f;
                    if (!quad.Positions.All(p => MathF.Abs(p[faceAxis] - plane) < 1e-4f)) continue;

                    outer++;
                    Assert.True(quad.Tile == bark,
                        $"{type.Name} facing {facing}: an outer face samples tile {quad.Tile}, not {species.Name}'s bark {bark}");
                }

                Assert.True(outer > 0, $"{type.Name} facing {facing} has no outer face");
            }

            // The pop and the debris fall back to the first face tile
            Assert.Equal(bark, type.FaceTiles[0]);
        }
    }

    [Fact]
    public void MiningAHollowLogDropsItsPlainLog()
    {
        foreach (var (hollow, species) in Pairs())
        {
            foreach (var facing in new[] { FallenLogGenerator.FacingAlongX, FallenLogGenerator.FacingAlongZ })
            {
                var drops = new List<ushort>();
                DropManager.RollLoot(hollow, new LootContext(0, 0, 0, State: facing), drops);
                Assert.Equal([species.WoodId], drops);
            }
        }
    }

    [Fact]
    public void AHollowLogIsWorldgenOnlyAndPickedAsItsLog()
    {
        foreach (var (hollow, species) in Pairs())
        {
            Assert.False(BlockType.ById[hollow]!.HasItem);
            Assert.Equal(species.WoodId, BlockType.PickId(hollow));
        }
    }

    [Fact]
    public void TheHoleIsOpenAlongTheAxisAndWalledAround()
    {
        foreach (var (hollow, _) in Pairs())
        {
            var type = BlockType.ById[hollow]!;
            var shapes = type.RotatedHitboxes!;
            for (var facing = 0; facing < shapes.Length; facing++)
            {
                var axis = AxisOf(facing);
                var boxes = shapes[facing];
                Assert.True(IsOpenAlong(boxes, axis), $"{type.Name} facing {facing} is not open along its axis");
                Assert.True(AnyBoxCrosses(boxes, 1), $"{type.Name} facing {facing} has no floor or roof");
                Assert.False(AnyQuadCrosses(type.RotatedModels![facing], axis),
                    $"{type.Name} facing {facing} draws a face across its hole");

                // The shell holds a body standing on it and does not let one sink into it
                var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
                var origin = center.Position * Chunk.Size;
                center.SetBlockRaw(8, 4, 8, hollow, (byte)facing);
                var top = origin.Y + 4 + boxes.Max(b => b.Max.Y);
                var column = new Vector3(origin.X + 8.5f, 0f, origin.Z + 8.5f);
                Assert.False(BodyCollision.BodyCollides(column + new Vector3(0f, top + 0.01f + Camera.EyeHeight, 0f), manager),
                    $"a body standing on {type.Name} facing {facing} collides with it");
                Assert.True(BodyCollision.BodyCollides(column + new Vector3(0f, top - 0.1f + Camera.EyeHeight, 0f), manager),
                    $"a body sunk into {type.Name} facing {facing} does not collide with it");
            }
        }
    }

    [Fact]
    public void StampedLogsFaceTheirAxis()
    {
        foreach (var (hollow, _) in Pairs())
        {
            var shapes = BlockType.ById[hollow]!.RotatedHitboxes!;
            Assert.True(IsOpenAlong(shapes[BlockType.ShapeIndex(FallenLogGenerator.FacingAlongX, shapes.Length)], 0));
            Assert.True(IsOpenAlong(shapes[BlockType.ShapeIndex(FallenLogGenerator.FacingAlongZ, shapes.Length)], 2));
        }
    }
}
