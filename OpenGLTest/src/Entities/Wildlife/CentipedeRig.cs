using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Models;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// What the body chain needs to know about a centipede model, read once from its Blockbench project: the joints
/// front to back and how far apart they are. The contract of the model: <c>Head</c>, <c>Segment1</c>..<c>SegmentN</c>
/// and <c>Tail</c> are top-level groups whose origins sit on the body's axis (x = 0) one behind the other along +Z
/// (the model faces -Z); everything else hangs off one of them (a segment's leg pair, the mandibles, the prongs)
/// and rides it. GL-free and immutable.
/// </summary>
public sealed class CentipedeRig
{
    private const string HeadPart = "Head";
    private const string SegmentPrefix = "Segment";
    private const string TailPart = "Tail";

    private static readonly ConcurrentDictionary<EntityType, CentipedeRig> Cache = new();

    private readonly Dictionary<string, int> _jointOfPart = new(StringComparer.Ordinal);

    private CentipedeRig(EntityType type)
    {
        var parts = type.Model.Parts;
        var names = new List<string> { HeadPart };
        for (var n = 1; parts.ContainsKey(SegmentPrefix + n); n++) names.Add(SegmentPrefix + n);
        names.Add(TailPart);

        Pivots = new Vector3[names.Count];
        Links = new float[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            if (!parts.TryGetValue(names[i], out var part))
                throw Broken(type, $"it needs a group named '{names[i]}'");
            if (part.Parent != null)
                throw Broken(type, $"group '{names[i]}' must be a top-level group, so the chain can place it on its own");
            if (MathF.Abs(part.Pivot.X) > 1e-4f)
                throw Broken(type, $"the origin of '{names[i]}' must sit on the body's axis (x = 0)");

            Pivots[i] = part.Pivot;
            if (i == 0) continue;
            Links[i] = part.Pivot.Z - Pivots[i - 1].Z;
            if (Links[i] <= 0f)
                throw Broken(type, $"the origin of '{names[i]}' must sit behind (+Z of) the one of '{names[i - 1]}'");
            Length += Links[i];
        }

        foreach (var name in parts.Keys)
        {
            var joint = names.IndexOf(TopLevelOf(parts, name));
            if (joint >= 0) _jointOfPart[name] = joint;
        }
    }

    /// <summary>The joints' group origins in model space (blocks), the head's first.</summary>
    public Vector3[] Pivots { get; }

    /// <summary>The distance from joint i to the one ahead of it (entry 0 is unused).</summary>
    public float[] Links { get; }

    /// <summary>The number of joints: the head, every segment and the tail.</summary>
    public int Count => Pivots.Length;

    /// <summary>The length of the chain, the head's joint to the tail's.</summary>
    public float Length { get; }

    /// <summary>The rig of <paramref name="type"/>'s model, read on first use; throws naming the model file when it
    /// breaks the contract above.</summary>
    public static CentipedeRig For(EntityType type) => Cache.GetOrAdd(type, static t => new CentipedeRig(t));

    /// <summary>The joint <paramref name="part"/> rides (itself, for a joint's own group), or -1 for a part that
    /// hangs off none of them.</summary>
    public int JointOf(string part) => _jointOfPart.GetValueOrDefault(part, -1);

    private static string TopLevelOf(Dictionary<string, BbmodelPart> parts, string name)
    {
        // The depth bound only guards a parent cycle in a broken file
        for (var depth = 0; depth < 32; depth++)
        {
            if (!parts.TryGetValue(name, out var part) || part.Parent == null) return name;
            name = part.Parent;
        }
        return name;
    }

    private static InvalidOperationException Broken(EntityType type, string what) =>
        new($"Centipede model '{type.ModelPath}': {what}.");
}
