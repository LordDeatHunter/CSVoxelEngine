using System;
using System.Numerics;
using VoxelEngine.UI.Widgets;

namespace VoxelEngine.UI.Forge;

/// <summary>
/// GL-free geometry of the anvil's recipe list in canvas units (see <see cref="UiCanvas"/>), centered on the canvas: a
/// title row, a line naming the metal stack on the anvil, a two-column grid of recipe cards (result icon, name, metal
/// cost) and a CLOSE button at the bottom. The draw (<c>ForgeRenderer</c>) and the clicks (<c>ForgeInput</c>) read this
/// one definition through <see cref="HitAt"/>.
/// </summary>
public static class ForgeLayout
{
    public const float PanelWidth = 720f;
    public const float PanelHeight = 600f;
    public const float TitleHeight = 40f;
    public const float StackLineHeight = 28f;
    public const float CardHeight = 56f;
    public const int Columns = 2;
    public const float Gap = 10f;
    public const float ButtonWidth = 200f;
    public const float ButtonHeight = 44f;

    /// <summary>The panel.</summary>
    public static UiRect Panel(Vector2 canvas) => new(canvas * 0.5f, new Vector2(PanelWidth, PanelHeight));

    private static UiRect Content(Vector2 canvas) => PanelFrameLayout.Standard(Panel(canvas)).Content;

    /// <summary>The title row.</summary>
    public static UiRect Title(Vector2 canvas)
    {
        var content = Content(canvas);
        return UiRect.FromTopLeft(new Vector2(content.Left, content.Top), new Vector2(content.Size.X, TitleHeight));
    }

    /// <summary>The line under the title naming the stack on the anvil.</summary>
    public static UiRect StackLine(Vector2 canvas)
    {
        var content = Content(canvas);
        return UiRect.FromTopLeft(new Vector2(content.Left, content.Top + TitleHeight),
            new Vector2(content.Size.X, StackLineHeight));
    }

    /// <summary>The CLOSE button, centered at the bottom of the panel.</summary>
    public static UiRect CloseButton(Vector2 canvas)
    {
        var content = Content(canvas);
        return new UiRect(new Vector2(content.Center.X, content.Bottom - ButtonHeight * 0.5f),
            new Vector2(ButtonWidth, ButtonHeight));
    }

    /// <summary>The recipe grid, between the stack line and the CLOSE button.</summary>
    public static UiRect Grid(Vector2 canvas)
    {
        var content = Content(canvas);
        var top = content.Top + TitleHeight + StackLineHeight + Gap;
        var bottom = content.Bottom - ButtonHeight - Gap;
        return UiRect.FromTopLeft(new Vector2(content.Left, top), new Vector2(content.Size.X, MathF.Max(0f, bottom - top)));
    }

    /// <summary>How many cards the grid shows: whole rows of <see cref="Columns"/> that fit its height.</summary>
    public static int Capacity(Vector2 canvas) => Columns * (int)((Grid(canvas).Size.Y + Gap) / (CardHeight + Gap));

    /// <summary>Card <paramref name="index"/>, filling the grid row by row.</summary>
    public static UiRect Card(int index, Vector2 canvas)
    {
        var grid = Grid(canvas);
        var width = (grid.Size.X - Gap * (Columns - 1)) / Columns;
        var column = index % Columns;
        var row = index / Columns;
        return UiRect.FromTopLeft(new Vector2(grid.Left + column * (width + Gap), grid.Top + row * (CardHeight + Gap)),
            new Vector2(width, CardHeight));
    }

    /// <summary>What <paramref name="point"/> lands on with <paramref name="cardCount"/> recipes listed (only the
    /// first <see cref="Capacity"/> are shown); <paramref name="card"/> is the card's index for
    /// <see cref="ForgeHitKind.Recipe"/>, -1 otherwise.</summary>
    public static ForgeHitKind HitAt(Vector2 point, Vector2 canvas, int cardCount, out int card)
    {
        card = -1;
        if (!Panel(canvas).Contains(point)) return ForgeHitKind.None;
        if (CloseButton(canvas).Contains(point)) return ForgeHitKind.Close;
        var shown = Math.Min(cardCount, Capacity(canvas));
        for (var i = 0; i < shown; i++)
        {
            if (!Card(i, canvas).Contains(point)) continue;
            card = i;
            return ForgeHitKind.Recipe;
        }
        return ForgeHitKind.Panel;
    }
}
