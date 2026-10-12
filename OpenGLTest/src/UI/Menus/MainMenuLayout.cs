using System;
using System.Numerics;
using VoxelEngine.Rendering;

namespace VoxelEngine.UI.Menus;

/// <summary>
/// Pure screen layout of the main menu in canvas units (see <see cref="UiCanvas"/>; origin
/// top-left, Y down): button, list-row, field and popup rectangles plus click hit-testing, with
/// no GL dependency. <see cref="UIRenderer"/>'s drawing and <see cref="HitAt"/> both read these
/// methods, so what is drawn and what is clicked cannot drift apart (the DrawPauseMenu rule).
/// The main page reuses <see cref="PauseMenuLayout"/>'s three-button stack.
/// </summary>
public static class MainMenuLayout
{
    /// <summary>
    /// A world row's second line: the seed and when the world was last played, so two saves are
    /// told apart at a glance. A world with no play time yet (a fresh save) says so.
    /// </summary>
    public static string WorldDetailText(int seed, long lastPlayedUnixMs) =>
        lastPlayedUnixMs <= 0
            ? $"seed {seed}   never played"
            : $"seed {seed}   played {DateTimeOffset.FromUnixTimeMilliseconds(lastPlayedUnixMs).ToLocalTime():yyyy-MM-dd HH:mm}";

    // --- Worlds page ---
    public const float RowHeight = 64f;
    public const float PanelWidth = 960f;
    public const float PanelTop = 140f;
    public const float ListBottomMargin = 160f;
    public const float RowPad = 12f;
    public const float RowButtonWidth = 120f;
    public const float RowButtonHeight = 44f;
    public const float RowButtonGap = 12f;

    /// <summary>Rows one mouse-wheel notch scrolls the worlds list.</summary>
    public const float WheelStepRows = 3f;

    public const float TitleTopY = 64f;

    private const float BottomButtonGap = 32f;

    public static float PanelLeft(Vector2 screenSize) => (screenSize.X - PanelWidth) * 0.5f;

    // --- Main page: a four-button stack (START, MULTIPLAYER, SETTINGS, EXIT). The pause menu keeps
    // PauseMenuLayout's three-button stack, so the two pages no longer share a layout. ---
    public static Vector2 MainButtonCenter(Vector2 screenSize, int index)
    {
        const int count = 4;
        var total = count * PauseMenuLayout.ButtonHeight + (count - 1) * PauseMenuLayout.ButtonGap;
        var top = screenSize.Y * 0.5f - total * 0.5f + PauseMenuLayout.ButtonHeight * 0.5f;
        return new Vector2(screenSize.X * 0.5f,
            top + index * (PauseMenuLayout.ButtonHeight + PauseMenuLayout.ButtonGap));
    }

    public static float MainTitleTop(Vector2 screenSize)
    {
        const int count = 4;
        var total = count * PauseMenuLayout.ButtonHeight + (count - 1) * PauseMenuLayout.ButtonGap;
        return screenSize.Y * 0.5f - total * 0.5f - 80f;
    }

    public static float ListTop(Vector2 screenSize) => PanelTop;

    public static float ListHeight(Vector2 screenSize) => screenSize.Y - PanelTop - ListBottomMargin;

    public static float ContentHeight(int rowCount) => rowCount * RowHeight;

    public static float ClampedOffset(float offset, int rowCount, Vector2 screenSize) =>
        ScrollList.Clamp(offset, ContentHeight(rowCount), ListHeight(screenSize));

    public static (int First, int EndExclusive) VisibleRows(float offset, int rowCount, Vector2 screenSize) =>
        ScrollList.VisibleRange(offset, RowHeight, rowCount, ListHeight(screenSize));

    public static float RowTop(int row, float offset, Vector2 screenSize) =>
        ListTop(screenSize) + ScrollList.RowTop(row, offset, RowHeight);

    public static Vector2 RowButtonSize => new(RowButtonWidth, RowButtonHeight);

    /// <summary>
    /// Center of a row's button: <paramref name="which"/> 0 = PLAY, 1 = EDIT, 2 = DELETE, right
    /// aligned inside the row.
    /// </summary>
    public static Vector2 RowButtonCenter(Vector2 screenSize, int row, float offset, int which) =>
        RowButtonCenter(screenSize, row, offset, which, 3);

    /// <summary>
    /// Center of a row's button for a row of <paramref name="buttonCount"/> right-aligned buttons
    /// (the server rows use two: CONNECT, DELETE).
    /// </summary>
    public static Vector2 RowButtonCenter(Vector2 screenSize, int row, float offset, int which, int buttonCount)
    {
        var x = PanelLeft(screenSize) + PanelWidth - RowPad
                - (buttonCount - 1 - which) * (RowButtonWidth + RowButtonGap) - RowButtonWidth * 0.5f;
        return new Vector2(x, RowTop(row, offset, screenSize) + RowHeight * 0.5f);
    }

    /// <summary>Left edge of a row's name and seed lines.</summary>
    public static float NameLeft(Vector2 screenSize) => PanelLeft(screenSize) + RowPad;

