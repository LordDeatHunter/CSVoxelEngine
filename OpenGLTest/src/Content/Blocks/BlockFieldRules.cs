using System;
using System.Collections.Generic;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Player;

namespace VoxelEngine.Content.Blocks;

/// <summary>The sideways-flow tuning of a fluid block (<c>flow.tickDivisor</c>, <c>flow.range</c>).</summary>
internal readonly record struct FlowTuning(int TickDivisor, int Range);

/// <summary>A block's stack limits: <c>stackMax</c> (0 = not a stack) and the count at which it turns solid.</summary>
internal readonly record struct StackLimits(int StackMax, int StackSolidAt);

/// <summary>The support flags of a block: whether it pops without a holder, whether a flow may wash it away and whether
/// only a full block face holds it.</summary>
internal readonly record struct SupportRules(bool NeedsSupport, bool FlowResistant, bool FullFace);

/// <summary>How a body moves over and through a block: speed, grip, pebbles, footfall dust and contact damage.</summary>
internal readonly record struct GroundTraits(
    float MovementSpeed, float Friction, float PebbleWeight, bool FootstepDust, float ContactDamage);

/// <summary>How the player body meets a block's top: the soft layer it sinks into and whether a landing on it is free.</summary>
internal readonly record struct BodySurface(float Sink, bool SoftLanding);

/// <summary>
/// The plain per-field rules of a block JSON that <see cref="BlockLoader"/> applies: each method
/// reads one group of <see cref="BlockDefinition"/> fields, throws a content error naming the file
/// for a bad value or a forbidden combination, and returns the validated value. The shape-related
/// fields (hitboxes, facing, panes, wood) live in <see cref="BlockSchemaParser"/>.
/// </summary>
internal static class BlockFieldRules
{
    /// <summary>The block's renderer, defaulting to the baked <see cref="BlockRenderer.Model"/>.</summary>
    internal static BlockRenderer ParseRenderer(BlockDefinition definition, string path) =>
        definition.Renderer switch
        {
            null or "model" => BlockRenderer.Model,
            "tree_wood" => BlockRenderer.TreeWood,
            "pebbles" => BlockRenderer.Pebbles,
            "branch_pile" => BlockRenderer.BranchPile,
            "ingot_pile" => BlockRenderer.IngotPile,
            "snow_layers" => BlockRenderer.SnowLayers,
            "fluid" => BlockRenderer.Fluid,
            "quern" => BlockRenderer.Quern,
            "multiblock" => BlockRenderer.Multiblock,
            "multiblock_part" => BlockRenderer.MultiblockPart,
            "pane" => BlockRenderer.Pane,
            _ => throw new InvalidOperationException(
                $"Block '{path}' has unknown renderer '{definition.Renderer}'; expected model, tree_wood, pebbles, branch_pile, ingot_pile, snow_layers, fluid, quern, multiblock, multiblock_part or pane."),
        };

    /// <summary>The pane profile of a <c>pane</c> block (null for every other renderer), refusing a pane object anywhere else.</summary>
    internal static PaneProfile? ParsePane(BlockDefinition definition, BlockRenderer renderer, string path)
    {
        var pane = renderer == BlockRenderer.Pane;
        if (pane)
            BlockSchemaParser.ValidatePane(definition, path);
        else if (definition.Pane != null)
            throw new InvalidOperationException(
                $"Block '{path}' declares a pane object without renderer \"pane\"; pane.* only shapes a pane.");
        return pane ? BlockSchemaParser.ParsePaneProfile(definition.Pane, path) : null;
    }

