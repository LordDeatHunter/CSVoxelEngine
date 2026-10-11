using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// One connected player as the server tracks it: its own <see cref="Inventory"/> (never shared with
/// another player or with the world's own), the server-owned creative and flight flags (survival by
/// default), the pose its last accepted <see cref="NetMessage.PlayerState"/> put it at (judged by
/// <see cref="MovementRules"/>, with the corrections it must ack) and when that arrived, and what
/// judging its intents needs: the <see cref="PlayerContext"/> the world runs them
/// through, the newest sequence id, the mining progress, the edit rate limiter and the attack pace.
/// The server persists it per player (<see cref="ServerPlayerSaves"/>) and the world's drop pickup
/// credits it directly (<see cref="World.ServerPlayers"/>). GL-free; main thread only, like the
/// rest of the server's state.
///
/// <para>
/// Its survival status is server-owned too, by the singleplayer rules: a <see cref="PlayerHealth"/>
/// pool (<see cref="Status"/>) billed by falls observed from its accepted states
/// (<see cref="ObserveAcceptedState"/>, through a <see cref="FallTracker"/>), by lava the body
/// stands in (<see cref="TickStatus"/>, read through <see cref="PlayerBodyProbe"/>) and by mob hits
/// (it is an <see cref="IMobTarget"/>, so the mob managers bill it directly and queue the
/// knockback for its client). A dead player stays dead, and immune, until it is
/// <see cref="Respawn"/>ed at its <see cref="RespawnEye"/>. Every change sets
/// <see cref="StatusDirty"/>, so the server knows to tell the client.
/// </para>
/// </summary>
public sealed class ServerPlayer : IMobTarget
{
    public ServerPlayer(int id, string name, Vector3 position)
    {
        Id = id;
        Name = name;
        Position = position;
        // Cached once, so the per-frame pickup loop never allocates a delegate
        CanAcceptDrop = Inventory.CanAccept;
        Context = new PlayerContext(Inventory) { Name = name, Eye = position };
    }

    /// <summary>
    /// The context every world intent of this player runs through: this player's own inventory and
    /// server-owned creative flag, and the eye and aim of the intent being applied (the last
    /// accepted pose between intents).
    /// </summary>
    public PlayerContext Context { get; }

    /// <summary>
    /// The sequence id of the newest intent (world or inventory) this player sent; an intent whose
    /// id is not above it is a replay and is refused. Inventory snapshots carry it, so the client
    /// can tell an answer to an older intent from one to its newest.
    /// </summary>
    public int LastSeq { get; private set; }

    /// <summary>This player's mining progress (see <see cref="BreakProgress"/>).</summary>
    public BreakProgress Breaking { get; } = new();

    /// <summary>Paces this player's edits (breaks, placements, uses and throws).</summary>
    public RateLimiter EditLimiter { get; } = new(WorldGenerationConfig.NetEditIntentsPerSecond,
        WorldGenerationConfig.NetEditIntentBurst);

    private double _lastAttackAt = double.NegativeInfinity;

    // The flags of the last creative/flight request the server refused and reported, so a stream of
    // states still carrying it (sent before the client heard back) is not answered again
    private NetPlayerFlags? _refusedRequest;

    /// <summary>
    /// Takes <paramref name="seq"/> as this player's newest intent, or refuses it (false, nothing
    /// recorded) when it is not newer than the last one: ids only ever increase.
    /// </summary>
    public bool AcceptSeq(int seq)
    {
        if (seq <= LastSeq) return false;
        LastSeq = seq;
        return true;
    }

    /// <summary>
    /// Whether an attack of <paramref name="kind"/> with <paramref name="held"/> may land at
    /// <paramref name="now"/>: the pace since the last one (<see cref="AttackPacing"/>), less
    /// <paramref name="slackSeconds"/> of network jitter. An attack that may land is recorded.
    /// </summary>
    public bool TryAttack(NetAttackKind kind, Item? held, double now, double slackSeconds)
    {
        if (now - _lastAttackAt + slackSeconds < AttackPacing.MinGapSeconds(kind, held))
            return false;
        _lastAttackAt = now;
        return true;
    }

