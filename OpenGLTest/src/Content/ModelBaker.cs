using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Turns a flattened <see cref="BlockModel"/> into a <see cref="BakedModel"/>: model-space
/// boxes become cell-local quads, texture variables become atlas slices, and Minecraft's UV
/// conventions are converted to this engine's.
///
/// The two conventions differ only by a mirror, and consistently so: for the four side faces
/// Minecraft's u runs opposite to the engine's, for top and bottom its v does (see
/// <c>FlipU</c>/<c>FlipV</c>). Applying that single flip makes a <c>cube_all</c> bake
/// reproduce the hand-written full-cube UVs in <see cref="Chunk"/> exactly, and makes a
/// sub-cell box with default UVs reproduce <c>Chunk.CropUv</c> — so models and the procedural
/// emitters sample the atlas identically.
///
/// Runs on the main thread at startup, after <see cref="TextureAtlas.Build"/>.
/// </summary>
public static class ModelBaker
{
    // Faces must land on their plane within this to count as covering it; model coordinates
    // are 1/16ths, so anything looser than a texel is a modelling mistake, not rounding.
    private const float Epsilon = 1e-4f;

    private static readonly string[] FaceNames = ["north", "south", "west", "east", "up", "down"];

    // Corner tables in Chunk.CubeFaces order. X/Y/Z pick the box's min (0) or max (1) on that
    // axis; Uv is Minecraft's corner index for the same corner, which is what its uv rect is
    // addressed by. Sides are Minecraft's order rotated by two, top and bottom match it.
    private static readonly (int X, int Y, int Z, int Uv)[][] Corners =
    [
        [(0, 0, 0, 2), (0, 1, 0, 3), (1, 1, 0, 0), (1, 0, 0, 1)], // north, -Z
        [(1, 0, 1, 2), (1, 1, 1, 3), (0, 1, 1, 0), (0, 0, 1, 1)], // south, +Z
        [(0, 0, 1, 2), (0, 1, 1, 3), (0, 1, 0, 0), (0, 0, 0, 1)], // west,  -X
        [(1, 0, 0, 2), (1, 1, 0, 3), (1, 1, 1, 0), (1, 0, 1, 1)], // east,  +X
        [(0, 1, 0, 0), (0, 1, 1, 1), (1, 1, 1, 2), (1, 1, 0, 3)], // up,    +Y
        [(0, 0, 1, 0), (0, 0, 0, 1), (1, 0, 0, 2), (1, 0, 1, 3)], // down,  -Y
    ];

    // Minecraft's u runs against the engine's on the side faces, its v on top and bottom
    private static readonly bool[] FlipU = [true, true, true, true, false, false];
    private static readonly bool[] FlipV = [false, false, false, false, true, true];

    /// <summary>Engine face index of a Minecraft face name, or -1 when it is not one.</summary>
    public static int FaceIndexOf(string name) => name switch
    {
        "north" => 0,
        "south" => 1,
        "west" => 2,
        "east" => 3,
        "up" => 4,
        "down" => 5,
        _ => -1,
    };

    /// <summary>Bakes every element of <paramref name="model"/> into atlas-ready quads.</summary>
    public static BakedModel Bake(BlockModel model)
    {
        var quads = new List<BakedQuad>(6);

        foreach (var element in model.Elements)
        {
            var from = ReadVector(element.From, "from", model.Source) / 16f;
            var to = ReadVector(element.To, "to", model.Source) / 16f;
            var shade = element.Shade ?? true;
            if (element.Faces == null) continue;

            foreach (var (name, face) in element.Faces)
            {
                var faceIndex = FaceIndexOf(name);
                if (faceIndex < 0)
                    throw new InvalidOperationException(
                        $"Model '{model.Source}' has an unknown face '{name}'; expected north/south/west/east/up/down.");

                quads.Add(BakeFace(model, element, face, faceIndex, from, to, shade));
            }
        }

        var hasFullFace = new bool[6];
        var faceTiles = new ushort[6];
        var fallback = quads.Count > 0 ? quads[0].Tile : default;
        for (var f = 0; f < 6; f++) faceTiles[f] = fallback;

        foreach (var quad in quads)
        {
            if (!quad.IsFullFace || hasFullFace[quad.Face]) continue;
            hasFullFace[quad.Face] = true;
            faceTiles[quad.Face] = quad.Tile;
        }

        var isFullCube = true;
        foreach (var full in hasFullFace) isFullCube &= full;

        return new BakedModel(quads.ToArray(), isFullCube, hasFullFace, faceTiles);
    }

