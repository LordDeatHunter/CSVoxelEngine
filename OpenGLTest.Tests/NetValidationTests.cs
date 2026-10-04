using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The server's client-input sanity rules: a plausible move is accepted, a teleport-scale one is
/// rejected (leaving the authoritative position), and an edit must be within interaction reach.
/// </summary>
public sealed class NetValidationTests
{
    [Fact]
    public void APlausibleMoveIsAcceptedAndATeleportIsRejected()
    {
        Assert.True(NetValidation.AcceptMove(Vector3.Zero, new Vector3(1f, 0f, 0f), 0.05, 80f, out var ok));
        Assert.Equal(new Vector3(1f, 0f, 0f), ok);

        Assert.False(NetValidation.AcceptMove(Vector3.Zero, new Vector3(500f, 0f, 0f), 0.05, 80f, out var rejected));
        Assert.Equal(Vector3.Zero, rejected);
    }

    [Fact]
    public void AnEditIsOnlyInReachNearTheEye()
    {
        var eye = new Vector3(0f, 0f, 0f);
        Assert.True(NetValidation.InEditReach(eye, 4, 0, 0, 6f));
        Assert.False(NetValidation.InEditReach(eye, 20, 0, 0, 6f));
    }
}
