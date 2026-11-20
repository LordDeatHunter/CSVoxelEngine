using System.Collections.Generic;

namespace VoxelEngine.Networking.ServerSide;

/// <summary>Pure per-client bounded request and one-active-transfer ownership.</summary>
internal sealed class LodTransferCredit
{
    internal readonly HashSet<(int Level, int Rx, int Rz)> Queued = [];
    internal readonly Dictionary<(int Level, int Rx, int Rz), int> Nonces = [];
    internal bool Available { get; private set; } = true;
    internal int ActiveNonce { get; private set; }
    internal int ActiveGeneration { get; private set; }
    internal (int Level, int Rx, int Rz)? AwaitingAck { get; private set; }
    internal double StartedAt { get; private set; }

    internal bool Request((int Level, int Rx, int Rz) key, int nonce, int max)
    {
        if (nonce <= 0 || AwaitingAck == key ||
            (!Queued.Contains(key) && Queued.Count >= max) ||
            Nonces.GetValueOrDefault(key) == nonce) return false;
        Queued.Add(key);
        Nonces[key] = nonce;
        return true;
    }

    internal bool CancelQueued((int Level, int Rx, int Rz) key, int nonce)
    {
        if (AwaitingAck == key || !Nonces.TryGetValue(key, out var known) || known != nonce)
            return false;
        Queued.Remove(key);
        Nonces.Remove(key);
        return true;
    }

    internal bool Begin((int Level, int Rx, int Rz) key, int nonce, int generation, double now)
    {
        if (!Available || !Queued.Contains(key) || Nonces.GetValueOrDefault(key) != nonce)
            return false;
        Available = false;
        AwaitingAck = key;
        ActiveNonce = nonce;
        ActiveGeneration = generation;
        StartedAt = now;
        return true;
    }

    internal bool Acknowledge((int Level, int Rx, int Rz) key, int nonce,
        int generation, byte status)
    {
        if (AwaitingAck != key || ActiveNonce != nonce ||
            (generation != ActiveGeneration && !(status == LodTransfer.Canceled && generation == 0)))
            return false;
        Queued.Remove(key);
        Nonces.Remove(key);
        Release();
        return true;
    }

    internal bool Expire(double now, double seconds)
    {
        if (Available || now - StartedAt < seconds) return false;
        if (AwaitingAck is { } key) { Queued.Remove(key); Nonces.Remove(key); }
        Release();
        return true;
    }

    internal void Clear()
    {
        Queued.Clear(); Nonces.Clear(); Release();
    }

    private void Release()
    {
        Available = true;
        AwaitingAck = null;
        ActiveNonce = ActiveGeneration = 0;
        StartedAt = 0;
    }
}
