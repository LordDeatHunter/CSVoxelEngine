using System;
using System.Text.Json;
using VoxelEngine.Content;
using VoxelEngine.Entities;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>An entity file's <c>blood</c>: omitted sprays red, "none" nothing, a color object its own color, and a
/// malformed value is refused naming the file.</summary>
public sealed class EntityBloodTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void OmittedBloodIsTheDefaultRed() =>
        Assert.Equal(EntityType.DefaultBloodColor, EntityLoader.ParseBlood(null, "x.json"));

    [Fact]
    public void NoneSpraysNothing() => Assert.Null(EntityLoader.ParseBlood(Json("\"none\""), "x.json"));

    [Fact]
    public void ColorObjectIsReadAsSrgb()
    {
        var color = EntityLoader.ParseBlood(Json("{\"color\":[255,0,51]}"), "x.json");
        Assert.Equal(1f, color!.Value.X);
        Assert.Equal(0.2f, color.Value.Z, 3);
    }

    [Fact]
    public void OmittedOrUnsizedBloodKeepsDropletsAtTheirSize()
    {
        Assert.Equal(1f, EntityLoader.ParseBloodSize(null, "x.json"));
        Assert.Equal(1f, EntityLoader.ParseBloodSize(Json("{\"color\":[255,0,51]}"), "x.json"));
    }

    [Fact]
    public void ABloodSizeScalesItsDroplets()
    {
        Assert.Equal(2.5f, EntityLoader.ParseBloodSize(Json("{\"color\":[1,2,3],\"size\":2.5}"), "x.json"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"big\"")]
    public void MalformedBloodSizeIsRefused(string size) =>
        Assert.Throws<InvalidOperationException>(() =>
            EntityLoader.ParseBloodSize(Json("{\"color\":[1,2,3],\"size\":" + size + "}"), "x.json"));

    [Theory]
    [InlineData("\"red\"")]
    [InlineData("{\"color\":[1,2]}")]
    [InlineData("{\"color\":[1,2,300]}")]
    public void MalformedBloodIsRefused(string text) =>
        Assert.Throws<InvalidOperationException>(() => EntityLoader.ParseBlood(Json(text), "x.json"));
}
