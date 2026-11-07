using System;
using System.Collections.Concurrent;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Goblins;

/// <summary>
/// A goblin: a short forest raider that lobs the flint it carries at the player from a
/// standoff band, backing off when the player closes in, and once its stones run out runs in
/// for a club swing. It walks rather than hops (auto-jumping a one-block step), floats in water
/// like the slime, and is animated from the Blockbench clips in its model through an
/// <see cref="EntityAnimator"/> (the body, physics and clips are <see cref="RiggedMob"/>'s): the stone leaves the hand at the throw clip's
/// <see cref="ReleaseEvent"/> and the swing lands at the melee clip's <see cref="ImpactEvent"/>,
/// which <c>GoblinManager</c> collects through <see cref="TryTakeThrowRelease"/> and
/// <see cref="TryTakeMeleeImpact"/>. Main thread only.
/// </summary>
public sealed class Goblin : RiggedMob
{
    /// <summary>Groups the goblin's model must provide; the renderer draws each one.</summary>
    public static readonly string[] RequiredParts = EntityContract.HumanoidParts;

    /// <summary>The part that holds and throws the stone.</summary>
    public const string HandPart = "ArmR";

    public const string ThrowClip = "throw";
    public const string MeleeClip = "melee";

    /// <summary>Timeline event on the throw clip at which the stone leaves the hand.</summary>
    public const string ReleaseEvent = "release";

    /// <summary>Timeline event on the melee clip at which the swing connects.</summary>
    public const string ImpactEvent = "impact";

    /// <summary>Draw scale of the flint held in the throwing hand while ammo remains.</summary>
    public const float HeldStoneScale = 0.28f;

    /// <summary>Scale the off hand's carried torch is drawn at (the block's model, not an item
    /// sprite).</summary>
    public const float HeldTorchScale = 0.5f;

    /// <summary>The bone holding a carried torch: the off hand, where the flint rides the right.</summary>
    public const string OffHandPart = "ArmL";

    // The rigs the animation poses, one per entity type and shared with nothing GL-bound, so the
    // torch flame point can be read without the renderer
    private static readonly ConcurrentDictionary<EntityType, PlayerRig> RigCache = new();

    /// <summary>Half the collision body's horizontal edge.</summary>
    public const float HalfExtent = 0.3f;

    /// <summary>Height of the collision body; <see cref="Mob.Position"/> is its center, so the
    /// feet are at <c>Position.Y - Height / 2</c>.</summary>
    public const float Height = 1.3f;

    // A floating goblin paddles toward its goal at this fraction of its walk speed, so it can
    // cross a pond and climb out instead of bobbing in place forever.
    private const float SwimSpeedFactor = 0.5f;

    private float _throwCooldown;
    private float _meleeCooldown;
    private float _losRetry;
    private bool _losBlocked;
    private Vector3 _aimTarget;
    // Which way the goblin sidesteps a charged throw aimed at it: fixed per goblin at spawn from
    // its cell, so the same goblin always breaks the same way instead of jittering
    private readonly float _dodgeSide;
    // Whether this goblin has ever started a throw; the pack alert only pulls a first throw forward
    private bool _thrownOnce;
    // Whether the manager last read the light as dark (a carried torch is out then)
    private bool _dark;
    // Set on the frame the goblin first noticed a target, read (and cleared) by the manager, which
    // alerts the pack with it
    private bool _justNoticed;
    private float _noticePipElapsed = MobConfig.GoblinNoticePipSeconds;
    private bool _noticed;

    public Goblin(EntityType type, Vector3 position, int ammo, bool spawnIn = true, int torches = 0)
        : base(type, position, MobConfig.GoblinWalkSpeed, spawnIn)
    {
        Ammo = Math.Max(0, ammo);
        Torches = Math.Clamp(torches, 0, MobConfig.GoblinTorchMaxCount);
        // The dodge side: fixed at spawn from the starting cell, so the same goblin always breaks
        // the same way out of a charged throw's line
        _dodgeSide = ((MathUtils.FloorToInt(position.X) + MathUtils.FloorToInt(position.Z)) & 1) == 0 ? 1f : -1f;
        // A fresh goblin notices the player before its first throw rather than throwing on its
        // first grounded frame; unjittered so scripted shots repeat.
        _throwCooldown = MobConfig.GoblinFirstThrowDelaySeconds;
    }

