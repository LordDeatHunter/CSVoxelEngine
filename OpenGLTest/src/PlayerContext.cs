using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Who an interaction runs for: the eye it is aimed from, the direction it is aimed along, the
/// inventory it spends from and credits, and whether that player is creative. Every
/// <see cref="World"/> path that acts for a player (break, place, the right-click chain, throws,
/// punches and slices, the item-use tail) reads this instead of the world's own camera, so a
/// server runs exactly the singleplayer rules for each connected player
/// (<see cref="ServerPlayer.Context"/>), and singleplayer builds one from its camera.
/// <para>
/// With a <see cref="Camera"/> (the local player) raycasts follow the camera's own aiming rules,
/// third-person shoulder validation included; without one they are the first-person ray from
/// <see cref="Eye"/> along <see cref="Forward"/>, which is the same ray a client's camera
/// validated its hit with. GL-free; main thread only.
/// </para>
/// </summary>
public sealed class PlayerContext(Inventory inventory)
{
    /// <summary>What this player carries: interactions spend from and credit it.</summary>
    public Inventory Inventory { get; } = inventory;

    /// <summary>The player's name, for logs; empty for the local player.</summary>
    public string Name { get; init; } = "";

    /// <summary>The player's eye: where raycasts, throws and attacks leave from.</summary>
    public Vector3 Eye { get; set; }

    /// <summary>The unit aim the raycasts fire along (the head ray toward the target).</summary>
    public Vector3 Forward { get; set; } = -Vector3.UnitZ;

    /// <summary>The unit look direction: what a throw, a punch cone and a structure's facing follow.</summary>
    public Vector3 ViewDirection { get; set; } = -Vector3.UnitZ;

    /// <summary>Creative: placing and using spend nothing, breaking drops nothing.</summary>
    public bool IsCreative { get; set; }

    /// <summary>
    /// False once the player has left: anything bound to it (a pour from its crucible) pauses
    /// instead of running against an inventory nobody is holding.
    /// </summary>
    public bool Present { get; set; } = true;

    /// <summary>The local player's camera, whose raycasts follow its third-person rules; null for a
    /// remote player, whose raycasts are the first-person ray from <see cref="Eye"/>.</summary>
    public Camera? Camera { get; init; }

    /// <summary>
    /// The first cell this player aims at within <paramref name="maxDistance"/> (fluid sources only
    /// when <paramref name="includeFluids"/>), and the face the ray entered it through.
    /// </summary>
    public bool Raycast(float maxDistance, ChunkManager chunks, out Vector3 hit, out Vector3 normal,
        bool includeFluids = false)
    {
        if (Camera != null)
            return Camera.Raycast(maxDistance, chunks, out hit, out normal, includeFluids);
        return BlockRaycaster.Raycast(Eye, Forward, maxDistance, 0f, stopAtRange: false, chunks,
            out hit, out normal, out _, out _, includeFluids);
    }

    /// <summary>
    /// Whether a solid block in <paramref name="cell"/> would overlap the body standing under
    /// <paramref name="eye"/>. Touching a face exactly does not count, so a block under the feet or
    /// beside the body stays legal.
    /// </summary>
    public static bool BodyOverlapsCell(Vector3 eye, Vector3 cell)
    {
        var min = new Vector3(eye.X - PlayerPhysics.PlayerRadius, eye.Y - Camera.EyeHeight,
            eye.Z - PlayerPhysics.PlayerRadius);
        var max = new Vector3(eye.X + PlayerPhysics.PlayerRadius,
            eye.Y - Camera.EyeHeight + PlayerPhysics.PlayerHeight, eye.Z + PlayerPhysics.PlayerRadius);
        var cellMin = cell.Floor();
        var cellMax = cellMin + Vector3.One;
        return cellMin.X < max.X && cellMax.X > min.X
            && cellMin.Y < max.Y && cellMax.Y > min.Y
            && cellMin.Z < max.Z && cellMax.Z > min.Z;
    }
}
