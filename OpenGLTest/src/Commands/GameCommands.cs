using VoxelEngine.Commands.Definitions;

namespace VoxelEngine.Commands;

/// <summary>The game's commands, one dispatcher for every side: singleplayer runs lines on it, a server runs its
/// players' lines on it, and a connected client completes lines from it (and sends them to the server).</summary>
public static class GameCommands
{
    public static CommandDispatcher Create()
    {
        var dispatcher = new CommandDispatcher();
        HelpCommand.Register(dispatcher);
        TeleportCommand.Register(dispatcher);
        GiveCommand.Register(dispatcher);
        SummonCommand.Register(dispatcher);
        TimeCommand.Register(dispatcher);
        GameModeCommand.Register(dispatcher);
        FlyCommand.Register(dispatcher);
        KillCommand.Register(dispatcher);
        LocateCommand.Register(dispatcher);
        return dispatcher;
    }
}
