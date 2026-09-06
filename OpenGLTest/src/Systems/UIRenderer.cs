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

    // Flat sprite icons for the pure item slots (see ItemType); block slots keep their 3D cubes
    private readonly Dictionary<byte, uint> _itemIcons = new();

    // Unit shapes, scaled per draw so a colour or size change costs no buffer work
    private readonly Mesh _unitSquare;

    // Count font, baked once from the TTF. Null when the file is missing or broken, in
    // which case the hotbar simply draws no counts.
    private readonly BitmapFont? _font;
    private readonly float _countScale;

    // Scratch for one DrawText call; reused so text layout allocates nothing per frame.
    // Safe to share: all UI drawing happens on the main thread.
    private readonly GlyphInstance[] _glyphScratch = new GlyphInstance[32];

    private Matrix4x4 _projection;
    private Vector2 _screenSize;
    private bool _depthCleared;

    public UIRenderer(GL gl, uint atlasTexture, BlockCubeCache blockCubes)
    {
        _gl = gl;
        _atlasTexture = atlasTexture;
        _blockCubes = blockCubes;

        var vertexSource = FileUtils.ReadFile("assets/shaders/ui-vertex.glsl");
        var fragmentSource = FileUtils.ReadFile("assets/shaders/ui-fragment.glsl");
        _shader = new Shader(gl, vertexSource, fragmentSource);
        _shader.SetUniform("uTex", 0);

        _crosshairTexture = TextureLoader.LoadFromFile(gl, "assets/textures/crosshair.png");
        foreach (var (id, type) in ItemType.Types)
            _itemIcons[id] = TextureLoader.LoadFromFile(gl, type.TexturePath);
        _unitSquare = Primitives.CreateSquare(gl, 1f);

        try
        {
            _font = new BitmapFont(gl, CountFontPath);
            _countScale = _font.ScaleForCapHeight(CountCapHeight);
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
        var slots = inventory.Slots;
        var stride = SlotSize + SlotGap;
        var rowWidth = slots.Length * SlotSize + (slots.Length - 1) * SlotGap;
        var firstCentreX = (_screenSize.X - rowWidth) * 0.5f + SlotSize * 0.5f;
        var centreY = _screenSize.Y - BottomMargin - SlotSize * 0.5f;
        var slotSize = new Vector2(SlotSize);

        for (var i = 0; i < slots.Length; i++)
        {
            var centre = new Vector2(firstCentreX + i * stride, centreY);
            var selected = i == inventory.SelectedIndex;

            DrawRect(centre, slotSize, selected ? SelectedBackground : SlotBackground);
            DrawBorder(centre, slotSize,
                selected ? SelectedBorder : SlotBorder,
                selected ? SelectedBorderColor : SlotBorderColor);
        }

        // Icons come after every background so the depth clear can't erase a later slot's panel
        for (var i = 0; i < slots.Length; i++)
        {
            var centre = new Vector2(firstCentreX + i * stride, centreY);
            // The pop marks a pickup; IconFill leaves enough margin for it to stay in the slot
            var iconSize = SlotSize * IconFill * inventory.PopScaleAt(i);
            // Items are flat sprites; blocks keep their 3D cubes
            if (ItemType.IsItem(slots[i]) && _itemIcons.TryGetValue(slots[i], out var icon))
                DrawTexture(centre, new Vector2(iconSize), icon);
            else
                DrawBlockIcon(centre, iconSize, slots[i]);
        }

        // Counts overlay the slot's bottom-right corner, after the icons so text is on top
        if (_font != null)
        {
            for (var i = 0; i < slots.Length; i++)
            {
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
