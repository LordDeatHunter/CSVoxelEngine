using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// A placed log takes its axis from the clicked face (<c>axisFromFace</c>): a floor or ceiling stands it upright, a wall lays
/// it along that wall's normal, whichever way the player faces. A trunk keeps its axis in the detached state byte, a
/// hollow log in its facing plus an upright bit.
/// </summary>
[Collection("Worldgen seed")]
public sealed class LogPlacementTests
{
    public LogPlacementTests() => TestContent.EnsureLoaded();

    private static readonly Vector3[] Flat = [-Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitX];

    private static BlockType[] Logs(bool hollow) =>
        BlockType.Types.Values.Where(t => t.AxisFromFace && (t.Renderer == BlockRenderer.TreeWood) != hollow).ToArray();

    // Whichever way the player is turned or tilted, placement must give the same answer for a face
    private static readonly Vector3[] Looks =
        [-Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY,
         Vector3.Normalize(new Vector3(1f, -0.4f, 0.3f))];

    private static byte Place(BlockType log, Vector3 normal, Vector3 look)
    {
        Assert.True(Placement.TryMount(log.Id, 0, normal, false, look, out var id, out var state));
        Assert.Equal(log.Id, id);
        return state;
    }

    [Fact]
    public void ALogLiesOutOfTheWallItIsPlacedAgainstAndStandsOnAFloorOrCeiling()
    {
        var logs = Logs(hollow: false);
        Assert.NotEmpty(logs);
        foreach (var log in logs)
        foreach (var look in Looks)
        {
            for (var facing = 0; facing < 4; facing++)
            {
                var state = Place(log, Flat[facing], look);
                Assert.True(WoodState.IsDetached(state), log.Name);
                Assert.Equal(facing < 2 ? 1 : 0, WoodState.LyingAxis(state));
                Assert.True(WoodState.IsFullCube(state));
            }

            foreach (var normal in new[] { Vector3.UnitY, -Vector3.UnitY })
            {
                var state = Place(log, normal, look);
                Assert.True(WoodState.IsDetached(state));
                Assert.Equal(-1, WoodState.LyingAxis(state));
                Assert.Equal(WoodState.Detached, state);
            }
        }
    }

    [Fact]
    public void ALyingLogIsAsHardAsAnUprightOneAndWearsItsEndsAcrossTheAxis()
    {
        foreach (var log in Logs(hollow: false))
        {
            Assert.Equal(Mining.HardnessOf(log.Id, WoodState.Detached), Mining.HardnessOf(log.Id, WoodState.LyingX));
            Assert.Equal(Mining.HardnessOf(log.Id, WoodState.Detached), Mining.HardnessOf(log.Id, WoodState.LyingZ));

            var end = log.FaceTiles[4];
            var side = log.FaceTiles[0];
            Assert.NotEqual(end, side);
            for (var axis = 0; axis < 2; axis++)
            {
                var model = log.LyingModels![axis];
                Assert.True(model.IsFullCube);
                var ends = axis == 0 ? new[] { 2, 3 } : new[] { 0, 1 };
                foreach (var quad in model.Quads)
                {
                    Assert.True(quad.IsFullFace, "a lying log keeps per-corner AO");
                    Assert.Equal(ends.Contains(quad.Face) ? end : side, quad.Tile);
                }
            }
        }
    }

    [Fact]
    public void AHollowLogLiesOutOfAWallAndStandsOnAFloorOrCeiling()
    {
        var logs = Logs(hollow: true);
        Assert.NotEmpty(logs);
        foreach (var log in logs)
        {
            Assert.Equal(8, log.RotatedModels!.Length);
            Assert.Equal(8, log.RotatedHitboxes!.Length);
            foreach (var look in Looks)
            {
                for (var facing = 0; facing < 4; facing++)
                    Assert.Equal(facing, Place(log, Flat[facing], look));
                foreach (var normal in new[] { Vector3.UnitY, -Vector3.UnitY })
                    Assert.Equal(Placement.UprightHollow, Place(log, normal, look) & Placement.UprightHollow);
            }
        }
    }

    [Fact]
    public void AnUprightHollowLogHasItsHoleAlongY()
    {
        static bool Covered(BlockHitbox[] boxes, Vector3 p) => boxes.Any(b =>
            p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z);

        foreach (var log in Logs(hollow: true))
        {
            var probes = Enumerable.Range(0, 11).Select(i => i / 10f).ToArray();
            // Lying along Z, the hole runs the length of Z through the cell's middle...
            Assert.All(probes, t => Assert.False(Covered(log.RotatedHitboxes![0], new Vector3(0.5f, 0.5f, t))));
            // ...and stood upright it runs the length of Y instead, whichever facing the state also carries
            for (var state = Placement.UprightHollow; state < 8; state++)
            {
                var boxes = log.RotatedHitboxes![state];
                Assert.All(probes, t => Assert.False(Covered(boxes, new Vector3(0.5f, t, 0.5f))));
                Assert.True(Covered(boxes, new Vector3(0.02f, 0.5f, 0.5f)), "the wall still stands");
            }
        }
    }

    [Fact]
    public void AnAxisFromFaceBlockNeedsAnAxisToTurn()
    {
        const string path = "data/blocks/x.json";
        var definition = new BlockDefinition { AxisFromFace = true };
        Assert.True(BlockSchemaParser.ParseAxisFromFace(definition, BlockRenderer.TreeWood, false, path));
        Assert.True(BlockSchemaParser.ParseAxisFromFace(definition, BlockRenderer.Model, true, path));
        Assert.False(BlockSchemaParser.ParseAxisFromFace(new BlockDefinition(), BlockRenderer.Model, false, path));
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            BlockSchemaParser.ParseAxisFromFace(definition, BlockRenderer.Model, false, path)).Message);
    }
}
