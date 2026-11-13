using VoxelEngine.Commands.Arguments;

namespace VoxelEngine.Commands;

/// <summary>Starts an argument step of a command tree, named for the key its value is read back by
/// (<see cref="CommandContext.Get{T}"/>). Each kind says what the step accepts and completes; chain them with
/// <see cref="CommandBuilder"/>.</summary>
public static class Arg
{
    /// <summary>One word from a fixed list; the value is the <see cref="string"/> option.</summary>
    public static ArgumentNode Word(string name, params string[] options) => new(name, new WordArgument(options));

    /// <summary>A whole number in a range; the value is an <see cref="int"/>.</summary>
    public static ArgumentNode Int(string name, int min, int max) => new(name, new IntArgument(min, max));

    /// <summary>Any finite number; the value is a <see cref="double"/>.</summary>
    public static ArgumentNode Number(string name) => new(name, new NumberArgument());

    /// <summary>An <c>x y z</c> position with <c>~</c> relative parts; the value is a <see cref="System.Numerics.Vector3"/>.</summary>
    public static ArgumentNode Coordinates(string name) => new(name, new CoordinatesArgument());

    /// <summary>An online player; read it with <see cref="CommandContext.Player"/>.</summary>
    public static ArgumentNode Player(string name) => new(name, new PlayerArgument());

    /// <summary>A registered entity kind; the value is its <see cref="string"/> name.</summary>
    public static ArgumentNode Entity(string name) => new(name, new EntityArgument());

    /// <summary>A block or item; the value is a <see cref="ContentItem"/>.</summary>
    public static ArgumentNode Item(string name) => new(name, new ItemArgument());

    /// <summary>A command of <paramref name="dispatcher"/> the source may run; the value is its <see cref="LiteralNode"/>.</summary>
    public static ArgumentNode Command(string name, CommandDispatcher dispatcher) => new(name, new CommandNameArgument(dispatcher));
}
