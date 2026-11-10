using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Player;

namespace VoxelEngine.Entities;

/// <summary>
/// One entity kind loaded from <c>assets/data/entities/&lt;name&gt;.json</c>: its display name,
/// health pool, death loot and the Blockbench model it draws from. Unlike blocks and items these
/// are not byte ids — the handful of live entity kinds are addressed by name through
/// <see cref="EntityRegistry"/>, so an entity never eats a chunk-storage id.
/// </summary>
public sealed class EntityType
{
    /// <summary>File name the definition was loaded from, without extension.</summary>
    public required string Name { get; init; }

    /// <summary>Display name for logs and future UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Starting (and maximum) health of one spawn; 0 means the kind has no health pool
    /// (it cannot back a <see cref="Mob"/>).</summary>
    public required int MaxHealth { get; init; }

    /// <summary>Table rolled on death; null drops nothing.</summary>
    public LootTable? LootTable { get; init; }

    /// <summary>Blockbench project that supplies the entity's mesh and skin.</summary>
    public required string ModelPath { get; init; }

    /// <summary>
    /// The parsed Blockbench project — parts, skin pixels and animation clips — shared by the
    /// entity, the renderer and the debugger. Parsed once at content load, GL-free.
    /// </summary>
    public required BbmodelData Model { get; init; }

    private IReadOnlyList<string>? _variantPaths;
    private IReadOnlyList<BbmodelData>? _variants;

    /// <summary>
    /// The Blockbench project of every variant (size class or look), index-parallel to
    /// <see cref="Variants"/>. A kind without authored variants has exactly one,
    /// <see cref="ModelPath"/>.
    /// </summary>
    public IReadOnlyList<string> VariantPaths
    {
        get => _variantPaths ??= [ModelPath];
        init => _variantPaths = value;
    }

    /// <summary>
    /// The parsed model of every variant. Never empty: <c>Variants[0]</c> is always
    /// <see cref="Model"/>, so a kind without authored variants has exactly that one.
    /// </summary>
    public IReadOnlyList<BbmodelData> Variants
    {
        get => _variants ??= [Model];
        init => _variants = value;
    }

    /// <summary>The item every spawn holds (the entity file's <c>held</c>), 0 for none.</summary>
    public byte HeldItemId { get; init; }

    /// <summary>The rig group whose hand holds <see cref="HeldItemId"/>; null when it is 0.</summary>
    public string? HeldPart { get; init; }

    /// <summary>The held item's transform in the hand anchor's space (its <c>held.display</c>).</summary>
    public Matrix4x4 HeldTransform { get; init; } = Matrix4x4.Identity;

    /// <summary>
    /// The world matrix of the held item's sprite: <see cref="HeldTransform"/> under the hand anchor of
    /// <see cref="HeldPart"/>, posed by <paramref name="animator"/>, so the item swings with that limb. The
    /// caller multiplies its body effect on, like the parts. Only meaningful when <see cref="HeldItemId"/> is set.
    /// </summary>
    public Matrix4x4 HeldMatrix(EntityAnimator animator, PlayerRig rig, Vector3 feet, float facingYaw) =>
        HeldTransform * animator.HandAnchor(HeldPart!, rig, feet, facingYaw);
}

/// <summary>
/// Static registry of every <see cref="EntityType"/>, published once by
/// <see cref="ContentRegistry.Load"/> before the <see cref="World"/> (and any worker thread)
/// exists, then read-only. Gameplay asks for a kind by name rather than holding a definition.
/// </summary>
public static class EntityRegistry
{
    private static Dictionary<string, EntityType> _byName = new(StringComparer.Ordinal);

    /// <summary>Every registered entity kind by file name.</summary>
    public static IReadOnlyDictionary<string, EntityType> ByName => _byName;

    /// <summary>The kind registered under that file name, or null.</summary>
    public static EntityType? TryGet(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The kind registered under that file name; throws naming the missing content at
    /// startup instead of silently spawning nothing.</summary>
    public static EntityType Require(string name)
    {
        if (_byName.TryGetValue(name, out var type)) return type;
        throw new InvalidOperationException($"No entity named '{name}' is registered.");
    }

    internal static void Publish(Dictionary<string, EntityType> types) => _byName = types;

    internal static void Reset() => _byName = new Dictionary<string, EntityType>(StringComparer.Ordinal);
}
