using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Commands;

namespace VoxelEngine.Tests.Support;

/// <summary>A command target that records what commands did to it, for the headless command tests. It may be
/// creative unless <see cref="MayBeCreative"/> says otherwise.</summary>
public sealed class RecordingCommandPlayer(string name, Vector3 feet) : ICommandPlayer
{
    public string Name { get; } = name;

    public Vector3 Feet { get; private set; } = feet;

    public bool IsCreative { get; private set; }

    public bool IsFlying { get; private set; }

    public bool IsDead { get; private set; }

    public bool MayBeCreative { get; init; } = true;

    /// <summary>Every grant, in order.</summary>
    public List<(ushort Id, int Count)> Given { get; } = [];

    public void Teleport(Vector3 feet) => Feet = feet;

    public void Give(ushort id, int count) => Given.Add((id, count));

    public bool TrySetModes(bool creative, bool flying, out string refusal)
    {
        if (creative && !MayBeCreative)
        {
            refusal = $"{Name} may not be creative";
            return false;
        }
        IsCreative = creative;
        IsFlying = creative && flying;
        refusal = "";
        return true;
    }

    public bool Kill()
    {
        if (IsDead) return false;
        IsDead = true;
        return true;
    }
}
