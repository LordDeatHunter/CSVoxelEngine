using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using VoxelEngine.Data;
using VoxelEngine.Graphics;
using VoxelEngine.UI;
using VoxelEngine.UI.Widgets;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// A reusable progress bar drawn from a sprite sheet of two rows (full on top, empty below) with three segments
/// per row: a left cap, a middle that repeats and a right cap. <see cref="SegmentedBarLayout"/> decides the
/// pieces; this class owns the texture and the cropped quads and draws them through the <see cref="UIRenderer"/>
/// primitives. Text (a name, a number) is the caller's, so the same bar serves a boss bar, a cast bar or a
/// loading bar. Render thread only.
/// </summary>
internal sealed class SegmentedBarRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly UIRenderer _ui;
    private readonly uint _texture;
    private readonly int _segmentPx;
    private readonly int _rowPx;
    private readonly Dictionary<(float Src0, float Src1, bool Full), Mesh> _quads = [];

    /// <param name="texturePath">The sheet: <c>3 * segmentPx</c> wide, two rows of <paramref name="rowPx"/> tall.</param>
    /// <param name="segmentPx">Width of one segment in texture pixels.</param>
    /// <param name="rowPx">Height of one row in texture pixels.</param>
    internal SegmentedBarRenderer(GL gl, UIRenderer ui, string texturePath, int segmentPx, int rowPx)
    {
        _gl = gl;
        _ui = ui;
        _segmentPx = segmentPx;
        _rowPx = rowPx;
        _texture = TextureLoader.LoadFromFile(gl, texturePath);
    }

    /// <summary>Draws the bar with its top-left corner at <paramref name="topLeft"/>, <paramref name="width"/> by
    /// <paramref name="height"/> canvas units, filled left to right up to <paramref name="share"/> (0..1).</summary>
    internal void Draw(Vector2 topLeft, float width, float height, float share)
    {
        foreach (var piece in SegmentedBarLayout.Pieces(width, height, share, _segmentPx, _rowPx))
        {
            var center = new Vector2(topLeft.X + piece.X + piece.Width * 0.5f, topLeft.Y + height * 0.5f);
            _ui.DrawMesh(QuadFor(piece), UIRenderer.RectTransform(center, new Vector2(piece.Width, height)),
                Rgba.White, _texture);
        }
    }

    // One quad per distinct crop, made on first use; the cropped boundary pieces are the only ones that vary.
    private Mesh QuadFor(BarPiece piece)
    {
        var key = (piece.SrcX0, piece.SrcX1, piece.Full);
        if (_quads.TryGetValue(key, out var mesh))
            return mesh;

        float sheetWidth = _segmentPx * 3f;
        float sheetHeight = _rowPx * 2f;
        var rowTop = piece.Full ? 0f : _rowPx;
        // UvRect measures V from the image's top, as authored
        var uv = new UvRect(
            new Vector2(piece.SrcX0 / sheetWidth, rowTop / sheetHeight),
            new Vector2(piece.SrcX1 / sheetWidth, (rowTop + _rowPx) / sheetHeight));
        mesh = Primitives.CreateRectangle(_gl, 1f, 1f, uv: uv);
        _quads[key] = mesh;
        return mesh;
    }

    public void Dispose()
    {
        _gl.DeleteTexture(_texture);
        foreach (var mesh in _quads.Values)
            mesh.Dispose();
        _quads.Clear();
    }
}
