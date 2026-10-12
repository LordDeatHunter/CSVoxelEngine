using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// The oasis: its site marks a disc with a smaller pond, its palm is an oasis-only species with a
/// fan crown, and the fan shape stays nested in its radius like every other shape.
/// </summary>
public sealed class OasisTests
{
    public OasisTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AnOasisSiteMarksItsDiscAndPond()
    {
        // A real deep-desert site from the pure hash (the gate makes them sparse, so the scan
        // walks whole cells)
        var found = false;
        var centreX = 0;
        var centreZ = 0;
        var surface = 0;
        for (var cellX = -80; cellX <= 80 && !found; cellX++)
        for (var cellZ = -80; cellZ <= 80 && !found; cellZ++)
        {
            if (!OasisGenerator.TryGetCentre(cellX, cellZ, out var x, out var z, out var level)) continue;
            centreX = x;
            centreZ = z;
            surface = level;
            found = true;
        }
        Assert.True(found, "no oasis site found in the scanned cells");

        Assert.True(OasisGenerator.IsDisc(centreX, centreZ));
        Assert.True(OasisGenerator.IsDisc(centreX + WorldGenerationConfig.OasisRadius - 1, centreZ));
        Assert.False(OasisGenerator.IsDisc(centreX + WorldGenerationConfig.OasisRadius + 1, centreZ));

        // The pond is the inner disc, and the surface is one under the centre's land height, so
        // the pond digs in rather than floods
        Assert.True(OasisGenerator.IsPond(centreX, centreZ));
        Assert.False(OasisGenerator.IsPond(centreX + WorldGenerationConfig.OasisPondRadius + 1, centreZ));
        Assert.True(surface < (int)TerrainGenerator.LandHeight(centreX, centreZ, out _, out _, out _, out _));
    }

    [Fact]
    public void ThePalmIsAnOasisOnlySpeciesWithAFanCrown()
    {
        var palm = TreeSpeciesRegistry.Require("palm");
        Assert.NotNull(palm.Worldgen);
        Assert.True(palm.Worldgen!.Oasis, "the palm is not marked oasis-only");
        Assert.True(palm.HasLeaves);
        Assert.Equal(LeafClusterShape.Fan, palm.LeafShape);

        // An oasis-only species never rides the ordinary biome table: no desert column outside a
        // disc may root it
        var outside = 0;
        for (var x = -600; x <= 600 && outside == 0; x += 7)
        for (var z = -600; z <= 600 && outside == 0; z += 7)
        {
            if (OasisGenerator.IsDisc(x, z)) continue;
            if (!TreeGenerator.TryGetTree(x, z, out var species, out _)) continue;
            Assert.NotEqual("palm", species.Name);
            outside++;
        }
    }

    [Fact]
    public void TheFanShapeIsNestedInItsRadius()
    {
        // Every shape must be nested in its radius: an offset inside the fan at r is inside it at
        // every larger radius, which keeps the decay manager's full-radius offset table exact
        for (var radius = 1; radius < 8; radius++)
        for (var dx = -8; dx <= 8; dx++)
        for (var dy = -8; dy <= 8; dy++)
        for (var dz = -8; dz <= 8; dz++)
        {
            if (!LeafCluster.InShape(LeafClusterShape.Fan, radius, dx, dy, dz)) continue;
            Assert.True(LeafCluster.InShape(LeafClusterShape.Fan, radius + 1, dx, dy, dz),
                $"({dx},{dy},{dz}) inside the fan at {radius} but not at {radius + 1}");
        }

        // And the crown is a cap over the tip: nothing more than a level under it
        Assert.False(LeafCluster.InShape(LeafClusterShape.Fan, 3, 0, -2, 0));
        Assert.True(LeafCluster.InShape(LeafClusterShape.Fan, 3, 0, -1, 0));
    }
}