    /// <summary>
    /// Widest a row's name line may be (the FitText bound): the panel minus the pads and the
    /// three buttons with their gaps, so a long name never runs under PLAY.
    /// </summary>
    public static float NameMaxWidth(Vector2 screenSize) =>
        PanelWidth - 3f * RowPad - 3f * RowButtonWidth - 2f * RowButtonGap;

    /// <summary>The list row under <paramref name="point"/>, or null outside the panel or past the last row.</summary>
    public static int? RowAt(Vector2 point, Vector2 screenSize, float offset, int rowCount)
    {
        var left = PanelLeft(screenSize);
        if (point.X < left || point.X > left + PanelWidth) return null;
        return ScrollList.RowAt(point.Y - ListTop(screenSize), offset, RowHeight, rowCount, ListHeight(screenSize));
    }

    /// <summary>Whether <paramref name="point"/> is over the worlds list's viewport (the wheel target).</summary>
    public static bool IsOverList(Vector2 point, Vector2 screenSize)
    {
        var left = PanelLeft(screenSize);
        var top = ListTop(screenSize);
        return point.X >= left && point.X <= left + PanelWidth
            && point.Y >= top && point.Y < top + ListHeight(screenSize);
    }

    /// <summary>
    /// Center of one of the two bottom buttons (0 left of center, 1 right): CREATE / BACK on the
    /// worlds and create pages, SAVE / BACK on the edit page.
    /// </summary>
    public static Vector2 BottomButtonCenter(Vector2 screenSize, int index)
    {
        var dx = (PauseMenuLayout.ButtonWidth + BottomButtonGap) * 0.5f;
        return new Vector2(screenSize.X * 0.5f + (index == 0 ? -dx : dx),
            screenSize.Y - ListBottomMargin * 0.5f);
    }

    /// <summary>
    /// Center of one of three bottom buttons (0, 1, 2 left to right): ADD SERVER / DIRECT CONNECT /
    /// BACK on the multiplayer page. Slightly narrower than <see cref="PauseMenuLayout.ButtonWidth"/>
    /// so the three still fit the panel.
    /// </summary>
    public static Vector2 TripleButtonCenter(Vector2 screenSize, int index)
    {
        const float width = 280f;
        var total = 3 * width + 2 * BottomButtonGap;
        var left = screenSize.X * 0.5f - total * 0.5f + width * 0.5f;
        return new Vector2(left + index * (width + BottomButtonGap), screenSize.Y - ListBottomMargin * 0.5f);
    }

    public static Vector2 TripleButtonSize => new(280f, PauseMenuLayout.ButtonHeight);

    /// <summary>Top of the notice line, between the list bottom and the bottom buttons.</summary>
    public static float NoticeTop(Vector2 screenSize)
    {
        var listBottom = ListTop(screenSize) + ListHeight(screenSize);
        var buttonTop = BottomButtonCenter(screenSize, 0).Y - PauseMenuLayout.ButtonHeight * 0.5f;
        return listBottom + (buttonTop - listBottom) * 0.25f;
    }

    // --- Create / Edit pages ---
    public static Vector2 FieldSize => new(560f, 56f);

    public static Vector2 NameFieldCenter(Vector2 screenSize) =>
        new(screenSize.X * 0.5f, screenSize.Y * 0.5f - 60f);

    public static Vector2 SeedFieldCenter(Vector2 screenSize) =>
        new(screenSize.X * 0.5f, screenSize.Y * 0.5f + 40f);

    /// <summary>Top of the label drawn above a field centered at <paramref name="center"/>.</summary>
    public static float FieldLabelTop(Vector2 center) => center.Y - FieldSize.Y * 0.5f - 26f;

    // --- Delete confirmation popup ---
    public static Vector2 ConfirmBoxSize => new(640f, 240f);

    public static Vector2 ConfirmBoxCenter(Vector2 screenSize) => screenSize * 0.5f;

    public static Vector2 ConfirmButtonSize => new(200f, 48f);

    private static float ConfirmButtonY(Vector2 screenSize) =>
        ConfirmBoxCenter(screenSize).Y + ConfirmBoxSize.Y * 0.5f - 24f - ConfirmButtonSize.Y * 0.5f;

    public static Vector2 ConfirmYesCenter(Vector2 screenSize) =>
        new(screenSize.X * 0.5f - 24f - ConfirmButtonSize.X * 0.5f, ConfirmButtonY(screenSize));

    public static Vector2 ConfirmNoCenter(Vector2 screenSize) =>
        new(screenSize.X * 0.5f + 24f + ConfirmButtonSize.X * 0.5f, ConfirmButtonY(screenSize));

    /// <summary>Top edge (Y) of a page's title text.</summary>
    public static float TitleTop(Vector2 screenSize, MainMenuPage page) =>
        page == MainMenuPage.Main ? MainTitleTop(screenSize) : TitleTopY;

