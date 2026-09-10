using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using StbImageSharp;

namespace silkgltriangle;

/// <summary>
/// Parsed Blockbench project for the player model: one skin texture plus named rigid parts
/// with pivot-relative cuboids. Cube faces land in <see cref="Chunk.CubeFaces"/> order, so the
/// mesh builder reuses the exact winding the chunk mesher uses.
/// </summary>
public sealed class BbmodelData
{
    /// <summary>Top-first RGBA skin, or null when the file declares no texture.</summary>
    public byte[]? TexturePixels;
    public int TextureWidth;
    public int TextureHeight;
    /// <summary>Project texture size face UVs are authored against.</summary>
    public int ResolutionWidth = 64;
    public int ResolutionHeight = 64;
    public readonly Dictionary<string, BbmodelPart> Parts = new(StringComparer.Ordinal);
}

public sealed class BbmodelPart
{
    /// <summary>Group origin in blocks, model space (author the model standing on y = 0).</summary>
    public Vector3 Pivot;
    public readonly List<BbmodelCube> Cubes = new();
}

public sealed class BbmodelCube
{
    /// <summary>Bounds in blocks, relative to the owning part's pivot.</summary>
    public Vector3 Min, Max;
    /// <summary>One entry per <see cref="Chunk.CubeFaces"/> face; null = face omitted in the file.</summary>
    public UvRect?[] Faces = new UvRect?[6];
}

/// <summary>
/// Minimal Blockbench (.bbmodel) reader for the player rig. It understands axis-aligned cubes
/// with per-face UVs — the posing itself stays procedural in <see cref="PlayerModel"/>, so group
/// and cube rotations must be zero and every failure names the file that caused it.
/// </summary>
public static class BbmodelLoader
{
    /// <summary>Group names the rig needs; anything else in the file is ignored.</summary>
    public static readonly string[] RequiredParts =
        ["Torso", "Head", "UpperArmL", "UpperArmR", "ForearmL", "ForearmR",
         "ThighL", "ThighR", "ShinL", "ShinR"];

    // Blockbench face keys in Chunk.CubeFaces order: -Z, +Z, -X, +X, +Y, -Y.
    private static readonly string[] FaceKeys = ["north", "south", "west", "east", "up", "down"];
    private const float UnitsPerBlock = 16f;

    public static BbmodelData Load(string path) => Parse(File.ReadAllText(path), path, File.ReadAllBytes);

