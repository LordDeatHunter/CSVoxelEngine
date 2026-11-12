namespace VoxelEngine.Config;

/// <summary>
/// Tuning of the rarity chrome over list rows and the hover tooltip (<c>RarityStyle</c>, <c>RarityChrome</c>, the
/// <c>uChrome*</c> uniforms of ui-fragment.glsl). The per-rarity arrays are indexed by <c>(int)Rarity</c>, Common
/// first: Common is all zero so its rows keep the plain look, and each tier is at least as dressed as the one below.
/// </summary>
public static class RarityStyleConfig
{
    /// <summary>How opaque the chrome is per rarity (uChromeColor.a); 0 draws no chrome at all.</summary>
    public static readonly float[] OpacityPerRarity = [0f, 0.8f, 0.9f, 0.95f, 1f];

    /// <summary>Strength of the rarity-colored wash that fades in from the row's left or the tooltip's top
    /// (uChromeLayers.x).</summary>
    public static readonly float[] WashPerRarity = [0f, 0.1f, 0.13f, 0.16f, 0.2f];

    /// <summary>Strength of the soft glow inside the border band (uChromeLayers.y).</summary>
    public static readonly float[] GlowPerRarity = [0f, 0f, 0.35f, 0.55f, 0.75f];

    /// <summary>Peak brightness of the white sheen that sweeps across the box (uChromeLayers.z).</summary>
    public static readonly float[] SheenPerRarity = [0f, 0f, 0.16f, 0.24f, 0.32f];

    /// <summary>Brightness of the twinkling four-point sparkles (uChromeLayers.w).</summary>
    public static readonly float[] SparklePerRarity = [0f, 0f, 0f, 0f, 0.85f];

    /// <summary>How deep the border and glow breathe: the pulse factor in uChromeMotion.y spans
    /// <c>1 - depth</c>..1.</summary>
    public static readonly float[] PulseDepthPerRarity = [0f, 0f, 0f, 0.3f, 0.45f];

    /// <summary>How far the inner glow reaches in from the edge, in canvas units (uChromeShape.y).</summary>
    public const float GlowReach = 6f;

    /// <summary>Half the width of the sheen band, in canvas units (uChromeShape.z).</summary>
    public const float SheenHalfWidth = 16f;

    /// <summary>How the sheen band leans (uChromeBox.z): it runs along <c>p.x + slant * p.y</c>, leaning like '/'.</summary>
    public const float SheenSlant = 0.6f;

    /// <summary>Seconds from one sheen sweep's start to the next.</summary>
    public const float SheenPeriodSeconds = 3.5f;

    /// <summary>The share of each sheen period the band spends crossing the box; it rests for the rest.</summary>
    public const float SheenSweepShare = 0.3f;

    /// <summary>Seconds each list row's sheen trails the row above it, so the sweep cascades down the list.</summary>
    public const float SheenRowStaggerSeconds = 0.12f;

    /// <summary>Seconds of one border and glow breath.</summary>
    public const float PulsePeriodSeconds = 2f;

    /// <summary>The side of one sparkle cell, in canvas units (uChromeShape.w); a cell holds at most one star.</summary>
    public const float SparkleCellSize = 10f;

    /// <summary>The share of sparkle cells that hold a star (uChromeBox.w).</summary>
    public const float SparkleDensity = 0.08f;

    /// <summary>How often a sparkle twinkles, in cycles per second.</summary>
    public const float SparkleTwinkleHz = 0.6f;

    /// <summary>The chrome's opacity factor on a dimmed row (an uncraftable recipe).</summary>
    public const float DimOpacity = 0.4f;
}
