using System.Numerics;
using VoxelEngine.Entities;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>A thrown drop waits its own grace period before it can be latched onto, a mined drop only the short one.</summary>
public sealed class ItemDropPickupDelayTests
{
    private const ushort AnyBlockId = 1;
    private const float Step = 1f / 60f;

    // A target far away keeps the drop from being collected on contact; only its latch is observed
    private static readonly Vector3 FarTarget = new(1000f, 1000f, 1000f);

    [Fact]
    public void ThrownDropIsNotAttractedUntilItsGraceEnds()
    {
        var drop = new ItemDrop(AnyBlockId, Vector3.Zero, pickupDelaySeconds: PlayerConfigDelay());

        Tick(drop, PlayerConfigDelay() * 0.5f);
        Assert.False(drop.IsAttracted);

        Tick(drop, PlayerConfigDelay());
        Assert.True(drop.IsAttracted);
    }

    [Fact]
    public void MinedDropIsAttractedSoonerThanAThrownOne()
    {
        var mined = new ItemDrop(AnyBlockId, Vector3.Zero);
        var thrown = new ItemDrop(AnyBlockId, Vector3.Zero, pickupDelaySeconds: PlayerConfigDelay());

        Tick(mined, PlayerConfigDelay() * 0.5f);
        Tick(thrown, PlayerConfigDelay() * 0.5f);

        Assert.True(mined.IsAttracted);
        Assert.False(thrown.IsAttracted);
    }

    private static float PlayerConfigDelay() => PlayerConfig.ThrownPickupDelaySeconds;

    // Chunks are null: the drop skips terrain physics, which these rules do not depend on
    private static void Tick(ItemDrop drop, float seconds)
    {
        for (var elapsed = 0f; elapsed < seconds; elapsed += Step)
            drop.Update(Step, FarTarget, inRange: true, chunks: null!);
    }
}
