using System;
using System.Collections.Generic;
using System.IO;
using VoxelEngine.Chunks;

namespace VoxelEngine.Saves;

/// <summary>
/// The named <see cref="IWorldSource"/>s a dedicated server can build a world from, chosen by the
/// world's <c>world.json</c> <c>source</c> field (<see cref="WorldInfo.Source"/>) or the server's
/// <c>--source &lt;name&gt;</c> argument: <see cref="Generated"/> (worldgen, the default) or
/// <see cref="Imported"/> (an <see cref="ImportedMapSource"/> over the world folder's
/// <see cref="ImportedMapSource.DirectoryName"/> folder, worldgen outside it). The game's main menu
/// never reads this; only the server does.
/// </summary>
public static class WorldSources
{
    /// <summary>Worldgen: <see cref="GeneratedWorldSource.Instance"/>.</summary>
    public const string Generated = "generated";

    /// <summary>The world folder's imported map (<see cref="ImportedMapSource"/>), worldgen outside
    /// the columns it supplies.</summary>
    public const string Imported = "imported";

    /// <summary>Every name <see cref="Create"/> accepts, case-insensitively.</summary>
    public static IReadOnlyList<string> Names { get; } = [Generated, Imported];

    /// <summary>
    /// Builds the source <paramref name="name"/> names for the world in
    /// <paramref name="worldDirectory"/>. A null or blank name, or <see cref="Generated"/>, is
    /// <see cref="GeneratedWorldSource.Instance"/>; <see cref="Imported"/> loads the map in
    /// <c>&lt;worldDirectory&gt;/import</c> and lets the <see cref="ImportedMapSource"/> constructor's
    /// exceptions through (<see cref="DirectoryNotFoundException"/> when the map or its columns
    /// folder is missing, <see cref="InvalidDataException"/> or <see cref="IOException"/> naming a
    /// bad column file). Any other name throws <see cref="ArgumentException"/> listing
    /// <see cref="Names"/>.
    /// </summary>
    public static IWorldSource Create(string? name, string worldDirectory)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals(Generated, StringComparison.OrdinalIgnoreCase))
            return GeneratedWorldSource.Instance;

        if (trimmed.Equals(Imported, StringComparison.OrdinalIgnoreCase))
        {
            ArgumentNullException.ThrowIfNull(worldDirectory);
            return new ImportedMapSource(Path.Combine(worldDirectory, ImportedMapSource.DirectoryName));
        }

        // No parameter name: the message is what the server prints
        throw new ArgumentException(
            $"Unknown world source '{trimmed}'; expected one of: {string.Join(", ", Names)}.");
    }
}
