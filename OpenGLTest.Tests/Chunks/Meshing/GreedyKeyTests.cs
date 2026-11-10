using VoxelEngine.Chunks.Meshing;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>
/// The greedy mesher groups faces by one packed int: every field must come back out exactly as
/// it went in, whatever the others hold, and no id bit may be lost to a neighboring field, or two
/// different blocks would share a plane.
/// </summary>
public sealed class GreedyKeyTests
{
    [Fact]
    public void EachFieldRoundTripsAtItsExtremesIndependently()
    {
        ushort[] ids = [0, ushort.MaxValue];
        int[] aos = [0, GreedyKey.AoMask];
        int[] surfaces = [0, GreedyKey.SurfaceMask];
        foreach (var id in ids)
        foreach (var ao in aos)
        {
            var dry = GreedyKey.Pack(id, ao);
            Assert.Equal(id, GreedyKey.IdOf(dry));
            Assert.Equal(ao, GreedyKey.AoOf(dry));
            Assert.False(GreedyKey.IsSubmerged(dry));
            Assert.Equal(0, GreedyKey.SurfaceCodeOf(dry));

            foreach (var surface in surfaces)
            {
                var wet = GreedyKey.WithSurface(dry, surface);
                Assert.Equal(id, GreedyKey.IdOf(wet));
                Assert.Equal(ao, GreedyKey.AoOf(wet));
                Assert.True(GreedyKey.IsSubmerged(wet));
                Assert.Equal(surface, GreedyKey.SurfaceCodeOf(wet));
            }
        }
    }

    [Fact]
    public void IdsDifferingInAnyBitGiveDifferentKeys()
    {
        int[] aos = [0, GreedyKey.AoMask];
        foreach (var ao in aos)
            for (var b = 0; b < 8 * sizeof(ushort); b++)
            {
                var id = (ushort)(1 << b);
                var key = GreedyKey.Pack(id, ao);
                Assert.NotEqual(GreedyKey.Pack(0, ao), key);
                Assert.Equal(id, GreedyKey.IdOf(key));
                Assert.Equal(ao, GreedyKey.AoOf(key));
            }
    }

    [Fact]
    public void ANegativeKeyStillDecodes()
    {
        var key = GreedyKey.WithSurface(GreedyKey.Pack(ushort.MaxValue, GreedyKey.AoMask), GreedyKey.SurfaceMask);

        Assert.True(key < 0);
        Assert.Equal(ushort.MaxValue, GreedyKey.IdOf(key));
        Assert.Equal(GreedyKey.AoMask, GreedyKey.AoOf(key));
        Assert.True(GreedyKey.IsSubmerged(key));
        Assert.Equal(GreedyKey.SurfaceMask, GreedyKey.SurfaceCodeOf(key));
    }
}
