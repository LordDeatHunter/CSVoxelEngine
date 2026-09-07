using System;
using System.IO;
using Silk.NET.Input;
using Xunit;

namespace silkgltriangle.Tests;

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
              ],
            }
            """, "tour");

        Assert.Equal("tour", scenario.Name);
        Assert.Equal(Path.Combine("visual", "tour"), scenario.OutputDir);
        Assert.Equal(8, scenario.Steps.Count);

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

    [Theory]
    [InlineData("""{ "steps": [{ "op": "dance" }] }""", "dance")]
    [InlineData("""{ "steps": [{ "op": "teleport", "pos": [1, 2] }] }""", "exactly 3")]
    [InlineData("""{ "steps": [{ "op": "teleport" }] }""", "'pos'")]
    [InlineData("""{ "steps": [{ "op": "wait", "seconds": 0 }] }""", "'seconds'")]
    [InlineData("""{ "steps": [{ "op": "walk", "seconds": 1, "keys": ["Ww"] }] }""", "Ww")]
    [InlineData("""{ "steps": [{ }] }""", "'op'")]
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
