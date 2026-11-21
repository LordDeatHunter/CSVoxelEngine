using System;
using System.Globalization;
using System.Numerics;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.UI.Hud;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Commands.Definitions;

/// <summary>
/// <c>/locate tower</c> and <c>/locate volcano</c>: the nearest battle tower or volcano, its coordinates a link whose
/// click suggests a <c>/tp</c> to the ground beside it (<see cref="Tower"/>, <see cref="Volcano"/>). Reads worldgen, so
/// it runs where the seed is: singleplayer, or the server for a connected player, searched from that player's eye.
/// </summary>
public static class LocateCommand
{
    public static void Register(CommandDispatcher dispatcher) => dispatcher.Register(
        Cmd.Literal("locate").Describe("Find the nearest battle tower or volcano")
            .Then(Cmd.Literal("tower").Executes(c =>
            {
                var (text, link) = Tower(c.Self.Feet, c.Source.Host.Towers);
                c.Reply(text, link);
            }))
            .Then(Cmd.Literal("volcano").Executes(c =>
            {
                var (text, link) = Volcano(c.Self.Feet);
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

        return Found("Nearest battle tower at ", found);
    }

    /// <summary>
    /// The answer for a search from <paramref name="feet"/>: the nearest volcano within
    /// <see cref="WorldGenerationConfig.VolcanoLocateRings"/> volcano cells (<see cref="StructureLocator.TryFindVolcano"/>),
    /// its coordinates linked to a teleport onto its rim, or a line with no link when none is found (every world but a
    /// Dynamic one, which alone raises volcanoes).
    /// </summary>
    public static (string Text, ChatLink? Link) Volcano(Vector3 feet)
    {
        if (!StructureLocator.TryFindVolcano((int)MathF.Floor(feet.X), (int)MathF.Floor(feet.Z),
                WorldGenerationConfig.VolcanoLocateRings, out var found))
            return (string.Create(CultureInfo.InvariantCulture,
                $"No volcano within {WorldGenerationConfig.VolcanoLocateRings * WorldGenerationConfig.VolcanoCellBlocks} blocks"), null);
        return Found("Nearest volcano at ", found);
    }

    // "<prefix>[x, y, z], N blocks away", the coordinates a link to a teleport onto the landing
    private static (string Text, ChatLink? Link) Found(string prefix, StructureLocation found)
    {
        var coordinates = string.Create(CultureInfo.InvariantCulture, $"[{found.X}, {found.Y}, {found.Z}]");
        var text = prefix + coordinates + string.Create(CultureInfo.InvariantCulture, $", {found.Distance:0} blocks away");
        var command = TeleportCommand.LineTo(new Vector3(found.LandX + 0.5f, found.LandFeetY, found.LandZ + 0.5f));
        return (text, new ChatLink(prefix.Length, coordinates.Length, command));
    }
}
