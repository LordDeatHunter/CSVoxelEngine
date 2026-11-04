using VoxelEngine.Interaction;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

public sealed class SwordComboTests
{
    [Fact]
    public void AFirstPressStartsTheCut()
    {
        var combo = new SwordCombo();

        Assert.True(combo.Press());
        Assert.True(combo.Active);
        Assert.Equal(SwordSwing.Cut, combo.Swing);
    }

    [Fact]
    public void BufferedPressesRunTheWholeChainInOrderAndWrapToTheCut()
    {
        var combo = new SwordCombo();
        combo.Press();
        var seen = new[] { combo.Swing };

        Assert.False(combo.Press());                 // buffered, not started
        Assert.Equal(SwordSwing.Cut, combo.Swing);   // still the cut
        Assert.True(combo.Advance());                // the cut ends, the back cut starts
        seen = [.. seen, combo.Swing];
        combo.Press();
        Assert.True(combo.Advance());
        seen = [.. seen, combo.Swing];
        combo.Press();
        Assert.True(combo.Advance());
        seen = [.. seen, combo.Swing];

        Assert.Equal([SwordSwing.Cut, SwordSwing.BackCut, SwordSwing.Chop, SwordSwing.Cut], seen);
        // Each swing's successor is the chain's next, and the chain is a cycle
        foreach (var swing in SwordSwings.All)
            Assert.NotEqual(swing, SwordSwings.Next(swing));
        Assert.Equal(SwordSwing.Cut, SwordSwings.Next(SwordSwing.Chop));
    }

    [Fact]
    public void APressInsideTheWindowAfterASwingContinuesTheChainAndALaterOneStartsOver()
    {
        var combo = new SwordCombo();
        combo.Press();
        Assert.False(combo.Advance());   // the cut ends with nothing buffered
        Assert.False(combo.Active);

        combo.Update(SwordCombo.ChainWindowSeconds * 0.5f);
        Assert.True(combo.Press());
        Assert.Equal(SwordSwing.BackCut, combo.Swing);

        combo.Advance();
        combo.Update(SwordCombo.ChainWindowSeconds * 1.5f);
        Assert.True(combo.Press());
        Assert.Equal(SwordSwing.Cut, combo.Swing);
    }

    [Fact]
    public void TheWindowOnlyAgesWhileIdle()
    {
        var combo = new SwordCombo();
        combo.Press();
        // Time passing during the swing itself never spends the window that opens after it
        combo.Update(SwordCombo.ChainWindowSeconds * 10f);
        Assert.False(combo.Advance());
        combo.Update(SwordCombo.ChainWindowSeconds * 0.9f);
        Assert.True(combo.Press());
        Assert.Equal(SwordSwing.BackCut, combo.Swing);
    }

    [Fact]
    public void ResetDropsEverything()
    {
        var combo = new SwordCombo();
        combo.Press();
        combo.Press();
        combo.Reset();

        Assert.False(combo.Active);
        Assert.False(combo.Advance());
        // No window survives a reset either: the next press is a fresh cut
        Assert.True(combo.Press());
        Assert.Equal(SwordSwing.Cut, combo.Swing);
    }
}
