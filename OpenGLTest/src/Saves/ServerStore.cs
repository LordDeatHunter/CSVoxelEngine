using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace silkgltriangle;

/// <summary>
/// The player's saved multiplayer servers, in one JSON file. Mirrors <see cref="WorldStore"/>'s
/// feel (unique case-insensitive names, most recently connected first, a temp-file write so a crash
/// cannot truncate the list) but is a flat file rather than a folder per entry: a server is only a
/// name and an address.
/// <para>
/// Every operation works against the file given to the constructor, so tests use a temp path and
/// only the game uses <see cref="DefaultPath"/>. A missing or broken file lists empty with a
/// warning rather than failing.
/// </para>
/// </summary>
public sealed class ServerStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Where the game keeps its server list (beside the saves folder).</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CSVoxelEngine", "servers.json");

    /// <summary>The list file this store reads and writes.</summary>
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>Replaceable clock (Unix milliseconds) so tests can order servers deterministically.</summary>
    internal Func<long> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Every readable server, most recently connected first, ties broken by name.</summary>
    public IReadOnlyList<ServerInfo> List()
    {
        if (!File.Exists(Path))
            return [];

        ServerDto[]? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<ServerDto[]>(File.ReadAllText(Path), JsonOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read saved servers '{Path}': {e.Message}");
            return [];
        }
        catch (JsonException e)
        {
            // The next Add/Delete writes the list back, which would turn one bad field into an
            // empty file: set the broken file aside instead so the entries can be recovered by hand.
            Log.Warn($"Could not parse saved servers '{Path}': {e.Message}");
            SetAsideBrokenFile();
            return [];
        }

        if (dtos == null)
            return [];

        var servers = new List<ServerInfo>(dtos.Length);
        foreach (var dto in dtos)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Host)
                || dto.Port is not (>= ServerAddress.MinPort and <= ServerAddress.MaxPort))
            {
                Log.Warn($"Skipping saved server with a bad address in '{Path}'");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(dto.Name) ? WorldNames.DefaultName : dto.Name;
            // An entry written without an id gets one derived from its address, so Delete and
            // TouchLastPlayed can match it on every List() rather than a fresh random id each time.
            var id = string.IsNullOrWhiteSpace(dto.Id) ? $"{dto.Host}:{dto.Port}" : dto.Id;
            servers.Add(new ServerInfo(id, name, dto.Host, dto.Port, dto.LastPlayed ?? 0));
        }

        return servers
            .OrderByDescending(s => s.LastPlayedUnixMs)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Appends a server, its name made unique against the existing ones, and saves.</summary>
    public ServerInfo Add(string? requestedName, string host, int port)
    {
        var name = WorldNames.Unique(requestedName, List().Select(s => s.Name));
        var server = new ServerInfo(Guid.NewGuid().ToString("N"), name, host, port, Clock());
        Write([.. List(), server]);
        return server;
    }

    /// <summary>Removes a server by <see cref="ServerInfo.Id"/> and saves; a missing id is a no-op.</summary>
    public void Delete(ServerInfo server)
    {
        Write(List().Where(s => s.Id != server.Id));
    }

    /// <summary>Stamps a server as connected now and saves.</summary>
    public void TouchLastPlayed(ServerInfo server)
    {
        server.LastPlayedUnixMs = Clock();
        var servers = List();
        for (var i = 0; i < servers.Count; i++)
        {
            if (servers[i].Id == server.Id)
            {
                servers[i].LastPlayedUnixMs = server.LastPlayedUnixMs;
                break;
            }
        }
        Write(servers);
    }

    private void SetAsideBrokenFile()
    {
        try
        {
            File.Move(Path, Path + ".bak", overwrite: true);
            Log.Warn($"Moved the broken servers file to '{Path}.bak'");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not set aside '{Path}': {e.Message}");
        }
    }

    private void Write(IEnumerable<ServerInfo> servers)
    {
        var dtos = servers.Select(s => new ServerDto
        {
            Id = s.Id,
            Name = s.Name,
            Host = s.Host,
            Port = s.Port,
            LastPlayed = s.LastPlayedUnixMs,
        }).ToArray();

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dtos, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }

    private sealed class ServerDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; }
        public long? LastPlayed { get; set; }
    }
}
