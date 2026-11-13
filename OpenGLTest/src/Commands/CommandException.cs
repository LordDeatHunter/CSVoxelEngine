using System;

namespace VoxelEngine.Commands;

/// <summary>A command that cannot do what it was asked: its message is answered to the source as a system line
/// (<see cref="CommandDispatcher.Execute"/> catches it), never a crash.</summary>
public sealed class CommandException(string message) : Exception(message);
