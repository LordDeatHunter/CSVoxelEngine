using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using StbImageSharp;
using VoxelEngine.Items;
using VoxelEngine.Particles;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free rules of a tool that ran out of durability: its shatter chips wear the mean color of
/// the item's sprite (<see cref="LandingDust.MeanColor"/> over the PNG's opaque texels), read
/// once per item and cached, since a break is rare and the read is a file.
/// </summary>
public static class ToolShatter
{
    private static readonly ConcurrentDictionary<byte, Vector3> Colors = new();

    /// <summary>Mean sRGB color of <paramref name="item"/>'s sprite; <paramref name="fallback"/>
    /// when the texture is missing or has not one opaque texel.</summary>
    public static Vector3 ColorOf(Item item, Vector3 fallback) =>
        Colors.GetOrAdd(item.Id, _ => ReadColor(item, fallback));

    private static Vector3 ReadColor(Item item, Vector3 fallback)
    {
        try
        {
            using var stream = File.OpenRead(item.TexturePath);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            return LandingDust.MeanColor(image.Data, fallback);
        }
        catch (IOException)
        {
            return fallback;
        }
    }
}
