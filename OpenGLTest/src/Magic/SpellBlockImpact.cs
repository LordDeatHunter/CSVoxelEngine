using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Items;
using VoxelEngine.Status;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Magic;

/// <summary>
/// What an element's <see cref="SpellPart.Impact"/> does to the cells a spell lands on, main thread
/// only and authoritative only (singleplayer and a server; a client never builds one). Every write
/// goes through a path that already runs the edit-path hooks:
/// <list type="bullet">
/// <item><see cref="SpellImpact.Ignite"/>, <see cref="SpellImpact.Freeze"/> and
/// <see cref="SpellImpact.Thaw"/> go through <see cref="StatusEffectBlockWriter"/>, whose cell
/// conversion schedules the fluid tick and leaf decay, topples a falls block above, remeshes,
/// unmarks player placement and runs the far-LOD and canopy hooks, capped per pulse and per frame.</item>
/// <item><see cref="SpellImpact.Prime"/> goes through <see cref="WorldItemUse.PrimeTnt"/>
/// (<c>RemoveCellForPrime</c>, which schedules leaf decay, then <c>ScheduleFluidAround</c> and
/// <c>MarkLodColumnEdited</c>), the firestarter's path minus its chance roll.</item>
/// <item><see cref="SpellImpact.Light"/> only lights a station (a fueled kiln, a full branch-pile
/// campfire), writing no cell and removing no station.</item>
/// <item><see cref="SpellImpact.Grow"/> goes through <see cref="World.TryGrowTree"/> and its
/// <c>TreeGrowthWriter</c>, which schedules fluids and leaf decay, remeshes and poisons the canopy.</item>
/// </list>
/// </summary>
internal sealed class SpellBlockImpact
{
    private readonly ChunkManager _chunks;
    private readonly StatusEffectBlockWriter _writer;
    private readonly WorldItemUse _itemUse;
    private readonly IItemUseWorld _use;

    internal SpellBlockImpact(ChunkManager chunks, StatusEffectBlockWriter writer, WorldItemUse itemUse)
    {
        _chunks = chunks;
        _writer = writer;
        _itemUse = itemUse;
        _use = itemUse;
    }

    /// <summary>
    /// Applies <paramref name="element"/>'s impacts to the cell a bolt landed on (integer coordinates),
    /// hit on the face with outward <paramref name="normal"/>. Nothing happens in an unloaded cell.
    /// </summary>
    internal void ApplyAt(SpellPart element, Vector3 cell, Vector3 normal)
    {
        var x = MathUtils.FloorToInt(cell.X);
        var y = MathUtils.FloorToInt(cell.Y);
        var z = MathUtils.FloorToInt(cell.Z);
        if (!_chunks.TryGetCell(x, y, z, out var id, out var state)) return;
        var impact = element.Impact;

        if ((impact & SpellImpact.Prime) != 0 && _use.IsPrimedTntBlock(id) && !_use.IsCellSubmerged(x, y, z))
            _itemUse.PrimeTnt(cell);
        if ((impact & SpellImpact.Light) != 0)
            Light(id, state, x, y, z);
        if ((impact & SpellImpact.Grow) != 0)
            Grow(element, id, cell, normal);

        var actions = SpellImpactRules.BlockActionsOf(impact);
        if (actions != StatusEffectBlockAction.None)
            _writer.ApplyAt(cell, SpellConfig.ImpactReachBlocks, actions);
    }

    /// <summary>
    /// Applies <paramref name="element"/>'s block conversions around a self-cast caster's
    /// <paramref name="feet"/> (the feet-centered status rule, which never freezes above the feet).
    /// Prime, light and grow are aimed impacts and do nothing here.
    /// </summary>
    internal void ApplyAtFeet(SpellPart element, Vector3 feet)
    {
        var actions = SpellImpactRules.BlockActionsOf(element.Impact);
        if (actions != StatusEffectBlockAction.None)
            _writer.Apply(feet, actions);
    }

    // The firestarter's targets minus its roll and durability: a fueled kiln from any of its cells,
    // else a full, unlit branch pile
    private void Light(ushort id, byte state, int x, int y, int z)
    {
        if (_use.CanIgniteKiln(x, y, z))
        {
            _use.IgniteKiln(x, y, z);
            return;
        }
        var pile = BlockType.ById[id];
        if (pile?.Renderer != BlockRenderer.BranchPile) return;
        if (_use.PileCountFor(id, x, y, z, state) < pile.StackMax || _use.IsCampfireLit(x, y, z)) return;
        _use.IgniteCampfire(x, y, z);
    }

    // A tree block grows its tree; anything else gets the element's sapling on the hit face, if it names one
    private void Grow(SpellPart element, ushort id, Vector3 cell, Vector3 normal)
    {
        if (TreeSpeciesRegistry.ForBlock(id) != null)
            _use.GrowTree(cell);
        else if (element.Plant != null)
            _use.PlantTree(element.Plant, cell, normal);
    }
}
