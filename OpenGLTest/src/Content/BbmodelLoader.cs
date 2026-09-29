using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using StbImageSharp;

namespace silkgltriangle;

/// <summary>
/// Parsed Blockbench project: one skin texture plus named rigid parts
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
    /// <summary>Keyframe clips from the project's <c>animations</c> array, in file order.</summary>
    public readonly List<AnimationClip> Animations = new();
}

public sealed class BbmodelPart
{
    /// <summary>Group origin in blocks, model space (author the model standing on y = 0).</summary>
    public Vector3 Pivot;
    /// <summary>Name of the enclosing group in the outliner, or null for a top-level group.</summary>
    public string? Parent;
    /// <summary>The group's Blockbench uuid ("" when the file has none); animators key it.</summary>
    public string Uuid = "";
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
/// Minimal Blockbench (.bbmodel) reader for rigid models: axis-aligned cubes with per-face UVs,
/// grouped by named parts. The player rig gives <see cref="RequiredParts"/> so a partially
/// authored rig fails fast; entity models pass no required parts and keep whatever groups the
/// file has. Groups keep their outliner nesting (<see cref="BbmodelPart.Parent"/>), and the
/// project's keyframe <c>animations</c> are parsed into <see cref="AnimationClip"/>s. Rest
/// rotations are not supported, so group and cube angles must be zero; every failure names the
/// file that caused it.
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

    /// <summary>Loads a player rig model: every <see cref="RequiredParts"/> group must be non-empty.</summary>
    public static BbmodelData Load(string path) => Load(path, RequiredParts);

    /// <summary>
    /// Loads a model, requiring <paramref name="requiredParts"/> groups (each non-empty). Pass an
    /// empty list for an entity model that keeps whatever groups it authored.
    /// </summary>
    public static BbmodelData Load(string path, IReadOnlyList<string> requiredParts) =>
        Parse(File.ReadAllText(path), path, File.ReadAllBytes, requiredParts);

    /// <summary>
    /// Parses bbmodel JSON against the player's <see cref="RequiredParts"/>.
    /// </summary>
    public static BbmodelData Parse(string json, string path, Func<string, byte[]> readBytes) =>
        Parse(json, path, readBytes, RequiredParts);

