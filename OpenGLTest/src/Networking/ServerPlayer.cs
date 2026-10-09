using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// One connected player as the server tracks it: its own <see cref="Inventory"/> (never shared with
/// another player or with the world's own), the server-owned creative and flight flags (survival by
/// default), the pose its last accepted <see cref="NetMessage.PlayerState"/> put it at and when that
/// arrived, and what judging its intents needs: the <see cref="PlayerContext"/> the world runs them
/// through, the newest sequence id, the mining progress, the edit rate limiter and the attack pace.
/// The server persists it per player (<see cref="ServerPlayerSaves"/>) and the world's drop pickup
/// credits it directly (<see cref="World.ServerPlayers"/>). GL-free; main thread only, like the
/// rest of the server's state.
/// </summary>
public sealed class ServerPlayer
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

    /// <summary>Kept and persisted for the player; nothing on the server damages it yet.</summary>
    public int Health { get; set; } = PlayerHealth.MaxHealth;

    /// <summary>The accepted camera eye position.</summary>
    public Vector3 Position { get; set; }

    public float Yaw { get; set; }

    public float Pitch { get; set; }

    /// <summary>The state flags the client last reported, rebroadcast in the player list.</summary>
    public byte Flags { get; set; }

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
    /// Applies a saved player file: the pose when it has one, the inventory, the health, and the
    /// modes only as far as <paramref name="allowCreative"/> lets this server grant them. Returns
    /// whether a position was restored, so the caller can spawn the player where they left.
    /// </summary>
    public bool Restore(PlayerSaveData? saved, bool allowCreative)
    {
        if (saved == null)
            return false;
        if (saved.Inventory is { } inventory)
            Inventory.Import(inventory);
        if (saved.Health is { } health)
            Health = Math.Clamp(health, 0, PlayerHealth.MaxHealth);
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
        Health = Health,
        Inventory = Inventory.Export(),
    };
}
