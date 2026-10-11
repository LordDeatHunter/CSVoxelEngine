using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Owns the live goblins and the stones they throw: ticks their AI and physics, spawns a stone
/// at each throw's release event, bills the club swing at each melee impact that still finds a
/// player in reach, shoves grounded goblins a player walks into, rolls death loot plus the
/// stones a goblin still carried, and spawns new ones on dry forest surface in a ring around a
/// player. Every rule runs against a list of <see cref="IMobTarget"/>s (the local camera, or a
/// server's connected players), each goblin chasing its nearest one. A stone that misses
/// shatters: it never becomes a pickup. Every spawn is judged by the global <see cref="MobCaps"/>
/// it shares with the other mob kinds. Main thread only.
/// </summary>
public sealed class GoblinManager : IMobPopulation
{
    private readonly EntityType _type;
    private readonly MobCaps _caps;
    private readonly List<Goblin> _goblins = [];
    private readonly List<ThrownProjectile> _stones = [];
    // Parallel to _stones: the goblin each stone was thrown by, so a stone never hits its thrower
    private readonly List<Goblin> _stoneOwners = [];
    // The mobs a frame's stones can hit, rebuilt per stone pass
    private readonly List<Mob> _stoneMobs = [];
    private readonly List<byte> _loot = [];
    private readonly byte _flintId;
    // One delegate for every goblin's collector: it only ever takes flint
    private readonly Func<byte, bool> _canScavenge;
    // The goblins a frame's collectors were built for, in the same order (AwardScavenged's index)
    private readonly List<Goblin> _collectorGoblins = [];
    // The camera overload's one-element target list, reused so it allocates nothing per frame.
    private readonly List<IMobTarget> _singleTarget = new(1);
    private float _spawnTimer;
    private int _spawnCursor;

    /// <summary>Validates the goblin content first, so a model missing a part, clip or event
    /// fails at startup naming its file. Joins <paramref name="caps"/> (the world's, shared with
    /// every other mob kind), or caps itself alone when none is given (tests, previews).</summary>
    public GoblinManager(EntityType type, MobCaps? caps = null)
    {
        Goblin.ValidateContent(type);
        _type = type;
        _flintId = ContentRegistry.RequireItem("flint").Id;
        _canScavenge = id => id == _flintId;
        _caps = caps ?? new MobCaps();
        _caps.Register(this);
    }

    /// <summary>Every goblin still drawn: the living ones plus any playing their death
    /// (<see cref="Mob.IsDying"/>), which are no longer targets and do not count toward the cap.</summary>
    public IReadOnlyList<Goblin> Goblins => _goblins;

