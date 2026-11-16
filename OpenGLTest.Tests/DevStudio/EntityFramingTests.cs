using System.Numerics;
using VoxelEngine.DevStudio;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.DevStudio;

/// <summary>The entity thumbnail camera: the posed bounds follow each part's matrix, and the framed box lies wholly in
/// view, seen from above and in front of the way the model faces.</summary>
public class EntityFramingTests
{
    [Fact]
    public void TheBoundsTakeEachCubeThroughItsPartsMatrix()
    {
        var cube = new PlayerRig.RigCube { Min = new Vector3(-0.5f, 0f, -0.5f), Max = new Vector3(0.5f, 1f, 0.5f) };
        var (min, max) = EntityFraming.Bounds([
            (Matrix4x4.CreateTranslation(0f, 2f, 0f), new[] { cube }),
            (Matrix4x4.CreateScale(2f), new[] { cube }),
        ]);

        Assert.Equal(new Vector3(-1f, 0f, -1f), min);
        Assert.Equal(new Vector3(1f, 3f, 1f), max);
    }

    [Theory]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, 0f, -1f)]
    public void TheFramedBoxIsWhollyInViewFromAboveAndInFront(float x, float y, float z)
    {
        var front = new Vector3(x, y, z);
        var min = new Vector3(-0.4f, 0f, -1.2f);
        var max = new Vector3(0.6f, 2.5f, 0.8f);

        var (viewProjection, eye) = EntityFraming.Frame(min, max, front);

        var center = (min + max) * 0.5f;
        Assert.True(eye.Y > center.Y);
        Assert.True(Vector3.Dot(eye - center, front) > 0f);
        for (var corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);
            var clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            Assert.InRange(ndc.X, -1f, 1f);
            Assert.InRange(ndc.Y, -1f, 1f);
            Assert.InRange(ndc.Z, 0f, 1f);
        }
    }
}