    /// <summary>Stones left to throw; the manager drops what remains on death.</summary>
    public int Ammo { get; private set; }

    /// <summary>Adds scavenged stones (flint picked up off the ground), clamped at
    /// <see cref="MobConfig.GoblinStoneMax"/>.</summary>
    public void AddAmmo(int amount) =>
        Ammo = Math.Clamp(Ammo + amount, 0, MobConfig.GoblinStoneMax);

    /// <summary>Torches this goblin still carries; it equips one while it is dark and they drop on
    /// death.</summary>
    public int Torches { get; private set; }

    /// <summary>Whether a carried torch is out: one is left and the manager last read it as dark
    /// (<see cref="SetDark"/>).</summary>
    public bool TorchLit => Torches > 0 && _dark;

    /// <summary>The manager's per-frame light reading for this goblin: whether a carried torch
    /// would be out (<see cref="GoblinTorches.Lit"/>).</summary>
    public void SetDark(float night, bool underground) => _dark = GoblinTorches.Lit(1, night, underground);

    /// <summary>
    /// Where a lit torch's flame burns in the world (the off hand's anchor through the body
    /// effect, the same matrices the renderer draws the torch with), or null while none is out.
    /// The world's fire gathering reads it, so a torch-bearing goblin lights its area and sheds
    /// embers.
    /// </summary>
    public Vector3? TorchFlamePoint
    {
        get
        {
            if (!TorchLit) return null;
            var feet = Position - new Vector3(0f, BodyHalfHeight, 0f);
            var rig = RigCache.GetOrAdd(Type, static type => PlayerRig.FromBbmodel(type.Model));
            var anchor = Animation.HandAnchor(OffHandPart, rig, feet, Facing) * BodyEffect(feet);
            return anchor.Translation;
        }
    }

    /// <summary>
    /// Whether the goblin has given up: at or below <see cref="MobConfig.GoblinFleeHealthShare"/>
    /// of its pool it turns and runs from the player instead of fighting, faster than its walk
    /// (<see cref="MobConfig.GoblinFleeSpeedFactor"/>); it still despawns by distance
    /// like any goblin.
    /// </summary>
    public bool IsFleeing => Health > 0 && Health <= MaxHealth * MobConfig.GoblinFleeHealthShare;

    /// <summary>
    /// Takes whether this goblin first noticed a target since the last call (true once): the
    /// manager uses it to alert the pack (goblins within
    /// <see cref="MobConfig.GoblinPackAlertRadiusBlocks"/> skip their first-throw delay).
    /// </summary>
    public bool TakeJustNoticed()
    {
        var noticed = _justNoticed;
        _justNoticed = false;
        return noticed;
    }

    /// <summary>
    /// Another goblin of the pack just noticed a player: skip this goblin's first-throw delay so
    /// the pack joins in together. Only a goblin that has not thrown yet (and has stones) is
    /// pulled forward; a later alert never resets a running cooldown.
    /// </summary>
    public void AlertPack()
    {
        if (_thrownOnce || IsDying || Ammo <= 0) return;
        _throwCooldown = 0f;
    }

    /// <summary>
    /// The notice pip's clock: 0 on the frame the goblin first had a player to chase (the same
    /// moment its first-throw delay starts), rising to 1 over
    /// <see cref="MobConfig.GoblinNoticePipSeconds"/>; 1 for a goblin that never
    /// noticed anyone, and never restarted when a target is lost and regained.
    /// </summary>
    public float NoticePipProgress =>
        MathUtils.Clamp01(_noticePipElapsed / MobConfig.GoblinNoticePipSeconds);

    /// <inheritdoc />
    public override float DeathSeconds => 0.8f;

