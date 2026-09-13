using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

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
        var into = new List<byte>();

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
}
