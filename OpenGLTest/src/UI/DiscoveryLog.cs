using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The discovered items behind the "New: ..." toast, GL-free: the first time an id is credited the
/// log reports it, so the HUD can name the find; a restored save's inventory is marked known
/// without reporting (see the world's load path), so loading a world never toasts.
/// </summary>
public sealed class DiscoveryLog
{
    private readonly HashSet<byte> _seen = [];

    /// <summary>Whether the id has been discovered.</summary>
    public bool Knows(byte id) => _seen.Contains(id);

    /// <summary>Marks an id known without reporting it (a restored inventory).</summary>
    public void MarkKnown(byte id) => _seen.Add(id);

    /// <summary>
    /// Records a credited id: true the first time it is seen (the caller shows the toast), false
    /// for a repeat. Air is never a find.
    /// </summary>
    public bool Discover(byte id) => id != 0 && _seen.Add(id);

    /// <summary>Forgets everything: the next world starts with nothing discovered.</summary>
    public void Reset() => _seen.Clear();
}
