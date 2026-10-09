using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The client's far-LOD request pacing: never more than the in-flight cap outstanding, no region
/// asked twice while its request is still young, and an unanswered request asked again once the
/// resend age has passed.
/// </summary>
public sealed class ClientLodStreamerTests
{
    public ClientLodStreamerTests() => TestContent.EnsureLoaded();

    [Fact]
    public void OutstandingRequestsNeverExceedTheInFlightCap()
    {
        var asked = new List<(int, int, int)>();
        var streamer = new ClientLodStreamer(new LodManager(null!), (l, x, z) => asked.Add((l, x, z)));

        // Many frames, no answers, well inside the resend age
        for (var i = 0; i < 200; i++)
            streamer.Update(Vector3.Zero, 0.016);

        Assert.NotEmpty(asked);
        Assert.True(asked.Count <= WorldGenerationConfig.NetLodInFlightMax,
            $"{asked.Count} requests outstanding, cap {WorldGenerationConfig.NetLodInFlightMax}");
        Assert.True(streamer.InFlight <= WorldGenerationConfig.NetLodInFlightMax);
        Assert.Equal(asked.Count, asked.Distinct().Count());
    }

    [Fact]
    public void AnUnansweredRequestIsAskedAgainOnlyAfterTheResendAge()
    {
        var asked = new List<(int, int, int)>();
        var streamer = new ClientLodStreamer(new LodManager(null!), (l, x, z) => asked.Add((l, x, z)));
        streamer.Update(Vector3.Zero, 0.016);
        var first = asked[0];

        // Just short of the resend age: still outstanding, never re-sent
        var step = WorldGenerationConfig.NetLodResendSeconds / 10.0;
        for (var i = 0; i < 9; i++)
            streamer.Update(Vector3.Zero, step * 0.99);
        Assert.Equal(1, asked.Count(k => k == first));

        // Past it: the request expired and goes out again
        streamer.Update(Vector3.Zero, step * 2);
        Assert.Equal(2, asked.Count(k => k == first));
    }
}
