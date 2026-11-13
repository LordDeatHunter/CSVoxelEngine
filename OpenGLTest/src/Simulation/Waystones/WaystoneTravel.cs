using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Simulation.Waystones;

/// <summary>
/// The local player's waystone trip, after <see cref="WaystoneTeleport"/> allowed and billed it: <see cref="Begin"/>
/// moves the camera over the destination at once, so the streaming focus follows it there, and then, like the spawn
/// settle, the camera is held over the waystone every frame until the destination's cells are loaded and its chunk is
/// meshed (so the traveler neither falls through unloaded terrain nor stands in a void), and lands it on the
/// <see cref="WaystoneArrival"/> spot. Past <see cref="StreamingConfig.WaystoneArrivalTimeoutSeconds"/> it lands on top
/// of the waystone anyway. A landing never bills a fall. A multiplayer client's trip is the server's: the server picks
/// the spot and snaps the client there, and <see cref="BeginLanding"/> only holds the camera on it until the client's own
/// streamed chunks there are meshed. World component; main thread only.
/// </summary>
internal sealed class WaystoneTravel
{
    private readonly Camera _camera;
    private readonly LocalPlayer _player;
    private readonly ChunkManager _chunks;
    private readonly ChunkArrivalCells _cells;
    private (int X, int Y, int Z) _target;
    // A client's landing: the spot the server picked, held as it is rather than searched for
    private WaystoneArrivalSpot? _landing;
    private double _held;

    internal WaystoneTravel(Camera camera, LocalPlayer player, ChunkManager chunks)
    {
        _camera = camera;
        _player = player;
        _chunks = chunks;
        _cells = new ChunkArrivalCells(chunks);
    }

    /// <summary>Whether a trip is waiting for its destination to load.</summary>
    internal bool IsArriving { get; private set; }

    /// <summary>Starts a trip to the waystone at <paramref name="waystone"/>: the camera moves over it at once.</summary>
    internal void Begin((int X, int Y, int Z) waystone)
    {
        _target = waystone;
        _landing = null;
        _held = 0;
        IsArriving = true;
        Hold(WaystoneArrival.OnTopFeet(waystone));
    }

    /// <summary>
    /// A client's trip: the server landed the player with its eye at <paramref name="eye"/> beside the waystone at
    /// <paramref name="waystone"/>. The camera is held there, facing away from the waystone, until the chunk under it is
    /// meshed on this side too (or the arrival timeout runs out), so it never falls through terrain still streaming in.
    /// </summary>
    internal void BeginLanding(Vector3 eye, (int X, int Y, int Z) waystone)
    {
        var feet = eye - new Vector3(0f, Camera.EyeHeight + WaystoneArrival.LandingLift, 0f);
        var dx = feet.X - (waystone.X + 0.5f);
        var dz = feet.Z - (waystone.Z + 0.5f);
        // On top of the waystone the facing is the player's own; beside it, away from it
        float? yaw = dx * dx + dz * dz > 0.01f ? MathF.Atan2(dz, dx) * (180f / MathF.PI) : null;
        _target = waystone;
        _landing = new WaystoneArrivalSpot(WaystoneArrivalStatus.Found, feet, yaw);
        _held = 0;
        IsArriving = true;
        Hold(feet);
    }

    /// <summary>Once a frame, before the camera moves: holds the traveler, or lands it once the destination is in.</summary>
    internal void Update(double deltaTime)
    {
        if (!IsArriving) return;
        _held += deltaTime;
        var spot = _landing ?? WaystoneArrival.Find(_cells, _target);
        var ready = spot.Status != WaystoneArrivalStatus.Unloaded && PlayerArrival.IsMeshed(_chunks, spot.Feet);
        if (!ready)
        {
            if (_held < StreamingConfig.WaystoneArrivalTimeoutSeconds)
            {
                Hold(_landing?.Feet ?? WaystoneArrival.OnTopFeet(_target));
                return;
            }
            spot = _landing ?? new WaystoneArrivalSpot(WaystoneArrivalStatus.OnTop, WaystoneArrival.OnTopFeet(_target), null);
        }
        Land(spot);
    }

    private void Hold(Vector3 feet)
    {
        _camera.TestTeleport(WaystoneArrival.EyeOver(feet), _camera.Yaw, _camera.Pitch);
        _player.ResetFall(feet.Y);
    }

    private void Land(WaystoneArrivalSpot spot)
    {
        IsArriving = false;
        _landing = null;
        _camera.TestTeleport(WaystoneArrival.EyeOver(spot.Feet), spot.Yaw ?? _camera.Yaw, spot.Yaw.HasValue ? 0f : _camera.Pitch);
        // The spot is picked by whole cells; the arrival lifts the body out of anything it still overlaps (a timed-out
        // landing, a partial block beside it) by the body's own collision rule
        _player.Arrival.BeginAt(spot.Feet);
    }
}
