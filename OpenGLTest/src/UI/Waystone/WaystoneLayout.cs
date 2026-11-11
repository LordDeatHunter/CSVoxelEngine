using System;
using System.Numerics;
using VoxelEngine.UI.Widgets;

namespace VoxelEngine.UI.Waystone;

/// <summary>
/// GL-free geometry of the waystone screen in canvas units (see <see cref="UiCanvas"/>), every box centered on the
/// canvas: the discovery popup (the name, a line under it, CONFIRM and, for the first discoverer, RENAME), the rename
/// dialog (a title, the text field, RENAME and CANCEL) and the teleport list panel (a title row, the search field, the
/// scrolled rows and a footer with the mana requirement). The draw (<c>WaystoneRenderer</c>) and the clicks
/// (<c>WaystoneInput</c>) read this one definition through <see cref="HitAt"/>.
/// </summary>
public static class WaystoneLayout
{
    public const float DialogWidth = 680f;
    public const float PopupHeight = 260f;
    public const float RenameHeight = 290f;
    public const float ButtonWidth = 200f;
    public const float ButtonHeight = 48f;
    public const float ButtonGap = 48f;
    public const float FieldWidth = 600f;
    public const float FieldHeight = 48f;

    public const float PanelWidth = 820f;
    public const float PanelHeight = 680f;
    public const float TitleHeight = 40f;
    public const float SearchHeight = 40f;
    public const float FooterHeight = 36f;
    public const float Gap = 10f;
    public const float RowHeight = 44f;

    /// <summary>The wheel scrolls this many rows a notch.</summary>
    public const int WheelStepRows = 3;

    private static float Edge => UiTheme.Space.Lg + 8f;

    // ---- Discovery popup ----

    /// <summary>The discovery popup's box.</summary>
    public static UiRect PopupBox(Vector2 canvas) => new(canvas * 0.5f, new Vector2(DialogWidth, PopupHeight));

    /// <summary>Center of the waystone's name in the popup.</summary>
    public static Vector2 PopupNameCenter(Vector2 canvas) => new(canvas.X * 0.5f, PopupBox(canvas).Top + 58f);

    /// <summary>Center of the "has been discovered" line under it.</summary>
    public static Vector2 PopupLineCenter(Vector2 canvas) => new(canvas.X * 0.5f, PopupBox(canvas).Top + 112f);

    private static float PopupButtonY(Vector2 canvas) => PopupBox(canvas).Bottom - Edge - ButtonHeight * 0.5f;

    /// <summary>CONFIRM: alone in the middle, or on the left beside RENAME when the player may rename.</summary>
    public static UiRect ConfirmButton(Vector2 canvas, bool canRename)
    {
        var x = canRename ? canvas.X * 0.5f - (ButtonGap + ButtonWidth) * 0.5f : canvas.X * 0.5f;
        return new UiRect(new Vector2(x, PopupButtonY(canvas)), new Vector2(ButtonWidth, ButtonHeight));
    }

    /// <summary>RENAME, right of CONFIRM (shown only to the first discoverer).</summary>
    public static UiRect RenameButton(Vector2 canvas) =>
        new(new Vector2(canvas.X * 0.5f + (ButtonGap + ButtonWidth) * 0.5f, PopupButtonY(canvas)),
            new Vector2(ButtonWidth, ButtonHeight));

    // ---- Rename dialog ----

    /// <summary>The rename dialog's box.</summary>
    public static UiRect RenameBox(Vector2 canvas) => new(canvas * 0.5f, new Vector2(DialogWidth, RenameHeight));

    /// <summary>Center of the dialog's title.</summary>
    public static Vector2 RenameTitleCenter(Vector2 canvas) => new(canvas.X * 0.5f, RenameBox(canvas).Top + 44f);

    /// <summary>The name field.</summary>
    public static UiRect RenameField(Vector2 canvas) =>
        new(new Vector2(canvas.X * 0.5f, RenameBox(canvas).Top + 108f), new Vector2(FieldWidth, FieldHeight));

    /// <summary>Center of the dim hint under the field.</summary>
    public static Vector2 RenameHintCenter(Vector2 canvas) => new(canvas.X * 0.5f, RenameBox(canvas).Top + 160f);

    private static float RenameButtonY(Vector2 canvas) => RenameBox(canvas).Bottom - Edge - ButtonHeight * 0.5f;

    /// <summary>RENAME (save), on the left.</summary>
    public static UiRect RenameOk(Vector2 canvas) =>
        new(new Vector2(canvas.X * 0.5f - (ButtonGap + ButtonWidth) * 0.5f, RenameButtonY(canvas)),
            new Vector2(ButtonWidth, ButtonHeight));

    /// <summary>CANCEL, on the right.</summary>
    public static UiRect RenameCancel(Vector2 canvas) =>
        new(new Vector2(canvas.X * 0.5f + (ButtonGap + ButtonWidth) * 0.5f, RenameButtonY(canvas)),
            new Vector2(ButtonWidth, ButtonHeight));

