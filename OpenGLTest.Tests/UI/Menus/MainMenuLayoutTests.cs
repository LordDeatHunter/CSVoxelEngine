using System;
using System.Numerics;
using VoxelEngine.Saves;
using VoxelEngine.UI;
using VoxelEngine.UI.Menus;
using Xunit;

namespace VoxelEngine.Tests.UI.Menus;

public sealed class MainMenuLayoutTests
{
    private static readonly Vector2 Screen = UiCanvas.ReferenceSize;

    private static MainMenuView WorldsView(int count, MainMenuPage page = MainMenuPage.Worlds)
    {
        var view = new MainMenuView { Page = page };
        for (var i = 0; i < count; i++)
            view.Worlds.Add(new WorldInfo($"w{i}", $"World {i}", i, 0, 0, 1, $"dir{i}"));
        return view;
    }

    private static MainMenuHitKind KindAt(Vector2 point, MainMenuView view) =>
        MainMenuLayout.HitAt(point, Screen, view).Kind;

    [Fact]
    public void MainPageButtonsHitInOrderAndGapsMiss()
    {
        var view = new MainMenuView();
        Assert.Equal(MainMenuHitKind.Start, KindAt(MainMenuLayout.MainButtonCenter(Screen, 0), view));
        Assert.Equal(MainMenuHitKind.Multiplayer, KindAt(MainMenuLayout.MainButtonCenter(Screen, 1), view));
        Assert.Equal(MainMenuHitKind.Settings, KindAt(MainMenuLayout.MainButtonCenter(Screen, 2), view));
        Assert.Equal(MainMenuHitKind.Exit, KindAt(MainMenuLayout.MainButtonCenter(Screen, 3), view));

        var gap = (MainMenuLayout.MainButtonCenter(Screen, 0) + MainMenuLayout.MainButtonCenter(Screen, 1)) * 0.5f;
        Assert.Equal(MainMenuHitKind.None, KindAt(gap, view));
    }

    [Fact]
    public void WorldsRowButtonsCarryTheirRowAndTheNameAreaDoesNot()
    {
        var view = WorldsView(3);
        for (var row = 0; row < view.Worlds.Count; row++)
        {
            for (var which = 0; which < 3; which++)
            {
                var hit = MainMenuLayout.HitAt(MainMenuLayout.RowButtonCenter(Screen, row, 0f, which), Screen, view);
                Assert.Equal((MainMenuHitKind)((int)MainMenuHitKind.Play + which), hit.Kind);
                Assert.Equal(row, hit.Row);
            }

            var rowCenterY = MainMenuLayout.RowTop(row, 0f, Screen) + MainMenuLayout.RowHeight * 0.5f;
            var onName = new Vector2(MainMenuLayout.NameLeft(Screen) + 10f, rowCenterY);
            Assert.Equal(MainMenuHitKind.None, KindAt(onName, view));
        }

        Assert.Equal(MainMenuHitKind.None, KindAt(MainMenuLayout.RowButtonCenter(Screen, 5, 0f, 0), view));
    }

    [Fact]
    public void ScrollingShiftsWhichRowAHitLandsOn()
    {
        var rows = (int)MathF.Ceiling(MainMenuLayout.ListHeight(Screen) / MainMenuLayout.RowHeight) + 3;
        var view = WorldsView(rows);
        var point = MainMenuLayout.RowButtonCenter(Screen, 0, 0f, 0);
        Assert.Equal(new MainMenuHit(MainMenuHitKind.Play, 0), MainMenuLayout.HitAt(point, Screen, view));

        view.ScrollOffset = MainMenuLayout.RowHeight * 2f;
        Assert.Equal(new MainMenuHit(MainMenuHitKind.Play, 2), MainMenuLayout.HitAt(point, Screen, view));

        Assert.Equal(MainMenuLayout.ContentHeight(rows) - MainMenuLayout.ListHeight(Screen),
            MainMenuLayout.ClampedOffset(1e6f, rows, Screen), 3);

        Assert.Equal(0f, MainMenuLayout.ClampedOffset(1e6f, 2, Screen));
        Assert.Equal(0f, MainMenuLayout.ClampedOffset(-50f, 2, Screen));
    }