    /// <summary>
    /// Records the creative/flight request a <see cref="NetMessage.PlayerState"/> carried against
    /// what the server granted, and returns whether the client should be told: true only the first
    /// time a request is refused, so it learns its modes once instead of on every state.
    /// </summary>
    public bool ShouldReportModes(NetPlayerFlags requested)
    {
        var modes = requested & (NetPlayerFlags.Creative | NetPlayerFlags.Flying);
        if (modes == GrantedModes)
        {
            _refusedRequest = null;
            return false;
        }
        if (_refusedRequest == modes)
            return false;
        _refusedRequest = modes;
        return true;
    }

    /// <summary>The modes the server grants this player, as player-state flags.</summary>
    public NetPlayerFlags GrantedModes =>
        (Creative ? NetPlayerFlags.Creative : NetPlayerFlags.None)
        | (Flying ? NetPlayerFlags.Flying : NetPlayerFlags.None);

    /// <summary>
    /// Points <see cref="Context"/> at an intent's eye and aim, with this player's current creative
    /// flag, before the world applies it.
    /// </summary>
    public void Aim(Vector3 eye, Vector3 direction)
    {
        Context.Eye = eye;
        Context.Forward = direction;
        Context.ViewDirection = direction;
        Context.IsCreative = Creative;
    }

    public int Id { get; }

    /// <summary>The player's name, unique among the players online; it also keys the save file.</summary>
    public string Name { get; }

    /// <summary>Everything this player carries; the server's copy is the truth the client mirrors.</summary>
    public Inventory Inventory { get; } = Inventory.Empty();

    /// <summary>Server-owned: whether creative-only intents (catalog grants and clears) are accepted.</summary>
    public bool Creative { get; set; }

    /// <summary>Server-owned flight flag; only ever set together with <see cref="Creative"/>.</summary>
    public bool Flying { get; set; }

    /// <summary>
    /// The player's health pool, owned by the server: falls, lava and mob hits land here, and the
    /// client only mirrors it. Persisted with the player.
    /// </summary>
    public PlayerHealth Status { get; } = new();

    /// <summary>The player's current health, 0..<see cref="PlayerHealth.MaxHealth"/>.</summary>
    public int Health => Status.Health;

    /// <summary>Whether the player is dead: held, immune and ignored until <see cref="Respawn"/>.</summary>
    public bool IsDead => Status.IsDead;

    /// <summary>
    /// The eye this player respawns at, or null for the server's spawn (the fallback
    /// <see cref="Respawn"/> is given). Persisted with the player.
    /// </summary>
    public Vector3? RespawnEye { get; set; }

    /// <summary>
    /// Set whenever the status the client mirrors changed (health, death, a knockback to apply), so
    /// the server sends it a fresh status; the server clears it once one is sent.
    /// </summary>
    public bool StatusDirty { get; set; }

    // The fall state, fed only from accepted states and reset by the server's own teleports
    private readonly FallTracker _fall = new();

    // A mob hit's shove, queued for the client's own physics to apply (the server never simulates
    // a player's body): the newest horizontal impulse wins, the strongest lift is kept
    private Vector3 _pendingKnockback;
    private float _pendingLift;

    // The newest PlayerCorrection issued to this player, and the one its states must ack before
    // their positions count again
    private int _correctionSeq, _awaitedCorrection;

    /// <summary>
    /// Numbers a new <see cref="NetMessage.PlayerCorrection"/> the server is about to send this
    /// player and makes it the one awaited: from here on a state's position is ignored until the
    /// client acks this id (<see cref="AcceptsStatePosition"/>), since states it sent before
    /// snapping still carry the position the correction undid.
    /// </summary>
    public int IssueCorrection() => _awaitedCorrection = ++_correctionSeq;

    /// <summary>
    /// Whether a <see cref="NetMessage.PlayerState"/> acking correction <paramref name="ack"/> may
    /// move the player: only once the client has applied the newest correction it was sent.
    /// </summary>
    public bool AcceptsStatePosition(int ack) => ack >= _awaitedCorrection;

