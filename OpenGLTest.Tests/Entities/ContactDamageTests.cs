using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The cactus prick (<see cref="ContactDamage"/>): a body beside a cactus cell touches it (the
/// collision keeps its box flush against the face), one standing on top does not, and the tick
/// bills the block's damage once per <see cref="ContactDamage.TickSeconds"/>.
/// </summary>
public sealed class ContactDamageTests
{
    public ContactDamageTests() => TestContent.EnsureLoaded();

    private static ChunkManager Floor(byte topId)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, topId, 0);
        manager.Chunks[Vector3.Zero] = chunk;
        return manager;
    }

    [Fact]
    public void ABodyBesideTheCactusTouchesItButOneOnTopDoesNot()
    {
        var chunks = Floor(ContentRegistry.RequireBlock("stone").Id);
        var cactus = ContentRegistry.RequireBlock("cactus").Id;
        var chunk = chunks.Chunks[Vector3.Zero];
        chunk.SetBlockRaw(2, 1, 1, cactus, 0);

        // Flush against the cell face, exactly where the collision leaves a body
        var beside = new Vector3(1.7f, 1f, 1.5f);
        Assert.True(ContactDamage.Touches(chunks, beside, PlayerPhysics.PlayerRadius,
            PlayerPhysics.PlayerHeight, out var damage));
        Assert.Equal(1, damage);

        // Standing on top of the cactus: its top face is the floor
        var onTop = new Vector3(2.5f, 2f, 1.5f);
        Assert.False(ContactDamage.Touches(chunks, onTop, PlayerPhysics.PlayerRadius,
            PlayerPhysics.PlayerHeight, out _));

        // Clear of it, and a stone column pricks nothing
        Assert.False(ContactDamage.Touches(chunks, new Vector3(6.5f, 1f, 6.5f),
            PlayerPhysics.PlayerRadius, PlayerPhysics.PlayerHeight, out _));
        chunk.SetBlockRaw(2, 1, 1, ContentRegistry.RequireBlock("stone").Id, 0);
        Assert.False(ContactDamage.Touches(chunks, beside, PlayerPhysics.PlayerRadius,
            PlayerPhysics.PlayerHeight, out _));
    }

    [Fact]
    public void TheTickBillsOncePerHalfSecondAndLeavingClearsIt()
    {
        var timer = 0f;
        const float dt = 1f / 60f;
        var total = 0;
        // 0.4 s of contact: nothing yet
        for (var i = 0; i < 24; i++)
            total += ContactDamage.Tick(ref timer, dt, 1, touching: true);
        Assert.Equal(0, total);

        // Crossing 0.5 s bills exactly one
        for (var i = 0; i < 6; i++)
            total += ContactDamage.Tick(ref timer, dt, 1, touching: true);
        Assert.Equal(1, total);

        // A whole second bills two more
        var before = total;
        for (var i = 0; i < 60; i++)
            total += ContactDamage.Tick(ref timer, dt, 1, touching: true);
        Assert.Equal(before + 2, total);

        // Leaving the cactus clears the partial tick: coming back does not bill instantly
        ContactDamage.Tick(ref timer, dt, 1, touching: false);
        total = 0;
        for (var i = 0; i < 24; i++)
            total += ContactDamage.Tick(ref timer, dt, 1, touching: true);
        Assert.Equal(0, total);
    }
}
