using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace silkgltriangle;

/// <summary>Where a connection attempt currently is.</summary>
public enum NetworkStatus
{
    Connecting,
    Handshaken,
    Failed,
    Disconnected,
}

/// <summary>What the server's hello told the client: the world to generate and the starting pose.</summary>
public readonly record struct ServerHelloInfo(string WorldName, int Seed, double Time, Vector3 Spawn, int PlayerId);

/// <summary>
/// A client connection to a <see cref="GameServer"/>: the handshake (client hello out, server hello
/// in) and, after that, framed messages between a reader thread and the main thread. Inbound frames
/// land on <see cref="Inbound"/> and only the main thread drains it and touches the world; outbound
/// sends are lock-guarded. GL-free.
/// </summary>
public sealed class NetworkClient(ServerAddress address, string playerName, double timeoutSeconds) : IDisposable
{
    private readonly object _writeGate = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private volatile bool _running;
    private Thread? _reader;

    // Written by the connect and reader threads, read by the main thread every frame: published
    // through volatile fields (the hello info is boxed so the reference write is atomic). Error and
    // Server are always written before the Status that announces them.
    private volatile NetworkStatus _status = NetworkStatus.Connecting;
    private volatile string? _error;
    private volatile StrongBox<ServerHelloInfo>? _server;

    public ServerAddress Address { get; } = address;
    public string PlayerName { get; } = playerName;
    public NetworkStatus Status => _status;
    public string? Error => _error;
    public ServerHelloInfo? Server => _server?.Value;

    /// <summary>Framed payloads (message id first) read by the reader thread for the main thread.</summary>
    public ConcurrentQueue<byte[]> Inbound { get; } = new();

    /// <summary>
    /// Runs the connect and handshake. Blocks, so call it on a background thread; on success
    /// <see cref="Status"/> is <see cref="NetworkStatus.Handshaken"/> and the reader thread is up.
    /// </summary>
    public void Connect()
    {
        try
        {
            var tcp = new TcpClient { NoDelay = true };
            // Owned from here on, so every failure path below (Fail) disposes it, the connect
            // timeout included: the pending ConnectAsync is abandoned with its socket closed.
            _tcp = tcp;
            if (!tcp.ConnectAsync(Address.Host, Address.Port).Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                Fail($"Timed out connecting to {Address}");
                return;
            }

            _stream = tcp.GetStream();
            // The handshake read is bounded: a server that accepts but never answers fails the
            // connect instead of leaving the connecting screen up forever.
            _stream.ReadTimeout = (int)Math.Min(int.MaxValue, timeoutSeconds * 1000.0);

            using (var hello = new PacketWriter(NetMessage.ClientHello))
            {
                hello.Write(NetFraming.ProtocolVersion);
                hello.Write(PlayerName);
                Send(hello.ToArray());
            }

            var payload = NetFraming.ReadFrame(_stream);
            if (payload == null)
            {
                Fail("Server closed the connection during handshake");
                return;
            }

            using var reader = new PacketReader(payload);
            if (reader.Message != NetMessage.ServerHello)
            {
                Fail($"Unexpected handshake reply {reader.Message}");
                return;
            }

            var version = reader.ReadInt();
            if (version != NetFraming.ProtocolVersion)
            {
                Fail($"Server protocol {version} does not match {NetFraming.ProtocolVersion}");
                return;
            }

            var playerId = reader.ReadInt();
            var worldName = reader.ReadString();
            var seed = reader.ReadInt();
            var time = reader.ReadDouble();
            var spawn = reader.ReadVector3();
            _server = new StrongBox<ServerHelloInfo>(new ServerHelloInfo(worldName, seed, time, spawn, playerId));

            // Play reads block for as long as the server is quiet; the idle sweep and KeepAlive
            // cover a dead peer from here on.
            _stream.ReadTimeout = Timeout.Infinite;
            _running = true;
            // Handshaken before the reader starts, so a server that closes at once cannot have
            // its Disconnected overwritten by a late Handshaken.
            _status = NetworkStatus.Handshaken;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "net-client-read" };
            _reader.Start();
        }
        catch (System.IO.IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            Fail("Timed out waiting for the server's handshake");
        }
        catch (Exception e)
        {
            // Runs on a dedicated thread: any escape would tear the process down, so a refused,
            // unreachable or timed-out server is reported through Status instead.
            Fail(e is AggregateException ? e.InnerException?.Message ?? e.Message : e.Message);
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (_running && _stream != null)
            {
                var payload = NetFraming.ReadFrame(_stream);
                if (payload == null)
                    break;
                Inbound.Enqueue(payload);
            }
        }
        catch (Exception e) when (e is System.IO.IOException or SocketException or ObjectDisposedException)
        {
        }

        // Unconditional: the reader only ever runs after the handshake, so its end is always a
        // disconnect (Handshaken was published before it started).
        _status = NetworkStatus.Disconnected;
    }

    public void Send(byte[] payload)
    {
        var stream = _stream;
        if (stream == null)
            return;
        lock (_writeGate)
        {
            try
            {
                NetFraming.WriteFrame(stream, payload);
            }
            catch (Exception e) when (e is System.IO.IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private void Fail(string message)
    {
        _error = message;
        _status = NetworkStatus.Failed;
        try { _tcp?.Dispose(); } catch (SocketException) { }
    }

    public void Dispose()
    {
        _running = false;
        try { _tcp?.Close(); } catch (SocketException) { }
        _reader?.Join(1000);
        _reader = null;
    }
}
