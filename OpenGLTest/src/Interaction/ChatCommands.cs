using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using VoxelEngine.Content;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI.Hud;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Interaction;

/// <summary>
/// The chat commands, GL-free: a line starting with <c>/</c> is parsed here and never echoed as
/// chat. The table names each command's handler and the rights it needs, so singleplayer and a
/// server run the same rules; the caller (ChatInput locally, GameServer for a connected player)
/// performs the effect and answers with a system line. <c>/locate</c>'s answer is built here for
/// both (<see cref="Locate"/>): its coordinates are a link whose click suggests a <c>/tp</c> beside
/// the structure (<see cref="TeleportCommand"/>).
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

        /// <summary><c>/locate tower</c>: find the nearest battle tower.</summary>
        Locate,
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
            "locate" => new Parsed(CommandKind.Locate, args),
            _ => new Parsed(CommandKind.Unknown, args),
        };
    }

    /// <summary>Whether a command needs creative rights (singleplayer) or operator rights (a
    /// server): <c>/tp</c> and <c>/give</c> do, <c>/help</c>, <c>/time</c> and the read-only
    /// <c>/locate</c> do not.</summary>
    public static bool RequiresOperator(CommandKind kind) => kind is CommandKind.Teleport or CommandKind.Give;

    /// <summary>The help text: every command on its own line.</summary>
    public static string HelpText =>
        "Commands: /help, /time day|night|<seconds>, /locate tower, /tp x y z (creative), /give <item> [count] (creative)";

    /// <summary>
    /// The lighting time <c>/time &lt;argument&gt;</c> sets, or null when the argument is not
    /// <c>day</c>, <c>night</c> or a number of seconds (the units <c>VOXEL_TIME</c> uses: one full
    /// cycle is <see cref="SkyConfig.DayLengthSeconds"/>).
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
    public static Vector3? TeleportTarget(IReadOnlyList<string> args)
    {
        if (args.Count < 3) return null;
        var values = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return null;
        }
        return new Vector3(values[0], values[1], values[2]);
    }

    /// <summary>The <c>/tp</c> line that <see cref="TeleportTarget"/> parses back to <paramref name="eye"/> (within its
    /// two decimals), under any culture.</summary>
    public static string TeleportCommand(Vector3 eye) =>
        string.Create(CultureInfo.InvariantCulture, $"/tp {eye.X:0.##} {eye.Y:0.##} {eye.Z:0.##}");

    /// <summary>
    /// The answer to <c>/locate &lt;args&gt;</c> asked from <paramref name="eye"/>, shared by singleplayer and a server:
    /// the nearest battle tower within <see cref="WorldGenerationConfig.TowerLocateRings"/> tower cells
    /// (<see cref="StructureLocator.TryFindTower"/>), its coordinates a link whose click suggests a <c>/tp</c> to the ground
    /// beside it, or a line with no link (the usage, or none found). Reads worldgen, so only a world holding the seed may
    /// ask. A tower <paramref name="towers"/> records as defeated is conquered and is not offered.
    /// </summary>
    public static (string Text, ChatLink? Link) Locate(IReadOnlyList<string> args, Vector3 eye, TowerManager? towers = null)
    {
        if (args.Count == 0 || !string.Equals(args[0], "tower", StringComparison.OrdinalIgnoreCase))
            return ("Usage: /locate tower", null);
        if (!StructureLocator.TryFindTower((int)MathF.Floor(eye.X), (int)MathF.Floor(eye.Z),
                WorldGenerationConfig.TowerLocateRings, out var found,
                (x, z) => towers?.RecordAt(x, z)?.BossDefeated == true))
            return (string.Create(CultureInfo.InvariantCulture,
                $"No battle tower within {WorldGenerationConfig.TowerLocateRings * WorldGenerationConfig.TowerCellBlocks} blocks"), null);

        const string prefix = "Nearest battle tower at ";
        var coordinates = string.Create(CultureInfo.InvariantCulture, $"[{found.X}, {found.Y}, {found.Z}]");
        var text = prefix + coordinates + string.Create(CultureInfo.InvariantCulture, $", {found.Distance:0} blocks away");
        var command = TeleportCommand(new Vector3(found.LandX + 0.5f, found.LandFeetY + Camera.EyeHeight, found.LandZ + 0.5f));
        return (text, new ChatLink(prefix.Length, coordinates.Length, command));
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
