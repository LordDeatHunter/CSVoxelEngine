using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Silk.NET.Input;

namespace silkgltriangle;

/// <summary>
/// One parsed visual-scenario step. Parsing and validation are GL-free so the xUnit suite
/// covers them; only <see cref="VisualDirector"/> touches the window.
/// </summary>
public sealed class VisualStep
{
    public required string Op { get; init; }
    public Vector3? Pos { get; init; }
    public float? Yaw { get; init; }
    public float? Pitch { get; init; }
    public float Seconds { get; init; }
    public Key[] Keys { get; init; } = [];
    public string ShotName { get; init; } = "shot";
    public bool HideHud { get; init; }
}

/// <summary>
/// A scripted in-game run: teleport/look/wait/walk/mine/place/screenshot steps loaded from
/// JSON (<c>VOXEL_SCRIPT</c>), driving the real world-update functions for agent screenshots.
/// </summary>
public sealed class VisualScenario
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Name { get; }
    public string OutputDir { get; }
    public IReadOnlyList<VisualStep> Steps { get; }

    private VisualScenario(string name, string outputDir, List<VisualStep> steps)
    {
        Name = name;
        OutputDir = outputDir;
        Steps = steps;
    }

    public static VisualScenario Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Visual scenario '{path}' cannot be read: {e.Message}", e);
        }

        var name = Path.GetFileNameWithoutExtension(path);
        try
        {
            return Parse(json, name);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"Visual scenario '{path}' is not valid JSON: {e.Message}", e);
        }
    }

    /// <summary>Parses scenario JSON directly; <paramref name="name"/> stands in for the
    /// file name (output folder default and error context).</summary>
    public static VisualScenario Parse(string json, string name)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var root = document.RootElement;

        var outputDir = root.TryGetProperty("output", out var output)
            ? output.GetString() ?? ""
            : "";
        if (string.IsNullOrWhiteSpace(outputDir))
            outputDir = Path.Combine("visual", name);

        var steps = new List<VisualStep>();
        if (root.TryGetProperty("steps", out var stepsElement))
        {
            var index = 0;
            foreach (var element in stepsElement.EnumerateArray())
                steps.Add(ParseStep(element, ++index));
        }

        return new VisualScenario(name, outputDir, steps);
    }

    private static VisualStep ParseStep(JsonElement element, int index)
    {
        if (!element.TryGetProperty("op", out var opElement) || opElement.GetString() is not { } op)
            throw new InvalidOperationException($"Step {index} has no 'op'.");

        return op.ToLowerInvariant() switch
        {
            "teleport" => new VisualStep
            {
                Op = "teleport",
                Pos = ReadPos(element, index),
                Yaw = ReadOptionalFloat(element, "yaw"),
                Pitch = ReadOptionalFloat(element, "pitch"),
            },
            "look" => new VisualStep
            {
                Op = "look",
                Yaw = ReadOptionalFloat(element, "yaw"),
                Pitch = ReadOptionalFloat(element, "pitch"),
            },
            "wait" => new VisualStep { Op = "wait", Seconds = ReadSeconds(element, index) },
            "walk" => new VisualStep
            {
                Op = "walk",
                Seconds = ReadSeconds(element, index),
                Keys = ReadKeys(element, index),
            },
            "mine" => new VisualStep { Op = "mine", Seconds = ReadSeconds(element, index) },
            "place" => new VisualStep { Op = "place" },
            "screenshot" => new VisualStep
            {
                Op = "screenshot",
                ShotName = element.TryGetProperty("name", out var name) ? name.GetString() ?? "shot" : "shot",
                HideHud = element.TryGetProperty("hideHud", out var hide) && hide.GetBoolean(),
            },
            _ => throw new InvalidOperationException(
                $"Step {index} has unknown op '{op}'; expected teleport, look, wait, walk, mine, place or screenshot."),
        };
    }

    private static Vector3 ReadPos(JsonElement element, int index)
    {
        if (!element.TryGetProperty("pos", out var pos))
            throw new InvalidOperationException($"Step {index} (teleport) needs 'pos': [x, y, z].");

        var values = new List<float>();
        foreach (var value in pos.EnumerateArray())
            values.Add((float)value.GetDouble());
        if (values.Count != 3)
            throw new InvalidOperationException(
                $"Step {index} (teleport) needs 'pos' with exactly 3 numbers, got {values.Count}.");

        return new Vector3(values[0], values[1], values[2]);
    }

    private static float? ReadOptionalFloat(JsonElement element, string field) =>
        element.TryGetProperty(field, out var value) ? (float)value.GetDouble() : null;

    private static float ReadSeconds(JsonElement element, int index)
    {
        var seconds = element.TryGetProperty("seconds", out var value) ? (float)value.GetDouble() : 0f;
        if (seconds <= 0f)
            throw new InvalidOperationException($"Step {index} needs 'seconds' above 0.");
        return seconds;
    }

    private static Key[] ReadKeys(JsonElement element, int index)
    {
        if (!element.TryGetProperty("keys", out var keys))
            return [];

        var parsed = new List<Key>();
        foreach (var key in keys.EnumerateArray())
        {
            var name = key.GetString() ?? "";
            if (!Enum.TryParse<Key>(name, ignoreCase: true, out var parsedKey))
                throw new InvalidOperationException($"Step {index} names unknown key '{name}'.");
            parsed.Add(parsedKey);
        }
        return parsed.ToArray();
    }
}