    public static MainMenuHit HitAt(Vector2 point, Vector2 screenSize, MainMenuView view)
    {
        switch (view.Page)
        {
            case MainMenuPage.Main:
            {
                for (var i = 0; i < 4; i++)
                {
                    if (InBox(point, MainButtonCenter(screenSize, i), PauseMenuLayout.ButtonSize))
                        return new((MainMenuHitKind)((int)MainMenuHitKind.Start + i));
                }
                return new(MainMenuHitKind.None);
            }

            case MainMenuPage.Worlds:
            {
                var count = view.Worlds.Count;
                var offset = ClampedOffset(view.ScrollOffset, count, screenSize);
                if (RowAt(point, screenSize, offset, count) is { } row)
                {
                    for (var which = 0; which < 3; which++)
                    {
                        if (InBox(point, RowButtonCenter(screenSize, row, offset, which), RowButtonSize))
                            return new((MainMenuHitKind)((int)MainMenuHitKind.Play + which), row);
                    }
                    return new(MainMenuHitKind.None);
                }
                return BottomHit(point, screenSize, MainMenuHitKind.Create);
            }

            case MainMenuPage.Create:
                if (InBox(point, NameFieldCenter(screenSize), FieldSize)) return new(MainMenuHitKind.NameField);
                if (InBox(point, SeedFieldCenter(screenSize), FieldSize)) return new(MainMenuHitKind.SeedField);
                return BottomHit(point, screenSize, MainMenuHitKind.Confirm);

            case MainMenuPage.Edit:
                if (InBox(point, NameFieldCenter(screenSize), FieldSize)) return new(MainMenuHitKind.NameField);
                return BottomHit(point, screenSize, MainMenuHitKind.Confirm);

            case MainMenuPage.ConfirmDelete:
                // The popup swallows every click beneath it: the rows are never tested here.
                if (InBox(point, ConfirmYesCenter(screenSize), ConfirmButtonSize))
                    return new(MainMenuHitKind.ConfirmDelete);
                if (InBox(point, ConfirmNoCenter(screenSize), ConfirmButtonSize))
                    return new(MainMenuHitKind.CancelDelete);
                return new(MainMenuHitKind.None);

            case MainMenuPage.Multiplayer:
            {
                var count = view.Servers.Count;
                var offset = ClampedOffset(view.ServerScrollOffset, count, screenSize);
                if (RowAt(point, screenSize, offset, count) is { } row)
                {
                    if (InBox(point, RowButtonCenter(screenSize, row, offset, 0, 2), RowButtonSize))
                        return new(MainMenuHitKind.Connect, row);
                    if (InBox(point, RowButtonCenter(screenSize, row, offset, 1, 2), RowButtonSize))
                        return new(MainMenuHitKind.DeleteServer, row);
                    return new(MainMenuHitKind.None);
                }
                if (InBox(point, TripleButtonCenter(screenSize, 0), TripleButtonSize))
                    return new(MainMenuHitKind.AddServer);
                if (InBox(point, TripleButtonCenter(screenSize, 1), TripleButtonSize))
                    return new(MainMenuHitKind.DirectConnect);
                if (InBox(point, TripleButtonCenter(screenSize, 2), TripleButtonSize))
                    return new(MainMenuHitKind.Back);
                return new(MainMenuHitKind.None);
            }

            case MainMenuPage.AddServer:
                if (InBox(point, NameFieldCenter(screenSize), FieldSize)) return new(MainMenuHitKind.ServerNameField);
                if (InBox(point, SeedFieldCenter(screenSize), FieldSize)) return new(MainMenuHitKind.AddressField);
                return BottomHit(point, screenSize, MainMenuHitKind.Confirm);

            case MainMenuPage.DirectConnect:
                if (InBox(point, NameFieldCenter(screenSize), FieldSize)) return new(MainMenuHitKind.AddressField);
                return BottomHit(point, screenSize, MainMenuHitKind.Connect);

            case MainMenuPage.ConfirmDeleteServer:
                if (InBox(point, ConfirmYesCenter(screenSize), ConfirmButtonSize))
                    return new(MainMenuHitKind.ConfirmDeleteServer);
                if (InBox(point, ConfirmNoCenter(screenSize), ConfirmButtonSize))
                    return new(MainMenuHitKind.CancelDeleteServer);
                return new(MainMenuHitKind.None);

            default:
                // Settings: SettingsLayout owns that page's clicks.
                return new(MainMenuHitKind.None);
        }
    }

    private static MainMenuHit BottomHit(Vector2 point, Vector2 screenSize, MainMenuHitKind left)
    {
        if (InBox(point, BottomButtonCenter(screenSize, 0), PauseMenuLayout.ButtonSize)) return new(left);
        if (InBox(point, BottomButtonCenter(screenSize, 1), PauseMenuLayout.ButtonSize)) return new(MainMenuHitKind.Back);
        return new(MainMenuHitKind.None);
    }

    private static bool InBox(Vector2 point, Vector2 center, Vector2 size) =>
        float.Abs(point.X - center.X) <= size.X * 0.5f
        && float.Abs(point.Y - center.Y) <= size.Y * 0.5f;
}
