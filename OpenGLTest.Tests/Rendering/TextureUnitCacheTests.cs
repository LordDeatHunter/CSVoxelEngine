using VoxelEngine.Rendering;
using Xunit;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// <c>TextureUnitCache</c> is the GL-free bookkeeping behind the render-state rebind cache
/// (PERF_TODO R5): which unit the context has selected and what is bound to each tracked unit. Only
/// the bookkeeping is covered here. The GL half — that a <em>skipped</em> <c>glActiveTexture</c> or
/// <c>glBindTexture</c> really did leave the context in the state the cache claims, and that no site
/// changes a unit behind <c>RenderManager</c>'s three primitives without invalidating — needs a live
/// GL context and is not reachable from a test, exactly as <c>HalfResTargetTests</c> and
/// <c>CloudCopiesTests</c> say of their own GL halves. It was proved instead by a temporary
/// readback harness run over the scripted visual scenarios, and is re-checked by the screenshot gate.
///
/// <para><c>ReplayingBindWorldTexturesIssuesNothingTheSecondTime</c> is the load-bearing fact: it
/// drives a local reimplementation of <c>RenderManager.BindWorldTextures</c>' body against the cache
/// and counts the GL calls that body would issue, which is where the round's quotable structural
/// figure (10 calls per replay, down to 0 when nothing moved and 1 after an item-sprite swap) comes
/// from. <c>MarkActiveStillReportsTheUnitActiveWhenTheGlCallIsSkipped</c> is the other one that
/// matters: <c>BindUnit</c>'s eager select is what keeps the active-unit trajectory identical to the
/// uncached code, and that only works because a skipped select still updates the record.</para>
/// </summary>
public sealed class TextureUnitCacheTests
{
    // The two GL texture targets this engine binds. Spelled as their raw enum values rather than
    // pulled from Silk.NET, because the cache itself takes a plain int and is deliberately GL-free.
    private const int Tex2D = 0x0DE1;        // GL_TEXTURE_2D
    private const int Tex2DArray = 0x8C1A;   // GL_TEXTURE_2D_ARRAY

    // Stand-ins for the five world textures, in BindWorldTextures' own order.
    private const uint Shadow = 11, Refl = 22, SceneColor = 33, SceneDepth = 44, Atlas = 55;

    /// <summary><c>RenderManager.BindLazy</c>, reimplemented: returns how many GL calls it would
    /// issue (0 on a full match, otherwise the optional select plus the always-issued bind).</summary>
    private static int BindLazy(TextureUnitCache cache, int unit, int target, uint texture)
    {
        if (cache.Matches(unit, target, texture)) return 0;
        var calls = cache.MarkActive(unit) ? 1 : 0;
        calls++;                                  // the glBindTexture itself
        cache.MarkBound(unit, target, texture);
        return calls;
    }

    /// <summary><c>RenderManager.BindUnit</c>, reimplemented: the select is unconditional work for
    /// the cache even when the GL call is skipped, which is the trajectory guarantee.</summary>
    private static int BindUnit(TextureUnitCache cache, int unit, int target, uint texture)
    {
        var calls = cache.MarkActive(unit) ? 1 : 0;
        if (cache.MarkBound(unit, target, texture)) calls++;
        return calls;
    }

    /// <summary><c>RenderManager.BindWorldTextures</c>' body, reimplemented: five lazy binds in the
    /// engine's order (1, 2, 3, 4, 0) and the unconditional trailing select of unit 0. Returns the
    /// GL call count that body would issue.</summary>
    private static int ReplayBindWorldTextures(TextureUnitCache cache, uint atlas = Atlas)
    {
        var calls = BindLazy(cache, 1, Tex2D, Shadow);
        calls += BindLazy(cache, 2, Tex2D, Refl);
        calls += BindLazy(cache, 3, Tex2D, SceneColor);
        calls += BindLazy(cache, 4, Tex2D, SceneDepth);
        calls += BindLazy(cache, 0, Tex2DArray, atlas);
        if (cache.MarkActive(0)) calls++;
        return calls;
    }

    [Fact]
    public void FirstBindOfAUnitIsAlwaysNeeded()
    {
        var cache = new TextureUnitCache();

        Assert.Equal(-1, cache.ActiveUnit);
        Assert.True(cache.MarkActive(0));
        Assert.True(cache.MarkBound(0, Tex2DArray, Atlas));
    }

    [Fact]
    public void RepeatBindOfTheSameTextureIsSkipped()
    {
        var cache = new TextureUnitCache();

        Assert.True(cache.MarkBound(0, Tex2DArray, 7));
        Assert.False(cache.MarkBound(0, Tex2DArray, 7));
    }

