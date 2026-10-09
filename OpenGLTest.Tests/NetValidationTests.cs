using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The server's client-input sanity rules: a plausible move is accepted verbatim, a teleport-scale
/// one is clamped toward its target at the speed cap (so the authoritative position converges
/// instead of freezing), and an edit must be within the client's own interaction reach.
/// </summary>
public sealed class NetValidationTests
{
    private const float MaxSpeed = WorldGenerationConfig.NetMaxMoveBlocksPerSecond;

    [Fact]
    public void APlausibleMoveIsAcceptedVerbatim()
    {
        var from = new Vector3(3f, 70f, -2f);
        var to = from + new Vector3(1f, 0.5f, 0f);

        Assert.True(NetValidation.AcceptMove(from, to, 0.05, MaxSpeed, out var accepted));
        Assert.Equal(to, accepted);
    }

    [Fact]
    public void ARejectedMoveAdvancesTowardItsTargetByExactlyTheAllowedDistance()
    {
        var from = new Vector3(3f, 70f, -2f);
        var to = from + new Vector3(4000f, -300f, 1200f);
        const double delta = 0.05;

        Assert.False(NetValidation.AcceptMove(from, to, delta, MaxSpeed, out var accepted));

        var allowed = NetValidation.AllowedDistance(delta, MaxSpeed);
        Assert.Equal(allowed, Vector3.Distance(from, accepted), 3);
        // Same direction as the requested move: the clamp lies on the segment from -> to.
        var wanted = Vector3.Normalize(to - from);
        var moved = Vector3.Normalize(accepted - from);
        Assert.True(Vector3.Dot(wanted, moved) > 0.9999f, $"clamped move points {moved}, wanted {wanted}");
        Assert.True(Vector3.Distance(accepted, to) < Vector3.Distance(from, to));
    }

    [Fact]
    public void RepeatedRejectedStatesConvergeOnTheClient()
    {
        // A client far ahead of the authoritative position keeps sending the same state: each
        // clamp closes the gap, so it is eventually accepted rather than rejected forever.
        var authoritative = Vector3.Zero;
        var client = new Vector3(500f, 0f, 0f);
        var accepted = false;
        for (var i = 0; i < 1000 && !accepted; i++)
        {
            accepted = NetValidation.AcceptMove(authoritative, client, 0.05, MaxSpeed, out var next);
            authoritative = next;
        }

        Assert.True(accepted);
        Assert.Equal(client, authoritative);
    }

    [Fact]
    public void AnEditIsOnlyInReachWithinTheClientsInteractionReach()
    {
        var eye = new Vector3(0.5f, 64.5f, 0.5f);
        var reach = WorldGenerationConfig.NetMaxEditReachBlocks;
        var near = (int)World.InteractionReach - 1;
        var far = (int)(reach * 3f);

        // Everything the client's own raycast can reach is accepted...
        Assert.True(NetValidation.InEditReach(eye, near, 64, 0, reach));
        Assert.True(NetValidation.InEditReach(eye, 0, 64 - near, 0, reach));
        // ...and a cell well outside it is refused.
        Assert.False(NetValidation.InEditReach(eye, far, 64, 0, reach));
    }
}
