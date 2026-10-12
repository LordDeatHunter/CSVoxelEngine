using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Networking;

/// <summary>What a <see cref="WorldIntent"/> asks the server to do.</summary>
public enum NetIntentKind : byte
{
    /// <summary>Left-click began mining a cell (or switched target or held item): the server
    /// starts that player's mining progress.</summary>
    BreakStart = 1,

    /// <summary>The client's mining reached the cell's hardness: the server breaks it only when its
    /// own progress, paced by the held item, got there too (creative breaks at once).</summary>
    BreakComplete = 2,

    /// <summary>Right-click placed the selected block against a face.</summary>
    Place = 3,

    /// <summary>Right-click was claimed by a station or by the held item's own use: the server runs
    /// the same right-click chain for the player.</summary>
    Use = 4,

    /// <summary>A charged throw was released.</summary>
    Throw = 5,

    /// <summary>A punch or a slice swing landed its hit.</summary>
    Attack = 6,

    /// <summary>A dead player asks to come back: the only intent the server accepts from a dead
    /// player, and the only one it refuses from a living one. It carries no cell.</summary>
    Respawn = 7,
}

/// <summary>Which left-click attack an <see cref="NetIntentKind.Attack"/> intent carries.</summary>
public enum NetAttackKind : byte
{
    Punch = 0,
    Slice = 1,
    /// <summary>The buffered return swing of a slice combo, at half power.</summary>
    SliceReturn = 2,
}

/// <summary>
/// One world intent a multiplayer client sends (<see cref="NetMessage.WorldIntent"/>): what the
/// player did, never its result. Every intent carries the eye and the unit aim it was made from;
/// the server checks the eye against the player's authoritative position and replays the action
/// for that player with the singleplayer rules (<see cref="World"/> through a
/// <see cref="PlayerContext"/>). Which fields a kind reads:
/// <list type="bullet">
/// <item><see cref="NetIntentKind.BreakStart"/> / <see cref="NetIntentKind.BreakComplete"/>: the
/// targeted cell.</item>
/// <item><see cref="NetIntentKind.Place"/>: the hit cell and the face normal placed against.</item>
/// <item><see cref="NetIntentKind.Use"/>: the cell the client aimed at, when it aimed at one
/// (<see cref="HasCell"/>); only used to scope the correction the server sends back.</item>
/// <item><see cref="NetIntentKind.Throw"/>: the charge, 0..1.</item>
/// <item><see cref="NetIntentKind.Attack"/>: the attack kind.</item>
/// <item><see cref="NetIntentKind.Respawn"/>: nothing beyond the eye and aim.</item>
/// </list>
/// GL-free and content-free.
/// </summary>
public readonly record struct WorldIntent(
    NetIntentKind Kind,
    Vector3 Eye,
    Vector3 Direction,
    int X = 0,
    int Y = 0,
    int Z = 0,
    bool HasCell = false,
    sbyte NormalX = 0,
    sbyte NormalY = 0,
    sbyte NormalZ = 0,
    float Charge = 0f,
    NetAttackKind Attack = NetAttackKind.Punch)
{
    /// <summary>The cell the intent names, as a floored world position.</summary>
    public Vector3 Cell => new(X, Y, Z);

    /// <summary>The face normal a placement names.</summary>
    public Vector3 Normal => new(NormalX, NormalY, NormalZ);

    /// <summary>Whether the intent can change cells, so the server answers it with the cells'
    /// authoritative state.</summary>
    public bool EditsCells => Kind is NetIntentKind.BreakComplete or NetIntentKind.Place or NetIntentKind.Use;

    /// <summary>Whether the client's prediction of the intent changes its inventory, so the server
    /// answers it with an inventory snapshot.</summary>
    public bool PredictsInventory => Kind is NetIntentKind.Place or NetIntentKind.Use or NetIntentKind.Throw;

    /// <summary>Whether the intent may only be sent at the edit rate
    /// (<see cref="WorldGenerationConfig.NetEditIntentsPerSecond"/>).</summary>
    public bool IsRateLimited =>
        Kind is NetIntentKind.BreakComplete or NetIntentKind.Place or NetIntentKind.Use or NetIntentKind.Throw;

    public static WorldIntent BreakStart(Vector3 eye, Vector3 direction, Vector3 cell) =>
        new(NetIntentKind.BreakStart, eye, direction, (int)cell.X, (int)cell.Y, (int)cell.Z, HasCell: true);

    public static WorldIntent BreakComplete(Vector3 eye, Vector3 direction, Vector3 cell) =>
        new(NetIntentKind.BreakComplete, eye, direction, (int)cell.X, (int)cell.Y, (int)cell.Z, HasCell: true);

    public static WorldIntent Place(Vector3 eye, Vector3 direction, Vector3 cell, Vector3 normal) =>
        new(NetIntentKind.Place, eye, direction, (int)cell.X, (int)cell.Y, (int)cell.Z, HasCell: true,
            NormalX: (sbyte)normal.X, NormalY: (sbyte)normal.Y, NormalZ: (sbyte)normal.Z);

    public static WorldIntent Use(Vector3 eye, Vector3 direction, Vector3? cell) =>
        cell is { } c
            ? new(NetIntentKind.Use, eye, direction, (int)c.X, (int)c.Y, (int)c.Z, HasCell: true)
            : new(NetIntentKind.Use, eye, direction);

    public static WorldIntent Throw(Vector3 eye, Vector3 direction, float charge01) =>
        new(NetIntentKind.Throw, eye, direction, Charge: charge01);

    public static WorldIntent AttackWith(Vector3 eye, Vector3 direction, NetAttackKind attack) =>
        new(NetIntentKind.Attack, eye, direction, Attack: attack);

    public static WorldIntent Respawn(Vector3 eye, Vector3 direction) =>
        new(NetIntentKind.Respawn, eye, direction);
}
