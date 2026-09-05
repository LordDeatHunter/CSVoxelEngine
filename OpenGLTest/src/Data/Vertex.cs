using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex(Vector3 position, byte r, byte g, byte b, byte a, Vector2 uv, float sun = 1f, float block = 0f, Vector3? normal = null)
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

    public static readonly uint Size = (uint)Marshal.SizeOf<Vertex>();
}
