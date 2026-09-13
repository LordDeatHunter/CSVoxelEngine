using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace silkgltriangle;

public enum PreviewMode
{
    Idle,
    Walk,
    Run,
    Jump,
}

public enum DebugButton
{
    Save,
    ReloadDisplay,
    ResetRenderer,
    Idle,
    Walk,
    Run,
    Jump,
    Mine,
}

/// <summary>One editable number: a labelled track the debugger draws and hit-tests.</summary>
public sealed class DebugSlider
{
    public DebugSlider(string label, float min, float max, int decimals, Func<float> get, Action<float> set)
    {
        Label = label;
        Min = min;
        Max = max;
        Decimals = decimals;
        Get = get;
        Set = set;
    }

    public string Label { get; }
    public float Min { get; }
    public float Max { get; }
    public int Decimals { get; }
    public Func<float> Get { get; }
    public Action<float> Set { get; }

    public Vector2 TrackCentre { get; set; }
    public Vector2 TrackSize { get; set; }
    public float Value => Get();
    public string Formatted => Value.ToString("F" + Decimals, CultureInfo.InvariantCulture);
}

/// <summary>
/// Immediate-mode UI for the item debugger: an item grid on the left, transform sliders and
/// animation-preview buttons on the right. Geometry is rebuilt each <see cref="Draw"/> and kept
/// so the next frame's mouse-down can hit-test it, mirroring how the game's panels work.
/// </summary>
public sealed class ItemDebuggerUi
{
    public readonly record struct GridEntry(byte Id, string Name, bool IsBlock);

    private const float Pad = 12f;
    private const float PanelWidth = 330f;
    private const float SlotSize = 46f;
    private const float SlotGap = 8f;
    private const float HeaderH = 52f;
    private const float SliderRowH = 42f;
    private const float TrackH = 10f;
    private const float ButtonH = 34f;
    private const float ButtonGap = 8f;
    private const float TextCap = 12f;

    private static readonly Rgba Panel = Rgba.Grey(0.03f, 0.74f);
    private static readonly Rgba PanelLine = Rgba.Grey(0.45f, 0.6f);
    private static readonly Rgba TextDim = Rgba.Grey(0.62f);
    private static readonly Rgba SlotBg = Rgba.Grey(0.09f, 0.72f);
    private static readonly Rgba Accent = new(232, 184, 72);
    private static readonly Rgba Track = Rgba.Grey(0.2f, 0.9f);
    private static readonly Rgba Knob = Rgba.White;
    private static readonly Rgba ButtonBg = Rgba.Grey(0.18f, 0.92f);
    private static readonly Rgba ButtonOff = Rgba.Grey(0.1f, 0.92f);

    public IReadOnlyList<GridEntry> Entries { get; }

    private int _columns = 5;
    private int _visibleRows = 1;
    private int _scroll;

    private readonly List<(Vector2 Centre, Vector2 Size, int Entry)> _slots = new();
    private readonly List<DebugSlider> _sliders = new();
    private readonly List<(Vector2 Centre, Vector2 Size, DebugButton Button, bool Enabled, bool Active)> _buttons = new();

    public ItemDebuggerUi()
    {
        var entries = new List<GridEntry>();
        foreach (var (id, item) in ItemRegistry.ById)
            entries.Add(new GridEntry(id, item.Name, !ItemRegistry.IsItem(id)));
        entries.Sort((a, b) =>
        {
            if (a.IsBlock != b.IsBlock) return a.IsBlock ? 1 : -1;
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : a.Id.CompareTo(b.Id);
        });
        Entries = entries;
    }

    public int Scroll => _scroll;

    public void ScrollBy(int rows)
    {
        var max = Math.Max(0, TotalRows() - _visibleRows);
        _scroll = Math.Clamp(_scroll + rows, 0, max);
    }

    public int? SlotAt(Vector2 point)
    {
        foreach (var (centre, size, entry) in _slots)
        {
            if (MathF.Abs(point.X - centre.X) <= size.X * 0.5f
                && MathF.Abs(point.Y - centre.Y) <= size.Y * 0.5f)
                return entry;
        }
        return null;
    }

    public DebugSlider? SliderAt(Vector2 point)
    {
        foreach (var slider in _sliders)
        {
            var halfW = slider.TrackSize.X * 0.5f;
            if (MathF.Abs(point.X - slider.TrackCentre.X) <= halfW
                && MathF.Abs(point.Y - slider.TrackCentre.Y) <= SliderRowH * 0.5f)
                return slider;
        }
        return null;
    }

