using System.IO;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Graphics;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// One-time headless content load shared by every test that names a block or item.
/// Thread-safe and idempotent, so parallel test collections can all call it.
/// BakeModels is GL-free (it only bakes quads), but it settles the occlusion and
/// light-passing flags the mesher reads, so tests need it too.
/// </summary>
internal static class TestContent
{
    private static readonly object Gate = new();
    private static bool _loaded;

    internal static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            LoadFrom(FindAssetsRoot());
            _loaded = true;
        }
    }

    /// <summary>
    /// Loads the content under another asset root in place of the real one. Only a test in the
    /// <see cref="ContentRegistrySwapCollection"/> may call it, and it must call
    /// <see cref="RestoreReal"/> in a <c>finally</c>.
    /// </summary>
    internal static void SwapIn(string root)
    {
        lock (Gate) LoadFrom(root);
    }

    /// <summary>Reloads the real content after a <see cref="SwapIn"/>.</summary>
    internal static void RestoreReal()
    {
        lock (Gate)
        {
            LoadFrom(FindAssetsRoot());
            _loaded = true;
        }
    }

    private static void LoadFrom(string root)
    {
        ContentRegistry.Load(root);
        TextureAtlas.RegisterTilesWithoutUpload(Path.Combine(root, "textures", "blocks"));
        ContentRegistry.BakeModels();
    }

    /// <summary>
    /// The tiles a block's faces are drawn with in the world: its model's
    /// <see cref="BlockType.FaceTiles"/>, or, for a biome-tinted block, the base and tinted tiles
    /// its <see cref="BlockType.BiomeTint"/> draws instead (the default tiles stay with icons).
    /// </summary>
    internal static System.Collections.Generic.HashSet<ushort> WorldTiles(BlockType block)
    {
        if (block.BiomeTint is not { } bt) return [.. block.FaceTiles];
        var tiles = new System.Collections.Generic.HashSet<ushort>();
        for (var f = 0; f < 6; f++)
        {
            if (bt.Base[f] != BiomeTintFaces.NoTile) tiles.Add(bt.Base[f]);
            if (bt.Tinted[f] != BiomeTintFaces.NoTile) tiles.Add(bt.Tinted[f]);
        }
        return tiles;
    }

    internal static string FindAssetsRoot() => AssetRoot.Find();
}