    /// <summary>
    /// A multiblock is a structure built out of a cube of cells: the anchor names the part that fills
    /// the others and the plain block it is built from and reverts to, and only an anchor can
    /// be a kiln. Both halves are ordinary solid full cells to everything but the mesher, so the
    /// anchor's surface really does cover its neighbors - which cutout, translucent and fluid would
    /// all break.
    /// </summary>
    internal static void ValidateMultiblock(BlockDefinition definition, BlockRenderer renderer, string path)
    {
        var isMultiblock = renderer is BlockRenderer.Multiblock or BlockRenderer.MultiblockPart;
        if ((renderer == BlockRenderer.Multiblock) != (definition.Multiblock != null))
            throw new InvalidOperationException(
                $"Block '{path}' {(definition.Multiblock != null ? "declares multiblock.* without renderer: \"multiblock\"" : "uses renderer \"multiblock\" without a multiblock object naming its part and source")}.");
        if (definition.Kiln != null && renderer != BlockRenderer.Multiblock)
            throw new InvalidOperationException(
                $"Block '{path}' declares a kiln object without renderer \"multiblock\"; only a multiblock anchor can be a kiln.");
        if (definition.Bloomery != null && renderer != BlockRenderer.Multiblock)
            throw new InvalidOperationException(
                $"Block '{path}' declares a bloomery object without renderer \"multiblock\"; only a multiblock anchor can be a bloomery.");
        if (definition.Bloomery != null && definition.Kiln != null)
            throw new InvalidOperationException(
                $"Block '{path}' declares both a kiln and a bloomery object; a structure is one or the other.");
        if (!isMultiblock) return;

