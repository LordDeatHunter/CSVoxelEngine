using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using VoxelEngine.Commands;
using VoxelEngine.Commands.Definitions;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Commands;

/// <summary>
/// The <c>/locate tower</c> answer (<see cref="LocateCommand.Tower"/>): the coordinates it shows are the linked span
/// and name the tower the locator finds, the link is a <c>/tp</c> that, run, stands the player on the landing beside
/// it, a conquered tower is skipped, and a suggested teleport runs back to its feet under any culture.
/// </summary>
[Collection("Worldgen seed")]
public sealed class LocateCommandTests
{
    private readonly RecordingCommandHost _host = new();

    public LocateCommandTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    [Fact]
    public void ALocateReplyLinksTheCoordinatesItShowsToATeleportOntoTheLanding()
    {
        Assert.True(StructureLocator.TryFindTower(0, 0, WorldGenerationConfig.TowerLocateRings, out var location),
            "no tower within the locate rings of the origin");

        var (text, link) = LocateCommand.Tower(new Vector3(0.5f, 80f, 0.5f), null);

        Assert.NotNull(link);
        Assert.True(link!.Value.FitsIn(text));
        var shown = text.Substring(link.Value.Start, link.Value.Length);
        var numbers = Regex.Matches(shown, @"-?\d+").Select(m => int.Parse(m.Value)).ToArray();
        Assert.Equal([location.X, location.Y, location.Z], numbers);

        var player = _host.Add("Self", new Vector3(0.5f, 80f, 0.5f));
        GameCommands.Create().Execute(link.Value.Command, _host.SourceFor(player));
        Assert.Equal(location.LandX, (int)MathF.Floor(player.Feet.X));
        Assert.Equal(location.LandZ, (int)MathF.Floor(player.Feet.Z));
        Assert.InRange(MathF.Abs(player.Feet.Y - location.LandFeetY), 0f, 0.01f);
    }

    [Fact]
    public void ALocateSkipsATowerWhoseKingHasFallen()
    {
        Assert.True(StructureLocator.TryFindTower(0, 0, WorldGenerationConfig.TowerLocateRings, out var nearest));
        var towers = new TowerManager(null, (_, _) => null);
        var record = towers.Track(TowerRecord.Build((0, 0), nearest.X, nearest.Z, 9,
            [(nearest.Y, nearest.Y + 8, new[] { (nearest.X, nearest.Y, nearest.Z) }, new[] { (nearest.X, nearest.Y + 2, nearest.Z) })],
            hasSite: false));
        record.AltarState = TowerAltarState.Defeated;

        var (text, _) = LocateCommand.Tower(new Vector3(0.5f, 80f, 0.5f), towers);

        Assert.DoesNotContain($"[{nearest.X}, {nearest.Y}, {nearest.Z}]", text);
    }

    [Fact]
    public void ASuggestedTeleportRunsBackToItsFeetUnderAnyCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // A culture whose decimal separator is a comma must not leak into the command
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var commands = GameCommands.Create();
            var player = _host.Add("Self");
            Vector3[] spots = [new(2875.5f, 12.62f, -1410.5f), new(-0.25f, 300f, 7.75f), new(0.5f, -401.38f, -9999.5f)];
            foreach (var feet in spots)
            {
                commands.Execute(TeleportCommand.LineTo(feet), _host.SourceFor(player, CommandPermission.Operator));
                Assert.InRange(Vector3.Distance(player.Feet, feet), 0f, 0.01f);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