    private static BakedQuad BakeFace(BlockModel model, ModelDefinition.ElementDefinition element,
        ModelDefinition.FaceDefinition face, int faceIndex, Vector3 from, Vector3 to, bool shade)
    {
        var textureName = ResolveTexture(model, face.Texture, FaceNames[faceIndex]);
        var tile = TextureAtlas.TileOf(textureName);

        var uv = face.Uv ?? DefaultUv(faceIndex, from * 16f, to * 16f);
        if (uv.Length != 4)
            throw new InvalidOperationException(
                $"Model '{model.Source}' face '{FaceNames[faceIndex]}' has {uv.Length} uv values; expected 4.");

        var rotation = face.Rotation ?? 0;
        if (rotation % 90 != 0 || rotation < 0 || rotation >= 360)
            throw new InvalidOperationException(
                $"Model '{model.Source}' face '{FaceNames[faceIndex]}' has uv rotation {rotation}; expected 0, 90, 180 or 270.");
        var uvShift = rotation / 90;

        var positions = new Vector3[4];
        var uvs = new Vector2[4];
        var corners = Corners[faceIndex];

        for (var j = 0; j < 4; j++)
        {
            var corner = corners[j];
            positions[j] = new Vector3(
                corner.X == 0 ? from.X : to.X,
                corner.Y == 0 ? from.Y : to.Y,
                corner.Z == 0 ? from.Z : to.Z);

            // Minecraft addresses its uv rect by corner index: 0 and 1 take u1, 2 and 3 take
            // u2; 0 and 3 take v1, 1 and 2 take v2. A uv rotation walks that index around.
            var i = (corner.Uv + uvShift) & 3;
            var u = (i is 0 or 1 ? uv[0] : uv[2]) / 16f;
            var v = (i is 0 or 3 ? uv[1] : uv[3]) / 16f;
            if (FlipU[faceIndex]) u = 1f - u;
            if (FlipV[faceIndex]) v = 1f - v;

            // Tile-local: the quad's Tile picks the atlas slice, so the UV stays 0..1 on it
            uvs[j] = new Vector2(u, v);
        }

        if (element.Rotation != null)
            ApplyRotation(positions, element.Rotation, model.Source);

        var normal = FaceNormal(positions);
        var dominantFace = DominantFace(normal);

        var cullFace = -1;
        if (face.Cullface != null)
        {
            cullFace = FaceIndexOf(face.Cullface);
            if (cullFace < 0)
                throw new InvalidOperationException(
                    $"Model '{model.Source}' face '{FaceNames[faceIndex]}' has an unknown cullface '{face.Cullface}'.");
        }

        return new BakedQuad(positions, uvs, normal, dominantFace, cullFace,
            IsFullFace(positions, dominantFace), shade, tile);
    }

    /// <summary>Minecraft's per-face default uv, derived from the box extents in 0..16.</summary>
    private static float[] DefaultUv(int faceIndex, Vector3 from, Vector3 to) => faceIndex switch
    {
        0 => [16f - to.X, 16f - to.Y, 16f - from.X, 16f - from.Y], // north
        1 => [from.X, 16f - to.Y, to.X, 16f - from.Y],             // south
        2 => [from.Z, 16f - to.Y, to.Z, 16f - from.Y],             // west
        3 => [16f - to.Z, 16f - to.Y, 16f - from.Z, 16f - from.Y], // east
        4 => [from.X, from.Z, to.X, to.Z],                         // up
        _ => [from.X, 16f - to.Z, to.X, 16f - from.Z],             // down
    };

