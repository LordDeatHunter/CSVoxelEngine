using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Icebergs;
using Xunit;

namespace VoxelEngine.Tests.Entities.Icebergs;

public sealed class IcebergShapeTests
{
    public IcebergShapeTests() => TestContent.EnsureLoaded();

    private static IcebergShape[] Shapes() => IcebergShape.ForType(EntityRegistry.Require("iceberg"));

    [Fact]
    public void EveryIcebergVariantFloatsWithDraftAndFreeboard()
    {
        var type = EntityRegistry.Require("iceberg");
        IcebergShape.ValidateContent(type);
        foreach (var shape in IcebergShape.ForType(type))
        {
            Assert.True(shape.Draft > 0f);
            Assert.True(shape.Freeboard > 0f);
        }
    }

    [Fact]
    public void ABiggerVariantOrScaleIsHeavier()
    {
        var byVolume = Shapes().OrderBy(s => s.Volume).ToArray();
        for (var i = 1; i < byVolume.Length; i++)
        {
            if (byVolume[i].Volume > byVolume[i - 1].Volume)
                Assert.True(byVolume[i].Mass(1f) > byVolume[i - 1].Mass(1f));
        }

        foreach (var shape in byVolume)
        {
            var previous = 0f;
            for (var scale = IcebergConfig.ScaleMin; scale <= IcebergConfig.ScaleMax; scale += 0.05f)
            {
                var mass = shape.Mass(scale);
                Assert.True(mass > previous);
                previous = mass;
            }
        }
    }

    [Fact]
    public void PlacedBoxesMatchTheDrawnCubesForEveryTurn()
    {
        const float scale = 1.17f;
        var center = new Vector3(10.5f, 62.3f, -7.25f);
        foreach (var shape in Shapes())
        {
            for (var turns = 0; turns < 4; turns++)
            {
                var matrix = IcebergShape.ModelMatrix(turns, scale, center);
                foreach (var box in shape.Boxes)
                {
                    var min = new Vector3(float.PositiveInfinity);
                    var max = new Vector3(float.NegativeInfinity);
                    foreach (var corner in Corners(box.Min, box.Max))
                    {
                        var drawn = Vector3.Transform(corner, matrix);
                        min = Vector3.Min(min, drawn);
                        max = Vector3.Max(max, drawn);
                    }

                    var placed = IcebergShape.Place(box, turns, scale, center);
                    AssertNear(min, placed.Min);
                    AssertNear(max, placed.Max);
                }
            }
        }
    }

    [Fact]
    public void AQuarterTurnKeepsTheVolumeAndFourTurnsAreTheIdentity()
    {
        var center = new Vector3(3f, 40f, 5f);
        foreach (var shape in Shapes())
        {
            var straight = shape.PlacedBounds(0, 1f, center);
            var turned = shape.PlacedBounds(1, 1f, center);
            var straightSize = straight.Max - straight.Min;
            var turnedSize = turned.Max - turned.Min;
            Assert.Equal(straightSize.X, turnedSize.Z, 4);
            Assert.Equal(straightSize.Z, turnedSize.X, 4);
            Assert.Equal(straightSize.Y, turnedSize.Y, 4);

            for (var turns = 0; turns < 4; turns++)
                Assert.Equal(shape.Volume, PlacedVolume(shape, turns, center), 3);

            foreach (var box in shape.Boxes)
            {
                Assert.Equal(IcebergShape.Place(box, 0, 1.1f, center), IcebergShape.Place(box, 4, 1.1f, center));
                Assert.Equal(box.Min, IcebergShape.RotateQuarter(box.Min, 4));
            }
        }
    }

    private static float PlacedVolume(IcebergShape shape, int turns, Vector3 center)
    {
        var placed = new List<(Vector3 Min, Vector3 Max)>();
        shape.PlaceAll(turns, 1f, center, placed);
        return placed.Sum(b => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) * (b.Max.Z - b.Min.Z));
    }

    private static IEnumerable<Vector3> Corners(Vector3 min, Vector3 max)
    {
        for (var i = 0; i < 8; i++)
            yield return new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
    }
}