    // ---- Teleport list ----

    /// <summary>The list panel.</summary>
    public static UiRect Panel(Vector2 canvas) => new(canvas * 0.5f, new Vector2(PanelWidth, PanelHeight));

    private static UiRect Content(Vector2 canvas) => PanelFrameLayout.Standard(Panel(canvas)).Content;

    /// <summary>The title row: the current waystone's name.</summary>
    public static UiRect Title(Vector2 canvas)
    {
        var content = Content(canvas);
        return UiRect.FromTopLeft(new Vector2(content.Left, content.Top), new Vector2(content.Size.X, TitleHeight));
    }

    /// <summary>The search field, under the title.</summary>
    public static UiRect SearchBox(Vector2 canvas)
    {
        var content = Content(canvas);
        return UiRect.FromTopLeft(new Vector2(content.Left, content.Top + TitleHeight + Gap),
            new Vector2(content.Size.X, SearchHeight));
    }

    /// <summary>The footer: the mana requirement and where the player stands with it.</summary>
    public static UiRect Footer(Vector2 canvas)
    {
        var content = Content(canvas);
        return UiRect.FromTopLeft(new Vector2(content.Left, content.Bottom - FooterHeight),
            new Vector2(content.Size.X, FooterHeight));
    }

    /// <summary>The list viewport, between the search field and the footer.</summary>
    public static UiRect List(Vector2 canvas)
    {
        var content = Content(canvas);
        var top = content.Top + TitleHeight + Gap + SearchHeight + Gap;
        var bottom = content.Bottom - FooterHeight - Gap;
        return UiRect.FromTopLeft(new Vector2(content.Left, top), new Vector2(content.Size.X, MathF.Max(0f, bottom - top)));
    }

    /// <summary>The scroll offset clamped for this many rows.</summary>
    public static float ClampedOffset(float offset, int rowCount, Vector2 canvas) =>
        ScrollList.Clamp(offset, rowCount * RowHeight, List(canvas).Size.Y);

    /// <summary>Row <paramref name="row"/> of a list scrolled by <paramref name="offset"/>.</summary>
    public static UiRect RowRect(int row, Vector2 canvas, float offset)
    {
        var list = List(canvas);
        return UiRect.FromTopLeft(new Vector2(list.Left, list.Top + ScrollList.RowTop(row, offset, RowHeight)),
            new Vector2(list.Size.X, RowHeight - 4f));
    }

    /// <summary>
    /// What <paramref name="point"/> lands on while <paramref name="page"/> is up: the popup's buttons (RENAME only
    /// when <paramref name="canRename"/>), the rename dialog's field and buttons, or the list's search field and rows
    /// (<paramref name="rowCount"/> rows scrolled by <paramref name="offset"/>, which is clamped here).
    /// </summary>
    public static WaystoneHit HitAt(WaystonePage page, Vector2 point, Vector2 canvas, bool canRename, int rowCount,
        float offset)
    {
        switch (page)
        {
            case WaystonePage.Discovered:
                if (ConfirmButton(canvas, canRename).Contains(point)) return new WaystoneHit(WaystoneHitKind.Confirm);
                if (canRename && RenameButton(canvas).Contains(point)) return new WaystoneHit(WaystoneHitKind.Rename);
                return new WaystoneHit(PopupBox(canvas).Contains(point) ? WaystoneHitKind.Panel : WaystoneHitKind.None);

            case WaystonePage.Rename:
                if (RenameField(canvas).Contains(point)) return new WaystoneHit(WaystoneHitKind.RenameField);
                if (RenameOk(canvas).Contains(point)) return new WaystoneHit(WaystoneHitKind.RenameOk);
                if (RenameCancel(canvas).Contains(point)) return new WaystoneHit(WaystoneHitKind.RenameCancel);
                return new WaystoneHit(RenameBox(canvas).Contains(point) ? WaystoneHitKind.Panel : WaystoneHitKind.None);

            case WaystonePage.List:
            {
                if (!Panel(canvas).Contains(point)) return new WaystoneHit(WaystoneHitKind.None);
                if (SearchBox(canvas).Contains(point)) return new WaystoneHit(WaystoneHitKind.Search);
                var list = List(canvas);
                if (point.X < list.Left || point.X >= list.Right) return new WaystoneHit(WaystoneHitKind.Panel);
                var row = ScrollList.RowAt(point.Y - list.Top, ClampedOffset(offset, rowCount, canvas), RowHeight,
                    rowCount, list.Size.Y);
                return row is { } r ? new WaystoneHit(WaystoneHitKind.Row, r) : new WaystoneHit(WaystoneHitKind.Panel);
            }

            default:
                return new WaystoneHit(WaystoneHitKind.None);
        }
    }
}
