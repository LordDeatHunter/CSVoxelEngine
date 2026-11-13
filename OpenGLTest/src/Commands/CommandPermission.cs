namespace VoxelEngine.Commands;

/// <summary>
/// The rights a command node needs, ordered: a source may use a node whose permission is at most its own
/// (<see cref="CommandSource.Allows"/>). Singleplayer is the operator of its own world; on a server a player is an
/// operator while it may be creative there (<c>ServerState.MayBeCreative</c>).
/// </summary>
public enum CommandPermission : byte
{
    /// <summary>Anyone may run it.</summary>
    Everyone,

    /// <summary>Only an operator may run it.</summary>
    Operator,
}