    /// <inheritdoc />
    public override float BodyHeight => Height;

    /// <inheritdoc />
    public override float BodyHalfWidth => HalfExtent;

    /// <summary>Where a thrown stone leaves the hand: a little in front of the body at
    /// shoulder height.</summary>
    public Vector3 ThrowOrigin => Position + Forward * 0.4f + new Vector3(0f, 0.35f, 0f);

    /// <summary>Where a stone is aimed on a player whose eye is at <paramref name="eye"/>: the
    /// chest, a little under the eye.</summary>
    public static Vector3 ChestOf(Vector3 eye) => eye - new Vector3(0f, 0.4f, 0f);

    /// <summary>
    /// Whether a goblin body centered at <paramref name="goblinPosition"/> can land a swing on a
    /// player whose eye is at <paramref name="playerEye"/>: horizontally within
    /// <see cref="MobConfig.GoblinMeleeReach"/> and roughly level with the player's
    /// body center.
    /// </summary>
    public static bool InMeleeReach(Vector3 goblinPosition, Vector3 playerEye) =>
        MobTargets.InMeleeReach(goblinPosition, playerEye, MobConfig.GoblinMeleeReach);

    /// <summary>
    /// Throws, naming the model file, unless the goblin's model provides every
    /// <see cref="RequiredParts"/> group, the idle, walk, throw and melee clips, and the
    /// release and impact events the throw and swing are timed by.
    /// </summary>
    public static void ValidateContent(EntityType type)
    {
        EntityContract.RequireHumanoid(type, "Goblin", MeleeClip, ImpactEvent);
        EntityContract.RequireEvent(type, "Goblin", EntityContract.RequireClip(type, "Goblin", ThrowClip), ReleaseEvent);
    }

