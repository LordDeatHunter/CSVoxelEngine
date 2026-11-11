using System;
using System.IO;
using System.Numerics;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Posing;
using VoxelEngine.Player;

namespace VoxelEngine.Content.Models;

/// <summary>
/// The one Blockbench model every chest block is drawn with in the world
/// (<c>assets/models/block/chest.bbmodel</c>): a hollow <see cref="BasePart"/>, a <see cref="LidPart"/> hinged along
/// its back edge and the three clips its lid plays (<see cref="Open"/>, <see cref="Close"/>, <see cref="Wobble"/>).
/// Chests differ only in the skin they wear (<see cref="SkinPath"/>, a block's <c>chest.skin</c>), laid out like
/// the model's own texture. Parsed once at content load, GL-free, so the renderer, the lid animation, the server
/// and the tests share one parse; a file missing a group or a clip fails the load naming it. The model is authored
/// standing on y = 0, centered on the origin and facing north (its latch on -Z), like a facing block's model.
/// </summary>
public static class ChestModel
{
    /// <summary>The group that holds the body; the root of the rig, which the wobble rocks.</summary>
    public const string BasePart = "Base";

    /// <summary>The group that holds the lid and its latch, a child of <see cref="BasePart"/> pivoted on the hinge.</summary>
    public const string LidPart = "Lid";

    private static readonly string[] RequiredParts = [BasePart, LidPart];

    private static BbmodelData? _data;
    private static PlayerRig? _rig;
    private static AnimationClip? _open;
    private static AnimationClip? _close;
    private static AnimationClip? _wobble;

    /// <summary>Whether <see cref="Load"/> has run; false in a run whose content has no chest model to load.</summary>
    public static bool IsLoaded => _rig != null;

    /// <summary>The parsed project, for a viewer that builds its own rig from it (the item debugger).</summary>
    public static BbmodelData Data => _data ?? throw NotLoaded();

    /// <summary>The file <see cref="Data"/> was parsed from.</summary>
    public static string ModelPath { get; private set; } = "";

    /// <summary>The parsed rig: the two parts with their pivots, cubes and face UVs, and the model's own skin.</summary>
    public static PlayerRig Rig => _rig ?? throw NotLoaded();

    /// <summary>The lid swinging open; it holds its last frame, so an open chest stays open.</summary>
    public static AnimationClip Open => _open ?? throw NotLoaded();

    /// <summary>The lid dropping shut, from the open pose back to rest.</summary>
    public static AnimationClip Close => _close ?? throw NotLoaded();

    /// <summary>A locked chest's rattle: the body rocks and the lid jumps against its latch, ending at rest.</summary>
    public static AnimationClip Wobble => _wobble ?? throw NotLoaded();

    /// <summary>Where the model file lives under an assets root.</summary>
    public static string PathIn(string assetsRoot) => Path.Combine(assetsRoot, "models", "block", "chest.bbmodel");

    /// <summary>Where the skin named <paramref name="skin"/> lives under an assets root.</summary>
    public static string SkinPath(string assetsRoot, string skin) =>
        Path.Combine(assetsRoot, "textures", "chests", skin + ".png");

    /// <summary>Parses the model and checks its contract: both groups hold cubes and all three clips exist.</summary>
    internal static void Load(string assetsRoot)
    {
        var path = PathIn(assetsRoot);
        if (!File.Exists(path))
            throw new InvalidOperationException($"The chest model '{path}' does not exist.");
        var data = BbmodelLoader.Load(path, RequiredParts);
        _open = RequireClip(data, path, "open");
        _close = RequireClip(data, path, "close");
        _wobble = RequireClip(data, path, "wobble");
        _rig = PlayerRig.FromBbmodel(data);
        _data = data;
        ModelPath = path;
    }

    /// <summary>Forgets the loaded model, with the rest of the content.</summary>
    internal static void Reset()
    {
        _data = null;
        ModelPath = "";
        _rig = null;
        _open = null;
        _close = null;
        _wobble = null;
    }

    /// <summary>
    /// Where a chest in <paramref name="cell"/> stands and which way it faces: the model's origin on the middle of
    /// the cell's floor, turned from its authored north to the facing in the low two bits of
    /// <paramref name="state"/> (<see cref="HorizontalFacing"/>), exactly as the static block model is turned.
    /// </summary>
    public static Matrix4x4 Root(Vector3 cell, byte state) =>
        Matrix4x4.CreateRotationY(FacingYaw(state & 3)) * Matrix4x4.CreateTranslation(cell + new Vector3(0.5f, 0f, 0.5f));

    /// <summary>The world matrix of <paramref name="part"/> of a chest standing at <paramref name="root"/>
    /// (<see cref="Root"/>) and showing <paramref name="pose"/>.</summary>
    public static Matrix4x4 PartMatrix(string part, in Matrix4x4 root, ClipPose pose) =>
        RigPose.PartMatrix(pose, part, Rig, root);

    // The yaw that turns the authored north front (-Z) to a HorizontalFacing index: south, west, east
    private static float FacingYaw(int facing) => facing switch
    {
        1 => MathF.PI,
        2 => MathF.PI / 2f,
        3 => -MathF.PI / 2f,
        _ => 0f,
    };

    private static AnimationClip RequireClip(BbmodelData data, string path, string name)
    {
        foreach (var clip in data.Animations)
        {
            if (string.Equals(clip.Name, name, StringComparison.OrdinalIgnoreCase))
                return clip;
        }

        throw new InvalidOperationException($"The chest model '{path}' has no animation named '{name}'.");
    }

    private static InvalidOperationException NotLoaded() =>
        new("The chest model is read before the content is loaded.");
}