    /// <summary>What the movement rules remember between this player's states (its hover time).</summary>
    public MovementState Movement { get; } = new();

    /// <summary>The accepted camera eye position.</summary>
    public Vector3 Position { get; set; }

    /// <summary>The mobs chase and hit this player at its accepted eye.</summary>
    Vector3 IMobTarget.Eye => Position;

    /// <summary>No mob hurts a creative, flying or dead player.</summary>
    public bool IsImmune => Creative || Flying || IsDead;

    /// <summary>The client's cosmetic sneaking flag: mobs notice a sneaking player from half as far.</summary>
    public bool IsSneaking => ((NetPlayerFlags)Flags & NetPlayerFlags.Sneaking) != 0;

    /// <summary>
    /// A mob hit landed on this player: the damage comes off <see cref="Status"/> at once, and the
    /// shove is queued (<see cref="TakePendingKnockback"/>) for the client to apply, since only the
    /// client simulates its body. A zero direction leaves the queued impulse as it was.
    /// </summary>
    public void TakeHit(int damage, Vector3 awayHorizontal, float knockbackSpeed, float lift)
    {
        Status.TakeDamage(damage);
        awayHorizontal.Y = 0f;
        if (awayHorizontal.LengthSquared() > 1e-6f)
            _pendingKnockback = Vector3.Normalize(awayHorizontal) * knockbackSpeed;
        _pendingLift = MathF.Max(_pendingLift, lift);
        StatusDirty = true;
    }

    /// <summary>
    /// Hands over the knockback queued since the last call (a horizontal impulse in blocks/s and an
    /// upward lift) and clears it. Returns whether any was queued.
    /// </summary>
    public bool TakePendingKnockback(out Vector3 impulse, out float lift)
    {
        impulse = _pendingKnockback;
        lift = _pendingLift;
        _pendingKnockback = Vector3.Zero;
        _pendingLift = 0f;
        return impulse.LengthSquared() > 0f || lift > 0f;
    }

    /// <summary>
    /// Feeds the fall rule one accepted <see cref="NetMessage.PlayerState"/> (called only when the
    /// state's position was taken), billing a landing to <see cref="Status"/>. The teleport bound is
    /// the distance the accepted position itself may move over <paramref name="stateGapSeconds"/>
    /// (<see cref="NetValidation.AllowedDistance"/>), so nothing an accepted state can do is ever
    /// mistaken for a teleport; only the server's own teleports reset the fall. A dead player, or
    /// one whose surroundings are not loaded (<see cref="PlayerBodyProbe.IsKnown"/>), is not judged.
    /// </summary>
    public void ObserveAcceptedState(ChunkManager chunks, double stateGapSeconds)
    {
        if (IsDead || !PlayerBodyProbe.IsKnown(Position, chunks))
            return;
        var feet = Position.Y - Camera.EyeHeight;
        var fall = _fall.Observe(feet, PlayerBodyProbe.Grounded(Position, chunks), Flying,
            PlayerBodyProbe.InWater(Position, chunks), Creative,
            NetValidation.AllowedDistance(stateGapSeconds, WorldGenerationConfig.NetMaxMoveBlocksPerSecond));
        if (fall > 0)
        {
            Status.TakeDamage(fall);
            StatusDirty = true;
        }
    }

    /// <summary>
    /// The per-tick half of the status: lava burns a body standing in it (creative and flight are
    /// immune, an unloaded body is not judged) and the damage flash decays. A dead player is left
    /// as it is until <see cref="Respawn"/>.
    /// </summary>
    public void TickStatus(float dt, ChunkManager chunks)
    {
        if (IsDead)
            return;
        var before = Status.Health;
        var inLava = PlayerBodyProbe.IsKnown(Position, chunks) && PlayerBodyProbe.InLava(Position, chunks);
        Status.UpdateLava(dt, inLava, Creative || Flying);
        Status.Update(dt);
        if (Status.Health != before || Status.IsDead)
            StatusDirty = true;
    }

