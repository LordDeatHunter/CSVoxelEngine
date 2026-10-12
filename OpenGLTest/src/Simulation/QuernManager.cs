using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Stations;
using VoxelEngine.Networking;

namespace VoxelEngine.Simulation;

/// <summary>
/// Block-entity registry for querns: one crushable unit per cell plus a one-rotation spin
/// timer. Right-clicking a quern with a crushable in hand inserts one unit; right-clicking
/// otherwise spins the plate and handle through exactly one full rotation
/// (<see cref="FullRotationSeconds"/>), ignoring further clicks while it turns. Each
/// completed rotation rolls <see cref="SuccessChance"/>: on success the input is consumed
/// and its crush recipe's result is thrown out as drops, on failure the input stays for
/// the next rotation. Main thread only, like the other managers.
/// </summary>
public sealed class QuernManager
{
    /// <summary>Seconds of one full plate rotation, started by right-clicking the quern.</summary>
    public const float FullRotationSeconds = 2f;

    /// <summary>Chance each completed rotation throws out the output and consumes the input.</summary>
    public const float SuccessChance = 0.6f;

    private const float TwoPi = MathF.Tau;

    /// <summary>Live state of one quern cell: its waiting input and where its top has turned.</summary>
    public sealed class QuernState
    {
        /// <summary>Waiting crushable id, or 0 when the quern is empty.</summary>
        public byte InputId;

        /// <summary>Top angle in radians; advances while spinning, back at rest modulo 2pi.</summary>
        public float Angle;

        /// <summary>Whether a triggered rotation is still turning.</summary>
        public bool Spinning;

        /// <summary>Whether a crushable is waiting for the next rotation.</summary>
        public bool HasInput => InputId != 0;
    }

    private readonly Dictionary<(int X, int Y, int Z), QuernState> _querns = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();

    /// <summary>Whether any quern cell holds state (input or spin), for render early-out.</summary>
    public bool HasAny => _querns.Count > 0;

    /// <summary>Live state of a quern cell, or null when it was never touched.</summary>
    public QuernState? StateAt(int x, int y, int z) =>
        _querns.GetValueOrDefault((x, y, z));

    /// <summary>Whether this quern's top is mid-rotation: clicks on it do nothing.</summary>
    public bool IsSpinning(int x, int y, int z) =>
        _querns.TryGetValue((x, y, z), out var state) && state.Spinning;

    /// <summary>
    /// Registers a freshly placed quern so its top draws at rest before anything is
    /// inserted or spun. The chunk only meshes the base (see <see cref="QuernRenderer"/>),
    /// so without a state the placed quern would render incomplete.
    /// </summary>
    public void Ensure(int x, int y, int z)
    {
        _querns.TryAdd((x, y, z), new QuernState());
    }

    /// <summary>
    /// Puts one crushable unit into an empty quern. Refuses anything with no crush recipe
    /// and anything while the quern already holds a unit.
    /// </summary>
    public bool TryInsert(int x, int y, int z, byte inputId)
    {
        if (ContentRegistry.CrushRecipeForInput(inputId) == null) return false;
        var key = (x, y, z);
        if (!_querns.TryGetValue(key, out var state))
        {
            state = new QuernState();
            _querns[key] = state;
        }
        if (state.HasInput) return false;
        state.InputId = inputId;
        return true;
    }

    /// <summary>
    /// Starts one full rotation, unless the top is already turning (that click is lost).
    /// Spinning an empty quern is allowed and simply rolls nothing on completion.
    /// </summary>
    public void StartSpin(int x, int y, int z)
    {
        var key = (x, y, z);
        if (!_querns.TryGetValue(key, out var state))
        {
            state = new QuernState();
            _querns[key] = state;
        }
        if (!state.Spinning)
            state.Spinning = true;
    }

    /// <summary>
    /// Drops a broken quern's waiting input where the block was and forgets the cell.
    /// </summary>
    public void NotifyBlockRemoved(int x, int y, int z, DropManager? drops = null)
    {
        var key = (x, y, z);
        if (!_querns.TryGetValue(key, out var state)) return;
        _querns.Remove(key);
        if (drops != null && state.HasInput)
            drops.Spawn(state.InputId, new Vector3(x, y, z));
    }

    /// <summary>Every quern cell with state, for the spinning-top render pass.</summary>
    public IEnumerable<(Vector3 Cell, float Angle, byte InputId)> QuernView()
    {
        foreach (var (key, state) in _querns)
            yield return (new Vector3(key.X, key.Y, key.Z), state.Angle, state.InputId);
    }