    /// <summary>
    /// Parses bbmodel JSON. <paramref name="readBytes"/> resolves a relative texture source
    /// against the model's location (a stub in tests).
    /// </summary>
    public static BbmodelData Parse(string json, string path, Func<string, byte[]> readBytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"Player model '{path}' is not valid JSON: {e.Message}", e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var data = new BbmodelData();

            if (root.TryGetProperty("resolution", out var resolution)
                && resolution.ValueKind == JsonValueKind.Object)
            {
                if (resolution.TryGetProperty("width", out var w)) data.ResolutionWidth = (int)Num(w, path, "resolution");
                if (resolution.TryGetProperty("height", out var h)) data.ResolutionHeight = (int)Num(h, path, "resolution");
            }
            if (data.ResolutionWidth <= 0 || data.ResolutionHeight <= 0)
                throw new InvalidOperationException($"Player model '{path}' needs a positive 'resolution'.");

            var cubesByUuid = IndexCubes(Req(root, "elements", path, "the model"), path,
                data.ResolutionWidth, data.ResolutionHeight);
            ReadOutliner(Req(root, "outliner", path, "the model"), path, data, cubesByUuid, null);

            var missing = new List<string>();
            foreach (var name in RequiredParts)
                if (!data.Parts.TryGetValue(name, out var part) || part.Cubes.Count == 0)
                    missing.Add(name);
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Player model '{path}' needs one non-empty group per part; missing: {string.Join(", ", missing)}.");

            ReadTexture(root, path, readBytes, data);
            return data;
        }
    }

    private static Dictionary<string, (Vector3 Min, Vector3 Max, UvRect?[] Faces)> IndexCubes(
        JsonElement elements, string path, int texWidth, int texHeight)
    {
        if (elements.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Player model '{path}' has no 'elements' array.");

        var cubes = new Dictionary<string, (Vector3, Vector3, UvRect?[])>(StringComparer.Ordinal);
        foreach (var el in elements.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (el.TryGetProperty("type", out var type) && type.GetString() != "cube")
                throw new InvalidOperationException(
                    $"Player model '{path}' element '{NameOf(el)}' is not a cube: only cubes are supported.");
            if (CubeAngle(el) != 0f)
                throw new InvalidOperationException(
                    $"Player model '{path}' cube '{NameOf(el)}' is rotated: un-rotate it, posing is procedural.");

            var from = ReadVec3(Req(el, "from", path, $"cube '{NameOf(el)}'"), path, $"cube '{NameOf(el)}'") / UnitsPerBlock;
            var to = ReadVec3(Req(el, "to", path, $"cube '{NameOf(el)}'"), path, $"cube '{NameOf(el)}'") / UnitsPerBlock;
            var inflate = 0f;
            if (el.TryGetProperty("inflate", out var inflateEl))
                inflate = Num(inflateEl, path, $"cube '{NameOf(el)}'") / UnitsPerBlock;
            from -= new Vector3(inflate);
            to += new Vector3(inflate);

            var faces = ReadFaces(el, path, NameOf(el), texWidth, texHeight);
            var uuid = el.TryGetProperty("uuid", out var uuidEl) ? uuidEl.GetString() ?? "" : "";
            cubes[uuid] = (from, to, faces);
        }
        return cubes;
    }

    private static void ReadOutliner(JsonElement outliner, string path, BbmodelData data,
        Dictionary<string, (Vector3 Min, Vector3 Max, UvRect?[] Faces)> cubesByUuid, string? group)
    {
        if (outliner.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Player model '{path}' has no 'outliner' array.");

        foreach (var entry in outliner.EnumerateArray())
        {
            // A bare string references a top-level cube; only groups carry pivots, so those
            // need a named group around them.
            if (entry.ValueKind == JsonValueKind.String)
            {
                if (group == null)
                    throw new InvalidOperationException(
                        $"Player model '{path}' has a cube outside any group: put every cube in a part group.");
                if (cubesByUuid.TryGetValue(entry.GetString() ?? "", out var cube))
                    AddCube(data, group, path, cube);
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object) continue;
            var name = NameOf(entry);
            var pivot = ReadVec3(Req(entry, "origin", path, $"group '{name}'"), path, $"group '{name}'") / UnitsPerBlock;
            if (GroupAngle(entry) != 0f)
                throw new InvalidOperationException(
                    $"Player model '{path}' group '{name}' is rotated: keep groups unrotated, posing is procedural.");

            if (!data.Parts.ContainsKey(name))
                data.Parts[name] = new BbmodelPart { Pivot = pivot };

            if (entry.TryGetProperty("children", out var children))
                ReadOutliner(children, path, data, cubesByUuid, name);
        }
    }

    private static void AddCube(BbmodelData data, string group, string path,
        (Vector3 Min, Vector3 Max, UvRect?[] Faces) cube)
    {
        if (!data.Parts.TryGetValue(group, out var part))
            return;
        // Geometry is stored pivot-relative, so a part's mesh hangs off its joint exactly
        // like the built-in cuboids do.
        data.Parts[group].Cubes.Add(new BbmodelCube
        {
            Min = cube.Min - part.Pivot,
            Max = cube.Max - part.Pivot,
            Faces = cube.Faces,
        });
    }

    private static UvRect?[] ReadFaces(JsonElement cube, string path, string cubeName, int texWidth, int texHeight)
    {
        var faces = new UvRect?[6];
        if (!cube.TryGetProperty("faces", out var facesEl) || facesEl.ValueKind != JsonValueKind.Object)
            return faces;

        for (var f = 0; f < 6; f++)
        {
            if (!facesEl.TryGetProperty(FaceKeys[f], out var face) || face.ValueKind != JsonValueKind.Object)
                continue;
            if (face.TryGetProperty("enabled", out var enabled) && !enabled.GetBoolean())
                continue;
            if (face.TryGetProperty("rotation", out var rotation) && Num(rotation, path, $"cube '{cubeName}'") != 0f)
                throw new InvalidOperationException(
                    $"Player model '{path}' cube '{cubeName}' has a rotated face UV: set it back to 0.");
            if (!face.TryGetProperty("uv", out var uv) || uv.ValueKind != JsonValueKind.Array || uv.GetArrayLength() != 4)
                throw new InvalidOperationException(
                    $"Player model '{path}' cube '{cubeName}' needs per-face 'uv' with 4 numbers: switch the cube to per-face UV.");

            var x1 = Num(uv[0], path, $"cube '{cubeName}'");
            var y1 = Num(uv[1], path, $"cube '{cubeName}'");
            var x2 = Num(uv[2], path, $"cube '{cubeName}'");
            var y2 = Num(uv[3], path, $"cube '{cubeName}'");
            // Blockbench UVs grow down from the texture's top-left, exactly like UvRect,
            // normalized here so the rig never sees texture pixels.
            faces[f] = new UvRect(new Vector2(x1 / texWidth, y1 / texHeight),
                new Vector2(x2 / texWidth, y2 / texHeight));
        }
        return faces;
    }

    private static void ReadTexture(JsonElement root, string path, Func<string, byte[]> readBytes, BbmodelData data)
    {
        if (!root.TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Array)
            return;

        string? source = null;
        foreach (var tex in textures.EnumerateArray())
        {
            if (tex.ValueKind != JsonValueKind.Object) continue;
            if (tex.TryGetProperty("source", out var sourceEl) && sourceEl.GetString() is { Length: > 0 } s)
            {
                source = s;
                break;
            }
        }
        if (source == null) return;

        byte[] bytes;
        if (source.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = source.IndexOf(',');
            try
            {
                bytes = Convert.FromBase64String(comma < 0 ? source : source[(comma + 1)..]);
            }
            catch (FormatException e)
            {
                throw new InvalidOperationException($"Player model '{path}' has a corrupt embedded texture.", e);
            }
        }
        else
        {
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            var resolved = Path.GetFullPath(Path.Combine(baseDir, source));
            try
            {
                bytes = readBytes(resolved);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"Player model '{path}' references texture '{source}' which could not be read: {e.Message}", e);
            }
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            data.TexturePixels = image.Data;
            data.TextureWidth = image.Width;
            data.TextureHeight = image.Height;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Player model '{path}' texture could not be decoded as PNG.", e);
        }
    }

    // A cube's rest rotation: the object form Blockbench writes, defaulting to unrotated.
    private static float CubeAngle(JsonElement cube)
    {
        if (!cube.TryGetProperty("rotation", out var rotation)) return 0f;
        if (rotation.ValueKind == JsonValueKind.Object && rotation.TryGetProperty("angle", out var angle))
            return (float)Math.Abs(angle.GetDouble());
        if (rotation.ValueKind == JsonValueKind.Array)
        {
            var sum = 0.0;
            foreach (var v in rotation.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number) sum += Math.Abs(v.GetDouble());
            return (float)sum;
        }
        return 0f;
    }

    private static float GroupAngle(JsonElement group)
    {
        if (!group.TryGetProperty("rotation", out var rotation) || rotation.ValueKind != JsonValueKind.Array)
            return 0f;
        var sum = 0.0;
        foreach (var v in rotation.EnumerateArray())
            if (v.ValueKind == JsonValueKind.Number) sum += Math.Abs(v.GetDouble());
        return (float)sum;
    }

    private static string NameOf(JsonElement el) =>
        el.TryGetProperty("name", out var name) ? name.GetString() ?? "?" : "?";

    private static JsonElement Req(JsonElement obj, string name, string path, string context)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value))
            return value;
        throw new InvalidOperationException($"Player model '{path}' {context} is missing '{name}'.");
    }

    private static Vector3 ReadVec3(JsonElement array, string path, string context)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 3)
            throw new InvalidOperationException($"Player model '{path}' {context} needs 3 numbers.");
        return new Vector3(Num(array[0], path, context), Num(array[1], path, context), Num(array[2], path, context));
    }

    private static float Num(JsonElement el, string path, string context)
    {
        if (el.ValueKind == JsonValueKind.Number) return (float)el.GetDouble();
        throw new InvalidOperationException($"Player model '{path}' {context} needs a number.");
    }
}