    [Fact]
    public void ConfirmPopupSwallowsClicksBeneathIt()
    {
        var view = WorldsView(3, MainMenuPage.ConfirmDelete);
        Assert.Equal(MainMenuHitKind.None, KindAt(MainMenuLayout.RowButtonCenter(Screen, 0, 0f, 2), view));
        Assert.Equal(MainMenuHitKind.None, KindAt(MainMenuLayout.BottomButtonCenter(Screen, 0), view));

        var box = MainMenuLayout.ConfirmBoxCenter(Screen);
        var outside = box + new Vector2(MainMenuLayout.ConfirmBoxSize.X, 0f);
        Assert.Equal(MainMenuHitKind.None, KindAt(outside, view));

        Assert.Equal(MainMenuHitKind.ConfirmDelete, KindAt(MainMenuLayout.ConfirmYesCenter(Screen), view));
        Assert.Equal(MainMenuHitKind.CancelDelete, KindAt(MainMenuLayout.ConfirmNoCenter(Screen), view));
    }

    [Fact]
    public void CreateAndEditPagesExposeTheirFields()
    {
        var create = new MainMenuView { Page = MainMenuPage.Create };
        Assert.Equal(MainMenuHitKind.NameField, KindAt(MainMenuLayout.NameFieldCenter(Screen), create));
        Assert.Equal(MainMenuHitKind.SeedField, KindAt(MainMenuLayout.SeedFieldCenter(Screen), create));
        Assert.Equal(MainMenuHitKind.Confirm, KindAt(MainMenuLayout.BottomButtonCenter(Screen, 0), create));
        Assert.Equal(MainMenuHitKind.Back, KindAt(MainMenuLayout.BottomButtonCenter(Screen, 1), create));

        var edit = new MainMenuView { Page = MainMenuPage.Edit };
        Assert.Equal(MainMenuHitKind.NameField, KindAt(MainMenuLayout.NameFieldCenter(Screen), edit));
        Assert.Equal(MainMenuHitKind.None, KindAt(MainMenuLayout.SeedFieldCenter(Screen), edit));
        Assert.Equal(MainMenuHitKind.Confirm, KindAt(MainMenuLayout.BottomButtonCenter(Screen, 0), edit));
        Assert.Equal(MainMenuHitKind.Back, KindAt(MainMenuLayout.BottomButtonCenter(Screen, 1), edit));
    }

    [Fact]
    public void EmptyWorldsPageHasNoRowHitsButKeepsCreateAndBack()
    {
        var view = WorldsView(0);
        Assert.Equal(MainMenuHitKind.None, KindAt(MainMenuLayout.RowButtonCenter(Screen, 0, 0f, 0), view));
        Assert.Equal(MainMenuHitKind.Create, KindAt(MainMenuLayout.BottomButtonCenter(Screen, 0), view));
        Assert.Equal(MainMenuHitKind.Back, KindAt(MainMenuLayout.BottomButtonCenter(Screen, 1), view));

        Assert.True(MainMenuLayout.NameMaxWidth(Screen) > 0f);
        var playLeft = MainMenuLayout.RowButtonCenter(Screen, 0, 0f, 0).X - MainMenuLayout.RowButtonSize.X * 0.5f;
        Assert.True(playLeft >= MainMenuLayout.NameLeft(Screen) + MainMenuLayout.NameMaxWidth(Screen));
    }

    [Fact]
    public void AWorldRowShowsItsSeedAndLastPlayedMoment()
    {
        var played = new DateTimeOffset(2026, 10, 11, 14, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var text = MainMenuLayout.WorldDetailText(seed: -42, played);
        Assert.Contains("seed -42", text);
        Assert.Contains("played", text);
        Assert.Contains("2026-10-11", text);

        // A world with no play time says so instead of showing 1970
        var fresh = MainMenuLayout.WorldDetailText(seed: 7, lastPlayedUnixMs: 0);
        Assert.Contains("seed 7", fresh);
        Assert.Contains("never played", fresh);
    }
}
