using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// The item drop's landing bounce: a drop that lands hops back up once or twice, each hop smaller,
/// and then rests on the floor instead of stopping dead or hopping forever.
/// </summary>
public sealed class DropBounceTests
{
    public DropBounceTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    [Fact]
    public void ADropThatLandsHopsThenSettles()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var drop = new ItemDrop(ContentRegistry.RequireBlock("stone").Id, new Vector3(8.5f, 4f, 8.5f));
        var target = new Vector3(8.5f, 1f, 8.5f);

        var rebounds = new List<float>();
        var previousSpeed = drop.Velocity.Y;
        for (var i = 0; i < 1200; i++)
        {
            drop.Update(Dt, target, inRange: false, chunks);
            var speed = drop.Velocity.Y;
            // A falling drop turned rising: the floor bounced it
            if (previousSpeed < 0f && speed > 0f) rebounds.Add(speed);
            previousSpeed = speed;
        }

        Assert.True(rebounds.Count >= 1, "the drop never hopped");
        Assert.True(rebounds.Count <= 6, $"the drop hopped {rebounds.Count} times, never settling");
        for (var i = 1; i < rebounds.Count; i++)
            Assert.True(rebounds[i] < rebounds[i - 1], "a hop did not die off");
        Assert.True(drop.IsGrounded, "the drop did not come to rest");
        Assert.True(drop.Position.Y < 1.5f, "the drop did not settle on the floor");
    }

    [Fact]
    public void ADropAlreadyRestingStaysPut()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var drop = new ItemDrop(ContentRegistry.RequireBlock("stone").Id, new Vector3(8.5f, 1.12f, 8.5f));
        var target = new Vector3(8.5f, 1f, 8.5f);

        for (var i = 0; i < 300; i++)
            drop.Update(Dt, target, inRange: false, chunks);

        Assert.True(drop.IsGrounded);
        Assert.Equal(1.12f, drop.Position.Y, 2);
    }
}
