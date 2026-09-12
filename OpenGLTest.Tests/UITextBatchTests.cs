using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The headless slice of the batched text path: <see cref="UIRenderer.WriteGlyphQuad"/> and
/// <see cref="UIRenderer.BuildQuadIndices"/> are pure CPU vertex/index math, so they can be
/// pinned without a GL context. The upload and the draw call itself are not covered here.
/// </summary>
public sealed class UITextBatchTests
{
    [Fact]
    public void GlyphQuadMatchesTheRectTransformItReplaces()
    {
        // Pins the three properties that make batching a drop-in for the old per-glyph mesh
        // draw: the same corner positions RectTransform produced, the same UV winding
        // UvRect.CornersTo hands Primitives.CreateRectangle, and the tint — which used to be
        // the per-draw uColor uniform — now carried per-vertex.
        var centre = new Vector2(37.5f, 12.25f);
        var size = new Vector2(9f, 14f);
        var uv = new UvRect(new Vector2(0.125f, 0.25f), new Vector2(0.1875f, 0.375f));
        var color = new Rgba(17, 200, 3, 180);

        Span<Vertex> quad = stackalloc Vertex[4];
        UIRenderer.WriteGlyphQuad(quad, new GlyphInstance(centre, size, uv), centre, color);

        // The body of the private UIRenderer.RectTransform, rebuilt independently.
        var model = Matrix4x4.CreateScale(size.X, -size.Y, 1f)
                    * Matrix4x4.CreateTranslation(centre.X, centre.Y, 0f);
        // Primitives.CreateRectangle's local corner order for a unit rect.
        Span<Vector3> local =
        [
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f)
        ];
        Span<Vector2> uvCorners = stackalloc Vector2[4];
        uv.CornersTo(uvCorners);

        var expectedPacked =
            new Vertex(Vector3.Zero, color.R, color.G, color.B, color.A, Vector2.Zero).PackedColor;

        for (var i = 0; i < 4; i++)
        {
            // Exact equality is right here: the local corners are +/-0.5 and half is size*0.5f,
            // so every partial product is an exact power-of-two scale or an exact zero and
            // Vector3.Transform agrees bit-for-bit with or without FMA.
            Assert.Equal(Vector3.Transform(local[i], model), quad[i].Position);
            Assert.Equal(uvCorners[i], quad[i].Uv);
            Assert.Equal(0, (int)quad[i].Layer);
            Assert.Equal(expectedPacked, quad[i].PackedColor);
        }
    }

    [Fact]
    public void TextQuadIndicesMatchThePrimitiveWinding()
    {
        const int quads = 3;
        var indices = new ushort[quads * 6];
        UIRenderer.BuildQuadIndices(indices);

        // Primitives.CreateRectangle's [0, 1, 2, 0, 2, 3] split, rebased per quad.
        int[] pattern = [0, 1, 2, 0, 2, 3];
        for (var q = 0; q < quads; q++)
        {
            var v = q * 4;
            for (var k = 0; k < pattern.Length; k++)
            {
                Assert.Equal(v + pattern[k], (int)indices[q * 6 + k]);
                // A quad never reaches into a neighbour's vertices.
                Assert.InRange((int)indices[q * 6 + k], v, v + 3);
            }
        }
    }
}
