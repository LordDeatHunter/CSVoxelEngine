using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Saves;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// The iron works of a world: its bloomeries and its anvils, owned by <see cref="World"/> as one unit so the frame,
/// the save (<c>stations.json</c>) and the station broadcast (<see cref="StationNetCodec"/>) each plumb a single
/// object. Main thread only; a client's copy is a display-only mirror that never ticks.
/// </summary>
public sealed class SmithingStations
{
    /// <summary>The bloomeries.</summary>
    public BloomeryManager Bloomeries { get; } = new();

    /// <summary>The anvils.</summary>
    public AnvilManager Anvils { get; } = new();

    /// <summary>How far each bellows is pressed down, for the draw (display only, never saved or sent).</summary>
    public BellowsSquash Bellows { get; } = new();

    /// <summary>Whether either holds anything, so an empty world sends no snapshot for them.</summary>
    public bool HasAny => Bloomeries.HasAny || Anvils.HasAny;

    /// <summary>
    /// Picks the recipe making <paramref name="resultId"/> for the stack on the anvil at <paramref name="cell"/>, to be
    /// hammered out next (<see cref="AnvilManager.TrySelect"/>): singleplayer's pick from the anvil's recipe list, a
    /// server replaying a client's <see cref="NetIntentKind.Forge"/> intent, and that client's prediction. The caller
    /// judges the hammer in hand and the reach.
    /// </summary>
    public bool Select(Vector3 cell, ushort resultId) => Anvils.TrySelect(cell, resultId);

    /// <summary>Runs both for one frame.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, LodManager? lod)
    {
        Bloomeries.Update(deltaTime, chunks, drops, lod);
        Anvils.Update(deltaTime, chunks, drops);
    }

    /// <summary>Writes both (bloomeries, then anvils).</summary>
    internal void WriteNet(PacketWriter writer)
    {
        Bloomeries.WriteNet(writer);
        Anvils.WriteNet(writer);
    }

    /// <summary>Replaces both with a snapshot written by <see cref="WriteNet"/>.</summary>
    internal void ReadNet(PacketReader reader)
    {
        Bloomeries.ReadNet(reader);
        Anvils.ReadNet(reader);
    }

    /// <summary>Captures both into <paramref name="data"/>.</summary>
    internal void Capture(StationSaveData data)
    {
        data.Bloomeries = Bloomeries.Capture();
        data.Anvils = Anvils.Capture();
    }

    /// <summary>Restores both from <paramref name="saved"/>; returns how many entries were dropped.</summary>
    internal int Restore(StationSaveData saved) => Bloomeries.Restore(saved.Bloomeries) + Anvils.Restore(saved.Anvils);
}
