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
    public string? Block { get; init; }
    public int Count { get; init; } = 1;
    public bool On { get; init; } = true;

    /// <summary>The entity name a <c>spawn</c> step creates (resolved at run time).</summary>
    public string? Entity { get; init; }

    /// <summary>The size a <c>spawn</c> step gives a slime (1 when the step names none; other
    /// kinds ignore it).</summary>
    public int Size { get; init; } = 1;

    /// <summary>True when a <c>spawn</c> step gave only <c>[x, z]</c>, and always for a <c>grow</c>
    /// step: the feet (or the tree's root search) go on top of the terrain column there, and
    /// <see cref="Pos"/>'s Y is ignored.</summary>
    public bool SnapToGround { get; init; }

    /// <summary>The tree species a <c>grow</c> step grows (resolved at run time).</summary>
    public string? Species { get; init; }

    /// <summary>The growth pulses a <c>grow</c> step runs.</summary>
    public int GrowSteps { get; init; }

    /// <summary>Health a <c>damage</c> step takes off every live mob, or off the player.</summary>
    public int Amount { get; init; }

    /// <summary>Who a <c>damage</c> step hurts: <c>mobs</c> (the default, every live mob) or
    /// <c>player</c> (the local player's health pool).</summary>
    public string Target { get; init; } = "mobs";

    /// <summary>The camera mode a <c>view</c> step selects (<c>first</c>, <c>shoulder</c> or <c>orbit</c>).</summary>
    public string? Mode { get; init; }

    /// <summary>What a <c>player</c> step makes the player's model do (<c>hurt</c>, <c>punch</c>,
    /// <c>slice</c>, <c>equip</c>, <c>charge</c>, <c>release</c>, <c>die</c> or <c>revive</c>).</summary>
    public string? Action { get; init; }

    /// <summary>The hotbar slot a <c>select</c> step picks, 1-based like the number keys.</summary>
    public int Slot { get; init; }
}

