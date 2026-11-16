using System;
using System.Threading;
using System.Threading.Tasks;
using Mono.Nat;

namespace VoxelEngine.Networking.Hosting;

/// <summary>Where a <see cref="UpnpMapping"/> stands.</summary>
public enum UpnpState
{
    Discovering,
    Active,
    Failed,
}

/// <summary>
/// An opt-in router port mapping for a hosted world (UPnP or NAT-PMP, through Mono.Nat), so players outside the
/// local network can join. It runs entirely in the background and never holds up hosting: discovery has a hard
/// deadline, and a failure only shows on the pause menu. It maps TCP on the server's port with a description unique
/// to this session, renews the lease while it lives, and on <see cref="Dispose"/> deletes only a mapping it made and
/// can read back as its own; a mapping someone else holds on the port is never touched. The game reads
/// <see cref="State"/>, <see cref="ExternalAddress"/> and <see cref="Error"/> from the main thread; the background
/// task publishes them through <see cref="Version"/>.
/// </summary>
public sealed class UpnpMapping : IDisposable
{
    private static readonly TimeSpan DiscoveryDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(5);
    private const int LeaseSeconds = 7200;
    private static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(LeaseSeconds / 2);

    private readonly int _port;
    private readonly string _description;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private INatDevice? _device;
    private Mapping? _created;
    private volatile int _version;

    private UpnpMapping(int port)
    {
        _port = port;
        // A per-session nonce, so a readback can tell this session's mapping from any other on the port
        _description = $"CSVoxelEngine {Guid.NewGuid():N}";
        _run = Task.Run(RunAsync);
    }

    /// <summary>Starts mapping <paramref name="port"/> in the background.</summary>
    public static UpnpMapping Start(int port) => new(port);

    public UpnpState State { get; private set; } = UpnpState.Discovering;

    /// <summary>The router's public address, once <see cref="UpnpState.Active"/>.</summary>
    public string? ExternalAddress { get; private set; }

    /// <summary>Why the mapping failed, once <see cref="UpnpState.Failed"/>.</summary>
    public string? Error { get; private set; }

    /// <summary>Bumped after every change of <see cref="State"/>, so a reader rebuilds its text only then.</summary>
    public int Version => _version;

    /// <summary>The pause menu's line for this mapping.</summary>
    public string StatusLine => State switch
    {
        UpnpState.Active => $"UPnP: open at {ExternalAddress}:{_port}",
        UpnpState.Failed => $"UPnP failed: {Error}",
        _ => "UPnP: asking the router...",
    };

    private async Task RunAsync()
    {
        try
        {
            var device = await DiscoverAsync();
            if (device == null)
            {
                Fail("no router answered");
                return;
            }
            _device = device;

            if (await ReadMappingAsync(device) is { } existing && existing.Description != _description)
            {
                Fail($"port {_port} is already forwarded by another device");
                return;
            }

            var mapping = new Mapping(Protocol.Tcp, _port, _port, LeaseSeconds, _description);
            await device.CreatePortMapAsync(mapping).WaitAsync(CallDeadline, _stop.Token);
            _created = mapping;
            ExternalAddress = (await device.GetExternalIPAsync().WaitAsync(CallDeadline, _stop.Token)).ToString();
            Publish(UpnpState.Active);

            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(RenewEvery, _stop.Token);
                await device.CreatePortMapAsync(mapping).WaitAsync(CallDeadline, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is MappingException or TimeoutException or System.Net.Sockets.SocketException)
        {
            Fail(e.Message);
        }
    }

    private async Task<INatDevice?> DiscoverAsync()
    {
        var found = new TaskCompletionSource<INatDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFound(object? sender, DeviceEventArgs args) => found.TrySetResult(args.Device);
        NatUtility.DeviceFound += OnFound;
        try
        {
            NatUtility.StartDiscovery(NatProtocol.Upnp, NatProtocol.Pmp);
            var first = await Task.WhenAny(found.Task, Task.Delay(DiscoveryDeadline, _stop.Token));
            return first == found.Task ? found.Task.Result : null;
        }
        finally
        {
            NatUtility.DeviceFound -= OnFound;
            NatUtility.StopDiscovery();
        }
    }

    // The mapping the router holds on the port, or null when it holds none
    private async Task<Mapping?> ReadMappingAsync(INatDevice device)
    {
        try
        {
            return await device.GetSpecificMappingAsync(Protocol.Tcp, _port).WaitAsync(CallDeadline);
        }
        catch (MappingException)
        {
            return null;
        }
    }

    private void Fail(string reason)
    {
        Error = reason;
        Publish(UpnpState.Failed);
        Log.Warn($"UPnP: {reason}");
    }

    private void Publish(UpnpState state)
    {
        State = state;
        Interlocked.Increment(ref _version);
    }

    /// <summary>Stops renewing and deletes the mapping if it is still this session's (bounded: a silent router
    /// leaves the lease to expire on its own).</summary>
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _run.Wait(CallDeadline);
            if (_device is not { } device || _created is not { } created) return;
            var read = ReadMappingAsync(device);
            if (read.Wait(CallDeadline) && read.Result is { } held && held.Description == _description)
                device.DeletePortMapAsync(created).Wait(CallDeadline);
        }
        catch (AggregateException e)
        {
            Log.Warn($"UPnP: could not remove the port mapping: {e.InnerException?.Message}");
        }
        _stop.Dispose();
    }
}
