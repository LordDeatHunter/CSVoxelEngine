using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex(Vector3 position, byte r, byte g, byte b, byte a, Vector2 uv, float sun = 1f, float block = 0f, Vector3? normal = null, float sway = 0f)
{
    public Vector3 Position = position;
    public uint PackedColor = (uint)(r | (g << 8) | (b << 16) | (a << 24));
    public Vector2 UV = uv;

    /// <summary>Skylight level 0..1, flood-filled from sky-exposed cells at mesh time.</summary>
    public float Sun = sun;

    /// <summary>Lamp light level 0..1, flood-filled from emissive blocks at mesh time.</summary>
    public float Block = block;

    /// <summary>Face normal, so the shader can do dynamic sun diffuse as the sun orbits.</summary>
    public Vector3 Normal = normal ?? Vector3.UnitY;

    /// <summary>Wind weight for the leaf-wave shader (1 on canopy leaves, 0 elsewhere).
    /// Chunk meshes set it; standalone cubes (drops, icons, mining pops) stay planted at 0.</summary>
    public float Sway = sway;

    public static readonly uint Size = (uint)Marshal.SizeOf<Vertex>();
}
