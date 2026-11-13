using System.Numerics;

namespace VoxelEngine.Commands;

/// <summary>
/// A player a command acts on: the local player in singleplayer, a connected <c>ServerPlayer</c> on a server. Each
/// host applies the effect its own way (a server corrects the client and reports its status), so a command
/// definition never knows which side it runs on.
/// </summary>
public interface ICommandPlayer
{
    string Name { get; }

    /// <summary>Where the player's feet stand: the base <c>~</c> coordinates are relative to.</summary>
    Vector3 Feet { get; }

    bool IsCreative { get; }

    bool IsFlying { get; }

    /// <summary>Moves the player so its feet stand at <paramref name="feet"/>.</summary>
    void Teleport(Vector3 feet);

    /// <summary>Puts <paramref name="count"/> of the block or item <paramref name="id"/> into the player's inventory.</summary>
    void Give(ushort id, int count);

    /// <summary>Sets the player's modes (flight only alongside creative), or refuses with the reason when the host
    /// does not let this player have them.</summary>
    bool TrySetModes(bool creative, bool flying, out string refusal);

    /// <summary>Kills the player; false when it is already dead.</summary>
    bool Kill();
}
