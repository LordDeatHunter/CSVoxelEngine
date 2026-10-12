using System;
using System.IO;
using Silk.NET.Input;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.VisualTest;

public sealed class VisualScenarioTests
{
    [Fact]
    public void ParsesEveryOpWithDefaults()
    {
        var scenario = VisualScenario.Parse("""
            {
              // comments and trailing commas are fine, like content JSON
              "steps": [
                { "op": "teleport", "pos": [8.5, 30.0, 8.5], "yaw": -90, "pitch": 0, },
                { "op": "look", "yaw": -90, "pitch": -10 },
                { "op": "wait", "seconds": 5 },
                { "op": "walk", "keys": ["W", "Space"], "seconds": 2.5 },
                { "op": "mine", "seconds": 1 },
                { "op": "place" },
                { "op": "screenshot", "name": "arrival", "hideHud": true },
                { "op": "screenshot" },
                { "op": "give", "block": "glass", "count": 16 },
                { "op": "give", "block": "stone" },
                { "op": "spawn", "entity": "goblin", "pos": [48.5, 40.5] },
                { "op": "grow", "species": "oak", "pos": [-79, -2], "steps": 40 },
              ],
            }
            """, "tour");

        Assert.Equal("tour", scenario.Name);
        Assert.Equal(Path.Combine("visual", "tour"), scenario.OutputDir);
        Assert.Equal(12, scenario.Steps.Count);

        var teleport = scenario.Steps[0];
        Assert.Equal(new System.Numerics.Vector3(8.5f, 30f, 8.5f), teleport.Pos);
        Assert.Equal(-90f, teleport.Yaw);
        Assert.Equal(new Key[] { Key.W, Key.Space }, scenario.Steps[3].Keys);

        var named = scenario.Steps[6];
        Assert.Equal("arrival", named.ShotName);
        Assert.True(named.HideHud);

        var plain = scenario.Steps[7];
        Assert.Equal("shot", plain.ShotName);
        Assert.False(plain.HideHud);

        var give = scenario.Steps[8];
        Assert.Equal("glass", give.Block);
        Assert.Equal(16, give.Count);

        var giveDefault = scenario.Steps[9];
        Assert.Equal("stone", giveDefault.Block);
        Assert.Equal(1, giveDefault.Count);

        // Two numbers are [x, z]: the director snaps the feet onto the terrain there.
        var spawn = scenario.Steps[10];
        Assert.Equal("spawn", spawn.Op);
        Assert.Equal("goblin", spawn.Entity);
        Assert.True(spawn.SnapToGround);
        Assert.Equal(48.5f, spawn.Pos!.Value.X);
        Assert.Equal(40.5f, spawn.Pos!.Value.Z);

        // A grow step always names a column: the director searches the tree on its ground.
        var grow = scenario.Steps[11];
        Assert.Equal("grow", grow.Op);
        Assert.Equal("oak", grow.Species);
        Assert.Equal(40, grow.GrowSteps);
        Assert.True(grow.SnapToGround);
        Assert.Equal(-79f, grow.Pos!.Value.X);
        Assert.Equal(-2f, grow.Pos!.Value.Z);
    }

