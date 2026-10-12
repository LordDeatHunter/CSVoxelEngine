using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoxelEngine.Saves;

/// <summary>
/// The saved worlds under one root directory: one folder per world (named by its
/// <see cref="WorldNames.Slug"/>), each holding a <see cref="MetadataFileName"/> and a
/// <see cref="ChunksDirectoryName"/> subfolder. Every operation works against the root given to
/// the constructor, so tests use a temp directory and only the game uses <see cref="DefaultRoot"/>.
/// <para>
/// Display names stay unique case-insensitively (<see cref="WorldNames.Unique"/>); the folder is
/// fixed at creation and never follows a rename. A folder whose metadata is missing or broken is
/// skipped with a warning rather than failing the whole listing. Metadata writes go to a
/// temporary file first and are moved over the old one, so a crash mid-write never leaves a
/// truncated <c>world.json</c>. The optional <c>source</c> field (the dedicated server's world
/// source, <see cref="WorldInfo.SourceName"/>) is carried through every rewrite and never written
/// for a world whose file has none.
/// </para>
/// </summary>
public sealed class WorldStore(string root)
{
    public const int CurrentVersion = 2;
    public const string MetadataFileName = "world.json";
    public const string ChunksDirectoryName = "chunks";

    /// <summary>The subfolder a hosted world keeps one file per multiplayer player in
    /// (<see cref="ServerPlayerSaves"/>), created on its first write.</summary>
    public const string PlayersDirectoryName = "players";
    private const string ChunkFileExtension = ".bin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Where the game keeps its worlds (outside the repo, beside the settings file).</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CSVoxelEngine", "saves");

    /// <summary>The root directory this store lists and creates worlds in.</summary>
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>Replaceable clock (Unix milliseconds) so tests can order worlds deterministically.</summary>
    internal Func<long> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Every readable world, most recently played first, ties broken by name.
    /// An absent root lists empty.</summary>
    public IReadOnlyList<WorldInfo> List()
    {
        if (!Directory.Exists(Root))
            return [];

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not list saved worlds in '{Root}': {e.Message}");
            return [];
        }

        var worlds = new List<WorldInfo>(folders.Length);
        foreach (var folder in folders)
        {
            if (TryRead(folder) is { } world)
                worlds.Add(world);
        }

        return worlds
            .OrderByDescending(w => w.LastPlayedUnixMs)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Creates a new world folder with its chunks subfolder and metadata. The name is
    /// made unique against the existing worlds and the slug against the existing folders.</summary>
    public WorldInfo Create(string? requestedName, int seed)
    {
        var name = WorldNames.Unique(requestedName, List().Select(w => w.Name));
        var slug = WorldNames.UniqueSlug(name, s => Directory.Exists(Path.Combine(Root, s)));
        var directory = Path.Combine(Root, slug);
        Directory.CreateDirectory(Path.Combine(directory, ChunksDirectoryName));

        var now = Clock();
        var world = new WorldInfo(slug, name, seed, now, now, CurrentVersion, directory);
        Write(world);
        return world;
    }

    /// <summary>Renames a world, unique against every OTHER world's name. The folder is untouched.</summary>
    public void Rename(WorldInfo world, string? requestedName)
    {
        var others = List().Where(w => w.Slug != world.Slug).Select(w => w.Name);
        world.Name = WorldNames.Unique(requestedName, others);
        Write(world);
    }

    /// <summary>Deletes a world's whole folder. Refuses (ArgumentException) any directory that is
    /// not strictly inside this store's root, so a forged <see cref="WorldInfo"/> can never
    /// delete anything else.</summary>
    public void Delete(WorldInfo world)
    {
        var rootPrefix = Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(world.Directory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(rootPrefix, comparison) || target.Length <= rootPrefix.Length)
            throw new ArgumentException($"World directory '{world.Directory}' is not inside the saves root '{Root}'.", nameof(world));

        Directory.Delete(target, recursive: true);
    }

    /// <summary>Stamps the world as played now and rewrites its metadata.</summary>
    public void TouchLastPlayed(WorldInfo world)
    {
        world.LastPlayedUnixMs = Clock();
        Write(world);
    }

    /// <summary>The file name one chunk is saved under inside the chunks folder.</summary>
    public static string ChunkFileName(int cx, int cy, int cz) =>
        string.Create(CultureInfo.InvariantCulture, $"{cx}_{cy}_{cz}{ChunkFileExtension}");

    /// <summary>Parses a <see cref="ChunkFileName"/> back into its chunk coordinates; any other
    /// name (a temp file, a stray file, the wrong number of parts) returns false.</summary>
    public static bool TryParseChunkFileName(string fileName, out int cx, out int cy, out int cz)
    {
        cx = cy = cz = 0;
        if (!fileName.EndsWith(ChunkFileExtension, StringComparison.Ordinal))
            return false;

        var parts = fileName[..^ChunkFileExtension.Length].Split('_');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out cx)
            && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out cy)
            && int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out cz);
    }

    private static WorldInfo? TryRead(string folder)
    {
        var path = Path.Combine(folder, MetadataFileName);
        WorldDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WorldDto>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Skipping saved world folder '{folder}': {e.Message}");
            return null;
        }

        if (dto == null)
        {
            Log.Warn($"Skipping saved world folder '{folder}': empty {MetadataFileName}");
            return null;
        }

        var created = dto.Created ?? 0;
        return new WorldInfo(
            Path.GetFileName(folder),
            string.IsNullOrWhiteSpace(dto.Name) ? WorldNames.DefaultName : dto.Name,
            // A seedless world.json must not inherit whatever seed the last world applied
            dto.Seed ?? WorldSeed.Default,
            created,
            dto.LastPlayed ?? created,
            dto.Version ?? 0,
            Path.GetFullPath(folder),
            dto.Source);
    }

    private static void Write(WorldInfo world)
    {
        var dto = new WorldDto
        {
            Name = world.Name,
            Seed = world.Seed,
            Created = world.CreatedUnixMs,
            LastPlayed = world.LastPlayedUnixMs,
            Version = world.Version,
            // Null stays null and is not written, so only a world whose file named a source keeps one
            Source = world.SourceName,
        };
        var path = Path.Combine(world.Directory, MetadataFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    // Every field nullable so a hand-edited or older file degrades to defaults field by field.
    private sealed class WorldDto
    {
        public string? Name { get; set; }
        public int? Seed { get; set; }
        public long? Created { get; set; }
        public long? LastPlayed { get; set; }
        public int? Version { get; set; }

        // The world source the dedicated server builds the world from (WorldSources); absent in
        // every file the game writes, which reads as generated
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Source { get; set; }
    }
}
