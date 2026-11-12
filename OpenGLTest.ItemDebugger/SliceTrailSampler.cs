using System.Numerics;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.ItemDebugger;

/// <summary>
/// Poses the previewed blade for one <see cref="PlayerPreviewState"/> step: <paramref name="blade"/> is the
/// matrix the held sprite is drawn with and <paramref name="owner"/> the swinging player's owner frame
/// (<see cref="SliceTrail.OwnerFrameAt"/>). Returns false when the previewed item leaves no trail (no
/// selection, a block, or an item whose attack is not a slice or a sword swing).
/// </summary>
public delegate bool SliceTrailSampler(PlayerModel model, out Matrix4x4 blade, out Matrix4x4 owner);