    public DebugSlider? Slider(string label)
    {
        foreach (var slider in _sliders)
            if (slider.Label == label)
                return slider;
        return null;
    }

    public bool OverGrid(Vector2 point) => point.X <= Pad + PanelWidth;

    public DebugButton? ButtonAt(Vector2 point)
    {
        foreach (var (centre, size, button, enabled, _) in _buttons)
        {
            if (!enabled) continue;
            if (MathF.Abs(point.X - centre.X) <= size.X * 0.5f
                && MathF.Abs(point.Y - centre.Y) <= size.Y * 0.5f)
                return button;
        }
        return null;
    }

    public static float Fraction(DebugSlider slider, float x) => Math.Clamp(
        (x - (slider.TrackCentre.X - slider.TrackSize.X * 0.5f)) / slider.TrackSize.X, 0f, 1f);

    public static float ValueAt(DebugSlider slider, float x) =>
        slider.Min + Fraction(slider, x) * (slider.Max - slider.Min);

    public void Draw(UIRenderer ui, ItemDisplayEditor editor, PreviewMode mode, Vector2 screen, string status)
    {
        BuildGrid(screen);
        BuildControls(editor, mode, screen);

        DrawPanel(ui, new Vector2(Pad + PanelWidth * 0.5f, screen.Y * 0.5f),
            new Vector2(PanelWidth, screen.Y - Pad * 2f));
        DrawPanel(ui, new Vector2(screen.X - Pad - PanelWidth * 0.5f, screen.Y * 0.5f),
            new Vector2(PanelWidth, screen.Y - Pad * 2f));

        DrawGrid(ui, editor);
        DrawControls(ui, editor, mode, screen, status);
    }

