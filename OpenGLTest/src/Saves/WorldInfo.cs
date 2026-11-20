using System.IO;
using VoxelEngine.Chunks.Sources;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Saves;

/// <summary>
/// One saved world as <see cref="WorldStore"/> lists it: the metadata in its <c>world.json</c>
/// plus the folder it lives in. <see cref="Slug"/> and <see cref="Directory"/> are fixed at
/// creation; a rename only changes <see cref="Name"/>.
/// </summary>
public sealed class WorldInfo(
    string slug, string name, int seed, long createdUnixMs, long lastPlayedUnixMs, int version, string directory,
    string? sourceName = null, int? terrainValue = null, bool terrainInvalid = false)
{
    private readonly int? _terrainValue = terrainValue;
    private readonly bool _terrainInvalid = terrainInvalid;

    /// <summary>The folder name under the saves root, derived from the name at creation.</summary>
    public string Slug { get; } = slug;

    /// <summary>The display name, unique case-insensitively among the store's worlds.</summary>
    public string Name { get; set; } = name;

    /// <summary>The worldgen seed the world was created with.</summary>
    public int Seed { get; } = seed;

    public long CreatedUnixMs { get; } = createdUnixMs;

    public long LastPlayedUnixMs { get; set; } = lastPlayedUnixMs;

    /// <summary>The save format version written in <c>world.json</c> (0 when the file had none).</summary>
    public int Version { get; } = version;

    /// <summary>The absolute path of the world's folder.</summary>
    public string Directory { get; } = directory;

    /// <summary>The <c>source</c> value written in <c>world.json</c>, trimmed, or null when the file
    /// has none (or a blank one). Kept raw, unvalidated, so a rewrite of the file (a rename, a
    /// touch) preserves exactly what was there and never adds the field to a world that lacked
    /// it; <see cref="WorldSources.Create"/> is what judges it.</summary>
    public string? SourceName { get; } = string.IsNullOrWhiteSpace(sourceName) ? null : sourceName.Trim();

    /// <summary>Which <see cref="IWorldSource"/> the dedicated server builds this world from
    /// (<see cref="WorldSources"/>): <see cref="SourceName"/>, or <see cref="WorldSources.Generated"/>
    /// when the file names none. The game's main menu ignores it and always generates.</summary>
    public string Source => SourceName ?? WorldSources.Generated;

    /// <summary>The optional value saved in world.json, absent for a legacy world.</summary>
    internal int? TerrainValue => _terrainValue;

    /// <summary>Resolves saved terrain without guessing when the field is malformed or unknown.</summary>
    public bool TryGetTerrainProfile(out WorldGenProfile profile, out string error)
    {
        if (!_terrainInvalid && (_terrainValue == null || _terrainValue == (int)WorldGenProfile.Legacy))
        {
            profile = WorldGenProfile.Legacy;
            error = "";
            return true;
        }
        if (!_terrainInvalid && _terrainValue == (int)WorldGenProfile.Dynamic)
        {
            profile = WorldGenProfile.Dynamic;
            error = "";
            return true;
        }

        profile = default;
        error = _terrainInvalid
            ? $"'{Name}' has an invalid terrain profile in world.json (expected 1 or 2)."
            : $"'{Name}' has unsupported terrain profile {_terrainValue} in world.json (expected 1 or 2).";
        return false;
    }

    /// <summary>The accepted profile, or an explicit refusal for invalid saved metadata.</summary>
    public WorldGenProfile TerrainProfile => TryGetTerrainProfile(out var profile, out var error)
        ? profile : throw new InvalidDataException(error);
}
