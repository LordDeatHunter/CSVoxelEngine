using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Items;
using VoxelEngine.Player;
using VoxelEngine.Status;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>
/// The rules gear adds on top of the pools: armor against each kind of blow, effect immunity, passives, rolled
/// modifiers and rarity, sets, and how the tooltip and the save carry them.
/// </summary>
public sealed class GearRulesTests
{
    public GearRulesTests() => TestContent.EnsureLoaded();

    private static GearItem Named(string sourceName) =>
        ItemRegistry.All.OfType<GearItem>().First(g => g.SourceName == sourceName);

    private static int Wear(Inventory inventory, GearItem gear)
    {
        Assert.True(inventory.Add(gear.Id));
        var instance = inventory.GearEntries().Last(e => e.ItemId == gear.Id).InstanceId;
        Assert.True(inventory.Equipment.TryEquipAuto(instance));
        return instance;
    }

    private static PlayerHealth Wearing(params string[] names)
    {
        var inventory = Inventory.Empty();
        foreach (var name in names) Wear(inventory, Named(name));
        var health = new PlayerHealth();
        health.ApplyGear(inventory.Equipment, simulatesMana: true);
        return health;
    }

    [Theory]
    [InlineData(DamageSource.Mob)]
    [InlineData(DamageSource.Fall)]
    [InlineData(DamageSource.Lava)]
    [InlineData(DamageSource.Cactus)]
    public void ArmorTakesPointsOffABlowButNeverBelowTheSourcesMinimum(DamageSource source)
    {
        var minimum = ArmorRules.MinDamage(source);
        Assert.True(minimum >= 1);

        Assert.True(ArmorRules.Reduce(10, 4, source) < 10);
        for (var armor = 0; armor <= GearConfig.ArmorCap; armor += 5)
        {
            var dealt = ArmorRules.Reduce(30, armor, source);
            Assert.True(dealt >= System.Math.Min(30, minimum));
            Assert.True(dealt <= 30);
            Assert.True(ArmorRules.Reduce(30, armor + 1, source) <= dealt, "more armor never means more damage");
        }
    }

    [Fact]
    public void ABlowSmallerThanTheMinimumIsNotRaisedToIt()
    {
        Assert.Equal(1, ArmorRules.Reduce(1, GearConfig.ArmorCap, DamageSource.Mob));
        Assert.Equal(0, ArmorRules.Reduce(0, 10, DamageSource.Mob));
    }

    [Theory]
    [InlineData(DamageSource.Drowning)]
    [InlineData(DamageSource.Magic)]
    [InlineData(DamageSource.None)]
    public void DrowningMagicAndUnnamedBlowsIgnoreArmor(DamageSource source) =>
        Assert.Equal(25, ArmorRules.Reduce(25, GearConfig.ArmorCap, source));

    [Fact]
    public void WornArmorReducesANamedBlowAndNotARawOne()
    {
        var bare = new PlayerHealth();
        var armored = Wearing("bronze_cap", "bronze_tunic");
        Assert.True(armored.Armor > 0);

        bare.TakeDamage(20, DamageSource.Mob);
        armored.TakeDamage(20, DamageSource.Mob);
        Assert.True(armored.Health > bare.Health);

        var rawBare = new PlayerHealth();
        var rawArmored = Wearing("bronze_cap", "bronze_tunic");
        rawBare.TakeDamage(20);
        rawArmored.TakeDamage(20);
        Assert.Equal(rawBare.Health, rawArmored.Health);
    }

    [Fact]
    public void ABurningBlowThroughArmorStillHurts()
    {
        var armored = Wearing("bronze_cap", "bronze_tunic", "bronze_pants", "bronze_boots");
        var before = armored.Health;
        armored.TakeDamage(5, DamageSource.Magic);
        Assert.Equal(before - 5, armored.Health);
    }

    [Fact]
    public void ImmunityBlocksOnlyTheEffectsTheGearNames()
    {
        var bare = new PlayerHealth();
        Assert.True(StatusEffects.Apply(bare, false, "burning"));

        var charmed = Wearing("ember_charm");
        Assert.False(StatusEffects.Apply(charmed, false, "burning"));
        Assert.True(StatusEffects.Apply(charmed, false, "frostburn"));
        Assert.False(charmed.Effects.IsActive(StatusEffectRegistry.Require("burning")));
    }

    [Fact]
    public void TheFireAndFrostCharmsEachBlockOnlyTheirOwnEffect()
    {
        var bare = new PlayerHealth();
        Assert.True(StatusEffects.Apply(bare, false, "burning"));
        Assert.True(StatusEffects.Apply(bare, false, "frostburn"));

        var fireward = Wearing("fire_charm");
        Assert.False(StatusEffects.Apply(fireward, false, "burning"));
        Assert.True(StatusEffects.Apply(fireward, false, "frostburn"));

        var frostward = Wearing("frost_charm");
        Assert.False(StatusEffects.Apply(frostward, false, "frostburn"));
        Assert.True(StatusEffects.Apply(frostward, false, "burning"));
    }