    /// <summary>
    /// Turns spinning tops and resolves each completed rotation: a 0.6 roll that consumes
    /// the waiting input and throws out its crush result, or keeps the input for the next
    /// rotation. A quern keeps its state while its block stands — the top entity only draws
    /// for tracked cells, so dropping an idle one would blink it out of the world. Cells
    /// that stopped being querns (blasts, replacements) drop their input and are forgotten;
    /// unloaded cells freeze until their chunk is back.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        if (deltaTime <= 0f || _querns.Count == 0) return;
        _stale.Clear();
        foreach (var (key, state) in _querns)
        {
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;
            if (!chunks.TryGetCell(key.X, key.Y, key.Z, out var id, out _)
                || BlockType.ById[id]?.Renderer != BlockRenderer.Quern)
            {
                _stale.Add(key);
                continue;
            }
            if (!state.Spinning) continue;
            state.Angle += TwoPi * deltaTime / FullRotationSeconds;
            if (state.Angle < TwoPi) continue;
            state.Angle %= TwoPi;
            state.Spinning = false;
            if (state.HasInput)
                TryCrush(key, state, drops);
        }
        foreach (var key in _stale)
        {
            if (!_querns.TryGetValue(key, out var state)) continue;
            _querns.Remove(key);
            if (state.HasInput)
                drops.Spawn(state.InputId, new Vector3(key.X, key.Y, key.Z));
        }
    }

    /// <summary>
    /// A multiplayer client's quern frame: spinning tops keep turning between the server's
    /// snapshots and come to rest after one rotation, but nothing is crushed or dropped - that is
    /// the server's, and its next snapshot says what the quern holds.
    /// </summary>
    public void Animate(float deltaTime)
    {
        if (deltaTime <= 0f || _querns.Count == 0) return;
        foreach (var state in _querns.Values)
        {
            if (!state.Spinning) continue;
            state.Angle += TwoPi * deltaTime / FullRotationSeconds;
            if (state.Angle < TwoPi) continue;
            state.Angle %= TwoPi;
            state.Spinning = false;
        }
    }

    /// <summary>Writes every tracked quern (its input, top angle and spin), for the station
    /// broadcast (<see cref="StationNetCodec"/>).</summary>
    internal void WriteNet(PacketWriter writer)
    {
        // At most what a client accepts (StationNetCodec.MaxEntries)
        var leftQuerns = Math.Min(_querns.Count, StationNetCodec.MaxEntries);
        writer.Write(leftQuerns);
        foreach (var ((x, y, z), state) in _querns)
        {
            if (leftQuerns-- == 0) break;
            StationNetCodec.WriteCell(writer, x, y, z);
            writer.Write(state.InputId);
            writer.Write(state.Angle);
            writer.Write(state.Spinning);
        }
    }

    /// <summary>
    /// Replaces the tracked querns with a snapshot written by <see cref="WriteNet"/>: a client's
    /// display-only mirror. A top this client is already turning keeps its own angle while the
    /// server's turns too, so the 10 Hz snapshots never yank a spin backwards by the latency.
    /// </summary>
    internal void ReadNet(PacketReader reader)
    {
        var count = StationNetCodec.ReadCount(reader);
        _stale.Clear();
        _stale.AddRange(_querns.Keys);
        for (var i = 0; i < count; i++)
        {
            var key = StationNetCodec.ReadCell(reader);
            var inputId = reader.ReadByte();
            var angle = StationNetCodec.ReadFinite(reader);
            var spinning = reader.ReadBool();
            _stale.Remove(key);
            if (!_querns.TryGetValue(key, out var state))
            {
                state = new QuernState();
                _querns[key] = state;
            }
            else if (state.Spinning && spinning)
            {
                state.InputId = inputId;
                continue;
            }
            state.InputId = inputId;
            state.Angle = angle;
            state.Spinning = spinning;
        }
        foreach (var key in _stale)
            _querns.Remove(key);
        _stale.Clear();
    }

    private static void TryCrush((int X, int Y, int Z) key, QuernState state, DropManager drops)
    {
        var recipe = ContentRegistry.CrushRecipeForInput(state.InputId);
        if (recipe == null)
        {
            state.InputId = 0;
            return;
        }
        if (Random.Shared.NextSingle() >= SuccessChance) return;
        var cell = new Vector3(key.X, key.Y, key.Z);
        for (var n = 0; n < recipe.ResultCount; n++)
            drops.Spawn(recipe.ResultId, cell);
        state.InputId = 0;
    }
}