    /// <summary>
    /// Parses bbmodel JSON. <paramref name="readBytes"/> resolves a relative texture source
    /// against the model's location (a stub in tests). <paramref name="requiredParts"/> is the set
    /// of groups that must each hold at least one cube; empty means no such requirement.
    /// </summary>
    public static BbmodelData Parse(string json, string path, Func<string, byte[]> readBytes,
        IReadOnlyList<string> requiredParts)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"Model '{path}' is not valid JSON: {e.Message}", e);
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
                throw new InvalidOperationException($"Model '{path}' needs a positive 'resolution'.");

            var cubesByUuid = IndexCubes(Req(root, "elements", path, "the model"), path,
                data.ResolutionWidth, data.ResolutionHeight);
            ReadOutliner(Req(root, "outliner", path, "the model"), path, data, cubesByUuid, null);

            var missing = new List<string>();
            foreach (var name in requiredParts)
                if (!data.Parts.TryGetValue(name, out var part) || part.Cubes.Count == 0)
                    missing.Add(name);
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Model '{path}' needs one non-empty group per part; missing: {string.Join(", ", missing)}.");

            ReadAnimations(root, path, data);
            ReadTexture(root, path, readBytes, data);
            return data;
        }
    }

    private static Dictionary<string, (Vector3 Min, Vector3 Max, UvRect?[] Faces)> IndexCubes(
        JsonElement elements, string path, int texWidth, int texHeight)
    {
        if (elements.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Model '{path}' has no 'elements' array.");

        var cubes = new Dictionary<string, (Vector3, Vector3, UvRect?[])>(StringComparer.Ordinal);
        foreach (var el in elements.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (el.TryGetProperty("type", out var type) && type.GetString() != "cube")
                throw new InvalidOperationException(
                    $"Model '{path}' element '{NameOf(el)}' is not a cube: only cubes are supported.");
            if (CubeAngle(el) != 0f)
                throw new InvalidOperationException(
                    $"Model '{path}' cube '{NameOf(el)}' is rotated: un-rotate it, posing is procedural.");

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
            throw new InvalidOperationException($"Model '{path}' has no 'outliner' array.");

        foreach (var entry in outliner.EnumerateArray())
        {
            // A bare string references a top-level cube; only groups carry pivots, so those
            // need a named group around them.
            if (entry.ValueKind == JsonValueKind.String)
            {
                if (group == null)
                    throw new InvalidOperationException(
                        $"Model '{path}' has a cube outside any group: put every cube in a part group.");
                if (cubesByUuid.TryGetValue(entry.GetString() ?? "", out var cube))
                    AddCube(data, group, path, cube);
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object) continue;
            var name = NameOf(entry);
            var pivot = ReadVec3(Req(entry, "origin", path, $"group '{name}'"), path, $"group '{name}'") / UnitsPerBlock;
            if (GroupAngle(entry) != 0f)
                throw new InvalidOperationException(
                    $"Model '{path}' group '{name}' is rotated: keep groups unrotated, posing is procedural.");

            // A duplicate group name keeps the first entry's parent and uuid.
            if (!data.Parts.ContainsKey(name))
                data.Parts[name] = new BbmodelPart
                {
                    Pivot = pivot,
                    Parent = group,
                    Uuid = entry.TryGetProperty("uuid", out var uuidEl) && uuidEl.ValueKind == JsonValueKind.String
                        ? uuidEl.GetString() ?? ""
                        : "",
                };

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
                    $"Model '{path}' cube '{cubeName}' has a rotated face UV: set it back to 0.");
            if (!face.TryGetProperty("uv", out var uv) || uv.ValueKind != JsonValueKind.Array || uv.GetArrayLength() != 4)
                throw new InvalidOperationException(
                    $"Model '{path}' cube '{cubeName}' needs per-face 'uv' with 4 numbers: switch the cube to per-face UV.");

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

    // Conversion from Blockbench's animation convention to the engine's, taken from Blockbench's
    // preview code (animation.js, BoneAnimator.displayRotation / displayPosition) and its group
    // meshes' Euler order 'ZYX':
    //   rotation (x, y, z) degrees -> engine Euler degrees (-x, -y, z), applied X first, then Y,
    //     then Z (AnimationClip.RotationFromDegrees);
    //   position (x, y, z) pixels  -> engine blocks (-x, y, z) / 16, in the parent bone's space;
    //   scale                      -> unchanged.
    // Handedness: +X is the model's right (ArmR), -Z its front. A positive engine X rotation
    // raises a hanging arm forward, so Blockbench authors type a negative x for that (as in
    // Bedrock's own humanoid animations). This is the one place the sign lives: if a clip plays
    // mirrored, fix it here, never in the clips.
    private static void ReadAnimations(JsonElement root, string path, BbmodelData data)
    {
        if (!root.TryGetProperty("animations", out var animations) || animations.ValueKind != JsonValueKind.Array)
            return;

        var uuidToName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (partName, part) in data.Parts)
            if (part.Uuid.Length > 0)
                uuidToName.TryAdd(part.Uuid, partName);

        foreach (var anim in animations.EnumerateArray())
        {
            if (anim.ValueKind != JsonValueKind.Object) continue;
            if (!anim.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameEl.GetString()))
                throw new InvalidOperationException($"Model '{path}' has an animation without a 'name'.");
            var name = nameEl.GetString()!;

            var loop = AnimationClip.LoopMode.Once;
            if (anim.TryGetProperty("loop", out var loopEl))
            {
                loop = (loopEl.ValueKind == JsonValueKind.String ? loopEl.GetString() : null) switch
                {
                    "loop" => AnimationClip.LoopMode.Loop,
                    "hold" => AnimationClip.LoopMode.Hold,
                    "once" => AnimationClip.LoopMode.Once,
                    var other => throw new InvalidOperationException(
                        $"Model '{path}' animation '{name}' has unknown loop mode '{other ?? loopEl.ToString()}'."),
                };
            }

            var length = 0f;
            if (anim.TryGetProperty("length", out var lengthEl))
                length = Num(lengthEl, path, $"animation '{name}' length");

            var tracks = new Dictionary<string, AnimationClip.BoneTrack>(StringComparer.Ordinal);
            var events = new List<AnimationClip.Event>();
            if (anim.TryGetProperty("animators", out var animators) && animators.ValueKind == JsonValueKind.Object)
            {
                foreach (var animator in animators.EnumerateObject())
                {
                    var value = animator.Value;
                    if (value.ValueKind != JsonValueKind.Object) continue;
                    var type = value.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                        ? typeEl.GetString()
                        : null;
                    if (animator.Name == "effects" || type == "effect")
                    {
                        ReadEffects(value, path, name, events);
                        continue;
                    }

                    // Blockbench keys a bone animator by the group's uuid; fall back to the
                    // animator's own name for hand-written files.
                    string? bone = null;
                    if (uuidToName.TryGetValue(animator.Name, out var byUuid))
                        bone = byUuid;
                    else if (value.TryGetProperty("name", out var boneNameEl)
                             && boneNameEl.ValueKind == JsonValueKind.String
                             && boneNameEl.GetString() is { } boneName
                             && data.Parts.ContainsKey(boneName))
                        bone = boneName;
                    if (bone == null)
                        throw new InvalidOperationException(
                            $"Model '{path}' animation '{name}' animates group '{animator.Name}' which the outliner does not have.");

                    tracks[bone] = ReadBoneTrack(value, path, name, bone);
                }
            }

            data.Animations.Add(new AnimationClip(name, length, loop, tracks, events));
        }
    }

    private static void ReadEffects(JsonElement animator, string path, string anim, List<AnimationClip.Event> events)
    {
        if (!animator.TryGetProperty("keyframes", out var keyframes) || keyframes.ValueKind != JsonValueKind.Array)
            return;
        foreach (var key in keyframes.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object) continue;
            // Only timeline keys carry script markers; sound and particle keys are ignored.
            if (!key.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.String
                || channel.GetString() != "timeline")
                continue;
            if (!key.TryGetProperty("data_points", out var points) || points.ValueKind != JsonValueKind.Array
                || points.GetArrayLength() == 0)
                continue;
            var point = points[0];
            if (point.ValueKind != JsonValueKind.Object || !point.TryGetProperty("script", out var scriptEl)
                || scriptEl.ValueKind != JsonValueKind.String)
                continue;
            var script = scriptEl.GetString()!.Trim();
            if (script.Length == 0) continue;
            var time = Num(Req(key, "time", path, $"animation '{anim}' timeline keyframe"), path,
                $"animation '{anim}' timeline keyframe time");
            events.Add(new AnimationClip.Event(time, script));
        }
    }

    private static AnimationClip.BoneTrack ReadBoneTrack(JsonElement animator, string path, string anim, string bone)
    {
        var rotation = new List<AnimationClip.Keyframe>();
        var position = new List<AnimationClip.Keyframe>();
        var scale = new List<AnimationClip.Keyframe>();

        if (animator.TryGetProperty("keyframes", out var keyframes) && keyframes.ValueKind == JsonValueKind.Array)
        {
            foreach (var key in keyframes.EnumerateArray())
            {
                if (key.ValueKind != JsonValueKind.Object) continue;
                var channel = key.TryGetProperty("channel", out var channelEl) && channelEl.ValueKind == JsonValueKind.String
                    ? channelEl.GetString()
                    : null;
                var target = channel switch
                {
                    "rotation" => rotation,
                    "position" => position,
                    "scale" => scale,
                    _ => throw new InvalidOperationException(
                        $"Model '{path}' animation '{anim}' bone '{bone}' has unknown channel '{channel ?? "(none)"}'."),
                };

                var mode = AnimationClip.Interpolation.Linear;
                if (key.TryGetProperty("interpolation", out var interpEl))
                {
                    mode = (interpEl.ValueKind == JsonValueKind.String ? interpEl.GetString() : null) switch
                    {
                        "linear" => AnimationClip.Interpolation.Linear,
                        "catmullrom" => AnimationClip.Interpolation.CatmullRom,
                        "step" => AnimationClip.Interpolation.Step,
                        var other => throw new InvalidOperationException(
                            $"Model '{path}' animation '{anim}' bone '{bone}' has unsupported interpolation '{other ?? interpEl.ToString()}'."),
                    };
                }

                var time = Num(Req(key, "time", path, $"animation '{anim}' bone '{bone}' keyframe"), path,
                    $"animation '{anim}' bone '{bone}' keyframe time");
                if (!key.TryGetProperty("data_points", out var points) || points.ValueKind != JsonValueKind.Array
                    || points.GetArrayLength() == 0 || points[0].ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException(
                        $"Model '{path}' animation '{anim}' bone '{bone}' has a keyframe without 'data_points'.");
                // Only the first data point is used (a second one is Blockbench's split key).
                var point = points[0];
                var raw = new Vector3(AnimNum(point, "x", path, anim, bone), AnimNum(point, "y", path, anim, bone),
                    AnimNum(point, "z", path, anim, bone));

                var value = channel switch
                {
                    "rotation" => new Vector3(-raw.X, -raw.Y, raw.Z),
                    "position" => new Vector3(-raw.X, raw.Y, raw.Z) / UnitsPerBlock,
                    _ => raw,
                };
                target.Add(new AnimationClip.Keyframe(time, value, mode));
            }
        }

        // OrderBy is stable, so keys at equal times keep their authored order.
        return new AnimationClip.BoneTrack
        {
            Rotation = rotation.OrderBy(k => k.Time).ToArray(),
            Position = position.OrderBy(k => k.Time).ToArray(),
            Scale = scale.OrderBy(k => k.Time).ToArray(),
        };
    }

    // A keyframe component: a JSON number, or the numeric string Blockbench writes. Molang
    // expressions (any other string) are rejected rather than silently read as 0.
    private static float AnimNum(JsonElement point, string axis, string path, string anim, string bone)
    {
        if (!point.TryGetProperty(axis, out var el))
            throw new InvalidOperationException(
                $"Model '{path}' animation '{anim}' bone '{bone}' has a keyframe without '{axis}'.");
        if (el.ValueKind == JsonValueKind.Number) return (float)el.GetDouble();
        var s = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString();
        if (el.ValueKind == JsonValueKind.String
            && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return v;
        throw new InvalidOperationException(
            $"Model '{path}' animation '{anim}' bone '{bone}' has a non-numeric value '{s}': Molang expressions are not supported.");
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
                throw new InvalidOperationException($"Model '{path}' has a corrupt embedded texture.", e);
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
                    $"Model '{path}' references texture '{source}' which could not be read: {e.Message}", e);
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
            throw new InvalidOperationException($"Model '{path}' texture could not be decoded as PNG.", e);
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
        throw new InvalidOperationException($"Model '{path}' {context} is missing '{name}'.");
    }

    private static Vector3 ReadVec3(JsonElement array, string path, string context)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 3)
            throw new InvalidOperationException($"Model '{path}' {context} needs 3 numbers.");
        return new Vector3(Num(array[0], path, context), Num(array[1], path, context), Num(array[2], path, context));
    }

    private static float Num(JsonElement el, string path, string context)
    {
        if (el.ValueKind == JsonValueKind.Number) return (float)el.GetDouble();
        throw new InvalidOperationException($"Model '{path}' {context} needs a number.");
    }
}
