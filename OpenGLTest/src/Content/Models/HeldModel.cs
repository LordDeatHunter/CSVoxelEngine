using System;
using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.Content.Models;

/// <summary>
/// The 3D shape an item is drawn as in the world (in a hand, thrown, dropped) while its sprite stays the
/// inventory icon: the cuboids of the Blockbench project its JSON <c>heldModel</c> names, with that
/// project's embedded skin. GL-free; <c>ItemMeshCache</c> builds the mesh.
///
/// The model stands in for the sprite in the sprite's own space, so everything that poses a sprite (the
/// item's <c>display</c>, a mob's held transform, a point-first throw) poses the model unchanged. It is
/// authored upright in Blockbench, tip toward +Y and centered on the origin, at 16 units to the block (one
/// block being the sprite's side), and <see cref="ToSpriteSpace"/> lays its +Y axis on the sprite's
/// (1, 1) diagonal, where every blade sprite has its tip.
/// </summary>
public sealed class HeldModel
{
    /// <summary>The turn from the authored model (tip toward +Y) to the sprite's space (tip toward +X+Y).</summary>
    public static readonly Matrix4x4 ToSpriteSpace = Matrix4x4.CreateRotationZ(-MathF.PI / 4f);

    /// <summary>Every cuboid of the project, in model space (blocks), as one pivotless part.</summary>
    public PlayerRig.RigPart Part { get; } = new();

    /// <summary>Top-first RGBA skin the cuboid faces sample.</summary>
    public byte[] TexturePixels { get; private init; } = [];
    public int TextureWidth { get; private init; }
    public int TextureHeight { get; private init; }

    /// <summary>
    /// Reads the project at <paramref name="path"/> (relative to the working directory, like an item's
    /// texture). It needs at least one cube and an embedded or relative skin; groups only organize the
    /// file, their cubes are flattened into one rigid shape.
    /// </summary>
    public static HeldModel Load(string path)
    {
        var data = BbmodelLoader.Load(path, []);
        if (data.TexturePixels == null)
            throw new InvalidOperationException($"Model '{path}' has no texture: a held model needs its skin.");

        var model = new HeldModel
        {
            TexturePixels = data.TexturePixels,
            TextureWidth = data.TextureWidth,
            TextureHeight = data.TextureHeight,
        };
        foreach (var part in data.Parts.Values)
            foreach (var cube in part.Cubes)
                model.Part.Cubes.Add(new PlayerRig.RigCube
                {
                    Min = cube.Min + part.Pivot,
                    Max = cube.Max + part.Pivot,
                    Faces = cube.Faces,
                });
        if (model.Part.Cubes.Count == 0)
            throw new InvalidOperationException($"Model '{path}' has no cubes.");
        return model;
    }
}
