using System;
using System.Numerics;
using VoxelEngine.Entities;
using Xunit;

namespace VoxelEngine.Tests.Entities;

/// <summary>
/// Mob fall damage (<see cref="MobFallDamage"/>, billed by <see cref="Mob"/> on each landing): a short
/// drop is free, the damage never falls as the drop grows, a mob dropped off a height is hurt, and a
/// slime's own hops never hurt it at any size.
/// </summary>
public sealed class MobFallDamageTests
{
    public MobFallDamageTests() => TestContent.EnsureLoaded();

    private const float Dt = 1f / 60f;

    [Theory]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(100)]
    public void AShortDropIsFreeAndTheDamageNeverFallsAsTheDropGrows(int maxHealth)
    {
        var safe = WorldGenerationConfig.MobSafeFallBlocks;
        Assert.Equal(0, MobFallDamage.Damage(0f, safe, maxHealth));
        Assert.Equal(0, MobFallDamage.Damage(safe, safe, maxHealth));

        var previous = 0;
        for (var drop = 0f; drop <= 40f; drop += 0.05f)
        {
            var damage = MobFallDamage.Damage(drop, safe, maxHealth);
            Assert.True(damage >= previous, $"a {drop:F2}-block drop deals {damage}, less than the shorter one's {previous}");
            Assert.InRange(damage, 0, maxHealth);
            previous = damage;
        }

        Assert.True(previous > 0, "no drop ever hurts");
        // A higher safe height never hurts more
        for (var drop = 0f; drop <= 40f; drop += 0.25f)
            Assert.True(MobFallDamage.Damage(drop, safe + 2f, maxHealth) <= MobFallDamage.Damage(drop, safe, maxHealth));
    }

    // A goblin let fall from feet height above the stone floor (feet land at y = 1), with nobody to chase.
    private static Goblin DropGoblin(float dropBlocks)
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var goblin = new Goblin(EntityRegistry.Require("goblin"),
            new Vector3(8.5f, 1f + dropBlocks + Goblin.Height * 0.5f, 8.5f), 0, spawnIn: false);
        for (var i = 0; i < 180; i++)
            goblin.Update(Dt, chunks, null);
        Assert.True(goblin.IsGrounded);
        return goblin;
    }

    [Fact]
    public void AGoblinDroppedOffAHeightIsHurtAndAShortHopIsNot()
    {
        var hop = DropGoblin(1.5f);
        Assert.Equal(hop.MaxHealth, hop.Health);

        var fall = DropGoblin(7f);
        Assert.True(fall.Health < fall.MaxHealth, "a 7-block fall cost the goblin nothing");
        Assert.True(fall.HitsTaken > 0);

        // A longer fall never costs less
        var longer = DropGoblin(10f);
        Assert.True(longer.Health <= fall.Health);
    }

    [Fact]
    public void AGoblinLandingInWaterIsNotHurt()
    {
        var chunks = MobTestWorld.Pool(waterCells: 2);
        var goblin = new Goblin(EntityRegistry.Require("goblin"),
            new Vector3(8.5f, 13f + Goblin.Height * 0.5f, 8.5f), 0, spawnIn: false);
        for (var i = 0; i < 240; i++)
            goblin.Update(Dt, chunks, null);
        Assert.Equal(goblin.MaxHealth, goblin.Health);
    }

    // Three by three loaded chunks with a stone floor at y = 0, wide enough for a giant's hops.
    private static ChunkManager WideFloor()
    {
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var cx = -1; cx <= 1; cx++)
        for (var cz = -1; cz <= 1; cz++)
        {
            var pos = new Vector3(cx, 0, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
            chunks.Chunks[pos] = chunk;
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, 0, z, stone, 0);
        }
        return chunks;
    }

    [Fact]
    public void ASlimesOwnHopsNeverHurtIt()
    {
        var chunks = WideFloor();
        for (var size = SlimeSize.Min; size <= SlimeSize.Max; size++)
        {
            var slime = new Slime(EntityRegistry.Require("slime"),
                new Vector3(-10.5f, 1.05f + Slime.HalfExtentFor(size), 8.5f), size);
            // A target across the floor: the slime hops over to it and then keeps hopping on the spot
            var target = new Vector3(8.5f, 1.62f, 8.5f);
            var hops = 0;
            for (var i = 0; i < (int)(20f / Dt); i++)
            {
                var wasGrounded = slime.IsGrounded;
                slime.Update(Dt, chunks, target);
                if (wasGrounded && slime.IsAirborne) hops++;
            }

            Assert.True(hops >= 3, $"a size {size} slime only hopped {hops} times");
            Assert.Equal(slime.MaxHealth, slime.Health);
        }
    }

    [Fact]
    public void ASlimeFallingOffACliffPastItsHopIsHurt()
    {
        var chunks = WideFloor();
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(8.5f, 15f, 8.5f));
        Assert.True(14f - Slime.HalfExtent > slime.SafeFallBlocks + 1f, "the drop must clear the slime's safe height");
        for (var i = 0; i < 180; i++)
            slime.Update(Dt, chunks, null);
        Assert.True(slime.Health < slime.MaxHealth);
    }
}
