using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VoxelEngine.Networking.Hosting;

/// <summary>One network interface as <see cref="LanAddress.Pick"/> weighs it.</summary>
public readonly record struct NetworkInterfaceInfo(bool Up, bool Loopback, bool HasGateway, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// The address other players on the same network join a hosted world at: an IPv4 address of an interface that is
/// up and not loopback, preferring one with a default gateway (the real LAN adapter over a virtual switch) and a
/// private range over a public one; never link-local (169.254.x.x, an adapter with no DHCP answer). GL-free.
/// </summary>
public static class LanAddress
{
    /// <summary>This machine's LAN address, or null when it has none (no network, only loopback).</summary>
    public static IPAddress? Find()
    {
        try
        {
            return Pick(NetworkInterface.GetAllNetworkInterfaces().Select(Describe));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>The best LAN address among <paramref name="interfaces"/>, or null.</summary>
    public static IPAddress? Pick(IEnumerable<NetworkInterfaceInfo> interfaces)
    {
        IPAddress? best = null;
        var bestRank = -1;
        foreach (var nic in interfaces)
        {
            if (!nic.Up || nic.Loopback) continue;
            foreach (var address in nic.Addresses)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || IsLinkLocal(address))
                    continue;
                var rank = (nic.HasGateway ? 2 : 0) + (IsPrivate(address) ? 1 : 0);
                if (rank <= bestRank) continue;
                best = address;
                bestRank = rank;
            }
        }
        return best;
    }

    private static NetworkInterfaceInfo Describe(NetworkInterface nic)
    {
        var properties = nic.GetIPProperties();
        return new NetworkInterfaceInfo(
            nic.OperationalStatus == OperationalStatus.Up,
            nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
            properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                 && !g.Address.Equals(IPAddress.Any)),
            properties.UnicastAddresses.Select(u => u.Address).ToList());
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    // 10/8, 172.16/12 and 192.168/16 (RFC 1918)
    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
