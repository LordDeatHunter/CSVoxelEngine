using VoxelEngine.Data;
using VoxelEngine.Items;

namespace VoxelEngine.UI;

/// <summary>
/// How a rarity dresses an item wherever the UI lists or describes it (GL-free): <see cref="Color"/> for its name,
/// the tooltip title and the chrome, and the strengths of the chrome's layers (<c>RarityChrome</c> turns them into
/// the shader's uniforms). The one table the [E] panel rows, the chest card rows and the hover tooltip share, read
/// from <see cref="Rarities.Color"/> and <see cref="RarityStyleConfig"/>; Common has no chrome.
/// </summary>
public readonly record struct RarityStyle(
    Rgba Color, float Opacity, float Wash, float Glow, float Sheen, float Sparkle, float PulseDepth)
{
    /// <summary>The style of a rarity.</summary>
    public static RarityStyle Of(Rarity rarity)
    {
        var i = (int)rarity;
        return new RarityStyle(
            Rarities.Color(rarity),
            RarityStyleConfig.OpacityPerRarity[i],
            RarityStyleConfig.WashPerRarity[i],
            RarityStyleConfig.GlowPerRarity[i],
            RarityStyleConfig.SheenPerRarity[i],
            RarityStyleConfig.SparklePerRarity[i],
            RarityStyleConfig.PulseDepthPerRarity[i]);
    }

    /// <summary>Whether the rarity draws any chrome at all (Common does not).</summary>
    public bool HasChrome => Opacity > 0f;

    /// <summary>The accent a row's bar takes: the rarity's color when it has chrome, else <paramref name="plain"/>.</summary>
    public Rgba AccentOr(Rgba plain) => HasChrome ? Color : plain;
}
