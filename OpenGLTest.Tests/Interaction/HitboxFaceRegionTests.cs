using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The surface the face highlight lights: the hit box's face plus every coplanar face connected to it, so a shaped
/// block's flat top lights as a whole, while a lower or detached level stays dark.
/// </summary>
public sealed class HitboxFaceRegionTests
{
    private static BlockHitbox Box(float x0, float z0, float x1, float z1, float top) =>
        new(new Vector3(x0, 0, z0), new Vector3(x1, top, z1));

    [Fact]
    public void ARimConnectedAroundAPocketLightsAsOneSurfaceAndTheFloorStaysOut()
    {
        BlockHitbox[] boxes =
        [
            Box(0, 0, 1, 0.25f, 0.5f), Box(0, 0.25f, 0.25f, 1, 0.5f), Box(0.25f, 0.75f, 1, 1, 0.5f),
            Box(0.75f, 0.25f, 1, 0.75f, 0.5f), Box(0.25f, 0.25f, 0.75f, 0.75f, 0.25f),
        ];
        var region = HitboxFaceRegion.Connected(boxes, 2, Vector3.UnitY);
        Assert.Equal([0, 1, 2, 3], region.OrderBy(i => i));
        Assert.Equal(2, region[0]);
    }

    [Fact]
    public void ADetachedBoxInTheSamePlaneIsNotConnected()
    {
        BlockHitbox[] boxes = [Box(0, 0, 0.2f, 0.2f, 0.5f), Box(0.6f, 0.6f, 1, 1, 0.5f)];
        Assert.Equal([0], HitboxFaceRegion.Connected(boxes, 0, Vector3.UnitY));
    }

    [Fact]
    public void SideFacesConnectOnlyAlongTheirOwnPlane()
    {
        BlockHitbox[] boxes = [Box(0, 0, 0.5f, 1, 0.5f), Box(0.5f, 0, 1, 0.5f, 0.5f), Box(0.5f, 0.5f, 1, 1, 0.5f)];
        // The +Z faces of the first and third boxes share z = 1 and touch at x = 0.5; the second ends at z = 0.5.
        var region = HitboxFaceRegion.Connected(boxes, 0, Vector3.UnitZ);
        Assert.Equal([0, 2], region.OrderBy(i => i));
    }
}
