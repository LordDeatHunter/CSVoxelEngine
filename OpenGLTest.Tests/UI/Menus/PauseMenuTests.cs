using System.Numerics;
using VoxelEngine.UI.Menus;
using Xunit;

namespace VoxelEngine.Tests.UI.Menus;

public sealed class PauseMenuTests
{
    private static readonly Vector2 Screen = new(1920f, 1080f);

    [Fact]
    public void StartsClosed()
    {
        var menu = new PauseMenuView();
        Assert.Equal(PauseMenuPage.Closed, menu.Page);
        Assert.False(menu.IsOpen);
    }

    [Theory]
    [InlineData(PauseMenuPage.Main, true)]
    [InlineData(PauseMenuPage.Settings, true)]
    [InlineData(PauseMenuPage.Closed, false)]
    public void IsOpenTracksPage(PauseMenuPage page, bool expected)
    {
        var menu = new PauseMenuView { Page = page };
        Assert.Equal(expected, menu.IsOpen);
    }

    [Fact]
    public void MainButtonsHitInOrder()
    {
        Assert.Equal(PauseMenuHit.Resume,
            PauseMenuLayout.HitAt(PauseMenuLayout.MainButtonCenter(Screen, 0), Screen, PauseMenuPage.Main));
        Assert.Equal(PauseMenuHit.Settings,
            PauseMenuLayout.HitAt(PauseMenuLayout.MainButtonCenter(Screen, 1), Screen, PauseMenuPage.Main));
        Assert.Equal(PauseMenuHit.Exit,
            PauseMenuLayout.HitAt(PauseMenuLayout.MainButtonCenter(Screen, 2), Screen, PauseMenuPage.Main));
    }

    [Fact]
    public void ClicksOutsideButtonsMiss()
    {
        Assert.Equal(PauseMenuHit.None,
            PauseMenuLayout.HitAt(new Vector2(10f, 10f), Screen, PauseMenuPage.Main));
        Assert.Equal(PauseMenuHit.None,
            PauseMenuLayout.HitAt(new Vector2(10f, 10f), Screen, PauseMenuPage.Settings));
        Assert.Equal(PauseMenuHit.None,
            PauseMenuLayout.HitAt(new Vector2(10f, 10f), Screen, PauseMenuPage.Closed));
    }

    [Fact]
    public void SettingsPageHitsBackOnly()
    {
        Assert.Equal(PauseMenuHit.Back,
            PauseMenuLayout.HitAt(PauseMenuLayout.BackButtonCenter(Screen), Screen, PauseMenuPage.Settings));
        Assert.Equal(PauseMenuHit.None,
            PauseMenuLayout.HitAt(PauseMenuLayout.MainButtonCenter(Screen, 0), Screen, PauseMenuPage.Settings));
    }
}