    /// <summary>Goblins that are alive (not yet dying), what the global caps count.</summary>
    public int LivingCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _goblins.Count; i++)
                if (!_goblins[i].IsDying) count++;
            return count;
        }
    }

    /// <summary>A client's network reconcile: the server no longer reports this goblin, so it plays
    /// its death where it stands and is then dropped.</summary>
    public bool Remove(Goblin goblin)
    {
        if (!_goblins.Contains(goblin)) return false;
        goblin.BeginDying();
        return true;
    }

    /// <summary>Advances the death and spawn clocks of the goblins without simulating them (a
    /// client never ticks the AI), dropping the ones whose death is over. A finished death bursts
    /// into the mob's skin colour (<paramref name="debris"/>, null when nothing draws it).</summary>
    public void AnimateMirrors(float deltaTime, BlockDebrisSystem? debris = null)
    {
        for (var i = _goblins.Count - 1; i >= 0; i--)
        {
            _goblins[i].AnimateMirror(deltaTime);
            if (!_goblins[i].IsRemovable) continue;
            if (debris != null) GroundDust.DeathPoof(_goblins[i], debris);
            _goblins.RemoveAt(i);
        }
    }

    /// <summary>
    /// Alerts every other living goblin within <see cref="WorldGenerationConfig.GoblinPackAlertRadiusBlocks"/>
    /// of one that just noticed a player: each skips its first-throw delay, so the pack joins in
    /// together. One pass over the pack, run on the noticing frame only.
    /// </summary>
    internal void AlertPackAround(Goblin noticed)
    {
        var radius = WorldGenerationConfig.GoblinPackAlertRadiusBlocks;
        var radiusSquared = radius * radius;
        for (var i = 0; i < _goblins.Count; i++)
        {
            var other = _goblins[i];
            if (other == noticed || other.IsDying) continue;
            var dx = other.Position.X - noticed.Position.X;
            var dz = other.Position.Z - noticed.Position.Z;
            if (dx * dx + dz * dz <= radiusSquared)
                other.AlertPack();
        }
    }

    /// <summary>Stones in flight; drawn and mirrored like the player's own throws.</summary>
    public IReadOnlyList<ThrownProjectile> Stones => _stones;

    /// <summary>Test seam: launches a stone as if <paramref name="owner"/> had thrown it, so a
    /// test can fly one at a mob without driving the whole throw clip.</summary>
    internal ThrownProjectile TestThrowStone(Goblin owner, Vector3 origin, Vector3 velocity)
    {
        var stone = new ThrownProjectile(_flintId, origin, velocity, 0f, 1f);
        _stones.Add(stone);
        _stoneOwners.Add(owner);
        return stone;
    }

    /// <summary>
    /// Appends one <see cref="DropCollector"/> per living goblin with ammo room: it takes only
    /// flint, which the world's drop pass flies in like a player's pickup. The order is kept so
    /// <see cref="AwardScavenged"/> can credit the right goblin by its collector index.
    /// </summary>
    public void AppendCollectors(List<DropCollector> into)
    {
        _collectorGoblins.Clear();
        for (var i = 0; i < _goblins.Count; i++)
        {
            var goblin = _goblins[i];
            if (goblin.IsDying || goblin.Ammo >= WorldGenerationConfig.GoblinStoneMax) continue;
            _collectorGoblins.Add(goblin);
            into.Add(new DropCollector(goblin.Position, _canScavenge));
        }
    }

    /// <summary>Credits a drop scavenged by the collector at <paramref name="index"/> of this
    /// frame's <see cref="AppendCollectors"/> order: flint becomes ammo, clamped at the cap.</summary>
    public void AwardScavenged(int index, byte id)
    {
        if (index < 0 || index >= _collectorGoblins.Count || id != _flintId) return;
        _collectorGoblins[index].AddAmmo(1);
    }

    /// <summary>Health the goblins took off every target together during the most recent
    /// <c>Update</c>; each hit also reached its target through <see cref="IMobTarget.TakeHit"/>.
    /// The singleplayer caller applies it to its <see cref="PlayerHealth"/> and shakes.</summary>
    public int DamageDealtThisFrame { get; private set; }

    /// <summary>
    /// Adds a goblin at an exact body-centre position: a forced spawn (scenarios, save restores, a
    /// client's mirror, tests), which may go past the natural cap but never the hard cap. Null, and
    /// nothing added, once the hard cap is full. The ring spawner adds through here too, after its
    /// own natural-cap check. A null <paramref name="ammo"/> rolls the stone count it carries.
    /// </summary>
    public Goblin? Spawn(Vector3 position, int? ammo = null)
    {
        if (_caps.ForcedRoom <= 0) return null;
        var stones = ammo ?? Random.Shared.Next(WorldGenerationConfig.GoblinStoneMin,
            WorldGenerationConfig.GoblinStoneMax + 1);
        var goblin = new Goblin(_type, position, stones);
        _goblins.Add(goblin);
        return goblin;
    }

    /// <summary>Ticks the goblins against the one local player (singleplayer and the tests); see
    /// <see cref="Update(float, ChunkManager, DropManager, IReadOnlyList{IMobTarget})"/>.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, Camera camera,
        BlockDebrisSystem? debris = null, IReadOnlyList<Slime>? slimes = null, Vector3? aimOrigin = null,
        Vector3? aimDirection = null, float aimCharge = 0f)
    {
        if (camera == null)
        {
            DamageDealtThisFrame = 0;
            return;
        }

        _singleTarget.Clear();
        _singleTarget.Add(camera);
        Update(deltaTime, chunks, drops, _singleTarget, debris, slimes, aimOrigin, aimDirection, aimCharge);
    }

    /// <summary>
    /// Whether a charged throw from <paramref name="origin"/> along <paramref name="direction"/> is
    /// aimed at a body at <paramref name="target"/>: inside the cone
    /// (<see cref="WorldGenerationConfig.GoblinDodgeConeDegrees"/>) and within
    /// <see cref="WorldGenerationConfig.GoblinDodgeRangeBlocks"/>. The dodge rule's reading of the
    /// aim; the caller owns the charge threshold.
    /// </summary>
    internal static bool IsAimedAt(Vector3 target, Vector3 origin, Vector3 direction)
    {
        var to = target - origin;
        var distance = to.Length();
        if (distance < 1e-3f || distance > WorldGenerationConfig.GoblinDodgeRangeBlocks) return false;
        var cosine = MathF.Cos(WorldGenerationConfig.GoblinDodgeConeDegrees * (MathF.PI / 180f));
        return Vector3.Dot(direction, to / distance) >= cosine;
    }

    /// <summary>
    /// Ticks every goblin and stone against <paramref name="targets"/>: each goblin throws at and
    /// closes on its nearest target (<see cref="MobTargets.Nearest"/>), a swing's impact hurts every
    /// non-immune target still in reach, a stone hurts the non-immune target it touches (any
    /// touched target stops it), a grounded goblin is shoved off by whoever walks into it, one past
    /// the despawn radius of every target is culled, and the spawn ring cycles over the targets
    /// under the one shared cap and timer. Every hit reaches its target through
    /// <see cref="IMobTarget.TakeHit"/> and is summed into <see cref="DamageDealtThisFrame"/>. With
    /// no targets nothing chases, despawns or spawns. A charged throw
    /// (<paramref name="aimCharge"/>, at least
    /// <see cref="WorldGenerationConfig.GoblinDodgeChargeThreshold"/>) aimed at a goblin from
    /// <paramref name="aimOrigin"/> along <paramref name="aimDirection"/> makes that goblin
    /// sidestep out of the line while the aim holds; singleplayer passes the local arm's aim.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, IReadOnlyList<IMobTarget> targets,
        BlockDebrisSystem? debris = null, IReadOnlyList<Slime>? slimes = null, Vector3? aimOrigin = null,
        Vector3? aimDirection = null, float aimCharge = 0f)
    {
        DamageDealtThisFrame = 0;
        if (chunks == null || targets == null) return;

        var dodgeAimed = aimCharge >= WorldGenerationConfig.GoblinDodgeChargeThreshold
                         && aimOrigin.HasValue && aimDirection.HasValue;
        var despawnSquared = WorldGenerationConfig.GoblinDespawnRadiusBlocks
            * WorldGenerationConfig.GoblinDespawnRadiusBlocks;

        for (var i = _goblins.Count - 1; i >= 0; i--)
        {
            var goblin = _goblins[i];
            // Its noticed target while it keeps interest (Mob.PickTarget), else a wander goal
            var chased = goblin.PickTarget(targets, deltaTime, chunks);
            var dodge = dodgeAimed && IsAimedAt(goblin.Position, aimOrigin!.Value, aimDirection!.Value);
            goblin.Update(deltaTime, chunks, chased >= 0 && !goblin.IsDying ? targets[chased] : null,
                chased < 0 ? goblin.WanderGoal(targets.Count, deltaTime) : null, dodge);
            goblin.UpdateHazards(deltaTime, chunks);

            // A goblin that just noticed a player pulls its whole pack forward
            if (goblin.TakeJustNoticed())
                AlertPackAround(goblin);

            if (goblin.IsDying)
            {
                if (goblin.IsRemovable)
                {
                    if (debris != null) GroundDust.DeathPoof(goblin, debris);
                    _goblins.RemoveAt(i);
                }
                continue;
            }

            if (goblin.IsDead)
            {
                DropLoot(goblin, drops);
                goblin.BeginDying();
                continue;
            }

            if (goblin.TryTakeThrowRelease(out var origin, out var velocity))
            {
                _stones.Add(new ThrownProjectile(_flintId, origin, velocity, 0f, 1f));
                _stoneOwners.Add(goblin);
            }

            // The swing is decided at its impact frame, so a player who backed off during the
            // wind-up dodges it.
            if (goblin.TryTakeMeleeImpact())
            {
                for (var t = 0; t < targets.Count; t++)
                {
                    var target = targets[t];
                    var eye = target.Eye;
                    if (target.IsImmune || !Goblin.InMeleeReach(goblin.Position, eye)) continue;

                    var damage = WorldGenerationConfig.GoblinMeleeDamage;
                    DamageDealtThisFrame += damage;
                    var away = new Vector3(eye.X - goblin.Position.X, 0f, eye.Z - goblin.Position.Z);
                    target.TakeHit(damage, away, WorldGenerationConfig.GoblinKnockbackSpeed,
                        WorldGenerationConfig.GoblinKnockbackLift);
                }
            }

            if (goblin.IsGrounded)
            {
                for (var t = 0; t < targets.Count; t++)
                {
                    var eye = targets[t].Eye;
                    if (!MobTargets.BodyTouches(eye, goblin.Position, Goblin.HalfExtent)) continue;

                    // Walking into a goblin is harmless: the player shoves it aside.
                    var away = new Vector3(goblin.Position.X - eye.X, 0f, goblin.Position.Z - eye.Z);
                    goblin.Shove(away, WorldGenerationConfig.GoblinShoveSpeed);
                }
            }

            // Past every target's despawn radius, or idle long and far off: fades out through the
            // death pose, dropping nothing
            if (targets.Count > 0 && (MobTargets.MinHorizontalDistanceSquared(targets, goblin.Position) > despawnSquared
                                      || goblin.ShouldIdleDespawn(targets)))
                goblin.BeginDying();
        }

        UpdateStones(deltaTime, chunks, targets, slimes);
        UpdateSpawning(deltaTime, chunks, targets);
    }

    private void UpdateStones(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets,
        IReadOnlyList<Slime>? slimes)
    {
        // The mobs a stone can hit: every living goblin (never its own thrower, which each stone
        // skips) plus the slimes
        _stoneMobs.Clear();
        for (var i = 0; i < _goblins.Count; i++)
            if (!_goblins[i].IsDying) _stoneMobs.Add(_goblins[i]);
        if (slimes != null)
            for (var i = 0; i < slimes.Count; i++)
                if (!slimes[i].IsDying) _stoneMobs.Add(slimes[i]);

        for (var i = _stones.Count - 1; i >= 0; i--)
        {
            var stone = _stones[i];
            var owner = _stoneOwners[i];
            // A stone hits mobs in its way (but never its own thrower) as well as targets
            var alive = stone.Update(deltaTime, chunks, _stoneMobs, out var mobHit, owner);

            // Any touched target stops the stone; it hurts the first touched one that is not
            // immune, so a hovering player beside a survival one never shields them.
            var touched = false;
            IMobTarget? hurt = null;
            for (var t = 0; t < targets.Count; t++)
            {
                var target = targets[t];
                if (!MobTargets.BodyTouches(target.Eye, stone.Position, ThrownProjectile.HalfExtent)) continue;
                touched = true;
                if (!target.IsImmune)
                {
                    hurt = target;
                    break;
                }
            }

            if (touched)
            {
                if (hurt != null)
                {
                    var damage = WorldGenerationConfig.GoblinStoneDamage;
                    DamageDealtThisFrame += damage;
                    var eye = hurt.Eye;
                    var away = new Vector3(eye.X - stone.Position.X, 0f, eye.Z - stone.Position.Z);
                    hurt.TakeHit(damage, away, WorldGenerationConfig.GoblinStoneKnockbackSpeed, 0f);
                }
                RemoveStone(i);
                continue;
            }

            if (mobHit != null)
            {
                // A mob in the way takes the stone and stops it: goblins hurt each other and the
                // slimes, exactly like a stone that struck a player
                mobHit.TakeDamage(WorldGenerationConfig.GoblinStoneDamage);
                var away = new Vector3(mobHit.Position.X - stone.Position.X, 0f,
                    mobHit.Position.Z - stone.Position.Z);
                mobHit.Shove(away, WorldGenerationConfig.GoblinStoneKnockbackSpeed);
                RemoveStone(i);
                continue;
            }

            if (!alive)
            {
                // A stone that hit the ground, a wall or its lifetime shatters: no drop, so
                // missed throws never pile up as pickups (the carried stones drop on death).
                RemoveStone(i);
            }
        }
    }

    private void RemoveStone(int index)
    {
        _stones.RemoveAt(index);
        _stoneOwners.RemoveAt(index);
    }

    private void DropLoot(Goblin goblin, DropManager drops)
    {
        if (_type.LootTable != null)
        {
            _loot.Clear();
            var (lootX, lootY, lootZ) = goblin.Position.BlockCell();
            _type.LootTable.Roll(new LootContext(lootX, lootY, lootZ, 0), _loot);

            foreach (var id in _loot)
                drops.SpawnAt(id, goblin.Position, MobLoot.RandomToss());
        }

        // The stones it never threw.
        for (var i = 0; i < goblin.Ammo; i++)
            drops.SpawnAt(_flintId, goblin.Position, MobLoot.RandomToss());
    }

    // One shared timer, under the natural cap every mob kind shares; each expiry tries the ring
    // round the next target in turn.
    private void UpdateSpawning(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets)
    {
        if (!_caps.AllowsNaturalSpawn) return;

        _spawnTimer += deltaTime;
        if (_spawnTimer < WorldGenerationConfig.GoblinSpawnIntervalSeconds) return;
        _spawnTimer = 0f;

        if (targets.Count == 0) return;
        var index = _spawnCursor % targets.Count;
        _spawnCursor = index + 1;

        if (TryFindSpawn(chunks, targets[index].Eye, out var position))
        {
            // A ring spawn rolls a pack: the found cell leads, and one to two more join it within
            // GoblinPackSpawnRadiusBlocks while the shared natural cap still has room
            var pack = 1 + Random.Shared.Next(WorldGenerationConfig.GoblinPackMaxSize);
            Spawn(position);
            for (var member = 1; member < pack && _caps.AllowsNaturalSpawn; member++)
            {
                if (TryFindPackMate(chunks, position, out var mate))
                    Spawn(mate);
            }
        }
    }

    // A pack mate's spot: the same dry-surface search, ringed on the leader's cell rather than the
    // player
    private static bool TryFindPackMate(ChunkManager chunks, Vector3 leader, out Vector3 position)
    {
        if (!MobSpawner.TryFindSurfaceSpawn(chunks, leader, 1f,
                WorldGenerationConfig.GoblinPackSpawnRadiusBlocks, WorldGenerationConfig.GoblinPackSpawnTries,
                out var x, out var groundY, out var z))
        {
            position = default;
            return false;
        }

        position = new Vector3(x + 0.5f, groundY + 1 + Goblin.Height * 0.5f, z + 0.5f);
        return true;
    }

    /// <summary>The goblin's spawn ring through <see cref="MobSpawner.TryFindSurfaceSpawn"/>,
    /// returning the body centre standing on the found ground cell.</summary>
    internal static bool TryFindSpawn(ChunkManager chunks, Vector3 playerPosition, out Vector3 position)
    {
        if (!MobSpawner.TryFindSurfaceSpawn(chunks, playerPosition,
                WorldGenerationConfig.GoblinSpawnMinRadiusBlocks, WorldGenerationConfig.GoblinSpawnMaxRadiusBlocks,
                WorldGenerationConfig.GoblinSpawnTries, out var x, out var groundY, out var z))
        {
            position = default;
            return false;
        }

        position = new Vector3(x + 0.5f, groundY + 1 + Goblin.Height * 0.5f, z + 0.5f);
        return true;
    }
}
