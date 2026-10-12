using Xunit;

namespace silkgltriangle.Tests;

public sealed class SliceComboTests
{
    [Fact]
    public void AFirstPressStartsARightToLeftSwing()
    {
        var combo = new SliceCombo();

        Assert.True(combo.Press());
        Assert.True(combo.Active);
        Assert.False(combo.ReturnSwing);
        Assert.Equal(1, combo.Direction);
    }

    [Fact]
    public void APressDuringASwingBuffersTheReturn()
    {
        var combo = new SliceCombo();
        combo.Press();

        Assert.False(combo.Press());     // buffered, not started
        Assert.False(combo.ReturnSwing); // still the first swing
        Assert.Equal(1, combo.Direction);

        Assert.True(combo.Advance());    // the first swing ends, the return starts
        Assert.True(combo.Active);
        Assert.True(combo.ReturnSwing);
        Assert.Equal(-1, combo.Direction);
    }

    [Fact]
    public void NoBufferedPressLetsTheComboGoIdle()
    {
        var combo = new SliceCombo();
        combo.Press();

        Assert.False(combo.Advance());
        Assert.False(combo.Active);
        Assert.Equal(0, combo.Direction);
    }

    [Fact]
    public void ChainingAlternatesDirections()
    {
        var combo = new SliceCombo();
        combo.Press();
        combo.Press();       // buffer the return
        combo.Advance();     // start the left-to-right return
        combo.Press();       // buffer another
        combo.Advance();     // start a fresh right-to-left

        Assert.True(combo.Active);
        Assert.False(combo.ReturnSwing);
        Assert.Equal(1, combo.Direction);
    }

    [Fact]
    public void ResetDropsEverything()
    {
        var combo = new SliceCombo();
        combo.Press();
        combo.Press();
        combo.Reset();

        Assert.False(combo.Active);
        Assert.False(combo.ReturnSwing);
        Assert.Equal(0, combo.Direction);
        Assert.False(combo.Advance());
    }
}
