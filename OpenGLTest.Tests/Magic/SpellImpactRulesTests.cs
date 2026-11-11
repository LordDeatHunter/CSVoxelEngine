using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Magic;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>The pure rules of a cast spell's impact: damage and range from the recipe, its parts, its block actions.</summary>
public class SpellImpactRulesTests
{
    public SpellImpactRulesTests() => TestContent.EnsureLoaded();

    private static SpellStats Stats(float damage = 1f, float range = 1f) =>
        new(SpellVerdict.Valid, 0f, 0f, 0f, damage, range);

    private static SpellPart Part(SpellPartKind kind, string name) =>
        SpellPartLoader.Compile(kind, name, new SpellPartDefinition { CostShare = 0.1f, Complexity = 1f, Tags = ["basic"] },
            "test/" + name + ".json");

    [Fact]
    public void TheDamageFactorScalesDamageMonotonically()
    {
        Assert.True(SpellImpactRules.DamageFor(Stats(damage: 1.5f)) > SpellImpactRules.DamageFor(Stats()));
        var previous = 0;
        for (float factor = 0.1f; factor <= 5f; factor += 0.1f)
        {
            var damage = SpellImpactRules.DamageFor(Stats(damage: factor));
            Assert.True(damage >= previous);
            previous = damage;
        }
    }

    [Fact]
    public void DamageIsNeverBelowOne() => Assert.True(SpellImpactRules.DamageFor(Stats(damage: 0.0001f)) >= 1);

    [Fact]
    public void TheRangeFactorLengthensTheFlight()
    {
        var single = SpellImpactRules.RangeFor(Stats());
        Assert.True(SpellImpactRules.RangeFor(Stats(range: 1.5f)) > single);
        Assert.Equal(2f * single, SpellImpactRules.RangeFor(Stats(range: 2f)), 4);
    }

    [Fact]
    public void ElementAndShapeAreFoundInAnyOrder()
    {
        var element = Part(SpellPartKind.Element, "fire");
        var shape = Part(SpellPartKind.Shape, "bolt");
        var modifier = Part(SpellPartKind.Modifier, "power");

        foreach (var parts in new[] { new[] { element, shape, modifier }, new[] { modifier, shape, element } })
        {
            Assert.Same(element, SpellImpactRules.ElementOf(parts));
            Assert.Same(shape, SpellImpactRules.ShapeOf(parts));
        }
        Assert.Null(SpellImpactRules.ElementOf([shape, modifier]));
        Assert.Null(SpellImpactRules.ShapeOf([element]));
    }

    [Fact]
    public void OnlyTheBlockImpactsBecomeBlockActions()
    {
        Assert.Equal(StatusEffectBlockAction.Ignite | StatusEffectBlockAction.Freeze | StatusEffectBlockAction.Thaw,
            SpellImpactRules.BlockActionsOf(SpellImpact.Ignite | SpellImpact.Freeze | SpellImpact.Thaw));
        Assert.Equal(StatusEffectBlockAction.None,
            SpellImpactRules.BlockActionsOf(SpellImpact.Prime | SpellImpact.Light | SpellImpact.Grow));
        Assert.Equal(StatusEffectBlockAction.Freeze,
            SpellImpactRules.BlockActionsOf(SpellImpact.Freeze | SpellImpact.Grow));
    }
}
