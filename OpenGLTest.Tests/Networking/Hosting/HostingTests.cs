using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using VoxelEngine.Networking;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking.Hosting;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Networking.Hosting;

/// <summary>
/// Opening a saved world as a server (<see cref="ServerLaunch"/>, shared by the dedicated server and hosting from
/// the game) refuses a world it must not open without leaving anything live or locked, and a world hosted from the
/// game (<see cref="HostedServer"/>) serves its host over loopback and releases the world when it stops.
/// </summary>
[Collection("Worldgen seed")]
public sealed class HostingTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly WorldStore _store;

    public HostingTests()
    {
        TestContent.EnsureLoaded();
        _store = new WorldStore(_root);
    }

    public void Dispose()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AWorldOpenInAnotherSessionIsRefusedWithoutGoingLive()
    {
        var world = _store.Create("Held", WorldSeed.Default);
        var held = new ChunkSaveStore(world.Directory);
        try
        {
            var launch = ServerLaunch.Start(world, 0, new OperatorList());

            Assert.Equal(ServerLaunchFailure.Locked, launch.Failure);
            Assert.Null(launch.World);
            Assert.False(WorldSeed.WorldIsLive);
        }
        finally
        {
            held.Shutdown(Budget);
        }
    }

    [Fact]
    public void AnOldFormatWorldIsRefused()
    {
        var current = _store.Create("Old", WorldSeed.Default);
        var old = new WorldInfo(current.Slug, current.Name, current.Seed, 0, 0, WorldStore.CurrentVersion - 1,
            current.Directory);

        var launch = ServerLaunch.Start(old, 0, new OperatorList());

        Assert.Equal(ServerLaunchFailure.OldFormat, launch.Failure);
        Assert.False(WorldSeed.WorldIsLive);
    }

    [Fact]
    public void APortInUseIsRefusedAndTheWorldIsClosedAgain()
    {
        var world = _store.Create("Busy", WorldSeed.Default);
        var occupant = TcpListener.Create(0);
        occupant.Start();
        try
        {
            var port = ((IPEndPoint)occupant.LocalEndpoint).Port;

            var hosted = HostedServer.TryStart(world, port, "Host", out var error);

            Assert.Null(hosted);
            Assert.Contains(port.ToString(), error);
            Assert.False(WorldSeed.WorldIsLive);
            // The folder's lock was released: the world opens again
            var reopened = new ChunkSaveStore(world.Directory);
            Assert.True(reopened.Shutdown(Budget));
        }
        finally
        {
            occupant.Stop();
        }
    }

    [Fact]
    public void AHostedWorldServesItsHostOverLoopbackAndReleasesTheWorldWhenStopped()
    {
        var world = _store.Create("Hosted", WorldSeed.Default);
        var hosted = HostedServer.TryStart(world, 0, "Host", out var error);
        Assert.True(hosted != null, error);
        Assert.True(WorldSeed.WorldIsLive);

        using var client = new NetworkClient(hosted.LoopbackAddress, "Host", 5.0);
        new Thread(client.Connect) { IsBackground = true }.Start();
        var sw = Stopwatch.StartNew();
        while (client.Status != NetworkStatus.Handshaken && sw.Elapsed < Budget)
        {
            hosted.Tick(0.02);
            Thread.Sleep(5);
        }
        Assert.Equal(NetworkStatus.Handshaken, client.Status);

        hosted.Stop();
        hosted.Stop();

        Assert.True(hosted.Stopped);
        sw.Restart();
        while (client.Status == NetworkStatus.Handshaken && sw.Elapsed < Budget)
            Thread.Sleep(5);
        Assert.Equal(NetworkStatus.Disconnected, client.Status);
        Assert.False(WorldSeed.WorldIsLive);
        var reopened = new ChunkSaveStore(world.Directory);
        Assert.True(reopened.Shutdown(Budget));
    }

    [Fact]
    public void TheLanAddressIsAnUpIPv4OffLoopbackPreferringTheGatewayAdapter()
    {
        var gateway = IPAddress.Parse("192.168.1.20");
        var virtualSwitch = IPAddress.Parse("172.20.0.1");
        var interfaces = new[]
        {
            new NetworkInterfaceInfo(true, true, false, [IPAddress.Loopback]),
            new NetworkInterfaceInfo(false, false, true, [IPAddress.Parse("10.0.0.5")]),
            new NetworkInterfaceInfo(true, false, false, [virtualSwitch, IPAddress.Parse("169.254.3.4")]),
            new NetworkInterfaceInfo(true, false, true, [IPAddress.IPv6Loopback, IPAddress.Parse("169.254.9.9"), gateway]),
        };

        Assert.Equal(gateway, LanAddress.Pick(interfaces));
        // Without the gateway adapter the virtual switch is still a LAN address; link-local never is
        Assert.Equal(virtualSwitch, LanAddress.Pick(interfaces[..3]));
        Assert.Null(LanAddress.Pick([interfaces[0], new NetworkInterfaceInfo(true, false, false, [IPAddress.Parse("169.254.3.4")])]));
    }
}
