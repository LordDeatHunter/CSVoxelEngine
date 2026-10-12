using VoxelEngine.Player;
using Xunit;

namespace VoxelEngine.Tests.Player;

/// <summary>
/// The sprint latch (<see cref="SprintToggle"/>): off, it follows the key; on, each fresh press
/// flips it and holding the key does nothing more.
/// </summary>
public sealed class SprintToggleTests
{
    [Fact]
    public void WithoutTheSettingTheLatchFollowsTheKey()
    {
        Assert.False(SprintToggle.Step(false, keyHeld: false, wasHeld: false, toggleMode: false));
        Assert.True(SprintToggle.Step(false, keyHeld: true, wasHeld: false, toggleMode: false));
        Assert.True(SprintToggle.Step(true, keyHeld: true, wasHeld: true, toggleMode: false));
        Assert.False(SprintToggle.Step(true, keyHeld: false, wasHeld: true, toggleMode: false));
    }

    [Fact]
    public void WithTheSettingEachFreshPressFlipsTheLatch()
    {
        var latched = false;
        // Press: on
        latched = SprintToggle.Step(latched, keyHeld: true, wasHeld: false, toggleMode: true);
        Assert.True(latched);
        // Held: unchanged
        latched = SprintToggle.Step(latched, keyHeld: true, wasHeld: true, toggleMode: true);
        Assert.True(latched);
        // Released: still on (the point of the toggle)
        latched = SprintToggle.Step(latched, keyHeld: false, wasHeld: true, toggleMode: true);
        Assert.True(latched);
        // Pressed again: off
        latched = SprintToggle.Step(latched, keyHeld: true, wasHeld: false, toggleMode: true);
        Assert.False(latched);
    }
}
