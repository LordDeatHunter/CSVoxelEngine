using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Graphics;
using VoxelEngine.Player;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Content.Blocks;

/// <summary>Reads and validates the shape-related fields of a block's JSON (material, flame, facing and support, multiblock size, pane, facingFromLook, open, tree wood, collidesByHitbox and hitboxes), each error naming the block file.</summary>
internal static class BlockSchemaParser
{
    /// <summary>
    /// Reads a block's <c>material</c> object, or null when it declared none. Every field is
    /// range-checked here rather than clamped in the shader, so a typo names its file at startup
    /// instead of quietly rendering wrong.
    /// </summary>
    internal static TileMaterial? ParseMaterial(BlockDefinition.MaterialDefinition? definition, string path)
    {
        if (definition == null) return null;

        var metallic = definition.Metallic ?? 0f;
        if (!float.IsFinite(metallic) || metallic < 0f || metallic > 1f)
            throw new InvalidOperationException(
                $"Block '{path}' has material.metallic {definition.Metallic}; expected 0..1.");

        var roughness = definition.Roughness ?? 1f;
        if (!float.IsFinite(roughness) || roughness <= 0f || roughness > 1f)
            throw new InvalidOperationException(
                $"Block '{path}' has material.roughness {definition.Roughness}; expected a positive value up to 1.");

        var normalStrength = definition.NormalStrength ?? 1f;
        if (!float.IsFinite(normalStrength) || normalStrength < 0f || normalStrength > 4f)
            throw new InvalidOperationException(
                $"Block '{path}' has material.normalStrength {definition.NormalStrength}; expected 0..4.");

        return new TileMaterial(metallic, roughness, normalStrength);
    }

    /// <summary>
    /// Reads a block's <c>flame</c>: three numbers in 0..16 model units, legal only on the
    /// <c>model</c> renderer (a procedural emitter has no authored head to burn on). A
    /// <c>facing: "horizontal"</c> block's point is authored facing north and pre-turned to all four
    /// facings exactly like its model (<see cref="HorizontalFacing.RotatePoint"/>); any other block
    /// keeps the one point. Returned in cell-local 0..1, or null when the block declares none.
    /// </summary>
    internal static Vector3[]? ParseFlame(BlockDefinition definition, BlockRenderer renderer, string path)
    {
        if (definition.Flame == null) return null;
        if (renderer != BlockRenderer.Model)
            throw new InvalidOperationException(
                $"Block '{path}' sets flame on renderer '{definition.Renderer}'; only a model block has an authored head for the flame.");
        var flame = definition.Flame;
        if (flame.Length != 3 || !Array.TrueForAll(flame, v => float.IsFinite(v) && v >= 0f && v <= 16f))
            throw new InvalidOperationException(
                $"Block '{path}' has flame [{string.Join(", ", flame)}]; expected three numbers in 0..16 model units.");

        var point = new Vector3(flame[0], flame[1], flame[2]) / 16f;
        if (definition.Facing != "horizontal")
            return [point];
        return
        [
            point,
            HorizontalFacing.RotatePoint(point, 1, 1f),
            HorizontalFacing.RotatePoint(point, 2, 1f),
            HorizontalFacing.RotatePoint(point, 3, 1f),
        ];
    }

    /// <summary>
    /// Reads a block's <c>facing</c> and <c>support</c>. A facing lives in the state byte, so it is
    /// refused wherever something else owns that byte (a fluid's level, a stack's count, a falling
    /// cube that would lose it) and on any renderer but <c>model</c> (a multiblock carries its own
    /// facing); <c>support</c> means nothing without <c>needsSupport</c>, and <c>"attached"</c>
    /// hangs off the cell behind the front, so it needs a facing to name that front.
    /// </summary>
    internal static (bool HorizontalFacing, bool SupportAttached) ParseFacingAndSupport(BlockDefinition definition,
        BlockRenderer renderer, bool fluid, bool falls, int stackMax, bool needsSupport, string path) =>
        ParseFacingAndSupport(definition, renderer, fluid, falls, stackMax, needsSupport, path, out _);

