using System;
using System.Numerics;

namespace VoxelEngine.UI.SpellBuilder;

/// <summary>
/// GL-free geometry of the spell builder panel in canvas units (see <see cref="UiCanvas"/>): a fixed-size
/// panel centered on the canvas holding, top to bottom, the title, one button per staff slot, the four
/// part columns (<see cref="SpellBuilderCatalog.Columns"/>) under their headers, and the stats beside the
/// CLEAR and BIND buttons. Every rectangle is a (center, size) pair and every method takes the canvas size,
/// so the draw and the click handling read one definition.
/// </summary>
public static class SpellBuilderLayout
{
    public const float PanelWidth = 1040f;
    public const float PanelHeight = 580f;
    public const float Pad = 20f;
    public const float TitleHeight = 48f;
    public const float SlotHeight = 40f;
    public const float SlotWidthMax = 120f;
    public const float SlotGap = 10f;
    public const float SectionGap = 14f;
    public const float HeaderHeight = 32f;
    public const float ColumnGap = 16f;
    public const float RowHeight = 40f;
    /// <summary>Vertical gap between two part rows (taken out of <see cref="RowHeight"/>).</summary>
    public const float RowGap = 4f;
    public const float ButtonWidth = 160f;
    public const float ButtonHeight = 48f;
    public const float ButtonGap = 16f;

    /// <summary>The most part rows a column shows; slice 1 ships few parts, so there is no scrolling.</summary>
    public const int MaxRowsPerColumn = 6;

    private const float InnerWidth = PanelWidth - Pad * 2f;

    public static (Vector2 Center, Vector2 Size) PanelRect(Vector2 canvas) =>
        (canvas * 0.5f, new Vector2(PanelWidth, PanelHeight));

    private static float Left(Vector2 canvas) => canvas.X * 0.5f - PanelWidth * 0.5f;
    private static float Top(Vector2 canvas) => canvas.Y * 0.5f - PanelHeight * 0.5f;
    private static float Right(Vector2 canvas) => Left(canvas) + PanelWidth;
    private static float Bottom(Vector2 canvas) => Top(canvas) + PanelHeight;

    /// <summary>Top-left corner of the title text.</summary>
    public static Vector2 TitleOrigin(Vector2 canvas) => new(Left(canvas) + Pad, Top(canvas) + Pad);

    private static float SlotsTop(Vector2 canvas) => Top(canvas) + Pad + TitleHeight;

    private static float SlotWidth(int slotCount)
    {
        var n = Math.Max(1, slotCount);
        return MathF.Min(SlotWidthMax, (InnerWidth - SlotGap * (n - 1)) / n);
    }

    /// <summary>The button of staff slot <paramref name="slot"/> in a row of <paramref name="slotCount"/>.</summary>
    public static (Vector2 Center, Vector2 Size) SlotRect(int slot, int slotCount, Vector2 canvas)
    {
        var width = SlotWidth(slotCount);
        var x = Left(canvas) + Pad + slot * (width + SlotGap) + width * 0.5f;
        return (new Vector2(x, SlotsTop(canvas) + SlotHeight * 0.5f), new Vector2(width, SlotHeight));
    }

    private static float HeadersTop(Vector2 canvas) => SlotsTop(canvas) + SlotHeight + SectionGap;
    private static float RowsTop(Vector2 canvas) => HeadersTop(canvas) + HeaderHeight;
    private static float ColumnWidth => (InnerWidth - ColumnGap * (SpellBuilderCatalog.Columns.Length - 1)) / SpellBuilderCatalog.Columns.Length;
    private static float ColumnCenterX(int column, Vector2 canvas) =>
        Left(canvas) + Pad + column * (ColumnWidth + ColumnGap) + ColumnWidth * 0.5f;

    /// <summary>Center of the header over column <paramref name="column"/>.</summary>
    public static Vector2 HeaderCenter(int column, Vector2 canvas) =>
        new(ColumnCenterX(column, canvas), HeadersTop(canvas) + HeaderHeight * 0.5f);

    /// <summary>Part row <paramref name="row"/> of column <paramref name="column"/>.</summary>
    public static (Vector2 Center, Vector2 Size) PartRect(int column, int row, Vector2 canvas) =>
        (new Vector2(ColumnCenterX(column, canvas), RowsTop(canvas) + row * RowHeight + RowHeight * 0.5f),
            new Vector2(ColumnWidth, RowHeight - RowGap));

    /// <summary>Top-left corner of the stats text, under the columns.</summary>
    public static Vector2 StatsOrigin(Vector2 canvas) =>
        new(Left(canvas) + Pad, RowsTop(canvas) + MaxRowsPerColumn * RowHeight + SectionGap);

    /// <summary>The BIND button, in the panel's bottom-right corner.</summary>
    public static (Vector2 Center, Vector2 Size) BindRect(Vector2 canvas) =>
        (new Vector2(Right(canvas) - Pad - ButtonWidth * 0.5f, Bottom(canvas) - Pad - ButtonHeight * 0.5f),
            new Vector2(ButtonWidth, ButtonHeight));

    /// <summary>The CLEAR button, left of BIND.</summary>
    public static (Vector2 Center, Vector2 Size) ClearRect(Vector2 canvas)
    {
        var bind = BindRect(canvas);
        return (bind.Center - new Vector2(ButtonWidth + ButtonGap, 0f), bind.Size);
    }

    /// <summary>Whether <paramref name="point"/> lies inside <paramref name="rect"/> (edges included).</summary>
    public static bool Contains((Vector2 Center, Vector2 Size) rect, Vector2 point) =>
        MathF.Abs(point.X - rect.Center.X) <= rect.Size.X * 0.5f
        && MathF.Abs(point.Y - rect.Center.Y) <= rect.Size.Y * 0.5f;

    /// <summary>
    /// What <paramref name="point"/> lands on. A part row counts only below
    /// <c>min(rowsInColumn(column), MaxRowsPerColumn)</c>; a staff with <paramref name="slotCount"/> slots shows
    /// that many slot buttons.
    /// </summary>
    public static SpellBuilderHit HitAt(Vector2 point, Vector2 canvas, Func<int, int> rowsInColumn, int slotCount)
    {
        if (!Contains(PanelRect(canvas), point)) return new SpellBuilderHit(SpellBuilderHitKind.None);

        for (var slot = 0; slot < slotCount; slot++)
        {
            if (Contains(SlotRect(slot, slotCount, canvas), point))
                return new SpellBuilderHit(SpellBuilderHitKind.Slot, Index: slot);
        }

        for (var column = 0; column < SpellBuilderCatalog.Columns.Length; column++)
        {
            var rows = Math.Min(rowsInColumn(column), MaxRowsPerColumn);
            for (var row = 0; row < rows; row++)
            {
                if (Contains(PartRect(column, row, canvas), point))
                    return new SpellBuilderHit(SpellBuilderHitKind.Part, column, row);
            }
        }

        if (Contains(BindRect(canvas), point)) return new SpellBuilderHit(SpellBuilderHitKind.Bind);
        if (Contains(ClearRect(canvas), point)) return new SpellBuilderHit(SpellBuilderHitKind.Clear);
        return new SpellBuilderHit(SpellBuilderHitKind.Panel);
    }
}
