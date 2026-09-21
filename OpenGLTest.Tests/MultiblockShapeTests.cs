using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// A multiblock's model is authored once, facing north, and pre-rotated to the other three
/// facings at bake time. These pin the rotation itself: it maps the 2x2x2 volume onto itself,
/// it turns the authored front onto the requested side, and a rotated bake keeps every quad
/// while dropping the per-corner AO flag that rotation would misindex.
/// </summary>
public sealed class MultiblockShapeTests
{
    public MultiblockShapeTests() => TestContent.EnsureLoaded();

    private static void AssertClose(Vector3 expected, Vector3 actual, string what)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{what}: expected {expected}, got {actual}");
    }

    [Fact]
    public void RotatePointIsIdentityForNorthAndFourQuarterTurnsCompose()
    {
        var point = new Vector3(0.5f, 1.25f, 0.75f);
        Assert.Equal(point, MultiblockShape.RotatePoint(point, 0));

        // A half turn about the footprint's centre is the identity, and a quarter turn takes
        // the volume's -X/-Z corner to its +X/+Z one: the volume maps onto itself either way.
        AssertClose(new Vector3(1f, 1f, 1f), MultiblockShape.RotatePoint(MultiblockShape.RotatePoint(
            new Vector3(1f, 1f, 1f), 1), 1), "centre survives a half turn");
        AssertClose(point, MultiblockShape.RotatePoint(MultiblockShape.RotatePoint(point, 1), 1),
            "half turn is the identity");
        AssertClose(new Vector3(2f, 0f, 2f), MultiblockShape.RotatePoint(Vector3.Zero, 1), "min corner");

        // The authored front (north) lands on exactly the requested side
        for (var facing = 0; facing < 4; facing++)
        {
            AssertClose(MultiblockShape.FrontNormal(facing),
                MultiblockShape.RotateDirection(MultiblockShape.FrontNormal(0), facing), $"front of facing {facing}");
            Assert.Equal(facing, MultiblockShape.FacingOf(MultiblockShape.FrontNormal(facing)));
        }

        // There is no vertical facing
        Assert.Equal(-1, MultiblockShape.FacingOf(Vector3.UnitY));
        Assert.Equal(-1, MultiblockShape.FacingOf(-Vector3.UnitY));
    }

    // Two boxes spanning the whole 2x2x2 volume in model units (0..32), the way a multiblock
    // model is authored. No cullface and no face covering a cell plane, so the bake validation
    // in ContentRegistry would accept it too.
    private static BakedModel BakeTestVolume()
    {
        var elements = new List<ModelDefinition.ElementDefinition>
        {
            MakeBox([0f, 0f, 0f], [32f, 20f, 12f]),
            MakeBox([4f, 20f, 8f], [28f, 32f, 32f]),
        };
        var model = new BlockModel("test", new Dictionary<string, string> { ["all"] = "stone" }, elements);
        return ModelBaker.Bake(model);
    }

    private static ModelDefinition.ElementDefinition MakeBox(float[] from, float[] to)
    {
        var faces = new Dictionary<string, ModelDefinition.FaceDefinition>();
        foreach (var name in new[] { "north", "south", "west", "east", "up", "down" })
            faces[name] = new ModelDefinition.FaceDefinition { Texture = "#all" };
        return new ModelDefinition.ElementDefinition { From = from, To = to, Faces = faces };
    }

    [Fact]
    public void RotatedBakeKeepsCountsAndStaysInsideTheVolume()
    {
        var baked = BakeTestVolume();
        Assert.NotEmpty(baked.Quads);

        for (var facing = 0; facing < 4; facing++)
        {
            var rotated = MultiblockShape.Rotate(baked, facing);
            Assert.Equal(baked.Quads.Length, rotated.Quads.Length);

            foreach (var quad in rotated.Quads)
            {
                foreach (var position in quad.Positions)
                {
                    Assert.InRange(position.X, -1e-4f, MultiblockShape.Size + 1e-4f);
                    Assert.InRange(position.Y, -1e-4f, MultiblockShape.Size + 1e-4f);
                    Assert.InRange(position.Z, -1e-4f, MultiblockShape.Size + 1e-4f);
                }

                Assert.Equal(ModelBaker.DominantFace(quad.Normal), quad.Face);
                // Rotated corners are no longer in CubeFaces order, so the per-corner AO path
                // must never run on a multiblock quad
                Assert.False(quad.IsFullFace);
            }
        }

        // A half turn swaps the two faces of each horizontal axis and leaves top and bottom be
        var north = MultiblockShape.Rotate(baked, 0).Quads.Select(q => q.Face).OrderBy(f => f).ToList();
        var half = MultiblockShape.Rotate(baked, 1).Quads.Select(q => q.Face).OrderBy(f => f).ToList();
        var expected = north.Select(f => f switch { 0 => 1, 1 => 0, 2 => 3, 3 => 2, _ => f })
            .OrderBy(f => f).ToList();
        Assert.Equal(expected, half);
    }
}
