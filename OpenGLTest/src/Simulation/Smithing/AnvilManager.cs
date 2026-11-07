using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Config;
using VoxelEngine.Content.Stations;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>What one hammer hit on an anvil did.</summary>
public enum AnvilHitKind
{
    /// <summary>Nothing happened: no anvil, nothing on it, or the last hit was too recent.</summary>
    Refused,

    /// <summary>The hit landed and the work is not done yet.</summary>
    Struck,

    /// <summary>The last hit landed: the piece became its result and its slag.</summary>
    Finished,
}

/// <summary>The outcome of <see cref="AnvilManager.TryHit"/>; the ids and counts are only meaningful for
/// <see cref="AnvilHitKind.Finished"/>.</summary>
public readonly record struct AnvilHit(AnvilHitKind Kind, byte ResultId = 0, int ResultCount = 0, byte SlagId = 0,
    int SlagCount = 0);

/// <summary>
/// Block-entity registry for anvils, keyed by the anvil's cell. An anvil holds at most one piece of its profile's
/// input (a bloom, laid on with right-click) and the hammer works it: every <see cref="TryHit"/> counts, with a gap
/// the hammer sets between two hits (<see cref="State.CooldownLeft"/> runs down in <see cref="Update"/>, so the
/// pace is judged by the simulation and not by a client's clock), and the hit that reaches
/// <see cref="AnvilProfile.Hits"/> turns the piece into its result and its slag. Main thread only; saved with the
/// world and mirrored to clients like the other stations. An entry whose block is gone is swept and its piece handed
/// back as a drop.
/// </summary>
public sealed class AnvilManager
{
    /// <summary>Live state of one anvil.</summary>
    public sealed class State
    {
        /// <summary>Block id of the anvil this entry was registered with.</summary>
        public byte BlockId;

        /// <summary>How this anvil behaves, from <see cref="BlockId"/>; never saved or sent.</summary>
        public AnvilProfile Profile = AnvilProfile.Default;

        /// <summary>Whether a piece lies on the anvil.</summary>
        public bool HasPiece;

        /// <summary>Hits the piece has taken.</summary>
        public int Hits;

        /// <summary>Seconds until the next hit counts.</summary>
        public float CooldownLeft;
    }

    private readonly Dictionary<(int X, int Y, int Z), State> _anvils = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();

    /// <summary>Whether any anvil is tracked.</summary>
    public bool HasAny => _anvils.Count > 0;

    /// <summary>Registers an anvil (or refreshes the block id of one already known).</summary>
    public void Ensure(Vector3 cell, byte blockId)
    {
        var key = KeyOf(cell);
        if (!_anvils.TryGetValue(key, out var state))
        {
            state = new State();
            _anvils[key] = state;
        }
        state.BlockId = blockId;
        state.Profile = ProfileOf(blockId);
    }

    /// <summary>The anvil profile of block <paramref name="blockId"/>, or <see cref="AnvilProfile.Default"/> when it
    /// is not (or no longer) an anvil.</summary>
    public static AnvilProfile ProfileOf(byte blockId) => BlockType.ById[blockId]?.Anvil ?? AnvilProfile.Default;

    /// <summary>Live state of the anvil at this cell, or null when none is tracked.</summary>
    public State? StateAt(int x, int y, int z) => _anvils.GetValueOrDefault((x, y, z));

    /// <summary>Every tracked anvil, for the render pass.</summary>
    public IEnumerable<(Vector3 Cell, State State)> View()
    {
        foreach (var (key, state) in _anvils)
            yield return (new Vector3(key.X, key.Y, key.Z), state);
    }

    /// <summary>Lays one <paramref name="item"/> on an empty anvil. Refuses anything but the profile's input and an
    /// anvil that already holds a piece.</summary>
    public bool TryPlace(Vector3 cell, byte item)
    {
        if (!_anvils.TryGetValue(KeyOf(cell), out var state) || state.HasPiece) return false;
        if (item == 0 || item != state.Profile.InputId) return false;
        state.HasPiece = true;
        state.Hits = 0;
        state.CooldownLeft = 0f;
        return true;
    }

    /// <summary>Takes the piece back off the anvil, however many hits it took (they are lost).</summary>
    public bool TryTake(Vector3 cell, out byte item)
    {
        item = 0;
        if (!_anvils.TryGetValue(KeyOf(cell), out var state) || !state.HasPiece) return false;
        item = state.Profile.InputId;
        state.HasPiece = false;
        state.Hits = 0;
        return true;
    }

