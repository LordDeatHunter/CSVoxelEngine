namespace silkgltriangle;

/// <summary>
/// One saved multiplayer server as <see cref="ServerStore"/> lists it: the display name the player
/// chose, the host and port it points at, and when it was last connected. <see cref="Id"/> is
/// generated at creation and never changes, so a rename could rekey the same row.
/// </summary>
public sealed class ServerInfo(string id, string name, string host, int port, long lastPlayedUnixMs)
{
    /// <summary>A stable, generated key for this entry; independent of the name.</summary>
    public string Id { get; } = id;

    /// <summary>The display name, unique case-insensitively among the store's servers.</summary>
    public string Name { get; set; } = name;

    /// <summary>The IP address or hostname (never including the port).</summary>
    public string Host { get; } = host;

    /// <summary>The TCP port.</summary>
    public int Port { get; } = port;

    public long LastPlayedUnixMs { get; set; } = lastPlayedUnixMs;

    /// <summary>The <see cref="Host"/> and <see cref="Port"/> as the client connects to them.</summary>
    public ServerAddress Address => new(Host, Port);
}
