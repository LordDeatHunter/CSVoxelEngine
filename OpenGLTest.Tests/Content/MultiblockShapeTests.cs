using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// A multiblock's model is authored once, facing north, and pre-rotated to the other three
/// facings at bake time. These pin the rotation itself: it maps the volume onto itself at every
/// size, it turns the authored front onto the requested side, and a rotated bake keeps every
/// quad while dropping the per-corner AO flag that rotation would misindex.
/// </summary>
public sealed class MultiblockShapeTests
{
    public MultiblockShapeTests() => TestContent.EnsureLoaded();

    private static void AssertClose(Vector3 expected, Vector3 actual, string what)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{what}: expected {expected}, got {actual}");
    }

    public static TheoryData<int> Sizes => new() { 2, 3, 4 };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RotatePointIsIdentityForNorthAndEveryTurnMapsTheVolumeOntoItself(int size)
    {
        var point = new Vector3(0.5f, 1.25f, 0.75f);
        Assert.Equal(point, MultiblockShape.RotatePoint(point, 0, size));

        // A half turn about the footprint's center is the identity and leaves the center be,
        // and it takes the volume's -X/-Z corner to its +X/+Z one
        var center = new Vector3(size / 2f, 1f, size / 2f);
        AssertClose(center, MultiblockShape.RotatePoint(center, 1, size), "center survives a half turn");
        AssertClose(point, MultiblockShape.RotatePoint(MultiblockShape.RotatePoint(point, 1, size), 1, size),
            "half turn is the identity");
        AssertClose(new Vector3(size, 0f, size), MultiblockShape.RotatePoint(Vector3.Zero, 1, size), "min corner");

        // Every turn sends the cell centers of the volume onto cell centers of the same volume,
        // each exactly once, so a rotated structure never leaves its cells
        for (var facing = 0; facing < 4; facing++)
        {
            var landed = new HashSet<(int, int, int)>();
            for (var x = 0; x < size; x++)
            for (var y = 0; y < size; y++)
            for (var z = 0; z < size; z++)
            {
                var turned = MultiblockShape.RotatePoint(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), facing, size);
                var cell = ((int)MathF.Floor(turned.X), (int)MathF.Floor(turned.Y), (int)MathF.Floor(turned.Z));
                AssertClose(new Vector3(cell.Item1 + 0.5f, cell.Item2 + 0.5f, cell.Item3 + 0.5f), turned,
                    $"cell ({x}, {y}, {z}) at facing {facing}");
                Assert.InRange(cell.Item1, 0, size - 1);
                Assert.InRange(cell.Item3, 0, size - 1);
                Assert.Equal(y, cell.Item2);
                Assert.True(landed.Add(cell), $"two cells land on {cell} at facing {facing}, size {size}");
            }
        }
    }

    [Fact]
    public void TheFrontTurnsOntoEveryFacing()
    {

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

    // Two boxes spanning the whole volume in model units (0..16 * size), the way a multiblock
    // model is authored. No cullface and no face covering a cell plane, so the bake validation
    // in ContentRegistry would accept it too.
    private static BakedModel BakeTestVolume(int size)
    {
        var s = size / 2f;
        var elements = new List<ModelDefinition.ElementDefinition>
        {
            MakeBox([0f, 0f, 0f], [32f * s, 20f * s, 12f * s]),
            MakeBox([4f * s, 20f * s, 8f * s], [28f * s, 32f * s, 32f * s]),
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

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RotatedBakeKeepsCountsAndStaysInsideTheVolume(int size)
    {
        var baked = BakeTestVolume(size);
        Assert.NotEmpty(baked.Quads);

        for (var facing = 0; facing < 4; facing++)
        {
            var rotated = MultiblockShape.Rotate(baked, facing, size);
            Assert.Equal(baked.Quads.Length, rotated.Quads.Length);

            foreach (var quad in rotated.Quads)
            {
                foreach (var position in quad.Positions)
                {
                    Assert.InRange(position.X, -1e-4f, size + 1e-4f);
                    Assert.InRange(position.Y, -1e-4f, size + 1e-4f);
                    Assert.InRange(position.Z, -1e-4f, size + 1e-4f);
                }

                Assert.Equal(ModelBaker.DominantFace(quad.Normal), quad.Face);
                // Rotated corners are no longer in CubeFaces order, so the per-corner AO path
                // must never run on a multiblock quad
                Assert.False(quad.IsFullFace);
            }
        }

        // A half turn swaps the two faces of each horizontal axis and leaves top and bottom be
        var north = MultiblockShape.Rotate(baked, 0, size).Quads.Select(q => q.Face).OrderBy(f => f).ToList();
        var half = MultiblockShape.Rotate(baked, 1, size).Quads.Select(q => q.Face).OrderBy(f => f).ToList();
        var expected = north.Select(f => f switch { 0 => 1, 1 => 0, 2 => 3, 3 => 2, _ => f })
            .OrderBy(f => f).ToList();
        Assert.Equal(expected, half);
    }

    private static BlockType AnchorOfSize(int size) => new()
    {
        Id = 0,
        Name = $"test_anchor_{size}",
        DisplayName = "Test Anchor",
        Renderer = BlockRenderer.Multiblock,
        MultiblockSize = size,
    };

    [Fact]
    public void TheBakeAcceptsAModelExactlyAsLargeAsItsVolume()
    {
        // A model authored for a 3x3x3 (0..48 model units) bakes for a size-3 anchor, and the
        // same model is refused on a 2x2x2 anchor, whose volume it overflows
        var three = BakeTestVolume(3);
        ModelBakeSteps.ValidateMultiblockBake(AnchorOfSize(3), three);
        ModelBakeSteps.ValidateMultiblockBake(AnchorOfSize(4), three);
        Assert.Throws<InvalidOperationException>(() => ModelBakeSteps.ValidateMultiblockBake(AnchorOfSize(2), three));
    }

    [Fact]
    public void TheMultiblockSizeDefaultsToTheKilnsAndRefusesWhatTheStateByteCannotHold()
    {
        Assert.Equal(0, BlockSchemaParser.ParseMultiblockSize(null, "test"));
        Assert.Equal(MultiblockShape.DefaultSize,
            BlockSchemaParser.ParseMultiblockSize(new BlockDefinition.MultiblockDefinition(), "test"));
        // The kiln renderer's spots are authored in the kiln's own volume
        Assert.Equal(VoxelEngine.Content.Stations.KilnRenderer.VolumeSize,
            ContentRegistry.RequireBlock("mud_brick_kiln").MultiblockSize);

        for (var size = MultiblockShape.MinSize; size <= MultiblockShape.MaxSize; size++)
            Assert.Equal(size, BlockSchemaParser.ParseMultiblockSize(
                new BlockDefinition.MultiblockDefinition { Size = size }, "test"));
        foreach (var size in new[] { MultiblockShape.MinSize - 1, MultiblockShape.MaxSize + 1 })
            Assert.Throws<InvalidOperationException>(() => BlockSchemaParser.ParseMultiblockSize(
                new BlockDefinition.MultiblockDefinition { Size = size }, "test"));
    }
}
