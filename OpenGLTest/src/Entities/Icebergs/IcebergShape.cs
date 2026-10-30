using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Models;

namespace VoxelEngine.Entities.Icebergs;

/// <summary>
/// The collision shape of one iceberg variant: the model's own cubes as axis-aligned boxes in
/// model space (blocks, y = 0 the waterline), read through <see cref="EntityModel.CubeBoxes"/> so
/// the frame is exactly the baked mesh's. Also the one definition of how a berg is turned, scaled
/// and placed: <see cref="RotateQuarter"/> for the boxes and <see cref="ModelMatrix"/> for the
/// draw agree exactly, so collision never drifts from what is drawn. Scale is horizontal only, so
/// step heights and freeboard stay as authored. GL-free and immutable.
/// </summary>
public sealed class IcebergShape
{
    private readonly (Vector3 Min, Vector3 Max)[] _boxes;

    private IcebergShape((Vector3 Min, Vector3 Max)[] boxes)
    {
        _boxes = boxes;
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var volume = 0f;
        foreach (var (boxMin, boxMax) in boxes)
        {
            min = Vector3.Min(min, boxMin);
            max = Vector3.Max(max, boxMax);
            var size = boxMax - boxMin;
            volume += size.X * size.Y * size.Z;
        }

        Bounds = boxes.Length == 0 ? (Vector3.Zero, Vector3.Zero) : (min, max);
        Volume = volume;
    }

    /// <summary>The model's cubes as boxes in model space, in blocks; y = 0 is the waterline.</summary>
    public IReadOnlyList<(Vector3 Min, Vector3 Max)> Boxes => _boxes;

    /// <summary>The union of <see cref="Boxes"/> in model space.</summary>
    public (Vector3 Min, Vector3 Max) Bounds { get; }

    /// <summary>The summed volume of <see cref="Boxes"/> in cubic blocks, at scale 1.</summary>
    public float Volume { get; }

    /// <summary>How deep the berg reaches below the waterline, in blocks.</summary>
    public float Draft => -Bounds.Min.Y;

    /// <summary>How high the berg stands above the waterline, in blocks.</summary>
    public float Freeboard => Bounds.Max.Y;

    /// <summary>The shape of one Blockbench project, its cubes in the baked mesh's frame.</summary>
    public static IcebergShape FromModel(BbmodelData model)
    {
        var boxes = new List<(Vector3 Min, Vector3 Max)>();
        foreach (var box in EntityModel.CubeBoxes(model))
            boxes.Add(box);
        return new IcebergShape(boxes.ToArray());
    }

    /// <summary>The shape of every variant of <paramref name="type"/>, in variant order.</summary>
    public static IcebergShape[] ForType(EntityType type)
    {
        var shapes = new IcebergShape[type.Variants.Count];
        for (var i = 0; i < shapes.Length; i++)
            shapes[i] = FromModel(type.Variants[i]);
        return shapes;
    }

    /// <summary>
    /// Throws naming the variant file unless every variant of <paramref name="type"/> has at least
    /// one box and floats: some of it below the waterline and some above.
    /// </summary>
    public static void ValidateContent(EntityType type)
    {
        for (var i = 0; i < type.Variants.Count; i++)
        {
            var shape = FromModel(type.Variants[i]);
            var path = type.VariantPaths[i];
            if (shape._boxes.Length == 0)
                throw new InvalidOperationException($"Iceberg model '{path}' has no cubes.");
            if (shape.Draft <= 0f)
                throw new InvalidOperationException(
                    $"Iceberg model '{path}' has nothing below the waterline (y = 0); a berg needs a draft.");
            if (shape.Freeboard <= 0f)
                throw new InvalidOperationException(
                    $"Iceberg model '{path}' has nothing above the waterline (y = 0); a berg needs a freeboard.");
        }
    }

    /// <summary>
    /// The berg's mass at horizontal <paramref name="scale"/>: density times volume, the volume
    /// growing with the square of the scale because the height never scales.
    /// </summary>
    public float Mass(float scale) => IcebergConfig.Density * Volume * scale * scale;

    /// <summary>
    /// Turns <paramref name="p"/> a quarter about the vertical axis <paramref name="turns"/> times,
    /// each turn mapping (x, z) to (z, -x): exactly what <c>Matrix4x4.CreateRotationY(π/2)</c> does
    /// to a row vector. Exact, since it only swaps and negates.
    /// </summary>
    public static Vector3 RotateQuarter(Vector3 p, int turns) => (turns & 3) switch
    {
        0 => p,
        1 => new Vector3(p.Z, p.Y, -p.X),
        2 => new Vector3(-p.X, p.Y, -p.Z),
        _ => new Vector3(-p.Z, p.Y, p.X),
    };

    /// <summary>
    /// The model-to-world matrix of a berg: horizontal scale, then <paramref name="turns"/> quarter
    /// turns (with exact 0 and ±1 entries, agreeing with <see cref="RotateQuarter"/>), then the
    /// waterline center. The renderer appends its wobble to this one matrix.
    /// </summary>
    public static Matrix4x4 ModelMatrix(int turns, float scale, Vector3 center)
    {
        var (c, s) = (turns & 3) switch
        {
            0 => (1f, 0f),
            1 => (0f, 1f),
            2 => (-1f, 0f),
            _ => (0f, -1f),
        };
        var rotation = Matrix4x4.Identity;
        rotation.M11 = c;
        rotation.M13 = -s;
        rotation.M31 = s;
        rotation.M33 = c;
        return Matrix4x4.CreateScale(scale, 1f, scale) * rotation * Matrix4x4.CreateTranslation(center);
    }

    /// <summary>
    /// One model-space <paramref name="box"/> placed in the world: scaled horizontally, turned and
    /// moved to <paramref name="center"/>, re-sorted so Min stays below Max on every axis.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) Place(
        (Vector3 Min, Vector3 Max) box, int turns, float scale, Vector3 center)
    {
        var horizontal = new Vector3(scale, 1f, scale);
        var a = RotateQuarter(box.Min * horizontal, turns);
        var b = RotateQuarter(box.Max * horizontal, turns);
        return (Vector3.Min(a, b) + center, Vector3.Max(a, b) + center);
    }

    /// <summary>Appends every box placed at (<paramref name="turns"/>, <paramref name="scale"/>, <paramref name="center"/>) to <paramref name="into"/>.</summary>
    public void PlaceAll(int turns, float scale, Vector3 center, List<(Vector3 Min, Vector3 Max)> into)
    {
        foreach (var box in _boxes)
            into.Add(Place(box, turns, scale, center));
    }

    /// <summary>The placed <see cref="Bounds"/>: the union of every box <see cref="PlaceAll"/> gives.</summary>
    public (Vector3 Min, Vector3 Max) PlacedBounds(int turns, float scale, Vector3 center) =>
        Place(Bounds, turns, scale, center);
}
