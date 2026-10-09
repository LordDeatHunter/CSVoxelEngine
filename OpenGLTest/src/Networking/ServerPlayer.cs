using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// One connected player as the server tracks it: its own <see cref="Inventory"/> (never shared with
/// another player or with the world's own), the server-owned creative and flight flags (survival by
/// default), the pose its last accepted <see cref="NetMessage.PlayerState"/> put it at and when that
/// arrived. The server persists it per player (<see cref="ServerPlayerSaves"/>) and the world's drop
/// pickup credits it directly (<see cref="World.ServerPlayers"/>). GL-free; main thread only, like
/// the rest of the server's state.
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
