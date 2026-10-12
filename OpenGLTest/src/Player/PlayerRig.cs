using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Models;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;

namespace VoxelEngine.Player;

/// <summary>
/// The shape being posed: one skin texture plus a pivot and pivot-relative cuboids per part,
/// keyed by the player body-part names. Either parsed out of the Blockbench project
/// (<see cref="FromBbmodel"/>) or the built-in Steve (<see cref="BuiltIn"/>). Extracted from
/// <see cref="PlayerModel"/> so the rig/mesh building is separate from the pose animation.
/// </summary>
public sealed class PlayerRig
{
    // Classic Steve layout, in blocks. Legs 0.75 + torso 0.625 + head 0.425 = 1.8.
    public const float TotalHeight = 1.8f;
    public const float HipY = 0.75f;
    public const float TorsoWidth = 0.5f;
    public const float TorsoHeight = 0.625f;
    public const float TorsoDepth = 0.25f;
    public const float NeckY = 1.375f;
    public const float HeadSize = 0.425f;
    // One width for all four limb segments (upper arm, forearm, thigh, shin), so the
    // limbs can never drift to different thicknesses. Slimmer than Steve's 0.25.
    public const float LimbWidth = 0.2f;
    public const float UpperArmLength = 0.375f;
    public const float ForearmLength = 0.375f;
    public const float ThighLength = 0.375f;
    public const float ShinLength = 0.375f;
    public const float ShoulderY = 1.325f;
    // Derived from the widths above: arms hang flush with the torso sides, legs flush
    // with each other under the torso.
    public const float ShoulderX = TorsoWidth / 2f + LimbWidth / 2f;
    public const float HipX = LimbWidth / 2f;

    // Palette swatches (see PalettePixels): solid Blockbench-style colors behind one UV map.
    public const int Skin = 0;
    public const int Shirt = 1;
    public const int Pants = 2;
    public const int Hair = 3;
    public const int Shoe = 4;
    public const int PaletteCols = 4;
    public const int PaletteRows = 2;

    public byte[] TexturePixels = [];
    public int TextureWidth;
    public int TextureHeight;
    public readonly Dictionary<string, RigPart> Parts = new(StringComparer.Ordinal);

    public sealed class RigPart
    {
        public Vector3 Pivot;
        /// <summary>Enclosing part's name from the Blockbench outliner, null at the top level.
        /// The player's own posing ignores it; <see cref="EntityAnimator"/> walks it.</summary>
        public string? Parent;
        public readonly List<RigCube> Cubes = new();
    }

    public sealed class RigCube
    {
        public Vector3 Min, Max;
        public UvRect?[] Faces = new UvRect?[6];
    }

    /// <summary>Steve with two-segment limbs, matching the historical hardcoded model.</summary>
    public static PlayerRig BuiltIn()
    {
        var rig = new PlayerRig
        {
            TexturePixels = PalettePixels(),
            TextureWidth = PaletteCols,
            TextureHeight = PaletteRows,
        };

        void Add(string name, Vector3 pivot, Vector3 min, Vector3 max, int[] swatches)
        {
            var part = new RigPart { Pivot = pivot };
            var faces = new UvRect?[6];
            for (var f = 0; f < 6; f++)
                faces[f] = Cell(swatches[f]);
            part.Cubes.Add(new RigCube { Min = min, Max = max, Faces = faces });
            rig.Parts[name] = part;
        }

        var hw = LimbWidth * 0.5f;
        Add("Torso", new Vector3(0f, HipY, 0f),
            new Vector3(-TorsoWidth * 0.5f, 0f, -TorsoDepth * 0.5f),
            new Vector3(TorsoWidth * 0.5f, TorsoHeight, TorsoDepth * 0.5f), TorsoSwatches);
        var hs = HeadSize * 0.5f;
        Add("Head", new Vector3(0f, NeckY, 0f),
            new Vector3(-hs, 0f, -hs), new Vector3(hs, HeadSize, hs), HeadSwatches);
        foreach (var side in new[] { ("L", -1f), ("R", 1f) })
        {
            var ax = ShoulderX * side.Item2;
            Add("UpperArm" + side.Item1, new Vector3(ax, ShoulderY, 0f),
                new Vector3(-hw, -UpperArmLength, -hw), new Vector3(hw, 0f, hw), UpperArmSwatches);
            Add("Forearm" + side.Item1, new Vector3(ax, ShoulderY - UpperArmLength, 0f),
                new Vector3(-hw, -ForearmLength, -hw), new Vector3(hw, 0f, hw), ForearmSwatches);
            var hx = HipX * side.Item2;
            Add("Thigh" + side.Item1, new Vector3(hx, HipY, 0f),
                new Vector3(-hw, -ThighLength, -hw), new Vector3(hw, 0f, hw), ThighSwatches);
            Add("Shin" + side.Item1, new Vector3(hx, HipY - ThighLength, 0f),
                new Vector3(-hw, -ShinLength, -hw), new Vector3(hw, 0f, hw), ShinSwatches);
        }
        return rig;
    }

