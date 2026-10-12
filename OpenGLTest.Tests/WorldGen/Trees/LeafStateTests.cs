using System;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

public sealed class LeafStateTests
{
    [Fact]
    public void RoundTripsHydrationAndPersistence()
    {
        for (var hydration = 0; hydration <= LeafState.MaxHydration; hydration++)
        foreach (var persistent in new[] { false, true })
        {
            var s = LeafState.Encode(hydration, persistent);
            Assert.Equal(hydration, LeafState.Hydration(s));
            Assert.Equal(persistent, LeafState.IsPersistent(s));
        }
    }

    [Fact]
    public void FieldsAreIndependent()
    {
        for (var hydration = 0; hydration <= LeafState.MaxHydration; hydration++)
        foreach (var persistent in new[] { false, true })
        {
            var s = LeafState.Encode(hydration, persistent);

            var h = LeafState.WithHydration(s, LeafState.MaxHydration - hydration);
            Assert.Equal(LeafState.MaxHydration - hydration, LeafState.Hydration(h));
            Assert.Equal(persistent, LeafState.IsPersistent(h));

            var p = LeafState.WithPersistent(s, !persistent);
            Assert.Equal(!persistent, LeafState.IsPersistent(p));
            Assert.Equal(hydration, LeafState.Hydration(p));

            Assert.Equal(s, LeafState.WithHydration(s, LeafState.Hydration(s)));
            Assert.Equal(s, LeafState.WithPersistent(s, LeafState.IsPersistent(s)));
        }
    }

    [Fact]
    public void HydrationAboveMaxIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LeafState.Encode(LeafState.MaxHydration + 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeafState.Encode(-1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeafState.WithHydration(0, LeafState.MaxHydration + 1));
    }
}
