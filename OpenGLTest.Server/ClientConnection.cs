using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using VoxelEngine.Networking;

namespace VoxelEngine.Server;

/// <summary>
/// One connected socket: its id and name, a lock-guarded framed send, the last time a frame
/// arrived (written by the reader thread, read by the main thread's idle sweep) and the
/// far-LOD regions queued for it.
/// </summary>
internal sealed class ClientConnection(TcpClient tcp, int id, ConcurrentQueue<Inbound> inbound)
{
    private readonly object _writeGate = new();
    private Thread? _reader;
    private long _lastInboundAt = Environment.TickCount64;
    private int _faulted;

    public int Id { get; } = id;
    public TcpClient Tcp { get; } = tcp;
    public NetworkStream Stream { get; } = tcp.GetStream();
    public string Name { get; set; } = "?";
    public bool Hello { get; set; }
    public string Remote { get; } = tcp.Client.RemoteEndPoint?.ToString() ?? "?";

    /// <summary>Set by <see cref="PlayerSessions.RemoveClient"/>; main thread only.</summary>
    public bool Removed { get; set; }

    /// <summary>Far-LOD regions queued for this connection (main thread only).</summary>
    public HashSet<(int Level, int Rx, int Rz)> LodQueued { get; } = [];

    /// <summary><see cref="Environment.TickCount64"/> of the last inbound frame (or the accept).</summary>
    public long LastInboundAt => Volatile.Read(ref _lastInboundAt);

    /// <summary>Reader thread: records that a frame just arrived.</summary>
    public void TouchInbound() => Volatile.Write(ref _lastInboundAt, Environment.TickCount64);

    public void StartReader(Action readLoop)
    {
        _reader = new Thread(() => readLoop()) { IsBackground = true, Name = $"net-read-{Id}" };
        _reader.Start();
    }

    public void Send(byte[] payload)
    {
        if (Volatile.Read(ref _faulted) != 0)
            return;
        lock (_writeGate)
        {
            try
            {
                NetFraming.WriteFrame(Stream, payload);
            }
            catch (Exception e) when (e is System.IO.IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                Fault();
            }
        }
    }

    // A failed (or timed-out) send leaves the stream in an unknown state: stop sending and
    // queue this connection's removal once. Only enqueues, so it is safe while Pump iterates
    // the client table.
    private void Fault()
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
            inbound.Enqueue(new Inbound(this, null));
    }

    public void Close()
    {
        try { Tcp.Close(); } catch (SocketException) { }
    }
}
