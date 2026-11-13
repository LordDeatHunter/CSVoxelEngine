using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Config;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Stumps;

/// <summary>
/// Block-entity registry for tree stumps (<see cref="BlockType.IsStump"/>): the log a player stood on one, free-form
/// on its top rather than on the grid. Authoritative state (singleplayer and the server tick it; a client's copy is
/// replaced by the station snapshots and only animates the wobble). A log whose stump block is gone is dropped as an
/// item and forgotten; an unloaded cell freezes until its chunk is back. Main thread only.
/// </summary>
public sealed class StumpManager
{
    private readonly Dictionary<(int X, int Y, int Z), StumpLog> _logs = new();
    private readonly List<(int X, int Y, int Z)> _stale = [];

    /// <summary>Whether any stump holds a log, for render early-out and the station broadcast.</summary>
    public bool HasAny => _logs.Count > 0;

    /// <summary>The log on a stump cell, or null when it holds none.</summary>
    public StumpLog? LogAt(int x, int y, int z) => _logs.GetValueOrDefault((x, y, z));

    /// <summary>Whether a block is a log a stump takes: tree wood whose species leaves a stump (a cactus is not a log), or a
    /// block that declares <c>stumpLog</c> (a hollow log).</summary>
    public static bool IsLog(ushort blockId) =>
        blockId != 0 && BlockType.ById[blockId] is { } block && (block.StumpId != 0 || block.StandsOnStump);

    /// <summary>
    /// Stands <paramref name="logId"/> on the empty stump at the cell, where the ray from <paramref name="eye"/> along
    /// <paramref name="direction"/> meets its top (clamped to stay on it; the middle when the ray looks up), turned by the
    /// aim's yaw. False for anything but a log, a cell that is no stump and a stump already holding one.
    /// </summary>
    public bool TryPlace(ChunkManager chunks, int x, int y, int z, ushort logId, Vector3 eye, Vector3 direction)
    {
        if (!IsLog(logId) || _logs.ContainsKey((x, y, z)) || !IsStumpAt(chunks, x, y, z)) return false;

        var offsetX = 0f;
        var offsetZ = 0f;
        if (direction.Y < -0.01f)
        {
            var t = (y + StumpConfig.StumpTop - eye.Y) / direction.Y;
            offsetX = eye.X + direction.X * t - (x + 0.5f);
            offsetZ = eye.Z + direction.Z * t - (z + 0.5f);
        }

        _logs[(x, y, z)] = new StumpLog
        {
            LogId = logId,
            OffsetX = Clamp(offsetX),
            OffsetZ = Clamp(offsetZ),
            Yaw = MathF.Atan2(direction.X, direction.Z),
        };
        return true;
    }

    /// <summary>Takes the log off a stump and returns its block id, or 0 when there was none.</summary>
    public ushort TryTake(int x, int y, int z) =>
        _logs.Remove((x, y, z), out var log) ? log.LogId : (ushort)0;

    /// <summary>Starts a wobble on the stump's log. False when the stump holds none.</summary>
    public bool Wobble(int x, int y, int z)
    {
        if (!_logs.TryGetValue((x, y, z), out var log)) return false;
        log.WobbleLeft = StumpConfig.WobbleSeconds;
        return true;
    }

    /// <summary>Drops a broken stump's log where the block was and forgets the cell.</summary>
    public void NotifyBlockRemoved(int x, int y, int z, DropManager drops)
    {
        if (_logs.Remove((x, y, z), out var log))
            drops.Spawn(log.LogId, new Vector3(x, y, z));
    }

    /// <summary>Every log with its cell, for the render pass.</summary>
    public IEnumerable<(Vector3 Cell, StumpLog Log)> View()
    {
        foreach (var ((x, y, z), log) in _logs)
            yield return (new Vector3(x, y, z), log);
    }

