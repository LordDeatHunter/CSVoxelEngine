using System;
using System.Text;
using VoxelEngine.Saves;
using Xunit;

namespace VoxelEngine.Tests.Saves;

/// <summary>Non-finite numeric fields in hand-edited entity JSON degrade as optional fields.</summary>
public sealed class EntitySaveNumericAdmissionTests
{
    [Theory]
    [InlineData("mob-position", 0, "1e1000")]
    [InlineData("mob-position", 1, "-1e1000")]
    [InlineData("mob-position", 2, "1e1000")]
    [InlineData("mob-velocity", 0, "-1e1000")]
    [InlineData("mob-velocity", 1, "1e1000")]
    [InlineData("mob-velocity", 2, "-1e1000")]
    [InlineData("drop-position", 0, "1e1000")]
    [InlineData("drop-position", 1, "-1e1000")]
    [InlineData("drop-position", 2, "1e1000")]
    [InlineData("drop-velocity", 0, "-1e1000")]
    [InlineData("drop-velocity", 1, "1e1000")]
    [InlineData("drop-velocity", 2, "-1e1000")]
    [InlineData("drop-stuck", 0, "1e1000")]
    [InlineData("drop-stuck", 1, "-1e1000")]
    [InlineData("drop-stuck", 2, "1e1000")]
    [InlineData("mob-facing", -1, "1e1000")]
    [InlineData("mob-facing", -1, "-1e1000")]
    [InlineData("drop-age", -1, "1e1000")]
    [InlineData("drop-age", -1, "-1e1000")]
    public void NonFiniteFieldFallsBackWithoutRejectingJson(string field, int component, string number)
    {
        var mobPosition = VectorJson("mob-position", field, component, number);
        var mobVelocity = VectorJson("mob-velocity", field, component, number);
        var dropPosition = VectorJson("drop-position", field, component, number);
        var dropVelocity = VectorJson("drop-velocity", field, component, number);
        var dropStuck = VectorJson("drop-stuck", field, component, number);
        var facing = ScalarJson("mob-facing", field, number, "1.25");
        var age = ScalarJson("drop-age", field, number, "0.5");
        var raw = $$"""
        {
          "Mobs": [
            { "Type": "slime", "Position": {{mobPosition}}, "Velocity": {{mobVelocity}}, "Facing": {{facing}}, "Health": 4 },
            { "Type": "goblin", "Position": [8, 70, 8], "Velocity": [0, 1, 0], "Facing": 0.75, "Health": 6 }
          ],
          "Drops": [
            { "Item": "block/rock", "Position": {{dropPosition}}, "Velocity": {{dropVelocity}}, "Age": {{age}}, "Stuck": {{dropStuck}} },
            { "Item": "item/other", "Position": [9, 70, 9], "Velocity": [1, 0, -1], "Age": 2.5 }
          ]
        }
        """;

        var saved = EntitySaveCodec.TryParse(Encoding.UTF8.GetBytes(raw), out var rejectedJson);

        Assert.False(rejectedJson);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Mobs!.Count);
        Assert.Equal(2, saved.Drops!.Count);
        Assert.Equal("slime", saved.Mobs[0].Type);
        Assert.Equal("goblin", saved.Mobs[1].Type);
        Assert.Equal("block/rock", saved.Drops[0].Item);
        Assert.Equal("item/other", saved.Drops[1].Item);
        Assert.Equal(new[] { 8f, 70f, 8f }, saved.Mobs[1].Position);
        Assert.Equal(0.75f, saved.Mobs[1].Facing);
        Assert.Equal(new[] { 9f, 70f, 9f }, saved.Drops[1].Position);
        Assert.Equal(2.5f, saved.Drops[1].Age);

        var mob = saved.Mobs[0];
        Assert.Equal(field == "mob-position" ? null : new[] { 2f, 70f, 2f }, mob.Position);
        Assert.Equal(field == "mob-velocity" ? null : new[] { 1f, 2f, 3f }, mob.Velocity);
        Assert.Equal(field == "mob-facing" ? null : 1.25f, mob.Facing);

        var drop = saved.Drops[0];
        Assert.Equal(field == "drop-position" ? null : new[] { 4f, 71f, 4f }, drop.Position);
        Assert.Equal(field == "drop-velocity" ? null : new[] { 0.25f, 1f, -0.5f }, drop.Velocity);
        Assert.Equal(field == "drop-age" ? null : 0.5f, drop.Age);
        Assert.Equal(field == "drop-stuck" ? null : new[] { 0f, 1f, 0f }, drop.Stuck);
    }

    [Fact]
    public void FiniteUnderflowAndWrongShapeArraysKeepTheirExistingValues()
    {
        const string raw = """
            { "Mobs": [{ "Type": "slime", "Position": [1e30, -1e-1000], "Velocity": [2, 3], "Facing": -1e-1000 }],
              "Drops": [{ "Item": "block/rock", "Position": [1, 2], "Stuck": [0, 1], "Age": -1e-1000 }] }
            """;

        var saved = EntitySaveCodec.TryParse(Encoding.UTF8.GetBytes(raw), out var rejectedJson);

        Assert.False(rejectedJson);
        Assert.NotNull(saved);
        Assert.Equal(new[] { 1e30f, 0f }, saved.Mobs![0].Position);
        Assert.Equal(new[] { 2f, 3f }, saved.Mobs[0].Velocity);
        Assert.Equal(0f, saved.Mobs[0].Facing);
        Assert.Equal(new[] { 1f, 2f }, saved.Drops![0].Position);
        Assert.Equal(new[] { 0f, 1f }, saved.Drops[0].Stuck);
        Assert.Equal(0f, saved.Drops[0].Age);
    }

    [Theory]
    [InlineData("{ \"Mobs\": [{ \"Facing\": \"Infinity\" }] }")]
    [InlineData("{ \"Drops\": [{ \"Age\": NaN }] }")]
    public void NamedNonFiniteNumbersRemainJsonRejections(string raw)
    {
        Assert.Null(EntitySaveCodec.TryParse(Encoding.UTF8.GetBytes(raw), out var rejectedJson));
        Assert.True(rejectedJson);
    }

    private static string VectorJson(string vector, string field, int component, string number)
    {
        var values = vector switch
        {
            "mob-position" => new[] { "2", "70", "2" },
            "mob-velocity" => new[] { "1", "2", "3" },
            "drop-position" => new[] { "4", "71", "4" },
            "drop-velocity" => new[] { "0.25", "1", "-0.5" },
            "drop-stuck" => new[] { "0", "1", "0" },
            _ => throw new ArgumentOutOfRangeException(nameof(vector))
        };
        if (field == vector)
            values[component] = number;
        return $"[{string.Join(", ", values)}]";
    }

    private static string ScalarJson(string scalar, string field, string number, string fallback) =>
        field == scalar ? number : fallback;
}