    /// <summary>
    /// One hammer hit. Refused unless a piece lies on the anvil and the gap since the last hit has run down to within
    /// <paramref name="slackSeconds"/> (network jitter on a server; 0 in singleplayer). A hit that lands starts a new
    /// gap of <paramref name="gapSeconds"/> (the hammer's own pace); the one that reaches the profile's hit count
    /// finishes the piece, clears the anvil and reports the result and slag to hand out.
    /// </summary>
    public AnvilHit TryHit(Vector3 cell, float gapSeconds, float slackSeconds)
    {
        if (!_anvils.TryGetValue(KeyOf(cell), out var state) || !state.HasPiece) return default;
        if (state.CooldownLeft > slackSeconds) return default;

        state.CooldownLeft = gapSeconds;
        state.Hits++;
        if (state.Hits < state.Profile.Hits)
            return new AnvilHit(AnvilHitKind.Struck);

        state.HasPiece = false;
        state.Hits = 0;
        return new AnvilHit(AnvilHitKind.Finished, state.Profile.ResultId, BloomeryConfig.IronPerBloom,
            state.Profile.SlagId, BloomeryConfig.SlagPerBloom);
    }

    /// <summary>
    /// Runs the hit gaps down and sweeps anvils whose block is gone (broken, blasted or replaced): the piece lying on
    /// one drops where it stood. An anvil in an unloaded chunk is left alone.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        if (deltaTime <= 0f || _anvils.Count == 0) return;
        _stale.Clear();
        foreach (var (key, state) in _anvils)
        {
            state.CooldownLeft = MathF.Max(0f, state.CooldownLeft - deltaTime);
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;
            if (!chunks.TryGetCell(key.X, key.Y, key.Z, out var id, out _) || id != state.BlockId)
                _stale.Add(key);
        }

        foreach (var key in _stale)
        {
            if (!_anvils.Remove(key, out var state)) continue;
            if (state.HasPiece)
                drops.Spawn(state.Profile.InputId, new Vector3(key.X, key.Y, key.Z));
        }
    }

    private static (int X, int Y, int Z) KeyOf(Vector3 cell) => VoxelCoords.BlockCellOf(cell);

    /// <summary>Every tracked anvil by content name, for <c>stations.json</c>.</summary>
    internal List<StationSaveData.AnvilData> Capture()
    {
        var result = new List<StationSaveData.AnvilData>();
        foreach (var ((x, y, z), state) in _anvils)
        {
            if (result.Count >= StationNetCodec.MaxEntries) break;
            if (!state.HasPiece || SaveNames.NameOf(state.BlockId) is not { } block) continue;
            result.Add(new StationSaveData.AnvilData
            {
                Cell = [x, y, z],
                Block = block,
                Hits = state.Hits,
                Cooldown = state.CooldownLeft,
            });
        }
        return result;
    }

    /// <summary>Adds the anvils saved by <see cref="Capture"/> (only ones holding a piece are saved). An entry whose
    /// block no longer resolves or whose numbers are out of range is dropped. Returns how many were dropped.</summary>
    internal int Restore(IReadOnlyList<StationSaveData.AnvilData>? saved)
    {
        var skipped = 0;
        if (saved == null) return 0;
        foreach (var data in saved.Take(StationNetCodec.MaxEntries))
        {
            if (!StationSaveCodec.TryCell(data.Cell, out var key)
                || !SaveNames.TryResolve(data.Block, out var block)
                || data.Hits < 0 || data.Hits >= ProfileOf(block).Hits
                || !float.IsFinite(data.Cooldown))
            {
                skipped++;
                continue;
            }
            _anvils[key] = new State
            {
                BlockId = block,
                Profile = ProfileOf(block),
                HasPiece = true,
                Hits = data.Hits,
                CooldownLeft = Math.Clamp(data.Cooldown, 0f, 60f),
            };
        }
        return skipped;
    }

    /// <summary>Writes every tracked anvil, for the station broadcast.</summary>
    internal void WriteNet(PacketWriter writer)
    {
        var left = Math.Min(_anvils.Count, StationNetCodec.MaxEntries);
        writer.Write(left);
        foreach (var ((x, y, z), state) in _anvils)
        {
            if (left-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.Write(state.BlockId);
            writer.Write(state.HasPiece);
            writer.Write((byte)state.Hits);
            writer.Write(state.CooldownLeft);
        }
    }

    /// <summary>Replaces the tracked anvils with a snapshot written by <see cref="WriteNet"/> (a client's mirror).</summary>
    internal void ReadNet(PacketReader reader)
    {
        _anvils.Clear();
        var count = StationNetCodec.ReadCount(reader);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var state = new State
            {
                BlockId = reader.ReadByte(),
                HasPiece = reader.ReadBool(),
                Hits = reader.ReadByte(),
                CooldownLeft = StationNetCodec.ReadFinite(reader),
            };
            state.Profile = ProfileOf(state.BlockId);
            if (state.Hits > AnvilProfile.MaxHits)
                throw new System.IO.InvalidDataException("anvil snapshot out of range");
            _anvils[key] = state;
        }
    }
}
