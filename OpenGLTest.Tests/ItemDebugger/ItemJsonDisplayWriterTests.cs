using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class ItemJsonDisplayWriterTests : IDisposable
{
    private static readonly JsonDocumentOptions Tolerant = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("item-debugger-tests").FullName;

    [Fact]
    public void ReplacesExistingDisplayPreservingCommentsAndOtherFields()
    {
        var json = """
            {
              // hand-written note
              "name": "Bucket",
              "texture": "assets/textures/items/bucket.png",
              "display": { "rotation": [1, 2, 3], "translation": [0, 0, 0], "scale": 1 },
              "tool": { "power": 2.0 } // another note
            }
            """;
        var path = Write(json);

        ItemJsonDisplayWriter.Save(path, new Vector3(10, -20, 30), new Vector3(0.1f, 0.2f, -0.3f), 0.5f);

        var text = File.ReadAllText(path);
        Assert.Contains("// hand-written note", text);
        Assert.Contains("// another note", text);
        Assert.Contains("\"texture\": \"assets/textures/items/bucket.png\"", text);
        using var doc = JsonDocument.Parse(text, Tolerant);
        var display = doc.RootElement.GetProperty("display");
        Assert.Equal([10f, -20f, 30f], ReadVector(display.GetProperty("rotation")));
        Assert.Equal([0.1f, 0.2f, -0.3f], ReadVector(display.GetProperty("translation")));
        Assert.Equal(0.5f, display.GetProperty("scale").GetSingle(), 4);
        Assert.Equal(2.0f, doc.RootElement.GetProperty("tool").GetProperty("power").GetSingle(), 4);
        Assert.False(doc.RootElement.TryGetProperty("displayBackup", out _));
    }

    [Fact]
    public void InsertsDisplayWhenAbsentKeepingEverythingElse()
    {
        var json = """
            {
              "name": "Flint",
              // keep this comment
              "texture": "assets/textures/items/flint.png"
            }
            """;
        var path = Write(json);

        ItemJsonDisplayWriter.Save(path, new Vector3(0, 90, 0), Vector3.Zero, 1.25f);

        var text = File.ReadAllText(path);
        Assert.Contains("// keep this comment", text);
        using var doc = JsonDocument.Parse(text, Tolerant);
        Assert.Equal([0f, 90f, 0f], ReadVector(doc.RootElement.GetProperty("display").GetProperty("rotation")));
        Assert.Equal(1.25f, doc.RootElement.GetProperty("display").GetProperty("scale").GetSingle(), 4);
    }

    [Fact]
    public void SavingTwiceIsIdempotentInContent()
    {
        var path = Write("""{ "name": "X", "texture": "x.png" }""");

        ItemJsonDisplayWriter.Save(path, new Vector3(1.5f, 2.5f, 3.5f), new Vector3(0.01f, 0.02f, 0.03f), 0.8f);
        var first = File.ReadAllText(path);
        ItemJsonDisplayWriter.Save(path, new Vector3(1.5f, 2.5f, 3.5f), new Vector3(0.01f, 0.02f, 0.03f), 0.8f);
        var second = File.ReadAllText(path);

        using var doc = JsonDocument.Parse(second, Tolerant);
        Assert.Equal(1, CountProperties(doc.RootElement, "display"));
        Assert.Equal([1.5f, 2.5f, 3.5f], ReadVector(doc.RootElement.GetProperty("display").GetProperty("rotation")));
        Assert.Equal(first, second);
    }

    private static int CountProperties(JsonElement root, string name)
    {
        var count = 0;
        foreach (var property in root.EnumerateObject())
            if (property.NameEquals(name))
                count++;
        return count;
    }

    private static float[] ReadVector(JsonElement element)
    {
        var values = new float[element.GetArrayLength()];
        for (var i = 0; i < values.Length; i++)
            values[i] = element[i].GetSingle();
        return values;
    }

    private string Write(string json)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
