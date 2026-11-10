using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;
using VoxelEngine.Items;
using VoxelEngine.UI;
using VoxelEngine.UI.Widgets;
using static VoxelEngine.UI.UiTheme;
using VoxelEngine.UI.Menus;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// Draws screen-space UI through an orthographic projection measured in canvas units, with the
/// origin at the top-left corner. The canvas is the window divided by a uniform scale the caller
/// picks (see <see cref="UiCanvas"/>), so every layout constant here is laid out against a
/// 1920x1080-ish reference and scales with the window; at scale 1 canvas units are window pixels.
/// One rule keeps input honest: the page renderers' <c>*At</c> hit-test methods take a <b>window-pixel</b>
/// point and convert it once through <see cref="ToCanvas"/>; everything else (draws, private
/// geometry, <see cref="ScreenSize"/>) works in canvas units. Shapes come from <see cref="Primitives"/> in their own Y-up space and
/// are flipped into that Y-down frame by the model matrix, so the same geometry works here and
/// in the world.
///
/// Flat shapes draw with depth testing off, in call order. <see cref="DrawBlockIcon"/> turns it
/// back on against a cleared depth buffer, which is safe because UI is the last pass of a frame.
///
/// <para>This class owns the UI's GL resources and their creation and disposal order (the ui
/// program, the count font, the text batch buffers, the item-icon textures and frame meshes; the
/// HUD's crosshair and heart sprites are created and deleted through <see cref="HudRenderer"/> at
/// their original points) and the shared draw primitives every page and the item debugger use.
/// The pages are the components it builds and exposes as internal properties -
/// <see cref="HudRenderer"/>, <see cref="TopStripRenderer"/>, <see cref="InventoryPanelRenderer"/>,
/// <see cref="CraftCardRenderer"/>, <see cref="SpellBuilderRenderer"/>, <see cref="WaystoneRenderer"/>, <see cref="MenuRenderer"/>,
/// <see cref="LoadingScreenRenderer"/> and <see cref="ChatRenderer"/> - each drawing through these primitives and reading the per-pass
/// state live. Render thread only.</para>
/// </summary>
public class UIRenderer : IDisposable
{
    // Ortho depth half-range. Only needs to clear a rotated icon cube's half-diagonal, but is
    // kept generous since nothing here depends on depth precision.
    private const float DepthRange = 512f;

    // Hotbar count text, in pixels. Counts sit in the slot's bottom-right corner, sized
    // by digit cap-height so the size survives swapping the font file.
    private const string CountFontPath = "assets/fonts/kongtext.ttf";

    // Durability bar under a tool icon: fraction of the slot width, a few px tall.
    internal const float DurabilityHeight = 5f;
    internal const float DurabilityFill = 0.8f;
    internal const float DurabilityPadBottom = 6f;
    private static readonly Rgba DurabilityBack = Rgba.Gray(0.05f, 0.9f);

    // Three-quarter view: yaw swings a side face toward the viewer, pitch tilts the top down
    private const float IconYaw = 45f * MathF.PI / 180f;
    private const float IconPitch = 30f * MathF.PI / 180f;

    private static readonly Matrix4x4 IconRotation =
        Matrix4x4.CreateRotationY(IconYaw) * Matrix4x4.CreateRotationX(IconPitch);

    // Widest screen-space span of a unit cube under IconRotation. Icons are sized by footprint
    // and divided by this, so retuning the angles above never changes how much room they take.
    private static readonly float IconExtent = UnitCubeExtent(IconRotation);

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _atlasTexture;
    private readonly BlockCubeCache _blockCubes;
    private readonly ItemMeshCache _itemMeshes;

    // Flat sprite textures for the list rows; the hotbar and the preview draw the same ids
    // as extruded 3D meshes through DrawItemIcon instead
    private readonly Dictionary<byte, uint> _itemIcons = new();
    // One unit square per sheet frame, UV'd to that frame's array slice (see UvRect.Tile):
    // the icon texture of a spritesheet item holds one slice per frame, so picking a frame
    // is picking a mesh, and every frame mipmaps without bleeding into its neighbors.
    private readonly Dictionary<byte, Mesh[]> _itemFrames = new();
    private readonly int _locAlphaCutoff;
    private readonly int _locSrgbTex;
    // Whether the shared block atlas is SRGB8_ALPHA8 (the game) or linear (the ItemDebugger);
    // decides whether its icons are re-encoded for the gamma framebuffer.
    private readonly bool _atlasIsSrgb;