    /// <summary>
    /// Advances the AI, the physics and the animation. With stones left the goblin keeps to the
    /// throw band (<see cref="MobConfig.GoblinThrowMinRange"/>..
    /// <see cref="MobConfig.GoblinThrowMaxRange"/>) and starts a throw whenever it
    /// stands on the ground with a clear line to the player's chest; out of stones it closes to
    /// the standoff distance and starts a swing when in reach. It stands still while a throw or
    /// swing plays. A null <paramref name="player"/> (nobody to chase) leaves the goblin standing
    /// where it is, still falling, floating and animating, or strolling toward
    /// <paramref name="wander"/> when one is given (<see cref="Mob.WanderGoal"/>).
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, IMobTarget? player, Vector3? wander = null,
        bool dodgeAimedThrow = false)
    {
        if (deltaTime <= 0f || chunks == null) return;

        TickBody(deltaTime);
        // A slowing status effect stretches the attack cooldowns (AttackFactor 1 = unslowed)
        _throwCooldown = MathF.Max(0f, _throwCooldown - deltaTime * AttackFactor);
        _meleeCooldown = MathF.Max(0f, _meleeCooldown - deltaTime * AttackFactor);
        _losRetry = MathF.Max(0f, _losRetry - deltaTime);
        _noticePipElapsed = MathF.Min(MobConfig.GoblinNoticePipSeconds,
            _noticePipElapsed + deltaTime);

        var move = Vector3.Zero;
        if (IsDying) player = null;
        if (player != null)
        {
            // The first sight of a player starts the pip; it never restarts on a re-notice
            if (!_noticed)
            {
                _noticed = true;
                _noticePipElapsed = 0f;
                _justNoticed = true;
            }

            var target = player.Eye;
            var to = new Vector3(target.X - Position.X, 0f, target.Z - Position.Z);
            var dist = to.Length();
            var toward = dist > 1e-3f ? to / dist : Vector3.Zero;
            // The model faces -Z at yaw 0, so this rotation points -Z at the player. It turns over a
            // few frames rather than snapping, except on the very first look; a fleeing goblin faces
            // down its escape, not at the player it runs from.
            if (dist > 1e-3f)
                FaceYaw(YawOf(to) + (IsFleeing ? MathF.PI : 0f), deltaTime);

            move = Decide(dist, toward, chunks, player, dodgeAimedThrow);
        }
        else if (wander is { } goal)
        {
            // Nobody chased (none noticed, or interest lost): stroll toward the wander goal
            move = WanderMove(goal, deltaTime);
        }

        // Walking at full speed on the ground and at a paddle afloat, both slowed by a status effect.
        Walk(move, player?.Eye,
            MobConfig.GoblinWalkSpeed * (IsFleeing ? MobConfig.GoblinFleeSpeedFactor : 1f) * MoveFactor,
            MobConfig.GoblinWalkSpeed * SwimSpeedFactor * MoveFactor, MobConfig.GoblinJumpSpeed, deltaTime, chunks);
    }

    // Chooses this frame's horizontal move (a unit vector or zero) and starts a throw or swing
    // when one is due.
    private Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget player,
        bool dodgeAimedThrow)
    {
        if (Animation.IsActionPlaying) return Vector3.Zero;

        // A charged throw aimed at it: sidestep out of the line (the side fixed per goblin so it
        // never jitters), never throw or swing while the aim holds
        if (dodgeAimedThrow) return new Vector3(-toward.Z, 0f, toward.X) * _dodgeSide;

        // Given up: turn and run, never throw or swing
        if (IsFleeing) return -toward;

        if (Ammo > 0)
        {
            if (dist > MobConfig.GoblinThrowMaxRange) return toward;
            if (dist < MobConfig.GoblinThrowMinRange) return -toward;

            // Grounded only, so the auto-jump can never lift the throw origin over a wall the
            // goblin is walking into; the LOS test is throttled while it stays blocked.
            if (IsGrounded && _throwCooldown <= 0f && _losRetry <= 0f)
            {
                var chest = ChestOf(player.Eye);
                if (!BlockRaycaster.SegmentHitsBlock(ThrowOrigin, chest, chunks))
                {
                    _losBlocked = false;
                    _aimTarget = chest;
                    _thrownOnce = true;
                    Animation.Play(ThrowClip);
                    _throwCooldown = MobConfig.GoblinThrowCooldownSeconds
                                     + (Random.Shared.NextSingle() - 0.5f) * 2f * MobConfig.GoblinThrowCooldownJitter;
                    return Vector3.Zero;
                }

                _losBlocked = true;
                _losRetry = MobConfig.GoblinLosRetrySeconds;
            }

            // Inside the band: hold, unless the last look found the line blocked, in which case
            // keep walking at the player until a throw finds it clear again.
            return _losBlocked ? toward : Vector3.Zero;
        }

        if (InMeleeReach(Position, player.Eye) && _meleeCooldown <= 0f)
        {
            Animation.Play(MeleeClip);
            _meleeCooldown = MobConfig.GoblinMeleeCooldownSeconds;
            return Vector3.Zero;
        }

        return dist > MobConfig.GoblinStandoffBlocks ? toward : Vector3.Zero;
    }

    /// <summary>
    /// True once per throw, at the throw clip's release event while stones remain: spends one
    /// stone and returns where it leaves the hand and the launch velocity that lobs it onto the
    /// chest the throw was aimed at (a maximum-range toss toward it when out of reach).
    /// </summary>
    public bool TryTakeThrowRelease(out Vector3 origin, out Vector3 velocity)
    {
        origin = default;
        velocity = default;
        if (!Animation.ConsumeEvent(ReleaseEvent) || Ammo <= 0) return false;

        origin = ThrowOrigin;
        // The bool is ignored: an out-of-range player still gets the fallback toss.
        GoblinAim.TryLaunchVelocity(origin, _aimTarget, MobConfig.GoblinStoneSpeed,
            ThrownProjectile.Gravity, out velocity);
        Ammo--;
        return true;
    }

    /// <summary>True once per swing, at the melee clip's impact event; the manager decides
    /// whether the player is still in reach.</summary>
    public bool TryTakeMeleeImpact() => Animation.ConsumeEvent(ImpactEvent);
}
