using System;
using System.Globalization;

namespace VoxelEngine.Networking;

/// <summary>
/// GL-free, content-free rules for a server address: <c>host</c> or <c>host:port</c>, the format
/// the multiplayer pages store and the client connects with. The port defaults to
/// <see cref="DefaultPort"/> when omitted, so a player can type a bare IP or hostname.
/// <para>
/// A host may be an IPv4 address, a bracketed IPv6 address (<c>[::1]:25566</c>) or a DNS name; the
/// parser only splits the port off and checks the ranges, it never resolves anything. Formatting is
/// the exact inverse, so <c>Parse(x.ToString())</c> round-trips.
/// </para>
/// </summary>
public readonly record struct ServerAddress(string Host, int Port)
{
    /// <summary>The port used when an address omits one.</summary>
    public const int DefaultPort = 25566;

    public const int MinPort = 1;
    public const int MaxPort = 65535;

    /// <summary>Whether the host is non-empty and the port is in range.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Host) && Port is >= MinPort and <= MaxPort;

    /// <summary>
    /// Parses <c>host[:port]</c>. A bracketed IPv6 host keeps its brackets so the port split stays
    /// unambiguous. Returns false (with <paramref name="address"/> at its default) for an empty
    /// host or an out-of-range/non-numeric port, so a bad direct-connect entry is refused instead
    /// of attempted.
    /// </summary>
    public static bool TryParse(string? text, out ServerAddress address)
    {
        address = default;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        string host;
        var port = DefaultPort;
        if (trimmed[0] == '[')
        {
            // [ipv6] or [ipv6]:port
            var close = trimmed.IndexOf(']');
            if (close <= 1)
                return false;
            host = trimmed[..(close + 1)];
            var rest = trimmed[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':' || !TryParsePort(rest[1..], out port))
                    return false;
            }
        }
        else
        {
            var colon = trimmed.LastIndexOf(':');
            // A single colon splits host:port; several (a bare IPv6 such as fe80::1) leave the
            // whole text as the host and use the default port.
            if (colon >= 0 && trimmed.IndexOf(':') == colon)
            {
                host = trimmed[..colon];
                if (!TryParsePort(trimmed[(colon + 1)..], out port))
                    return false;
            }
            else
            {
                host = trimmed;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
            return false;

        address = new ServerAddress(host, port);
        return true;
    }

    /// <summary>The address as <c>host:port</c>, always with the port.</summary>
    public override string ToString() =>
        Port == DefaultPort ? Host : string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
        && port is >= MinPort and <= MaxPort;
}
