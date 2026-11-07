using System;
using System.Collections.Generic;
using VoxelEngine.Simulation;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>
/// Crucible component: what one vessel holds, one <see cref="CrucibleMetal"/> per molten material
/// (molten and pending ml each). A plain kiln keeps a vessel to one metal and an alloy kiln lets
/// the parts of an alloy share it (<see cref="KilnManager.CanAccept"/>, <see cref="AlloyRules"/>);
/// the component itself only keeps the books. The capacity is the crucible item's, counted over
/// every metal together (<see cref="TotalMl"/>).
///
/// <para>
/// Immutable and value-like: the metals are kept sorted by material id with no empty entry, so an
/// emptied metal is simply gone (an emptied vessel forgets what it held) and two components with
/// the same contents are equal. Every change returns a new component (<see cref="With"/>).
/// </para>
///
/// <para>
/// <see cref="PendingInputId"/> is the block/item id of the <em>last</em> melt input charged in
/// (0 = none recorded). It is display-only — the kiln's render pass draws that item as the lump
/// waiting over the pool — and only meaningful while <see cref="PendingMl"/> is above zero; it
/// takes no part in acceptance, capacity, melting or equality of the metals.
/// </para>
/// </summary>
public readonly struct CrucibleComponent : IEquatable<CrucibleComponent>
{
    /// <summary>Most distinct metals one vessel may hold; the save and wire readers refuse more.</summary>
    public const int MaxMetals = 8;

    // Sorted by material id, every entry non-empty, never mutated after construction
    private readonly CrucibleMetal[]? _metals;

    /// <summary>A vessel holding one metal, or nothing when both amounts are 0 (then
    /// <paramref name="materialId"/> may be 0).</summary>
    public CrucibleComponent(byte materialId, int moltenMl, int pendingMl, byte pendingInputId = 0)
        : this([new CrucibleMetal(materialId, moltenMl, pendingMl)], pendingInputId)
    {
    }

    /// <summary>A vessel holding these metals: entries of one material are summed and empty ones
    /// dropped. A negative amount, or an amount of material 0, throws.</summary>
    public CrucibleComponent(IEnumerable<CrucibleMetal> metals, byte pendingInputId = 0)
    {
        PendingInputId = pendingInputId;
        var merged = new SortedDictionary<byte, CrucibleMetal>();
        foreach (var metal in metals)
        {
            if (metal.MoltenMl < 0 || metal.PendingMl < 0)
                throw new ArgumentException($"negative crucible amount for material {metal.MaterialId}");
            if (metal.TotalMl == 0) continue;
            if (metal.MaterialId == 0)
                throw new ArgumentException("crucible metal without a material");
            merged[metal.MaterialId] = merged.TryGetValue(metal.MaterialId, out var held)
                ? held with { MoltenMl = held.MoltenMl + metal.MoltenMl, PendingMl = held.PendingMl + metal.PendingMl }
                : metal;
        }
        _metals = merged.Count == 0 ? null : [.. merged.Values];
    }

    /// <summary>The last melt input charged in, for the drawn lump; 0 when none was recorded.</summary>
    public byte PendingInputId { get; init; }

    /// <summary>Every metal held, sorted by material id, none empty.</summary>
    public IReadOnlyList<CrucibleMetal> Metals => _metals ?? [];

    /// <summary>Molten ml over every metal.</summary>
    public int MoltenMl => Sum(true, false);

    /// <summary>Pending ml over every metal.</summary>
    public int PendingMl => Sum(false, true);

    /// <summary>Everything in the vessel, for the capacity check.</summary>
    public int TotalMl => Sum(true, true);

    /// <summary>Whether the vessel holds nothing.</summary>
    public bool IsEmpty => _metals == null;

    /// <summary>Whether any of <paramref name="materialId"/> is in the vessel, molten or pending.</summary>
    public bool Holds(byte materialId) => Find(materialId) is not null;

    /// <summary>Molten ml of one metal.</summary>
    public int MoltenOf(byte materialId) => Find(materialId)?.MoltenMl ?? 0;

    /// <summary>Pending ml of one metal.</summary>
    public int PendingOf(byte materialId) => Find(materialId)?.PendingMl ?? 0;

    /// <summary>The metal with the most molten ml (the lowest id on a tie), or 0 when nothing is
    /// molten: what a pour with no other preference takes and what the drawn pool is tinted as.</summary>
    public byte MostMoltenMaterial
    {
        get
        {
            byte best = 0;
            var bestMl = 0;
            foreach (var metal in Metals)
            {
                if (metal.MoltenMl <= bestMl) continue;
                best = metal.MaterialId;
                bestMl = metal.MoltenMl;
            }
            return best;
        }
    }

    /// <summary>A copy with one metal's molten and pending ml moved by the deltas, keeping
    /// <see cref="PendingInputId"/>. A result below zero throws.</summary>
    public CrucibleComponent With(byte materialId, int moltenDelta, int pendingDelta)
    {
        var metals = new List<CrucibleMetal>(Metals.Count + 1);
        var found = false;
        foreach (var metal in Metals)
        {
            if (metal.MaterialId != materialId)
            {
                metals.Add(metal);
                continue;
            }
            metals.Add(metal with { MoltenMl = metal.MoltenMl + moltenDelta, PendingMl = metal.PendingMl + pendingDelta });
            found = true;
        }
        if (!found) metals.Add(new CrucibleMetal(materialId, moltenDelta, pendingDelta));
        return new CrucibleComponent(metals, PendingInputId);
    }

    public bool Equals(CrucibleComponent other)
    {
        if (PendingInputId != other.PendingInputId) return false;
        var a = Metals;
        var b = other.Metals;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CrucibleComponent other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PendingInputId);
        foreach (var metal in Metals) hash.Add(metal);
        return hash.ToHashCode();
    }

    public static bool operator ==(CrucibleComponent left, CrucibleComponent right) => left.Equals(right);

    public static bool operator !=(CrucibleComponent left, CrucibleComponent right) => !left.Equals(right);

    public override string ToString() =>
        $"Crucible[{string.Join(", ", Metals)}; input {PendingInputId}]";

    private CrucibleMetal? Find(byte materialId)
    {
        foreach (var metal in Metals)
            if (metal.MaterialId == materialId) return metal;
        return null;
    }

    private int Sum(bool molten, bool pending)
    {
        var total = 0;
        foreach (var metal in Metals)
            total += (molten ? metal.MoltenMl : 0) + (pending ? metal.PendingMl : 0);
        return total;
    }
}
