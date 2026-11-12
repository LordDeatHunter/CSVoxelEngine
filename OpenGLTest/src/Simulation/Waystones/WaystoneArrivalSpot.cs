using System.Numerics;

namespace VoxelEngine.Simulation.Waystones;

/// <summary>Where a waystone traveler lands (<see cref="WaystoneArrival.Find"/>): the feet position (the middle of the
/// cell's floor) and, for a <see cref="WaystoneArrivalStatus.Found"/> spot, the yaw in degrees facing away from the
/// waystone (null otherwise: keep the traveler's own). Meaningless while <see cref="Status"/> is
/// <see cref="WaystoneArrivalStatus.Unloaded"/>.</summary>
public readonly record struct WaystoneArrivalSpot(WaystoneArrivalStatus Status, Vector3 Feet, float? Yaw);
