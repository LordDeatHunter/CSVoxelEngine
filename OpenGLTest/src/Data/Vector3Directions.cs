using System.Numerics;

namespace silkgltriangle;

public static class Vector3Directions
{
    public static readonly Vector3 Up = new(0, 1, 0);
    public static readonly Vector3 Down = new(0, -1, 0);
    public static readonly Vector3 Left = new(-1, 0, 0);
    public static readonly Vector3 Right = new(1, 0, 0);
    public static readonly Vector3 Forward = new(0, 0, 1);
    public static readonly Vector3 Backward = new(0, 0, -1);
    public static readonly Vector3 UpRight = new(1, 1, 0);
    public static readonly Vector3 UpLeft = new(-1, 1, 0);
    public static readonly Vector3 DownRight = new(1, -1, 0);
    public static readonly Vector3 DownLeft = new(-1, -1, 0);
    public static readonly Vector3 UpForward = new(0, 1, 1);
    public static readonly Vector3 UpBackward = new(0, 1, -1);
    public static readonly Vector3 DownForward = new(0, -1, 1);
    public static readonly Vector3 DownBackward = new(0, -1, -1);
    public static readonly Vector3 LeftForward = new(-1, 0, 1);
    public static readonly Vector3 LeftBackward = new(-1, 0, -1);
    public static readonly Vector3 RightForward = new(1, 0, 1);
    public static readonly Vector3 RightBackward = new(1, 0, -1);
    public static readonly Vector3 UpLeftForward = new(-1, 1, 1);
    public static readonly Vector3 UpLeftBackward = new(-1, 1, -1);
    public static readonly Vector3 UpRightForward = new(1, 1, 1);
    public static readonly Vector3 UpRightBackward = new(1, 1, -1);
    public static readonly Vector3 DownLeftForward = new(-1, -1, 1);
    public static readonly Vector3 DownLeftBackward = new(-1, -1, -1);
    public static readonly Vector3 DownRightForward = new(1, -1, 1);
    public static readonly Vector3 DownRightBackward = new(1, -1, -1);

    public static readonly Vector3[] CardinalDirections =
    [
        Up,
        Down,
        Left,
        Right,
        Forward,
        Backward
    ];

    public static readonly Vector3[] EdgeDirections =
    [
        UpRight,
        UpLeft,
        DownRight,
        DownLeft,
        UpForward,
        UpBackward,
        DownForward,
        DownBackward,
        LeftForward,
        LeftBackward,
        RightForward,
        RightBackward
    ];

    public static readonly Vector3[] DiagonalDirections =
    [
        UpLeftForward,
        UpLeftBackward,
        UpRightForward,
        UpRightBackward,
        DownLeftForward,
        DownLeftBackward,
        DownRightForward,
        DownRightBackward
    ];
    
    public static readonly Vector3[] AllDirections =
    [
        Up,
        Down,
        Left,
        Right,
        Forward,
        Backward,
        UpRight,
        UpLeft,
        DownRight,
        DownLeft,
        UpForward,
        UpBackward,
        DownForward,
        DownBackward,
        LeftForward,
        LeftBackward,
        RightForward,
        RightBackward,
        UpLeftForward,
        UpLeftBackward,
        UpRightForward,
        UpRightBackward,
        DownLeftForward,
        DownLeftBackward,
        DownRightForward,
        DownRightBackward
    ];
}