    // Unit shapes, scaled per draw so a color or size change costs no buffer work
    private readonly Mesh _unitSquare;

    // Count font, baked once from the TTF. Null when the file is missing or broken, in
    // which case the hotbar simply draws no counts.
    private readonly BitmapFont? _font;
    private readonly float _countScale;
    private readonly float _headScale;

    // Formats the per-frame HUD strings only when their values change; the draws still happen
    // every frame (PERF_TODO R8).
    private readonly UITextCache _textCache = new();

    // Longest run one text batch holds: the glyph scratch, the vertex scratch and the static index
    // buffer are all sized from it (the 16-bit indices allow far more). It is a buffer bound far above
    // any UI string (the longest field is 200 characters), not a layout limit; a longer run is cut here.
    private const int MaxTextGlyphs = 2048;

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

    // Alpha multiplier DrawText applies to both the face and the shadow; the loading overlay, the
    // HUD toast and the hotbar item name lower it and restore 1 afterwards, and Begin resets it to 1.
    private float _textFade = 1f;

    // The fade and slide of the panel being drawn (SetOverlay); Begin resets them to none.
    private Vector2 _overlayOffset;
    private int _locFade = -1;

    // Main-menu and loading-screen string caches, render thread only, so an idle menu allocates
    // nothing per frame: FitText allocates a Substring per trial length, and the seed
    // strings would otherwise be formatted every frame. Fitted strings are keyed by
    // the raw text, scale and width they were fitted to, and cleared when the canvas size changes.
    private const int MaxFittedTexts = 256;
    private readonly Dictionary<(string Text, float Scale, float Width), string> _fittedTexts = new();
    private Vector2 _fitCacheScreen;

    // The pages, built in the constructor; each draws through this renderer's primitives and reads
    // its per-pass state through the accessors below ToCanvas.
    internal ChatRenderer Chat { get; }
    internal LoadingScreenRenderer LoadingScreen { get; }
    internal MenuRenderer Menu { get; }
    internal CraftCardRenderer CraftCard { get; }
    internal EquipmentCardRenderer EquipmentCard { get; }
    internal ChestCardRenderer ChestCard { get; }
    internal InventoryPanelRenderer Panel { get; }
    internal SpellBuilderRenderer SpellBuilder { get; }
    internal WaystoneRenderer Waystone { get; }
    internal ForgeRenderer Forge { get; }
    internal TopStripRenderer TopStrip { get; }
    internal HudRenderer Hud { get; }
    /// <summary>The rarity chrome over list rows and the hover tooltip.</summary>
    internal RarityChromeRenderer Chrome { get; }

    /// <param name="gl">The GL context every UI resource is created on.</param>
    /// <param name="atlasTexture">The block atlas array texture the block icons sample.</param>
    /// <param name="blockCubes">Cached cube meshes the block icons draw.</param>
    /// <param name="itemMeshes">Cached item meshes the item icons draw.</param>
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

        // The HUD loads its crosshair and heart textures and meshes here, exactly where they always loaded.
        Hud = new HudRenderer(gl, this);
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
        _locFade = _shader.GetUniformLocation("uFade");
        _unitSquare = Primitives.CreateSquare(gl, 1f);

        try
        {
            _font = new BitmapFont(gl, CountFontPath);
            _countScale = _font.ScaleForCapHeight(TextSize.Body);
            _headScale = _font.ScaleForCapHeight(TextSize.Head);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Log.WriteLine($"Warning: count font '{CountFontPath}' not loaded ({e.Message}); hotbar counts hidden.");
        }

        Chrome = new RarityChromeRenderer(this);
        CraftCard = new CraftCardRenderer(gl, this);
        EquipmentCard = new EquipmentCardRenderer(this);
        ChestCard = new ChestCardRenderer(gl, this);

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