    /// <summary>Maps parsed file data onto the rig; a file without a texture keeps the palette.</summary>
    public static PlayerRig FromBbmodel(BbmodelData data)
    {
        var rig = new PlayerRig();
        if (data.TexturePixels != null)
        {
            rig.TexturePixels = data.TexturePixels;
            rig.TextureWidth = data.TextureWidth;
            rig.TextureHeight = data.TextureHeight;
        }
        else
        {
            rig.TexturePixels = PalettePixels();
            rig.TextureWidth = PaletteCols;
            rig.TextureHeight = PaletteRows;
        }

        foreach (var (name, part) in data.Parts)
        {
            var rigPart = new RigPart { Pivot = part.Pivot, Parent = part.Parent };
            foreach (var cube in part.Cubes)
                rigPart.Cubes.Add(new RigCube { Min = cube.Min, Max = cube.Max, Faces = cube.Faces });
            rig.Parts[name] = rigPart;
        }
        return rig;
    }

    private static UvRect Cell(int swatch)
    {
        var col = swatch % PaletteCols;
        var row = swatch / PaletteCols;
        return new UvRect(new Vector2(col / (float)PaletteCols, row / (float)PaletteRows),
            new Vector2((col + 1) / (float)PaletteCols, (row + 1) / (float)PaletteRows));
    }

    /// <summary>
    /// The 4x2 solid-color palette texture (top-first RGBA): skin, shirt, pants, hair,
    /// shoe, then white spares. Every cuboid face UVs to one swatch center, so the model
    /// is UV-mapped Blockbench-style with zero image assets to author.
    /// </summary>
    public static byte[] PalettePixels()
    {
        // Row 0 (top): skin, shirt, pants, hair. Row 1: shoe, white, white, white.
        (byte r, byte g, byte b)[] swatches =
        [
            (201, 149, 108), // skin
            (0, 170, 170),   // shirt (Steve cyan)
            (58, 58, 210),   // pants (Steve blue)
            (74, 47, 24),    // hair
            (90, 90, 90),    // shoe
            (255, 255, 255),
            (255, 255, 255),
            (255, 255, 255),
        ];
        var pixels = new byte[PaletteCols * PaletteRows * 4];
        for (var i = 0; i < swatches.Length; i++)
        {
            pixels[i * 4 + 0] = swatches[i].r;
            pixels[i * 4 + 1] = swatches[i].g;
            pixels[i * 4 + 2] = swatches[i].b;
            pixels[i * 4 + 3] = 255;
        }
        return pixels;
    }

    /// <summary>UV center of a palette swatch (v = 0 is the top row, like TextureLoader).</summary>
    public static Vector2 SwatchUv(int swatch) =>
        new(((swatch % PaletteCols) + 0.5f) / PaletteCols, ((swatch / PaletteCols) + 0.5f) / PaletteRows);

    // Face order is Chunk.CubeFaces: -Z (front, the face the model looks through), +Z, -X,
    // +X, +Y, -Y. Head wears hair everywhere but the face; the torso's collar and waistband
    // peek skin/pants; forearms are bare skin under shirt sleeves; shins end in shoes.
    private static readonly int[] HeadSwatches = [Skin, Hair, Hair, Hair, Hair, Skin];
    private static readonly int[] TorsoSwatches = [Shirt, Shirt, Shirt, Shirt, Skin, Pants];
    private static readonly int[] UpperArmSwatches = [Shirt, Shirt, Shirt, Shirt, Shirt, Shirt];
    private static readonly int[] ForearmSwatches = [Skin, Skin, Skin, Skin, Shirt, Skin];
    private static readonly int[] ThighSwatches = [Pants, Pants, Pants, Pants, Pants, Pants];
    private static readonly int[] ShinSwatches = [Pants, Pants, Pants, Pants, Pants, Shoe];

    /// <summary>
    /// One mesh per rig part: every present face of every cuboid in the shared
    /// <see cref="Vertex"/> layout, textured from the rig's skin. The vertex color stays
    /// white; asymmetric sculpts get their own left/right meshes automatically.
    /// </summary>
    public static Dictionary<string, Mesh> BuildMeshes(GL gl, PlayerRig rig)
    {
        var meshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        foreach (var (name, part) in rig.Parts)
            meshes[name] = BuildPartMesh(gl, part);
        return meshes;
    }

    private static Mesh BuildPartMesh(GL gl, PlayerRig.RigPart part)
    {
        var vertices = new List<Vertex>();
        var indices = new List<ushort>();
        Span<Vector2> corners = stackalloc Vector2[4];

        foreach (var cube in part.Cubes)
        {
            var size = cube.Max - cube.Min;
            for (var face = 0; face < 6; face++)
            {
                // A face omitted in the file leaves a hole (open reinforced edges, no cap).
                if (cube.Faces[face] is not { } rect) continue;
                rect.CornersTo(corners);

                var positions = Chunk.CubeFaces[face].face;
                var normal = Chunk.CubeFaces[face].normal;
                var quadBase = (ushort)vertices.Count;
                for (var corner = 0; corner < 4; corner++)
                {
                    var p = positions[corner];
                    vertices.Add(new Vertex(
                        cube.Min + new Vector3(p.X * size.X, p.Y * size.Y, p.Z * size.Z),
                        255, 255, 255, 255, corners[corner], 0, 1f, 0f, normal));
                }
                indices.AddRange([(ushort)quadBase, (ushort)(quadBase + 1), (ushort)(quadBase + 2),
                    quadBase, (ushort)(quadBase + 2), (ushort)(quadBase + 3)]);
            }
        }

        return new Mesh(gl,
            CollectionsMarshal.AsSpan(vertices),
            CollectionsMarshal.AsSpan(indices));
    }
}
