using System.Numerics;

namespace VoxelEngine.Particles.Heave;

/// <summary>One ground block of a <see cref="GroundHeave"/>: the center of its cell, its block, the biome tint its copy
/// wears (white for an untinted block), when it starts to rise on the heave's clock (later the farther it stands from the
/// strike) and how high it rises.</summary>
public readonly record struct HeavedBlock(Vector3 Center, byte BlockId, Vector3 Tint, float Start, float Height);
