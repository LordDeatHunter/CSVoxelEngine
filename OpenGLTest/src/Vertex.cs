using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex(Vector3 position, byte r, byte g, byte b, byte a)
{
    public Vector3 Position = position;
    public byte R = r, G = g, B = b, A = a;

    public static readonly uint Size = (uint)Marshal.SizeOf<Vertex>();
}