        if (definition.HasItem ?? true)
            throw new InvalidOperationException(
                $"Block '{path}' uses a multiblock renderer without hasItem: false; a structure is built from its source block, never held.");
        if ((definition.Cutout ?? false) || (definition.Translucent ?? false) || (definition.Fluid ?? false))
            throw new InvalidOperationException(
                $"Block '{path}' uses a multiblock renderer with cutout, translucent or fluid; a multiblock cell must be an opaque solid so the structure occludes its neighbors.");
    }

    /// <summary>
    /// The status-effect block interaction flags (<c>flammable</c>, <c>freezesTo</c>,
    /// <c>meltsTo</c>): a fluid never burns and a multiblock cell never burns out of its structure,
    /// only a fluid freezes, and only a non-fluid melts. The freeze and melt targets themselves are
    /// resolved once every block is loaded (<see cref="BlockLinkResolver"/>).
    /// </summary>
    internal static void ValidateStatusFlags(BlockDefinition definition, string path)
    {
        var fluid = definition.Fluid ?? false;
        if (definition.Flammable ?? false)
        {
            if (fluid)
                throw new InvalidOperationException(
                    $"Block '{path}' sets flammable on a fluid; only a block can burn away.");
            if (ParseRenderer(definition, path) is BlockRenderer.Multiblock or BlockRenderer.MultiblockPart)
                throw new InvalidOperationException(
                    $"Block '{path}' sets flammable on a multiblock cell; burning one cell would leave the structure broken.");
        }
        if (definition.FreezesTo != null && !fluid)
            throw new InvalidOperationException(
                $"Block '{path}' sets freezesTo without fluid: true; only a fluid freezes.");
        if (definition.MeltsTo != null && fluid)
            throw new InvalidOperationException(
                $"Block '{path}' sets meltsTo on a fluid; only a solid block melts into a fluid.");
    }

    /// <summary>
    /// The <c>unbreakable</c> flag. Every field refused here would hand the cell to a raw-write
    /// path the unbreakable guards do not cover: the fluid tick (fluid), toppling (falls), support
    /// and flow pops (needsSupport), placement takeover (replaceableByPlacement), burning and
    /// thawing (flammable, meltsTo), felling, decay and growth (tree wood), multiblock convert and
    /// revert (multiblock renderers), tall-block removal (upperHalf) and priming (prime_tnt).
    /// </summary>
    internal static void ValidateUnbreakable(BlockDefinition definition, BlockRenderer renderer, string path)
    {
        if (!(definition.Unbreakable ?? false)) return;

        string? field = null;
        if (definition.Fluid ?? false) field = "fluid (a fluid cannot be unbreakable)";
        else if (definition.Falls ?? false) field = "falls";
        else if (definition.NeedsSupport ?? false) field = "needsSupport";
        else if (definition.ReplaceableByPlacement ?? false) field = "replaceableByPlacement";
        else if (definition.Flammable ?? false) field = "flammable";
        else if (definition.MeltsTo != null) field = "meltsTo";
        else if (renderer is BlockRenderer.TreeWood or BlockRenderer.Multiblock or BlockRenderer.MultiblockPart)
            field = $"renderer \"{definition.Renderer}\"";
        else if (definition.UpperHalf != null) field = "upperHalf";
        else if (definition.OnBreak == "prime_tnt") field = "onBreak \"prime_tnt\"";
        if (field != null)
            throw new InvalidOperationException(
                $"Block '{path}' sets unbreakable with {field}; that path would remove the block anyway.");
    }

    /// <summary>
    /// The <c>coversSky</c> flag. A covering block stops the sun seeds of every cell under it in a
    /// light volume, so it must be an opaque, plain full cell: anything that passes light or is
    /// drawn see-through would leave the cells under it visibly lit by the sky it claims to hide.
    /// </summary>
    internal static void ValidateCoversSky(BlockDefinition definition, BlockRenderer renderer, string path)
    {
        if (!(definition.CoversSky ?? false)) return;

        string? field = null;
        if (definition.Fluid ?? false) field = "fluid";
        else if (definition.Cutout ?? false) field = "cutout";
        else if (definition.Translucent ?? false) field = "translucent";
        else if (!(definition.Solid ?? true)) field = "solid: false";
        else if (definition.Light?.Passes == true) field = "light.passes: true";
        else if (renderer != BlockRenderer.Model) field = $"renderer \"{definition.Renderer}\"";
        if (field != null)
            throw new InvalidOperationException(
                $"Block '{path}' sets coversSky with {field}; only an opaque, plain full cell can shade the cells under it.");
    }

    /// <summary>What mining the block does: roll its loot, or prime TNT.</summary>
    internal static BlockBreakAction ParseOnBreak(BlockDefinition definition, string path) =>
        definition.OnBreak switch
        {
            null or "drop" => BlockBreakAction.Drop,
            "prime_tnt" => BlockBreakAction.PrimeTnt,
            _ => throw new InvalidOperationException(
                $"Block '{path}' has unknown onBreak '{definition.OnBreak}'; expected drop or prime_tnt."),
        };

    /// <summary>
    /// Tree wood carries its thickness and parent direction in the state byte; its wood object
    /// tunes how hardness and loot follow that thickness, so it means nothing on any other renderer.
    /// </summary>
    internal static (float TipHardness, WoodLootBand[]? LootBands, int HandMaxThickness) ParseWood(
        BlockDefinition definition, BlockRenderer renderer, string path)
    {
        var treeWood = renderer == BlockRenderer.TreeWood;
        if (!treeWood && definition.Wood != null)
            throw new InvalidOperationException(
                $"Block '{path}' declares a wood object without renderer \"tree_wood\"; wood.* only tunes tree wood.");
        return treeWood
            ? BlockSchemaParser.ParseTreeWood(definition, path)
            : (0.25f, (WoodLootBand[]?)null, -1);
    }

    /// <summary>The block's <c>light.emission</c>, 0..15.</summary>
    internal static byte ParseEmission(BlockDefinition definition, string path)
    {
        var emission = definition.Light?.Emission ?? 0;
        if (emission > 15)
            throw new InvalidOperationException($"Block '{path}' has light.emission {emission}; expected 0..15.");
        return emission;
    }

    /// <summary>The <c>flow.*</c> tuning, only legal on a fluid.</summary>
    internal static FlowTuning ParseFlow(BlockDefinition definition, string path)
    {
        var flow = definition.Flow;
        if (flow != null && !(definition.Fluid ?? false))
            throw new InvalidOperationException(
                $"Block '{path}' sets flow.* without fluid: true; flow tuning only applies to fluids.");
        var tickDivisor = flow?.TickDivisor ?? 1;
        if (tickDivisor is < 1 or > 8)
            throw new InvalidOperationException(
                $"Block '{path}' has flow.tickDivisor {tickDivisor}; expected 1..8.");
        var range = flow?.Range ?? 7;
        if (range is < 1 or > 7)
            throw new InvalidOperationException(
                $"Block '{path}' has flow.range {range}; expected 1..7 (the state byte holds levels 1..7).");
        return new FlowTuning(tickDivisor, range);
    }

    /// <summary>The <c>stackMax</c> / <c>stackSolidAt</c> pair, refusing the fluid and falls combinations a stack cannot honor.</summary>
    internal static StackLimits ParseStacking(BlockDefinition definition, bool fluid, bool falls, string path)
    {
        if (fluid && falls)
            throw new InvalidOperationException(
                $"Block '{path}' sets both fluid and falls: a fluid cell has no block to topple.");
        var stackMax = definition.StackMax ?? 0;
        if (definition.StackMax.HasValue && (stackMax < 2 || stackMax > 16))
            throw new InvalidOperationException(
                $"Block '{path}' has stackMax {stackMax}; expected 2..16.");
        if (stackMax >= 2 && fluid)
            throw new InvalidOperationException(
                $"Block '{path}' sets stackMax on a fluid: the state byte already holds its levels.");
        if (stackMax >= 2 && falls)
            throw new InvalidOperationException(
                $"Block '{path}' sets stackMax with falls: a toppled pile would lose its count.");
        var stackSolidAt = definition.StackSolidAt ?? 0;
        if (definition.StackSolidAt.HasValue && (stackMax < 2 || stackSolidAt < 1 || stackSolidAt > stackMax))
            throw new InvalidOperationException(
                $"Block '{path}' has stackSolidAt {stackSolidAt}; expected 1..stackMax on a stack block.");
        return new StackLimits(stackMax, stackSolidAt);
    }

    /// <summary>The <c>needsSupport</c> / <c>flowResistant</c> / <c>supportFullFace</c> flags. A fluid is never support-fragile (it is what pops those blocks).</summary>
    internal static SupportRules ParseSupport(BlockDefinition definition, bool fluid, bool falls, string path)
    {
        var needsSupport = !fluid && (definition.NeedsSupport ?? false);
        if (falls && needsSupport)
            throw new InvalidOperationException(
                $"Block '{path}' sets both falls and needsSupport: a falling block topples instead of popping.");
        var flowResistant = definition.FlowResistant ?? false;
        if (flowResistant && !needsSupport)
            throw new InvalidOperationException(
                $"Block '{path}' sets flowResistant without needsSupport: only a fragile block has a wash-away rule to opt out of.");
        var fullFace = definition.SupportFullFace ?? false;
        if (fullFace && !needsSupport)
            throw new InvalidOperationException(
                $"Block '{path}' sets supportFullFace without needsSupport: only a fragile block has a holder to be picky about.");
        return new SupportRules(needsSupport, flowResistant, fullFace);
    }

    /// <summary>State-dependent solidity (<c>stackSolidAt</c>) needs a non-solid block to switch on.</summary>
    internal static void ValidateStackSolidity(int stackSolidAt, bool solid, string path)
    {
        if (stackSolidAt > 0 && solid)
            throw new InvalidOperationException(
                $"Block '{path}' sets stackSolidAt on a solid block; state-dependent solidity needs solid: false.");
    }

    /// <summary>
    /// A placement only takes over a non-solid cell, so an always-solid block could never honor the
    /// flag: the author's intent cannot work, so it is a content error rather than an inert extra.
    /// </summary>
    internal static bool ParseReplaceableByPlacement(BlockDefinition definition, bool fluid, bool solid, string path)
    {
        var replaceable = !fluid && (definition.ReplaceableByPlacement ?? false);
        if (replaceable && solid)
            throw new InvalidOperationException(
                $"Block '{path}' sets replaceableByPlacement but is always solid; a placement only takes over a non-solid cell, so the flag needs solid: false.");
        return replaceable;
    }

    /// <summary>Movement speed, friction, pebble weight, footstep dust and contact damage, with the fluid exclusions.</summary>
    internal static GroundTraits ParseGroundTraits(BlockDefinition definition, bool fluid, string path)
    {
        var movementSpeed = definition.MovementSpeed ?? 0f;
        if (!float.IsFinite(movementSpeed) || movementSpeed < 0f || movementSpeed > 1f)
            throw new InvalidOperationException(
                $"Block '{path}' has movementSpeed {definition.MovementSpeed}; expected 0..1.");
        if (movementSpeed > 0f && fluid)
            throw new InvalidOperationException(
                $"Block '{path}' sets movementSpeed on a fluid: swimming handles how fast a body moves through it.");

        var friction = definition.Friction ?? BlockType.DefaultFriction;
        if (!float.IsFinite(friction) || friction < 0f || friction > 1f)
            throw new InvalidOperationException(
                $"Block '{path}' has friction {definition.Friction}; expected 0..1.");

        var pebbleWeight = definition.PebbleWeight ?? 0f;
        if (!float.IsFinite(pebbleWeight) || pebbleWeight < 0f || pebbleWeight > 1f)
            throw new InvalidOperationException(
                $"Block '{path}' has pebbleWeight {definition.PebbleWeight}; expected 0..1.");
        // A fluid is never the ground beneath a pebble cell, like its other forced-off traits
        if (fluid) pebbleWeight = 0f;

        // Nor is it ground a footfall scuffs: a swimmer kicks up no dust
        if (fluid && definition.FootstepDust == true)
            throw new InvalidOperationException(
                $"Block '{path}' sets footstepDust on a fluid: nobody runs on a fluid, so it can never puff.");
        var footstepDust = definition.FootstepDust ?? false;

        var contactDamage = definition.ContactDamage ?? 0f;
        if (!float.IsFinite(contactDamage) || contactDamage < 0f || contactDamage > 100f)
            throw new InvalidOperationException(
                $"Block '{path}' has contactDamage {definition.ContactDamage}; expected 0..100.");

        return new GroundTraits(movementSpeed, friction, pebbleWeight, footstepDust, contactDamage);
    }

    /// <summary>
    /// The <c>softLanding</c> and <c>sink</c> flags. A sink layer is cut off the top of a plain
    /// full-cell collider, so it is refused on anything that collides by another shape or not at
    /// all (a fluid, a non-solid or pass-through block, a stack, tree wood, a hitbox collider), and
    /// on a bounce block, whose spring would fight the slow sink.
    /// </summary>
    internal static BodySurface ParseBodySurface(BlockDefinition definition, BlockRenderer renderer, bool fluid,
        bool solid, float movementSpeed, bool collidesByHitbox, int stackMax, string path)
    {
        if (fluid && definition.SoftLanding == true)
            throw new InvalidOperationException(
                $"Block '{path}' sets softLanding on a fluid: a body never lands on a fluid.");

        var sink = definition.Sink ?? 0f;
        if (!float.IsFinite(sink) || sink < 0f || sink > SinkLayer.MaxSink)
            throw new InvalidOperationException(
                $"Block '{path}' has sink {definition.Sink}; expected 0..{SinkLayer.MaxSink}.");
        if (sink > 0f && (fluid || !solid || movementSpeed > 0f || collidesByHitbox || stackMax > 0
                          || renderer == BlockRenderer.TreeWood || definition.Bounce == true))
            throw new InvalidOperationException(
                $"Block '{path}' sets sink but is not a plain solid full-cell collider (a fluid, non-solid, pass-through, stack, tree wood, hitbox collider or bounce block); the soft layer is cut off the top of a whole solid cell.");

        return new BodySurface(sink, definition.SoftLanding ?? false);
    }

    /// <summary>The block's <c>hardness</c>, 0 or more.</summary>
    internal static float ParseHardness(BlockDefinition definition, string path)
    {
        var hardness = definition.Hardness ?? BlockType.DefaultHardness;
        if (!float.IsFinite(hardness) || hardness < 0f)
            throw new InvalidOperationException(
                $"Block '{path}' has hardness {definition.Hardness}; expected 0 or more.");
        return hardness;
    }

    /// <summary>The <c>requiresTool</c> gate and its <c>tools</c> tag table (null when none is listed).</summary>
    internal static (bool RequiresTool, IReadOnlyDictionary<string, float>? Tools) ParseTools(
        BlockDefinition definition, string path)
    {
        var requiresTool = definition.RequiresTool ?? false;
        IReadOnlyDictionary<string, float>? tools = null;
        if (definition.Tools is { Count: > 0 } authoredTools)
        {
            var table = new Dictionary<string, float>(authoredTools.Count, StringComparer.Ordinal);
            foreach (var (tag, required) in authoredTools)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    throw new InvalidOperationException($"Block '{path}' has a tools entry with no tag name.");
                if (!float.IsFinite(required) || required < 0f)
                    throw new InvalidOperationException(
                        $"Block '{path}' has tools.{tag} {required}; expected 0 or more.");
                table[tag] = required;
            }
            tools = table;
        }
        if (requiresTool && tools == null)
            Log.Warn($"Block '{path}' sets requiresTool but lists no tools; the gate is inert and any tool mines it.");
        return (requiresTool, tools);
    }
}
