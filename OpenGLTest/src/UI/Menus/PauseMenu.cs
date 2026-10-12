using System.Numerics;
using VoxelEngine.Rendering;

namespace VoxelEngine.UI.Menus;

/// <summary>Which pause-menu page is showing. Closed means the game runs normally.</summary>
public enum PauseMenuPage
{
    Closed,
    Main,
    Settings,
}

/// <summary>What a click on the pause menu landed on.</summary>
public enum PauseMenuHit
{
    None,
    Resume,
    Settings,
    Exit,
    Back,
}

/// <summary>
/// Selection state of the ESC pause menu. Owned by <see cref="Program"/>;
/// <see cref="UIRenderer"/> only reads it. While open the game keeps rendering
/// behind a dim overlay but stops simulating (see Program.OnUpdate).
/// </summary>
public sealed class PauseMenuView
{
    public PauseMenuPage Page { get; set; } = PauseMenuPage.Closed;

    public bool IsOpen => Page != PauseMenuPage.Closed;
}

/// <summary>
/// Pure screen layout of the pause menu: button rectangles and click hit-testing
/// with no GL dependency, so headless tests can cover the click targets the
/// renderer draws. Origin is top-left, Y down, in pixels.
/// </summary>
public static class PauseMenuLayout
{
    public const float ButtonWidth = 320f;
    public const float ButtonHeight = 56f;
    public const float ButtonGap = 16f;

    public static Vector2 ButtonSize => new(ButtonWidth, ButtonHeight);

    /// <summary>Centre of the Resume/Settings/Exit button on the main page.</summary>
    public static Vector2 MainButtonCentre(Vector2 screenSize, int index)
    {
        var totalHeight = 3 * ButtonHeight + 2 * ButtonGap;
        var top = screenSize.Y * 0.5f - totalHeight * 0.5f + ButtonHeight * 0.5f;
        return new Vector2(screenSize.X * 0.5f, top + index * (ButtonHeight + ButtonGap));
    }

    /// <summary>Centre of the Back button on the settings page.</summary>
    public static Vector2 BackButtonCentre(Vector2 screenSize) =>
        new(screenSize.X * 0.5f, screenSize.Y * 0.5f + 80f);

    /// <summary>Top edge (Y) of the page title text.</summary>
    public static float TitleTop(Vector2 screenSize, PauseMenuPage page) =>
        page == PauseMenuPage.Settings
            ? screenSize.Y * 0.5f - 120f
            : screenSize.Y * 0.5f - (3 * ButtonHeight + 2 * ButtonGap) * 0.5f - 80f;

    public static PauseMenuHit HitAt(Vector2 point, Vector2 screenSize, PauseMenuPage page)
    {
        if (page == PauseMenuPage.Main)
        {
            for (var i = 0; i < 3; i++)
            {
                if (InBox(point, MainButtonCentre(screenSize, i), ButtonSize))
                    return (PauseMenuHit)((int)PauseMenuHit.Resume + i);
            }
            return PauseMenuHit.None;
        }

        if (page == PauseMenuPage.Settings && InBox(point, BackButtonCentre(screenSize), ButtonSize))
            return PauseMenuHit.Back;

        return PauseMenuHit.None;
    }

    private static bool InBox(Vector2 point, Vector2 centre, Vector2 size) =>
        float.Abs(point.X - centre.X) <= size.X * 0.5f
        && float.Abs(point.Y - centre.Y) <= size.Y * 0.5f;
}
