using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.VisualTest;
using Xunit;

namespace VoxelEngine.Tests.VisualTest;

public sealed class VisualScenarioUiOpsTests
{
    [Fact]
    public void LanternClickIsAUiActionWithNoClientAuthoredFuel()
    {
        var step = Assert.Single(VisualScenario.Parse("""{ "steps": [{ "op": "lantern" }] }""", "t").Steps);
        Assert.Equal("lantern", step.Op);
        Assert.Null(step.Block);
        Assert.Null(step.Pos);
    }

    [Fact]
    public void PanelAndSpellBuilderStepsOpenUnlessToldOtherwise()
    {
        var scenario = VisualScenario.Parse("""
            { "steps": [
                { "op": "panel" },
                { "op": "panel", "open": false },
                { "op": "spellbuilder", "open": true },
                { "op": "spellbuilder", "open": false }
            ] }
            """, "t");

        Assert.Equal([true, false, true, false], scenario.Steps.Select(s => s.On));
    }

    [Fact]
    public void TabSearchRowAndHoverStepsParseTheirArguments()
    {
        var scenario = VisualScenario.Parse("""
            { "steps": [
                { "op": "tab", "tab": "Crafting" },
                { "op": "search", "text": "stone" },
                { "op": "row", "row": 3 },
                { "op": "hover", "pos": [200, 240.5] },
                { "op": "hover", "clear": true }
            ] }
            """, "t");

        Assert.Equal("crafting", scenario.Steps[0].Tab);
        Assert.Equal("stone", scenario.Steps[1].Text);
        Assert.Equal(3, scenario.Steps[2].Row);
        Assert.Equal(new Vector2(200f, 240.5f), scenario.Steps[3].Point);
        Assert.Null(scenario.Steps[4].Point);
    }

    [Theory]
    [InlineData("""{ "steps": [ { "op": "tab", "tab": "armor" } ] }""", "'tab'")]
    [InlineData("""{ "steps": [ { "op": "tab" } ] }""", "'tab'")]
    [InlineData("""{ "steps": [ { "op": "search" } ] }""", "'text'")]
    [InlineData("""{ "steps": [ { "op": "search", "text": 5 } ] }""", "'text'")]
    [InlineData("""{ "steps": [ { "op": "row" } ] }""", "'row'")]
    [InlineData("""{ "steps": [ { "op": "row", "row": 0 } ] }""", "'row'")]
    [InlineData("""{ "steps": [ { "op": "hover" } ] }""", "'pos'")]
    [InlineData("""{ "steps": [ { "op": "hover", "pos": [1, 2, 3] } ] }""", "2 numbers")]
    public void BadUiArgumentsAreRefusedNamingTheStepAndField(string json, string fragment)
    {
        var error = Assert.Throws<InvalidOperationException>(() => VisualScenario.Parse(json, "t"));
        Assert.Contains("Step 1", error.Message);
        Assert.Contains(fragment, error.Message);
    }

    [Fact]
    public void TheUnknownOpErrorListsTheUiOps()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => VisualScenario.Parse("""{ "steps": [ { "op": "dance" } ] }""", "t"));
        foreach (var op in new[] { "panel", "tab", "search", "row", "hover", "spellbuilder" })
            Assert.Contains(op, error.Message);
    }

    [Fact]
    public void AUiStepWithNoLiveUiStopsTheRunNamingTheStep()
    {
        var scenario = VisualScenario.Parse("""{ "steps": [ { "op": "panel" } ] }""", "t");
        var director = new VisualDirector(scenario, null!);
        var error = Assert.Throws<InvalidOperationException>(() => director.Drive(0.016));
        Assert.Contains("Step 1 (panel)", error.Message);
    }
}