    /// <summary>
    /// <see cref="ParseFacingAndSupport(BlockDefinition, BlockRenderer, bool, bool, int, bool, string)"/>,
    /// also reporting <c>support: "above"</c> (<paramref name="supportAbove"/>): a block hanging from
    /// the cell over it (a lantern), which needs no facing.
    /// </summary>
    internal static (bool HorizontalFacing, bool SupportAttached) ParseFacingAndSupport(BlockDefinition definition,
        BlockRenderer renderer, bool fluid, bool falls, int stackMax, bool needsSupport, string path,
        out bool supportAbove)
    {
        var facing = definition.Facing switch
        {
            null => false,
            "horizontal" => true,
            _ => throw new InvalidOperationException(
                $"Block '{path}' has unknown facing '{definition.Facing}'; expected horizontal."),
        };
        switch (facing)
        {
            case true when renderer != BlockRenderer.Model:
                throw new InvalidOperationException(
                    $"Block '{path}' sets facing on renderer '{definition.Renderer}'; only a model block turns its model by the state byte.");
            case true when fluid || falls || stackMax >= 2:
                throw new InvalidOperationException(
                    $"Block '{path}' sets facing with fluid, falls or stackMax; each of those owns the state byte the facing lives in.");
        }

        var attached = definition.Support switch
        {
            null or "below" or "above" => false,
            "attached" => true,
            _ => throw new InvalidOperationException(
                $"Block '{path}' has unknown support '{definition.Support}'; expected below, above or attached."),
        };
        supportAbove = definition.Support == "above";
        if (definition.Support != null && !needsSupport)
            throw new InvalidOperationException(
                $"Block '{path}' sets support without needsSupport; only a support-fragile block is held up by anything.");
        if (attached && !facing)
            throw new InvalidOperationException(
                $"Block '{path}' sets support: \"attached\" without facing: \"horizontal\"; the attached face is the one behind its front.");
        return (facing, attached);
    }

