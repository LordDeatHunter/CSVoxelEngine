using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace silkgltriangle;

public enum DebugButton
{
    Save,
    ReloadDisplay,
    ResetRenderer,
    FirstPerson,
    TabItems,
    TabEntities,
    TabTrees,
    TreeStepDown,
    TreeStepUp,
    TreeMature,
    TreeRegrow,
    // The button's Arg is the index into EntityPreviewState.Modes.
    EntityMode,
    // The button's Arg is the index into PlayerPreviewState.AllModes.
    PlayerMode,
    // Toggles AnimationClock.Loop of the timeline.
    Loop,
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
/// animation-preview buttons on the right. Three tab buttons at the top of the left panel switch
/// the grid between items, entities and tree species. On the entities tab the right panel holds
/// one button per mode of the selected entity (its clips, then HURT) and nothing else; on the
/// trees tab it holds STEP -/STEP +/MATURE, REGROW and the growth step slider
/// (<see cref="TreeStepSlider"/>) over <see cref="TreePreviewState"/>. Geometry is rebuilt each
/// <see cref="Draw"/> and kept so the next frame's mouse-down can hit-test it, mirroring how the
/// game's panels work.
/// </summary>
public sealed class ItemDebuggerUi
{
    public readonly record struct GridEntry(byte Id, string Name, bool IsBlock);

    /// <summary>The trees tab's growth step slider, which the program drags by label.</summary>
    public const string TreeStepSlider = "Step";

    /// <summary>The timeline's sliders, which the program drags by label: how far through the animation it is
    /// (scrubbing it stops the clock) and the playback speed.</summary>
    public const string ProgressSlider = "Progress (s)";
    public const string SpeedSlider = "Speed (x)";

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
    // The tab buttons' row, which pushes the grid header and the grid down.
    private const float TabRowH = ButtonH + ButtonGap;
    // Entities and tree species have no icon, so their slots are wide enough to hold the name.
    private const int EntityColumns = 2;
    private const int ModesPerRow = 3;
    private const int PlayerModesPerRow = 4;

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
    // One scroll position per tab, so switching back to ITEMS finds the grid where it was left.
    private readonly int[] _scrollByTab = new int[Enum.GetValues<DebuggerTab>().Length];
    private DebuggerTab _tab;
    private int _gridCount;

    private readonly List<(Vector2 Centre, Vector2 Size, int Entry)> _slots = new();
    private readonly List<DebugSlider> _sliders = new();
    private readonly List<(Vector2 Centre, Vector2 Size, DebugButton Button, int Arg, string Label, bool Enabled, bool Active)> _buttons = new();

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

    public int Scroll => _scrollByTab[(int)_tab];