    [Fact]
    public void TheFeatherCharmNegatesFallDamageButNoOtherBlow()
    {
        var drop = 1f;
        while (PlayerHealth.FallDamage(drop) <= 0) drop += 1f;
        var damage = PlayerHealth.FallDamage(drop);

        var bare = new PlayerHealth();
        bare.TakeDamage(damage, DamageSource.Fall);
        Assert.True(bare.Health < bare.Max);

        var feathered = Wearing("feather_charm");
        Assert.True(feathered.NegatesFallDamage);
        feathered.TakeDamage(damage, DamageSource.Fall);
        Assert.Equal(feathered.Max, feathered.Health);

        feathered.TakeDamage(damage, DamageSource.Lava);
        Assert.True(feathered.Health < feathered.Max);
    }

    [Fact]
    public void AnOnHitPassiveFiresByItsChanceOnTheStruckMob()
    {
        var wearer = Wearing("ember_charm");
        var slime = new Slime(EntityRegistry.Require("slime"), Vector3.Zero, 1);
        var burning = StatusEffectRegistry.Require("burning");

        Assert.Equal(0, GearPassives.OnHit(wearer, slime, () => 0.99f));
        Assert.False(slime.Effects.IsActive(burning));

        Assert.Equal(1, GearPassives.OnHit(wearer, slime, () => 0f));
        Assert.True(slime.Effects.IsActive(burning));
        Assert.Equal(0, GearPassives.OnHit(new PlayerHealth(), slime, () => 0f));
    }

    [Fact]
    public void RollsArePureFunctionsOfPieceRaritySeedAndTheirModifierCountFollowsRarity()
    {
        var cap = Named("bronze_cap");
        foreach (var rarity in System.Enum.GetValues<Rarity>())
        {
            var a = GearRoller.Roll(cap, rarity, 1234);
            var b = GearRoller.Roll(cap, rarity, 1234);
            Assert.Equal(a.Modifiers, b.Modifiers);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(GearConfig.ModifiersPerRarity[(int)rarity], a.Modifiers.Modifiers.Count);
            Assert.Equal(a.Modifiers.Modifiers.Count, a.Modifiers.Modifiers.Select(m => m.Stat).Distinct().Count());
        }
        var seeds = Enumerable.Range(0, 40).Select(s => GearRoller.Roll(cap, Rarity.Epic, (ulong)s).Modifiers).ToList();
        Assert.True(seeds.Distinct().Count() > 1, "different seeds should not all roll the same");
        Assert.Equal(cap.Name, GearRoller.Roll(cap, Rarity.Common, 7).Name);
    }

    [Fact]
    public void RolledValuesStayWithinTheClampTheSaveRestoreUses()
    {
        var ring = Named("focus_ring");
        for (ulong seed = 0; seed < 60; seed++)
            foreach (var modifier in GearRoller.Roll(ring, Rarity.Legendary, seed).Modifiers.Modifiers)
                Assert.InRange(modifier.Value, -GearRoller.MaxModifierValue(modifier.Stat), GearRoller.MaxModifierValue(modifier.Stat));
    }

    [Fact]
    public void RarityByWeightFavorsTheCommonAndReachesEveryTier()
    {
        var counts = new int[5];
        for (var i = 0; i < 1000; i++)
            counts[(int)Rarities.PickByWeight(i / 1000f)]++;
        Assert.All(counts, c => Assert.True(c > 0));
        for (var tier = 1; tier < counts.Length; tier++)
            Assert.True(counts[tier] < counts[tier - 1], "a rarer tier is never more likely");
    }

    [Fact]
    public void RolledModifiersAddToTheWearersStatsAndSurviveASave()
    {
        var inventory = Inventory.Empty();
        var cap = Named("bronze_cap");
        var rolled = GearRoller.Roll(cap, Rarity.Epic, 99);
        Assert.True(inventory.AddRolledGear(cap.Id, rolled));
        var instance = inventory.GearEntries()[0].InstanceId;
        inventory.Equipment.TryEquip((int)GearSlot.Head, instance);

        var plain = GearStatsCalculator.Compute([new ItemStack(1, cap)]);
        Assert.NotEqual(plain, inventory.Equipment.Stats);

        var restored = Inventory.Empty();
        restored.Import(inventory.Export());
        var stack = restored.GetToolStack(instance)!;
        Assert.Equal(rolled.Modifiers, stack.Get<ModifiersComponent>());
        Assert.Equal(rolled.Name, stack.ResolvedName);
        Assert.Equal(inventory.Equipment.Stats, restored.Equipment.Stats);
    }

