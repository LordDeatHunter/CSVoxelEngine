using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>
/// Draws screen-space UI through an orthographic projection measured in canvas units, with the
/// origin at the top-left corner. The canvas is the window divided by a uniform scale the caller
/// picks (see <see cref="UiCanvas"/>), so every layout constant here is laid out against a
/// 1920x1080-ish reference and scales with the window; at scale 1 canvas units are window pixels.
/// One rule keeps input honest: public <c>*At</c> hit-test methods take a <b>window-pixel</b>
/// point and convert it once through <see cref="ToCanvas"/>; everything else (draws, private
/// geometry, <see cref="ScreenSize"/>) works in canvas units. Shapes come from <see cref="Primitives"/> in their own Y-up space and
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

    // Hearts row above the hotbar, in pixels. The sprites are 32px wide with 1px of empty
    // padding down each side, so only the middle 30px are drawn and the stride matches it:
    // neighbours sit exactly edge to edge with no double-padding gap.
    private const float HeartDrawWidth = 30f;
    private const float HeartDrawHeight = 32f;
    private const float HeartStride = 30f;
    private const float HeartGap = 10f;

    // Hotbar metrics, in pixels
    // Aliases of the shared hotbar geometry (HotbarLayout), so the HUD draws read short names.
    private const float SlotSize = HotbarLayout.SlotSize;
    private const float SlotGap = HotbarLayout.SlotGap;
    private const float BottomMargin = HotbarLayout.BottomMargin;
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
    // Aliases of InventoryPanelLayout, which owns the panel geometry; they serve the list, its
    // rows and the non-crafting preview only. The crafting card reads CraftCardLayout.
    private const float PanelLeft = InventoryPanelLayout.PanelLeft;
    private const float PanelTop = InventoryPanelLayout.PanelTop;
    private float PanelWidth => InventoryPanelLayout.PanelWidth(_screenSize);
    private const float PanelPad = InventoryPanelLayout.PanelPad;
    private const float TabHeight = InventoryPanelLayout.TabHeight;
    private const float RowHeight = InventoryPanelLayout.RowHeight;
    private const float RowIcon = 30f;
    private const float PreviewSize = 190f;
    private const float PreviewRight = 300f;
    private const float HeadCapHeight = 22f;
    private static readonly Rgba PanelBackground = Rgba.Grey(0.03f, 0.72f);
    private static readonly Rgba RowSelected = Rgba.Grey(0.22f, 0.8f);

    // Inventory prettify palette: warm gold accents, green ready states, muted dims
    private static readonly Rgba AccentGold = new(232, 184, 72);
    private static readonly Rgba AccentDim = new(150, 120, 60);
    private static readonly Rgba ReadyGreen = new(126, 217, 87);
    private static readonly Rgba DangerRed = new(230, 110, 110);
    private static readonly Rgba TextDim = Rgba.Grey(0.55f, 1f);
    private static readonly Rgba CardBackground = Rgba.Grey(0.07f, 0.85f);
    private static readonly Rgba ChipBackground = Rgba.Grey(0.12f, 0.9f);
    private static readonly Rgba ButtonReady = new(232, 184, 72);
    private static readonly Rgba ButtonDisabled = Rgba.Grey(0.12f, 0.9f);
    private static readonly Rgba ButtonTextReady = new(30, 26, 20);
    private static readonly Rgba ButtonTextDisabled = Rgba.Grey(0.45f, 1f);

    // Durability bar under a tool icon: fraction of the slot width, a few px tall.
    private const float DurabilityHeight = 5f;
    private const float DurabilityFill = 0.8f;
    private const float DurabilityPadBottom = 6f;
    private static readonly Rgba DurabilityBack = Rgba.Grey(0.05f, 0.9f);

    // Frame-timing overlay (DrawProfiler), in pixels
    private const float ProfilerLeft = 10f;
    private const float ProfilerPadding = 6f;
    private static readonly Rgba ProfilerBackground = Rgba.Grey(0.04f, 0.6f);

    // Top-center compass bar (DrawCompass), in pixels: a scrolling 120-degree window
    // of the horizon with cardinals every 45 degrees and minor ticks every 15
    private const float CompassTop = 10f;
    private const float CompassWidth = 440f;
    private const float CompassHeight = 42f;
    private const float CompassSidePad = 14f;
    private const float CompassHalfSpan = 60f;
    private const float CompassTickStep = 15f;
    private const float CompassCardinalStep = 45f;
    private const float CompassReadoutTop = 2f;
    private const float CompassTickTop = 20f;
    private const float CompassCoordsGap = 4f;
    private const float CompassSpawnDot = 8f;
    private const float CompassHideSpawnWithin = 1f;
    private static readonly Rgba CompassNorth = new(255, 92, 92);
    private static readonly Rgba CompassSpawn = new(140, 210, 255);
    private static readonly Rgba CompassTick = Rgba.Grey(0.75f, 0.9f);

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
    private readonly uint _heartTexture;
    private readonly uint _heartEmptyTexture;
    private readonly BlockCubeCache _blockCubes;
    private readonly ItemMeshCache _itemMeshes;

    // Flat sprite textures for the list rows; the hotbar and the preview draw the same ids
    // as extruded 3D meshes through DrawItemIcon instead
    private readonly Dictionary<byte, uint> _itemIcons = new();
    // One unit square per sheet frame, UV'd to that frame's array slice (see UvRect.Tile):
    // the icon texture of a spritesheet item holds one slice per frame, so picking a frame
    // is picking a mesh, and every frame mipmaps without bleeding into its neighbours.
    private readonly Dictionary<byte, Mesh[]> _itemFrames = new();
    private readonly int _locAlphaCutoff;
    private readonly int _locSrgbTex;
    // Whether the shared block atlas is SRGB8_ALPHA8 (the game) or linear (the ItemDebugger);
    // decides whether its icons are re-encoded for the gamma framebuffer.
    private readonly bool _atlasIsSrgb;

    // Unit shapes, scaled per draw so a colour or size change costs no buffer work
    private readonly Mesh _unitSquare;

    // Heart sprites with the 1px side padding cropped out of their UVs (see the Heart
    // metrics above): full width for empty/full, left half for a half heart.
    private readonly Mesh _heartEmptyMesh;
    private readonly Mesh _heartFullMesh;
    private readonly Mesh _heartHalfMesh;

    // Count font, baked once from the TTF. Null when the file is missing or broken, in
    // which case the hotbar simply draws no counts.
    private readonly BitmapFont? _font;
    private readonly float _countScale;
    private readonly float _headScale;

    // Formats the per-frame HUD strings only when their values change; the draws still happen
    // every frame (PERF_TODO R8).
    private readonly UITextCache _textCache = new();

    // Crafting card text measures, bound once in the constructor so neither the cache nor a
    // frame ever allocates a delegate; the "Batch" label is measured once for the same reason.
    private readonly TextMeasure _measureHead;
    private readonly TextMeasure _measureBody;
    private readonly float _batchLabelWidth;
    // The crafting card's wrapped layout, rebuilt only when its screen, recipe or status changes;
    // its Current is also what CraftHitAt hits, so a click reads the geometry that was drawn.
    private readonly CraftCardLayoutCache _craftLayouts;

    // Longest run DrawText draws. BitmapFont.Layout clamps its output to the scratch span it
    // is handed, so this single constant bounds all three text buffers at once — the glyph
    // scratch, the vertex scratch and the static index buffer — and a longer string is
    // truncated by layout rather than overrunning any of them.
    // Must agree with BitmapFont.MaxLayoutChars (layout stops there anyway), so it is defined from it.
    private const int MaxTextGlyphs = BitmapFont.MaxLayoutChars;

    // Scratch for one DrawText call; reused so text layout allocates nothing per frame.
    // Safe to share: all UI drawing happens on the main thread.
    private readonly GlyphInstance[] _glyphScratch = new GlyphInstance[MaxTextGlyphs];

    // One dynamic buffer for every glyph quad of a text run (shadow copies and face copies
    // both), so DrawText issues a single DrawElements instead of one per glyph per pass
    // (see DrawTextBatch). Sized for the worst case: every scratch slot as both a shadow and
    // a face quad. The index buffer is static — each quad's 6 indices only ever reference its
    // own 4 vertices at a fixed offset, exactly like the particle renderers' dynamic quads.
    private const int MaxTextQuads = MaxTextGlyphs * 2;
    private readonly Vertex[] _textVerts = new Vertex[MaxTextQuads * 4];
    private readonly uint _textVao, _textVbo, _textEbo;

    private Matrix4x4 _projection;
    // The canvas size (window / _scale): what every draw and layout constant is measured in.
    private Vector2 _screenSize;
    private Vector2 _windowSize;
    private Vector2 _framebufferSize;
    private float _scale = 1f;
    private bool _depthCleared;

    // Alpha multiplier DrawText applies to both the face and the shadow; only the loading
    // overlay lowers it (inside a try/finally), and Begin resets it to 1.
    private float _textFade = 1f;

    // Main-menu and loading-screen string caches, render thread only, so an idle menu allocates
    // nothing per frame: FitText allocates a Substring per trial length, and the seed and
    // percentage strings would otherwise be formatted every frame. Fitted strings are keyed by
    // the raw text, scale and width they were fitted to, and cleared when the canvas size changes.
    private const int MaxFittedTexts = 256;
    private readonly Dictionary<(string Text, float Scale, float Width), string> _fittedTexts = new();
    private Vector2 _fitCacheScreen;
    private readonly Dictionary<int, string> _seedTexts = new();
    private readonly string?[] _percentTexts = new string?[101];

    // The opaque backdrop behind every menu and loading frame: nothing is rendered behind those.
    private static readonly Rgba MenuBackdrop = Rgba.Grey(0.08f);

    /// <param name="atlasIsSrgb">Whether <paramref name="atlasTexture"/> was uploaded as
    /// <c>SRGB8_ALPHA8</c>: its icons are then re-encoded in the shader so they write the same
    /// bytes to the gamma framebuffer as a linear upload would.</param>
    public UIRenderer(GL gl, uint atlasTexture, BlockCubeCache blockCubes, ItemMeshCache itemMeshes,
        bool atlasIsSrgb)
    {
        _gl = gl;
        _atlasTexture = atlasTexture;
        _atlasIsSrgb = atlasIsSrgb;
        _blockCubes = blockCubes;
        _itemMeshes = itemMeshes;

        var vertexSource = FileUtils.ReadFile("assets/shaders/ui-vertex.glsl");
        var fragmentSource = FileUtils.ReadFile("assets/shaders/ui-fragment.glsl");
        _shader = new Shader(gl, vertexSource, fragmentSource);
        _shader.SetUniform("uTex", 0);
        _locSrgbTex = _shader.GetUniformLocation("uSrgbTex");
        _shader.SetUniform(_locSrgbTex, false);

        _crosshairTexture = TextureLoader.LoadFromFile(gl, "assets/textures/crosshair.png");
        _heartTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/heart.png");
        _heartEmptyTexture = TextureLoader.LoadFromFile(gl, "assets/textures/ui/heart_empty.png");
        var heartUv = new UvRect(new Vector2(1f / 32f, 0f), new Vector2(31f / 32f, 1f));
        var heartHalfUv = new UvRect(new Vector2(1f / 32f, 0f), new Vector2(16f / 32f, 1f));
        _heartEmptyMesh = Primitives.CreateRectangle(gl, 1f, 1f, uv: heartUv);
        _heartFullMesh = Primitives.CreateRectangle(gl, 1f, 1f, uv: heartUv);
        _heartHalfMesh = Primitives.CreateRectangle(gl, 1f, 1f, uv: heartHalfUv);
        foreach (var (id, type) in ItemRegistry.ById)
        {
            if (type is BlockItem) continue;
            _itemIcons[id] = type.Animation == ItemAnimationMode.None
                ? TextureLoader.LoadFromFile(gl, type.TexturePath)
                : TextureLoader.LoadSpritesheet(gl, type.TexturePath,
                    type.SpriteCols, type.SpriteRows, type.FrameCount);
            if (type.Animation == ItemAnimationMode.None) continue;
            var frames = new Mesh[type.FrameCount];
            for (var f = 0; f < frames.Length; f++)
                frames[f] = Primitives.CreateSquare(gl, 1f, uv: UvRect.Tile((ushort)f));
            _itemFrames[id] = frames;
        }
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
            Log.WriteLine($"Warning: count font '{CountFontPath}' not loaded ({e.Message}); hotbar counts hidden.");
        }

        if (_font is { } font)
        {
            var headScale = _headScale;
            var countScale = _countScale;
            _measureHead = s => font.MeasureSize(s, headScale).X;
            _measureBody = s => font.MeasureSize(s, countScale).X;
            _batchLabelWidth = font.MeasureSize("Batch".AsSpan(), countScale).X;
        }
        else
        {
            // No font: everything lays out as one line, and DrawText draws nothing anyway.
            _measureHead = static _ => 0f;
            _measureBody = static _ => 0f;
            _batchLabelWidth = 44f;
        }
        _craftLayouts = new CraftCardLayoutCache(_measureHead, _measureBody, _batchLabelWidth);

        // Text batch buffers: a dynamic VBO refilled per run (DrawTextBatch) and a static EBO
        // whose indices never change — quad q always draws vertices [4q, 4q+4) — exactly the
        // pattern the particle renderers use for their own per-frame dynamic quads.
        _textVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_textVao);

        _textVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textVbo);
        _gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer,
            (nuint)(_textVerts.Length * Vertex.Size), _textVerts, BufferUsageARB.DynamicDraw);

        var textIndices = new ushort[MaxTextQuads * 6];
        BuildQuadIndices(textIndices);

        _textEbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _textEbo);
        _gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer,
            (nuint)(textIndices.Length * sizeof(ushort)), textIndices, BufferUsageARB.StaticDraw);

        Mesh.ConfigureVertexAttribs(_gl);
        _gl.BindVertexArray(0);
    }

    /// <summary>Starts a UI pass at scale 1, the canvas being the window itself. Must be paired
    /// with <see cref="End"/>.</summary>
    public void Begin(Vector2D<int> screenSize) => Begin(screenSize, screenSize, 1f);

    /// <summary>
    /// Starts a UI pass on a virtual canvas of <c>windowSize / scale</c>. <paramref name="scale"/>
    /// is window pixels per canvas unit — the caller decides the policy (see
    /// <see cref="UiCanvas.ScaleFor"/>); a non-positive scale is treated as 1. The GL viewport is
    /// left alone (the full window); only the projection changes. Must be paired with
    /// <see cref="End"/>.
    /// </summary>
    public void Begin(Vector2D<int> windowSize, Vector2D<int> framebufferSize, float scale)
    {
        _windowSize = new Vector2(windowSize.X, windowSize.Y);
        _framebufferSize = new Vector2(framebufferSize.X, framebufferSize.Y);
        _scale = scale > 0f ? scale : 1f;
        _screenSize = UiCanvas.CanvasSizeFor(_windowSize, _scale);
        if (_screenSize != _fitCacheScreen)
        {
            // Fitted strings depend on the canvas they were fitted for.
            _fittedTexts.Clear();
            _fitCacheScreen = _screenSize;
        }
        _textFade = 1f;
        _projection = Matrix4x4.CreateOrthographicOffCenter(
            0f, _screenSize.X, _screenSize.Y, 0f, -DepthRange, DepthRange);

        // Safety: the pass never starts scissored, whatever a previous pass left behind.
        _gl.Disable(EnableCap.ScissorTest);
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

    /// <summary>The canvas size of the current (or last) pass, in canvas units.</summary>
    public Vector2 ScreenSize => _screenSize;

    /// <summary>Window pixels per canvas unit of the current (or last) pass.</summary>
    public float Scale => _scale;

    /// <summary>Window pixels -> the canvas every layout constant is measured in; valid after the
    /// first <see cref="Begin(Vector2D{int}, Vector2D{int}, float)"/>.</summary>
    public Vector2 ToCanvas(Vector2 windowPoint) => UiCanvas.WindowToCanvas(windowPoint, _scale);

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
        DrawRect(centre with { X = centre.X + half.X + inset }, vertical, color);
    }

    private void DrawDurabilityBar(Vector2 centre, float width, float fraction)
    {
        var f = MathUtils.Clamp01(fraction);
        DrawRect(centre, new Vector2(width, DurabilityHeight), DurabilityBack);
        if (f <= 0f) return;
        var fill = new Rgba((byte)(230 - 104 * f), (byte)(110 + 107 * f), (byte)(110 - 23 * f));
        DrawRect(new Vector2(centre.X - width * 0.5f + width * f * 0.5f, centre.Y),
            new Vector2(width * f, DurabilityHeight), fill);
    }

    /// <summary>
    /// Clears the depth buffer once per UI pass, the first time a 3D icon needs it. A glClear
    /// honours the scissor, so this must never first run while the list scissor is on — the
    /// inventory panel calls it before enabling the scissor for exactly that reason.
    /// </summary>
    private void EnsureDepthCleared()
    {
        if (_depthCleared) return;
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _depthCleared = true;
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
        EnsureDepthCleared();

        var model = Matrix4x4.CreateScale(size / IconExtent)
                    * IconRotation
                    * FlipToScreen(centre);

        // Cutout blocks (flower, leaves) carry transparent texels: discard them so they
        // draw neither colour nor depth, exactly like the world opaque pass and DrawItemIcon.
        // Without this the invisible pixels depth-swallow geometry behind them.
        var cutout = BlockType.ById[blockId]?.Cutout == true;
        if (cutout)
        {
            _shader.Use();
            _shader.SetUniform(_locAlphaCutoff, 0.5f);
        }
        DrawMesh(mesh, model, Rgba.White, _atlasTexture, _atlasIsSrgb);
        if (cutout)
            _shader.SetUniform(_locAlphaCutoff, 0f);

        _gl.Disable(EnableCap.DepthTest);
    }

    public void DrawCrosshair() =>
        DrawTexture(ScreenCentre, new Vector2(CrosshairSize), _crosshairTexture);

    /// <summary>
    /// The survival hearts: 10 in a row centred above the hotbar, 1 heart per 10 HP.
    /// Every heart draws its empty shell first and the red heart over it — full, left
    /// half (5 HP steps), or nothing — so damage reads heart by heart like Minecraft.
    /// </summary>
    public void DrawHearts(int health)
    {
        var hp = Math.Clamp(health, 0, PlayerHealth.MaxHealth);
        var rowWidth = HeartDrawWidth + (PlayerHealth.HeartCount - 1) * HeartStride;
        var firstX = (_screenSize.X - rowWidth) * 0.5f + HeartDrawWidth * 0.5f;
        var hotbarTop = _screenSize.Y - BottomMargin - SlotSize;
        var centreY = hotbarTop - HeartGap - HeartDrawHeight * 0.5f;
        var fullSize = new Vector2(HeartDrawWidth, HeartDrawHeight);
        var halfSize = new Vector2(HeartDrawWidth * 0.5f, HeartDrawHeight);

        for (var i = 0; i < PlayerHealth.HeartCount; i++)
        {
            var centre = new Vector2(firstX + i * HeartStride, centreY);
            DrawMesh(_heartEmptyMesh, RectTransform(centre, fullSize), Rgba.White, _heartEmptyTexture);

            var fill = Math.Clamp(hp - i * PlayerHealth.HpPerHeart, 0, PlayerHealth.HpPerHeart);
            if (fill >= PlayerHealth.HpPerHeart)
                DrawMesh(_heartFullMesh, RectTransform(centre, fullSize), Rgba.White, _heartTexture);
            else if (fill >= PlayerHealth.HpPerHeart / 2)
                DrawMesh(_heartHalfMesh,
                    RectTransform(centre with { X = centre.X - HeartDrawWidth * 0.25f }, halfSize),
                    Rgba.White, _heartTexture);
        }
    }

    public void DrawFps(int fps) =>
        DrawText(_textCache.Fps(fps), new Vector2(ProfilerLeft, FpsTop), _countScale, Rgba.White);

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
    /// RPG-style compass: a translucent bar across the top centre showing a 120-degree
    /// window of the horizon around the camera heading, with cardinal labels every 45
    /// degrees (N in red), minor ticks every 15, a centre caret, the numeric heading on
    /// top and a light-blue dot at the bearing toward the world spawn. The font only
    /// covers printable ASCII, so the readout is "N 005" rather than using a degree sign.
    /// <paramref name="yaw"/> is the camera yaw in degrees (0 looks down +X);
    /// <paramref name="position"/> is the eye position, also feeding the optional
    /// coordinates line under the bar.
    /// </summary>
    public void DrawCompass(float yaw, Vector3 position, bool showCoordinates = true)
    {
        var heading = Compass.HeadingFromYaw(yaw);
        var cx = _screenSize.X * 0.5f;
        var halfInner = CompassWidth * 0.5f - CompassSidePad;

        DrawRect(new Vector2(cx, CompassTop + CompassHeight * 0.5f),
            new Vector2(CompassWidth, CompassHeight), PanelBackground);

        var readout = _textCache.CompassReadout(heading);
        DrawTextCentred(readout, cx, CompassTop + CompassReadoutTop, _countScale, Rgba.White);

        var tickTop = CompassTop + CompassTickTop;
        var start = MathF.Floor((heading - CompassHalfSpan) / CompassTickStep) * CompassTickStep;
        for (var d = start; d <= heading + CompassHalfSpan + 0.01f; d += CompassTickStep)
        {
            var rel = Compass.WrapDelta(d - heading);
            if (MathF.Abs(rel) > CompassHalfSpan) continue;
            var x = cx + rel / CompassHalfSpan * halfInner;
            var norm = ((d % 360f) + 360f) % 360f;
            if (MathF.Abs(norm % CompassCardinalStep) < 0.01f)
            {
                var name = Compass.CardinalName(norm);
                DrawTextCentred(name, x, tickTop, _countScale,
                    name == "N" ? CompassNorth : Rgba.White);
            }
            else
            {
                DrawRect(new Vector2(x, tickTop + 7f), new Vector2(2f, 10f), CompassTick);
            }
        }

        var spawnDx = Compass.SpawnX - position.X;
        var spawnDz = Compass.SpawnZ - position.Z;
        if (MathF.Sqrt(spawnDx * spawnDx + spawnDz * spawnDz) > CompassHideSpawnWithin)
        {
            var spawnRel = Compass.WrapDelta(
                Compass.BearingTo(position.X, position.Z, Compass.SpawnX, Compass.SpawnZ) - heading);
            if (MathF.Abs(spawnRel) <= CompassHalfSpan)
                DrawSquare(new Vector2(cx + spawnRel / CompassHalfSpan * halfInner, tickTop + 16f),
                    CompassSpawnDot, CompassSpawn);
        }

        DrawRect(new Vector2(cx, tickTop + 8f), new Vector2(2f, 22f), Rgba.White);

        if (showCoordinates && _font != null)
        {
            var coords = _textCache.Coords(position);
            DrawTextCentred(coords, cx, CompassTop + CompassHeight + CompassCoordsGap,
                _countScale, Rgba.White);
        }
    }

    /// <summary>
    /// Screen-space text with its top-left corner at <paramref name="topLeft"/> (pixels, Y
    /// down). A one-pixel dark shadow is drawn under each glyph so the text reads over
    /// bright icons. Strings longer than 64 characters are truncated.
    ///
    /// Every glyph of the run — shadow copies and face copies alike — is written into one
    /// dynamic vertex buffer and drawn with a single <see cref="GL.DrawElements"/> call
    /// (<see cref="DrawTextBatch"/>), instead of the two draws per glyph a naive per-glyph
    /// <see cref="DrawMesh"/> would cost. Shadows are laid out before faces in the index
    /// order, so a glyph's face still draws over every shadow, exactly as separate draw
    /// calls in that order would.
    /// </summary>
    public void DrawText(string text, Vector2 topLeft, float scale, Rgba color)
    {
        if (_font == null || string.IsNullOrEmpty(text))
            return;
        var instances = _glyphScratch.AsSpan();
        var n = _font.Layout(text.AsSpan(), topLeft, scale, instances);
        if (n == 0)
            return;

        // _textFade is 1 outside the loading overlay, so ordinary text keeps its exact colours.
        var shadow = _textFade >= 1f ? CountShadow : Faded(CountShadow, _textFade);
        var face = _textFade >= 1f ? color : Faded(color, _textFade);
        for (var i = 0; i < n; i++)
            WriteGlyphQuad(_textVerts.AsSpan(i * 4, 4), instances[i],
                instances[i].Centre + new Vector2(1f, 1f), shadow);
        for (var i = 0; i < n; i++)
            WriteGlyphQuad(_textVerts.AsSpan((n + i) * 4, 4), instances[i],
                instances[i].Centre, face);

        DrawTextBatch(n * 2);
    }

    /// <summary>Draw scale mapping a digit's cap-height to <paramref name="pixels"/>, for
    /// callers that do not hold the font (the item debugger) and size their own text with it.</summary>
    public float CapHeightScale(float pixels) => _font?.ScaleForCapHeight(pixels) ?? 1f;

    /// <summary>Pixel width of <paramref name="text"/> at a draw scale, for anchoring.</summary>
    public float MeasureTextWidth(string text, float scale) =>
        _font == null || string.IsNullOrEmpty(text) ? 0f : _font.MeasureSize(text.AsSpan(), scale).X;

    /// <summary>
    /// Fills a static quad index buffer: quad <c>i</c> draws vertices <c>[4i, 4i+4)</c> as
    /// <c>4i, 4i+1, 4i+2, 4i, 4i+2, 4i+3</c> — the same triangle split and winding
    /// <see cref="Primitives.CreateRectangle"/> bakes into a single-quad mesh, so a batched
    /// glyph keeps the exact diagonal its own mesh had.
    /// </summary>
    internal static void BuildQuadIndices(Span<ushort> indices)
    {
        for (var i = 0; i < indices.Length / 6; i++)
        {
            var v = (ushort)(i * 4);
            var o = i * 6;
            indices[o + 0] = v;
            indices[o + 1] = (ushort)(v + 1);
            indices[o + 2] = (ushort)(v + 2);
            indices[o + 3] = v;
            indices[o + 4] = (ushort)(v + 2);
            indices[o + 5] = (ushort)(v + 3);
        }
    }

    /// <summary>
    /// Writes one glyph's 4 vertices into <paramref name="quad"/>, positioned exactly where
    /// <see cref="RectTransform"/> would have placed a unit rect of
    /// <paramref name="instance"/>.Size at <paramref name="centre"/> — this is that
    /// transform's closed form, which is why the local bottom corner lands at
    /// <c>centre.Y + half.Y</c> (the rect transform scales Y negatively).
    /// <paramref name="color"/> rides per-vertex instead of arriving as a per-draw uniform.
    /// Corner order matches the static text EBO (see <see cref="BuildQuadIndices"/>).
    /// </summary>
    internal static void WriteGlyphQuad(Span<Vertex> quad, GlyphInstance instance, Vector2 centre,
        Rgba color)
    {
        Span<Vector2> corners = stackalloc Vector2[4];
        instance.Uv.CornersTo(corners);
        var half = instance.Size * 0.5f;

        quad[0] = new Vertex(new Vector3(centre.X - half.X, centre.Y + half.Y, 0f),
            color.R, color.G, color.B, color.A, corners[0]);
        quad[1] = new Vertex(new Vector3(centre.X - half.X, centre.Y - half.Y, 0f),
            color.R, color.G, color.B, color.A, corners[1]);
        quad[2] = new Vertex(new Vector3(centre.X + half.X, centre.Y - half.Y, 0f),
            color.R, color.G, color.B, color.A, corners[2]);
        quad[3] = new Vertex(new Vector3(centre.X + half.X, centre.Y + half.Y, 0f),
            color.R, color.G, color.B, color.A, corners[3]);
    }

    /// <summary>
    /// Uploads the first <paramref name="quadCount"/> quads of <see cref="_textVerts"/> and
    /// draws them in one call. Colour rides per-vertex (see <see cref="WriteGlyphQuad"/>), so
    /// <c>uColor</c> stays white here: ui-fragment.glsl computes <c>fColor * uColor * texel</c>
    /// and a white <c>uColor</c> is an exact identity, so the per-vertex tint reproduces the
    /// old per-draw tint uniform exactly. That identity depends on the fragment shader keeping
    /// its per-vertex term — drop <c>fColor</c> there and all text turns white.
    ///
    /// Sets the same four uniforms <see cref="DrawMesh"/> does and no others, so
    /// <c>uAlphaCutoff</c> keeps the sticky value it had before.
    /// </summary>
    private void DrawTextBatch(int quadCount)
    {
        _shader.Use();
        _shader.SetUniform("uModel", Matrix4x4.Identity);
        _shader.SetUniform("uColor", Rgba.White.ToVector4());
        _shader.SetUniform("uUseTexture", true);
        // The font is a linear (UI-only) texture: never re-encode it.
        _shader.SetUniform(_locSrgbTex, false);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, _font!.Texture);

        _gl.BindVertexArray(_textVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textVbo);
        _gl.BufferSubData<Vertex>(BufferTargetARB.ArrayBuffer, 0,
            new ReadOnlySpan<Vertex>(_textVerts, 0, quadCount * 4));
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)(quadCount * 6),
                DrawElementsType.UnsignedShort, null);
        }
        // Leave no VAO bound: the text VAO owns a long-lived ELEMENT_ARRAY_BUFFER binding, and
        // any later element-buffer bind while it happened to be current would rewrite it.
        _gl.BindVertexArray(0);
    }

    /// <summary>Draws the hotbar centred along the bottom edge, highlighting the selected slot.</summary>
    /// <param name="headingDegrees">Compass heading (0 = north), picking heading-driven sheet frames.</param>
    /// <param name="timeSeconds">Scene clock, picking loop-driven sheet frames.</param>
    public void DrawHotbar(Inventory inventory, float headingDegrees, float timeSeconds)
    {
        var stride = SlotSize + SlotGap;
        var firstCentreX = HotbarLayout.FirstCentreX(_screenSize);
        var centreY = HotbarLayout.CentreY(_screenSize);
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
            if (ItemRegistry.IsItem(id) && _itemIcons.TryGetValue(id, out var icon))
                DrawItemSprite(centre, new Vector2(iconSize), id, icon, headingDegrees, timeSeconds);
            else
                DrawBlockIcon(centre, iconSize, id);
        }

        // Durability bars sit along the slot bottom, under the icons so they stay visible.
        for (var i = 0; i < Inventory.SlotCount; i++)
        {
            var slot = inventory.SlotAt(i);
            if (!slot.IsTool) continue;
            var fraction = inventory.ToolDurabilityFraction(slot.ToolInstance);
            if (fraction == null) continue;
            var centre = new Vector2(firstCentreX + i * stride,
                centreY + SlotSize * 0.5f - DurabilityPadBottom - DurabilityHeight * 0.5f);
            DrawDurabilityBar(centre, SlotSize * DurabilityFill, fraction.Value);
        }

        // Counts overlay the slot's bottom-right corner, after the icons so text is on top.
        // Slots share pool totals, so two slots showing one block show the same number.
        if (_font != null)
        {
            for (var i = 0; i < Inventory.SlotCount; i++)
            {
                if (inventory.IdAt(i) == 0) continue;
                var text = _textCache.SlotCount(i, inventory.CountAt(i));
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
    public void DrawInventoryPanel(Inventory inventory, InventoryView view, float time,
        float headingDegrees, bool creative)
    {
        var top = PanelTop;
        var height = InventoryPanelLayout.PanelHeight(_screenSize);
        var panel = new Vector2(PanelLeft + PanelWidth * 0.5f, top + height * 0.5f);
        DrawRect(panel, new Vector2(PanelWidth, height), PanelBackground);

        var tabs = InventoryPanelLayout.Tabs;
        var tabWidth = InventoryPanelLayout.TabWidth(_screenSize);
        for (var t = 0; t < tabs.Length; t++)
        {
            var centre = InventoryPanelLayout.TabCentre(t, _screenSize);
            var active = view.Tab == tabs[t];
            DrawRect(centre, new Vector2(tabWidth - 4f, TabHeight),
                active ? SelectedBackground : SlotBackground);
            if (active)
                DrawBorder(centre, new Vector2(tabWidth - 4f, TabHeight), SlotBorder, SelectedBorderColor);
            DrawTextCentredFitted(TabTitle(tabs[t]),
                centre.X, top + PanelPad + 6f, _countScale, Rgba.White, tabWidth - 8f);
            DrawTextCentredFitted(TabSubtitle(inventory, tabs[t]),
                centre.X, top + PanelPad + 30f, _countScale, Rgba.White, tabWidth - 8f);
        }

        var (searchCentre, searchSize) = InventoryPanelLayout.SearchBoxRect(_screenSize, creative);
        DrawInputField(searchCentre, searchSize, view.Search.Text,
            view.Focus == InventoryFocus.Search, "Search items and recipes", time);
        if (creative)
        {
            var (quantityCentre, quantitySize) = InventoryPanelLayout.QuantityBoxRect(_screenSize);
            DrawInputField(quantityCentre, quantitySize, view.Quantity,
                view.Focus == InventoryFocus.Quantity, "Qty", time);
        }

        // A query replaces the tab's list with search results; the list region below doubles as
        // the field's dropdown, and Program.HandleInventoryClick reads the same InventorySearch
        // call, so the drawn rows and the click always agree.
        IReadOnlyList<SearchHit>? hits = view.Search.IsEmpty
            ? null
            : InventorySearch.Search(view.Search.Query, inventory);

        // One list per frame for the active tab (never re-query StackEntries/ToolEntries for
        // the visible range: that would double the LINQ sort).
        IReadOnlyList<Inventory.ToolRow>? tools = null;
        IReadOnlyList<CraftingRecipe>? recipes = null;
        IReadOnlyList<Inventory.StackRow>? rows = null;
        int rowCount;
        if (hits != null)
            rowCount = hits.Count;
        else if (view.Tab == InventoryCategory.Tools)
            rowCount = (tools = inventory.ToolEntries()).Count;
        else if (view.Tab == InventoryCategory.Crafting)
            rowCount = (recipes = ContentRegistry.CraftingRecipes).Count;
        else
            rowCount = (rows = inventory.StackEntries(view.Tab)).Count;

        // Clamp the stored offset with this frame's row count, exactly as
        // Program.HandleInventoryClick does, so the drawn list and the click always agree.
        var offset = hits != null
            ? InventoryPanelLayout.ClampedOffset(view.Search.ScrollOffset, rowCount, _screenSize)
            : InventoryPanelLayout.ClampedOffset(view.ScrollOffset, rowCount, _screenSize);
        var listHeight = InventoryPanelLayout.ListHeight(_screenSize);
        var (first, end) = ScrollList.VisibleRange(offset, RowHeight, rowCount, listHeight);

        // A glClear honours the scissor, so the pass's one full-screen depth clear must happen
        // before the scissor goes on; otherwise the latch would record a clear that covered only
        // the list, and the preview and crafting-card icons would z-fight the world's depth.
        EnsureDepthCleared();

        // glScissor takes framebuffer pixels with a bottom-left origin, hence the conversion
        // from the canvas rect. Nothing but the row loops may run while it is enabled (the
        // preview, crafting card, pause menu and later passes all assume it off, and Begin
        // disables it as a belt-and-braces), and nothing may return from inside this block.
        var (sx, sy, sw, sh) = UiCanvas.ScissorRect(
            new Vector2(InventoryPanelLayout.PanelLeft, InventoryPanelLayout.ListTop),
            new Vector2(InventoryPanelLayout.PanelWidth(_screenSize), listHeight),
            _scale, _windowSize, _framebufferSize);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
        try
        {
            if (hits != null)
            {
                for (var i = first; i < end; i++)
                    DrawSearchRow(i, offset, hits[i], i == view.Search.Highlight, headingDegrees, time);
            }
            else if (tools != null)
            {
                for (var i = first; i < end; i++)
                {
                    var selected = view.SelectedToolInstance == tools[i].InstanceId;
                    DrawEntryRow(i, offset, tools[i].ItemId, tools[i].Name, "1", selected, false, headingDegrees,
                        time, null, inventory.ToolDurabilityFraction(tools[i].InstanceId));
                }
            }
            else if (recipes != null)
            {
                for (var i = first; i < end; i++)
                {
                    var ready = inventory.CanCraft(recipes[i]);
                    DrawEntryRow(i, offset, recipes[i].ResultId,
                        Inventory.DisplayName(recipes[i].ResultId),
                        $"x{recipes[i].ResultCount}",
                        view.Craft.Recipe != null && view.Craft.Recipe.Name == recipes[i].Name,
                        !ready, headingDegrees, time,
                        ready ? ReadyGreen : null);
                }
            }
            else if (rows != null)
            {
                for (var i = first; i < end; i++)
                    DrawEntryRow(i, offset, rows[i].Id, rows[i].Name, rows[i].Count.ToString(),
                        view.SelectedId == rows[i].Id, false, headingDegrees, time);
            }
        }
        finally
        {
            _gl.Disable(EnableCap.ScissorTest);
        }

        // The scrollbar sits in the panel's right padding and only exists while the list
        // overflows; the thumb is highlighted while it is being dragged.
        if (ScrollList.Overflows(InventoryPanelLayout.ContentHeight(rowCount), listHeight))
        {
            var track = InventoryPanelLayout.TrackRect(_screenSize);
            DrawRect(track.Centre, track.Size, SlotBackground);
            var thumb = InventoryPanelLayout.ThumbRect(_screenSize, offset, rowCount);
            DrawRect(thumb.Centre, thumb.Size,
                view.ThumbDragGrab.HasValue ? SelectedBorderColor : SlotBorderColor);
        }

        DrawPreview(inventory, view, time, headingDegrees);
    }

    /// <summary>
    /// The ESC pause menu over the still-rendered game: a fullscreen dim standing in for a
    /// blur (this pass has no blur target; the dark translucency keeps the scene visible
    /// but unreadable, like the Godot reference's BlurOverlay in overlay mode), then a
    /// centred title with RESUME / SETTINGS / SAVE &amp; QUIT buttons, or the settings title with a
    /// BACK button. Click targets come from <see cref="PauseMenuLayout"/> so drawing and
    /// hit-testing can never drift apart (the third button is still <see cref="PauseMenuHit.Exit"/>).
    /// </summary>
    public void DrawPauseMenu(PauseMenuView view, SettingsMenuView settings)
    {
        if (view.Page == PauseMenuPage.Closed) return;

        DrawRect(_screenSize * 0.5f, _screenSize, Rgba.Grey(0f, 0.62f));

        var screen = _screenSize;
        if (view.Page == PauseMenuPage.Main)
        {
            DrawTextCentred("PAUSED", screen.X * 0.5f,
                PauseMenuLayout.TitleTop(screen, view.Page), _headScale, Rgba.White);
            DrawMenuButton(PauseMenuLayout.MainButtonCentre(screen, 0), "RESUME");
            DrawMenuButton(PauseMenuLayout.MainButtonCentre(screen, 1), "SETTINGS");
            DrawMenuButton(PauseMenuLayout.MainButtonCentre(screen, 2), "SAVE & QUIT");
            return;
        }

        DrawSettingsMenu(settings);
    }

    /// <summary>
    /// The settings page: title, one tab strip (Display/Graphics/Water/Audio/Controls), the
    /// active tab's widget rows, BACK button. The busiest tab holds six rows, so the whole
    /// page fits without scrolling.
    /// </summary>
    public void DrawSettingsMenu(SettingsMenuView view)
    {
        var screen = _screenSize;
        var left = SettingsLayout.PanelLeft(screen);
        DrawTextCentred("SETTINGS", screen.X * 0.5f, SettingsLayout.PanelTop, _headScale, Rgba.White);

        for (var t = 0; t < SettingsRows.Tabs.Length; t++)
        {
            var centre = SettingsLayout.TabCentre(t, screen);
            var size = new Vector2(SettingsLayout.TabWidth() - 4f, SettingsLayout.TabHeight);
            var active = SettingsRows.Tabs[t] == view.Tab;
            DrawRect(centre, size, active ? SelectedBackground : SlotBackground);
            if (active)
                DrawBorder(centre, size, SlotBorder, SelectedBorderColor);
            DrawTextCentred(SettingsRows.Tabs[t], centre.X, centre.Y - 8f, _countScale, Rgba.White);
        }

        var rows = SettingsRows.RowsInTab(view.Tab);
        for (var r = 0; r < rows.Length; r++)
        {
            var row = SettingsRows.All[rows[r]];
            var centre = SettingsLayout.RowCentre(r, screen);
            DrawText(FitText(row.Label, _countScale, SettingsLayout.LabelWidth - 16f),
                new Vector2(left + 8f, centre.Y - 8f), _countScale, Rgba.White);

            switch (row)
            {
                case ToggleRow t:
                    DrawToggle(SettingsLayout.ToggleCentre(r, screen), t.Get());
                    DrawValueRight(centre.Y, SettingsRows.FormatValue(row), screen);
                    break;
                case SliderRow s:
                    DrawSlider(r, screen, s, view.DragRow == rows[r]);
                    DrawValueRight(centre.Y, SettingsRows.FormatValue(row), screen);
                    break;
                case OptionsRow:
                    // The GUI Scale row names the effective scale (this frame's Begin scale) while capped.
                    var value = ReferenceEquals(row, SettingsRows.GuiScaleRow)
                        ? UiCanvas.ScaleLabel(GameSettings.RequestedGuiScale, _scale)
                        : SettingsRows.FormatValue(row);
                    DrawTextCentred($"< {value} >",
                        SettingsLayout.TrackRect(r, screen).Centre.X,
                        centre.Y - 8f, _countScale, Rgba.White);
                    break;
            }
        }

        DrawMenuButton(SettingsLayout.BackCentre(screen), "BACK");
    }

    /// <summary>Settings widget under a window-pixel point (top-left origin), if any.</summary>
    public SettingsHit SettingsHitAt(Vector2 point, SettingsMenuView view)
    {
        point = ToCanvas(point);
        return SettingsLayout.HitAt(point, view.Tab, _screenSize);
    }

    private void DrawValueRight(float centreY, string text, Vector2 screen)
    {
        var w = TextWidth(text, _countScale);
        DrawText(text, new Vector2(SettingsLayout.ValueRight(screen) - w, centreY - 8f),
            _countScale, TextDim);
    }

    private void DrawToggle(Vector2 centre, bool on)
    {
        var size = new Vector2(SettingsLayout.ToggleSize);
        DrawRect(centre, size, SlotBackground);
        DrawBorder(centre, size, SlotBorder, SlotBorderColor);
        if (on)
            DrawSquare(centre, SettingsLayout.ToggleSize - 8f, AccentGold);
    }

    private void DrawSlider(int tabRow, Vector2 screen, SliderRow def, bool active)
    {
        var (centre, size) = SettingsLayout.TrackRect(tabRow, screen);
        var fraction = MathUtils.Clamp01((def.Get() - def.Min) / (def.Max - def.Min));
        DrawRect(centre, size, SlotBackground);
        if (fraction > 0f)
        {
            var fillW = size.X * fraction;
            DrawRect(new Vector2(centre.X - size.X * 0.5f + fillW * 0.5f, centre.Y),
                new Vector2(fillW, size.Y), active ? Rgba.White : AccentGold);
        }
        DrawSquare(new Vector2(centre.X - size.X * 0.5f + size.X * fraction, centre.Y),
            14f, active ? Rgba.White : SelectedBorderColor);
    }

    /// <summary>Menu button or BACK under a window-pixel point (top-left origin), if any.</summary>
    public PauseMenuHit PauseMenuHitAt(Vector2 point, PauseMenuView view)
    {
        point = ToCanvas(point);
        return PauseMenuLayout.HitAt(point, _screenSize, view.Page);
    }

    private void DrawMenuButton(Vector2 centre, string label)
    {
        DrawRect(centre, PauseMenuLayout.ButtonSize, SlotBackground);
        DrawBorder(centre, PauseMenuLayout.ButtonSize, SlotBorder, SelectedBorderColor);
        DrawTextCentred(label, centre.X, centre.Y - 9f, _countScale, Rgba.White);
    }

    /// <summary>
    /// The main menu the game boots to, over an opaque backdrop (no world is rendered behind a
    /// menu frame). Every rectangle comes from <see cref="MainMenuLayout"/> (the main page from
    /// <see cref="PauseMenuLayout"/>), which <see cref="MainMenuHitAt"/> also reads, so drawing and
    /// hit-testing cannot drift apart. The settings page is the pause menu's, shared as is.
    /// <paramref name="time"/> is the window clock, for the text caret's blink.
    /// </summary>
    public void DrawMainMenu(MainMenuView view, SettingsMenuView settings, float time)
    {
        var screen = _screenSize;
        DrawRect(screen * 0.5f, screen, MenuBackdrop);

        switch (view.Page)
        {
            case MainMenuPage.Main:
                DrawTextCentred("CSVOXELENGINE", screen.X * 0.5f,
                    MainMenuLayout.TitleTop(screen, MainMenuPage.Main), _headScale, Rgba.White);
                DrawMenuButton(PauseMenuLayout.MainButtonCentre(screen, 0), "START");
                DrawMenuButton(PauseMenuLayout.MainButtonCentre(screen, 1), "SETTINGS");
                DrawMenuButton(PauseMenuLayout.MainButtonCentre(screen, 2), "EXIT");
                break;

            case MainMenuPage.Worlds:
                DrawWorldsPage(view, screen);
                break;

            case MainMenuPage.Create:
                DrawTextCentred("NEW WORLD", screen.X * 0.5f,
                    MainMenuLayout.TitleTop(screen, MainMenuPage.Create), _headScale, Rgba.White);
                DrawLabelledField(MainMenuLayout.NameFieldCentre(screen), "NAME", view.Name,
                    view.Focus == MenuField.Name, WorldNames.DefaultName, time);
                DrawLabelledField(MainMenuLayout.SeedFieldCentre(screen), "SEED", view.Seed,
                    view.Focus == MenuField.Seed, "random", time);
                DrawMenuButton(MainMenuLayout.BottomButtonCentre(screen, 0), "CREATE");
                DrawMenuButton(MainMenuLayout.BottomButtonCentre(screen, 1), "BACK");
                break;

            case MainMenuPage.Edit:
                DrawTextCentred("EDIT WORLD", screen.X * 0.5f,
                    MainMenuLayout.TitleTop(screen, MainMenuPage.Edit), _headScale, Rgba.White);
                DrawLabelledField(MainMenuLayout.NameFieldCentre(screen), "NAME", view.Name,
                    view.Focus == MenuField.Name, WorldNames.DefaultName, time);
                DrawMenuButton(MainMenuLayout.BottomButtonCentre(screen, 0), "SAVE");
                DrawMenuButton(MainMenuLayout.BottomButtonCentre(screen, 1), "BACK");
                break;

            case MainMenuPage.ConfirmDelete:
                DrawWorldsPage(view, screen);
                DrawConfirmDelete(view, screen);
                break;

            case MainMenuPage.Settings:
                DrawSettingsMenu(settings);
                break;
        }
    }

    // The worlds list: title, panel, the scissored rows (name and seed on their own lines, never
    // concatenated since a layout run stops at MaxLayoutChars), the empty-state line, the notice
    // and the CREATE / BACK buttons.
    private void DrawWorldsPage(MainMenuView view, Vector2 screen)
    {
        DrawTextCentred("WORLDS", screen.X * 0.5f,
            MainMenuLayout.TitleTop(screen, MainMenuPage.Worlds), _headScale, Rgba.White);

        var left = MainMenuLayout.PanelLeft(screen);
        var top = MainMenuLayout.ListTop(screen);
        var listHeight = MainMenuLayout.ListHeight(screen);
        var panelCentre = new Vector2(left + MainMenuLayout.PanelWidth * 0.5f, top + listHeight * 0.5f);
        var panelSize = new Vector2(MainMenuLayout.PanelWidth, listHeight);
        DrawRect(panelCentre, panelSize, SlotBackground);
        DrawBorder(panelCentre, panelSize, SlotBorder, SlotBorderColor);

        var count = view.Worlds.Count;
        if (count == 0)
        {
            DrawTextCentred("No worlds yet - create one", panelCentre.X, panelCentre.Y - 8f,
                _countScale, TextDim);
        }
        else
        {
            // Clamped with this frame's row count, exactly as MainMenuLayout.HitAt clamps it.
            var offset = MainMenuLayout.ClampedOffset(view.ScrollOffset, count, screen);
            var (first, end) = MainMenuLayout.VisibleRows(offset, count, screen);
            var nameLeft = MainMenuLayout.NameLeft(screen);
            var nameWidth = MainMenuLayout.NameMaxWidth(screen);

            // glScissor takes framebuffer pixels with a bottom-left origin, hence the conversion.
            // Only the row loop runs while it is enabled, and nothing returns from inside it.
            var (sx, sy, sw, sh) = UiCanvas.ScissorRect(new Vector2(left, top), panelSize,
                _scale, _windowSize, _framebufferSize);
            _gl.Enable(EnableCap.ScissorTest);
            _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
            try
            {
                for (var i = first; i < end; i++)
                {
                    var world = view.Worlds[i];
                    var rowTop = MainMenuLayout.RowTop(i, offset, screen);
                    if (i > 0)
                        DrawRect(new Vector2(panelCentre.X, rowTop),
                            new Vector2(MainMenuLayout.PanelWidth - 2f * MainMenuLayout.RowPad, 1.5f),
                            SlotBorderColor);
                    DrawText(FittedText(world.Name, _countScale, nameWidth),
                        new Vector2(nameLeft, rowTop + 8f), _countScale, Rgba.White);
                    DrawText(SeedText(world.Seed), new Vector2(nameLeft, rowTop + 36f), _countScale, TextDim);
                    DrawSmallButton(MainMenuLayout.RowButtonCentre(screen, i, offset, 0),
                        MainMenuLayout.RowButtonSize, "PLAY");
                    DrawSmallButton(MainMenuLayout.RowButtonCentre(screen, i, offset, 1),
                        MainMenuLayout.RowButtonSize, "EDIT");
                    DrawSmallButton(MainMenuLayout.RowButtonCentre(screen, i, offset, 2),
                        MainMenuLayout.RowButtonSize, "DELETE", DangerRed);
                }
            }
            finally
            {
                _gl.Disable(EnableCap.ScissorTest);
            }
        }

        if (!string.IsNullOrEmpty(view.Notice))
            DrawTextCentred(FittedText(view.Notice, _countScale, MainMenuLayout.PanelWidth),
                screen.X * 0.5f, MainMenuLayout.NoticeTop(screen), _countScale, AccentGold);

        DrawMenuButton(MainMenuLayout.BottomButtonCentre(screen, 0), "CREATE");
        DrawMenuButton(MainMenuLayout.BottomButtonCentre(screen, 1), "BACK");
    }

    // The delete confirmation over the dimmed worlds page: question, the world's name on its own
    // line, DELETE / CANCEL.
    private void DrawConfirmDelete(MainMenuView view, Vector2 screen)
    {
        DrawRect(screen * 0.5f, screen, Rgba.Grey(0f, 0.62f));

        var centre = MainMenuLayout.ConfirmBoxCentre(screen);
        var size = MainMenuLayout.ConfirmBoxSize;
        // CardBackground rather than the translucent SlotBackground, so the rows under the dim
        // do not show through the box.
        DrawRect(centre, size, CardBackground);
        DrawBorder(centre, size, SlotBorder, SelectedBorderColor);

        var top = centre.Y - size.Y * 0.5f;
        DrawTextCentred("Delete this world?", centre.X, top + 32f, _headScale, Rgba.White);
        if (view.SelectedWorld is { } world)
            DrawTextCentred(FittedText(world.Name, _countScale, size.X - 48f),
                centre.X, top + 84f, _countScale, AccentGold);

        DrawSmallButton(MainMenuLayout.ConfirmYesCentre(screen), MainMenuLayout.ConfirmButtonSize,
            "DELETE", DangerRed);
        DrawSmallButton(MainMenuLayout.ConfirmNoCentre(screen), MainMenuLayout.ConfirmButtonSize, "CANCEL");
    }

    private void DrawLabelledField(Vector2 centre, string label, TextFieldState field, bool focused,
        string placeholder, float time)
    {
        DrawText(label, new Vector2(centre.X - MainMenuLayout.FieldSize.X * 0.5f,
            MainMenuLayout.FieldLabelTop(centre)), _countScale, TextDim);
        DrawInputField(centre, MainMenuLayout.FieldSize, field, focused, placeholder, time);
    }

    /// <summary>A <see cref="DrawMenuButton"/> of any size, with an optional border colour.</summary>
    private void DrawSmallButton(Vector2 centre, Vector2 size, string label, Rgba? border = null)
    {
        DrawRect(centre, size, SlotBackground);
        DrawBorder(centre, size, SlotBorder, border ?? SelectedBorderColor);
        DrawTextCentred(label, centre.X, centre.Y - 9f, _countScale, Rgba.White);
    }

    /// <summary>
    /// A one-line text field: the text left-aligned, the placeholder dimmed while it is empty,
    /// and while focused a caret after the text blinking twice a second on the window clock.
    /// Shared by the main menu, the inventory search field and the creative quantity field;
    /// callers size the text to the box's length cap.
    /// </summary>
    public void DrawInputField(Vector2 centre, Vector2 size, TextFieldState field, bool focused,
        string placeholder, float time)
    {
        const float pad = 12f;
        DrawRect(centre, size, SlotBackground);
        DrawBorder(centre, size, SlotBorder, focused ? SelectedBorderColor : SlotBorderColor);

        var textLeft = centre.X - size.X * 0.5f + pad;
        var textTop = centre.Y - 9f;
        if (field.IsEmpty)
            DrawText(placeholder, new Vector2(textLeft, textTop), _countScale, TextDim);
        else
            DrawText(field.Text, new Vector2(textLeft, textTop), _countScale, Rgba.White);

        if (focused && ((int)(time * 2f)) % 2 == 0)
        {
            var caretX = textLeft + (field.IsEmpty ? 0f : TextWidth(field.Text, _countScale)) + 3f;
            DrawRect(new Vector2(caretX, centre.Y), new Vector2(2f, 22f), Rgba.White);
        }
    }

    /// <summary>Main-menu widget under a window-pixel point (top-left origin).</summary>
    public MainMenuHit MainMenuHitAt(Vector2 windowPoint, MainMenuView view) =>
        MainMenuLayout.HitAt(ToCanvas(windowPoint), _screenSize, view);

    /// <summary>Whether a window-pixel point is over the worlds list (the mouse-wheel target).</summary>
    public bool IsOverWorldList(Vector2 windowPoint) =>
        MainMenuLayout.IsOverList(ToCanvas(windowPoint), _screenSize);

    /// <summary>
    /// The loading screen: the world name, an animated spinner (a pure function of the window
    /// clock, so a hitch never stalls it), the caption and, for <see cref="LoadingScreenKind.Loading"/>,
    /// a progress bar with its percentage. <paramref name="opaque"/> draws a fully opaque backdrop
    /// (a loading or saving frame, with nothing behind it); otherwise the backdrop takes
    /// <see cref="LoadingScreenView.FadeAlpha"/>, which is the fade over the first playing frames.
    /// Every other element is multiplied by the fade alpha either way. Geometry comes from
    /// <see cref="LoadingScreenLayout"/>.
    /// </summary>
    public void DrawLoadingScreen(LoadingScreenView view, double time, bool opaque)
    {
        var fade = MathUtils.Clamp01(view.FadeAlpha);
        var screen = _screenSize;
        DrawRect(screen * 0.5f, screen, opaque ? MenuBackdrop : Faded(MenuBackdrop, fade));
        if (fade <= 0f) return;

        _textFade = fade;
        try
        {
            var barSize = LoadingScreenLayout.BarSize;
            if (!string.IsNullOrEmpty(view.WorldName))
                DrawTextCentred(FittedText(view.WorldName, _headScale, barSize.X), screen.X * 0.5f,
                    LoadingScreenLayout.TitleTop(screen), _headScale, Rgba.White);
            DrawTextCentred(view.Caption, screen.X * 0.5f, LoadingScreenLayout.CaptionTop(screen),
                _countScale, TextDim);

            const int dots = WorldGenerationConfig.LoadingSpinnerDots;
            var phase = LoadingScreenLayout.SpinnerPhase(time);
            for (var d = 0; d < dots; d++)
            {
                var brightness = LoadingScreenLayout.DotBrightness(d, dots, phase);
                DrawSquare(LoadingScreenLayout.DotCentre(screen, d, dots), LoadingScreenLayout.DotSize,
                    Faded(AccentGold, brightness * fade));
            }

            if (view.Kind == LoadingScreenKind.Loading)
            {
                var centre = LoadingScreenLayout.BarCentre(screen);
                DrawRect(centre, barSize, Faded(SlotBackground, fade));
                DrawBorder(centre, barSize, SlotBorder, Faded(SlotBorderColor, fade));
                var progress = MathUtils.Clamp01(view.ShownProgress);
                if (progress > 0f)
                {
                    var fillW = barSize.X * progress;
                    DrawRect(new Vector2(centre.X - barSize.X * 0.5f + fillW * 0.5f, centre.Y),
                        new Vector2(fillW, barSize.Y), Faded(AccentGold, fade));
                }
                DrawTextCentred(PercentText(progress), screen.X * 0.5f,
                    LoadingScreenLayout.PercentTop(screen), _countScale, Rgba.White);
            }
        }
        finally
        {
            _textFade = 1f;
        }
    }

    // A colour with its alpha multiplied by factor (clamped to 0..1).
    private static Rgba Faded(Rgba color, float factor) =>
        color with { A = (byte)(color.A * MathUtils.Clamp01(factor) + 0.5f) };

    // FitText through the cache: each (text, width) pair is fitted once per canvas size.
    private string FittedText(string text, float scale, float maxWidth)
    {
        if (_fittedTexts.TryGetValue((text, scale, maxWidth), out var fitted)) return fitted;
        if (_fittedTexts.Count >= MaxFittedTexts) _fittedTexts.Clear();
        fitted = FitText(text, scale, maxWidth);
        _fittedTexts[(text, scale, maxWidth)] = fitted;
        return fitted;
    }

    private string SeedText(int seed)
    {
        if (_seedTexts.TryGetValue(seed, out var text)) return text;
        if (_seedTexts.Count >= MaxFittedTexts) _seedTexts.Clear();
        text = $"seed {seed}";
        _seedTexts[seed] = text;
        return text;
    }

    private string PercentText(float progress)
    {
        var p = Math.Clamp((int)MathF.Round(progress * 100f), 0, 100);
        return _percentTexts[p] ??= $"{p}%";
    }

    /// <summary>Hotbar slot under a window-pixel point (top-left origin), if any.</summary>
    public int? HotbarSlotAt(Vector2 point)
    {
        point = ToCanvas(point);
        return HotbarLayout.SlotAt(point, _screenSize);
    }

    /// <summary>Panel tab under a window-pixel point, if any.</summary>
    public InventoryCategory? PanelTabAt(Vector2 point)
    {
        point = ToCanvas(point);
        return InventoryPanelLayout.TabAt(point, _screenSize);
    }

    /// <summary>
    /// List row index under a window-pixel point for a tab holding this many rows, scrolled by
    /// <paramref name="scrollOffset"/> canvas pixels, if any. Only the row rectangle hits; the
    /// panel's right padding is reserved for the scrollbar.
    /// </summary>
    public int? PanelRowAt(Vector2 point, float scrollOffset, int rowCount)
    {
        point = ToCanvas(point);
        return InventoryPanelLayout.RowAt(point, _screenSize, scrollOffset, rowCount);
    }

    /// <summary>Whether a window-pixel point is over the panel's search field box.</summary>
    public bool SearchBoxAt(Vector2 windowPoint, bool creative) =>
        InventoryPanelLayout.SearchBoxAt(ToCanvas(windowPoint), _screenSize, creative);

    /// <summary>Whether a window-pixel point is over the creative bulk-quantity field box.</summary>
    public bool QuantityBoxAt(Vector2 windowPoint) =>
        InventoryPanelLayout.QuantityBoxAt(ToCanvas(windowPoint), _screenSize);

    private static string TabTitle(InventoryCategory tab) => tab switch
    {
        InventoryCategory.Blocks => "Blocks",
        InventoryCategory.Tools => "Tools",
        InventoryCategory.Crafting => "Craft",
        _ => "Misc",
    };

    private static string TabSubtitle(Inventory inventory, InventoryCategory tab) =>
        tab == InventoryCategory.Crafting
            ? $"{inventory.Craftable().Count}/{ContentRegistry.CraftingRecipes.Count}"
            : $"{inventory.UsedOf(tab)}/{Inventory.CapacityOf(tab)}";

    private void DrawEntryRow(int row, float offset, byte id, string name, string count, bool selected,
        bool dim, float headingDegrees, float timeSeconds, Rgba? accent = null, float? durability = null)
    {
        var centreY = InventoryPanelLayout.RowCentreY(row, offset, _screenSize);
        var centre = new Vector2(PanelLeft + PanelWidth * 0.5f, centreY);
        DrawRect(centre, new Vector2(PanelWidth - PanelPad * 2f, RowHeight - 4f),
            selected ? RowSelected : SlotBackground);

        var barColor = accent ?? (selected ? SelectedBorderColor : SlotBorderColor);
        DrawRect(new Vector2(PanelLeft + PanelPad + 2f, centreY),
            new Vector2(4f, RowHeight - 4f), dim ? TextDim : barColor);

        var iconCentre = new Vector2(PanelLeft + PanelPad + RowIcon * 0.5f + 8f, centreY);
        if (ItemRegistry.IsItem(id) && _itemIcons.TryGetValue(id, out var icon))
            DrawItemSprite(iconCentre, new Vector2(RowIcon), id, icon, headingDegrees, timeSeconds);
        else
            DrawBlockIcon(iconCentre, RowIcon, id);

        DrawText(FitText(name, _countScale, RowNameAvail(count)), new Vector2(iconCentre.X + RowIcon * 0.5f + 8f, centreY - 9f),
            _countScale, dim && !selected ? TextDim : Rgba.White);

        if (_font != null)
        {
            var size = _font.MeasureSize(count.AsSpan(), _countScale);
            DrawText(count,
                new Vector2(PanelLeft + PanelWidth - PanelPad - 8f - size.X, centreY - 9f),
                _countScale, dim && !selected ? TextDim : Rgba.White);
        }

        if (durability != null)
        {
            var barLeft = iconCentre.X + RowIcon * 0.5f + 8f;
            const float barWidth = 140f;
            DrawDurabilityBar(new Vector2(barLeft + barWidth * 0.5f, centreY + 11f),
                barWidth, durability.Value);
        }
    }

    private void DrawSearchRow(int row, float offset, SearchHit hit, bool selected,
        float headingDegrees, float timeSeconds)
    {
        var detail = hit.IsRecipe ? $"recipe x{hit.Count}"
            : hit.ToolInstance != 0 ? "tool"
            : hit.Count.ToString();
        DrawEntryRow(row, offset, hit.Id, hit.Name, detail, selected, false,
            headingDegrees, timeSeconds, hit.IsRecipe ? AccentGold : null);
    }

    private void DrawPreview(Inventory inventory, InventoryView view, float time, float headingDegrees)
    {
        if (view.Tab == InventoryCategory.Crafting)
        {
            DrawCraftDetail(inventory, view, time, headingDegrees);
            return;
        }

        byte id = 0;
        string name = "";
        string stock = "";
        if (view.Tab == InventoryCategory.Tools)
        {
            if (view.SelectedToolInstance != 0)
            {
                id = inventory.ToolItemId(view.SelectedToolInstance);
                if (id != 0)
                {
                    name = Inventory.DisplayName(id);
                    stock = "tool in hand";
                }
            }
        }
        else if (view.SelectedId != 0 && inventory.CountFor(view.SelectedId) > 0)
        {
            id = view.SelectedId;
            name = Inventory.DisplayName(id);
            stock = $"x{inventory.CountFor(id)} in stock";
        }
        if (id == 0) return;

        var centre = new Vector2(_screenSize.X - PreviewRight, _screenSize.Y * 0.38f);
        var cardSize = new Vector2(PreviewSize + 40f, PreviewSize + 96f);
        DrawRect(centre, cardSize, PanelBackground);
        DrawBorder(centre, cardSize, SlotBorder, SlotBorderColor);
        DrawRect(new Vector2(centre.X, centre.Y - cardSize.Y * 0.5f + 3f),
            new Vector2(cardSize.X - 4f, 2f), AccentGold);

        var spin = time * 0.9f;
        if (ItemRegistry.IsItem(id))
            DrawItemIcon(centre with { Y = centre.Y - 12f }, PreviewSize * 0.7f, id, spin);
        else
            DrawBlockIconSpinning(centre with { Y = centre.Y - 12f }, PreviewSize * 0.7f, id, spin);

        DrawTextCentredFitted(name, centre.X, centre.Y + PreviewSize * 0.5f + 12f, _headScale, Rgba.White,
            cardSize.X - 16f);
        DrawTextCentred(stock, centre.X, centre.Y + PreviewSize * 0.5f + 40f, _countScale, TextDim);
        DrawTextCentred("Click a hotbar slot to equip", centre.X,
            centre.Y + PreviewSize * 0.5f + 58f, _countScale, TextDim);
    }

    /// <summary>
    /// This frame's crafting card layout, or null (and the cache cleared, so a stale layout is
    /// never hit-tested) when no recipe is open.
    /// </summary>
    private CraftCardLayout? CraftLayoutFor(Inventory inventory, InventoryView view)
    {
        if (view.Craft.Recipe is not { } recipe)
        {
            _craftLayouts.Clear();
            return null;
        }
        return _craftLayouts.Get(_screenSize, recipe, view.Craft.StatusLine(inventory));
    }

    /// <summary>
    /// Which crafting control sits under a window-pixel point, if any. Hits the layout that was
    /// last drawn, so a click can never disagree with the pixels; a layout that no longer matches
    /// the open recipe or the canvas size hits nothing.
    /// </summary>
    public CraftHit CraftHitAt(Vector2 point, InventoryView view)
    {
        point = ToCanvas(point);
        var layout = _craftLayouts.Current;
        if (layout == null || view.Craft.Recipe == null
            || !ReferenceEquals(layout.Recipe, view.Craft.Recipe) || layout.Screen != _screenSize)
            return new CraftHit(CraftHitKind.None);
        return layout.HitAt(point, layout.ClampScroll(view.CraftScrollOffset));
    }

    /// <summary>Whether a window-pixel point lies on the last drawn crafting card.</summary>
    public bool IsOverCraftCard(Vector2 windowPoint) =>
        _craftLayouts.Current?.Contains(ToCanvas(windowPoint)) ?? false;

    /// <summary>Clamps a crafting card scroll offset against the last drawn layout (0 without one).</summary>
    public float ClampCraftScroll(float offset) => _craftLayouts.Current?.ClampScroll(offset) ?? 0f;

    private void DrawBoxButton(Vector2 centre, float size, string label, Rgba bg, Rgba border, Rgba text)
    {
        DrawRect(centre, new Vector2(size), bg);
        DrawBorder(centre, new Vector2(size), 1.5f, border);
        DrawTextCentred(label, centre.X, centre.Y - 8f, _countScale, text);
    }

    private void DrawIdIcon(Vector2 centre, float size, byte id, float headingDegrees, float timeSeconds)
    {
        if (ItemRegistry.IsItem(id) && _itemIcons.TryGetValue(id, out var icon))
            DrawItemSprite(centre, new Vector2(size), id, icon, headingDegrees, timeSeconds);
        else
            DrawBlockIcon(centre, size, id);
    }

    private void DrawCraftDetail(Inventory inventory, InventoryView view, float time, float headingDegrees)
    {
        const float top = CraftCardLayout.Top;
        var layout = CraftLayoutFor(inventory, view);
        if (layout == null)
        {
            var width = CraftCardLayout.WidthFor(_screenSize);
            var centre = new Vector2(CraftCardLayout.LeftFor(_screenSize) + width * 0.5f,
                top + CraftCardLayout.EmptyHeight * 0.5f);
            var size = new Vector2(width, CraftCardLayout.EmptyHeight);
            DrawRect(centre, size, PanelBackground);
            DrawBorder(centre, size, SlotBorder, SlotBorderColor);
            DrawRect(new Vector2(centre.X, top + 3f), new Vector2(width - 4f, 2f), AccentGold);
            var avail = CraftCardLayout.InnerWidthFor(_screenSize) - 8f;
            DrawTextCentredFitted("Crafting", centre.X, top + CraftCardLayout.Pad, _headScale, Rgba.White, avail);
            DrawTextCentredFitted("Pick a recipe on the left", centre.X,
                top + CraftCardLayout.Pad + 34f, _countScale, TextDim, avail);
            DrawTextCentredFitted("then feed it items and hit CRAFT", centre.X,
                top + CraftCardLayout.Pad + 54f, _countScale, TextDim, avail);
            return;
        }

        var scroll = layout.ClampScroll(view.CraftScrollOffset);
        DrawRect(layout.CardCentre, layout.CardSize, PanelBackground);
        DrawBorder(layout.CardCentre, layout.CardSize, SlotBorder, SlotBorderColor);
        DrawRect(new Vector2(layout.CardCentre.X, top + 3f), new Vector2(layout.Width - 4f, 2f), AccentGold);

        if (layout.Overflows)
        {
            // Same idiom as DrawInventoryPanel's row loop: DrawInventoryPanel already ran
            // EnsureDepthCleared before its own list scissor, so the spinning icons' latched
            // depth clear never happens under this scissor. Nothing may return from inside.
            var (sx, sy, sw, sh) = UiCanvas.ScissorRect(
                new Vector2(layout.Left, top + 2f), new Vector2(layout.Width, layout.ViewHeight - 4f),
                _scale, _windowSize, _framebufferSize);
            _gl.Enable(EnableCap.ScissorTest);
            _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
        }
        try
        {
            DrawCraftContent(inventory, view, layout, scroll, time, headingDegrees);
        }
        finally
        {
            if (layout.Overflows) _gl.Disable(EnableCap.ScissorTest);
        }

        if (layout.Overflows)
        {
            var track = layout.TrackRect();
            DrawRect(track.Centre, track.Size, SlotBackground);
            var thumb = layout.ThumbRect(scroll);
            DrawRect(thumb.Centre, thumb.Size, SlotBorderColor);
        }
    }

    /// <summary>The crafting card's content, every Y read from the layout and offset by the scroll.</summary>
    private void DrawCraftContent(Inventory inventory, InventoryView view, CraftCardLayout layout,
        float scroll, float time, float headingDegrees)
    {
        const float pad = CraftCardLayout.Pad;
        const float ingPad = CraftCardLayout.IngPad;
        var session = view.Craft;
        var recipe = layout.Recipe;
        var ready = session.IsReady(inventory);
        var cardCentreX = layout.CardCentre.X;
        var innerWidth = layout.InnerWidth;

        var iconCentre = layout.IconCentre(scroll);
        var spin = time * 0.9f;
        if (ItemRegistry.IsItem(recipe.ResultId))
            DrawItemIcon(iconCentre, 56f, recipe.ResultId, spin);
        else
            DrawBlockIconSpinning(iconCentre, 60f, recipe.ResultId, spin);

        var textX = layout.TextX;
        var titleY = layout.ContentY(layout.TitleY, scroll);
        for (var k = 0; k < layout.TitleLines.Length; k++)
            DrawText(layout.TitleLines[k], new Vector2(textX, titleY + k * CraftCardLayout.TitleLineH),
                _headScale, Rgba.White);
        var makesY = layout.ContentY(layout.MakesY, scroll);
        for (var k = 0; k < layout.MakesLines.Length; k++)
            DrawText(layout.MakesLines[k], new Vector2(textX, makesY + k * CraftCardLayout.BodyLineH),
                _countScale, TextDim);

        var batchTextY = layout.ContentY(layout.BatchTextY, scroll);
        DrawText("Batch", new Vector2(textX, batchTextY), _countScale, TextDim);
        var batchMinus = layout.BatchMinusCentre(scroll);
        var batchPlus = layout.BatchPlusCentre(scroll);
        DrawBoxButton(batchMinus, CraftCardLayout.StepBox, "-", ChipBackground, SlotBorderColor, Rgba.White);
        DrawTextCentred(session.Batch.ToString(), layout.BatchCountCentre(scroll).X,
            batchMinus.Y - 8f, _countScale, Rgba.White);
        DrawBoxButton(batchPlus, CraftCardLayout.StepBox, "+", ChipBackground, SlotBorderColor, Rgba.White);
        var maxBatch = session.MaxAffordable(inventory);
        var maxX = layout.BatchMaxX(scroll);
        var maxRight = layout.Left + layout.Width - pad;
        DrawText(FitText($"max {maxBatch}", _countScale, Math.Max(20f, maxRight - maxX)),
            new Vector2(maxX, batchTextY),
            _countScale, maxBatch >= session.Batch ? ReadyGreen : DangerRed);

        var separatorY = layout.ContentY(pad + layout.HeaderHeight + CraftCardLayout.HeaderGap - 5f, scroll);
        DrawRect(new Vector2(cardCentreX, separatorY), new Vector2(innerWidth, 1.5f), SlotBorderColor);

        var ingLeft = layout.Left + pad + ingPad;
        for (var i = 0; i < recipe.Ingredients.Length; i++)
        {
            var ing = layout.Ingredients[i];
            var cardTop = layout.ContentY(ing.Top, scroll);
            DrawRect(new Vector2(cardCentreX, cardTop + ing.Height * 0.5f),
                new Vector2(innerWidth, ing.Height), CardBackground);

            var need = session.NeedAt(i);
            var fed = session.FedAt(i);
            var ingTitleY = layout.ContentY(ing.TitleY, scroll);
            var titleLines = layout.IngredientTitleLines[i];
            for (var k = 0; k < titleLines.Length; k++)
                DrawText(titleLines[k], new Vector2(ingLeft, ingTitleY + k * CraftCardLayout.IngTitleLineH),
                    _countScale, Rgba.White);

            var barY = layout.ContentY(ing.BarY, scroll);
            var barW = innerWidth - ingPad * 2f;
            DrawRect(new Vector2(ingLeft + barW * 0.5f, barY + CraftCardLayout.IngBarH * 0.5f),
                new Vector2(barW, CraftCardLayout.IngBarH), SlotBackground);
            var fill = MathUtils.Clamp01(need > 0 ? fed.Count / (float)need : 1f);
            if (fill > 0f)
                DrawRect(new Vector2(ingLeft + barW * fill * 0.5f, barY + CraftCardLayout.IngBarH * 0.5f),
                    new Vector2(barW * fill, CraftCardLayout.IngBarH), fill >= 1f ? ReadyGreen : AccentGold);

            var fedY = layout.ContentY(ing.FedY, scroll);
            var fedCx = ingLeft + 16f;
            var fedCy = fedY + CraftCardLayout.IngFedH * 0.5f;
            if (fed.MemberId != 0)
                DrawIdIcon(new Vector2(fedCx, fedCy), 30f, fed.MemberId, headingDegrees, time);
            else
                DrawTextCentred("?", fedCx, fedCy - 8f, _countScale, TextDim);

            var minus = layout.IngMinusCentre(i, scroll);
            var plus = layout.IngPlusCentre(i, scroll);
            var fedText = $"{fed.Count} / {need} fed";
            var fedAvail = minus.X - CraftCardLayout.IngStepBox * 0.5f - (fedCx + 24f) - 8f;
            DrawText(FitText(fedText, _countScale, Math.Max(20f, fedAvail)),
                new Vector2(fedCx + 24f, fedCy - 8f), _countScale,
                fed.Count >= need ? ReadyGreen : Rgba.White);
            DrawBoxButton(minus, CraftCardLayout.IngStepBox, "-", ChipBackground, SlotBorderColor, Rgba.White);
            DrawBoxButton(plus, CraftCardLayout.IngStepBox, "+", ChipBackground, SlotBorderColor, Rgba.White);

            var members = recipe.Ingredients[i].AcceptedIds;
            var picked = fed.MemberId;
            const float chipSize = CraftCardLayout.ChipSize;
            for (var c = 0; c < ing.MemberCount; c++)
            {
                var chipC = layout.ChipCentre(i, c, scroll);
                var owned = inventory.CountFor(members[c]);
                var isPicked = picked != 0 && picked == members[c];
                DrawRect(chipC, new Vector2(chipSize), owned > 0 ? ChipBackground : SlotBackground);
                DrawBorder(chipC, new Vector2(chipSize), isPicked ? 2.5f : 1.5f,
                    isPicked ? AccentGold : SlotBorderColor);
                DrawIdIcon(chipC with { Y = chipC.Y - 3f }, 26f, members[c], headingDegrees, time);
                var ownedText = owned.ToString();
                if (_font != null)
                {
                    var size = _font.MeasureSize(ownedText.AsSpan(), _countScale);
                    DrawText(ownedText,
                        new Vector2(chipC.X + chipSize * 0.5f - 3f - size.X,
                            chipC.Y + chipSize * 0.5f - 3f - size.Y),
                        _countScale, owned > 0 ? Rgba.White : TextDim);
                }
            }
        }

        var statusY = layout.ContentY(layout.StatusY, scroll);
        var statusColor = ready ? ReadyGreen : AccentGold;
        for (var k = 0; k < layout.StatusLines.Length; k++)
            DrawTextCentred(layout.StatusLines[k], cardCentreX, statusY + k * CraftCardLayout.StatusLineH,
                _countScale, statusColor);

        var buttonC = layout.ButtonCentre(scroll);
        var buttonS = layout.ButtonSize;
        var total = recipe.ResultCount * session.Batch;
        DrawRect(buttonC, buttonS, ready ? ButtonReady : ButtonDisabled);
        DrawBorder(buttonC, buttonS, 2f, ready ? Rgba.White : SlotBorderColor);
        DrawTextCentred(session.Batch > 1 ? $"CRAFT  x{total}" : "CRAFT",
            buttonC.X, buttonC.Y - 9f, _headScale,
            ready ? ButtonTextReady : ButtonTextDisabled);
    }

    /// <summary>
    /// A flat item sprite: the sheet frame the moment picks for an animated item, the whole
    /// texture for a plain one. Falls back to the whole texture when the frame meshes are
    /// missing, so an icon never vanishes over a cache mismatch.
    /// </summary>
    private void DrawItemSprite(Vector2 centre, Vector2 size, byte id, uint texture,
        float headingDegrees, float timeSeconds)
    {
        if (_itemFrames.TryGetValue(id, out var frames)
            && ItemRegistry.TryGet(id) is { } type)
        {
            var frame = Math.Clamp(type.FrameAt(timeSeconds, headingDegrees), 0, frames.Length - 1);
            DrawMesh(frames[frame], RectTransform(centre, size), Rgba.White, texture);
            return;
        }
        DrawTexture(centre, size, texture);
    }

    /// <summary>
    /// A pure item's flat 2D sprite, sized to <paramref name="pixels"/> square and drawn with the
    /// frame the given moment picks (frame 0 for a plain sprite). This is what the hotbar and
    /// inventory rows show; the item debugger uses it for its grid so icons read as sprites.
    /// Air, blocks and unknown ids draw nothing.
    /// </summary>
    public void DrawItemSprite(Vector2 centre, float pixels, byte itemId, float headingDegrees,
        float timeSeconds)
    {
        if (!ItemRegistry.IsItem(itemId) || !_itemIcons.TryGetValue(itemId, out var texture)) return;
        DrawItemSprite(centre, new Vector2(pixels), itemId, texture, headingDegrees, timeSeconds);
    }

    /// <summary>
    /// A pure item as its extruded world-drop sprite, yawed like the block icons. Only the
    /// preview uses this; rows and hotbar draw the flat sprite.
    /// </summary>
    public void DrawItemIcon(Vector2 centre, float size, byte itemId, float yaw)
    {
        if (!_itemMeshes.TryGet(itemId, out var mesh, out var texture)) return;

        _gl.Enable(EnableCap.DepthTest);
        EnsureDepthCleared();

        var rotation = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(IconPitch);
        var model = Matrix4x4.CreateScale(size)
                    * rotation
                    * FlipToScreen(centre);

        // Cut out the sprite's transparent texels so they write neither colour nor depth:
        // without this the front face's invisible pixels depth-swallow the extrusion walls
        // behind them for half of every turn
        _shader.Use();
        _shader.SetUniform(_locAlphaCutoff, 0.5f);
        DrawMesh(mesh, model, Rgba.White, texture, _itemMeshes.IsSrgb);
        _shader.SetUniform(_locAlphaCutoff, 0f);

        _gl.Disable(EnableCap.DepthTest);
    }

    /// <summary>A block cube yawed by <paramref name="yaw"/> instead of the fixed icon angle.</summary>
    public void DrawBlockIconSpinning(Vector2 centre, float size, byte blockId, float yaw)
    {
        var mesh = _blockCubes.Get(blockId);
        if (mesh == null) return;

        _gl.Enable(EnableCap.DepthTest);
        EnsureDepthCleared();

        var rotation = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(IconPitch);
        var model = Matrix4x4.CreateScale(size / IconExtent)
                    * rotation
                    * FlipToScreen(centre);

        // Same cutout discard as DrawBlockIcon: the spinning preview would otherwise
        // rasterize the sprite's transparent texels (and their depth) as black.
        var cutout = BlockType.ById[blockId]?.Cutout == true;
        if (cutout)
        {
            _shader.Use();
            _shader.SetUniform(_locAlphaCutoff, 0.5f);
        }
        DrawMesh(mesh, model, Rgba.White, _atlasTexture, _atlasIsSrgb);
        if (cutout)
            _shader.SetUniform(_locAlphaCutoff, 0f);

        _gl.Disable(EnableCap.DepthTest);
    }

    private void DrawTextCentred(string text, float centreX, float topY, float scale, Rgba color)
    {
        if (_font == null || string.IsNullOrEmpty(text)) return;
        var width = _font.MeasureSize(text.AsSpan(), scale).X;
        DrawText(text, new Vector2(centreX - width * 0.5f, topY), scale, color);
    }

    private float TextWidth(string text, float scale) =>
        _font == null || string.IsNullOrEmpty(text)
            ? 0f
            : _font.MeasureSize(text.AsSpan(), scale).X;

    // Truncates text with "..." to fit maxWidth at scale. ASCII dots because the font
    // only covers printable ASCII (U+2026 would vanish).
    private string FitText(string text, float scale, float maxWidth)
    {
        if (_font == null || string.IsNullOrEmpty(text)) return text;
        if (maxWidth <= 0f) return "";
        if (_font.MeasureSize(text.AsSpan(), scale).X <= maxWidth) return text;
        const string dots = "...";
        for (var len = text.Length - 1; len > 0; len--)
        {
            var sub = text.Substring(0, len) + dots;
            if (_font.MeasureSize(sub.AsSpan(), scale).X <= maxWidth) return sub;
        }
        return dots;
    }

    private float FitScale(string text, float scale, float maxWidth)
    {
        if (_font == null || string.IsNullOrEmpty(text) || maxWidth <= 0f) return scale;
        var w = _font.MeasureSize(text.AsSpan(), scale).X;
        if (w <= maxWidth || w <= 0f) return scale;
        return Math.Max(scale * 0.7f, scale * maxWidth / w);
    }

    private void DrawTextCentredFitted(string text, float centreX, float topY, float scale,
        Rgba color, float maxWidth)
    {
        if (_font == null || string.IsNullOrEmpty(text)) return;
        var s = FitScale(text, scale, maxWidth);
        DrawTextCentred(FitText(text, s, maxWidth), centreX, topY, s, color);
    }

    private float RowNameAvail(string count)
    {
        var nameX = PanelLeft + PanelPad + RowIcon + 16f;
        var countW = TextWidth(count, _countScale);
        return PanelLeft + PanelWidth - PanelPad - 8f - countW - nameX - 8f;
    }

    /// <summary>
    /// Draws any primitive with an explicit model matrix. <paramref name="texture"/> of 0 draws
    /// the mesh as flat colour without sampling. <paramref name="srgb"/> says the texture is
    /// <c>SRGB8_ALPHA8</c> (the shared atlas or an <see cref="ItemMeshCache"/> texture), so the
    /// shader re-encodes the decoded texel; UIRenderer's own sprites are linear and pass false.
    /// </summary>
    public void DrawMesh(Mesh mesh, Matrix4x4 model, Rgba? tint = null, uint texture = 0, bool srgb = false)
    {
        _shader.Use();
        _shader.SetUniform("uModel", model);
        _shader.SetUniform("uColor", (tint ?? Rgba.White).ToVector4());
        _shader.SetUniform("uUseTexture", texture != 0);
        _shader.SetUniform(_locSrgbTex, texture != 0 && srgb);

        if (texture != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2DArray, texture);
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
        _gl.DeleteBuffer(_textVbo);
        _gl.DeleteBuffer(_textEbo);
        _gl.DeleteVertexArray(_textVao);
        _gl.DeleteTexture(_crosshairTexture);
        _gl.DeleteTexture(_heartTexture);
        _gl.DeleteTexture(_heartEmptyTexture);
        _heartEmptyMesh.Dispose();
        _heartFullMesh.Dispose();
        _heartHalfMesh.Dispose();
        foreach (var icon in _itemIcons.Values)
            _gl.DeleteTexture(icon);
        foreach (var frames in _itemFrames.Values)
            foreach (var frame in frames)
                frame.Dispose();

        GC.SuppressFinalize(this);
    }
}
