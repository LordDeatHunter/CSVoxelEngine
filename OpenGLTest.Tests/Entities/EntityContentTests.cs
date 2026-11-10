using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Entities;

public sealed class EntityContentTests
{
    public EntityContentTests() => TestContent.EnsureLoaded();

    [Fact]
    public void SlimeEntityDeclaresNameHealthLootAndModel()
    {
        var type = EntityRegistry.Require("slime");

        Assert.Equal("Slime", type.DisplayName);
        Assert.Equal(8, type.MaxHealth);
        Assert.NotNull(type.LootTable);
        Assert.EndsWith("slime.bbmodel", type.ModelPath);
    }

    [Fact]
    public void SlimeLootOnlyEverRollsSlimeballs()
    {
        var type = EntityRegistry.Require("slime");
        var slimeball = ContentRegistry.Require("item/slimeball");
        var into = new List<ushort>();

        for (var i = 0; i < 32; i++)
            type.LootTable!.Roll(new LootContext(0, 0, 0, 0), into);

        Assert.NotEmpty(into);
        Assert.All(into, id => Assert.Equal(slimeball, id));
    }

    [Fact]
    public void SlimeModelParsesToOneGroundBodyCubeWithASkin()
    {
        var type = EntityRegistry.Require("slime");
        var data = BbmodelLoader.Load(type.ModelPath, Array.Empty<string>());

        Assert.Equal(32, data.ResolutionWidth);
        var body = Assert.Single(data.Parts["Body"].Cubes);
        // from [-6,0,-6] to [6,12,6] units is a 0.75-block cube standing on y = 0.
        Assert.Equal(new Vector3(-0.375f, 0f, -0.375f), body.Min);
        Assert.Equal(new Vector3(0.375f, 0.75f, 0.375f), body.Max);
        Assert.NotNull(data.TexturePixels);
        Assert.Equal(32, data.TextureWidth);
        Assert.Equal(32, data.TextureHeight);
    }

    [Fact]
    public void EntityModelLoadsItsSkin()
    {
        var type = EntityRegistry.Require("slime");
        var model = EntityModel.Load(type.ModelPath);

        Assert.Equal(32, model.TextureWidth);
        Assert.Equal(32, model.TextureHeight);
        Assert.NotEmpty(model.TexturePixels);
    }

    [Fact]
    public void EveryEntityHasItsModelAsTheFirstVariant()
    {
        Assert.NotEmpty(EntityRegistry.ByName);
        foreach (var type in EntityRegistry.ByName.Values)
        {
            Assert.NotEmpty(type.Variants);
            Assert.Same(type.Model, type.Variants[0]);
            Assert.Equal(type.ModelPath, type.VariantPaths[0]);
            Assert.Equal(type.Variants.Count, type.VariantPaths.Count);
        }
    }

    [Fact]
    public void EveryHeldItemIsAPureItemInAGroupOfEveryVariant()
    {
        var holders = 0;
        foreach (var type in EntityRegistry.ByName.Values)
        {
            if (type.HeldItemId == 0)
            {
                Assert.Null(type.HeldPart);
                continue;
            }
            holders++;
            Assert.True(ItemRegistry.IsItem(type.HeldItemId), $"{type.Name} holds a non-item id {type.HeldItemId}");
            Assert.NotNull(type.HeldPart);
            Assert.All(type.Variants, model => Assert.True(model.Parts.ContainsKey(type.HeldPart!)));
        }
        Assert.True(holders > 0, "no entity holds an item, so the held path is untested");
    }

    [Fact]
    public void AMobRefusesATypeWithoutAHealthPool()
    {
        var slime = EntityRegistry.Require("slime");
        var poolless = new EntityType
        {
            Name = slime.Name,
            DisplayName = slime.DisplayName,
            MaxHealth = 0,
            LootTable = slime.LootTable,
            ModelPath = slime.ModelPath,
            Model = slime.Model,
        };

        Assert.Throws<InvalidOperationException>(() => new Slime(poolless, Vector3.Zero));
    }

    [Fact]
    public void FromDataBakesTheSameCubesAsLoad()
    {
        var type = EntityRegistry.Require("slime");
        var loaded = EntityModel.Load(type.ModelPath);

        Assert.Equal(EntityModel.CubeBoxes(type.Model), loaded.BakedBoxes());
        Assert.Equal(loaded.BakedBoxes(), EntityModel.FromData(type.Model, type.ModelPath).BakedBoxes());
    }
}
