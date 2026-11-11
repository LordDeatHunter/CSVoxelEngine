using System.Linq;
using System.Numerics;
using Silk.NET.Input;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.Networking.ServerSide;
using VoxelEngine.Player;
using VoxelEngine.Status;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.Status;

/// <summary>
/// The movement and attack factors of a slowing status effect: the local player's hit pace
/// stretches (and stays legal to the server's minimum-gap pacing), and a slowed goblin and a
/// slowed player cover less ground in the same time.
/// </summary>
public sealed class StatusEffectFactorTests
{
    public StatusEffectFactorTests() => TestContent.EnsureLoaded();

    private static StatusEffect Slow(float move, float attack) =>
        StatusEffectLoader.Compile("slow", new StatusEffectDefinition
        {
            DurationSeconds = 60f, MoveFactor = move, AttackFactor = attack,
        }, "slow.json", 0);

    [Fact]
    public void ASlowedHitGapIsLongerAndStaysLegalOnTheServer()
    {
        var held = ItemRegistry.All.Select(i => (Item?)i).Append(null);
        foreach (var item in held)
        {
            var plain = Mining.HitGapSeconds(item);
            Assert.Equal(plain, Mining.HitGapSeconds(item, 1f));
            var slowed = Mining.HitGapSeconds(item, 0.6f);
            Assert.True(slowed > plain, $"{item?.Name ?? "bare hand"}: slowed gap {slowed} not above {plain}");
            Assert.True(slowed >= AttackPacing.MinGapSeconds(NetAttackKind.Punch, NetAttackKind.Punch, item),
                $"{item?.Name ?? "bare hand"}: a slowed punch would be refused by the server");
            // A stronger slow is a longer gap
            Assert.True(Mining.HitGapSeconds(item, 0.3f) > slowed);
        }
    }

    [Fact]
    public void AFrostburnedMobMovesLessInTheSameTime()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var player = new Camera(null, null);
        player.TestTeleport(new Vector3(8.5f, 1f + Camera.EyeHeight, 40.5f), yaw: 0f, pitch: 0f);

        float Walked(bool slowed)
        {
            var start = new Vector3(8.5f, 1f + Goblin.Height * 0.5f, 8.5f);
            var goblin = new Goblin(EntityRegistry.Require("goblin"), start, ammo: 0, spawnIn: false);
            if (slowed) Assert.True(goblin.Effects.Apply(Slow(0.5f, 1f), 1f));
            IMobTarget target = player;
            for (var i = 0; i < 60; i++)
                goblin.Update(1f / 60f, chunks, target);
            return new Vector2(goblin.Position.X - start.X, goblin.Position.Z - start.Z).Length();
        }

        var free = Walked(slowed: false);
        var slow = Walked(slowed: true);
        Assert.True(free > 0.5f, $"the unslowed goblin did not walk ({free})");
        Assert.True(slow < free * 0.8f, $"the slowed goblin walked {slow} vs {free}");
        Assert.True(slow > 0f, "the slowed goblin did not move at all");
    }

    [Fact]
    public void ASlowedPlayerWalksLessFarInTheSameFrames()
    {
        float Walked(bool slowed)
        {
            var manager = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
            var camera = new Camera(null, null);
            camera.TestTeleport(new Vector3(2.5f, 1f + Camera.EyeHeight, 8.5f), yaw: 0f, pitch: 0f);
            camera.Effects = new StatusEffectSet();
            if (slowed) Assert.True(camera.Effects.Apply(Slow(0.5f, 1f), 1f));
            var keys = new ScriptedKeyboard();
            keys.Press([Key.W]);
            var start = camera.Position.X;
            for (var i = 0; i < 20; i++)
                camera.Update(0.05f, keys, manager);
            keys.ReleaseAll();
            return camera.Position.X - start;
        }

        var free = Walked(slowed: false);
        var slow = Walked(slowed: true);
        Assert.True(free > 1f, $"the unslowed player did not walk ({free})");
        Assert.True(slow < free * 0.8f, $"the slowed player walked {slow} vs {free}");
        Assert.True(slow > 0f, "the slowed player did not move at all");
    }
}
