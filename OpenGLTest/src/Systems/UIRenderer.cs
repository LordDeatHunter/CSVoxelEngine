using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Draws screen-space UI through an orthographic projection measured in pixels, with the origin
/// at the top-left corner. Shapes come from <see cref="Primitives"/> in their own Y-up space and
/// are flipped into that Y-down frame by the model matrix, so the same geometry works here and
/// in the world.
///
/// Flat shapes draw with depth testing off, in call order. <see cref="DrawBlockIcon"/> turns it
/// back on against a cleared depth buffer, which is safe because UI is the last pass of a frame.
/// </summary>
public class UIRenderer : IDisposable
{
    // Ortho depth half-range. Only needs to clear a rotated icon cube's half-diagonal, but is
    // kept generous since nothing here depends on depth precision.
    private const float DepthRange = 512f;

    private const float CrosshairSize = 32f;

    // Hotbar metrics, in pixels
    private const float SlotSize = 56f;
    private const float SlotGap = 6f;
    private const float BottomMargin = 14f;
    private const float SlotBorder = 2f;
    private const float SelectedBorder = 3f;

    // Fraction of a slot the icon's footprint fills, leaving a margin inside the border
    private const float IconFill = 0.72f;

    // Hotbar count text, in pixels. Counts sit in the slot's bottom-right corner, sized
    // by digit cap-height so the size survives swapping the font file.
    private const string CountFontPath = "assets/fonts/kongtext.ttf";
    private const float CountCapHeight = 14f;
    private const float CountPadRight = 5f;
    private const float CountPadBottom = 4f;
    private static readonly Rgba CountShadow = new(0, 0, 0, 180);

    // The FPS counter's top-left corner, in pixels; the profiler table starts one line below it
    private const float FpsTop = 10f;

    // [E] inventory panel, in pixels: a Skyrim-style list on the left, preview on the right
    private const float PanelLeft = 40f;
    private const float PanelTop = 80f;
    private const float PanelWidth = 560f;
    private const float PanelPad = 10f;
    private const float TabHeight = 60f;
    private const float RowHeight = 42f;
    private const float RowIcon = 30f;
    private const float PreviewSize = 190f;
    private const float PreviewRight = 300f;
    private const float HeadCapHeight = 22f;
    private static readonly Rgba PanelBackground = Rgba.Grey(0.03f, 0.72f);
    private static readonly Rgba RowSelected = Rgba.Grey(0.22f, 0.8f);

    // Frame-timing overlay (DrawProfiler), in pixels
    private const float ProfilerLeft = 10f;
    private const float ProfilerPadding = 6f;
    private static readonly Rgba ProfilerBackground = Rgba.Grey(0.04f, 0.6f);

    // Three-quarter view: yaw swings a side face towards the viewer, pitch tilts the top down
    private const float IconYaw = 45f * MathF.PI / 180f;
    private const float IconPitch = 30f * MathF.PI / 180f;

    private static readonly Matrix4x4 IconRotation =
        Matrix4x4.CreateRotationY(IconYaw) * Matrix4x4.CreateRotationX(IconPitch);

    // Widest screen-space span of a unit cube under IconRotation. Icons are sized by footprint
    // and divided by this, so retuning the angles above never changes how much room they take.
    private static readonly float IconExtent = UnitCubeExtent(IconRotation);

    private static readonly Rgba SlotBackground = Rgba.Grey(0.08f, 0.55f);
    private static readonly Rgba SlotBorderColor = Rgba.Grey(0.65f, 0.45f);
    private static readonly Rgba SelectedBackground = Rgba.Grey(0.18f, 0.65f);
    private static readonly Rgba SelectedBorderColor = Rgba.White;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _atlasTexture;
    private readonly uint _crosshairTexture;
    private readonly BlockCubeCache _blockCubes;
    private readonly ItemMeshCache _itemMeshes;

    // Flat sprite textures for the list rows; the hotbar and the preview draw the same ids
    // as extruded 3D meshes through DrawItemIcon instead
    private readonly Dictionary<byte, uint> _itemIcons = new();
    private readonly int _locAlphaCutoff;

    // Unit shapes, scaled per draw so a colour or size change costs no buffer work
    private readonly Mesh _unitSquare;

    // Count font, baked once from the TTF. Null when the file is missing or broken, in
    // which case the hotbar simply draws no counts.
    private readonly BitmapFont? _font;
    private readonly float _countScale;
    private readonly float _headScale;

