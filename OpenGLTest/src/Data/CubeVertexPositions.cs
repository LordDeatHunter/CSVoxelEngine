using System.Numerics;

namespace VoxelEngine.Data;

public static class CubeVertexPositions
{
    public static readonly Vector3 BackLeftBottom = new(0, 0, 0);
    public static readonly Vector3 BackLeftTop = new(0, 1, 0);
    public static readonly Vector3 BackRightBottom = new(1, 0, 0);
    public static readonly Vector3 BackRightTop = new(1, 1, 0);
    public static readonly Vector3 FrontLeftBottom = new(0, 0, 1);
    public static readonly Vector3 FrontLeftTop = new(0, 1, 1);
    public static readonly Vector3 FrontRightBottom = new(1, 0, 1);
    public static readonly Vector3 FrontRightTop = new(1, 1, 1);
}