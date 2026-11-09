using VoxelEngine.Data;

namespace VoxelEngine.UI;

/// <summary>
/// The one place the list-based UI takes its spacing, border widths, text sizes and palette from, so a
/// panel, a card and a button agree without each renderer holding its own numbers. Canvas units (see
/// <see cref="UiCanvas"/>); GL-free. The look is the established one (warm gold accents on translucent
/// near-black panels, kongtext), only named.
/// </summary>
public static class UiTheme
{
    /// <summary>The spacing scale: gaps and paddings are one of these.</summary>
    public static class Space
    {
        public const float Xs = 4f;
        public const float Sm = 8f;
        public const float Md = 12f;
        public const float Lg = 16f;
    }

    /// <summary>Outline widths: a hairline, the standard frame, and a selected or picked frame.</summary>
    public static class Border
    {
        public const float Thin = 1.5f;
        public const float Standard = 2f;
        public const float Selected = 3f;
    }

    /// <summary>
    /// Text sizes as the pixel height of a digit (the font is sized by cap height so the size survives
    /// swapping the font file); <see cref="UIRenderer"/> turns them into draw scales.
    /// </summary>
    public static class TextSize
    {
        public const float Body = 14f;
        public const float Head = 22f;
    }

    // Translucent fills
    public static readonly Rgba PanelBackground = Rgba.Gray(0.03f, 0.72f);
    public static readonly Rgba CardBackground = Rgba.Gray(0.07f, 0.85f);
    public static readonly Rgba ChipBackground = Rgba.Gray(0.12f, 0.9f);
    public static readonly Rgba SlotBackground = Rgba.Gray(0.08f, 0.55f);
    public static readonly Rgba SelectedBackground = Rgba.Gray(0.18f, 0.65f);
    public static readonly Rgba HoverBackground = Rgba.Gray(0.14f, 0.6f);

    // Frames
    public static readonly Rgba SlotBorderColor = Rgba.Gray(0.65f, 0.45f);
    public static readonly Rgba SelectedBorderColor = Rgba.White;

    // Accents and text: warm gold, green ready states, red danger, muted dims
    public static readonly Rgba AccentGold = new(232, 184, 72);
    public static readonly Rgba ReadyGreen = new(126, 217, 87);
    public static readonly Rgba DangerRed = new(230, 110, 110);
    /// <summary>A clickable chat link (the <c>/locate</c> coordinates), drawn underlined.</summary>
    public static readonly Rgba LinkBlue = new(110, 180, 255);
    public static readonly Rgba TextDim = Rgba.Gray(0.55f, 1f);
    public static readonly Rgba TextShadow = new(0, 0, 0, 180);

    /// <summary>The opaque backdrop behind every menu and loading frame: nothing is rendered behind those.</summary>
    public static readonly Rgba MenuBackdrop = Rgba.Gray(0.08f);
}
