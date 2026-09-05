using System.Numerics;

namespace silkgltriangle;

/// <summary>One sphere of a worm-carved tunnel, in world block coordinates.</summary>
public readonly record struct CaveNode(Vector3 Center, float Radius);