    [Fact]
    public void MatchesIsAPureQuery()
    {
        var cache = new TextureUnitCache();

        Assert.False(cache.Matches(0, Tex2DArray, 7));
        // A query on a fresh cache must not have recorded anything, so the bind is still needed.
        Assert.True(cache.MarkBound(0, Tex2DArray, 7));
        Assert.True(cache.Matches(0, Tex2DArray, 7));

        // Querying repeatedly changes nothing either: the bind is still skippable, and the unit
        // the cache believes is active is untouched by a query.
        Assert.True(cache.MarkActive(2));
        Assert.True(cache.Matches(0, Tex2DArray, 7));
        Assert.False(cache.Matches(0, Tex2D, 7));
        Assert.Equal(2, cache.ActiveUnit);
        Assert.False(cache.MarkBound(0, Tex2DArray, 7));
    }

    [Fact]
    public void MarkActiveStillReportsTheUnitActiveWhenTheGlCallIsSkipped()
    {
        var cache = new TextureUnitCache();

        Assert.True(cache.MarkActive(3));
        Assert.Equal(3, cache.ActiveUnit);
        Assert.False(cache.MarkActive(3));
        Assert.Equal(3, cache.ActiveUnit);
    }

    [Fact]
    public void ADifferentTextureOnTheSameUnitNeedsABind()
    {
        var cache = new TextureUnitCache();

        Assert.True(cache.MarkBound(2, Tex2D, 7));
        Assert.True(cache.MarkBound(2, Tex2D, 9));
        Assert.False(cache.MarkBound(2, Tex2D, 9));
        Assert.False(cache.Matches(2, Tex2D, 7));
    }

    [Fact]
    public void ADifferentTargetOnTheSameUnitReplacesTheRecord()
    {
        var cache = new TextureUnitCache();

        Assert.True(cache.MarkBound(0, Tex2DArray, 7));
        Assert.True(cache.MarkBound(0, Tex2D, 9));
        // Conservative by design: the displaced pair is forgotten rather than kept in a second
        // slot, so asking for it again costs one needless rebind and never a missed one.
        Assert.True(cache.MarkBound(0, Tex2DArray, 7));
    }

    [Fact]
    public void InvalidateMakesEveryUnitAndTheActiveUnitUnknown()
    {
        var cache = new TextureUnitCache();
        ReplayBindWorldTextures(cache);
        Assert.Equal(0, cache.ActiveUnit);

        cache.Invalidate();

        Assert.Equal(-1, cache.ActiveUnit);
        Assert.True(cache.MarkActive(0));
        cache.Invalidate();
        Assert.False(cache.Matches(1, Tex2D, Shadow));
        Assert.False(cache.Matches(2, Tex2D, Refl));
        Assert.False(cache.Matches(3, Tex2D, SceneColor));
        Assert.False(cache.Matches(4, Tex2D, SceneDepth));
        Assert.False(cache.Matches(0, Tex2DArray, Atlas));
        // And the first replay after an invalidate pays the full price again.
        Assert.Equal(10, ReplayBindWorldTextures(cache));
    }

    [Fact]
    public void UntrackedUnitsAlwaysNeedABind()
    {
        var cache = new TextureUnitCache();

        // Unit 5 is the underwater rays' half-res texture: above TrackedUnits, so nothing about it
        // is ever remembered and it can never produce a stale hit.
        Assert.True(cache.MarkBound(TextureUnitCache.TrackedUnits, Tex2D, 7));
        Assert.True(cache.MarkBound(TextureUnitCache.TrackedUnits, Tex2D, 7));
        Assert.False(cache.Matches(TextureUnitCache.TrackedUnits, Tex2D, 7));
        Assert.False(cache.Matches(-1, Tex2D, 7));
        Assert.True(cache.MarkBound(-1, Tex2D, 7));
    }

    [Fact]
    public void ReplayingBindWorldTexturesIssuesNothingTheSecondTime()
    {
        var cache = new TextureUnitCache();

        // Five selects plus five binds, exactly what the uncached method issued every single time.
        Assert.Equal(10, ReplayBindWorldTextures(cache));
        Assert.Equal(0, cache.ActiveUnit);

        Assert.Equal(0, ReplayBindWorldTextures(cache));
        Assert.Equal(0, cache.ActiveUnit);
    }

    [Fact]
    public void TheEntityPassRestoreCostsOneCall()
    {
        var cache = new TextureUnitCache();
        ReplayBindWorldTextures(cache);

        // RenderItemDrops swapping unit 0 to an item sprite: the select is already satisfied, so
        // this is one glBindTexture.
        const uint itemTexture = 99;
        Assert.Equal(1, BindUnit(cache, 0, Tex2DArray, itemTexture));

        // ...and putting the world textures back is the other one. Units 1-4 never moved.
        Assert.Equal(1, ReplayBindWorldTextures(cache));
        Assert.Equal(0, cache.ActiveUnit);
    }

    [Fact]
    public void ATrailingSelectIsIssuedWhenAnotherUnitWasLeftActive()
    {
        var cache = new TextureUnitCache();
        ReplayBindWorldTextures(cache);

        Assert.True(cache.MarkActive(3));

        // Every binding still matches, so the five binds are all skipped and the only call left is
        // the trailing select that restores the "unit 0 is active on the way out" postcondition.
        Assert.Equal(1, ReplayBindWorldTextures(cache));
        Assert.Equal(0, cache.ActiveUnit);
    }
}