        Chat = new ChatRenderer(this);
        LoadingScreen = new LoadingScreenRenderer(this);
        Menu = new MenuRenderer(gl, this);
        Panel = new InventoryPanelRenderer(gl, this);
        SpellBuilder = new SpellBuilderRenderer(this);
        Waystone = new WaystoneRenderer(gl, this);
        Forge = new ForgeRenderer(this);
        TopStrip = new TopStripRenderer(gl, this);
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
        _overlayOffset = Vector2.Zero;
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
        _shader.SetUniform(_locFade, 1f);
    }

    /// <summary>
    /// Draws everything until <see cref="ClearOverlay"/> shifted by <paramref name="offset"/> canvas
    /// units and with its alpha multiplied by <paramref name="alpha"/> (a panel's open and close
    /// easing). Scissor rects must add <see cref="OverlayOffset"/> themselves.
    /// </summary>
    internal void SetOverlay(Vector2 offset, float alpha)
    {
        _overlayOffset = offset;
        _shader.Use();
        _shader.SetUniform("uProjection", Matrix4x4.CreateTranslation(offset.X, offset.Y, 0f) * _projection);
        _shader.SetUniform(_locFade, alpha);
    }

    /// <summary>Ends <see cref="SetOverlay"/>: no shift, full alpha.</summary>
    internal void ClearOverlay() => SetOverlay(Vector2.Zero, 1f);

    /// <summary>Canvas shift of the overlay being drawn, zero outside one.</summary>
    internal Vector2 OverlayOffset => _overlayOffset;

    public void End()
    {
        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.DepthTest);
    }

    public Vector2 ScreenCenter => _screenSize * 0.5f;

    /// <summary>The canvas size of the current (or last) pass, in canvas units.</summary>
    public Vector2 ScreenSize => _screenSize;

    /// <summary>Window pixels per canvas unit of the current (or last) pass.</summary>
    public float Scale => _scale;

    /// <summary>Window pixels -> the canvas every layout constant is measured in; valid after the
    /// first <see cref="Begin(Vector2D{int}, Vector2D{int}, float)"/>.</summary>
    public Vector2 ToCanvas(Vector2 windowPoint) => UiCanvas.WindowToCanvas(windowPoint, _scale);

    /// <summary>The pointer in canvas units, set once per frame before the UI pass (null with no pointer); what
    /// <see cref="IsHovered"/> tests, so every button lights up the same way.</summary>
    internal Vector2? Pointer { get; set; }

    /// <summary>Whether the pointer is over the box of this center and size.</summary>
    internal bool IsHovered(Vector2 center, Vector2 size) => Pointer is { } p && new UiRect(center, size).Contains(p);

    // The per-pass state and shared resources the page renderers draw with. They read it live,
    // never as copies: Begin rewrites the canvas and the text fade every pass.
    internal float CountScale => _countScale;
    internal float HeadScale => _headScale;
    internal Vector2 WindowSize => _windowSize;
    internal Vector2 FramebufferSize => _framebufferSize;
    internal float TextFade
    {
        get => _textFade;
        set => _textFade = value;
    }
    internal UITextCache TextCache => _textCache;
    internal Shader UiShader => _shader;
    internal int AlphaCutoffLocation => _locAlphaCutoff;
    internal uint AtlasTexture => _atlasTexture;
    internal bool AtlasIsSrgb => _atlasIsSrgb;
    internal BlockCubeCache BlockCubes => _blockCubes;
    internal Mesh UnitSquare => _unitSquare;

    /// <summary>Solid-color rectangle centered on <paramref name="center"/>.</summary>
    public void DrawRect(Vector2 center, Vector2 size, Rgba color) =>
        DrawMesh(_unitSquare, RectTransform(center, size), color);

    public void DrawSquare(Vector2 center, float size, Rgba color) =>
        DrawRect(center, new Vector2(size), color);

    /// <summary>
    /// Textured rectangle. The whole texture is stretched to fit; for a region of an atlas,
    /// build a square with a <see cref="UvRect"/> via <see cref="Primitives"/> and draw it
    /// through <see cref="DrawMesh"/>.
    /// </summary>
    public void DrawTexture(Vector2 center, Vector2 size, uint texture, Rgba? tint = null) =>
        DrawMesh(_unitSquare, RectTransform(center, size), tint, texture);

    /// <summary>An outline drawn as four rectangles just inside the given bounds.</summary>
    /// <summary>
    /// The frame every panel and card shares (<see cref="PanelFrameLayout"/>): the fill, a border just inside
    /// the outer rectangle and the thin gold accent bar under its top edge.
    /// </summary>
    internal void DrawPanelFrame(UiRect outer, Rgba fill)
    {
        var frame = PanelFrameLayout.Standard(outer);
        DrawRect(outer.Center, outer.Size, fill);
        DrawBorder(outer.Center, outer.Size, frame.BorderWidth, SlotBorderColor);
        DrawRect(frame.AccentBar.Center, frame.AccentBar.Size, AccentGold);
    }

    public void DrawBorder(Vector2 center, Vector2 size, float thickness, Rgba color)
    {
        var half = size * 0.5f;
        var horizontal = new Vector2(size.X, thickness);
        var vertical = new Vector2(thickness, size.Y - thickness * 2f);
        var inset = thickness * 0.5f;

        DrawRect(center with { Y = center.Y - half.Y + inset }, horizontal, color);
        DrawRect(center with { Y = center.Y + half.Y - inset }, horizontal, color);
        DrawRect(center with { X = center.X - half.X + inset }, vertical, color);
        DrawRect(center with { X = center.X + half.X - inset }, vertical, color);
    }

    internal void DrawDurabilityBar(Vector2 center, float width, float fraction)
    {
        var f = MathUtils.Clamp01(fraction);
        DrawRect(center, new Vector2(width, DurabilityHeight), DurabilityBack);
        if (f <= 0f) return;
        var fill = new Rgba((byte)(230 - 104 * f), (byte)(110 + 107 * f), (byte)(110 - 23 * f));
        DrawRect(new Vector2(center.X - width * 0.5f + width * f * 0.5f, center.Y),
            new Vector2(width * f, DurabilityHeight), fill);
    }

    /// <summary>
    /// Clears the depth buffer once per UI pass, the first time a 3D icon needs it. A glClear
    /// honors the scissor, so this must never first run while the list scissor is on — the
    /// inventory panel calls it before enabling the scissor for exactly that reason.
    /// </summary>
    internal void EnsureDepthCleared()
    {
        if (_depthCleared) return;
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _depthCleared = true;
    }

    /// <summary>
    /// Clears the depth buffer again, so the next 3D icon draws over every icon already drawn this pass: icons never
    /// overlap except one riding the pointer (a dragged row). Never while a scissor is on, like the first clear.
    /// </summary>
    internal void ClearDepthForOverlay()
    {
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _depthCleared = true;
    }

    /// <summary>
    /// A block drawn as a rotated 3D cube. Air (id 0) and unknown ids draw nothing.
    /// </summary>
    /// <param name="center">Center of the icon, in canvas pixels.</param>
    /// <param name="blockId">The block to draw.</param>
    /// <param name="size">
    /// Width and height of the box the icon fits in, in pixels — not the cube's edge length,
    /// which is derived from the viewing angle.
    /// </param>
    public void DrawBlockIcon(Vector2 center, float size, byte blockId)
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
                    * FlipToScreen(center);

        // Cutout blocks (flower, leaves) carry transparent texels: discard them so they
        // draw neither color nor depth, exactly like the world opaque pass and DrawItemIcon.
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

    /// <summary>
    /// Screen-space text with its top-left corner at <paramref name="topLeft"/> (pixels, Y
    /// down). A one-pixel dark shadow is drawn under each glyph so the text reads over
    /// bright icons.
    ///
    /// Every glyph of the run — shadow copies and face copies alike — is written into one
    /// dynamic vertex buffer and drawn with a single <c>GL.DrawElements</c> call
    /// (<see cref="DrawTextBatch"/>), instead of the two draws per glyph a naive per-glyph
    /// <see cref="DrawMesh"/> would cost. Shadows are laid out before faces in the index
    /// order, so a glyph's face still draws over every shadow, exactly as separate draw
    /// calls in that order would.
    /// </summary>
    public void DrawText(string text, Vector2 topLeft, float scale, Rgba color) =>
        DrawText(text.AsSpan(), topLeft, scale, color);

    /// <summary>The same text draw over a slice of a string, so a run of a line (a chat link) draws without a
    /// substring.</summary>
    public void DrawText(ReadOnlySpan<char> text, Vector2 topLeft, float scale, Rgba color)
    {
        if (_font == null || text.IsEmpty)
            return;
        DrawGlyphs(_font.Layout(text, topLeft, scale, _glyphScratch), color);
    }

    /// <summary>
    /// One-line text starting at <paramref name="left"/> with the digit cap box centered on
    /// <paramref name="centerY"/>: the shared vertical-centering rule for labels in a box, independent
    /// of the glyphs in the string (see <see cref="BitmapFont.LayoutCentered"/>).
    /// </summary>
    public void DrawTextCenteredY(string text, float left, float centerY, float scale, Rgba color)
    {
        if (_font == null || string.IsNullOrEmpty(text))
            return;
        DrawGlyphs(_font.LayoutCentered(text.AsSpan(), left, centerY, scale, _glyphScratch), color);
    }

    /// <summary>
    /// One-line text centered vertically on <paramref name="centerY"/> from <paramref name="left"/>, clipped to
    /// <paramref name="clip"/>. A text that fits its clip draws as <see cref="DrawTextCenteredY"/>; a wider one
    /// scrolls back and forth inside the clip (see <see cref="TextMarquee"/>) so no part of it is ever cut off.
    /// </summary>
    public void DrawTextMarqueeY(string text, UiRect clip, float left, float centerY, float scale, Rgba color,
        float time)
    {
        if (_font == null || string.IsNullOrEmpty(text))
            return;
        var width = MeasureTextWidth(text, scale);
        if (width <= clip.Size.X)
        {
            DrawTextCenteredY(text, left, centerY, scale, color);
            return;
        }

        // The pass's one full-screen depth clear must happen before the scissor goes on (a clear honors it)
        EnsureDepthCleared();
        var shift = TextMarquee.Offset(width, clip.Size.X, time);
        var (sx, sy, sw, sh) = UiCanvas.ScissorRect(new Vector2(clip.Left, clip.Top) + _overlayOffset, clip.Size,
            _scale, _windowSize, _framebufferSize);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(sx, sy, (uint)sw, (uint)sh);
        try
        {
            DrawTextCenteredY(text, left - shift, centerY, scale, color);
        }
        finally
        {
            _gl.Disable(EnableCap.ScissorTest);
        }
    }

    // Writes the laid-out glyphs in _glyphScratch as shadow quads then face quads and draws them.
    private void DrawGlyphs(int n, Rgba color)
    {
        if (n == 0)
            return;
        var instances = _glyphScratch.AsSpan();

        // _textFade is 1 outside the fading draws, so ordinary text keeps its exact colors.
        var shadow = _textFade >= 1f ? TextShadow : Faded(TextShadow, _textFade);
        var face = _textFade >= 1f ? color : Faded(color, _textFade);
        for (var i = 0; i < n; i++)
            WriteGlyphQuad(_textVerts.AsSpan(i * 4, 4), instances[i],
                instances[i].Center + new Vector2(1f, 1f), shadow);
        for (var i = 0; i < n; i++)
            WriteGlyphQuad(_textVerts.AsSpan((n + i) * 4, 4), instances[i],
                instances[i].Center, face);

        DrawTextBatch(n * 2);
    }

    /// <summary>The HUD's baked font, shared with the world-space text (floating damage numbers);
    /// null when the font file failed to load.</summary>
    public BitmapFont? Font => _font;

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
    /// <paramref name="instance"/>.Size at <paramref name="center"/> — this is that
    /// transform's closed form, which is why the local bottom corner lands at
    /// <c>center.Y + half.Y</c> (the rect transform scales Y negatively).
    /// <paramref name="color"/> rides per-vertex instead of arriving as a per-draw uniform.
    /// Corner order matches the static text EBO (see <see cref="BuildQuadIndices"/>).
    /// </summary>
    internal static void WriteGlyphQuad(Span<Vertex> quad, GlyphInstance instance, Vector2 center,
        Rgba color)
    {
        Span<Vector2> corners = stackalloc Vector2[4];
        instance.Uv.CornersTo(corners);
        var half = instance.Size * 0.5f;

        quad[0] = new Vertex(new Vector3(center.X - half.X, center.Y + half.Y, 0f),
            color.R, color.G, color.B, color.A, corners[0]);
        quad[1] = new Vertex(new Vector3(center.X - half.X, center.Y - half.Y, 0f),
            color.R, color.G, color.B, color.A, corners[1]);
        quad[2] = new Vertex(new Vector3(center.X + half.X, center.Y - half.Y, 0f),
            color.R, color.G, color.B, color.A, corners[2]);
        quad[3] = new Vertex(new Vector3(center.X + half.X, center.Y + half.Y, 0f),
            color.R, color.G, color.B, color.A, corners[3]);
    }

    /// <summary>
    /// Uploads the first <paramref name="quadCount"/> quads of <see cref="_textVerts"/> and
    /// draws them in one call. Color rides per-vertex (see <see cref="WriteGlyphQuad"/>), so
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

    /// <summary>
    /// A one-line text field: the text left-aligned, the placeholder dimmed while it is empty,
    /// and while focused a caret after the text blinking twice a second on the window clock.
    /// Shared by the main menu and the inventory search field; callers size the text to the
    /// box's length cap.
    /// </summary>
    public void DrawInputField(Vector2 center, Vector2 size, TextFieldState field, bool focused,
        string placeholder, float time)
    {
        var box = new TextBoxLayout(new UiRect(center, size));
        DrawRect(center, size, SlotBackground);
        DrawBorder(center, size, Border.Standard, focused ? SelectedBorderColor : SlotBorderColor);

        if (field.IsEmpty)
        {
            DrawTextCenteredY(placeholder, box.TextLeft, box.CenterY, _countScale, TextDim);
            if (focused && ((int)(time * 2f)) % 2 == 0)
            {
                var emptyCaret = box.Caret(0f);
                DrawRect(emptyCaret.Center, emptyCaret.Size, Rgba.White);
            }
            return;
        }

        // A field wider than its box scrolls to keep the caret in view; the visible slice is
        // what fits after the view start.
        var text = field.Text;
        var start = KeepCaretVisible(field, box.TextBudget);
        var end = text.Length;
        while (end > start + 1 && TextWidth(text[start..end], _countScale) > box.TextBudget) end--;
        var shown = text[start..end];

        if (focused && field.HasSelection)
        {
            var from = Math.Clamp(field.SelectionStart, start, end);
            var to = Math.Clamp(field.SelectionEnd, start, end);
            if (to > from)
            {
                var x0 = box.TextLeft + TextWidth(text[start..from], _countScale);
                var x1 = box.TextLeft + TextWidth(text[start..to], _countScale);
                DrawRect(new Vector2((x0 + x1) * 0.5f, box.CenterY), new Vector2(x1 - x0, TextBoxLayout.CaretHeight),
                    Faded(SelectedBorderColor, 0.55f));
            }
        }
        DrawTextCenteredY(shown, box.TextLeft, box.CenterY, _countScale, Rgba.White);

        if (focused && ((int)(time * 2f)) % 2 == 0)
        {
            var caretEnd = Math.Clamp(field.Caret, start, end);
            var caret = box.Caret(TextWidth(text[start..caretEnd], _countScale));
            DrawRect(caret.Center, caret.Size, Rgba.White);
        }
    }

    /// <summary>
    /// Moves <see cref="TextFieldState.ViewStart"/> the least that keeps the caret inside
    /// <paramref name="budget"/> and shows as much text as fits after a delete; returns it.
    /// </summary>
    private int KeepCaretVisible(TextFieldState field, float budget)
    {
        var text = field.Text;
        var start = Math.Clamp(field.ViewStart, 0, field.Caret);
        while (start < field.Caret && TextWidth(text[start..field.Caret], _countScale) > budget) start++;
        while (start > 0 && TextWidth(text[(start - 1)..], _countScale) <= budget) start--;
        field.ViewStart = start;
        return start;
    }

    /// <summary>
    /// The character boundary of <paramref name="field"/> nearest to the canvas x
    /// <paramref name="canvasX"/> when drawn in the box at <paramref name="center"/>/<paramref name="size"/>
    /// (the mouse's caret placement).
    /// </summary>
    public int CaretIndexAt(Vector2 center, Vector2 size, TextFieldState field, float canvasX)
    {
        var local = canvasX - new TextBoxLayout(new UiRect(center, size)).TextLeft;
        var text = field.Text;
        var start = Math.Clamp(field.ViewStart, 0, text.Length);
        var best = start;
        var bestDistance = float.MaxValue;
        for (var i = start; i <= text.Length; i++)
        {
            var distance = MathF.Abs(TextWidth(text[start..i], _countScale) - local);
            if (distance >= bestDistance) break;
            best = i;
            bestDistance = distance;
        }
        return best;
    }

    // A color with its alpha multiplied by factor (clamped to 0..1).
    internal static Rgba Faded(Rgba color, float factor) =>
        color with { A = (byte)(color.A * MathUtils.Clamp01(factor) + 0.5f) };

    // FitText through the cache: each (text, width) pair is fitted once per canvas size.
    internal string FittedText(string text, float scale, float maxWidth)
    {
        if (_fittedTexts.TryGetValue((text, scale, maxWidth), out var fitted)) return fitted;
        if (_fittedTexts.Count >= MaxFittedTexts) _fittedTexts.Clear();
        fitted = FitText(text, scale, maxWidth);
        _fittedTexts[(text, scale, maxWidth)] = fitted;
        return fitted;
    }

    internal void DrawIdIcon(Vector2 center, float size, byte id, float headingDegrees, float timeSeconds)
    {
        if (ItemRegistry.IsItem(id) && _itemIcons.TryGetValue(id, out var icon))
            DrawItemSprite(center, new Vector2(size), id, icon, headingDegrees, timeSeconds);
        else
            DrawBlockIcon(center, size, id);
    }

    /// <summary>
    /// A flat item sprite: the sheet frame the moment picks for an animated item, the whole
    /// texture for a plain one. Falls back to the whole texture when the frame meshes are
    /// missing, so an icon never vanishes over a cache mismatch.
    /// </summary>
    internal void DrawItemSprite(Vector2 center, Vector2 size, byte id, uint texture,
        float headingDegrees, float timeSeconds)
    {
        if (_itemFrames.TryGetValue(id, out var frames)
            && ItemRegistry.TryGet(id) is { } type)
        {
            var frame = Math.Clamp(type.FrameAt(timeSeconds, headingDegrees), 0, frames.Length - 1);
            DrawMesh(frames[frame], RectTransform(center, size), Rgba.White, texture);
            return;
        }
        DrawTexture(center, size, texture);
    }

    /// <summary>
    /// A pure item's flat 2D sprite, sized to <paramref name="pixels"/> square and drawn with the
    /// frame the given moment picks (frame 0 for a plain sprite). This is what the hotbar and
    /// inventory rows show; the item debugger uses it for its grid so icons read as sprites.
    /// Air, blocks and unknown ids draw nothing.
    /// </summary>
    public void DrawItemSprite(Vector2 center, float pixels, byte itemId, float headingDegrees,
        float timeSeconds)
    {
        if (!ItemRegistry.IsItem(itemId) || !_itemIcons.TryGetValue(itemId, out var texture)) return;
        DrawItemSprite(center, new Vector2(pixels), itemId, texture, headingDegrees, timeSeconds);
    }

    /// <summary>
    /// A pure item as its extruded world-drop sprite, yawed like the block icons. Only the
    /// preview uses this; rows and hotbar draw the flat sprite.
    /// </summary>
    public void DrawItemIcon(Vector2 center, float size, byte itemId, float yaw)
    {
        if (!_itemMeshes.TryGet(itemId, out var mesh, out var texture)) return;

        _gl.Enable(EnableCap.DepthTest);
        EnsureDepthCleared();

        var rotation = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(IconPitch);
        var model = Matrix4x4.CreateScale(size)
                    * rotation
                    * FlipToScreen(center);

        // Cut out the sprite's transparent texels so they write neither color nor depth:
        // without this the front face's invisible pixels depth-swallow the extrusion walls
        // behind them for half of every turn
        _shader.Use();
        _shader.SetUniform(_locAlphaCutoff, 0.5f);
        DrawMesh(mesh, model, Rgba.White, texture, _itemMeshes.IsSrgb);
        _shader.SetUniform(_locAlphaCutoff, 0f);

        _gl.Disable(EnableCap.DepthTest);
    }

    /// <summary>A block cube yawed by <paramref name="yaw"/> instead of the fixed icon angle.</summary>
    public void DrawBlockIconSpinning(Vector2 center, float size, byte blockId, float yaw)
    {
        var mesh = _blockCubes.Get(blockId);
        if (mesh == null) return;

        _gl.Enable(EnableCap.DepthTest);
        EnsureDepthCleared();

        var rotation = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(IconPitch);
        var model = Matrix4x4.CreateScale(size / IconExtent)
                    * rotation
                    * FlipToScreen(center);

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

    internal void DrawTextCentered(string text, float centerX, float topY, float scale, Rgba color)
    {
        if (_font == null || string.IsNullOrEmpty(text)) return;
        var width = _font.MeasureSize(text.AsSpan(), scale).X;
        DrawText(text, new Vector2(centerX - width * 0.5f, topY), scale, color);
    }

    internal float TextWidth(string text, float scale) =>
        _font == null || string.IsNullOrEmpty(text)
            ? 0f
            : _font.MeasureSize(text.AsSpan(), scale).X;

    // Cuts text with an ellipsis to fit maxWidth at scale (no shrinking: see TextFit.SingleLine).
    internal string FitText(string text, float scale, float maxWidth) =>
        _font == null ? text : TextFit.SingleLine(text, scale, maxWidth, Measure, minScaleShare: 1f).Text;

    internal void DrawTextCenteredFitted(string text, float centerX, float topY, float scale,
        Rgba color, float maxWidth)
    {
        if (_font == null || string.IsNullOrEmpty(text)) return;
        var fitted = TextFit.SingleLine(text, scale, maxWidth, Measure);
        DrawTextCentered(fitted.Text, centerX, topY, fitted.Scale, color);
    }

    // The measure TextFit runs against: the baked font at a given draw scale.
    private float MeasureAt(ReadOnlySpan<char> text, float scale) => _font!.MeasureSize(text, scale).X;
    private TextFit.ScaledMeasure? _measure;
    private TextFit.ScaledMeasure Measure => _measure ??= MeasureAt;

    /// <summary>One-line text centered on <paramref name="center"/> both ways (the cap box vertically,
    /// see <see cref="DrawTextCenteredY"/>).</summary>
    internal void DrawTextMiddle(string text, Vector2 center, float scale, Rgba color)
    {
        if (_font == null || string.IsNullOrEmpty(text)) return;
        DrawTextCenteredY(text, center.X - _font.MeasureSize(text.AsSpan(), scale).X * 0.5f, center.Y, scale, color);
    }

    /// <summary>
    /// Draws any primitive with an explicit model matrix. <paramref name="texture"/> of 0 draws
    /// the mesh as flat color without sampling. <paramref name="srgb"/> says the texture is
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
    internal static Matrix4x4 RectTransform(Vector2 center, Vector2 size) =>
        Matrix4x4.CreateScale(size.X, -size.Y, 1f)
        * Matrix4x4.CreateTranslation(center.X, center.Y, 0f);

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

    internal static Matrix4x4 FlipToScreen(Vector2 center) =>
        Matrix4x4.CreateScale(1f, -1f, 1f)
        * Matrix4x4.CreateTranslation(center.X, center.Y, 0f);

    public void Dispose()
    {
        // _blockCubes is owned by RenderManager and shared with the world passes
        _font?.Dispose();
        _unitSquare.Dispose();
        _shader.Dispose();
        _gl.DeleteBuffer(_textVbo);
        _gl.DeleteBuffer(_textEbo);
        _gl.DeleteVertexArray(_textVao);
        Hud.Dispose();
        foreach (var icon in _itemIcons.Values)
            _gl.DeleteTexture(icon);
        foreach (var frames in _itemFrames.Values)
            foreach (var frame in frames)
                frame.Dispose();

        GC.SuppressFinalize(this);
    }
}