    /// <summary>
    /// Validates a <c>pane</c> block: its shape comes from its neighbors (<see cref="PaneShape"/>),
    /// so it declares no hitbox, and its state byte stays free, so it is no fluid, falling block,
    /// stack or support-fragile block either (a pane stands on its own like glass).
    /// </summary>
    internal static void ValidatePane(BlockDefinition definition, string path)
    {
        if (definition.Hitbox.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            throw new InvalidOperationException(
                $"Block '{path}' declares a hitbox on renderer \"pane\"; its boxes follow its arms.");
        if ((definition.Fluid ?? false) || (definition.Falls ?? false) || definition.StackMax.HasValue
            || (definition.NeedsSupport ?? false))
            throw new InvalidOperationException(
                $"Block '{path}' uses renderer \"pane\" with fluid, falls, stackMax or needsSupport; a pane is a plain standing block.");
    }

    /// <summary>
    /// Reads a block's <c>facingFromLook</c>: a placement that faces the block along the player's
    /// look needs a facing to write, and an attached block's facing already names its wall.
    /// </summary>
    internal static bool ParseFacingFromLook(BlockDefinition definition, bool horizontalFacing, bool supportAttached,
        string path)
    {
        if (!(definition.FacingFromLook ?? false)) return false;
        if (!horizontalFacing)
            throw new InvalidOperationException(
                $"Block '{path}' sets facingFromLook without facing: \"horizontal\"; the look has no facing to turn.");
        if (supportAttached)
            throw new InvalidOperationException(
                $"Block '{path}' sets facingFromLook with support: \"attached\"; an attached block faces away from its wall.");
        return true;
    }

    /// <summary>
    /// Reads a block's <c>open</c> object: its open boxes, or null for a block that does not open.
    /// The open bit shares the state byte with the facing, so an openable block needs one, and
    /// both the open model and the open boxes must be written.
    /// </summary>
    internal static BlockHitbox[]? ParseOpen(BlockDefinition definition, bool horizontalFacing, string path)
    {
        if (definition.Open is not { } open) return null;
        if (!horizontalFacing)
            throw new InvalidOperationException(
                $"Block '{path}' declares an open object without facing: \"horizontal\"; the open bit sits beside the facing.");
        if (open.Model.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidOperationException($"Block '{path}' declares an open object without a model.");
        return ParseHitboxes(open.Hitbox, path)
               ?? throw new InvalidOperationException($"Block '{path}' declares an open object without a hitbox.");
    }

    /// <summary>
    /// Reads a multiblock anchor's <c>multiblock.size</c>, the edge length of its cubic volume in
    /// cells: <see cref="MultiblockShape.DefaultSize"/> when omitted, and anything outside
    /// <see cref="MultiblockShape.MinSize"/>..<see cref="MultiblockShape.MaxSize"/> (the most the
    /// state byte's two bits per offset hold) throws naming the file. 0 for a block that is not
    /// an anchor.
    /// </summary>
    internal static int ParseMultiblockSize(BlockDefinition.MultiblockDefinition? definition, string path)
    {
        if (definition == null) return 0;
        var size = definition.Size ?? MultiblockShape.DefaultSize;
        if (!MultiblockShape.IsValidSize(size))
            throw new InvalidOperationException(
                $"Block '{path}' has multiblock.size {size}; a multiblock is {MultiblockShape.MinSize}..{MultiblockShape.MaxSize} cells along each edge.");
        return size;
    }

    /// <summary>
    /// Reads a kiln anchor's <c>kiln</c> object into its <see cref="KilnProfile"/>: omitted
    /// fields take the plain kiln's (fuel cap 4, speed 1, no alloying), and a fuel cap or melt
    /// speed <see cref="KilnProfile.Create"/> refuses throws naming the file. The layout is the
    /// one of the anchor's <paramref name="multiblockSize"/> (<see cref="KilnLayout.ForSize"/>),
    /// and a size no kiln model exists for throws too; a block with no multiblock (0) keeps the
    /// default, since the renderer check refuses it anyway. Null for a block that is not a kiln.
    /// </summary>
    internal static KilnProfile? ParseKiln(BlockDefinition.KilnDefinition? definition, string path,
        int multiblockSize = MultiblockShape.DefaultSize)
    {
        if (definition == null) return null;
        KilnProfile profile;
        try
        {
            profile = KilnProfile.Create(
                definition.FuelCap ?? KilnProfile.Default.FuelCap,
                definition.MeltSpeed ?? KilnProfile.Default.MeltSpeed,
                definition.CanAlloy ?? KilnProfile.Default.CanAlloy);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"Block '{path}' has a kiln object with {e.Message}", e);
        }

        if (multiblockSize == 0) return profile;
        var layout = KilnLayout.ForSize(multiblockSize)
            ?? throw new InvalidOperationException(
                $"Block '{path}' is a kiln of multiblock.size {multiblockSize}, but kiln models exist only for sizes {string.Join(", ", KilnLayout.All.Select(l => l.VolumeSize))}.");
        return profile with { Layout = layout };
    }

    /// <summary>Reads a bloomery anchor's <c>bloomery</c> object into its <see cref="BloomeryProfile"/>: omitted fields
    /// take the defaults and a cap <see cref="BloomeryProfile.Create"/> refuses throws naming the file. Null for a
    /// block that is not a bloomery.</summary>
    internal static BloomeryProfile? ParseBloomery(BlockDefinition.BloomeryDefinition? definition, string path)
    {
        if (definition == null) return null;
        try
        {
            return BloomeryProfile.Create(definition.OreCap ?? BloomeryProfile.DefaultOreCap,
                definition.FuelCap ?? BloomeryProfile.DefaultFuelCap,
                ResolveItemName(definition.Ore, "bloomery.ore", path),
                ResolveItemName(definition.Bloom, "bloomery.bloom", path),
                ResolveItemName(definition.Slag, "bloomery.slag", path));
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"Block '{path}' has a bloomery object with {e.Message}", e);
        }
    }

