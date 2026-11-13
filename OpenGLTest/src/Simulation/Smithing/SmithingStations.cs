using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Lod;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// The iron works of a world: its bloomeries and its anvils, owned by <see cref="World"/> as one unit so the frame,
/// the save (<c>stations.json</c>) and the station broadcast (<see cref="StationNetCodec"/>) each plumb a single
/// object. Main thread only; a client's copy is a display-only mirror that never ticks.
/// </summary>
public sealed class SmithingStations
{
    /// <summary>The bloomeries.</summary>
    public BloomeryManager Bloomeries { get; } = new();

    /// <summary>The anvils.</summary>
    public AnvilManager Anvils { get; } = new();

    /// <summary>Whether either holds anything, so an empty world sends no snapshot for them.</summary>
    public bool HasAny => Bloomeries.HasAny || Anvils.HasAny;

    /// <summary>
    /// Forges the recipe making <paramref name="resultId"/> from the stack on the anvil at <paramref name="cell"/> into
    /// <paramref name="inventory"/>: singleplayer's pick from the anvil's recipe list, a server replaying a client's
    /// <see cref="NetIntentKind.Forge"/> intent, and that client's prediction. Refused when the recipe is not offered
    /// for the stack (<see cref="AnvilManager.TryForge"/>) or its result does not fit the inventory, so no metal is
    /// ever lost. The caller judges the hammer in hand and the reach.
    /// </summary>
    public bool Forge(Vector3 cell, ushort resultId, Inventory inventory)
    {
        if (ContentRegistry.ForgeRecipeForResult(resultId) is not { } wanted
            || Inventory.CategoryOf(wanted.ResultId) is not { } category
            || Inventory.CapacityOf(category) - inventory.UsedOf(category) < wanted.ResultCount)
            return false;
        if (!Anvils.TryForge(cell, resultId, out var recipe) || recipe == null) return false;
        inventory.Add(recipe.ResultId, recipe.ResultCount);
        inventory.NoteCrafted(recipe.ResultId, recipe.ResultCount);
        return true;
    }

    /// <summary>Runs both for one frame.</summary>
    public void Update(float deltaTime, ChunkManager chunks, DropManager drops, LodManager? lod)
    {
        Bloomeries.Update(deltaTime, chunks, drops, lod);
        Anvils.Update(deltaTime, chunks, drops);
    }

    /// <summary>Writes both (bloomeries, then anvils).</summary>
    internal void WriteNet(PacketWriter writer)
    {
        Bloomeries.WriteNet(writer);
        Anvils.WriteNet(writer);
    }

    /// <summary>Replaces both with a snapshot written by <see cref="WriteNet"/>.</summary>
    internal void ReadNet(PacketReader reader)
    {
        Bloomeries.ReadNet(reader);
        Anvils.ReadNet(reader);
    }

    /// <summary>Captures both into <paramref name="data"/>.</summary>
    internal void Capture(StationSaveData data)
    {
        data.Bloomeries = Bloomeries.Capture();
        data.Anvils = Anvils.Capture();
    }

    /// <summary>Restores both from <paramref name="saved"/>; returns how many entries were dropped.</summary>
    internal int Restore(StationSaveData saved) => Bloomeries.Restore(saved.Bloomeries) + Anvils.Restore(saved.Anvils);
}
