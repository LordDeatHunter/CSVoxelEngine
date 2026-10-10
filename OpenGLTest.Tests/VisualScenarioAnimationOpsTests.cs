using System;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class VisualScenarioAnimationOpsTests
{
    [Fact]
    public void DamageViewAndPlayerStepsParseTheirArguments()
    {
        var scenario = VisualScenario.Parse("""
            { "steps": [
                { "op": "damage", "amount": 3 },
                { "op": "view", "mode": "Shoulder" },
                { "op": "player", "action": "slice" }
            ] }
            """, "t");

        Assert.Equal(3, scenario.Steps[0].Amount);
        Assert.Equal("shoulder", scenario.Steps[1].Mode);
        Assert.Equal("slice", scenario.Steps[2].Action);
    }

    [Theory]
    [InlineData("""{ "steps": [ { "op": "damage" } ] }""")]
    [InlineData("""{ "steps": [ { "op": "damage", "amount": 0 } ] }""")]
    [InlineData("""{ "steps": [ { "op": "view", "mode": "sideways" } ] }""")]
    [InlineData("""{ "steps": [ { "op": "player", "action": "dance" } ] }""")]
    public void BadArgumentsAreRefusedNamingTheStep(string json)
    {
        var error = Assert.Throws<InvalidOperationException>(() => VisualScenario.Parse(json, "t"));
        Assert.Contains("Step 1", error.Message);
    }

    [Fact]
    public void TheShippedAnimationsScenarioParses()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "visual-examples", "animations.json");
        if (!System.IO.File.Exists(path)) return;

        var scenario = VisualScenario.Load(path);

        Assert.Contains(scenario.Steps, step => step.Op == "damage");
        Assert.Contains(scenario.Steps, step => step.Op == "player");
    }
}
