using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoxelEngine.Networking;

namespace VoxelEngine.Saves;

/// <summary>
/// The player's saved multiplayer servers, in one JSON file. Mirrors <see cref="WorldStore"/>'s
/// feel (unique case-insensitive names, most recently connected first, a temp-file write so a crash
/// cannot truncate the list) but is a flat file rather than a folder per entry: a server is only a
/// name and an address.
/// <para>
/// Every operation works against the file given to the constructor, so tests use a temp path and
/// only the game uses <see cref="DefaultPath"/>. <see cref="List"/> keeps a best-effort empty
/// result on read failures; <see cref="TryList"/> distinguishes failures so callers can report them.
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

    internal Func<string, string> ReadAllText { get; set; } = File.ReadAllText;

    /// <summary>Every readable server, most recently connected first, ties broken by name.</summary>
    public IReadOnlyList<ServerInfo> List()
    {
        TryList(out var servers);
        return servers;
    }

    /// <summary>Reads the server list, distinguishing an empty list from a root read/archive failure.</summary>
    internal bool TryList(out IReadOnlyList<ServerInfo> servers)
    {
        ServerDto[]? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<ServerDto[]>(ReadAllText(Path), JsonOptions);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            servers = [];
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read saved servers '{Path}': {e.Message}");
            servers = [];
            return false;
        }
        catch (JsonException e)
        {
            // The next mutation writes the list back, which would turn one bad field into an
            // empty file: set the broken file aside instead so the entries can be recovered by hand.
            Log.Warn($"Could not parse saved servers '{Path}': {e.Message}");
            servers = [];
            return SetAsideBrokenFile();
        }

        if (dtos == null)
        {
            servers = [];
            return true;
        }

        var readableServers = new List<ServerInfo>(dtos.Length);
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
            readableServers.Add(new ServerInfo(id, name, dto.Host, dto.Port, dto.LastPlayed ?? 0));
        }

        servers = readableServers
            .OrderByDescending(s => s.LastPlayedUnixMs)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return true;
    }

    /// <summary>Appends a server, its name made unique against the existing ones, and saves.</summary>
    public ServerInfo Add(string? requestedName, string host, int port)
    {
        var servers = ReadForMutation();
        var name = WorldNames.Unique(requestedName, servers.Select(s => s.Name));
        var server = new ServerInfo(Guid.NewGuid().ToString("N"), name, host, port, Clock());
        Write(servers.Append(server));
        return server;
    }

    /// <summary>Removes a server by <see cref="ServerInfo.Id"/> and saves; a missing id is a no-op.</summary>
    public void Delete(ServerInfo server)
    {
        Write(ReadForMutation().Where(s => s.Id != server.Id));
    }

    /// <summary>Stamps a server as connected now and saves.</summary>
    public void TouchLastPlayed(ServerInfo server)
    {
        var servers = ReadForMutation();
        server.LastPlayedUnixMs = Clock();
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

    private IReadOnlyList<ServerInfo> ReadForMutation()
    {
        if (TryList(out var servers))
            return servers;
        throw new IOException($"Could not read saved servers '{Path}'.");
    }

    private bool SetAsideBrokenFile()
    {
        try
        {
            File.Move(Path, Path + ".bak", overwrite: true);
            Log.Warn($"Moved the broken servers file to '{Path}.bak'");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not set aside '{Path}': {e.Message}");
            return false;
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