    /// <summary>Rings the wobbles down and drops the log of any cell that stopped being a stump (a blast, a replacement).</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        if (deltaTime <= 0f || _logs.Count == 0) return;
        _stale.Clear();
        foreach (var (key, log) in _logs)
        {
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;
            if (!IsStumpAt(chunks, key.X, key.Y, key.Z))
            {
                _stale.Add(key);
                continue;
            }
            log.WobbleLeft = MathF.Max(0f, log.WobbleLeft - deltaTime);
        }
        foreach (var key in _stale)
            NotifyBlockRemoved(key.X, key.Y, key.Z, drops);
    }

    /// <summary>A multiplayer client's frame: the wobbles ring down between the server's snapshots.</summary>
    public void Animate(float deltaTime)
    {
        if (deltaTime <= 0f) return;
        foreach (var log in _logs.Values)
            log.WobbleLeft = MathF.Max(0f, log.WobbleLeft - deltaTime);
    }

    private static bool IsStumpAt(ChunkManager chunks, int x, int y, int z) =>
        chunks.TryGetCell(x, y, z, out var id, out _) && BlockType.ById[id]?.IsStump == true;

    /// <summary>A stored blow count kept below the log's split, so a bad value can never show a log already split.</summary>
    private static int ClampHits(ushort logId, int hits) =>
        Math.Clamp(hits, 0, Math.Max(0, (BlockType.ById[logId]?.SplitHits ?? 1) - 1));

    private static float Clamp(float offset) =>
        float.IsFinite(offset) ? Math.Clamp(offset, -StumpConfig.MaxOffset, StumpConfig.MaxOffset) : 0f;

    /// <summary>Every log by content name, for <c>stations.json</c> (at most <see cref="StationNetCodec.MaxEntries"/>).</summary>
    internal List<StationSaveData.StumpData> Capture() =>
        _logs.Take(StationNetCodec.MaxEntries).Select(entry => new StationSaveData.StumpData
        {
            Cell = [entry.Key.X, entry.Key.Y, entry.Key.Z],
            Log = SaveNames.NameOf(entry.Value.LogId),
            OffsetX = entry.Value.OffsetX,
            OffsetZ = entry.Value.OffsetZ,
            Yaw = entry.Value.Yaw,
            Hits = entry.Value.SplitHits,
        }).ToList();

    /// <summary>Adds the logs saved by <see cref="Capture"/> to an empty registry. An entry with a bad cell, a log that no
    /// longer resolves or that is no log any more is dropped; returns how many were.</summary>
    internal int Restore(IReadOnlyList<StationSaveData.StumpData>? saved)
    {
        var skipped = 0;
        if (saved == null) return 0;
        foreach (var data in saved.Take(StationNetCodec.MaxEntries))
        {
            if (!StationSaveCodec.TryCell(data.Cell, out var key)
                || data.Log == null || !SaveNames.TryResolve(data.Log, out var logId) || !IsLog(logId))
            {
                skipped++;
                continue;
            }
            _logs[key] = new StumpLog
            {
                LogId = logId,
                OffsetX = Clamp(data.OffsetX),
                OffsetZ = Clamp(data.OffsetZ),
                Yaw = float.IsFinite(data.Yaw) ? data.Yaw : 0f,
                SplitHits = ClampHits(logId, data.Hits),
            };
        }
        return skipped;
    }

    /// <summary>Writes every log (cell, log, spot, turn, wobble left) for the station broadcast.</summary>
    internal void WriteNet(PacketWriter writer)
    {
        // At most what a client accepts (StationNetCodec.MaxEntries)
        var left = Math.Min(_logs.Count, StationNetCodec.MaxEntries);
        writer.Write(left);
        foreach (var ((x, y, z), log) in _logs)
        {
            if (left-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.WriteId(log.LogId);
            writer.Write(log.OffsetX);
            writer.Write(log.OffsetZ);
            writer.Write(log.Yaw);
            writer.Write(log.WobbleLeft);
            writer.Write((byte)log.SplitHits);
        }
    }

    /// <summary>
    /// Replaces the logs with a snapshot written by <see cref="WriteNet"/>: a client's display-only mirror. A wobble this
    /// client is already ringing keeps its own clock unless the server started a fresh one, so the 10 Hz snapshots never
    /// yank a swing backwards by the latency.
    /// </summary>
    internal void ReadNet(PacketReader reader)
    {
        var count = StationNetCodec.ReadCount(reader);
        _stale.Clear();
        _stale.AddRange(_logs.Keys);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var logId = BlockTables.RequireAssigned(reader.ReadId());
            var offsetX = StationNetCodec.ReadFinite(reader);
            var offsetZ = StationNetCodec.ReadFinite(reader);
            var yaw = StationNetCodec.ReadFinite(reader);
            var wobble = Math.Clamp(StationNetCodec.ReadFinite(reader), 0f, StumpConfig.WobbleSeconds);
            var hits = ClampHits(logId, reader.ReadByte());
            _stale.Remove(key);
            if (!_logs.TryGetValue(key, out var log))
            {
                log = new StumpLog();
                _logs[key] = log;
            }
            else if (wobble <= log.WobbleLeft)
            {
                wobble = log.WobbleLeft;
            }
            log.LogId = logId;
            log.OffsetX = offsetX;
            log.OffsetZ = offsetZ;
            log.Yaw = yaw;
            log.WobbleLeft = wobble;
            log.SplitHits = hits;
        }
        foreach (var key in _stale)
            _logs.Remove(key);
        _stale.Clear();
    }
}
