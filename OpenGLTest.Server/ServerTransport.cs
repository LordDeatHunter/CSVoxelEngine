using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using VoxelEngine.Networking;

namespace VoxelEngine.Server;

/// <summary>The server's sockets: the dual-mode TCP listener, the accept thread and one reader thread per connection. The threads only accept and frame inbound bytes onto <see cref="InboundQueue"/>; GameServer.Pump drains it on the main thread.</summary>
internal sealed class ServerTransport
{
    private readonly TcpListener _listener;

    // Reader threads hand inbound frames here; only Pump (main thread) drains it.
    private readonly ConcurrentQueue<Inbound> _inbound = new();

    // Connections the accept thread opened, handed to Pump, which keeps the ones that have not
    // said hello yet in ServerState.Pending so the idle sweep can drop a peer that never does.
    private readonly ConcurrentQueue<ClientConnection> _accepted = new();

    private Thread? _acceptThread;

    private volatile bool _running;
    private int _nextId;

    internal ServerTransport(int port)
    {
        // Dual-mode (IPv6 any, with IPv4 mapped), so [::1]:port works like 127.0.0.1:port.
        _listener = TcpListener.Create(port);
    }

    internal TcpListener Listener => _listener;

    internal bool Running { get => _running; set => _running = value; }

    internal ConcurrentQueue<Inbound> InboundQueue => _inbound;

    internal ConcurrentQueue<ClientConnection> Accepted => _accepted;

    internal void StartAcceptThread()
    {
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "net-accept" };
        _acceptThread.Start();
    }

    internal void JoinAcceptThread()
    {
        _acceptThread?.Join(SaveConfig.WorkerStopJoinTimeoutMs);
        _acceptThread = null;
    }


    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient tcp;
            try
            {
                tcp = _listener.AcceptTcpClient();
            }
            catch (SocketException) when (!_running)
            {
                break;
            }
            catch (SocketException e)
            {
                // A transient accept failure (a peer reset mid-handshake, a resource hiccup) must
                // not end the accept thread, which would leave the server deaf for the session.
                Log.Warn($"net: accept failed: {e.Message}");
                Thread.Sleep(100);
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            ClientConnection client;
            try
            {
                tcp.NoDelay = true;
                // A stalled peer must not freeze the main thread inside a blocking write: past the
                // timeout the send throws and the connection is faulted and removed.
                tcp.SendTimeout = NetworkConfig.NetSendTimeoutMs;
                client = new ClientConnection(tcp, Interlocked.Increment(ref _nextId), _inbound);
                client.Stream.WriteTimeout = NetworkConfig.NetSendTimeoutMs;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException
                                          or System.IO.IOException)
            {
                // The peer went away between the accept and the setup.
                try { tcp.Close(); } catch (SocketException) { }
                continue;
            }

            // Queued before the reader starts, so Pump always knows the connection before its
            // first frame (the idle sweep needs it even if no hello ever comes).
            _accepted.Enqueue(client);
            client.StartReader(() => ReadLoop(client));
        }
    }

    private void ReadLoop(ClientConnection client)
    {
        try
        {
            while (_running)
            {
                var payload = NetFraming.ReadFrame(client.Stream);
                if (payload == null)
                    break;
                client.TouchInbound();
                _inbound.Enqueue(new Inbound(client, payload));
            }
        }
        catch (Exception e) when (e is System.IO.IOException or SocketException or ObjectDisposedException)
        {
        }

        _inbound.Enqueue(new Inbound(client, null));
    }
}