    /// <summary>A required block or item name of a block's object, resolved to its shared id; a missing or unknown
    /// name throws naming the file and the field.</summary>
    private static ushort ResolveItemName(string? name, string field, string path)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"Block '{path}' needs {field}, which names the item it works.");
        if (ContentFiles.TryResolve(name, out var id)) return id;
        throw new InvalidOperationException($"Block '{path}' names '{name}' in {field}, which is not a registered block or item.");
    }

    /// <summary>Reads a block's <c>anvil</c> object into its <see cref="AnvilProfile"/>. An anvil is a plain model
    /// block; a multiblock is refused. Null for a block that is not an anvil.</summary>
    /// <summary>Validates a block's <c>spritesheet</c> object into its <see cref="TextureSheet"/>.</summary>
    internal static TextureSheet ParseSpritesheet(BlockDefinition.SpritesheetDefinition definition, string path)
    {
        var texture = definition.Texture;
        if (string.IsNullOrWhiteSpace(texture))
            throw new InvalidOperationException($"Block '{path}' has a spritesheet without a texture name.");
        var cols = definition.Cols ?? 0;
        var rows = definition.Rows ?? 0;
        if (cols < 1 || rows < 1)
            throw new InvalidOperationException($"Block '{path}' has a spritesheet without a positive cols and rows.");
        var frames = definition.Frames ?? cols * rows;
        if (frames < 1 || frames > cols * rows)
            throw new InvalidOperationException(
                $"Block '{path}' has spritesheet.frames {frames}; expected 1..{cols * rows}.");
        var fps = definition.Fps ?? 8f;
        if (!(fps > 0f) || float.IsInfinity(fps))
            throw new InvalidOperationException($"Block '{path}' has spritesheet.fps {fps}; expected a positive number.");
        return new TextureSheet(texture, cols, rows, frames, fps);
    }

    internal static AnvilProfile? ParseAnvil(BlockDefinition.AnvilDefinition? definition, BlockRenderer renderer,
        string path)
    {
        if (definition == null) return null;
        if (renderer is not BlockRenderer.Model)
            throw new InvalidOperationException(
                $"Block '{path}' declares an anvil object with renderer \"{renderer}\"; an anvil is a plain model block.");
        try
        {
            return AnvilProfile.Create(definition.Hits ?? AnvilProfile.DefaultHits,
                ResolveItemName(definition.Input, "anvil.input", path),
                ResolveItemName(definition.Result, "anvil.result", path),
                ResolveItemName(definition.Slag, "anvil.slag", path));
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"Block '{path}' has an anvil object with {e.Message}", e);
        }
    }

    /// <summary>
    /// Reads a block's <c>chest</c> object: its slot count (1..<see cref="ChestConfig.MaxSlots"/>) and the optional loot
    /// table a never-opened one rolls, which must be a loaded loot table (loot tables load before blocks), or instead a
    /// non-empty <c>tiers</c> list of loaded loot tables (at most <see cref="ChestProfile.MaxTiers"/>), one per tier of
    /// a tiered chest; <c>loot</c> and <c>tiers</c> are exclusive. Its <c>skin</c> must name a PNG under
    /// <c>textures/chests</c> (<see cref="ChestModel.SkinPath"/>). A chest is a plain model block; any other renderer
    /// is refused. Null for a block that is not a chest.
    /// </summary>
    internal static ChestProfile? ParseChest(BlockDefinition.ChestDefinition? definition, BlockRenderer renderer,
        string path)
    {
        if (definition == null) return null;
        if (renderer != BlockRenderer.Model)
            throw new InvalidOperationException($"Block '{path}' has a chest object but is not a model block.");
        if (definition.Slots is not { } slots || slots < 1 || slots > ChestConfig.MaxSlots)
            throw new InvalidOperationException(
                $"Block '{path}' has chest.slots {definition.Slots}; expected 1..{ChestConfig.MaxSlots}.");
        if (string.IsNullOrWhiteSpace(definition.Skin))
            throw new InvalidOperationException($"Block '{path}' has a chest object without a chest.skin.");
        var skin = definition.Skin.Trim();
        var skinPath = ChestModel.SkinPath(ContentRegistry.AssetsRoot, skin);
        if (!File.Exists(skinPath))
            throw new InvalidOperationException(
                $"Block '{path}' has chest.skin '{skin}', but '{skinPath}' does not exist.");
        var loot = string.IsNullOrWhiteSpace(definition.Loot) ? null : definition.Loot.Trim();
        if (loot != null && !ContentRegistry.LootTables.ContainsKey(loot))
            throw new InvalidOperationException($"Block '{path}' has chest.loot '{loot}', which is not a loot table.");
        if (definition.Tiers == null) return new ChestProfile(slots, loot, Skin: skin);

        if (loot != null)
            throw new InvalidOperationException($"Block '{path}' has both chest.loot and chest.tiers; give one.");
        if (definition.Tiers.Length == 0)
            throw new InvalidOperationException($"Block '{path}' has an empty chest.tiers list.");
        if (definition.Tiers.Length > ChestProfile.MaxTiers)
            throw new InvalidOperationException(
                $"Block '{path}' has {definition.Tiers.Length} chest.tiers; at most {ChestProfile.MaxTiers}.");
        var tiers = new List<string>(definition.Tiers.Length);
        foreach (var entry in definition.Tiers)
        {
            if (string.IsNullOrWhiteSpace(entry))
                throw new InvalidOperationException($"Block '{path}' has a blank chest.tiers entry.");
            var name = entry.Trim();
            if (!ContentRegistry.LootTables.ContainsKey(name))
                throw new InvalidOperationException(
                    $"Block '{path}' has chest.tiers entry '{name}', which is not a loot table.");
            tiers.Add(name);
        }
        return new ChestProfile(slots, null, tiers, skin);
    }

    /// <summary>
    /// Reads a pane block's <c>pane</c> object into its <see cref="PaneProfile"/>: omitted fields
    /// take the glass pane's dimensions (post 2 px, arm as wide as the post, one full-height
    /// rail), and anything <see cref="PaneProfile.Create"/> refuses throws naming the file.
    /// </summary>
    internal static PaneProfile ParsePaneProfile(BlockDefinition.PaneDefinition? definition, string path)
    {
        if (definition == null) return PaneProfile.Default;
        var post = definition.Post ?? PaneProfile.Default.PostPx;
        var arm = definition.Arm ?? post;
        var rails = new List<(int Bottom, int Top)>();
        if (definition.Rails == null)
            rails.Add((0, PaneProfile.PixelsPerBlock));
        else
            foreach (var rail in definition.Rails)
            {
                if (rail is not { Length: 2 })
                    throw new InvalidOperationException(
                        $"Block '{path}' has a pane.rails entry that is not a [bottom, top] pair.");
                rails.Add((rail[0], rail[1]));
            }

        try
        {
            return PaneProfile.Create(post, arm, rails);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"Block '{path}' has a pane object with {e.Message}", e);
        }
    }

    /// <summary>
    /// Validates a <c>tree_wood</c> block: it needs a <c>wood</c> object, cannot be cutout, translucent, a fluid, falling, support-fragile, a stack or
    /// declare a hitbox (its shape comes from the state byte, see <see cref="TreeWoodShape"/>),
    /// and returns its tip hardness, its ascending thickness loot bands and the highest thickness code a bare hand may mine (-1 for none).
    /// </summary>
    internal static (float TipHardness, WoodLootBand[] Bands, int HandMaxThickness) ParseTreeWood(BlockDefinition definition, string path)
    {
        var wood = definition.Wood ?? throw new InvalidOperationException(
            $"Block '{path}' uses renderer \"tree_wood\" without a wood object.");
        if ((definition.Cutout ?? false) || (definition.Translucent ?? false) || (definition.Fluid ?? false))
            throw new InvalidOperationException(
                $"Block '{path}' uses renderer \"tree_wood\" with cutout, translucent or fluid; tree wood is an opaque solid.");
        if ((definition.Falls ?? false) || (definition.NeedsSupport ?? false) || definition.StackMax.HasValue)
            throw new InvalidOperationException(
                $"Block '{path}' uses renderer \"tree_wood\" with falls, needsSupport or stackMax; its state byte holds the tree graph.");
        if (definition.Hitbox.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            throw new InvalidOperationException(
                $"Block '{path}' declares a hitbox on renderer \"tree_wood\"; its boxes follow its thickness and connections.");

        var tip = wood.TipHardness ?? 0.25f;
        if (!float.IsFinite(tip) || tip < 0f || tip > 1f)
            throw new InvalidOperationException(
                $"Block '{path}' has wood.tipHardness {wood.TipHardness}; expected 0..1.");

        var hand = wood.HandMaxThickness ?? -1;
        if (hand is < -1 or > WoodState.MaxThickness)
            throw new InvalidOperationException(
                $"Block '{path}' has wood.handMaxThickness {hand}; expected 0..{WoodState.MaxThickness}.");

        if (wood.Loot is not { Length: > 0 } authored)
            return (tip, [new WoodLootBand(0, null, true)], hand);

        var bands = new WoodLootBand[authored.Length];
        for (var i = 0; i < authored.Length; i++)
        {
            var band = authored[i] ?? throw new InvalidOperationException(
                $"Block '{path}' has a null wood.loot[{i}] entry.");
            var min = band.MinThickness ?? 0;
            if (min is < 0 or > WoodState.MaxThickness)
                throw new InvalidOperationException(
                    $"Block '{path}' has wood.loot[{i}].minThickness {min}; expected 0..{WoodState.MaxThickness}.");
            switch (i)
            {
                case 0 when min != 0:
                    throw new InvalidOperationException(
                        $"Block '{path}' has wood.loot[0].minThickness {min}; the first band must start at 0 so every thickness has loot.");
                case > 0 when min <= bands[i - 1].MinThickness:
                    throw new InvalidOperationException(
                        $"Block '{path}' has wood.loot[{i}].minThickness {min}; bands must be strictly ascending.");
                default:
                {
                    var loot = band.Loot ?? "self";
                    bands[i] = loot switch
                    {
                        "self" => new WoodLootBand(min, null, true),
                        "none" => new WoodLootBand(min, LootTable.None, false),
                        _ => ContentRegistry.LootTables.TryGetValue(loot, out var table)
                            ? new WoodLootBand(min, table, false)
                            : throw new InvalidOperationException(
                                $"Block '{path}' has wood.loot[{i}].loot '{loot}', which is neither self, none nor an existing loot table."),
                    };
                    break;
                }
            }
        }

        return (tip, bands, hand);
    }

    /// <summary>The loot table named by a log's <c>wood.split</c>, or null when it names none; a name that is no loot table
    /// throws naming the file.</summary>
    internal static LootTable? ParseSplitLoot(BlockDefinition definition, string path)
    {
        if (definition.Wood?.Split is not { } name) return null;
        return ContentRegistry.LootTables.TryGetValue(name, out var table)
            ? table
            : throw new InvalidOperationException($"Block '{path}' has wood.split '{name}', which is not an existing loot table.");
    }

    /// <summary>Settles <c>collidesByHitbox</c>: only a solid, non-fluid, non-stack block that declares
    /// a hitbox may collide by shape, because <see cref="Camera"/>'s collision reads exactly those
    /// boxes; anything else throws naming the file.</summary>
    internal static bool ResolveCollidesByHitbox(BlockDefinition definition, BlockHitbox[]? declaredHitboxes,
        bool solid, bool fluid, int stackMax, string path)
    {
        if (!(definition.CollidesByHitbox ?? false)) return false;
        if (declaredHitboxes == null)
            throw new InvalidOperationException(
                $"Block '{path}' sets collidesByHitbox without a hitbox; declare the boxes the body should collide with.");
        if (fluid)
            throw new InvalidOperationException(
                $"Block '{path}' sets collidesByHitbox on a fluid; nothing collides with a fluid.");
        if (stackMax > 0)
            throw new InvalidOperationException(
                $"Block '{path}' sets collidesByHitbox on a stack block; a stack's solidity is state-driven (see stackSolidAt).");
        if (!solid)
            throw new InvalidOperationException(
                $"Block '{path}' sets collidesByHitbox with solid: false; a non-solid block never collides.");
        return true;
    }

    internal static BlockHitbox[]? ParseHitboxes(JsonElement hitbox, string path)
    {
        switch (hitbox.ValueKind)
        {
            case JsonValueKind.Undefined or JsonValueKind.Null:
                return null;
            case JsonValueKind.Array when hitbox.GetArrayLength() == 0:
                throw new InvalidOperationException($"Block '{path}' has an empty hitbox array; omit hitbox for a full cube.");
            case JsonValueKind.Array:
            {
                var boxes = new BlockHitbox[hitbox.GetArrayLength()];
                var i = 0;
                foreach (var element in hitbox.EnumerateArray())
                    boxes[i++] = ParseOneBox(element, path);
                return boxes;
            }
            case JsonValueKind.Object:
                return [ParseOneBox(hitbox, path)];
            default:
                throw new InvalidOperationException($"Block '{path}' has a hitbox that is neither an object nor an array.");
        }
    }

    private static BlockHitbox ParseOneBox(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Block '{path}' has a hitbox entry that is not an object.");
        if (!element.TryGetProperty("min", out var minElement) || !element.TryGetProperty("max", out var maxElement))
            throw new InvalidOperationException($"Block '{path}' has a hitbox entry without min and max.");
        var min = ReadBoxCorner(minElement, "min", path);
        var max = ReadBoxCorner(maxElement, "max", path);
        if (min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
            throw new InvalidOperationException($"Block '{path}' has a hitbox whose min {min} is not below its max {max}.");
        return new BlockHitbox(min, max);
    }

    private static Vector3 ReadBoxCorner(JsonElement element, string field, string path)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
            throw new InvalidOperationException($"Block '{path}' has a hitbox.{field} that is not a 3-number array.");
        var values = new float[3];
        var i = 0;
        foreach (var number in element.EnumerateArray())
        {
            if (number.ValueKind != JsonValueKind.Number)
                throw new InvalidOperationException($"Block '{path}' has a hitbox.{field} that is not a 3-number array.");
            var value = number.GetSingle();
            if (!float.IsFinite(value) || value < 0f || value > 1f)
                throw new InvalidOperationException($"Block '{path}' has a hitbox.{field} value {value}; expected 0..1.");
            values[i++] = value;
        }
        return new Vector3(values[0], values[1], values[2]);
    }
}
