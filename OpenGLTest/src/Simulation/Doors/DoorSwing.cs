using System.Numerics;
using VoxelEngine.Interaction;

namespace VoxelEngine.Simulation.Doors;

/// <summary>
/// One door mid-swing, as <see cref="DoorSwings"/> reports it each tick.
/// </summary>
/// <param name="Cell">The door's lower cell.</param>
/// <param name="LowerId">The lower half's block id.</param>
/// <param name="State">The state byte the door is swinging to (<see cref="DoorState"/>): its facing, its hinge
/// and whether it is opening.</param>
/// <param name="Open">How far open the panel stands right now, 0 shut to 1 open.</param>
/// <param name="Done">Whether the panel has arrived: its last tick before the cells go back to the chunk mesh.</param>
public readonly record struct DoorSwing(Vector3 Cell, ushort LowerId, byte State, float Open, bool Done)
{
    /// <summary>Whether the door is swinging open rather than shut.</summary>
    public bool Opening => DoorState.IsOpen(State);
}
