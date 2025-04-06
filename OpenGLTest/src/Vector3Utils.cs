using System;
using System.Numerics;

namespace silkgltriangle;

public static class Vector3Utils
{
    public static Vector3 Floor(this Vector3 vector3) => new(MathF.Floor(vector3.X), MathF.Floor(vector3.Y), MathF.Floor(vector3.Z));
    
    public static Vector3 Ceil(this Vector3 vector3) => new(MathF.Ceiling(vector3.X), MathF.Ceiling(vector3.Y), MathF.Ceiling(vector3.Z));
    
    public static Vector3 Round(this Vector3 vector3) => new(MathF.Round(vector3.X), MathF.Round(vector3.Y), MathF.Round(vector3.Z));
    
    public static Vector3 Sign(this Vector3 vector3) => new(MathF.Sign(vector3.X), MathF.Sign(vector3.Y), MathF.Sign(vector3.Z));
}