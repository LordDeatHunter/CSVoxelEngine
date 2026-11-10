using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Models;

namespace VoxelEngine.Entities.Spiders;

/// <summary>
/// What the leg solver needs to know about a spider model, read once from its Blockbench project: the eight
/// legs' hips, segment lengths and resting foot points, and which bones they are. The contract of the model: four
/// legs a side, <c>LegR1</c>..<c>LegR4</c> on +X and <c>LegL1</c>..<c>LegL4</c> on -X (1 the front pair), each a
/// top-level group whose cubes run straight out along X from its origin (the hip), with a child group named
/// <c>...Lower</c> whose origin (the knee) sits straight out from the hip and whose cubes run on along X to the
/// foot. The legs rest flat because the loader has no rest rotations; <see cref="SpiderLegs"/> poses them.
/// GL-free and immutable.
/// </summary>
public sealed class SpiderRig
{
    /// <summary>Legs per side.</summary>
    public const int LegsPerSide = 4;

    /// <summary>Legs in all: the right side first (front to back), then the left.</summary>
    public const int LegCount = LegsPerSide * 2;

    private static readonly ConcurrentDictionary<EntityType, SpiderRig> Cache = new();

    private readonly Dictionary<string, (int Leg, bool Lower)> _bones = new(StringComparer.Ordinal);

    /// <summary>One leg: its hip in model space, which side it is on (+1 the model's right, -1 its left), its two
    /// segment lengths and where its foot rests on the model's ground plane.</summary>
    public readonly record struct Leg(Vector3 Hip, float Side, float UpperLength, float LowerLength, Vector3 Rest)
    {
        /// <summary>The leg's full length, hip to foot tip.</summary>
        public float Reach => UpperLength + LowerLength;
    }

    private SpiderRig(EntityType type)
    {
        Legs = new Leg[LegCount];
        for (var i = 0; i < LegCount; i++)
        {
            var side = i < LegsPerSide ? 1f : -1f;
            var upperName = $"Leg{(side > 0f ? 'R' : 'L')}{i % LegsPerSide + 1}";
            var lowerName = upperName + "Lower";
            var upper = Require(type, upperName);
            var lower = Require(type, lowerName);
            if (upper.Parent != null)
                throw Broken(type, $"group '{upperName}' must be a top-level group, so the body's clips never move a hip");
            if (lower.Parent != upperName)
                throw Broken(type, $"group '{lowerName}' must sit inside '{upperName}'");

            var knee = lower.Pivot - upper.Pivot;
            if (knee.X * side <= 0f || MathF.Abs(knee.Y) > 1e-4f || MathF.Abs(knee.Z) > 1e-4f)
                throw Broken(type, $"the origin of '{lowerName}' must sit straight out along X from '{upperName}'");

            var lowerLength = 0f;
            foreach (var cube in lower.Cubes)
                lowerLength = MathF.Max(lowerLength, side > 0f ? cube.Max.X : -cube.Min.X);
            if (lowerLength <= 0f)
                throw Broken(type, $"the cubes of '{lowerName}' must run out along X from its origin");

            var upperLength = MathF.Abs(knee.X);
            var azimuth = SpiderConfig.LegAzimuthDegrees[i % LegsPerSide] * (MathF.PI / 180f);
            var stance = (upperLength + lowerLength) * SpiderConfig.LegStanceShare;
            var rest = new Vector3(upper.Pivot.X + side * MathF.Cos(azimuth) * stance, 0f,
                upper.Pivot.Z - MathF.Sin(azimuth) * stance);
            Legs[i] = new Leg(upper.Pivot, side, upperLength, lowerLength, rest);
            _bones[upperName] = (i, false);
            _bones[lowerName] = (i, true);
        }
    }

    /// <summary>The eight legs: the right side front to back, then the left.</summary>
    public Leg[] Legs { get; }

    /// <summary>The rig of <paramref name="type"/>'s model, read on first use; throws naming the model file when it
    /// breaks the contract above.</summary>
    public static SpiderRig For(EntityType type) => Cache.GetOrAdd(type, static t => new SpiderRig(t));

    /// <summary>Whether <paramref name="bone"/> is a leg segment, and which: the leg's index and whether it is the
    /// lower segment.</summary>
    public bool TryGetLegBone(string bone, out int leg, out bool lower)
    {
        var found = _bones.TryGetValue(bone, out var entry);
        leg = entry.Leg;
        lower = entry.Lower;
        return found;
    }

    private static BbmodelPart Require(EntityType type, string name)
    {
        if (!type.Model.Parts.TryGetValue(name, out var part) || part.Cubes.Count == 0)
            throw Broken(type, $"it needs a non-empty group named '{name}'");
        return part;
    }

    private static InvalidOperationException Broken(EntityType type, string what) =>
        new($"Spider model '{type.ModelPath}': {what}.");
}
