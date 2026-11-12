using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.InventoryPanel;

/// <summary>The panel eases open and shut: monotone, exactly 0 and 1 at the ends, the slide spent when settled.</summary>
public class PanelTransitionTests
{
    private static PanelTransition Run(bool open, float seconds, PanelTransition? transition = null, float from = 0f)
    {
        transition ??= new PanelTransition();
        for (var t = 0f; t <= seconds; t += 0.01f)
            transition.Step(open, from + t);
        return transition;
    }

    [Fact]
    public void OpeningRisesMonotonicallyToExactlyOne()
    {
        var transition = new PanelTransition();
        var previous = 0f;
        for (var t = 0f; t < 1f; t += 0.01f)
        {
            transition.Step(true, t);
            Assert.True(transition.Alpha >= previous);
            previous = transition.Alpha;
        }
        Assert.Equal(1f, transition.Alpha);
        Assert.Equal(0f, transition.SlideY);
    }

    [Fact]
    public void ClosingEndsHiddenAndItIsQuickerThanOpening()
    {
        var transition = Run(true, 1f);
        Run(false, 1f, transition, from: 1.01f);
        Assert.False(transition.Visible);
        Assert.Equal(0f, transition.Alpha);

        Assert.True(HudConfig.PanelCloseSeconds <= HudConfig.PanelOpenSeconds);
    }

    [Fact]
    public void ABigTimeJumpNeverSnapsTheTransition()
    {
        var transition = new PanelTransition();
        transition.Step(true, 0f);
        transition.Step(true, 50f);
        Assert.True(transition.Alpha < 1f);
    }

    [Fact]
    public void ResetClosesIt()
    {
        var transition = Run(true, 1f);
        transition.Reset();
        Assert.False(transition.Visible);
    }
}
