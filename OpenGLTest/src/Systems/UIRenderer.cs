using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace silkgltriangle;

/// <summary>What part of the crafting detail card a click landed on.</summary>
public enum CraftHitKind
{
    None,
    BatchMinus,
    BatchPlus,
    CraftButton,
    IngMinus,
    IngPlus,
    MemberChip,
}

/// <summary>One hit-test result inside the crafting detail card.</summary>
public readonly record struct CraftHit(CraftHitKind Kind, int IngredientIndex = -1, byte MemberId = 0);

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
    private const float PanelGap = 16f;
    private const float PanelRightMargin = 24f;
    private const float PanelWidthMin = 480f;
    private const float PanelWidthMax = 660f;
    private const float DetailWidthMin = 300f;
    private const float DetailWidthMax = 480f;
    private float PanelWidth =>
        Math.Clamp((_screenSize.X - PanelLeft - PanelGap - PanelRightMargin) * 0.58f,
            PanelWidthMin, PanelWidthMax);
    private float DetailWidth =>
        Math.Clamp(_screenSize.X - PanelLeft - PanelWidth - PanelGap - PanelRightMargin,
            DetailWidthMin, DetailWidthMax);
    private const float PanelPad = 10f;
    private const float TabHeight = 60f;
    private const float RowHeight = 42f;
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

    // Crafting detail card, in pixels: floats right where the preview sits
    private const float DetailPad = 14f;
    private const float DetailHeaderH = 84f;
    private const float StepBox = 24f;
    private const float BatchCountW = 34f;
    private const float BatchGap = 8f;
    private const float IngPad = 10f;
    private const float IngTitleH = 20f;
    private const float IngBarH = 6f;
    private const float IngFedH = 40f;
    private const float IngStepBox = 26f;
    private const float ChipSize = 36f;
    private const float ChipGap = 6f;
    private const float CraftButtonH = 46f;
    private const float StatusH = 22f;

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

    // Unit shapes, scaled per draw so a colour or size change costs no buffer work
    private readonly Mesh _unitSquare;

    // Count font, baked once from the TTF. Null when the file is missing or broken, in
    // which case the hotbar simply draws no counts.
    private readonly BitmapFont? _font;
    private readonly float _countScale;
    private readonly float _headScale;

    // Formats the per-frame HUD strings only when their values change; the draws still happen
    // every frame (PERF_TODO R8).
    private readonly UITextCache _textCache = new();

    // Longest run DrawText draws. BitmapFont.Layout clamps its output to the scratch span it
    // is handed, so this single constant bounds all three text buffers at once — the glyph
    // scratch, the vertex scratch and the static index buffer — and a longer string is
    // truncated by layout rather than overrunning any of them.
    private const int MaxTextGlyphs = 64;

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

        // Cutout blocks (flower, leaves) carry transparent texels: discard them so they
        // draw neither colour nor depth, exactly like the world opaque pass and DrawItemIcon.
        // Without this the invisible pixels depth-swallow geometry behind them.
        var cutout = BlockType.ById[blockId]?.Cutout == true;
        if (cutout)
        {
            _shader.Use();
            _shader.SetUniform(_locAlphaCutoff, 0.5f);
        }
        DrawMesh(mesh, model, Rgba.White, _atlasTexture);
        if (cutout)
            _shader.SetUniform(_locAlphaCutoff, 0f);

        _gl.Disable(EnableCap.DepthTest);
    }

    public void DrawCrosshair() =>
        DrawTexture(ScreenCentre, new Vector2(CrosshairSize), _crosshairTexture);

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

        for (var i = 0; i < n; i++)
            WriteGlyphQuad(_textVerts.AsSpan(i * 4, 4), instances[i],
                instances[i].Centre + new Vector2(1f, 1f), CountShadow);
        for (var i = 0; i < n; i++)
            WriteGlyphQuad(_textVerts.AsSpan((n + i) * 4, 4), instances[i],
                instances[i].Centre, color);

        DrawTextBatch(n * 2);
    }

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
    /// Sets the same three uniforms <see cref="DrawMesh"/> does and no others, so
    /// <c>uAlphaCutoff</c> keeps the sticky value it had before.
    /// </summary>
    private void DrawTextBatch(int quadCount)
    {
        _shader.Use();
        _shader.SetUniform("uModel", Matrix4x4.Identity);
        _shader.SetUniform("uColor", Rgba.White.ToVector4());
        _shader.SetUniform("uUseTexture", true);

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
        float headingDegrees)
    {
        var top = PanelTop;
        var bottom = _screenSize.Y - BottomMargin - (SlotSize + 14f);
        var height = Math.Max(RowHeight * 2f, bottom - top);
        var panel = new Vector2(PanelLeft + PanelWidth * 0.5f, top + height * 0.5f);
        DrawRect(panel, new Vector2(PanelWidth, height), PanelBackground);

        var tabs = new[] { InventoryCategory.Blocks, InventoryCategory.Tools, InventoryCategory.Misc, InventoryCategory.Crafting };
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
            DrawTextCentredFitted(TabTitle(tabs[t]),
                centre.X, top + PanelPad + 6f, _countScale, Rgba.White, tabWidth - 8f);
            DrawTextCentredFitted(TabSubtitle(inventory, tabs[t]),
                centre.X, top + PanelPad + 30f, _countScale, Rgba.White, tabWidth - 8f);
        }

        var listTop = top + PanelPad * 2f + TabHeight;
        if (view.Tab == InventoryCategory.Tools)
        {
            var tools = inventory.ToolEntries();
            for (var i = 0; i < tools.Count; i++)
            {
                var selected = view.SelectedToolInstance == tools[i].InstanceId;
                DrawEntryRow(i, listTop, tools[i].ItemId, tools[i].Name, "1", selected, false, headingDegrees, time,
                    null, inventory.ToolDurabilityFraction(tools[i].InstanceId));
            }
        }
        else if (view.Tab == InventoryCategory.Crafting)
        {
            var recipes = ContentRegistry.CraftingRecipes;
            for (var i = 0; i < recipes.Count; i++)
            {
                var ready = inventory.CanCraft(recipes[i]);
                DrawEntryRow(i, listTop, recipes[i].ResultId,
                    Inventory.DisplayName(recipes[i].ResultId),
                    $"x{recipes[i].ResultCount}",
                    view.Craft.Recipe != null && view.Craft.Recipe.Name == recipes[i].Name,
                    !ready, headingDegrees, time,
                    ready ? ReadyGreen : null);
            }
        }
        else
        {
            var rows = inventory.StackEntries(view.Tab);
            for (var i = 0; i < rows.Count; i++)
                DrawEntryRow(i, listTop, rows[i].Id, rows[i].Name, rows[i].Count.ToString(),
                    view.SelectedId == rows[i].Id, false, headingDegrees, time);
        }

        DrawPreview(inventory, view, time, headingDegrees);
    }

    /// <summary>
    /// The ESC pause menu over the still-rendered game: a fullscreen dim standing in for a
    /// blur (this pass has no blur target; the dark translucency keeps the scene visible
    /// but unreadable, like the Godot reference's BlurOverlay in overlay mode), then a
    /// centred title with RESUME / SETTINGS / EXIT buttons, or the settings title with a
    /// BACK button. Click targets come from <see cref="PauseMenuLayout"/> so drawing and
    /// hit-testing can never drift apart.
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
            DrawMenuButton(PauseMenuLayout.MainButtonCentre(screen, 2), "EXIT");
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
                case OptionsRow o:
                    DrawTextCentred($"< {SettingsRows.FormatValue(row)} >",
                        SettingsLayout.TrackRect(r, screen).Centre.X,
                        centre.Y - 8f, _countScale, Rgba.White);
                    break;
            }
        }

        DrawMenuButton(SettingsLayout.BackCentre(screen), "BACK");
    }

    /// <summary>Settings widget under a screen point (pixels, top-left origin), if any.</summary>
    public SettingsHit SettingsHitAt(Vector2 point, SettingsMenuView view) =>
        SettingsLayout.HitAt(point, view.Tab, _screenSize);

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

    /// <summary>Menu button or BACK under a screen point (pixels, top-left origin), if any.</summary>
    public PauseMenuHit PauseMenuHitAt(Vector2 point, PauseMenuView view) =>
        PauseMenuLayout.HitAt(point, _screenSize, view.Page);

    private void DrawMenuButton(Vector2 centre, string label)
    {
        DrawRect(centre, PauseMenuLayout.ButtonSize, SlotBackground);
        DrawBorder(centre, PauseMenuLayout.ButtonSize, SlotBorder, SelectedBorderColor);
        DrawTextCentred(label, centre.X, centre.Y - 9f, _countScale, Rgba.White);
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
        var tabs = new[] { InventoryCategory.Blocks, InventoryCategory.Tools, InventoryCategory.Misc, InventoryCategory.Crafting };
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
        InventoryCategory.Crafting => "Craft",
        _ => "Misc",
    };

    private static string TabSubtitle(Inventory inventory, InventoryCategory tab) =>
        tab == InventoryCategory.Crafting
            ? $"{inventory.Craftable().Count}/{ContentRegistry.CraftingRecipes.Count}"
            : $"{inventory.UsedOf(tab)}/{Inventory.CapacityOf(tab)}";

    private void DrawEntryRow(int row, float listTop, byte id, string name, string count, bool selected,
        bool dim, float headingDegrees, float timeSeconds, Rgba? accent = null, float? durability = null)
    {
        var centreY = listTop + row * RowHeight + RowHeight * 0.5f;
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

    private float CraftInnerWidth => DetailWidth - DetailPad * 2f;

    private int CraftChipsPerRow =>
        Math.Max(1, (int)((CraftInnerWidth - IngPad * 2f + ChipGap) / (ChipSize + ChipGap)));

    private static float CraftIngCardHeight(int memberCount, int chipsPerRow)
    {
        var rows = Math.Max(1, (memberCount + chipsPerRow - 1) / chipsPerRow);
        return IngPad + IngTitleH + IngBarH + 6f + IngFedH + 6f
            + rows * (ChipSize + ChipGap) - ChipGap + IngPad;
    }

    private float CraftCardHeight(InventoryView view)
    {
        if (view.Craft.Recipe == null) return DetailPad * 2f + 90f;
        var height = DetailPad + DetailHeaderH + 10f;
        var chipsPerRow = CraftChipsPerRow;
        foreach (var ingredient in view.Craft.Recipe.Ingredients)
            height += CraftIngCardHeight(ingredient.AcceptedIds.Length, chipsPerRow) + 8f;
        return height + StatusH + 8f + CraftButtonH + DetailPad;
    }

    private Vector2 CraftCardCentre(InventoryView view) =>
        new(_screenSize.X - PreviewRight, PanelTop + CraftCardHeight(view) * 0.5f);

    private Vector2 CraftBatchMinusCentre(InventoryView view)
    {
        var cardLeft = _screenSize.X - PreviewRight - DetailWidth * 0.5f;
        var x = cardLeft + DetailPad + 76f + CraftBatchLabelW() + BatchGap + StepBox * 0.5f;
        return new Vector2(x, CraftBatchCentreY(view));
    }

    private float CraftBatchLabelW() =>
        _font?.MeasureSize("Batch".AsSpan(), _countScale).X ?? 44f;

    private Vector2 CraftBatchCountCentre(InventoryView view)
    {
        var minus = CraftBatchMinusCentre(view);
        return new Vector2(minus.X + StepBox * 0.5f + BatchGap + BatchCountW * 0.5f, minus.Y);
    }

    private Vector2 CraftBatchPlusCentre(InventoryView view)
    {
        var count = CraftBatchCountCentre(view);
        return new Vector2(count.X + BatchCountW * 0.5f + BatchGap + StepBox * 0.5f, count.Y);
    }

    private float CraftBatchMaxX(InventoryView view) =>
        CraftBatchPlusCentre(view).X + StepBox * 0.5f + BatchGap;

    private float CraftBatchCentreY(InventoryView view)
    {
        _ = view;
        return PanelTop + DetailPad + 56f + 8f;
    }

    private float CraftIngCardTop(InventoryView view, int index)
    {
        var y = PanelTop + DetailPad + DetailHeaderH + 10f;
        var chipsPerRow = CraftChipsPerRow;
        var ingredients = view.Craft.Recipe!.Ingredients;
        for (var i = 0; i < index; i++)
            y += CraftIngCardHeight(ingredients[i].AcceptedIds.Length, chipsPerRow) + 8f;
        return y;
    }

    private Vector2 CraftIngMinusCentre(InventoryView view, int index)
    {
        var cardLeft = _screenSize.X - PreviewRight - DetailWidth * 0.5f;
        var cardRight = cardLeft + DetailWidth - DetailPad;
        var top = CraftIngCardTop(view, index);
        var fedY = top + IngPad + IngTitleH + IngBarH + 6f;
        var plusX = cardRight - IngPad - IngStepBox * 0.5f;
        return new Vector2(plusX - IngStepBox - 8f, fedY + IngFedH * 0.5f);
    }

    private Vector2 CraftIngPlusCentre(InventoryView view, int index)
    {
        var minus = CraftIngMinusCentre(view, index);
        return minus with { X = minus.X + IngStepBox + 8f };
    }

    private Vector2 CraftChipCentre(InventoryView view, int ingredientIndex, int chipPos)
    {
        var cardLeft = _screenSize.X - PreviewRight - DetailWidth * 0.5f;
        var top = CraftIngCardTop(view, ingredientIndex);
        var chipsY = top + IngPad + IngTitleH + IngBarH + 6f + IngFedH + 6f;
        var chipsPerRow = CraftChipsPerRow;
        var row = chipPos / chipsPerRow;
        var col = chipPos % chipsPerRow;
        return new Vector2(
            cardLeft + DetailPad + IngPad + ChipSize * 0.5f + col * (ChipSize + ChipGap),
            chipsY + ChipSize * 0.5f + row * (ChipSize + ChipGap));
    }

    private Vector2 CraftButtonCentre(InventoryView view)
    {
        var height = CraftCardHeight(view);
        return new Vector2(_screenSize.X - PreviewRight,
            PanelTop + height - DetailPad - CraftButtonH * 0.5f);
    }

    private static bool InBox(Vector2 point, Vector2 centre, Vector2 size) =>
        MathF.Abs(point.X - centre.X) <= size.X * 0.5f
        && MathF.Abs(point.Y - centre.Y) <= size.Y * 0.5f;

    /// <summary>Which crafting control sits under a screen point, if any.</summary>
    public CraftHit CraftHitAt(Vector2 point, InventoryView view)
    {
        if (view.Craft.Recipe == null) return new CraftHit(CraftHitKind.None);
        if (InBox(point, CraftButtonCentre(view), new Vector2(CraftInnerWidth, CraftButtonH)))
            return new CraftHit(CraftHitKind.CraftButton);
        if (InBox(point, CraftBatchMinusCentre(view), new Vector2(StepBox)))
            return new CraftHit(CraftHitKind.BatchMinus);
        if (InBox(point, CraftBatchPlusCentre(view), new Vector2(StepBox)))
            return new CraftHit(CraftHitKind.BatchPlus);

        var ingredients = view.Craft.Recipe.Ingredients;
        for (var i = 0; i < ingredients.Length; i++)
        {
            if (InBox(point, CraftIngMinusCentre(view, i), new Vector2(IngStepBox)))
                return new CraftHit(CraftHitKind.IngMinus, i);
            if (InBox(point, CraftIngPlusCentre(view, i), new Vector2(IngStepBox)))
                return new CraftHit(CraftHitKind.IngPlus, i);
            var members = ingredients[i].AcceptedIds;
            for (var c = 0; c < members.Length; c++)
                if (InBox(point, CraftChipCentre(view, i, c), new Vector2(ChipSize)))
                    return new CraftHit(CraftHitKind.MemberChip, i, members[c]);
        }
        return new CraftHit(CraftHitKind.None);
    }

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
        var session = view.Craft;
        var cardCentre = CraftCardCentre(view);
        var cardSize = new Vector2(DetailWidth, CraftCardHeight(view));
        DrawRect(cardCentre, cardSize, PanelBackground);
        DrawBorder(cardCentre, cardSize, SlotBorder, SlotBorderColor);
        var cardLeft = cardCentre.X - DetailWidth * 0.5f;
        DrawRect(new Vector2(cardCentre.X, PanelTop + 3f),
            new Vector2(DetailWidth - 4f, 2f), AccentGold);

        if (session.Recipe == null)
        {
            DrawTextCentredFitted("Crafting", cardCentre.X, PanelTop + DetailPad, _headScale, Rgba.White,
                CraftInnerWidth - 8f);
            DrawTextCentredFitted("Pick a recipe on the left", cardCentre.X,
                PanelTop + DetailPad + 34f, _countScale, TextDim, CraftInnerWidth - 8f);
            DrawTextCentredFitted("then feed it items and hit CRAFT", cardCentre.X,
                PanelTop + DetailPad + 54f, _countScale, TextDim, CraftInnerWidth - 8f);
            return;
        }

        var recipe = session.Recipe;
        var ready = session.IsReady(inventory);
        var y = PanelTop + DetailPad;

        var iconCentre = new Vector2(cardLeft + DetailPad + 32f, y + 38f);
        var spin = time * 0.9f;
        if (ItemRegistry.IsItem(recipe.ResultId))
            DrawItemIcon(iconCentre, 56f, recipe.ResultId, spin);
        else
            DrawBlockIconSpinning(iconCentre, 60f, recipe.ResultId, spin);

        var textX = cardLeft + DetailPad + 76f;
        var headAvail = DetailWidth - DetailPad * 2f - 76f;
        var title = Inventory.DisplayName(recipe.ResultId);
        var titleScale = FitScale(title, _headScale, headAvail);
        DrawText(FitText(title, titleScale, headAvail), new Vector2(textX, y + 4f), titleScale, Rgba.White);
        DrawText(FitText($"Makes {recipe.ResultCount} per craft", _countScale, headAvail),
            new Vector2(textX, y + 32f), _countScale, TextDim);

        DrawText("Batch", new Vector2(textX, y + 56f), _countScale, TextDim);
        var batchMinus = CraftBatchMinusCentre(view);
        var batchPlus = CraftBatchPlusCentre(view);
        DrawBoxButton(batchMinus, StepBox, "-", ChipBackground, SlotBorderColor, Rgba.White);
        DrawTextCentred(session.Batch.ToString(), CraftBatchCountCentre(view).X,
            batchMinus.Y - 8f, _countScale, Rgba.White);
        DrawBoxButton(batchPlus, StepBox, "+", ChipBackground, SlotBorderColor, Rgba.White);
        var maxBatch = session.MaxAffordable(inventory);
        var maxX = CraftBatchMaxX(view);
        var maxRight = cardLeft + DetailWidth - DetailPad;
        DrawText(FitText($"max {maxBatch}", _countScale, Math.Max(20f, maxRight - maxX)),
            new Vector2(maxX, y + 56f),
            _countScale, maxBatch >= session.Batch ? ReadyGreen : DangerRed);

        y += DetailHeaderH + 10f;
        DrawRect(new Vector2(cardCentre.X, y - 5f), new Vector2(CraftInnerWidth, 1.5f), SlotBorderColor);

        for (var i = 0; i < recipe.Ingredients.Length; i++)
        {
            var ingredient = recipe.Ingredients[i];
            var cardTop = CraftIngCardTop(view, i);
            var cardH = CraftIngCardHeight(ingredient.AcceptedIds.Length, CraftChipsPerRow);
            var ingCentre = new Vector2(cardCentre.X, cardTop + cardH * 0.5f);
            DrawRect(ingCentre, new Vector2(CraftInnerWidth, cardH), CardBackground);

            var need = session.NeedAt(i);
            var fed = session.FedAt(i);
            var label = ingredient.IsTag ? $"Any {ingredient.Tag}" : Inventory.DisplayName(ingredient.Id);
            var ingTitleAvail = CraftInnerWidth - IngPad * 2f;
            DrawText(FitText($"{i + 1}. {label}  x{ingredient.Count}/craft", _countScale, ingTitleAvail),
                new Vector2(cardLeft + DetailPad + IngPad, cardTop + IngPad), _countScale, Rgba.White);

            var barY = cardTop + IngPad + IngTitleH;
            var barW = CraftInnerWidth - IngPad * 2f;
            DrawRect(new Vector2(cardLeft + DetailPad + IngPad + barW * 0.5f, barY + IngBarH * 0.5f),
                new Vector2(barW, IngBarH), SlotBackground);
            var fill = Math.Clamp(need > 0 ? fed.Count / (float)need : 1f, 0f, 1f);
            if (fill > 0f)
                DrawRect(new Vector2(cardLeft + DetailPad + IngPad + barW * fill * 0.5f, barY + IngBarH * 0.5f),
                    new Vector2(barW * fill, IngBarH), fill >= 1f ? ReadyGreen : AccentGold);

            var fedY = barY + IngBarH + 6f;
            var fedCx = cardLeft + DetailPad + IngPad + 16f;
            var fedCy = fedY + IngFedH * 0.5f;
            if (fed.MemberId != 0)
                DrawIdIcon(new Vector2(fedCx, fedCy), 30f, fed.MemberId, headingDegrees, time);
            else
                DrawTextCentred("?", fedCx, fedCy - 8f, _countScale, TextDim);

            var minus = CraftIngMinusCentre(view, i);
            var plus = CraftIngPlusCentre(view, i);
            var fedText = $"{fed.Count} / {need} fed";
            var fedAvail = minus.X - IngStepBox * 0.5f - (fedCx + 24f) - 8f;
            DrawText(FitText(fedText, _countScale, Math.Max(20f, fedAvail)),
                new Vector2(fedCx + 24f, fedCy - 8f), _countScale,
                fed.Count >= need ? ReadyGreen : Rgba.White);
            DrawBoxButton(minus, IngStepBox, "-", ChipBackground, SlotBorderColor, Rgba.White);
            DrawBoxButton(plus, IngStepBox, "+", ChipBackground, SlotBorderColor, Rgba.White);

            var members = ingredient.AcceptedIds;
            var picked = fed.MemberId;
            for (var c = 0; c < members.Length; c++)
            {
                var chipC = CraftChipCentre(view, i, c);
                var owned = inventory.CountFor(members[c]);
                var isPicked = picked != 0 && picked == members[c];
                DrawRect(chipC, new Vector2(ChipSize), owned > 0 ? ChipBackground : SlotBackground);
                DrawBorder(chipC, new Vector2(ChipSize), isPicked ? 2.5f : 1.5f,
                    isPicked ? AccentGold : SlotBorderColor);
                DrawIdIcon(chipC with { Y = chipC.Y - 3f }, 26f, members[c], headingDegrees, time);
                var ownedText = owned.ToString();
                if (_font != null)
                {
                    var size = _font.MeasureSize(ownedText.AsSpan(), _countScale);
                    DrawText(ownedText,
                        new Vector2(chipC.X + ChipSize * 0.5f - 3f - size.X,
                            chipC.Y + ChipSize * 0.5f - 3f - size.Y),
                        _countScale, owned > 0 ? Rgba.White : TextDim);
                }
            }
        }

        var statusY = PanelTop + CraftCardHeight(view) - DetailPad - CraftButtonH - 8f - StatusH;
        DrawTextCentredFitted(session.StatusLine(inventory), cardCentre.X, statusY,
            _countScale, ready ? ReadyGreen : AccentGold, CraftInnerWidth - 8f);

        var buttonC = CraftButtonCentre(view);
        var buttonS = new Vector2(CraftInnerWidth, CraftButtonH);
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

        // Same cutout discard as DrawBlockIcon: the spinning preview would otherwise
        // rasterize the sprite's transparent texels (and their depth) as black.
        var cutout = BlockType.ById[blockId]?.Cutout == true;
        if (cutout)
        {
            _shader.Use();
            _shader.SetUniform(_locAlphaCutoff, 0.5f);
        }
        DrawMesh(mesh, model, Rgba.White, _atlasTexture);
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
        foreach (var icon in _itemIcons.Values)
            _gl.DeleteTexture(icon);
        foreach (var frames in _itemFrames.Values)
            foreach (var frame in frames)
                frame.Dispose();

        GC.SuppressFinalize(this);
    }
}