    [Theory]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "grow", "pos": [1, 2], "steps": 4 }] }""", "'species'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "grow", "species": "  ", "pos": [1, 2], "steps": 4 }] }""", "'species'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "grow", "species": "oak", "steps": 4 }] }""", "'pos'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "grow", "species": "oak", "pos": [1, 2, 3], "steps": 4 }] }""", "'pos'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "grow", "species": "oak", "pos": [1], "steps": 4 }] }""", "'pos'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "grow", "species": "oak", "pos": [1, 2] }] }""", "'steps'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "grow", "species": "oak", "pos": [1, 2], "steps": 0 }] }""", "'steps'")]
    public void GrowNeedsASpeciesTwoCoordinatesAndPositiveSteps(string json, string fragment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VisualScenario.Parse(json, "bad"));
        Assert.Contains(fragment, ex.Message);
        Assert.Contains("Step 2", ex.Message);
        Assert.Contains("(grow)", ex.Message);
    }

    [Theory]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "spawn", "pos": [1, 2] }] }""", "'entity'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "spawn", "entity": "  ", "pos": [1, 2] }] }""", "'entity'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "spawn", "entity": "goblin" }] }""", "'pos'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 1 }, { "op": "spawn", "entity": "goblin", "pos": [1] }] }""", "'pos'")]
    public void SpawnNeedsAnEntityAndAPosition(string json, string fragment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VisualScenario.Parse(json, "bad"));
        Assert.Contains(fragment, ex.Message);
        Assert.Contains("Step 2", ex.Message);
        Assert.Contains("(spawn)", ex.Message);
    }

    [Fact]
    public void SpawnWithThreeCoordinatesIsExact()
    {
        var scenario = VisualScenario.Parse(
            """{ "steps": [{ "op": "spawn", "entity": "slime", "pos": [3.5, 64, -7.25] }] }""", "tour");
        var spawn = scenario.Steps[0];
        Assert.Equal("slime", spawn.Entity);
        Assert.False(spawn.SnapToGround);
        Assert.Equal(new System.Numerics.Vector3(3.5f, 64f, -7.25f), spawn.Pos);
    }

    [Fact]
    public void SpawnCarriesAnOptionalSizeThatDefaultsToTheSmallest()
    {
        var scenario = VisualScenario.Parse(
            """{ "steps": [{ "op": "spawn", "entity": "slime", "pos": [1, 2], "size": 3 }, { "op": "spawn", "entity": "slime", "pos": [1, 2] }] }""",
            "sizes");
        Assert.Equal(3, scenario.Steps[0].Size);
        Assert.Equal(1, scenario.Steps[1].Size);
    }

    [Theory]
    [InlineData("""{ "steps": [{ "op": "spawn", "entity": "slime", "pos": [1, 2], "size": 0 }] }""")]
    [InlineData("""{ "steps": [{ "op": "spawn", "entity": "slime", "pos": [1, 2], "size": 1.5 }] }""")]
    [InlineData("""{ "steps": [{ "op": "spawn", "entity": "slime", "pos": [1, 2], "size": "big" }] }""")]
    public void SpawnRefusesASizeThatIsNotAPositiveInteger(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VisualScenario.Parse(json, "bad"));
        Assert.Contains("'size'", ex.Message);
        Assert.Contains("(spawn)", ex.Message);
    }

    [Fact]
    public void UnknownOpNamesEveryOp()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VisualScenario.Parse("""{ "steps": [{ "op": "dance" }] }""", "bad"));
        foreach (var op in new[] { "teleport", "look", "wait", "walk", "mine", "place", "fly", "give", "spawn", "grow", "select", "screenshot" })
            Assert.Contains(op, ex.Message);
    }

    [Fact]
    public void DamageHurtsTheMobsUnlessItTargetsThePlayer()
    {
        var scenario = VisualScenario.Parse("""
            { "steps": [
              { "op": "damage", "amount": 3 },
              { "op": "damage", "amount": 70, "target": "player" },
            ] }
            """, "tour");
        Assert.Equal("mobs", scenario.Steps[0].Target);
        Assert.Equal("player", scenario.Steps[1].Target);
        Assert.Equal(70, scenario.Steps[1].Amount);

        var ex = Assert.Throws<InvalidOperationException>(() => VisualScenario.Parse(
            """{ "steps": [{ "op": "damage", "amount": 3, "target": "camera" }] }""", "bad"));
        Assert.Contains("'target'", ex.Message);
    }

    [Fact]
    public void SelectTakesAOneBasedHotbarSlot()
    {
        var scenario = VisualScenario.Parse(
            $$"""{ "steps": [{ "op": "select", "slot": 1 }, { "op": "select", "slot": {{Inventory.SlotCount}} }] }""", "tour");
        Assert.Equal("select", scenario.Steps[0].Op);
        Assert.Equal(1, scenario.Steps[0].Slot);
        Assert.Equal(Inventory.SlotCount, scenario.Steps[1].Slot);
    }

    [Theory]
    [InlineData("""{ "steps": [{ "op": "select" }] }""")]
    [InlineData("""{ "steps": [{ "op": "select", "slot": 0 }] }""")]
    [InlineData("""{ "steps": [{ "op": "select", "slot": 999 }] }""")]
    public void SelectRefusesASlotOffTheHotbar(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VisualScenario.Parse(json, "bad"));
        Assert.Contains("(select)", ex.Message);
        Assert.Contains("'slot'", ex.Message);
    }

    [Fact]
    public void OutputFieldOverridesTheFolder()
    {
        var scenario = VisualScenario.Parse(
            """{ "output": "custom/dir", "steps": [] }""", "tour");
        Assert.Equal("custom/dir", scenario.OutputDir);
    }

    [Fact]
    public void ScriptedKeyboardPressAndRelease()
    {
        var keyboard = new ScriptedKeyboard();
        keyboard.Press([Key.W, Key.ShiftLeft]);

        Assert.True(keyboard.IsKeyPressed(Key.W));
        Assert.True(keyboard.IsKeyPressed(Key.ShiftLeft));
        Assert.False(keyboard.IsKeyPressed(Key.S));

        keyboard.ReleaseAll();
        Assert.False(keyboard.IsKeyPressed(Key.W));
    }

    [Fact]
    public void FlyStepDefaultsOnAndCanBeTurnedOff()
    {
        var on = VisualScenario.Parse("""{ "steps": [{ "op": "fly" }] }""", "tour");
        Assert.Equal("fly", on.Steps[0].Op);
        Assert.True(on.Steps[0].On);

        var off = VisualScenario.Parse("""{ "steps": [{ "op": "fly", "on": false }] }""", "tour");
        Assert.Equal("fly", off.Steps[0].Op);
        Assert.False(off.Steps[0].On);
    }

    [Fact]
    public void PrimeNamesTheCellToPrimeAndTheUnknownOpErrorListsIt()
    {
        var scenario = VisualScenario.Parse("""{ "steps": [{ "op": "prime", "pos": [4, 11, -6] }] }""", "tour");
        Assert.Equal("prime", scenario.Steps[0].Op);
        Assert.Equal(new System.Numerics.Vector3(4f, 11f, -6f), scenario.Steps[0].Pos);

        var missing = Assert.Throws<InvalidOperationException>(
            () => VisualScenario.Parse("""{ "steps": [{ "op": "prime" }] }""", "bad"));
        Assert.Contains("'pos'", missing.Message);
        var unknown = Assert.Throws<InvalidOperationException>(
            () => VisualScenario.Parse("""{ "steps": [{ "op": "dance" }] }""", "bad"));
        Assert.Contains("prime", unknown.Message);
    }

    [Theory]
    [InlineData("""{ "steps": [{ "op": "dance" }] }""", "dance")]
    [InlineData("""{ "steps": [{ "op": "teleport", "pos": [1, 2] }] }""", "exactly 3")]
    [InlineData("""{ "steps": [{ "op": "teleport" }] }""", "'pos'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 0 }] }""", "'seconds'")]
    [InlineData("""{ "steps": [{ "op": "walk", "seconds": 1, "keys": ["Ww"] }] }""", "Ww")]
    [InlineData("""{ "steps": [{ }] }""", "'op'")]
    [InlineData("""{ "steps": [{ "op": "give" }] }""", "block")]
    public void BadStepsThrowNamingTheProblem(string json, string fragment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VisualScenario.Parse(json, "bad"));
        Assert.Contains(fragment, ex.Message);
    }

    [Fact]
    public void MissingFileThrowsNamingThePath()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VisualScenario.Load("does/not/exist.json"));
        Assert.Contains("does/not/exist.json", ex.Message);
    }
}