    [Fact]
    public void ASavedRollWithAnAbsurdValueOrUnknownStatIsClampedOrDropped()
    {
        var inventory = Inventory.Empty();
        var cap = Named("bronze_cap");
        inventory.AddRolledGear(cap.Id, GearRoller.Roll(cap, Rarity.Rare, 5));
        var data = inventory.Export();
        data.Tools![0].Rolled!.Modifiers = [
            new() { Stat = "maxHealth", Value = 1_000_000f },
            new() { Stat = "teleportation", Value = 5f },
        ];

        var restored = Inventory.Empty();
        restored.Import(data);
        var mods = restored.GetToolStack(data.Tools[0].InstanceId!.Value)!.Get<ModifiersComponent>().Modifiers;
        Assert.Single(mods);
        Assert.True(mods[0].Value <= GearRoller.MaxModifierValue(GearStat.MaxHealth));
    }

    [Fact]
    public void SetBonusesSwitchOnAtTheirPieceCountAndNotBefore()
    {
        Assert.True(GearSetRegistry.TryGet("bronze", out var set));
        var members = set.Members.Select(id => (GearItem)ItemRegistry.TryGet(id)!).ToList();
        var inventory = Inventory.Empty();
        var previous = GearStats.None;
        var worn = 0;
        foreach (var piece in members)
        {
            Wear(inventory, piece);
            worn++;
            var withBonuses = inventory.Equipment.Stats;
            var fixedOnly = GearStats.None;
            foreach (var stack in inventory.Equipment.Worn())
                foreach (var modifier in ((GearItem)stack.Item).Info.Modifiers)
                    fixedOnly = fixedOnly.With(modifier.Stat, modifier.Value);
            var expectedBonus = set.Bonuses.Any(b => worn >= b.Pieces);
            Assert.Equal(expectedBonus, withBonuses != fixedOnly.Clamped());
            Assert.True(withBonuses.Armor >= previous.Armor, "wearing more never lowers the armor");
            previous = withBonuses;
        }
    }

    [Fact]
    public void BootsAndGlovesNeverCountTowardASet()
    {
        Assert.True(GearSetRegistry.TryGet("bronze", out var set));
        var all = ItemRegistry.All.OfType<GearItem>().Where(g => g.Info.SetName == "bronze").ToList();
        Assert.All(set.Members, id => Assert.True(GearSlots.CountsForSets(((GearItem)ItemRegistry.TryGet(id)!).Info.Slot)));
        var free = all.Where(g => !GearSlots.CountsForSets(g.Info.Slot)).ToList();
        var inventory = Inventory.Empty();
        foreach (var piece in free) Wear(inventory, piece);
        Assert.Equal(0, GearStatsCalculator.WornCount(set, inventory.Equipment.Worn()));
    }

    [Fact]
    public void ASetBonusThatAsksForMorePiecesThanTheSetHasIsRefusedNamingTheFile()
    {
        var definition = new GearSetDefinition
        {
            Bonuses = [new GearSetDefinition.BonusDefinition { Pieces = 9, Modifiers = new() { ["armor"] = 1 } }],
        };
        var error = Assert.Throws<System.InvalidOperationException>(() =>
            GearSetLoader.Compile("tiny", definition, "data/sets/tiny.json", [1, 2]));
        Assert.Contains("data/sets/tiny.json", error.Message);

        var unknownStat = new GearSetDefinition
        {
            Bonuses = [new GearSetDefinition.BonusDefinition { Pieces = 1, Modifiers = new() { ["luck"] = 1 } }],
        };
        Assert.Throws<System.InvalidOperationException>(() =>
            GearSetLoader.Compile("tiny", unknownStat, "data/sets/tiny.json", [1, 2]));
    }

    [Fact]
    public void GearWithBadEquipDataIsRefusedNamingTheItem()
    {
        var both = new ItemDefinition { Equip = new() { Slot = "head" }, Use = new() { Action = "heal" } };
        var error = Assert.Throws<System.InvalidOperationException>(() => ItemLoader.ParseEquip(both, false, "items/x.json"));
        Assert.Contains("items/x.json", error.Message);

        Assert.Throws<System.InvalidOperationException>(() =>
            ItemLoader.ParseEquip(new ItemDefinition { Equip = new() { Slot = "tail" } }, false, "items/x.json"));
        Assert.Throws<System.InvalidOperationException>(() =>
            ItemLoader.ParseEquip(new ItemDefinition { Equip = new() { Slot = "head", Modifiers = new() { ["luck"] = 1f } } },
                false, "items/x.json"));
        Assert.Throws<System.InvalidOperationException>(() =>
            ItemLoader.ParseEquip(new ItemDefinition
            {
                Equip = new() { Slot = "head", Passives = [new ItemDefinition.PassiveDefinition { Trigger = "on_hit", Effect = "x", Chance = 2f }] },
            }, false, "items/x.json"));
        Assert.Null(ItemLoader.ParseEquip(new ItemDefinition(), false, "items/x.json"));
    }

