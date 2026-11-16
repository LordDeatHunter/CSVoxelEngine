namespace VoxelEngine.Commands;

/// <summary>
/// A server's operator list as <c>/op</c> and <c>/deop</c> change it. Only a multiplayer host has one
/// (<see cref="ICommandHost.Operators"/>); singleplayer is the operator of its own world and has none.
/// </summary>
public interface IOperatorAdmin
{
    /// <summary>Makes <paramref name="name"/> an operator (saved with the world), or refuses with the reason.</summary>
    bool TryOp(string name, out string refusal);

    /// <summary>Takes operator rights from <paramref name="name"/> (and with them creative and flight), or refuses
    /// with the reason.</summary>
    bool TryDeop(string name, out string refusal);
}
