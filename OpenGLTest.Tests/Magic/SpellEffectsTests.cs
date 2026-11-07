using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using VoxelEngine.Magic;
using VoxelEngine.Tests.Interaction;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>
/// What an accepted cast does (<see cref="SpellEffects"/>), over a headless server world: a bolt lands its
/// element on the cell or mob it hits (priming TNT, lighting a pile, freezing water, planting a tree, burning
/// and hurting a mob by the recipe's damage factor), a self cast applies the element to an immune-aware caster,
/// and only an authoritative world installs the effect at all.
/// </summary>
[Collection("Worldgen seed")]
public sealed class SpellEffectsTests : IDisposable
{
    private SupportTestWorld? _world;
    private World? _clientWorld;
    private double _now = 10.0;

    public SpellEffectsTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        _world?.Dispose();
        _clientWorld?.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private SupportTestWorld World() => _world ??= new SupportTestWorld();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    private static SpellPart[] Parts(params string[] keys) =>
        keys.Select(k => SpellPartRegistry.TryGetByKey(k) ?? throw new InvalidOperationException(k)).ToArray();

    // Binds the parts into slot 0 of a staff in the player's hand, aims the player's eye at the target and casts
    private SpellStats Cast(SupportTestWorld world, Vector3 target, params string[] keys)
    {
        var parts = Parts(keys);
        StaffFixture.Hold(world.Player.Inventory, "wooden_staff", out var staff, out var stack);
        var stats = StaffFixture.Bind(staff, stack, 0, parts);
        var context = world.Player.Context;
        context.Forward = Vector3.Normalize(target - context.Eye);
        context.ViewDirection = context.Forward;
        _now += 10.0;
        Assert.Equal(CastVerdict.Cast, world.World.CastFor(context, 0, new CastPacing(), _now, 0));
        return stats;
    }

    // Flies every bolt until each has landed or run out of range
    private static void Fly(SupportTestWorld world)
    {
        var spells = world.World.Spells!;
        for (var i = 0; i < 600 && spells.Projectiles.Count > 0; i++)
            spells.Update(1f / 60f);
        Assert.Empty(spells.Projectiles);
    }

    private static Vector3 Center(int x, int y, int z) => new(x + 0.5f, y + 0.5f, z + 0.5f);

    [Fact]
    public void AFireBoltPrimesTnt()
    {
        var world = World();
        var (x, y, z) = (8, world.Top + 1, 4);
        world.Load(x, y, z);
        world.Set(x, y, z, Block("tnt"));

        Cast(world, Center(x, y, z), "element/fire", "shape/projectile");
        Fly(world);

        Assert.Equal(0, world.IdAt(x, y, z));
        Assert.Single(world.World.PrimedTnt);
    }

    [Fact]
    public void AFireBoltLightsAFullBranchPile()
    {
        var world = World();
        var (x, y, z) = (8, world.Top + 1, 4);
        world.Load(x, y, z);
        var pile = ContentRegistry.RequireBlock("branch_pile");
        world.Set(x, y, z, pile.Id, (byte)pile.StackMax);
        IItemUseWorld use = world.World.ItemUse;
        Assert.False(use.IsCampfireLit(x, y, z));

        Cast(world, new Vector3(x + 0.5f, y + 0.2f, z + 0.5f), "element/fire", "shape/projectile");
        Fly(world);

        Assert.True(use.IsCampfireLit(x, y, z));
    }

    [Fact]
    public void AFrostBoltFreezesAWaterSource()
    {
        var world = World();
        var (x, y, z) = (8, world.Top + 1, 4);
        world.Load(x, y, z);
        var water = Block("water");
        world.Set(x, y, z, water);
        var frozen = BlockType.FreezesToById[water];
        Assert.NotEqual(0, frozen);

        Cast(world, Center(x, y, z), "element/frost", "shape/projectile");
        Fly(world);

        Assert.Equal(frozen, world.IdAt(x, y, z));
    }

    [Fact]
    public void ANatureBoltPlantsATreeOnTheGround()
    {
        var world = World();
        var (x, y, z) = (8, world.Top, 6);
        world.Load(x, y, z);
        world.Set(x, y, z, TerrainGenerator.SurfaceBlock);
        Assert.Equal(0, world.IdAt(x, y + 1, z));

        var nature = Parts("element/nature")[0];
        Assert.NotNull(nature.Plant);
        Cast(world, new Vector3(x + 0.5f, y + 0.99f, z + 0.5f), "element/nature", "shape/projectile");
        Fly(world);

        Assert.Equal(TreeSpeciesRegistry.TryGet(nature.Plant!)!.WoodId, world.IdAt(x, y + 1, z));
    }

    [Fact]
    public void ABoltDamagesAMobByItsDamageFactorAndAppliesTheElementEffect()
    {
        var world = World();
        var top = world.Top + 1;
        world.Load(8, top, 3);
        world.Load(4, top, 8);
        Assert.True(world.World.SpawnEntity("goblin", new Vector3(8.5f, top, 3.5f)));
        Assert.True(world.World.SpawnEntity("goblin", new Vector3(4.5f, top, 8.5f)));
        world.World.RefreshMobs();
        var goblins = world.World.Goblins.ToArray();
        Assert.Equal(2, goblins.Length);
        var plain = goblins.First(g => g.Position.Z < 6f);
        var powered = goblins.First(g => g.Position.Z > 6f);
        var burning = StatusEffectRegistry.Require(Parts("element/fire")[0].Effect!);

        var plainBefore = plain.Health;
        var plainStats = Cast(world, plain.Position, "element/fire", "shape/projectile");
        Fly(world);
        var plainDamage = plainBefore - plain.Health;
        Assert.Equal(SpellImpactRules.DamageFor(plainStats), plainDamage);
        Assert.True(plain.Effects.IsActive(burning));

        var poweredBefore = powered.Health;
        Cast(world, powered.Position, "element/fire", "shape/projectile", "modifier/power");
        Fly(world);
        Assert.True(poweredBefore - powered.Health > plainDamage);
    }

    [Fact]
    public void ASelfCastAppliesTheEffectToASurvivalCasterOnly()
    {
        var world = World();
        var frostburn = StatusEffectRegistry.Require(Parts("element/frost")[0].Effect!);
        var below = world.Player.Context.Eye - new Vector3(0f, 3f, 0f);

        world.Player.Creative = true;
        world.Player.Context.IsCreative = true;
        Cast(world, below, "element/frost", "shape/self");
        Assert.False(world.Player.Effects.IsActive(frostburn));

        world.Player.Creative = false;
        world.Player.Context.IsCreative = false;
        Cast(world, below, "element/frost", "shape/self");
        Assert.True(world.Player.Effects.IsActive(frostburn));
        Assert.Empty(world.World.Spells!.Projectiles);
    }

    [Fact]
    public void OnlyAnAuthoritativeWorldInstallsTheEffect()
    {
        var server = World().World;
        Assert.NotNull(server.Spells);
        Assert.NotNull(server.SpellEffect);

        _world!.Dispose();
        _world = null;
        _clientWorld = new World(null!, null!, null!, null!, null!, null!, null, null, clientMode: true);
        Assert.Null(_clientWorld.Spells);
        Assert.Null(_clientWorld.SpellEffect);
    }
}
