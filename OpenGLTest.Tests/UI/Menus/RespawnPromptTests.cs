using System.Numerics;
using VoxelEngine.UI.Menus;
using Xunit;

namespace VoxelEngine.Tests.UI.Menus;

public sealed class RespawnPromptTests
{
    private static RespawnPrompt DeadFor(float seconds)
    {
        var prompt = new RespawnPrompt();
        prompt.Update(seconds, dead: true);
        return prompt;
    }

    [Fact]
    public void TheScreenWaitsForTheDeathToPlay()
    {
        Assert.False(DeadFor(RespawnPrompt.DelaySeconds * 0.5f).IsShowing);
        Assert.True(DeadFor(RespawnPrompt.DelaySeconds).IsShowing);
    }

    [Fact]
    public void ARespawnCannotBeAskedForWhileAliveOrBeforeTheScreenShows()
    {
        var alive = new RespawnPrompt();
        alive.Update(10f, dead: false);
        Assert.False(alive.Request());

        var early = DeadFor(RespawnPrompt.DelaySeconds * 0.5f);
        Assert.False(early.Request());
        Assert.False(early.Requested);
    }

    [Fact]
    public void AskingCountsOncePerDeathAndComingBackClearsIt()
    {
        var prompt = DeadFor(RespawnPrompt.DelaySeconds);
        Assert.True(prompt.Request());
        Assert.True(prompt.Requested);
        Assert.False(prompt.Request());

        prompt.Update(0.1f, dead: false);
        Assert.False(prompt.Requested);
        Assert.False(prompt.IsShowing);

        // The next death starts from zero
        prompt.Update(RespawnPrompt.DelaySeconds * 0.5f, dead: true);
        Assert.False(prompt.IsShowing);
    }

    [Fact]
    public void ResetDropsTheWholeState()
    {
        var prompt = DeadFor(RespawnPrompt.DelaySeconds);
        prompt.Request();
        prompt.Reset();
        Assert.False(prompt.IsShowing);
        Assert.False(prompt.Requested);
        Assert.Equal(0f, prompt.DeadSeconds);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void OnlyTheRespawnButtonHits(int width, int height)
    {
        var screen = new Vector2(width, height);
        Assert.True(DeathScreenLayout.HitsRespawn(DeathScreenLayout.ButtonCenter(screen), screen));
        Assert.False(DeathScreenLayout.HitsRespawn(Vector2.Zero, screen));
        var size = PauseMenuLayout.ButtonSize;
        var edge = DeathScreenLayout.ButtonCenter(screen) + new Vector2(size.X * 0.5f + 1f, 0f);
        Assert.False(DeathScreenLayout.HitsRespawn(edge, screen));
    }
}
