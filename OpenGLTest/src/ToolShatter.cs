using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using StbImageSharp;

namespace silkgltriangle;

/// <summary>
/// GL-free rules of a tool that ran out of durability: its shatter chips wear the mean colour of
/// the item's sprite (<see cref="LandingDust.MeanColour"/> over the PNG's opaque texels), read
/// once per item and cached, since a break is rare and the read is a file.
/// </summary>
public static class ToolShatter
{
    private static readonly ConcurrentDictionary<byte, Vector3> Colours = new();

    /// <summary>Mean sRGB colour of <paramref name="item"/>'s sprite; <paramref name="fallback"/>
    /// when the texture is missing or has not one opaque texel.</summary>
    public static Vector3 ColourOf(Item item, Vector3 fallback) =>
        Colours.GetOrAdd(item.Id, _ => ReadColour(item, fallback));

    private static Vector3 ReadColour(Item item, Vector3 fallback)
    {
        try
        {
            using var stream = File.OpenRead(item.TexturePath);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            return LandingDust.MeanColour(image.Data, fallback);
        }
        catch (IOException)
        {
            return fallback;
        }
    }
}
