using System;
using System.Globalization;
using System.Numerics;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI.Hud;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Commands.Definitions;

/// <summary>
/// <c>/locate tower</c>: the nearest battle tower, its coordinates a link whose click suggests a <c>/tp</c> to the
/// ground beside it (<see cref="Tower"/>). Reads worldgen, so it runs where the seed is: singleplayer, or the server
/// for a connected player, searched from that player's eye.
/// </summary>
public static class LocateCommand
{
    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("locate").Describe("Find the nearest battle tower")
            .Then(Cmd.Literal("tower").Executes(c =>
            {
                var (text, link) = Tower(c.Self.Feet, c.Source.Host.Towers);
                c.Reply(text, link);
            })));

    /// <summary>
    /// The answer for a search from <paramref name="feet"/>: the nearest tower within
    /// <see cref="WorldGenerationConfig.TowerLocateRings"/> tower cells (<see cref="StructureLocator.TryFindTower"/>)
    /// that <paramref name="towers"/> does not record as conquered, its coordinates linked to a teleport onto its
    /// landing, or a line with no link when none is found.
    /// </summary>
    public static (string Text, ChatLink? Link) Tower(Vector3 feet, TowerManager? towers)
    {
        if (!StructureLocator.TryFindTower((int)MathF.Floor(feet.X), (int)MathF.Floor(feet.Z),
                WorldGenerationConfig.TowerLocateRings, out var found,
                (x, z) => towers?.RecordAt(x, z)?.BossDefeated == true))
            return (string.Create(CultureInfo.InvariantCulture,
                $"No battle tower within {WorldGenerationConfig.TowerLocateRings * WorldGenerationConfig.TowerCellBlocks} blocks"), null);

        const string prefix = "Nearest battle tower at ";
        var coordinates = string.Create(CultureInfo.InvariantCulture, $"[{found.X}, {found.Y}, {found.Z}]");
        var text = prefix + coordinates + string.Create(CultureInfo.InvariantCulture, $", {found.Distance:0} blocks away");
        var command = TeleportCommand.LineTo(new Vector3(found.LandX + 0.5f, found.LandFeetY, found.LandZ + 0.5f));
        return (text, new ChatLink(prefix.Length, coordinates.Length, command));
    }
}
