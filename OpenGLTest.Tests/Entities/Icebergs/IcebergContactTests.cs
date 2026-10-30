using System.Numerics;
using VoxelEngine.Entities.Icebergs;
using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Entities.Icebergs;

public sealed class IcebergContactTests
{
    // A berg box: 3 x 2 x 3 blocks, its top at y = 0.5 (a shelf).
    private static readonly Vector3 BoxMin = new(10f, -1.5f, 10f);
    private static readonly Vector3 BoxMax = new(13f, 0.5f, 13f);

    // The eye of a body whose feet stand at feetY.
    private static Vector3 EyeAt(float x, float feetY, float z) => new(x, feetY + Camera.EyeHeight, z);

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0f, -1f)]
    public void ABodyStoppedByTheSweepAgainstABoxIsInSideContact(float dx, float dz)
    {
        var center = (BoxMin + BoxMax) * 0.5f;
        var direction = new Vector3(dx, 0f, dz);
        // Start four blocks out from the box's center line at the box's mid height, then step in.
        var eye = EyeAt(center.X - dx * 4f, -0.5f, center.Z - dz * 4f);
        while (true)
        {
            var next = eye + direction * BoxSweep.StepSize;
            if (IcebergContact.Overlaps(IcebergContact.BodyMin(next), IcebergContact.BodyMax(next), BoxMin, BoxMax))
                break;
            eye = next;
        }

        Assert.True(IcebergContact.TrySideContact(
            IcebergContact.BodyMin(eye), IcebergContact.BodyMax(eye), BoxMin, BoxMax, out var push));
        Assert.Equal(new Vector2(dx, dz), push);
    }

    [Fact]
    public void ABodyRestingOnTheTopRidesAndDoesNotPush()
    {
        var eye = EyeAt(11.5f, BoxMax.Y, 11.5f);
        var min = IcebergContact.BodyMin(eye);
        var max = IcebergContact.BodyMax(eye);

        Assert.True(IcebergContact.IsRiding(min, max, BoxMin, BoxMax));
        Assert.False(IcebergContact.TrySideContact(min, max, BoxMin, BoxMax, out _));
        Assert.False(IcebergContact.Overlaps(min, max, BoxMin, BoxMax));

        // Standing on the rim, half off the box, still rides it.
        var rim = EyeAt(BoxMax.X, BoxMax.Y, 11.5f);
        Assert.True(IcebergContact.IsRiding(IcebergContact.BodyMin(rim), IcebergContact.BodyMax(rim), BoxMin, BoxMax));
    }

    [Fact]
    public void ABodyBesideButAboveTheTopNeitherRidesNorPushes()
    {
        // Feet a little above the top, flush against the box's +X side.
        var eye = EyeAt(BoxMax.X + PlayerPhysics.PlayerRadius, BoxMax.Y + 0.05f, 11.5f);
        var min = IcebergContact.BodyMin(eye);
        var max = IcebergContact.BodyMax(eye);

        Assert.False(IcebergContact.IsRiding(min, max, BoxMin, BoxMax));
        Assert.False(IcebergContact.TrySideContact(min, max, BoxMin, BoxMax, out _));
    }

    [Fact]
    public void TouchingFacesDoNotOverlap()
    {
        var a = (Min: new Vector3(0f, 0f, 0f), Max: new Vector3(1f, 1f, 1f));
        Assert.False(IcebergContact.Overlaps(a.Min, a.Max, new Vector3(1f, 0f, 0f), new Vector3(2f, 1f, 1f)));
        Assert.False(IcebergContact.Overlaps(a.Min, a.Max, new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f)));
        Assert.False(IcebergContact.Overlaps(a.Min, a.Max, new Vector3(0f, 0f, -1f), new Vector3(1f, 1f, 0f)));
        Assert.True(IcebergContact.Overlaps(a.Min, a.Max, new Vector3(0.5f, 0.5f, 0.5f), new Vector3(2f, 2f, 2f)));
    }
}
