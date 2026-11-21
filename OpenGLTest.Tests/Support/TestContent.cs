using System;
using System.Collections.Generic;
using System.IO;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Graphics;
using VoxelEngine.WorldGen.Trees;

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
    private static Dictionary<string, (TreeSpecies Owner, string Wood, string? Leaves)>? _realTreeOwners;

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
        lock (Gate)
        {
            if (!_loaded) throw new InvalidOperationException("Load real test content before swapping it.");
            if (_realTreeOwners is not null) throw new InvalidOperationException("Nested content swaps are unsupported.");

            var owners = new Dictionary<string, (TreeSpecies, string, string?)>(StringComparer.Ordinal);
            foreach (var (name, species) in TreeSpeciesRegistry.ByName)
                owners.Add(name, (species, BlockName(species.WoodId),
                    species.HasLeaves ? BlockName(species.LeavesId) : null));
            _realTreeOwners = owners;
            LoadFrom(root);
        }
    }

    /// <summary>Reloads the real content after a <see cref="SwapIn"/>.</summary>
    internal static void RestoreReal()
    {
        lock (Gate)
        {
            var owners = _realTreeOwners ?? throw new InvalidOperationException("No content swap to restore.");
            _loaded = false;
            LoadFrom(FindAssetsRoot());

            // The generator's immutable tables still hold the original species objects. Only
            // republish them when the restored real content has the same names and block bindings.
            if (TreeSpeciesRegistry.ByName.Count != owners.Count)
                throw new InvalidOperationException("Restored tree species membership changed.");
            var original = new Dictionary<string, TreeSpecies>(StringComparer.Ordinal);
            foreach (var (name, saved) in owners)
            {
                if (!TreeSpeciesRegistry.ByName.TryGetValue(name, out var reloaded)
                    || reloaded.WoodId != saved.Owner.WoodId
                    || reloaded.LeavesId != saved.Owner.LeavesId
                    || BlockName(reloaded.WoodId) != saved.Wood
                    || (reloaded.HasLeaves ? BlockName(reloaded.LeavesId) : null) != saved.Leaves)
                    throw new InvalidOperationException($"Restored tree species '{name}' changed its block bindings.");
                original.Add(name, saved.Owner);
            }
            TreeSpeciesRegistry.Publish(original);
            _realTreeOwners = null;
            _loaded = true;
        }
    }

    private static string BlockName(ushort id) =>
        id < BlockType.ById.Length && BlockType.ById[id] is { } block
            ? block.Name
            : throw new InvalidOperationException($"Tree block {id} is not registered.");

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
