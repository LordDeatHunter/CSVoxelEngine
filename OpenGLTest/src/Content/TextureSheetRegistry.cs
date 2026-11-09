using System;
using System.Collections.Generic;

namespace VoxelEngine.Content;

/// <summary>
/// One block texture laid out as a spritesheet: a grid of <see cref="Cols"/> by <see cref="Rows"/> cells
/// read left to right, top to bottom, of which the first <see cref="Frames"/> are animation frames. The atlas
/// slices each frame into its own layer, the first under the texture's own name and the rest right after it.
/// </summary>
/// <param name="Texture">The texture's file name without extension, under <c>assets/textures/blocks</c>.</param>
/// <param name="Cols">Cells across the texture.</param>
/// <param name="Rows">Cells down the texture.</param>
/// <param name="Frames">Frames used, from the top-left cell.</param>
/// <param name="Fps">Frames per second the fluid shader cycles through them at.</param>
public sealed record TextureSheet(string Texture, int Cols, int Rows, int Frames, float Fps);

/// <summary>
/// Every spritesheet a block declares, collected by <see cref="BlockLoader"/> during
/// <see cref="ContentRegistry.Load"/>. Read by <see cref="VoxelEngine.Graphics.TextureAtlas"/> when it
/// slices the block textures and by the render setup for the frame rate, so it is read-only after load.
/// </summary>
public static class TextureSheetRegistry
{
    private static readonly List<TextureSheet> Sheets = [];

    /// <summary>Every declared spritesheet, in block file-name order.</summary>
    public static IReadOnlyList<TextureSheet> All => Sheets;

    /// <summary>The spritesheet declared for <paramref name="texture"/>, or null when it is a plain tile.</summary>
    public static TextureSheet? TryGet(string texture)
    {
        foreach (var sheet in Sheets)
            if (sheet.Texture == texture)
                return sheet;
        return null;
    }

    internal static void Add(TextureSheet sheet)
    {
        if (TryGet(sheet.Texture) != null)
            throw new InvalidOperationException($"Two blocks declare the spritesheet texture '{sheet.Texture}'.");
        Sheets.Add(sheet);
    }

    internal static void Reset() => Sheets.Clear();
}