    // Scratch for one DrawText call; reused so text layout allocates nothing per frame.
    // Safe to share: all UI drawing happens on the main thread.
    private readonly GlyphInstance[] _glyphScratch = new GlyphInstance[32];

    private Matrix4x4 _projection;
    private Vector2 _screenSize;
    private bool _depthCleared;

    public UIRenderer(GL gl, uint atlasTexture, BlockCubeCache blockCubes, ItemMeshCache itemMeshes)
    {
        _gl = gl;
        _atlasTexture = atlasTexture;
        _blockCubes = blockCubes;
        _itemMeshes = itemMeshes;

        var vertexSource = FileUtils.ReadFile("assets/shaders/ui-vertex.glsl");
        var fragmentSource = FileUtils.ReadFile("assets/shaders/ui-fragment.glsl");
        _shader = new Shader(gl, vertexSource, fragmentSource);
        _shader.SetUniform("uTex", 0);

        _crosshairTexture = TextureLoader.LoadFromFile(gl, "assets/textures/crosshair.png");
        foreach (var (id, type) in ItemType.Types)
            _itemIcons[id] = TextureLoader.LoadFromFile(gl, type.TexturePath);
        _locAlphaCutoff = _shader.GetUniformLocation("uAlphaCutoff");
        _unitSquare = Primitives.CreateSquare(gl, 1f);

        try
        {
            _font = new BitmapFont(gl, CountFontPath);
            _countScale = _font.ScaleForCapHeight(CountCapHeight);
            _headScale = _font.ScaleForCapHeight(HeadCapHeight);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Console.WriteLine($"Warning: count font '{CountFontPath}' not loaded ({e.Message}); hotbar counts hidden.");
        }
    }

    /// <summary>Starts a UI pass sized to the window. Must be paired with <see cref="End"/>.</summary>
    public void Begin(Vector2D<int> screenSize)
    {
        _screenSize = new Vector2(screenSize.X, screenSize.Y);
        _projection = Matrix4x4.CreateOrthographicOffCenter(
            0f, _screenSize.X, _screenSize.Y, 0f, -DepthRange, DepthRange);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _depthCleared = false;

        _shader.Use();
        _shader.SetUniform("uProjection", _projection);
    }

