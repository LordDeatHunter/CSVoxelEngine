using System;
using System.Collections.Generic;
using System.Globalization;
using VoxelEngine.Content;
using VoxelEngine.Effects.Sky;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Interaction;

/// <summary>
/// The chat commands, GL-free: a line starting with <c>/</c> is parsed here and never echoed as
/// chat. The table names each command's handler and the rights it needs, so singleplayer and a
/// server run the same rules; the caller (Program locally, GameServer for a connected player)
/// performs the effect and answers with a system line.
/// </summary>
public static class ChatCommands
{
    /// <summary>What a parsed line asks for.</summary>
    public enum CommandKind : byte
    {
        /// <summary>Not a command (no leading slash).</summary>
        None,

        /// <summary>An unknown command: answer with the help text.</summary>
        Unknown,

        /// <summary><c>/help</c>: list the commands.</summary>
        Help,

        /// <summary><c>/time day|night|&lt;seconds&gt;</c>: set the lighting clock.</summary>
        Time,

        /// <summary><c>/tp x y z</c>: teleport the player.</summary>
        Teleport,

        /// <summary><c>/give &lt;item&gt; [count]</c>: grant items.</summary>
        Give,
    }

    /// <summary>One parsed line: its kind and the arguments after the command word.</summary>
    public readonly record struct Parsed(CommandKind Kind, string[] Args);

    /// <summary>
    /// Parses a chat line: anything without a leading slash is <see cref="CommandKind.None"/>, an
    /// unknown word is <see cref="CommandKind.Unknown"/> (the caller answers with the help text),
    /// and the rest carries its arguments (case-insensitively).
    /// </summary>
    public static Parsed Parse(string line)
    {
        var trimmed = line?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed[0] != '/') return new Parsed(CommandKind.None, []);
        var parts = trimmed[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return new Parsed(CommandKind.Unknown, []);
        var args = parts.Length > 1 ? parts[1..] : [];
        return parts[0].ToLowerInvariant() switch
        {
            "help" => new Parsed(CommandKind.Help, args),
            "time" => new Parsed(CommandKind.Time, args),
            "tp" => new Parsed(CommandKind.Teleport, args),
            "give" => new Parsed(CommandKind.Give, args),
            _ => new Parsed(CommandKind.Unknown, args),
        };
    }

    /// <summary>Whether a command needs creative rights (singleplayer) or operator rights (a
    /// server): <c>/tp</c> and <c>/give</c> do, <c>/help</c> and <c>/time</c> do not.</summary>
    public static bool RequiresOperator(CommandKind kind) => kind is CommandKind.Teleport or CommandKind.Give;

    /// <summary>The help text: every command on its own line.</summary>
    public static string HelpText =>
        "Commands: /help, /time day|night|<seconds>, /tp x y z (creative), /give <item> [count] (creative)";

    /// <summary>
    /// The lighting time <c>/time &lt;argument&gt;</c> sets, or null when the argument is not
    /// <c>day</c>, <c>night</c> or a number of seconds (the units <c>VOXEL_TIME</c> uses: one full
    /// cycle is <see cref="WorldGenerationConfig.DayLengthSeconds"/>).
    /// </summary>
    public static double? TimeSeconds(string argument) => argument.ToLowerInvariant() switch
    {
        "day" => DayCycle.NoonTime,
        "night" => Clock.MidnightTime,
        _ => double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null,
    };

    /// <summary>
    /// The teleport target <c>/tp x y z</c> asks for, or null when the three numbers do not parse.
    /// The caller decides whether the player may teleport.
    /// </summary>
    public static System.Numerics.Vector3? TeleportTarget(IReadOnlyList<string> args)
    {
        if (args.Count < 3) return null;
        var values = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return null;
        }
        return new System.Numerics.Vector3(values[0], values[1], values[2]);
    }

    /// <summary>
    /// The <c>/give</c> request: the item name (resolved by the caller through
    /// <see cref="ContentRegistry"/>) and how many, clamped to at least 1. The count defaults to 1
    /// and a non-numeric one is refused (null).
    /// </summary>
    public static (string Item, int Count)? GiveRequest(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;
        var count = 1;
        if (args.Count > 1 && !int.TryParse(args[1], out count)) return null;
        return (args[0], Math.Max(1, count));
    }
}
