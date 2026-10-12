using System.Numerics;

namespace VoxelEngine.Data;

public struct Plane
{
    public Vector3 Normal;
    public float D;

    public Plane(float a, float b, float c, float d)
    {
        Normal = new Vector3(a, b, c);
        D = d;
    }

    public void Normalize()
    {
        float length = Normal.Length();
        if (length == 0) return;
        Normal /= length;
        D /= length;
    }

    public float DistanceToPoint(Vector3 point)
    {
        return Vector3.Dot(Normal, point) + D;
    }
}