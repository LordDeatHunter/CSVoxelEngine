using System.Numerics;

namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// A server player's waystone trip waiting for its destination, GL-free: the server's twin of the singleplayer hold in
/// <see cref="WaystoneTravel"/>. The player is kept over the destination (<see cref="HoldEye"/>, which is also what moves
/// its streaming focus there) and each <see cref="Step"/> retries the <see cref="WaystoneArrival"/> search: the spot once
/// every cell it reads is loaded, else, past <see cref="StreamingConfig.WaystoneArrivalTimeoutSeconds"/>, the top of the
/// waystone. A headless server needs no mesh, only the cells.
/// </summary>
public sealed class WaystoneArrivalHold((int X, int Y, int Z) target)
{
    /// <summary>The destination waystone's cell.</summary>
    public (int X, int Y, int Z) Target { get; } = target;

    /// <summary>Seconds the traveler has been held so far.</summary>
    public double Held { get; private set; }

    /// <summary>Where the traveler's eye is held while it waits: over the top of the destination waystone.</summary>
    public Vector3 HoldEye => WaystoneArrival.EyeOver(WaystoneArrival.OnTopFeet(Target));

    /// <summary>Advances the hold by <paramref name="deltaTime"/>: the spot to land on once the destination's cells are
    /// loaded (or the top of the waystone once the timeout ran out), else null to keep waiting.</summary>
    public WaystoneArrivalSpot? Step(double deltaTime, IArrivalCells cells)
    {
        Held += deltaTime;
        var spot = WaystoneArrival.Find(cells, Target);
        if (spot.Status != WaystoneArrivalStatus.Unloaded) return spot;
        if (Held < StreamingConfig.WaystoneArrivalTimeoutSeconds) return null;
        return new WaystoneArrivalSpot(WaystoneArrivalStatus.OnTop, WaystoneArrival.OnTopFeet(Target), null);
    }
}
