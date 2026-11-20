using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using VoxelEngine.Networking.Client;
using VoxelEngine.Networking.Hosting;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
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
    public void UnsupportedSavedTerrainIsRefusedBeforeSeedAndStoreChange()
    {
        var world = _store.Create("Unknown terrain", WorldSeed.Default + 101);
        var path = Path.Combine(world.Directory, WorldStore.MetadataFileName);
        var original = "{\"Name\":\"Unknown terrain\",\"Seed\":" + world.Seed +
            ",\"Version\":" + WorldStore.CurrentVersion + ",\"terrain\":99}";
        File.WriteAllText(path, original);
        var listed = Assert.Single(_store.List());
        WorldSeed.Apply(WorldSeed.Default);
        ServerLaunchResult? launch = null;
        ChunkSaveStore? reopened = null;
        try
        {
            launch = ServerLaunch.Start(listed, 0, new OperatorList());
            Assert.False(launch.Started);
            Assert.Equal("InvalidTerrain", launch.Failure.ToString());
            Assert.Equal(WorldSeed.Default, WorldSeed.Value);
            Assert.False(WorldSeed.WorldIsLive);
            reopened = new ChunkSaveStore(world.Directory);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            if (launch?.Started == true)
            {
                launch.Server!.Dispose();
                launch.World!.SaveAndDispose();
            }
            reopened?.Shutdown(Budget);
        }
    }

    [Fact]
    public void ServerLaunchSelectsTheSavedProfileForNewAndOldWorlds()
    {
        var dynamicWorld = _store.Create("Dynamic", WorldSeed.Default + 1);
        var oldWorld = _store.Create("Old", WorldSeed.Default + 2);
        File.WriteAllText(Path.Combine(oldWorld.Directory, WorldStore.MetadataFileName),
            "{\"Name\":\"Old\",\"Seed\":" + oldWorld.Seed +
            ",\"Version\":" + WorldStore.CurrentVersion + "}");

        foreach (var (world, expected) in new[]
        {
            (dynamicWorld, WorldGenProfile.Dynamic),
            (_store.List().Single(w => w.Slug == oldWorld.Slug), WorldGenProfile.Legacy),
        })
        {
            ServerLaunchResult? launch = null;
            try
            {
                launch = ServerLaunch.Start(world, 0, new OperatorList());
                Assert.True(launch.Started, launch.Message);
                Assert.Equal(expected, WorldSeed.Profile);
                Assert.Equal(world.Seed, WorldSeed.Value);
            }
            finally
            {
                if (launch?.Started == true)
                {
                    launch.Server!.Dispose();
                    Assert.True(launch.World!.SaveAndDispose());
                }
            }
            Assert.False(WorldSeed.WorldIsLive);
            var reopened = new ChunkSaveStore(world.Directory);
            Assert.True(reopened.Shutdown(Budget));
        }
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

            var hosted = HostedServer.TryStart(world, port, "Host", upnp: false, out var error);

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
    public void UnmigratablePlayerSaveRefusesHostAndServerWithoutKeepingTheFolderLocked()
    {
        var world = _store.Create("Exhausted lanterns", WorldSeed.Default);
        var file = Path.Combine(world.Directory, PlayerSaveCodec.FileName);
        var original = """{"Inventory":{"Tools":[{"InstanceId":2147483647,"Name":"block/lantern"}],"Blocks":[{"Name":"block/lantern","Count":1}]}}"""u8.ToArray();
        File.WriteAllBytes(file, original);
        HostedServer? hosted = null;
        ServerLaunchResult? launch = null;
        ChunkSaveStore? reopened = null;
        try
        {
            hosted = HostedServer.TryStart(world, 0, "Host", upnp: false, out var error);
            Assert.Null(hosted);
            Assert.Contains("lantern", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(WorldSeed.WorldIsLive);
            reopened = new ChunkSaveStore(world.Directory);
            Assert.True(reopened.Shutdown(Budget));
            reopened = null;

            launch = ServerLaunch.Start(world, 0, new OperatorList());
            Assert.False(launch.Started);
            Assert.Equal(ServerLaunchFailure.InvalidSave, launch.Failure);
            Assert.False(WorldSeed.WorldIsLive);
            reopened = new ChunkSaveStore(world.Directory);
            Assert.Equal(original, reopened.ReadFile(PlayerSaveCodec.FileName));
            Assert.False(File.Exists(file + ".bad"));
        }
        finally
        {
            hosted?.Stop();
            if (launch?.Started == true)
            {
                launch.Server!.Dispose();
                launch.World!.SaveAndDispose();
            }
            reopened?.Shutdown(Budget);
        }
    }

    [Fact]
    public void AHostedWorldServesItsHostOverLoopbackAndReleasesTheWorldWhenStopped()
    {
        var world = _store.Create("Hosted", WorldSeed.Default);
        var hosted = HostedServer.TryStart(world, 0, "Host", upnp: false, out var error);
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
    public void SequentialServerLaunchesKeepSavedOperatorsWithTheirWorld()
    {
        var worldA = _store.Create("Operators A", WorldSeed.Default);
        var worldB = _store.Create("Operators B", WorldSeed.Default);
        var savedOperators = new OperatorList();
        savedOperators.Add("Alice");
        File.WriteAllBytes(Path.Combine(worldA.Directory, OperatorList.FileName), savedOperators.ToJson());

        var baseOperators = new OperatorList();
        baseOperators.Add("BaseOp");
        ServerLaunchResult? first = null;
        ServerLaunchResult? second = null;
        try
        {
            first = ServerLaunch.Start(worldA, 0, baseOperators);
            Assert.True(first.Started, first.Message);
            Assert.True(first.Server!.IsOperator("BaseOp"));
            Assert.True(first.Server.IsOperator("Alice"));

            first.Server.Dispose();
            Assert.True(first.World!.SaveAndDispose());
            Assert.False(WorldSeed.WorldIsLive);
            var reopened = new ChunkSaveStore(worldA.Directory);
            Assert.True(reopened.Shutdown(Budget));
            first = null;

            second = ServerLaunch.Start(worldB, 0, baseOperators);
            Assert.True(second.Started, second.Message);
            Assert.False(second.Server!.IsOperator("Alice"));
            Assert.True(second.Server.IsOperator("BaseOp"));
            Assert.Equal(new[] { "BaseOp" }, baseOperators.Names);
        }
        finally
        {
            if (second?.Server is { } secondServer)
            {
                secondServer.Dispose();
                second.World!.SaveAndDispose();
            }
            if (first?.Server is { } firstServer)
            {
                firstServer.Dispose();
                first.World!.SaveAndDispose();
            }
        }
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