/// <summary>
/// A scripted in-game run: teleport/look/wait/walk/mine/place/fly/give/spawn/grow/screenshot steps loaded from
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
                Pos = ReadPos(element, index, "teleport"),
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
            "prime" => new VisualStep { Op = "prime", Pos = ReadPos(element, index, "prime") },
            "fly" => new VisualStep
            {
                Op = "fly",
                On = !element.TryGetProperty("on", out var on) || on.GetBoolean(),
            },
            "give" => new VisualStep
            {
                Op = "give",
                Block = ReadGiveBlock(element, index),
                Count = ReadGiveCount(element, index),
            },
            "spawn" => ReadSpawn(element, index),
            "grow" => ReadGrow(element, index),
            "damage" => new VisualStep
            {
                Op = "damage",
                Amount = element.TryGetProperty("amount", out var amount) && amount.TryGetInt32(out var hp) && hp > 0
                    ? hp
                    : throw new InvalidOperationException($"Step {index} (damage) needs 'amount' above 0."),
                Target = element.TryGetProperty("target", out _)
                    ? ReadChoice(element, index, "damage", "target", ["mobs", "player"])
                    : "mobs",
            },
            "view" => new VisualStep { Op = "view", Mode = ReadChoice(element, index, "view", "mode", ["first", "shoulder", "orbit"]) },
            "player" => new VisualStep
            {
                Op = "player",
                Action = ReadChoice(element, index, "player", "action", ["hurt", "punch", "slice", "equip", "charge", "release", "die", "revive"]),
            },
            "select" => new VisualStep
            {
                Op = "select",
                Slot = element.TryGetProperty("slot", out var slot) && slot.TryGetInt32(out var s)
                       && s >= 1 && s <= Inventory.SlotCount
                    ? s
                    : throw new InvalidOperationException(
                        $"Step {index} (select) needs 'slot' from 1 to {Inventory.SlotCount}."),
            },
            "screenshot" => new VisualStep
            {
                Op = "screenshot",
                ShotName = element.TryGetProperty("name", out var name) ? name.GetString() ?? "shot" : "shot",
                HideHud = element.TryGetProperty("hideHud", out var hide) && hide.GetBoolean(),
            },
            _ => throw new InvalidOperationException(
                $"Step {index} has unknown op '{op}'; expected teleport, look, wait, walk, mine, place, prime, fly, give, spawn, grow, damage, view, player, select or screenshot."),
        };
    }

    private static string ReadChoice(JsonElement element, int index, string op, string field, string[] choices)
    {
        var value = element.TryGetProperty(field, out var property) ? property.GetString()?.Trim().ToLowerInvariant() : null;
        if (value == null || Array.IndexOf(choices, value) < 0)
            throw new InvalidOperationException(
                $"Step {index} ({op}) needs '{field}' to be one of: {string.Join(", ", choices)}.");
        return value;
    }

    private static Vector3 ReadPos(JsonElement element, int index, string op)
    {
        var values = ReadPosValues(element, index, op, "[x, y, z]");
        if (values.Count != 3)
            throw new InvalidOperationException(
                $"Step {index} ({op}) needs 'pos' with exactly 3 numbers, got {values.Count}.");

        return new Vector3(values[0], values[1], values[2]);
    }

    private static List<float> ReadPosValues(JsonElement element, int index, string op, string shape)
    {
        if (!element.TryGetProperty("pos", out var pos) || pos.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Step {index} ({op}) needs 'pos': {shape}.");

        var values = new List<float>();
        foreach (var value in pos.EnumerateArray())
            values.Add((float)value.GetDouble());
        return values;
    }

    /// <summary>A <c>spawn</c> step: <c>entity</c> names what to create (validated against the
    /// registry only when the step runs, so parsing stays content-free), <c>pos</c> is either
    /// <c>[x, z]</c> (feet snapped onto the terrain) or <c>[x, y, z]</c> (an exact feet position),
    /// and the optional <c>size</c> (an integer from 1, default 1) is a slime's size.</summary>
    private static VisualStep ReadSpawn(JsonElement element, int index)
    {
        if (!element.TryGetProperty("entity", out var entity)
            || entity.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(entity.GetString()))
            throw new InvalidOperationException($"Step {index} (spawn) needs 'entity'.");

        var size = 1;
        if (element.TryGetProperty("size", out var sizeElement)
            && (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt32(out size) || size < 1))
            throw new InvalidOperationException($"Step {index} (spawn) needs 'size' to be an integer from 1.");

        var values = ReadPosValues(element, index, "spawn", "[x, z] or [x, y, z]");
        return values.Count switch
        {
            2 => new VisualStep
            {
                Op = "spawn", Entity = entity.GetString()!.Trim(), Size = size,
                Pos = new Vector3(values[0], 0f, values[1]), SnapToGround = true,
            },
            3 => new VisualStep
            {
                Op = "spawn", Entity = entity.GetString()!.Trim(), Size = size,
                Pos = new Vector3(values[0], values[1], values[2]),
            },
            _ => throw new InvalidOperationException(
                $"Step {index} (spawn) needs 'pos' with 2 or 3 numbers, got {values.Count}."),
        };
    }

    /// <summary>A <c>grow</c> step: <c>species</c> names the tree (validated against the registry
    /// only when the step runs, so parsing stays content-free), <c>pos</c> is the <c>[x, z]</c>
    /// column (snapped onto the terrain there) and <c>steps</c> the growth pulses to run.</summary>
    private static VisualStep ReadGrow(JsonElement element, int index)
    {
        if (!element.TryGetProperty("species", out var species)
            || species.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(species.GetString()))
            throw new InvalidOperationException($"Step {index} (grow) needs 'species'.");

        var values = ReadPosValues(element, index, "grow", "[x, z]");
        if (values.Count != 2)
            throw new InvalidOperationException(
                $"Step {index} (grow) needs 'pos' with exactly 2 numbers ([x, z]), got {values.Count}.");

        if (!element.TryGetProperty("steps", out var stepsElement)
            || stepsElement.ValueKind != JsonValueKind.Number
            || !stepsElement.TryGetInt32(out var steps)
            || steps <= 0)
            throw new InvalidOperationException($"Step {index} (grow) needs 'steps' above 0.");

        return new VisualStep
        {
            Op = "grow", Species = species.GetString()!.Trim(),
            Pos = new Vector3(values[0], 0f, values[1]), SnapToGround = true,
            GrowSteps = steps,
        };
    }

    private static float? ReadOptionalFloat(JsonElement element, string field) =>
        element.TryGetProperty(field, out var value) ? (float)value.GetDouble() : null;

    private static string ReadGiveBlock(JsonElement element, int index)
    {
        if (!element.TryGetProperty("block", out var block) || string.IsNullOrWhiteSpace(block.GetString()))
            throw new InvalidOperationException($"Step {index} (give) needs 'block'.");
        return block.GetString()!;
    }

    private static int ReadGiveCount(JsonElement element, int index)
    {
        var count = element.TryGetProperty("count", out var value) ? value.GetInt32() : 1;
        if (count <= 0)
            throw new InvalidOperationException($"Step {index} (give) needs 'count' above 0.");
        return count;
    }

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
