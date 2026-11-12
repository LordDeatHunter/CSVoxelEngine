using VoxelEngine.Particles;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>
/// The slice-trail glow profile (the twin of <c>slice-trail-fragment.glsl</c>): a dark inner edge, a body
/// brightening toward the tip edge, a core band peaking near that edge, and streaks that only ever dim
/// the body and switch on along the arc.
/// </summary>
public sealed class SliceTrailGlowTests
{
    private const float Span = ParticleConfig.SliceTrailStreakSpanBlocks;
    private const int Grid = 33;

    private static float AlongAt(int i) => 2f * Span * i / (Grid - 1);

    private static float VAt(int j) => (float)j / (Grid - 1);

    [Fact]
    public void TheInnerEdgeIsDark()
    {
        for (var i = 0; i < Grid; i++)
            Assert.Equal(0f, SliceTrailGlow.Glow(AlongAt(i), 0f));
    }

    [Fact]
    public void TheProfileIsBounded()
    {
        for (var i = 0; i < Grid; i++)
        for (var j = 0; j < Grid; j++)
        {
            var glow = SliceTrailGlow.Glow(AlongAt(i), VAt(j));
            var core = SliceTrailGlow.Core(VAt(j));
            Assert.InRange(glow, 0f, 1f);
            Assert.InRange(core, 0f, 1f);
        }
    }

    [Fact]
    public void TheCorePeaksNearTheTipEdge()
    {
        var peak = SliceTrailGlow.Core(ParticleConfig.SliceTrailCoreCenter);
        Assert.True(peak > SliceTrailGlow.Core(0f));
        Assert.True(peak > SliceTrailGlow.Core(1f));
    }

    [Fact]
    public void TheBodyBrightensTowardTheTipEdgeWhereTheStreaksAreOn()
    {
        // Past the streak span every lane's streak is fully on, so the glow is the body alone.
        var previous = SliceTrailGlow.Glow(Span, 0f);
        for (var j = 1; j <= 200; j++)
        {
            var glow = SliceTrailGlow.Glow(Span, j / 200f);
            Assert.True(glow >= previous, $"glow fell from {previous} to {glow} at v = {j / 200f}");
            previous = glow;
        }
    }

    [Fact]
    public void StreaksOnlyDimTheBody()
    {
        for (var j = 0; j < Grid; j++)
        {
            var v = VAt(j);
            var lit = SliceTrailGlow.Glow(Span, v);
            var previous = SliceTrailGlow.Glow(0f, v);
            for (var i = 0; i < Grid; i++)
            {
                var glow = SliceTrailGlow.Glow(AlongAt(i), v);
                Assert.True(glow <= lit + 1e-6f, $"a streak brightened v = {v} at along = {AlongAt(i)}");
                // Within one lane (fixed v) the streak only switches on along the arc.
                Assert.True(glow >= previous - 1e-6f, $"the glow fell along the arc at v = {v}, along = {AlongAt(i)}");
                previous = glow;
            }
        }
    }

    [Fact]
    public void AlphaFollowsTheVertexAlpha()
    {
        for (var i = 0; i < Grid; i++)
        for (var j = 0; j < Grid; j++)
        {
            var along = AlongAt(i);
            var v = VAt(j);
            Assert.Equal(0f, SliceTrailGlow.Alpha(along, v, 0f));
            var previous = 0f;
            for (var k = 1; k <= 10; k++)
            {
                var alpha = SliceTrailGlow.Alpha(along, v, k / 10f);
                Assert.True(alpha >= previous, $"alpha fell with the vertex alpha at along = {along}, v = {v}");
                previous = alpha;
            }
        }
    }

    [Fact]
    public void TheHashIsAUnitFraction()
    {
        for (var x = -200f; x <= 200f; x += 0.37f)
            Assert.InRange(SliceTrailGlow.Hash(x), 0f, 0.9999999f);
        for (var lane = 1; lane <= ParticleConfig.SliceTrailStreakLanes + 1; lane++)
            Assert.InRange(SliceTrailGlow.Hash(lane), 0f, 0.9999999f);
    }
}
