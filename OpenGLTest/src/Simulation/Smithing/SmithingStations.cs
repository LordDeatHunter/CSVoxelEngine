using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.Simulation.Leatherwork;
using VoxelEngine.Simulation.WorkStations;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// The works of a world: its bloomeries, its work stations (anvils, fleshing beams, leather tables) and its tanning
/// vats, owned by <see cref="World"/> as one unit so the frame, the save (<c>stations.json</c>) and the station broadcast
/// (<see cref="StationNetCodec"/>) each plumb a single object (the name predates the leather works). Main thread only; a client's copy is a display-only mirror that never ticks.
/// </summary>
public sealed class SmithingStations
{
    /// <summary>The bloomeries.</summary>
    public BloomeryManager Bloomeries { get; } = new();

    /// <summary>The work stations: anvils, fleshing beams, leather tables.</summary>
    public WorkStationManager WorkStations { get; } = new();

    /// <summary>The tanning vats.</summary>
    public TanningVatManager Vats { get; } = new();

    /// <summary>How far each bellows is pressed down, for the draw (display only, never saved or sent).</summary>
    public BellowsSquash Bellows { get; } = new();

    /// <summary>Whether any holds anything, so an empty world sends no snapshot for them.</summary>
    public bool HasAny => Bloomeries.HasAny || WorkStations.HasAny || Vats.HasAny;

    /// <summary>
    /// Picks the recipe making <paramref name="resultId"/> for the stack on the work station at <paramref name="cell"/>, to be
    /// worked next (<see cref="WorkStationManager.TrySelect"/>): singleplayer's pick from the station's recipe list, a
    /// server replaying a client's <see cref="NetIntentKind.Forge"/> intent, and that client's prediction. The caller
    /// judges the tool in hand and the reach.
    /// </summary>
    public bool Select(Vector3 cell, ushort resultId) => WorkStations.TrySelect(cell, resultId);

    /// <summary>Runs all three for one frame.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, LodManager? lod)
    {
        Bloomeries.Update(deltaTime, chunks, drops, lod);
        WorkStations.Update(deltaTime, chunks, drops);
        Vats.Update(deltaTime, chunks, drops);
    }

    /// <summary>Writes all three (bloomeries, then work stations, then vats).</summary>
    internal void WriteNet(PacketWriter writer)
    {
        Bloomeries.WriteNet(writer);
        WorkStations.WriteNet(writer);
        Vats.WriteNet(writer);
    }

    /// <summary>Replaces all three with a snapshot written by <see cref="WriteNet"/>.</summary>
    internal void ReadNet(PacketReader reader)
    {
        Bloomeries.ReadNet(reader);
        WorkStations.ReadNet(reader);
        Vats.ReadNet(reader);
    }

    /// <summary>Captures all three into <paramref name="data"/>.</summary>
    internal void Capture(StationSaveData data)
    {
        data.Bloomeries = Bloomeries.Capture();
        data.Anvils = WorkStations.Capture();
        data.Vats = Vats.Capture();
    }

    /// <summary>Restores all three from <paramref name="saved"/>; returns how many entries were dropped.</summary>
    internal int Restore(StationSaveData saved) =>
        Bloomeries.Restore(saved.Bloomeries) + WorkStations.Restore(saved.Anvils) + Vats.Restore(saved.Vats);
}
