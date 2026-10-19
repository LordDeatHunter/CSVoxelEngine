using System;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.UI.Menus;
using static VoxelEngine.Rendering.Overlays.UIRenderer;

namespace VoxelEngine.Rendering.Overlays;

/// <summary>
/// The loading screen of <see cref="UIRenderer"/>: the opaque loading and saving frames and the
/// fade over the first playing frames. Draws through the owner's primitives. Render thread only.
/// </summary>
internal sealed class LoadingScreenRenderer
{
    private readonly UIRenderer _ui;

    // The percentage strings, each formatted once, so an idle loading screen allocates nothing per frame.
    private readonly string?[] _percentTexts = new string?[101];

    internal LoadingScreenRenderer(UIRenderer ui)
    {
        _ui = ui;
    }

    /// <summary>
    /// The loading screen: the world name, an animated spinner (a pure function of the window
    /// clock, so a hitch never stalls it), the caption and, for <see cref="LoadingScreenKind.Loading"/>,
    /// a progress bar with its percentage. <paramref name="opaque"/> draws a fully opaque backdrop
    /// (a loading or saving frame, with nothing behind it); otherwise the backdrop takes
    /// <see cref="LoadingScreenView.FadeAlpha"/>, which is the fade over the first playing frames.
    /// Every other element is multiplied by the fade alpha either way. Geometry comes from
    /// <see cref="LoadingScreenLayout"/>.
    /// </summary>
    internal void DrawLoadingScreen(LoadingScreenView view, double time, bool opaque)
    {
        var fade = MathUtils.Clamp01(view.FadeAlpha);
        var screen = _ui.ScreenSize;
        _ui.DrawRect(screen * 0.5f, screen, opaque ? MenuBackdrop : Faded(MenuBackdrop, fade));
        if (fade <= 0f) return;

        _ui.TextFade = fade;
        try
        {
            var barSize = LoadingScreenLayout.BarSize;
            if (!string.IsNullOrEmpty(view.WorldName))
                _ui.DrawTextCentered(_ui.FittedText(view.WorldName, _ui.HeadScale, barSize.X), screen.X * 0.5f,
                    LoadingScreenLayout.TitleTop(screen), _ui.HeadScale, Rgba.White);
            _ui.DrawTextCentered(view.Caption, screen.X * 0.5f, LoadingScreenLayout.CaptionTop(screen),
                _ui.CountScale, TextDim);

            const int dots = HudConfig.LoadingSpinnerDots;
            var phase = LoadingScreenLayout.SpinnerPhase(time);
            for (var d = 0; d < dots; d++)
            {
                var brightness = LoadingScreenLayout.DotBrightness(d, dots, phase);
                _ui.DrawSquare(LoadingScreenLayout.DotCenter(screen, d, dots), LoadingScreenLayout.DotSize,
                    Faded(AccentGold, brightness * fade));
            }

            if (view.Kind == LoadingScreenKind.Loading)
            {
                var center = LoadingScreenLayout.BarCenter(screen);
                _ui.DrawRect(center, barSize, Faded(SlotBackground, fade));
                _ui.DrawBorder(center, barSize, SlotBorder, Faded(SlotBorderColor, fade));
                var progress = MathUtils.Clamp01(view.ShownProgress);
                if (progress > 0f)
                {
                    var fillW = barSize.X * progress;
                    _ui.DrawRect(new Vector2(center.X - barSize.X * 0.5f + fillW * 0.5f, center.Y),
                        new Vector2(fillW, barSize.Y), Faded(AccentGold, fade));
                }
                _ui.DrawTextCentered(PercentText(progress), screen.X * 0.5f,
                    LoadingScreenLayout.PercentTop(screen), _ui.CountScale, Rgba.White);
            }
        }
        finally
        {
            _ui.TextFade = 1f;
        }
    }

    private string PercentText(float progress)
    {
        var p = Math.Clamp((int)MathF.Round(progress * 100f), 0, 100);
        return _percentTexts[p] ??= $"{p}%";
    }
}