    private void BuildGrid(Vector2 screen)
    {
        _slots.Clear();

        var stride = SlotSize + SlotGap;
        _columns = Math.Max(1, (int)((PanelWidth - Pad * 2f + SlotGap) / stride));
        var gridWidth = _columns * SlotSize + (_columns - 1) * SlotGap;
        var gridLeft = Pad + (PanelWidth - gridWidth) * 0.5f;
        var gridTop = Pad + HeaderH;
        _visibleRows = Math.Max(1, (int)((screen.Y - Pad - gridTop + SlotGap) / stride));

        var totalRows = TotalRows();
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, totalRows - _visibleRows));

        for (var row = 0; row < _visibleRows; row++)
        {
            for (var col = 0; col < _columns; col++)
            {
                var index = (_scroll + row) * _columns + col;
                if (index >= Entries.Count) return;

                var centre = new Vector2(
                    gridLeft + SlotSize * 0.5f + col * stride,
                    gridTop + SlotSize * 0.5f + row * stride);
                _slots.Add((centre, new Vector2(SlotSize), index));
            }
        }
    }

    private void BuildControls(ItemDisplayEditor editor, PreviewMode mode, Vector2 screen)
    {
        _sliders.Clear();
        _buttons.Clear();

        var left = screen.X - Pad - PanelWidth + Pad;
        var right = screen.X - Pad - Pad;
        var width = right - left;
        var y = Pad + 56f;

        var buttonWidth = (width - ButtonGap * 3f) / 4f;
        AddButton(new Vector2(left + buttonWidth * 0.5f, y + ButtonH * 0.5f),
            new Vector2(buttonWidth, ButtonH), DebugButton.Idle, "IDLE", true, mode == PreviewMode.Idle);
        AddButton(new Vector2(left + buttonWidth * 1.5f + ButtonGap, y + ButtonH * 0.5f),
            new Vector2(buttonWidth, ButtonH), DebugButton.Walk, "WALK", true, mode == PreviewMode.Walk);
        AddButton(new Vector2(left + buttonWidth * 2.5f + ButtonGap * 2f, y + ButtonH * 0.5f),
            new Vector2(buttonWidth, ButtonH), DebugButton.Run, "RUN", true, mode == PreviewMode.Run);
        AddButton(new Vector2(left + buttonWidth * 3.5f + ButtonGap * 3f, y + ButtonH * 0.5f),
            new Vector2(buttonWidth, ButtonH), DebugButton.Jump, "JUMP", true, mode == PreviewMode.Jump);
        y += ButtonH + ButtonGap;

        AddButton(new Vector2((left + right) * 0.5f, y + ButtonH * 0.5f),
            new Vector2(width, ButtonH), DebugButton.Mine, "MINE SWING", true, false);
        y += ButtonH + ButtonGap + 10f;

        if (editor.IsPureItem) AddDisplaySliders(editor);
        AddRendererSliders(editor);

        for (var i = 0; i < _sliders.Count; i++)
        {
            var slider = _sliders[i];
            var rowTop = y + i * SliderRowH;
            slider.TrackCentre = new Vector2((left + right) * 0.5f, rowTop + 22f);
            slider.TrackSize = new Vector2(right - left, TrackH);
        }

        var buttonY = screen.Y - Pad - ButtonH * 0.5f;
        AddButton(new Vector2((left + right) * 0.5f, buttonY), new Vector2(width, ButtonH),
            DebugButton.Save, "SAVE TO JSON", editor.IsPureItem, false);
        buttonY -= ButtonH + ButtonGap;
        AddButton(new Vector2((left + right) * 0.5f, buttonY), new Vector2(width, ButtonH),
            DebugButton.ReloadDisplay, "RELOAD FROM JSON", true, false);
        buttonY -= ButtonH + ButtonGap;
        AddButton(new Vector2((left + right) * 0.5f, buttonY), new Vector2(width, ButtonH),
            DebugButton.ResetRenderer, "RESET PREVIEW", true, false);
    }

    private void AddDisplaySliders(ItemDisplayEditor editor)
    {
        AddSlider("Rot pitch", -180f, 180f, 0, () => editor.Rotation.X,
            v => editor.Rotation = editor.Rotation with { X = v });
        AddSlider("Rot yaw", -180f, 180f, 0, () => editor.Rotation.Y,
            v => editor.Rotation = editor.Rotation with { Y = v });
        AddSlider("Rot roll", -180f, 180f, 0, () => editor.Rotation.Z,
            v => editor.Rotation = editor.Rotation with { Z = v });
        AddSlider("Trans X", -0.5f, 0.5f, 3, () => editor.Translation.X,
            v => editor.Translation = editor.Translation with { X = v });
        AddSlider("Trans Y", -0.5f, 0.5f, 3, () => editor.Translation.Y,
            v => editor.Translation = editor.Translation with { Y = v });
        AddSlider("Trans Z", -0.5f, 0.5f, 3, () => editor.Translation.Z,
            v => editor.Translation = editor.Translation with { Z = v });
        AddSlider("Display scale", 0.05f, 2f, 2, () => editor.Scale, v => editor.Scale = v);
    }

    private void AddRendererSliders(ItemDisplayEditor editor)
    {
        if (editor.IsPureItem)
        {
            AddSlider("Held scale", 0.05f, 2f, 2, () => editor.ItemHeldScale, v => editor.ItemHeldScale = v);
            AddSlider("Grip X", -0.3f, 0.3f, 3, () => editor.ItemGrip.X,
                v => editor.ItemGrip = editor.ItemGrip with { X = v });
            AddSlider("Grip Y", -0.3f, 0.3f, 3, () => editor.ItemGrip.Y,
                v => editor.ItemGrip = editor.ItemGrip with { Y = v });
            AddSlider("Grip Z", -0.3f, 0.3f, 3, () => editor.ItemGrip.Z,
                v => editor.ItemGrip = editor.ItemGrip with { Z = v });
            return;
        }

        AddSlider("Held scale", 0.05f, 2f, 2, () => editor.BlockHeldScale, v => editor.BlockHeldScale = v);
        AddSlider("Grip X", -0.3f, 0.3f, 3, () => editor.BlockGrip.X,
            v => editor.BlockGrip = editor.BlockGrip with { X = v });
        AddSlider("Grip Y", -0.3f, 0.3f, 3, () => editor.BlockGrip.Y,
            v => editor.BlockGrip = editor.BlockGrip with { Y = v });
        AddSlider("Grip Z", -0.3f, 0.3f, 3, () => editor.BlockGrip.Z,
            v => editor.BlockGrip = editor.BlockGrip with { Z = v });
    }

    private void AddSlider(string label, float min, float max, int decimals, Func<float> get, Action<float> set) =>
        _sliders.Add(new DebugSlider(label, min, max, decimals, get, set));

    private void AddButton(Vector2 centre, Vector2 size, DebugButton button, string label, bool enabled, bool active) =>
        _buttons.Add((centre, size, button, enabled, active));

    private int TotalRows() => (Entries.Count + _columns - 1) / _columns;

    private static void DrawPanel(UIRenderer ui, Vector2 centre, Vector2 size) =>
        ui.DrawRect(centre, size, Panel);

    private void DrawGrid(UIRenderer ui, ItemDisplayEditor editor)
    {
        var scale = ui.CapHeightScale(TextCap);
        ui.DrawText($"ITEMS  ({Entries.Count})", new Vector2(Pad + 8f, Pad + 8f), ui.CapHeightScale(18f), Rgba.White);

        // Flat sprites for items, cubes for blocks: the same mix the inventory rows show.
        foreach (var (centre, size, index) in _slots)
        {
            var entry = Entries[index];
            ui.DrawRect(centre, size, SlotBg);
            if (entry.Id == editor.SelectedId)
                ui.DrawBorder(centre, size, 2f, Accent);
            else
                ui.DrawBorder(centre, size, 1f, PanelLine);

            if (entry.IsBlock)
                ui.DrawBlockIcon(centre, SlotSize * 0.74f, entry.Id);
            else
                ui.DrawItemSprite(centre, SlotSize * 0.72f, entry.Id, 0f, 0f);
        }

        if (TotalRows() > _visibleRows)
            ui.DrawText($"{_scroll + 1}-{Math.Min(_scroll + _visibleRows, TotalRows())}/{TotalRows()}",
                new Vector2(Pad + 8f, Pad + HeaderH * 0.5f), scale, TextDim);
    }

    private void DrawControls(UIRenderer ui, ItemDisplayEditor editor, PreviewMode mode, Vector2 screen,
        string status)
    {
        var scale = ui.CapHeightScale(TextCap);
        var titleScale = ui.CapHeightScale(18f);
        var left = screen.X - Pad - PanelWidth + Pad;
        var right = screen.X - Pad - Pad;

        ui.DrawText(editor.DisplayName, new Vector2(left, Pad + 8f), titleScale, Rgba.White);
        if (!editor.IsPureItem && editor.HasSelection)
            ui.DrawText("block: not saved", new Vector2(left, Pad + 30f), scale, TextDim);
        else if (!string.IsNullOrEmpty(status))
            ui.DrawText(status, new Vector2(left, Pad + 30f), scale, Accent);

        foreach (var (centre, size, button, enabled, active) in _buttons)
        {
            var background = !enabled ? ButtonOff : active ? Accent : ButtonBg;
            ui.DrawRect(centre, size, background);
            ui.DrawBorder(centre, size, 1f, PanelLine);

            var label = Label(button);
            var width = ui.MeasureTextWidth(label, scale);
            ui.DrawText(label, new Vector2(centre.X - width * 0.5f, centre.Y - TextCap * 0.5f), scale,
                enabled ? Rgba.White : TextDim);
        }

        foreach (var slider in _sliders)
        {
            var labelY = slider.TrackCentre.Y - 22f;
            ui.DrawText(slider.Label, new Vector2(left, labelY), scale, Rgba.White);
            var valueWidth = ui.MeasureTextWidth(slider.Formatted, scale);
            ui.DrawText(slider.Formatted, new Vector2(right - valueWidth, labelY), scale, TextDim);

            ui.DrawRect(slider.TrackCentre, slider.TrackSize, Track);
            var fraction = Math.Clamp((slider.Value - slider.Min) / (slider.Max - slider.Min), 0f, 1f);
            var trackLeft = slider.TrackCentre.X - slider.TrackSize.X * 0.5f;
            if (fraction > 0f)
                ui.DrawRect(new Vector2(trackLeft + slider.TrackSize.X * fraction * 0.5f, slider.TrackCentre.Y),
                    new Vector2(slider.TrackSize.X * fraction, TrackH), Accent);
            ui.DrawRect(new Vector2(trackLeft + slider.TrackSize.X * fraction, slider.TrackCentre.Y),
                new Vector2(6f, 20f), Knob);
        }
    }

    private static string Label(DebugButton button) => button switch
    {
        DebugButton.Save => "SAVE TO JSON",
        DebugButton.ReloadDisplay => "RELOAD FROM JSON",
        DebugButton.ResetRenderer => "RESET PREVIEW",
        DebugButton.Idle => "IDLE",
        DebugButton.Walk => "WALK",
        DebugButton.Run => "RUN",
        DebugButton.Jump => "JUMP",
        DebugButton.Mine => "MINE SWING",
        _ => "",
    };
}
