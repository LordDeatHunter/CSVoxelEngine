using System;
using System.Collections.Generic;
using System.IO;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.Content;

/// <summary>Compiles the tree species under <c>data/trees</c> into <see cref="TreeSpeciesRegistry"/> and fills the wood blocks' pixel-rim table.</summary>
internal static class TreeSpeciesLoader
{
    /// <summary>
    /// Compiles every tree species under <c>assets/data/trees</c> into
    /// <see cref="TreeSpeciesRegistry"/>. Runs after the blocks, so block ids and
    /// <see cref="BlockType.ById"/> are settled; nothing from <see cref="ContentRegistry.BakeModels"/> is read. A
    /// missing folder is tolerated (an empty registry), like the recipe folders. Also fills
    /// <see cref="BlockType.PixelRimById"/> from each species' rim, throwing naming both files
    /// when two species sharing a wood block disagree.
    /// </summary>
    internal static void LoadTreeSpecies(string folder)
    {
        var species = new Dictionary<string, TreeSpecies>(StringComparer.Ordinal);
        // The rim is a property of the wood block (the mesher and collision only see the id), so
        // two species sharing one wood must agree on it
        var rimByWood = new Dictionary<byte, (TrunkRim Rim, string Path)>();
        if (Directory.Exists(folder))
        {
            foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            {
                var s = CompileTreeSpeciesFile(name, path);
                species[name] = s;
                if (rimByWood.TryGetValue(s.WoodId, out var seen) && seen.Rim != s.Rim)
                    throw new InvalidOperationException(
                        $"Tree species '{path}' and '{seen.Path}' share wood '{BlockType.ById[s.WoodId]!.Name}' " +
                        $"but disagree on thickness.rim ({s.Rim} vs {seen.Rim}).");
                rimByWood[s.WoodId] = (s.Rim, path);
                BlockType.PixelRimById[s.WoodId] = s.Rim == TrunkRim.Pixel;
            }
        }

        TreeSpeciesRegistry.Publish(species);
    }

    /// <summary>
    /// Reads and compiles one species file. Internal so tests can feed a file and see the same
    /// JSON and validation errors, each naming that path, without reloading the registry.
    /// </summary>
    internal static TreeSpecies CompileTreeSpeciesFile(string name, string path)
    {
        var species = TreeSpeciesCompiler.Compile(name, ContentFiles.ReadJson<TreeSpeciesDefinition>(path), path, ResolveSpeciesBlock);
        // A grown tree's wood carries its thickness and parent direction in the state byte, so
        // only a tree_wood block can draw, collide and fell it
        if (BlockType.ById[species.WoodId]!.Renderer != BlockRenderer.TreeWood)
            throw new InvalidOperationException(
                $"Tree species '{path}' grows wood '{BlockType.ById[species.WoodId]!.Name}', which does not use renderer \"tree_wood\".");
        return species;
    }

    /// <summary>
    /// A species' block reference: an optional <c>block/</c> prefix or a bare block file name.
    /// Species name blocks only, never items, which is why <see cref="ContentFiles.TryResolve"/>'s bare-name
    /// item fallback is not used; anything else resolves to null.
    /// </summary>
    private static byte? ResolveSpeciesBlock(string reference)
    {
        if (reference.StartsWith("item/", StringComparison.Ordinal)) return null;
        var bare = reference.StartsWith("block/", StringComparison.Ordinal) ? reference["block/".Length..] : reference;
        return ContentRegistry.BlockIds.TryGetValue(bare, out var id) && BlockType.ById[id] != null ? id : null;
    }
}
