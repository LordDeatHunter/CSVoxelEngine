using System.Numerics;
using VoxelEngine.UI;
using static VoxelEngine.UI.UiTheme;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// Draws a <see cref="RarityStyle"/>'s chrome over a list row or the hover tooltip box: one quad of the ui program with
/// <c>uChrome</c> on (its uniforms packed by <see cref="RarityChrome.For"/>), which it then turns back off so no later
/// rect, icon or text sees it. Draws nothing for a style without chrome (Common). Built by <see cref="UIRenderer"/>.
/// Render thread only.
/// </summary>
internal sealed class RarityChromeRenderer
{
    private readonly UIRenderer _ui;
    private readonly int _locChrome;
    private readonly int _locBox;
    private readonly int _locColor;
    private readonly int _locLayers;
    private readonly int _locShape;
    private readonly int _locMotion;

    internal RarityChromeRenderer(UIRenderer ui)
    {
        _ui = ui;
        var shader = ui.UiShader;
        _locChrome = shader.GetUniformLocation("uChrome");
        _locBox = shader.GetUniformLocation("uChromeBox");
        _locColor = shader.GetUniformLocation("uChromeColor");
        _locLayers = shader.GetUniformLocation("uChromeLayers");
        _locShape = shader.GetUniformLocation("uChromeShape");
        _locMotion = shader.GetUniformLocation("uChromeMotion");
    }

    /// <summary>
    /// Dresses the list row at <paramref name="center"/>: its sheen trails the row above by
    /// <see cref="RarityStyleConfig.SheenRowStaggerSeconds"/>, so the sweep cascades down the list, and
    /// <paramref name="opacity"/> fades the whole chrome (a dimmed row).
    /// </summary>
    internal void DrawRow(Vector2 center, Vector2 size, RarityStyle style, float time, int row, float opacity = 1f)
    {
        if (!style.HasChrome) return;
        Draw(center, RarityChrome.For(style, size, time - row * RarityStyleConfig.SheenRowStaggerSeconds,
            Border.Thin, washFromTop: false, opacity));
    }

    /// <summary>Dresses the hover tooltip box; its border band stands in for the plain border.</summary>
    internal void DrawTooltip(Vector2 center, Vector2 size, RarityStyle style, float time)
    {
        if (!style.HasChrome) return;
        Draw(center, RarityChrome.For(style, size, time, Border.Standard, washFromTop: true));
    }

    private void Draw(Vector2 center, in RarityChrome.Inputs inputs)
    {
        var shader = _ui.UiShader;
        shader.Use();
        shader.SetUniform(_locChrome, 1);
        shader.SetUniform(_locBox, inputs.Box);
        shader.SetUniform(_locColor, inputs.Color);
        shader.SetUniform(_locLayers, inputs.Layers);
        shader.SetUniform(_locShape, inputs.Shape);
        shader.SetUniform(_locMotion, inputs.Motion);
        _ui.DrawMesh(_ui.UnitSquare, UIRenderer.RectTransform(center, new Vector2(inputs.Box.X, inputs.Box.Y)));
        // Every other ui draw (rects, icons, text, the ItemDebugger) must see the plain path.
        shader.SetUniform(_locChrome, 0);
    }
}
