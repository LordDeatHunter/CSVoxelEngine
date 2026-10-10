namespace silkgltriangle;

/// <summary>
/// One saved world as <see cref="WorldStore"/> lists it: the metadata in its <c>world.json</c>
/// plus the folder it lives in. <see cref="Slug"/> and <see cref="Directory"/> are fixed at
/// creation; a rename only changes <see cref="Name"/>.
/// </summary>
public sealed class WorldInfo(
    string slug, string name, int seed, long createdUnixMs, long lastPlayedUnixMs, int version, string directory,
    string? sourceName = null)
{
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
}