    /// <summary>
    /// The server moves the player: the accepted eye jumps to <paramref name="eye"/> and the fall
    /// and the hover time are forgotten, since a teleport is never a fall or a hover.
    /// </summary>
    public void Teleport(Vector3 eye)
    {
        Position = eye;
        Aim(eye, Forward);
        _fall.Reset(eye.Y - Camera.EyeHeight, grounded: false);
        Movement.AirborneStillSeconds = 0;
    }

    /// <summary>Forgets the fall built up so far, at the current position (after a correction).</summary>
    public void ResetFall() => _fall.Reset(Position.Y - Camera.EyeHeight, grounded: false);

    /// <summary>
    /// Brings a dead player back: full health, at its <see cref="RespawnEye"/> or else
    /// <paramref name="fallbackEye"/> (the server's spawn), with the fall forgotten.
    /// </summary>
    public void Respawn(Vector3 fallbackEye)
    {
        Status.Respawn();
        Teleport(RespawnEye ?? fallbackEye);
        StatusDirty = true;
    }

    public float Yaw { get; set; }

    public float Pitch { get; set; }

    /// <summary>The state flags the client last reported, rebroadcast in the player list.</summary>
    public byte Flags { get; set; }

    /// <summary>The swing this player last made (a <see cref="NetPlayerAction"/>), rebroadcast so
    /// other clients can play it on this player's model.</summary>
    public NetPlayerAction LastAction { get; private set; }

    /// <summary>Moves on every <see cref="NoteAction"/>, so a client sees a repeat of the same swing.</summary>
    public byte ActionSeq { get; private set; }

    /// <summary>Records a swing for the next player list broadcast.</summary>
    public void NoteAction(NetPlayerAction action)
    {
        LastAction = action;
        unchecked { ActionSeq++; }
    }

    /// <summary>Server clock of the last accepted <see cref="NetMessage.PlayerState"/>.</summary>
    public double LastStateAt { get; set; }

    /// <summary>
    /// Set when the server changed this inventory outside an intent (a drop picked up), so the next
    /// pump sends this player a fresh snapshot; cleared once one is sent.
    /// </summary>
    public bool InventoryChanged { get; set; }

    /// <summary>Whether a pickup of an id would fit this player's inventory (a cached method group).</summary>
    public Func<byte, bool> CanAcceptDrop { get; }

    /// <summary>Unit look direction from <see cref="Yaw"/> and <see cref="Pitch"/>.</summary>
    public Vector3 Forward => Vector3Utils.FromYawPitch(Yaw, Pitch);

    /// <summary>
    /// Applies a saved player file: the pose when it has one, the inventory, the health, the respawn
    /// eye, and the modes only as far as <paramref name="allowCreative"/> lets this server grant them.
    /// Returns whether a position was restored, so the caller can spawn the player where they left.
    /// </summary>
    public bool Restore(PlayerSaveData? saved, bool allowCreative)
    {
        if (saved == null)
            return false;
        if (saved.Inventory is { } inventory)
            Inventory.Import(inventory);
        if (saved.Health is { } health)
            Status.Restore(health);
        if (saved.RespawnEye is { Length: 3 } r)
            RespawnEye = new Vector3(r[0], r[1], r[2]);
        Creative = allowCreative && saved.Creative == true;
        Flying = Creative && saved.Flying == true;
        Yaw = saved.Yaw ?? Yaw;
        Pitch = saved.Pitch ?? Pitch;
        if (saved.Position is not { Length: 3 } p)
            return false;
        Position = new Vector3(p[0], p[1], p[2]);
        return true;
    }

    /// <summary>The player as its save file records it (no lighting clock: that is the world's).</summary>
    public PlayerSaveData Capture() => new()
    {
        Position = [Position.X, Position.Y, Position.Z],
        Yaw = Yaw,
        Pitch = Pitch,
        Creative = Creative,
        Flying = Flying,
        Health = Status.Health,
        RespawnEye = RespawnEye is { } r ? [r.X, r.Y, r.Z] : null,
        Inventory = Inventory.Export(),
    };
}
