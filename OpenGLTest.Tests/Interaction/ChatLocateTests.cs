using System;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using VoxelEngine.Interaction;
using VoxelEngine.Player;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The <c>/locate</c> answer both runners send (<see cref="ChatCommands.Locate"/>): the coordinates it shows are the linked
/// span, they name the tower the locator finds, and the link suggests a teleport whose eye stands on the landing beside it;
/// a request for nothing it knows answers with no link.
/// </summary>
[Collection("Worldgen seed")]
public sealed class ChatLocateTests
{
    public ChatLocateTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    [Fact]
    public void ALocateReplyLinksTheCoordinatesItShowsToATeleportBesideTheTower()
    {
        Assert.True(StructureLocator.TryFindTower(0, 0, WorldGenerationConfig.TowerLocateRings, out var location),
            "no tower within the locate rings of the origin");

        var (text, link) = ChatCommands.Locate(["tower"], new Vector3(0.5f, 80f, 0.5f));

        Assert.NotNull(link);
        Assert.True(link!.Value.FitsIn(text));
        var shown = text.Substring(link.Value.Start, link.Value.Length);
        var numbers = Regex.Matches(shown, @"-?\d+").Select(m => int.Parse(m.Value)).ToArray();
        Assert.Equal([location.X, location.Y, location.Z], numbers);

        var parsed = ChatCommands.Parse(link.Value.Command);
        Assert.Equal(ChatCommands.CommandKind.Teleport, parsed.Kind);
        var target = ChatCommands.TeleportTarget(parsed.Args);
        Assert.NotNull(target);
        Assert.Equal(location.LandX, (int)MathF.Floor(target!.Value.X));
        Assert.Equal(location.LandZ, (int)MathF.Floor(target.Value.Z));
        Assert.InRange(MathF.Abs(target.Value.Y - Camera.EyeHeight - location.LandFeetY), 0f, 0.01f);
    }

    [Fact]
    public void ALocateWithoutAKnownStructureAnswersWithoutALink()
    {
        Assert.Null(ChatCommands.Locate([], Vector3.Zero).Link);
        Assert.Null(ChatCommands.Locate(["village"], Vector3.Zero).Link);
    }
}
