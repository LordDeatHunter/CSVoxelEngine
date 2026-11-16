using System;
using System.Numerics;
using VoxelEngine.UI.Hud;

namespace VoxelEngine.Commands;

/// <summary>
/// Who runs a command line: its name and rights, where it stands (<see cref="Feet"/>, the base of <c>~</c>
/// coordinates), the player it acts on by default (<see cref="Self"/>; null on a client that only completes lines),
/// the host the effects go to, and where its answers go (<see cref="Reply"/>, a system line to that source only).
/// </summary>
public sealed record CommandSource(
    string Name,
    CommandPermission Permission,
    Vector3 Feet,
    ICommandPlayer? Self,
    ICommandHost Host,
    Action<string, ChatLink?> Reply)
{
    /// <summary>Whether this source may use a node that needs <paramref name="required"/>.</summary>
    public bool Allows(CommandPermission required) => Permission >= required;

    /// <summary>Whether <paramref name="root"/> exists for this source's host: a multiplayer-only command needs an
    /// operator list (<see cref="ICommandHost.Operators"/>).</summary>
    public bool Has(LiteralNode root) => !root.MultiplayerOnly || Host.Operators != null;

    /// <summary>Whether this source may run <paramref name="root"/>: it exists here and its rights allow it.</summary>
    public bool MayRun(LiteralNode root) => Has(root) && Allows(root.Permission);
}
