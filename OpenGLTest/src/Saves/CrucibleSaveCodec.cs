using System;
using System.Collections.Generic;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Saves;

/// <summary>
/// A crucible's contents (<see cref="CrucibleComponent"/>) in its save shape
/// (<see cref="PlayerSaveData.CrucibleData"/>), by molten material and item name, never id: the
/// one mapping a carried crucible (<c>player.json</c> and the inventory snapshot) and one standing
/// in a kiln (<c>stations.json</c>) share.
/// </summary>
public static class CrucibleSaveCodec
{
    /// <summary>The save shape of <paramref name="contents"/>; a metal whose material has no name
    /// this run is left out.</summary>
    public static PlayerSaveData.CrucibleData Export(CrucibleComponent contents)
    {
        var metals = new List<PlayerSaveData.MetalData>(contents.Metals.Count);
        foreach (var metal in contents.Metals)
        {
            if (SaveNames.MaterialNameOf(metal.MaterialId) is not { } name) continue;
            metals.Add(new PlayerSaveData.MetalData
            {
                Material = name,
                MoltenMl = metal.MoltenMl,
                PendingMl = metal.PendingMl,
            });
        }
        return new PlayerSaveData.CrucibleData
        {
            Metals = metals,
            PendingInput = SaveNames.NameOf(contents.PendingInputId),
        };
    }

    /// <summary>
    /// The contents <paramref name="data"/> describes in a vessel of <paramref name="capacityMl"/>:
    /// at most <see cref="CrucibleComponent.MaxMetals"/> metals, each amount clamped to 0 and to what
    /// room the metals before it left, so a hand-edited file can never overfill the vessel. A metal
    /// whose material no longer resolves is dropped and named in <paramref name="dropped"/>.
    /// </summary>
    public static CrucibleComponent Import(PlayerSaveData.CrucibleData? data, int capacityMl, List<string> dropped)
    {
        if (data == null) return default;
        var room = Math.Max(0, capacityMl);
        var metals = new List<CrucibleMetal>();
        foreach (var metal in data.Metals ?? [])
        {
            if (metal == null || metals.Count >= CrucibleComponent.MaxMetals) continue;
            if (!SaveNames.TryResolveMaterial(metal.Material, out var material))
            {
                dropped.Add($"molten {metal.Material}");
                continue;
            }
            var molten = Math.Clamp(metal.MoltenMl, 0, room);
            var pending = Math.Clamp(metal.PendingMl, 0, room - molten);
            room -= molten + pending;
            metals.Add(new CrucibleMetal(material, molten, pending));
        }
        var pendingInput = SaveNames.TryResolve(data.PendingInput, out var input) ? input : (byte)0;
        return new CrucibleComponent(metals, pendingInput);
    }
}