    public void End()
    {
        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.DepthTest);
    }

    public Vector2 ScreenCentre => _screenSize * 0.5f;

    /// <summary>Solid-colour rectangle centred on <paramref name="centre"/>.</summary>
    public void DrawRect(Vector2 centre, Vector2 size, Rgba color) =>
        DrawMesh(_unitSquare, RectTransform(centre, size), color);

    public void DrawSquare(Vector2 centre, float size, Rgba color) =>
        DrawRect(centre, new Vector2(size), color);

    /// <summary>
    /// Textured rectangle. The whole texture is stretched to fit; for a region of an atlas,
    /// build a square with a <see cref="UvRect"/> via <see cref="Primitives"/> and draw it
    /// through <see cref="DrawMesh"/>.
    /// </summary>
    public void DrawTexture(Vector2 centre, Vector2 size, uint texture, Rgba? tint = null) =>
        DrawMesh(_unitSquare, RectTransform(centre, size), tint, texture);

    /// <summary>An outline drawn as four rectangles just inside the given bounds.</summary>
    public void DrawBorder(Vector2 centre, Vector2 size, float thickness, Rgba color)
    {
        var half = size * 0.5f;
        var horizontal = new Vector2(size.X, thickness);
        var vertical = new Vector2(thickness, size.Y - thickness * 2f);
        var inset = thickness * 0.5f;

        DrawRect(centre with { Y = centre.Y - half.Y + inset }, horizontal, color);
        DrawRect(centre with { Y = centre.Y + half.Y - inset }, horizontal, color);
        DrawRect(centre with { X = centre.X - half.X + inset }, vertical, color);
        DrawRect(centre with { X = centre.X + half.X - inset }, vertical, color);
    }

    /// <summary>
    /// A block drawn as a rotated 3D cube. Air (id 0) and unknown ids draw nothing.
    /// </summary>
    /// <param name="size">
    /// Width and height of the box the icon fits in, in pixels — not the cube's edge length,
    /// which is derived from the viewing angle.
    /// </param>
    public void DrawBlockIcon(Vector2 centre, float size, byte blockId)
    {
        var mesh = _blockCubes.Get(blockId);
        if (mesh == null) return;

        // Icons are convex and never overlap each other, so one clear per pass is enough
        // sorting. Flat shapes draw with the depth test off, which also stops them writing
        // depth, so nothing else in the pass can invalidate this.
        _gl.Enable(EnableCap.DepthTest);
        if (!_depthCleared)
        {
            _gl.Clear(ClearBufferMask.DepthBufferBit);
            _depthCleared = true;
        }

        var model = Matrix4x4.CreateScale(size / IconExtent)
                    * IconRotation
                    * FlipToScreen(centre);

        DrawMesh(mesh, model, Rgba.White, _atlasTexture);

        _gl.Disable(EnableCap.DepthTest);
    }

    public void DrawCrosshair() =>
        DrawTexture(ScreenCentre, new Vector2(CrosshairSize), _crosshairTexture);

    public void DrawFps(int fps) =>
        DrawText($"FPS {fps}", new Vector2(ProfilerLeft, FpsTop), _countScale, Rgba.White);

    /// <summary>
    /// Draws the frame-timing table under the FPS text: a translucent panel sized to the block
    /// so it reads over bright terrain, then one <see cref="DrawText"/> line per row. The lines
    /// are already formatted by <see cref="FrameProfiler"/> (rebuilt once a second), so this
    /// allocates nothing per frame.
    /// </summary>
    public void DrawProfiler(IReadOnlyList<string> lines)
    {
        if (_font == null || lines.Count == 0) return;

        // Cap height of a glyph, so the spacing follows the font rather than a magic number
        var lineStep = _font.MeasureSize("X".AsSpan(), _countScale).Y * 1.5f;
        var top = FpsTop + lineStep;

        // Only the longest line is measured — the count font is monospaced, so character
        // count orders the rows by width, and measuring all of them every frame would cost
        // as much again as drawing them
        var longest = 0;
        for (var i = 1; i < lines.Count; i++)
            if (lines[i].Length > lines[longest].Length)
                longest = i;

        var size = new Vector2(
            _font.MeasureSize(lines[longest].AsSpan(), _countScale).X + ProfilerPadding * 2f,
            lines.Count * lineStep + ProfilerPadding * 2f);
        DrawRect(new Vector2(ProfilerLeft - ProfilerPadding + size.X * 0.5f,
            top - ProfilerPadding + size.Y * 0.5f), size, ProfilerBackground);

        for (var i = 0; i < lines.Count; i++)
            DrawText(lines[i], new Vector2(ProfilerLeft, top + i * lineStep), _countScale, Rgba.White);
    }

    /// <summary>
    /// Screen-space text with its top-left corner at <paramref name="topLeft"/> (pixels, Y
    /// down). A one-pixel dark shadow is drawn under each glyph so the text reads over
    /// bright icons. Strings longer than 32 characters are truncated.
    /// </summary>
    public void DrawText(string text, Vector2 topLeft, float scale, Rgba color)
    {
        if (_font == null || string.IsNullOrEmpty(text))
            return;

        var instances = _glyphScratch.AsSpan();
        var n = _font.Layout(text.AsSpan(), topLeft, scale, instances);

        // Every shadow first, so each glyph's face draws over every shadow
        for (var i = 0; i < n; i++)
            DrawMesh(instances[i].Mesh,
                RectTransform(instances[i].Centre + new Vector2(1f, 1f), instances[i].Size),
                CountShadow, _font.Texture);

        for (var i = 0; i < n; i++)
            DrawMesh(instances[i].Mesh,
                RectTransform(instances[i].Centre, instances[i].Size),
                color, _font.Texture);
    }

    /// <summary>Draws the hotbar centred along the bottom edge, highlighting the selected slot.</summary>
    public void DrawHotbar(Inventory inventory)
    {
        var stride = SlotSize + SlotGap;
        var rowWidth = Inventory.SlotCount * SlotSize + (Inventory.SlotCount - 1) * SlotGap;
        var firstCentreX = (_screenSize.X - rowWidth) * 0.5f + SlotSize * 0.5f;
        var centreY = _screenSize.Y - BottomMargin - SlotSize * 0.5f;
        var slotSize = new Vector2(SlotSize);

        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var centre = new Vector2(firstCentreX + i * stride, centreY);
            var selected = i == inventory.SelectedIndex;

            DrawRect(centre, slotSize, selected ? SelectedBackground : SlotBackground);
            DrawBorder(centre, slotSize,
                selected ? SelectedBorder : SlotBorder,
                selected ? SelectedBorderColor : SlotBorderColor);
        }

        // Icons come after every background so the depth clear can't erase a later slot's panel
        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var id = inventory.IdAt(i);
            if (id == 0) continue;
            var centre = new Vector2(firstCentreX + i * stride, centreY);
            // The pop marks a pickup; IconFill leaves enough margin for it to stay in the slot
            var iconSize = SlotSize * IconFill * inventory.PopScaleAt(i);
            // Items show their flat sprites here like the list rows; blocks keep 3D cubes
            if (ItemType.IsItem(id) && _itemIcons.TryGetValue(id, out var icon))
                DrawTexture(centre, new Vector2(iconSize), icon);
            else
                DrawBlockIcon(centre, iconSize, id);
        }

        // Counts overlay the slot's bottom-right corner, after the icons so text is on top.
        // Slots share pool totals, so two slots showing one block show the same number.
        if (_font != null)
        {
            for (var i = 0; i < Inventory.SlotCount; i++)
            {
                if (inventory.IdAt(i) == 0) continue;
                var text = inventory.CountAt(i).ToString();
                var size = _font.MeasureSize(text.AsSpan(), _countScale);
                var slotRight = firstCentreX + i * stride + SlotSize * 0.5f;
                var slotBottom = centreY + SlotSize * 0.5f;
                DrawText(text,
                    new Vector2(slotRight - CountPadRight - size.X, slotBottom - CountPadBottom - size.Y),
                    _countScale, Rgba.White);
            }
        }
    }

    /// <summary>
    /// The [E] panel: tabs and a name-sorted list on the left, a rotating preview with the
    /// picked name on the right. The hotbar stays visible underneath so picks can be aimed
    /// at slots. <paramref name="time"/> spins the preview.
    /// </summary>
    public void DrawInventoryPanel(Inventory inventory, InventoryView view, float time)
    {
        var top = PanelTop;
        var bottom = _screenSize.Y - BottomMargin - (SlotSize + 14f);
        var height = Math.Max(RowHeight * 2f, bottom - top);
        var panel = new Vector2(PanelLeft + PanelWidth * 0.5f, top + height * 0.5f);
        DrawRect(panel, new Vector2(PanelWidth, height), PanelBackground);

        var tabs = new[] { InventoryCategory.Blocks, InventoryCategory.Tools, InventoryCategory.Misc };
        var tabWidth = (PanelWidth - PanelPad * 2f) / tabs.Length;
        for (var t = 0; t < tabs.Length; t++)
        {
            var centre = new Vector2(
                PanelLeft + PanelPad + tabWidth * (t + 0.5f), top + PanelPad + TabHeight * 0.5f);
            var active = view.Tab == tabs[t];
            DrawRect(centre, new Vector2(tabWidth - 4f, TabHeight),
                active ? SelectedBackground : SlotBackground);
            if (active)
                DrawBorder(centre, new Vector2(tabWidth - 4f, TabHeight), SlotBorder, SelectedBorderColor);
            DrawTextCentred(TabTitle(tabs[t]),
                centre.X, top + PanelPad + 6f, _countScale, Rgba.White);
            DrawTextCentred($"{inventory.UsedOf(tabs[t])}/{Inventory.CapacityOf(tabs[t])}",
                centre.X, top + PanelPad + 30f, _countScale, Rgba.White);
        }

        var listTop = top + PanelPad * 2f + TabHeight;
        if (view.Tab == InventoryCategory.Tools)
        {
            var tools = inventory.ToolEntries();
            for (var i = 0; i < tools.Count; i++)
            {
                var selected = view.SelectedToolInstance == tools[i].InstanceId;
                DrawEntryRow(i, listTop, tools[i].ItemId, tools[i].Name, "1", selected);
            }
        }
        else
        {
            var rows = inventory.StackEntries(view.Tab);
            for (var i = 0; i < rows.Count; i++)
                DrawEntryRow(i, listTop, rows[i].Id, rows[i].Name, rows[i].Count.ToString(),
                    view.SelectedId == rows[i].Id);
        }

        DrawPreview(inventory, view, time);
    }

    /// <summary>Hotbar slot under a screen point (pixels, top-left origin), if any.</summary>
    public int? HotbarSlotAt(Vector2 point)
    {
        var stride = SlotSize + SlotGap;
        var rowWidth = Inventory.SlotCount * SlotSize + (Inventory.SlotCount - 1) * SlotGap;
        var firstCentreX = (_screenSize.X - rowWidth) * 0.5f + SlotSize * 0.5f;
        var centreY = _screenSize.Y - BottomMargin - SlotSize * 0.5f;

        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var centre = new Vector2(firstCentreX + i * stride, centreY);
            if (MathF.Abs(point.X - centre.X) <= SlotSize * 0.5f
                && MathF.Abs(point.Y - centre.Y) <= SlotSize * 0.5f)
                return i;
        }
        return null;
    }

    /// <summary>Panel tab under a screen point, if any.</summary>
    public InventoryCategory? PanelTabAt(Vector2 point)
    {
        var tabs = new[] { InventoryCategory.Blocks, InventoryCategory.Tools, InventoryCategory.Misc };
        var tabWidth = (PanelWidth - PanelPad * 2f) / tabs.Length;

        if (point.X < PanelLeft || point.X > PanelLeft + PanelWidth) return null;
        if (point.Y < PanelTop + PanelPad || point.Y > PanelTop + PanelPad + TabHeight) return null;

        var t = (int)((point.X - PanelLeft - PanelPad) / tabWidth);
        return t >= 0 && t < tabs.Length ? tabs[t] : null;
    }

    /// <summary>List row index under a screen point for a tab holding this many rows, if any.</summary>
    public int? PanelRowAt(Vector2 point, int rowCount)
    {
        if (point.X < PanelLeft || point.X > PanelLeft + PanelWidth) return null;

        var listTop = PanelTop + PanelPad * 2f + TabHeight;
        var y = point.Y - listTop;
        if (y < 0) return null;

        var row = (int)(y / RowHeight);
        return row >= 0 && row < rowCount ? row : null;
    }

    private static string TabTitle(InventoryCategory tab) => tab switch
    {
        InventoryCategory.Blocks => "Blocks",
        InventoryCategory.Tools => "Tools",
        _ => "Misc",
    };

    private void DrawEntryRow(int row, float listTop, byte id, string name, string count, bool selected)
    {
        var centreY = listTop + row * RowHeight + RowHeight * 0.5f;
        var centre = new Vector2(PanelLeft + PanelWidth * 0.5f, centreY);
        DrawRect(centre, new Vector2(PanelWidth - PanelPad * 2f, RowHeight - 4f),
            selected ? RowSelected : SlotBackground);

        var iconCentre = new Vector2(PanelLeft + PanelPad + RowIcon * 0.5f + 4f, centreY);
        if (ItemType.IsItem(id) && _itemIcons.TryGetValue(id, out var icon))
            DrawTexture(iconCentre, new Vector2(RowIcon), icon);
        else
            DrawBlockIcon(iconCentre, RowIcon, id);

        DrawText(name, new Vector2(iconCentre.X + RowIcon * 0.5f + 8f, centreY - 9f),
            _countScale, Rgba.White);

        if (_font != null)
        {
            var size = _font.MeasureSize(count.AsSpan(), _countScale);
            DrawText(count,
                new Vector2(PanelLeft + PanelWidth - PanelPad - 8f - size.X, centreY - 9f),
                _countScale, Rgba.White);
        }
    }

    private void DrawPreview(Inventory inventory, InventoryView view, float time)
    {
        byte id = 0;
        string name = "";
        if (view.Tab == InventoryCategory.Tools)
        {
            if (view.SelectedToolInstance != 0)
            {
                id = inventory.ToolItemId(view.SelectedToolInstance);
                if (id != 0) name = Inventory.DisplayName(id);
            }
        }
        else if (view.SelectedId != 0 && inventory.CountFor(view.SelectedId) > 0)
        {
            id = view.SelectedId;
            name = Inventory.DisplayName(id);
        }
        if (id == 0) return;

        var centre = new Vector2(_screenSize.X - PreviewRight, _screenSize.Y * 0.38f);
        DrawRect(centre, new Vector2(PreviewSize + 40f, PreviewSize + 70f), PanelBackground);

        var spin = time * 0.9f;
        if (ItemType.IsItem(id))
            DrawItemIcon(centre, PreviewSize * 0.7f, id, spin);
        else
            DrawBlockIconSpinning(centre, PreviewSize * 0.7f, id, spin);

        DrawTextCentred(name, centre.X, centre.Y + PreviewSize * 0.5f + 22f, _headScale, Rgba.White);
    }

    /// <summary>
    /// A pure item as its extruded world-drop sprite, yawed like the block icons. Only the
    /// preview uses this; rows and hotbar draw the flat sprite.
    /// </summary>
    public void DrawItemIcon(Vector2 centre, float size, byte itemId, float yaw)
    {
        if (!_itemMeshes.TryGet(itemId, out var mesh, out var texture)) return;

        _gl.Enable(EnableCap.DepthTest);
        if (!_depthCleared)
        {
            _gl.Clear(ClearBufferMask.DepthBufferBit);
            _depthCleared = true;
        }

        var rotation = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(IconPitch);
        var model = Matrix4x4.CreateScale(size)
                    * rotation
                    * FlipToScreen(centre);

        // Cut out the sprite's transparent texels so they write neither colour nor depth:
        // without this the front face's invisible pixels depth-swallow the extrusion walls
        // behind them for half of every turn
        _shader.Use();
        _shader.SetUniform(_locAlphaCutoff, 0.5f);
        DrawMesh(mesh, model, Rgba.White, texture);
        _shader.SetUniform(_locAlphaCutoff, 0f);

        _gl.Disable(EnableCap.DepthTest);
    }

    /// <summary>A block cube yawed by <paramref name="yaw"/> instead of the fixed icon angle.</summary>
    public void DrawBlockIconSpinning(Vector2 centre, float size, byte blockId, float yaw)
    {
        var mesh = _blockCubes.Get(blockId);
        if (mesh == null) return;

        _gl.Enable(EnableCap.DepthTest);
        if (!_depthCleared)
        {
            _gl.Clear(ClearBufferMask.DepthBufferBit);
            _depthCleared = true;
        }

        var rotation = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(IconPitch);
        var model = Matrix4x4.CreateScale(size / IconExtent)
                    * rotation
                    * FlipToScreen(centre);

        DrawMesh(mesh, model, Rgba.White, _atlasTexture);

        _gl.Disable(EnableCap.DepthTest);
    }

    private void DrawTextCentred(string text, float centreX, float topY, float scale, Rgba color)
    {
        if (_font == null || string.IsNullOrEmpty(text)) return;
        var width = _font.MeasureSize(text.AsSpan(), scale).X;
        DrawText(text, new Vector2(centreX - width * 0.5f, topY), scale, color);
    }

    /// <summary>
    /// Draws any primitive with an explicit model matrix. <paramref name="texture"/> of 0 draws
    /// the mesh as flat colour without sampling.
    /// </summary>
    public void DrawMesh(Mesh mesh, Matrix4x4 model, Rgba? tint = null, uint texture = 0)
    {
        _shader.Use();
        _shader.SetUniform("uModel", model);
        _shader.SetUniform("uColor", (tint ?? Rgba.White).ToVector4());
        _shader.SetUniform("uUseTexture", texture != 0);

        if (texture != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, texture);
        }

        mesh.Render();
    }

    // Scales the unit square to pixel size and moves it into place. The negative Y scale is the
    // flip from the shape's Y-up space into this renderer's Y-down screen space.
    private static Matrix4x4 RectTransform(Vector2 centre, Vector2 size) =>
        Matrix4x4.CreateScale(size.X, -size.Y, 1f)
        * Matrix4x4.CreateTranslation(centre.X, centre.Y, 0f);

    /// <summary>
    /// Screen-space span of a rotated unit cube, taken as the larger of its width and height.
    /// Each axis' span is the sum of that axis' component across the rotated basis vectors.
    /// </summary>
    private static float UnitCubeExtent(Matrix4x4 rotation)
    {
        var width = MathF.Abs(rotation.M11) + MathF.Abs(rotation.M21) + MathF.Abs(rotation.M31);
        var height = MathF.Abs(rotation.M12) + MathF.Abs(rotation.M22) + MathF.Abs(rotation.M32);
        return MathF.Max(width, height);
    }

    private static Matrix4x4 FlipToScreen(Vector2 centre) =>
        Matrix4x4.CreateScale(1f, -1f, 1f)
        * Matrix4x4.CreateTranslation(centre.X, centre.Y, 0f);

    public void Dispose()
    {
        // _blockCubes is owned by RenderManager and shared with the world passes
        _font?.Dispose();
        _unitSquare.Dispose();
        _shader.Dispose();
        _gl.DeleteTexture(_crosshairTexture);
        foreach (var icon in _itemIcons.Values)
            _gl.DeleteTexture(icon);

        GC.SuppressFinalize(this);
    }
}
