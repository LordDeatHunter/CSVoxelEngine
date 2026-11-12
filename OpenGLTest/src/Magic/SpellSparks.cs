using System.Numerics;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Particles;

namespace VoxelEngine.Magic;

/// <summary>
/// The cosmetic sparks every spell shows, GL-free (they are <see cref="BlockDebrisSystem"/> chips), main
/// thread only: a flying bolt's trail, the burst where it lands, and the burst at the staff when a cast
/// leaves it. The authoritative bolts (<see cref="SpellEffects"/>), a client's predicted bolts
/// (<see cref="SpellBoltPreview"/>) and the cast feedback (<see cref="SpellFeedback"/>) all draw through it.
/// </summary>
internal static class SpellSparks
{
    /// <summary>The sRGB spark color of <paramref name="element"/>: its status effect's tint, else the default bolt color.</summary>
    internal static Vector3 ColorOf(SpellPart element) =>
        element.Effect != null && StatusEffectRegistry.Require(element.Effect).TintLinear is { } tint
            ? ColorSpace.LinearToSrgb(tint)
            : SpellConfig.BoltDefaultColor;

    /// <summary>
    /// A flying bolt's trail: a puff of sparks every <see cref="SpellConfig.BoltSparkSeconds"/>, timed by the
    /// bolt's own <see cref="SpellProjectile.SparkTimer"/>.
    /// </summary>
    internal static void Trail(BlockDebrisSystem debris, SpellProjectile bolt, float deltaTime)
    {
        bolt.SparkTimer -= deltaTime;
        if (bolt.SparkTimer > 0f) return;
        bolt.SparkTimer = SpellConfig.BoltSparkSeconds;
        debris.Spark(bolt.Position, bolt.SparkColor, SpellConfig.ProjectileHalfExtent, SpellConfig.BoltSparkCount,
            SpellConfig.BoltSparkSpeed, SpellConfig.BoltSparkSize);
    }

    /// <summary>The burst where a bolt lands, at <paramref name="position"/> in <paramref name="colorSrgb"/>.</summary>
    internal static void Impact(BlockDebrisSystem debris, Vector3 position, Vector3 colorSrgb) =>
        debris.Spark(position, colorSrgb, SpellConfig.ProjectileHalfExtent, SpellConfig.ImpactSparkCount,
            SpellConfig.ImpactSparkSpeed, SpellConfig.BoltSparkSize);

    /// <summary>
    /// Where a cast leaves the staff: the launch point of its bolt (<see cref="SpellProjectile.Origin"/>),
    /// <see cref="SpellConfig.ProjectileLaunchOffsetBlocks"/> in front of <paramref name="eye"/> along
    /// <paramref name="direction"/> (-Z for a zero direction, as a bolt does).
    /// </summary>
    internal static Vector3 BurstOrigin(Vector3 eye, Vector3 direction)
    {
        var aim = direction.LengthSquared() < 1e-8f ? -Vector3.UnitZ : Vector3.Normalize(direction);
        return eye + aim * SpellConfig.ProjectileLaunchOffsetBlocks;
    }

    /// <summary>The burst at the staff when a cast leaves it, at <paramref name="origin"/> in <paramref name="colorSrgb"/>.</summary>
    internal static void Burst(BlockDebrisSystem debris, Vector3 origin, Vector3 colorSrgb) =>
        debris.Spark(origin, colorSrgb, SpellConfig.ProjectileHalfExtent, SpellConfig.CastBurstSparkCount,
            SpellConfig.CastBurstSparkSpeed, SpellConfig.BoltSparkSize);
}
