using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Entities.Goblins;

/// <summary>
/// The goblin's shipped content: it satisfies <see cref="Goblin.ValidateContent"/> (parts, clips
/// and swing events), keeps its head and arms under the torso, has a skin, and a loot table that
/// only rolls items (the carried flint is dropped separately).
/// </summary>
public sealed class GoblinContentTests
{
    public GoblinContentTests() => TestContent.EnsureLoaded();

    [Fact]
    public void GoblinContentSatisfiesTheGoblinsContract()
    {
        var type = EntityRegistry.Require("goblin");
        Goblin.ValidateContent(type);

        // Head and arms hang off the torso, so a torso lean carries them.
        foreach (var child in new[] { "Head", "ArmL", "ArmR" })
            Assert.Equal("Torso", type.Model.Parts[child].Parent);
        Assert.NotNull(type.Model.TexturePixels);
    }

    [Fact]
    public void ValidateContentNamesTheMissingClip()
    {
        const string path = "assets/models/entity/clipless_goblin.bbmodel";
        var model = new BbmodelData();
        foreach (var name in Goblin.RequiredParts)
        {
            var part = new BbmodelPart();
            part.Cubes.Add(new BbmodelCube { Min = Vector3.Zero, Max = Vector3.One });
            model.Parts.Add(name, part);
        }

        var type = new EntityType
        {
            Name = "clipless_goblin",
            DisplayName = "Clipless Goblin",
            MaxHealth = 1,
            ModelPath = path,
            Model = model,
        };

        var error = Assert.Throws<InvalidOperationException>(() => Goblin.ValidateContent(type));
        Assert.Contains(path, error.Message);
        Assert.Contains(EntityAnimator.IdleClip, error.Message);
    }

    [Fact]
    public void GoblinLootOnlyRollsItems()
    {
        var type = EntityRegistry.Require("goblin");
        Assert.NotNull(type.LootTable);

        var into = new List<byte>();
        for (var i = 0; i < 64; i++)
            type.LootTable!.Roll(new LootContext(0, 0, 0, 0), into);

        Assert.All(into, id => Assert.True(ItemRegistry.IsItem(id), $"loot rolled non-item id {id}"));
    }
}