    public void ScrollBy(int rows)
    {
        var max = Math.Max(0, TotalRows() - _visibleRows);
        _scrollByTab[(int)_tab] = Math.Clamp(Scroll + rows, 0, max);
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

    /// <summary>The enabled button under <paramref name="point"/> and its argument (the mode
    /// index for <see cref="DebugButton.EntityMode"/>, 0 otherwise); only buttons the last
    /// <see cref="Draw"/> built can be hit.</summary>
    public (DebugButton Button, int Arg)? ButtonAt(Vector2 point)
    {
        foreach (var (centre, size, button, arg, _, enabled, _) in _buttons)
        {
            if (!enabled) continue;
            if (MathF.Abs(point.X - centre.X) <= size.X * 0.5f
                && MathF.Abs(point.Y - centre.Y) <= size.Y * 0.5f)
                return (button, arg);
        }
        return null;
    }

    public static float Fraction(DebugSlider slider, float x) => Math.Clamp(
        (x - (slider.TrackCentre.X - slider.TrackSize.X * 0.5f)) / slider.TrackSize.X, 0f, 1f);

    public static float ValueAt(DebugSlider slider, float x) =>
        slider.Min + Fraction(slider, x) * (slider.Max - slider.Min);

    public void Draw(UIRenderer ui, ItemDisplayEditor editor, EntityPreviewState entities, TreePreviewState trees,
        DebuggerTab tab, PlayerPreviewState player, Vector2 screen, string status, bool firstPerson,
        AnimationClock? timeline = null)
    {
        _tab = tab;
        _sliders.Clear();
        _buttons.Clear();
        _timelineBar = null;
        BuildTabs(tab);
        var count = tab switch
        {
            DebuggerTab.Entities => entities.Entries.Count,
            DebuggerTab.Trees => trees.Entries.Count,
            _ => Entries.Count,
        };
        BuildGrid(screen, count, tab);
        switch (tab)
        {
            case DebuggerTab.Entities:
                BuildEntityControls(entities, screen);
                break;
            case DebuggerTab.Trees:
                BuildTreeControls(trees, screen);
                break;
            default:
                BuildControls(editor, player, screen, firstPerson);
                break;
        }

        // The timeline runs along the bottom of the viewport between the two panels (ITEMS and ENTITIES only).
        if (timeline != null && tab != DebuggerTab.Trees)
            BuildTimeline(timeline, screen);

        DrawPanel(ui, new Vector2(Pad + PanelWidth * 0.5f, screen.Y * 0.5f),
            new Vector2(PanelWidth, screen.Y - Pad * 2f));
        DrawPanel(ui, new Vector2(screen.X - Pad - PanelWidth * 0.5f, screen.Y * 0.5f),
            new Vector2(PanelWidth, screen.Y - Pad * 2f));
        if (_timelineBar is { } bar)
            DrawPanel(ui, bar.Centre, bar.Size);

        switch (tab)
        {
            case DebuggerTab.Entities:
                DrawEntityGrid(ui, entities);
                DrawEntityTitle(ui, entities, screen);
                break;
            case DebuggerTab.Trees:
                DrawTreeGrid(ui, trees);
                DrawTreeTitle(ui, trees, screen);
                break;
            default:
                DrawGrid(ui, editor);
                DrawTitle(ui, editor, screen, status);
                break;
        }
        DrawControls(ui, screen);
    }

    private void BuildTabs(DebuggerTab tab)
    {
        var width = (PanelWidth - Pad * 2f - ButtonGap * 2f) / 3f;
        var y = Pad + 6f + ButtonH * 0.5f;
        var left = Pad + Pad;
        AddButton(new Vector2(left + width * 0.5f, y), new Vector2(width, ButtonH), DebugButton.TabItems,
            "ITEMS", true, tab == DebuggerTab.Items);
        AddButton(new Vector2(left + width * 1.5f + ButtonGap, y), new Vector2(width, ButtonH),
            DebugButton.TabEntities, "ENTITIES", true, tab == DebuggerTab.Entities);
        AddButton(new Vector2(left + width * 2.5f + ButtonGap * 2f, y), new Vector2(width, ButtonH),
            DebugButton.TabTrees, "TREES", true, tab == DebuggerTab.Trees);
    }

    private void BuildGrid(Vector2 screen, int count, DebuggerTab tab)
    {
        _slots.Clear();
        _gridCount = count;

        var available = PanelWidth - Pad * 2f;
        var slotSize = tab != DebuggerTab.Items
            ? new Vector2((available - (EntityColumns - 1) * SlotGap) / EntityColumns, SlotSize)
            : new Vector2(SlotSize);
        _columns = tab != DebuggerTab.Items
            ? EntityColumns
            : Math.Max(1, (int)((available + SlotGap) / (SlotSize + SlotGap)));
        var strideX = slotSize.X + SlotGap;
        var strideY = slotSize.Y + SlotGap;
        var gridWidth = _columns * slotSize.X + (_columns - 1) * SlotGap;
        var gridLeft = Pad + (PanelWidth - gridWidth) * 0.5f;
        var gridTop = Pad + TabRowH + HeaderH;
        _visibleRows = Math.Max(1, (int)((screen.Y - Pad - gridTop + SlotGap) / strideY));

        var totalRows = TotalRows();
        var scroll = _scrollByTab[(int)tab] = Math.Clamp(_scrollByTab[(int)tab], 0,
            Math.Max(0, totalRows - _visibleRows));

        for (var row = 0; row < _visibleRows; row++)
        {
            for (var col = 0; col < _columns; col++)
            {
                var index = (scroll + row) * _columns + col;
                if (index >= count) return;

                var centre = new Vector2(
                    gridLeft + slotSize.X * 0.5f + col * strideX,
                    gridTop + slotSize.Y * 0.5f + row * strideY);
                _slots.Add((centre, slotSize, index));
            }
        }
    }

    private void BuildEntityControls(EntityPreviewState entities, Vector2 screen)
    {
        var left = screen.X - Pad - PanelWidth + Pad;
        var right = screen.X - Pad - Pad;
        var width = right - left;
        var y = Pad + 56f;

        // One button per mode, in rows of up to three: the model's clips in file order, then HURT.
        var modes = entities.Modes;
        for (var rowStart = 0; rowStart < modes.Count; rowStart += ModesPerRow)
        {
            var inRow = Math.Min(ModesPerRow, modes.Count - rowStart);
            var buttonWidth = (width - ButtonGap * (inRow - 1)) / inRow;
            for (var col = 0; col < inRow; col++)
            {
                var index = rowStart + col;
                var label = modes[index].ToUpperInvariant();
                AddButton(new Vector2(left + buttonWidth * (col + 0.5f) + ButtonGap * col, y + ButtonH * 0.5f),
                    new Vector2(buttonWidth, ButtonH), DebugButton.EntityMode, label, true,
                    string.Equals(label, entities.Mode, StringComparison.OrdinalIgnoreCase), index);
            }
            y += ButtonH + ButtonGap;
        }
    }

    private void BuildTreeControls(TreePreviewState trees, Vector2 screen)
    {
        var left = screen.X - Pad - PanelWidth + Pad;
        var right = screen.X - Pad - Pad;
        var width = right - left;
        var y = Pad + 56f;
        var enabled = trees.Selected != null;

        var buttonWidth = (width - ButtonGap * 2f) / 3f;
        AddButton(new Vector2(left + buttonWidth * 0.5f, y + ButtonH * 0.5f),
            new Vector2(buttonWidth, ButtonH), DebugButton.TreeStepDown, "STEP -", enabled && trees.Step > 0, false);
        AddButton(new Vector2(left + buttonWidth * 1.5f + ButtonGap, y + ButtonH * 0.5f),
            new Vector2(buttonWidth, ButtonH), DebugButton.TreeStepUp, "STEP +",
            enabled && trees.Step < trees.MaxStep, false);
        AddButton(new Vector2(left + buttonWidth * 2.5f + ButtonGap * 2f, y + ButtonH * 0.5f),
            new Vector2(buttonWidth, ButtonH), DebugButton.TreeMature, "MATURE", enabled,
            enabled && trees.Step == trees.MaturitySteps);
        y += ButtonH + ButtonGap;

        AddButton(new Vector2((left + right) * 0.5f, y + ButtonH * 0.5f),
            new Vector2(width, ButtonH), DebugButton.TreeRegrow, "REGROW (NEW SEED)", enabled, false);
        y += ButtonH + ButtonGap + 10f;

        if (!enabled) return;
        // At least 1 wide, so a species that could never grow still draws a sane track.
        AddSlider(TreeStepSlider, 0f, Math.Max(1, trees.MaxStep), 0, () => trees.Step,
            v => trees.SetStep((int)MathF.Round(v)));
        var slider = _sliders[^1];
        slider.TrackCentre = new Vector2((left + right) * 0.5f, y + 22f);
        slider.TrackSize = new Vector2(width, TrackH);
    }

    private void BuildControls(ItemDisplayEditor editor, PlayerPreviewState player, Vector2 screen, bool firstPerson)
    {
        var left = screen.X - Pad - PanelWidth + Pad;
        var right = screen.X - Pad - Pad;
        var width = right - left;
        var y = Pad + 56f;

        // One button per player animation (PlayerPreviewState.AllModes), in rows: poses first, then the
        // swings and reactions, which replay on their own.
        var modes = PlayerPreviewState.AllModes;
        for (var rowStart = 0; rowStart < modes.Count; rowStart += PlayerModesPerRow)
        {
            var inRow = Math.Min(PlayerModesPerRow, modes.Count - rowStart);
            var buttonWidth = (width - ButtonGap * (inRow - 1)) / inRow;
            for (var col = 0; col < inRow; col++)
            {
                var index = rowStart + col;
                AddButton(new Vector2(left + buttonWidth * (col + 0.5f) + ButtonGap * col, y + ButtonH * 0.5f),
                    new Vector2(buttonWidth, ButtonH), DebugButton.PlayerMode, PlayerPreviewState.LabelOf(modes[index]), true,
                    string.Equals(modes[index], player.Mode, StringComparison.OrdinalIgnoreCase), index);
            }
            y += ButtonH + ButtonGap;
        }

        AddButton(new Vector2((left + right) * 0.5f, y + ButtonH * 0.5f),
            new Vector2(width, ButtonH), DebugButton.FirstPerson,
            firstPerson ? "VIEW: FIRST PERSON" : "VIEW: THIRD PERSON", true, firstPerson);
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

    // Where the timeline strip sits this frame (null when the tab has none).
    private (Vector2 Centre, Vector2 Size)? _timelineBar;

    // The animation's progress and speed sliders and the loop toggle, in one row along the bottom of the viewport.
    // The sliders are positioned here rather than in the right panel's column, and their labels follow their own
    // tracks (DrawControls).
    private void BuildTimeline(AnimationClock clock, Vector2 screen)
    {
        const float gap = 22f;
        const float loopWidth = 130f;
        const float speedWidth = 220f;
        var left = Pad + PanelWidth + Pad * 2f;
        var right = screen.X - Pad - PanelWidth - Pad * 2f;
        var width = right - left;
        if (width < 360f) return;

        var trackY = screen.Y - Pad - 20f;
        var progressWidth = Math.Max(120f, width - loopWidth - speedWidth - gap * 2f);
        _timelineBar = (new Vector2((left + right) * 0.5f, screen.Y - Pad - 34f),
            new Vector2(width + 16f, 68f));

        AddSlider(ProgressSlider, 0f, clock.Duration, 2, () => clock.Time, v => clock.Seek(v));
        _sliders[^1].TrackCentre = new Vector2(left + progressWidth * 0.5f, trackY);
        _sliders[^1].TrackSize = new Vector2(progressWidth, TrackH);

        AddSlider(SpeedSlider, 0f, AnimationClock.MaxSpeed, 1, () => clock.Speed, v => clock.Speed = v);
        _sliders[^1].TrackCentre = new Vector2(left + progressWidth + gap + speedWidth * 0.5f, trackY);
        _sliders[^1].TrackSize = new Vector2(speedWidth, TrackH);

        AddButton(new Vector2(right - loopWidth * 0.5f, trackY), new Vector2(loopWidth, ButtonH),
            DebugButton.Loop, clock.Loop ? "LOOP: ON" : "LOOP: OFF", true, clock.Loop);
    }

    private void AddButton(Vector2 centre, Vector2 size, DebugButton button, string label, bool enabled, bool active,
        int arg = 0) =>
        _buttons.Add((centre, size, button, arg, label, enabled, active));

    private int TotalRows() => (_gridCount + _columns - 1) / _columns;

    private static void DrawPanel(UIRenderer ui, Vector2 centre, Vector2 size) =>
        ui.DrawRect(centre, size, Panel);

    private void DrawGrid(UIRenderer ui, ItemDisplayEditor editor)
    {
        DrawGridHeader(ui, $"ITEMS  ({Entries.Count})");

        // Flat sprites for items, cubes for blocks: the same mix the inventory rows show.
        foreach (var (centre, size, index) in _slots)
        {
            var entry = Entries[index];
            DrawSlot(ui, centre, size, entry.Id == editor.SelectedId);

            if (entry.IsBlock)
                ui.DrawBlockIcon(centre, SlotSize * 0.74f, entry.Id);
            else
                ui.DrawItemSprite(centre, SlotSize * 0.72f, entry.Id, 0f, 0f);
        }
    }

    private void DrawEntityGrid(UIRenderer ui, EntityPreviewState entities)
    {
        DrawGridHeader(ui, $"ENTITIES  ({entities.Entries.Count})");

        // No icon renderer exists for entities, so a slot is its display name.
        var scale = ui.CapHeightScale(TextCap);
        foreach (var (centre, size, index) in _slots)
        {
            DrawSlot(ui, centre, size, index == entities.SelectedIndex);
            var name = entities.Entries[index].DisplayName;
            var width = ui.MeasureTextWidth(name, scale);
            ui.DrawText(name, new Vector2(centre.X - width * 0.5f, centre.Y - TextCap * 0.5f), scale, Rgba.White);
        }
    }

    private void DrawTreeGrid(UIRenderer ui, TreePreviewState trees)
    {
        DrawGridHeader(ui, $"TREES  ({trees.Entries.Count})");

        foreach (var (centre, size, index) in _slots)
        {
            DrawSlot(ui, centre, size, index == trees.SelectedIndex);
            var name = TreePreviewState.DisplayNameOf(trees.Entries[index]);
            var (scale, cap) = FitText(ui, name, TextCap, size.X - 8f);
            var width = ui.MeasureTextWidth(name, scale);
            ui.DrawText(name, new Vector2(centre.X - width * 0.5f, centre.Y - cap * 0.5f), scale, Rgba.White);
        }
    }

    private void DrawGridHeader(UIRenderer ui, string header)
    {
        var top = Pad + TabRowH;
        ui.DrawText(header, new Vector2(Pad + 8f, top + 8f), ui.CapHeightScale(18f), Rgba.White);

        var scroll = Scroll;
        if (TotalRows() > _visibleRows)
            ui.DrawText($"{scroll + 1}-{Math.Min(scroll + _visibleRows, TotalRows())}/{TotalRows()}",
                new Vector2(Pad + 8f, top + HeaderH * 0.5f), ui.CapHeightScale(TextCap), TextDim);
    }

    private static void DrawSlot(UIRenderer ui, Vector2 centre, Vector2 size, bool selected)
    {
        ui.DrawRect(centre, size, SlotBg);
        if (selected)
            ui.DrawBorder(centre, size, 2f, Accent);
        else
            ui.DrawBorder(centre, size, 1f, PanelLine);
    }

    private static void DrawTitle(UIRenderer ui, ItemDisplayEditor editor, Vector2 screen, string status)
    {
        var scale = ui.CapHeightScale(TextCap);
        var left = screen.X - Pad - PanelWidth + Pad;

        ui.DrawText(editor.DisplayName, new Vector2(left, Pad + 8f), ui.CapHeightScale(18f), Rgba.White);
        if (!editor.IsPureItem && editor.HasSelection)
            ui.DrawText("block: not saved", new Vector2(left, Pad + 30f), scale, TextDim);
        else if (!string.IsNullOrEmpty(status))
            ui.DrawText(status, new Vector2(left, Pad + 30f), scale, Accent);
    }

    private static void DrawEntityTitle(UIRenderer ui, EntityPreviewState entities, Vector2 screen)
    {
        var left = screen.X - Pad - PanelWidth + Pad;
        var title = entities.Selected?.DisplayName ?? "No entities";
        ui.DrawText(title, new Vector2(left, Pad + 8f), ui.CapHeightScale(18f), Rgba.White);
        if (entities.Selected != null && !entities.HasClips)
            ui.DrawText(entities.PreviewMob != null ? "no clips: code-driven" : "no clips: rest pose", new Vector2(left, Pad + 30f), ui.CapHeightScale(TextCap), TextDim);
    }

    private void DrawTreeTitle(UIRenderer ui, TreePreviewState trees, Vector2 screen)
    {
        var left = screen.X - Pad - PanelWidth + Pad;
        var lineWidth = PanelWidth - Pad * 2f;
        if (trees.Selected is not { } species)
        {
            ui.DrawText("No tree species", new Vector2(left, Pad + 8f), ui.CapHeightScale(18f), Rgba.White);
            return;
        }

        ui.DrawText(TreePreviewState.DisplayNameOf(species), new Vector2(left, Pad + 8f), ui.CapHeightScale(18f),
            Rgba.White);
        DrawFitted(ui, $"step {trees.Step}/{trees.MaxStep}  mature {trees.MaturitySteps}  seed {trees.Seed}",
            new Vector2(left, Pad + 30f), lineWidth);

        // Below the step slider: what the shown tree is made of. Flat preview ground carves
        // nothing, so the roots feature never grows here.
        if (Slider(TreeStepSlider) is not { } slider) return;
        var y = slider.TrackCentre.Y + 22f;
        DrawFitted(ui, $"wood {trees.WoodCount}  leaves {trees.Cells.Count - trees.WoodCount}",
            new Vector2(left, y), lineWidth);
        if (!trees.FeaturesApplied)
        {
            y += 20f;
            DrawFitted(ui, "features at maturity", new Vector2(left, y), lineWidth);
        }
        DrawFitted(ui, "flat ground: no roots", new Vector2(left, y + 20f), lineWidth);
    }

    private static void DrawFitted(UIRenderer ui, string text, Vector2 topLeft, float maxWidth)
    {
        var (scale, _) = FitText(ui, text, TextCap, maxWidth);
        ui.DrawText(text, topLeft, scale, TextDim);
    }

    /// <summary>The text scale (and the cap height it gives) for <paramref name="text"/> at
    /// <paramref name="cap"/>, shrunk only as far as it takes to fit <paramref name="maxWidth"/>.</summary>
    private static (float Scale, float Cap) FitText(UIRenderer ui, string text, float cap, float maxWidth)
    {
        var scale = ui.CapHeightScale(cap);
        var width = ui.MeasureTextWidth(text, scale);
        if (width <= maxWidth || width <= 0f || maxWidth <= 0f) return (scale, cap);
        cap *= maxWidth / width;
        return (ui.CapHeightScale(cap), cap);
    }

    private void DrawControls(UIRenderer ui, Vector2 screen)
    {
        var scale = ui.CapHeightScale(TextCap);
        var left = screen.X - Pad - PanelWidth + Pad;
        var right = screen.X - Pad - Pad;

        foreach (var (centre, size, _, _, label, enabled, active) in _buttons)
        {
            var background = !enabled ? ButtonOff : active ? Accent : ButtonBg;
            ui.DrawRect(centre, size, background);
            ui.DrawBorder(centre, size, 1f, PanelLine);

            var (labelScale, cap) = FitText(ui, label, TextCap, size.X - 8f);
            var width = ui.MeasureTextWidth(label, labelScale);
            ui.DrawText(label, new Vector2(centre.X - width * 0.5f, centre.Y - cap * 0.5f), labelScale,
                enabled ? Rgba.White : TextDim);
        }

        foreach (var slider in _sliders)
        {
            // The label and value sit over their own track, so a slider need not be in the right panel's column.
            var labelY = slider.TrackCentre.Y - 22f;
            var trackStart = slider.TrackCentre.X - slider.TrackSize.X * 0.5f;
            var trackEnd = slider.TrackCentre.X + slider.TrackSize.X * 0.5f;
            ui.DrawText(slider.Label, new Vector2(trackStart, labelY), scale, Rgba.White);
            var valueWidth = ui.MeasureTextWidth(slider.Formatted, scale);
            ui.DrawText(slider.Formatted, new Vector2(trackEnd - valueWidth, labelY), scale, TextDim);

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
}
