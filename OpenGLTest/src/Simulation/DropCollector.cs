using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Simulation;

/// <summary>
/// One player a drop may fly to on a multiplayer server: where their eye is and what their own
/// inventory still has room for (null takes everything). See
/// <see cref="DropManager.Update(float, System.Collections.Generic.IReadOnlyList{DropCollector}, ChunkManager)"/>.
/// </summary>
public readonly record struct DropCollector(Vector3 Eye, Func<byte, bool>? CanCollect);
