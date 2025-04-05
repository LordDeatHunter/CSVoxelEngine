using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

[StructLayout(LayoutKind.Sequential)]
internal record struct Vertex(Vector3 Position, byte R, byte G, byte B, byte A)
{
    public const uint Size = 3 * 4 + 4;
}