    private static void ApplyRotation(Vector3[] positions, ModelDefinition.RotationDefinition rotation, string source)
    {
        var origin = ReadVector(rotation.Origin, "rotation.origin", source) / 16f;
        var axis = rotation.Axis?.ToLowerInvariant() switch
        {
            "x" => 0,
            "y" => 1,
            "z" => 2,
            _ => throw new InvalidOperationException(
                $"Model '{source}' has rotation axis '{rotation.Axis}'; expected x, y or z."),
        };

        var radians = rotation.Angle * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        // Rescale grows the box on the two axes it spins in, so a rotated slab still reaches
        // the cell edge; the factor is Minecraft's 1/cos(angle).
        var scale = rotation.Rescale == true && cos != 0f ? 1f / MathF.Abs(cos) : 1f;

        for (var i = 0; i < positions.Length; i++)
        {
            var v = positions[i] - origin;
            v = axis switch
            {
                0 => new Vector3(v.X, v.Y * cos - v.Z * sin, v.Y * sin + v.Z * cos),
                1 => new Vector3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos),
                _ => new Vector3(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z),
            };
            if (scale != 1f)
                v = axis switch
                {
                    0 => new Vector3(v.X, v.Y * scale, v.Z * scale),
                    1 => new Vector3(v.X * scale, v.Y, v.Z * scale),
                    _ => new Vector3(v.X * scale, v.Y * scale, v.Z),
                };
            positions[i] = v + origin;
        }
    }

    private static Vector3 FaceNormal(Vector3[] positions)
    {
        var normal = Vector3.Cross(positions[1] - positions[0], positions[2] - positions[0]);
        return normal.NormalizeSafeOr(Vector3.UnitY, 0f);
    }

    /// <summary>Engine face index a normal points along: its dominant axis, signed.
    /// Internal because <see cref="MultiblockShape.Rotate"/> has to recompute it for a quad
    /// whose normal it just turned.</summary>
    internal static int DominantFace(Vector3 normal)
    {
        var ax = MathF.Abs(normal.X);
        var ay = MathF.Abs(normal.Y);
        var az = MathF.Abs(normal.Z);
        if (az >= ax && az >= ay) return normal.Z < 0f ? 0 : 1;
        if (ax >= ay) return normal.X < 0f ? 2 : 3;
        return normal.Y > 0f ? 4 : 5;
    }

    // A quad counts as a full face when it sits on that cube face's plane and spans the whole
    // cell in the other two axes: only then do per-corner AO and neighbour lighting apply.
    private static bool IsFullFace(Vector3[] positions, int face)
    {
        var planeAxis = face < 2 ? 2 : face < 4 ? 0 : 1;
        var planeValue = face is 1 or 3 or 4 ? 1f : 0f;

        for (var axis = 0; axis < 3; axis++)
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var position in positions)
            {
                var value = axis == 0 ? position.X : axis == 1 ? position.Y : position.Z;
                min = MathF.Min(min, value);
                max = MathF.Max(max, value);
            }

            if (axis == planeAxis)
            {
                if (MathF.Abs(min - planeValue) > Epsilon || MathF.Abs(max - planeValue) > Epsilon) return false;
            }
            else if (MathF.Abs(min) > Epsilon || MathF.Abs(max - 1f) > Epsilon)
            {
                return false;
            }
        }

        return true;
    }

    private static Vector3 ReadVector(float[]? values, string field, string source)
    {
        if (values is not { Length: 3 })
            throw new InvalidOperationException(
                $"Model '{source}' has a '{field}' with {values?.Length ?? 0} values; expected 3.");
        return new Vector3(values[0], values[1], values[2]);
    }

    // Texture variables chain (#side -> #all -> "stone"); a missing link names the model
    private static string ResolveTexture(BlockModel model, string? reference, string faceName)
    {
        var value = reference ?? throw new InvalidOperationException(
            $"Model '{model.Source}' face '{faceName}' has no texture.");

        for (var guard = 0; value.StartsWith('#'); guard++)
        {
            if (guard > 16)
                throw new InvalidOperationException(
                    $"Model '{model.Source}' face '{faceName}' has a cyclic texture variable '{value}'.");
            if (!model.Textures.TryGetValue(value[1..], out var next))
                throw new InvalidOperationException(
                    $"Model '{model.Source}' face '{faceName}' references undefined texture variable '{value}'.");
            value = next;
        }

        return NormalizeTextureName(value);
    }

    // Strips the namespace and any block/ folder so a vanilla-style reference resolves against
    // the flat assets/textures/blocks atlas
    private static string NormalizeTextureName(string name)
    {
        var value = name;
        var colon = value.IndexOf(':');
        if (colon >= 0) value = value[(colon + 1)..];
        value = value.TrimStart('/');
        if (value.StartsWith("blocks/", StringComparison.Ordinal)) return value["blocks/".Length..];
        if (value.StartsWith("block/", StringComparison.Ordinal)) return value["block/".Length..];
        return value;
    }
}
