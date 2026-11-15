namespace VoxelEngine.Commands;

/// <summary>
/// The rights a command node needs, ordered: a source may use a node whose permission is at most its own
/// (<see cref="CommandSource.Allows"/>). Singleplayer is the operator of its own world; on a server a player is an
/// operator when it is in the server's operator list (<c>ServerState.IsOperator</c>), in any game mode.
/// </summary>
public enum CommandPermission : byte
{
    /// <summary>Anyone may run it.</summary>
    Everyone,

    /// <summary>Only an operator may run it.</summary>
    Operator,
}
