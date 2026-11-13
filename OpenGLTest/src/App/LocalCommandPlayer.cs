using System;
using System.Numerics;
using VoxelEngine.Commands;
using VoxelEngine.Player;

namespace VoxelEngine.App;

/// <summary>The singleplayer player as commands see it: the camera, the world's inventory and health. Main thread
/// only.</summary>
internal sealed class LocalCommandPlayer(World world, Func<string> name) : ICommandPlayer
{
    public string Name => name();

    public Vector3 Feet => FeetOf(world.Camera);

    /// <summary>Where <paramref name="camera"/>'s player stands: the eye one eye height down.</summary>
    internal static Vector3 FeetOf(Camera camera) => camera.Position - new Vector3(0f, Camera.EyeHeight, 0f);

    public bool IsCreative => world.Camera.IsCreative;

    public bool IsFlying => world.Camera.IsFlying;

    // Held until the cells there are in and lifted out of any solid, so a teleport never buries the player
    public void Teleport(Vector3 feet) => world.LocalPlayer.Arrival.BeginAt(feet);

    public void Give(ushort id, int count) => world.CreditItem(id, count);

    // Singleplayer's modes are the player's own to switch (a keybind does it too)
    public bool TrySetModes(bool creative, bool flying, out string refusal)
    {
        world.Camera.SetModes(creative, flying);
        refusal = "";
        return true;
    }

    public bool Kill() => world.LocalPlayer.Kill();
}
