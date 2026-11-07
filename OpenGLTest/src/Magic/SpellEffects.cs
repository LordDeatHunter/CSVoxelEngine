using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.Status;

namespace VoxelEngine.Magic;

/// <summary>
/// What an accepted cast does, owned by <see cref="World"/> and built only where the world is
/// authoritative (singleplayer and a server, never a client); main thread only. <see cref="Cast"/> is
/// the <see cref="SpellCaster.Effect"/> hook World installs. The recipe's shape decides the delivery
/// (<see cref="SpellPart.Delivery"/>): a projectile launches a <see cref="SpellProjectile"/> that this
/// component flies each update and lands as a direct hit on a mob (<see cref="SpellImpactRules.DamageFor"/>
/// plus the element's status effect) or as the element's impacts on a cell (<see cref="SpellBlockImpact"/>);
/// a self cast applies the element's effect to the caster and its block conversions around the caster's
/// feet. Bolts never hit players, are not saved and are not sent to clients; a non-server world shows
/// them as a trail of sparks.
/// </summary>
internal sealed class SpellEffects
{
    private readonly World _world;
    private readonly ChunkManager _chunks;
    private readonly SpellBlockImpact _blocks;
    private readonly BlockDebrisSystem _debris;
    private readonly bool _serverMode;
    private readonly List<SpellProjectile> _projectiles = [];

    internal SpellEffects(World world, ChunkManager chunks, StatusEffectBlockWriter writer, WorldItemUse itemUse,
        BlockDebrisSystem debris, bool serverMode)
    {
        _world = world;
        _chunks = chunks;
        _blocks = new SpellBlockImpact(chunks, writer, itemUse);
        _debris = debris;
        _serverMode = serverMode;
    }

    /// <summary>The bolts in flight, oldest first.</summary>
    internal IReadOnlyList<SpellProjectile> Projectiles => _projectiles;

    /// <summary>The cast hook: runs <paramref name="cast"/>'s effect.</summary>
    internal void Cast(SpellCast cast) => Fire(cast.Caster, cast.Parts, cast.Stats, cast.Eye, cast.Direction);

    /// <summary>
    /// Delivers a compiled recipe's element by its shape from <paramref name="eye"/> along
    /// <paramref name="direction"/>; a recipe missing either part does nothing.
    /// </summary>
    internal void Fire(PlayerContext caster, IReadOnlyList<SpellPart> parts, SpellStats stats, Vector3 eye,
        Vector3 direction)
    {
        var element = SpellImpactRules.ElementOf(parts);
        var shape = SpellImpactRules.ShapeOf(parts);
        if (element == null || shape == null) return;

        if (shape.Delivery == SpellDelivery.Self)
        {
            ApplyToCaster(caster, element);
            _blocks.ApplyAtFeet(element, caster.Eye - new Vector3(0f, Camera.EyeHeight, 0f));
            return;
        }

        if (_projectiles.Count >= SpellConfig.MaxLiveProjectiles)
            _projectiles.RemoveAt(0);
        _projectiles.Add(new SpellProjectile(caster, element, stats, eye, direction) { SparkColor = SparkColorOf(element) });
    }

    /// <summary>Flies every bolt and lands those that hit something; nothing while the world is not playing.</summary>
    internal void Update(float deltaTime)
    {
        if (_projectiles.Count == 0 || !_world.IsPlaying) return;
        var mobs = _world.Mobs;
        for (var i = _projectiles.Count - 1; i >= 0; i--)
        {
            var bolt = _projectiles[i];
            var flight = bolt.Update(deltaTime, _chunks, mobs);
            if (flight == SpellFlight.Flying)
            {
                Trail(bolt, deltaTime);
                continue;
            }

            _projectiles.RemoveAt(i);
            if (flight == SpellFlight.HitMob)
                HitMob(bolt, bolt.HitMob!);
            else if (flight == SpellFlight.HitCell)
                _blocks.ApplyAt(bolt.Element, bolt.ImpactCell, bolt.ImpactNormal);
            if (flight != SpellFlight.Spent && !_serverMode)
                _debris.Spark(bolt.Position, bolt.SparkColor, SpellConfig.ProjectileHalfExtent,
                    SpellConfig.ImpactSparkCount, SpellConfig.ImpactSparkSpeed, SpellConfig.BoltSparkSize);
        }
    }

    /// <summary>
    /// Scenario seam: fires <paramref name="parts"/> from the local player's eye and aim without a staff
    /// or mana, like the <c>prime</c> op. False when the recipe is invalid.
    /// </summary>
    internal bool TestCast(IReadOnlyList<SpellPart> parts)
    {
        var stats = new SpellRecipe(parts).Compile();
        if (!stats.IsValid) return false;
        var context = _world.LocalPlayer.LocalContext();
        Fire(context, parts, stats, context.Eye, context.Forward);
        return true;
    }

    private void HitMob(SpellProjectile bolt, Mob mob)
    {
        _world.Strikes.StrikeMob(mob, SpellImpactRules.DamageFor(bolt.Stats), bolt.Origin, bolt.Caster.Health);
        if (bolt.Element.Effect != null)
            StatusEffects.Apply(mob, bolt.Element.Effect);
    }

    // The caster as the immune-aware target it is: the local camera, a server's connected player, or
    // failing both its own pool with creative as the immunity
    private void ApplyToCaster(PlayerContext caster, SpellPart element)
    {
        var effect = element.Effect;
        if (effect == null) return;
        if (caster.Camera != null)
        {
            StatusEffects.Apply((IMobTarget)caster.Camera, effect);
            return;
        }
        var players = _world.ServerPlayers;
        for (var i = 0; i < players.Count; i++)
        {
            if (!ReferenceEquals(players[i].Context, caster)) continue;
            StatusEffects.Apply(players[i], effect);
            return;
        }
        if (caster.Health != null)
            StatusEffects.Apply(caster.Health, caster.IsCreative, effect);
    }

    // A cosmetic spark trail behind a flying bolt; a server draws nothing
    private void Trail(SpellProjectile bolt, float deltaTime)
    {
        if (_serverMode) return;
        bolt.SparkTimer -= deltaTime;
        if (bolt.SparkTimer > 0f) return;
        bolt.SparkTimer = SpellConfig.BoltSparkSeconds;
        _debris.Spark(bolt.Position, bolt.SparkColor, SpellConfig.ProjectileHalfExtent, SpellConfig.BoltSparkCount,
            SpellConfig.BoltSparkSpeed, SpellConfig.BoltSparkSize);
    }

    // The element's status tint, else the default bolt color
    private static Vector3 SparkColorOf(SpellPart element) =>
        element.Effect != null && StatusEffectRegistry.Require(element.Effect).TintLinear is { } tint
            ? ColorSpace.LinearToSrgb(tint)
            : SpellConfig.BoltDefaultColor;
}
