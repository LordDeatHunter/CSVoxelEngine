using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// What an <see cref="Item"/> override may query and change during <see cref="Item.OnUse"/>.
/// Implemented once by <see cref="World"/>; item code never touches managers directly, so
/// overrides stay headless-testable against a stub. Queries read the live world, effects write
/// it, and the inventory tail (cooldown, durability, spending) runs through the selected slot.
/// </summary>
public interface IItemUseWorld
{
    /// <summary>Scene clock for cooldowns; matches the value items were gated against.</summary>
    double NowSeconds { get; }

    /// <summary>Creative mode: uses succeed without spending stacks.</summary>
    bool IsCreative { get; }

    /// <summary>A uniform roll in 0..1 for a use's success chance (a firestarter's spark).</summary>
    float NextUseRoll();

    /// <summary>Aims at a fluid surface (sources only, so a bucket never scoops a skirt).</summary>
    bool RaycastFluidSource(float maxDistance, out Vector3 hit);

    /// <summary>Aims at terrain, excluding fluids so a pond hit lands on its floor.</summary>
    bool RaycastSolid(float maxDistance, out Vector3 hit, out Vector3 normal);

    /// <summary>Cell contents with its fluid/state byte.</summary>
    bool TryGetCell(int x, int y, int z, out byte id, out byte state);

    /// <summary>Block id at a world position; 0 (air) for unloaded chunks.</summary>
    byte GetBlockAt(Vector3 pos);

    /// <summary>Removes a fluid cell immediately (scoop): the only fluid write that remeshes on
    /// the spot instead of on the next tick.</summary>
    bool ScoopFluidCell(Vector3 pos);

    /// <summary>Places a fluid source immediately (pour).</summary>
    bool PourFluidCell(Vector3 pos, byte fluidId);

    /// <summary>Grounds one block against a raycast hit face; state rides into the state byte
    /// (a fresh rock pile starts at 1) and <paramref name="placedCell"/> reports the cell that was
    /// filled, which is the hit cell itself when it held a stack the placement took over.</summary>
    bool PlaceBlockAtFace(Vector3 hit, Vector3 normal, byte blockId, byte state, out Vector3 placedCell);

    /// <summary>Runs after a block went down (sand topples when placed into thin air).</summary>
    void AfterPlaced(Vector3 pos);

    /// <summary>Units in a player-built pile cell (branch piles read state directly, pebbles mix
    /// in the worldgen hash).</summary>
    int PileCountFor(byte blockId, int x, int y, int z, byte state);

    /// <summary>Obsidian share of a pebble pile cell; 0 for every other pile.</summary>
    int PileSecondaryFor(byte blockId, int x, int y, int z, byte state);

    /// <summary>Writes a pile cell's unit count.</summary>
    bool SetPileCount(Vector3 pos, byte count);

    /// <summary>Writes a pebble pile cell's total and obsidian share; other piles ignore the share.</summary>
    bool SetPileComposition(Vector3 pos, byte blockId, int total, int secondary);

    /// <summary>Drops nothing check for a pour that displaces a fragile block.</summary>
    void UnmarkPlayerPlaced(int x, int y, int z);

    /// <summary>Loot pop for a block displaced by a pour (creative pours drop nothing).</summary>
    void SpawnLoot(byte blockId, Vector3 pos);

    /// <summary>Lit campfire entity at this cell.</summary>
    bool IsCampfireLit(int x, int y, int z);

    /// <summary>Lights the campfire entity; false when there is none to light.</summary>
    bool IgniteCampfire(int x, int y, int z);

    /// <summary>Whether this cell belongs to a kiln structure that is unlit and holds fuel, so a
    /// strike would light it. Any of the structure's eight cells answers for the whole.</summary>
    bool CanIgniteKiln(int x, int y, int z);

    /// <summary>Lights the kiln this cell belongs to, spending its first unit of fuel; false when
    /// there is no kiln to light or it holds none.</summary>
    bool IgniteKiln(int x, int y, int z);

    /// <summary>
    /// Turns the 2x2x2 volume of <paramref name="sourceId"/> that the clicked face belongs to
    /// into the <paramref name="anchorId"/> multiblock, with its opening pointed back at the
    /// player. The world picks the volume from its own camera (see
    /// <see cref="MultiblockPlacement.TryFindVolume"/>), so item code passes only the hit.
    /// False when no candidate volume is whole.
    /// </summary>
    bool TryConvertMultiblock(Vector3 hitCell, Vector3 hitNormal, byte sourceId, byte anchorId);

    /// <summary>Whether this block id primes into TNT instead of dropping.</summary>
    bool IsPrimedTntBlock(byte id);

    /// <summary>Whether a TNT cell touches fluid and must refuse to prime.</summary>
    bool IsCellSubmerged(int x, int y, int z);

    /// <summary>Clears the cell a primed TNT vacates.</summary>
    bool RemoveCellForPrime(Vector3 cell);

    /// <summary>Spawns the live primed cube.</summary>
    void SpawnPrimedTnt(Vector3 cell, byte blockId);

    /// <summary>Neighbours of an edit recompute their fluid levels.</summary>
    void ScheduleFluidAround(Vector3 cell);

    /// <summary>Destroyed blocks expose the buried LOD, which rebuilds below the hole.</summary>
    void MarkLodColumnEdited(int x, int y, int z);

    /// <summary>Whether the selected tool instance's use delay has elapsed.</summary>
    bool ToolReady();

    /// <summary>Starts the selected tool instance's use-delay cooldown.</summary>
    void MarkToolUsed(float delaySeconds);

    /// <summary>Spends durability on the selected tool instance; true when it broke.</summary>
    bool DamageTool(int amount = 1);

    /// <summary>Spends one unit from the selected stackable slot.</summary>
    void ConsumeSelected();

    /// <summary>Restores up to <paramref name="amount"/> HP to the player the use runs for (a
    /// bandage); false when they are at full health, so the use is never spent for nothing.</summary>
    bool Heal(int amount);
}

/// <summary>Inputs to <see cref="Item.OnUse"/> beyond the world itself: how far the aim reaches
/// and which tool instance is in hand (0 for stackable misc items like flint).</summary>
public sealed class ItemUseContext
{
    public IItemUseWorld World { get; }
    public float MaxDistance { get; }
    public int ToolInstanceId { get; }

    public ItemUseContext(IItemUseWorld world, float maxDistance, int toolInstanceId)
    {
        World = world;
        MaxDistance = maxDistance;
        ToolInstanceId = toolInstanceId;
    }
}