    private static GearInfo Accessory(params string[]? abilities) =>
        ItemLoader.ParseEquip(new ItemDefinition { Equip = new() { Slot = "accessory", Abilities = abilities } },
            false, "items/x.json")!;

    [Fact]
    public void AbilitiesParseIntoFlagsWhateverTheirCaseOrRepeats()
    {
        var info = Accessory("air_jump", "MAGNET", " air_jump ");
        Assert.True(info.Abilities.HasFlag(GearAbility.AirJump));
        Assert.True(info.Abilities.HasFlag(GearAbility.Magnet));
        Assert.False(info.Abilities.HasFlag(GearAbility.NoFallDamage));
        Assert.False(info.Abilities.HasFlag(GearAbility.ExtraAir));

        Assert.Equal(GearAbility.None, Accessory(null).Abilities);

        // The name table and the parser agree on every ability
        foreach (var ability in GearAbilities.All)
            Assert.Equal(ability, Accessory(GearAbilities.Name(ability)).Abilities);
    }

    [Fact]
    public void AnUnknownOrBlankAbilityIsRefusedNamingTheItem()
    {
        var unknown = Assert.Throws<System.InvalidOperationException>(() => Accessory("bogus"));
        Assert.Contains("items/x.json", unknown.Message);
        var blank = Assert.Throws<System.InvalidOperationException>(() => Accessory(" "));
        Assert.Contains("items/x.json", blank.Message);
    }

    [Fact]
    public void UndeadDamageNeverFoldsIntoAttackDamageAndClampsAtZero()
    {
        var boosted = GearStats.None.With(GearStat.UndeadDamage, 0.1f);
        Assert.Equal(0f, boosted.AttackDamage);
        Assert.True(boosted.UndeadDamage > 0f);

        Assert.Equal(0f, GearStats.None.With(GearStat.UndeadDamage, -1f).Clamped().UndeadDamage);
        Assert.True(GearStats.None.With(GearStat.UndeadDamage, 1000f).Clamped().UndeadDamage <= GearConfig.FactorBonusCap);

        Assert.True(GearStats.TryParse("undeadDamage", out var stat));
        Assert.Equal(GearStat.UndeadDamage, stat);
    }

    [Fact]
    public void LootRollsNeverGiveUndeadDamage()
    {
        foreach (var gear in ItemRegistry.All.OfType<GearItem>())
            for (ulong seed = 0; seed < 200; seed++)
                Assert.DoesNotContain(GearRoller.Roll(gear, Rarity.Legendary, seed).Modifiers.Modifiers,
                    m => m.Stat == GearStat.UndeadDamage);
    }

    [Fact]
    public void ATooltipShowsRarityRolledModifiersAndSetProgress()
    {
        var inventory = Inventory.Empty();
        var cap = Named("bronze_cap");
        var rolled = GearRoller.Roll(cap, Rarity.Rare, 3);
        inventory.AddRolledGear(cap.Id, rolled);
        var instance = inventory.GearEntries()[0].InstanceId;
        var stack = inventory.GetToolStack(instance)!;

        var lines = ItemTooltip.Lines(cap.Id, stack, inventory.Equipment, out var colors);

        Assert.Equal(rolled.Name, lines[0]);
        Assert.Contains("Rare", lines[1]);
        Assert.Equal(Rarities.Color(Rarity.Rare), colors[0]);
        foreach (var modifier in rolled.Modifiers.Modifiers)
            Assert.Contains(GearText.Line(modifier), lines);
        Assert.Contains(lines, l => l.Contains("set (0/"));
    }

    [Fact]
    public void AnItemShowsItsRolledRarityOverItsOwnAndABlockShowsCommon()
    {
        var inventory = Inventory.Empty();
        var cap = Named("bronze_cap");
        var rolledRarity = cap.Rarity == Rarity.Legendary ? Rarity.Common : cap.Rarity + 1;
        Assert.True(inventory.AddRolledGear(cap.Id, GearRoller.Roll(cap, rolledRarity, 11)));
        var rolledStack = inventory.GetToolStack(inventory.GearEntries()[0].InstanceId)!;

        Assert.Equal(rolledRarity, Rarities.Of(cap.Id, rolledStack));
        Assert.Equal(cap.Rarity, Rarities.Of(cap.Id, null));
        Assert.Equal(cap.Rarity, Rarities.Of(cap.Id, new ItemStack(1, cap.Id)));
        Assert.Equal(Rarity.Common, Rarities.Of(ContentRegistry.RequireBlock("stone").Id, null));
    }
}
