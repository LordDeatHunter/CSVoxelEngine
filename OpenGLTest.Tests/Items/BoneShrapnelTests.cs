using System;
using System.Numerics;
using VoxelEngine.Config;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.Tests.Interaction;
using VoxelEngine.Tests.Support;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The bone-shrapnel rules: which weapon and gear combination sprays which burst
/// (<see cref="ShrapnelProfile.Resolve"/>), that only a landed hit of a shrapnel weapon sprays at all,
/// and that a fragment wounds a nearby enemy but never the mob it burst out of.
/// </summary>
[Collection("Worldgen seed")]
public sealed class BoneShrapnelTests : IDisposable
{
    private SupportTestWorld? _world;

    public BoneShrapnelTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        _world?.Dispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private SupportTestWorld World() => _world ??= new SupportTestWorld();

    private static Item Blade => ContentRegistry.RequireItem("bone_breaker_blade");

    [Fact]
    public void AWeaponWithoutShrapnelAndNoCharmSpraysNothing()
    {
        Assert.True(ShrapnelProfile.Resolve(ContentRegistry.RequireItem("iron_sword"), false).IsEmpty);
        Assert.True(ShrapnelProfile.Resolve(null, false).IsEmpty);
    }

    [Fact]
    public void TheCharmAloneSpraysFewerWeakerShardsThanTheBlade()
    {
        var blade = ShrapnelProfile.Resolve(Blade, false);
        var charm = ShrapnelProfile.Resolve(ContentRegistry.RequireItem("iron_sword"), true);

        Assert.False(blade.IsEmpty);
        Assert.False(charm.IsEmpty);
        Assert.True(charm.Count < blade.Count, "the blade's own burst is the stronger one");
        Assert.True(charm.Damage < blade.Damage);
    }

    [Fact]
    public void AWornCharmSharpensTheBladesBurst()
    {
        var alone = ShrapnelProfile.Resolve(Blade, false);
        var sharp = ShrapnelProfile.Resolve(Blade, true);

        Assert.True(sharp.Count > alone.Count);
        Assert.True(sharp.Damage > alone.Damage);
        Assert.Equal(Raised(alone.Count), sharp.Count);
        Assert.Equal(Raised(alone.Damage), sharp.Damage);
    }

    private static int Raised(int value) =>
        Math.Max(1, (int)MathF.Round(value * ShrapnelConfig.TogetherFactor, MidpointRounding.AwayFromZero));

    [Fact]
    public void OnlyALandedHitWithShrapnelSprays()
    {
        var world = World();
        world.Load(8, world.Top + 1, 8);
        Assert.True(world.World.SpawnEntity("slime", new Vector3(8.5f, world.Top + 1, 8.5f)));
        var mob = world.World.Mobs[0];
        var eye = mob.Position + new Vector3(0f, 0f, -1f);

        Assert.True(world.World.Strikes.StrikeMob(mob, 1, eye));
        Assert.Equal(0, world.World.Shrapnel.LiveCount);

        Assert.True(world.World.Strikes.StrikeMob(mob, 1, eye, shrapnel: ShrapnelProfile.Resolve(Blade, false)));
        Assert.True(world.World.Shrapnel.LiveCount > 0);
    }

    [Fact]
    public void AFragmentWoundsANearbyEnemyAndNeverItsSource()
    {
        var world = World();
        world.Load(8, world.Top + 1, 8);
        Assert.True(world.World.SpawnEntity("slime", new Vector3(4.5f, world.Top + 1, 8.5f)));
        Assert.True(world.World.SpawnEntity("slime", new Vector3(6.5f, world.Top + 1, 8.5f)));
        var source = world.World.Mobs[0];
        var target = world.World.Mobs[1];
        var sourceHealth = source.Health;
        var targetHealth = target.Health;

        world.World.Shrapnel.Launch(source.Position + new Vector3(0.2f, 0f, 0f), new Vector3(8f, 0f, 0f), 1, source, null);
        for (var i = 0; i < 120 && world.World.Shrapnel.LiveCount > 0; i++)
            world.World.Shrapnel.Update(1f / 60f);

        Assert.Equal(0, world.World.Shrapnel.LiveCount);
        Assert.Equal(targetHealth - 1, target.Health);
        Assert.Equal(sourceHealth, source.Health);
    }

    [Fact]
    public void AFragmentsOwnHitNeverSpraysAgainEvenForACharmWearer()
    {
        var world = World();
        world.Load(8, world.Top + 1, 8);
        Assert.True(world.World.SpawnEntity("slime", new Vector3(4.5f, world.Top + 1, 8.5f)));
        Assert.True(world.World.SpawnEntity("slime", new Vector3(6.5f, world.Top + 1, 8.5f)));
        var source = world.World.Mobs[0];
        var target = world.World.Mobs[1];

        var inventory = Inventory.Empty();
        var charm = SyntheticGear.Create(GearSlot.Accessory, [GearAbilities.Name(GearAbility.BoneShrapnel)]);
        Assert.True(inventory.Equipment.TryEquipAuto(SyntheticGear.Own(inventory, charm)));
        var striker = new PlayerHealth();
        striker.ApplyGear(inventory.Equipment, simulatesMana: false);

        var sourceHealth = source.Health;
        var targetHealth = target.Health;
        world.World.Shrapnel.Launch(source.Position + new Vector3(0.2f, 0f, 0f), new Vector3(8f, 0f, 0f), 1, source, striker);
        for (var i = 0; i < 120 && world.World.Shrapnel.LiveCount > 0; i++)
        {
            world.World.Shrapnel.Update(1f / 60f);
            Assert.True(world.World.Shrapnel.LiveCount <= 1, "a fragment's own hit sprayed another burst");
        }

        Assert.Equal(0, world.World.Shrapnel.LiveCount);
        Assert.Equal(targetHealth - 1, target.Health);
        Assert.Equal(sourceHealth, source.Health);
    }

    [Fact]
    public void ShrapnelIsRefusedOnAPunchItemAndParserRoundTripsABlade()
    {
        var definition = new ItemDefinition.ShrapnelDefinition { Count = 2, Damage = 1 };

        var error = Assert.Throws<InvalidOperationException>(() =>
            ItemLoader.ParseShrapnel(definition, AttackPattern.Punch, "items/x.json"));
        Assert.Contains("items/x.json", error.Message);

        Assert.Null(ItemLoader.ParseShrapnel(null, AttackPattern.Sword, "items/x.json"));

        var parsed = ItemLoader.ParseShrapnel(definition, AttackPattern.Sword, "items/x.json");
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Count);
        Assert.Equal(1, parsed.Value.Damage);
    }
}
