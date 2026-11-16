using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Lanterns;
using VoxelEngine.Simulation.Smithing;

namespace VoxelEngine.Effects.Lighting;

/// <summary>
/// An authoritative world's man-made point lights (<see cref="IManMadePointLights"/>): its fueled lanterns and its
/// lit campfires, kilns and bloomeries, each read from the station that owns it.
/// </summary>
public sealed class StationPointLights(ChunkManager chunks, LanternManager lanterns, CampfireManager campfires,
    KilnManager kilns, BloomeryManager bloomeries) : IManMadePointLights
{
    public void AppendLitCells(List<(int X, int Y, int Z)> cells)
    {
        lanterns.AppendLitCells(chunks, cells);
        AppendFlames(campfires.LitCells, cells);
        AppendFlames(kilns.LitCells, cells);
        AppendFlames(bloomeries.LitCells, cells);
    }

    // A fire's light origin sits within its own cell (a campfire's cell itself, a kiln's flame under its lintel)
    private static void AppendFlames(IReadOnlyList<Vector3> flames, List<(int X, int Y, int Z)> cells)
    {
        foreach (var flame in flames)
            cells.Add(flame.BlockCell());
    }
}
