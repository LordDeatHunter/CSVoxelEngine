using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.Entities.TowerMobs;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Interaction;

/// <summary>
/// The undead bonus through the one strike path (<see cref="MobStrikes.StrikeMob"/>): a player wearing gear with
/// undead damage hits an undead mob (a skeleton) harder, and a living mob exactly as hard as without it.
/// </summary>
public sealed class UndeadStrikeTests
{
    public UndeadStrikeTests() => TestContent.EnsureLoaded();

    private const int Damage = 6;

    // Server mode draws no sparks or damage numbers, so neither the world nor the debris is ever read
    private static MobStrikes Strikes() => new(null!, null!, null, serverMode: true);

    private static PlayerHealth Hallowed()
    {
        var inventory = Inventory.Empty();
        var charm = SyntheticGear.Create(GearSlot.Accessory, [],
            new Dictionary<string, float> { ["undeadDamage"] = 0.5f });
        Assert.True(inventory.Equipment.TryEquipAuto(SyntheticGear.Own(inventory, charm)));
        var health = new PlayerHealth();
        health.ApplyGear(inventory.Equipment, simulatesMana: false);
        return health;
    }

    private static Skeleton NewSkeleton() =>
        new(EntityRegistry.Require(Skeleton.EntityName), new Vector3(8.5f, 2f, 8.5f), spawnIn: false);

    private static Hellbrute NewBrute() =>
        new(EntityRegistry.Require(Hellbrute.EntityName), new Vector3(8.5f, 2f, 8.5f), spawnIn: false);

    private static int LostTo(Mob mob, PlayerHealth? striker)
    {
        var before = mob.Health;
        Strikes().StrikeMob(mob, Damage, mob.Position + new Vector3(-1f, 0f, 0f), striker);
        return before - mob.Health;
    }

    [Fact]
    public void TheUndeadBonusRaisesAStrikeOnASkeletonButNotOnALivingMob()
    {
        var striker = Hallowed();

        Assert.True(LostTo(NewSkeleton(), striker) > LostTo(NewSkeleton(), null),
            "the undead bonus did not raise a strike on a skeleton");
        Assert.Equal(LostTo(NewBrute(), null), LostTo(NewBrute(), striker));

        // A striker wearing nothing hits a skeleton exactly as hard as no striker at all
        Assert.Equal(LostTo(NewSkeleton(), null), LostTo(NewSkeleton(), new PlayerHealth()));
    }

    private static SkeletonKing NewKing()
    {
        var center = new Vector3(8.5f, 2f, 8.5f);
        return new SkeletonKing(EntityRegistry.Require(SkeletonKing.EntityName), center,
            SkeletonKingHome.Unbound(center - new Vector3(0f, MobConfig.SkeletonKingHeight / 2f, 0f)), spawnIn: false);
    }

    [Fact]
    public void TheUndeadBonusRaisesAStrikeOnTheSkeletonKing()
    {
        var striker = Hallowed();

        Assert.True(LostTo(NewKing(), striker) > LostTo(NewKing(), null),
            "the undead bonus did not raise a strike on the Skeleton King");
        Assert.Equal(LostTo(NewKing(), null), LostTo(NewKing(), new PlayerHealth()));
    }
}
