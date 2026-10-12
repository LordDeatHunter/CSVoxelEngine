using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The cast-mold pipeline end to end, asserted through the baked tables rather than the JSON:
/// the six slab blocks bake to non-occluding quarter-height shapes inside their declared
/// hitbox, their tops carry a rim and a sunken pocket, their only cullfaces sit on the cell
/// boundary (so the pocket walls always draw), each raw mold crafts from and uncrafts to three
/// mudballs, and a raw mold fires into its cooked twin on a lit campfire.
/// </summary>
public sealed class MoldTests
{
    public MoldTests() => TestContent.EnsureLoaded();

    private static readonly string[] All =
    [
        "mold_pickaxe", "mold_axe", "mold_ingot",
        "fired_mold_pickaxe", "fired_mold_axe", "fired_mold_ingot",
    ];

    private static readonly (string Raw, string Fired)[] Pairs =
    [
        ("mold_pickaxe", "fired_mold_pickaxe"),
        ("mold_axe", "fired_mold_axe"),
        ("mold_ingot", "fired_mold_ingot"),
    ];

    public static IEnumerable<object[]> AllMolds => All.Select(name => new object[] { name });

    public static IEnumerable<object[]> MoldPairs => Pairs.Select(p => new object[] { p.Raw, p.Fired });

    private static BakedQuad[] Quads(string name) => ContentRegistry.RequireBlock(name).BakedModel!.Quads;

    /// <summary>Footprint of a horizontal quad, which is all an up or down quad ever is.</summary>
    private static float Area(BakedQuad quad)
    {
        var xs = quad.Positions.Select(p => p.X).ToArray();
        var zs = quad.Positions.Select(p => p.Z).ToArray();
        return (xs.Max() - xs.Min()) * (zs.Max() - zs.Min());
    }

    private static byte BranchPile => ContentRegistry.RequireBlock("branch_pile").Id;

    private static ChunkManager LitPile(out CampfireManager fires, out DropManager drops)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        chunk.SetBlockRaw(0, 0, 0, BranchPile, 4);

        fires = new CampfireManager();
        drops = new DropManager();
        Assert.True(fires.Ignite(0, 0, 0));
        return manager;
    }

    [Theory]
    [MemberData(nameof(AllMolds))]
    public void MoldsBakeToNonOccludingSlabsWithinTheirHitbox(string name)
    {
        var type = ContentRegistry.RequireBlock(name);

        Assert.False(type.BakedModel!.IsFullCube);
        Assert.False(type.OccludesNeighbours);
        Assert.True(type.PassesLight);
        Assert.True(type.Solid);
        Assert.True(type.HasItem);
        Assert.True(type.CollidesByHitbox);
        Assert.False(type.CastsAmbientOcclusion);
        Assert.IsType<BlockItem>(ItemRegistry.TryGet(type.Id));

        var box = Assert.Single(type.Hitboxes);
        Assert.Equal(Vector3.Zero, box.Min);
        Assert.Equal(1f, box.Max.X, precision: 4);
        Assert.Equal(1f, box.Max.Z, precision: 4);
        Assert.InRange(box.Max.Y, 0.001f, 0.999f);

        var quads = Quads(name);
        // The whole shape lives inside the cell footprint and under the hitbox lid, so the
        // declared slab really is what the body collides with.
        Assert.All(quads, q => Assert.All(q.Positions, p =>
        {
            Assert.InRange(p.X, 0f, 1f);
            Assert.InRange(p.Z, 0f, 1f);
            Assert.InRange(p.Y, 0f, box.Max.Y + 1e-4f);
        }));
        // The generator's $SlabHeight/16 and the block JSON's hitbox height are one number.
        Assert.Equal(box.Max.Y, quads.SelectMany(q => q.Positions).Max(p => p.Y), precision: 4);
        // Per-voxel model emitter: a few dozen quads is the budget. Trade detail away with the
        // script's $RimCells/$AlphaThreshold rather than raising this cap.
        Assert.InRange(quads.Length, 20, 300);
    }

    [Theory]
    [MemberData(nameof(AllMolds))]
    public void MoldTopsHaveARimAndASunkenPocket(string name)
    {
        var type = ContentRegistry.RequireBlock(name);
        var top = type.Hitboxes[0].Max.Y;
        var ups = Quads(name).Where(q => q.Face == 4).ToArray();

        var levels = ups.Select(q => MathF.Round(q.Positions[0].Y, 4)).Distinct().OrderBy(y => y).ToArray();
        Assert.Equal(2, levels.Length);
        var floor = levels[0];
        Assert.Equal(top, levels[1], precision: 4);
        Assert.InRange(floor, 0.001f, top - 0.001f);

        foreach (var quad in ups.Where(q => MathF.Round(q.Positions[0].Y, 4) == floor))
        {
            // The pocket floor is interior geometry: nothing may cull it away.
            Assert.Equal(-1, quad.CullFace);
            Assert.All(quad.Positions, p =>
            {
                Assert.InRange(p.X, 1f / 16f, 15f / 16f);
                Assert.InRange(p.Z, 1f / 16f, 15f / 16f);
            });
        }

        // Rim and pocket floor together tile the cell top exactly once.
        Assert.Equal(1f, ups.Sum(Area), precision: 3);
    }

    [Theory]
    [MemberData(nameof(AllMolds))]
    public void MoldCullfacesOnlySitOnCellBoundaries(string name)
    {
        var quads = Quads(name);

        foreach (var quad in quads.Where(q => q.CullFace >= 0))
        {
            Assert.Equal(quad.Face, quad.CullFace);
            foreach (var p in quad.Positions)
            {
                var (coord, plane) = quad.Face switch
                {
                    0 => (p.Z, 0f),
                    1 => (p.Z, 1f),
                    2 => (p.X, 0f),
                    3 => (p.X, 1f),
                    5 => (p.Y, 0f),
                    _ => (float.NaN, 0f),
                };
                Assert.False(float.IsNaN(coord), "an up face may never be culled");
                Assert.Equal(plane, coord, precision: 5);
            }
        }

        Assert.DoesNotContain(quads, q => q.Face == 4 && q.CullFace >= 0);
        Assert.All(quads.Where(q => q.Face == 5), q =>
        {
            Assert.Equal(5, q.CullFace);
            Assert.All(q.Positions, p => Assert.Equal(0f, p.Y, precision: 5));
        });
        Assert.Equal(1f, quads.Where(q => q.Face == 5).Sum(Area), precision: 3);

        // A cullface culls against the world neighbor, never a sibling element, so every
        // pocket wall carries none and therefore sits strictly inside the cell.
        foreach (var quad in quads.Where(q => q.Face <= 3 && q.CullFace < 0))
        {
            var coord = quad.Face <= 1 ? quad.Positions[0].Z : quad.Positions[0].X;
            Assert.InRange(coord, 1e-5f, 1f - 1e-5f);
        }
    }

    [Fact]
    public void FiredMoldsShareGeometryWithRawAndWearFiredMud()
    {
        var mud = TextureAtlas.TileOf("mud");
        var firedMud = TextureAtlas.TileOf("fired_mud");
        Assert.NotEqual(mud, firedMud);

        foreach (var (rawName, firedName) in Pairs)
        {
            var raw = Quads(rawName);
            var fired = Quads(firedName);
            Assert.Equal(raw.Length, fired.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                Assert.Equal(raw[i].Positions, fired[i].Positions);
                Assert.Equal(raw[i].Face, fired[i].Face);
                Assert.Equal(raw[i].CullFace, fired[i].CullFace);
            }

            Assert.All(raw, q => Assert.Equal(mud, q.Tile));
            Assert.All(fired, q => Assert.Equal(firedMud, q.Tile));
        }
    }

    [Fact]
    public void RawMoldsCraftFromAndUncraftToThreeMudballs()
    {
        var mudball = ContentRegistry.RequireItem("mudball").Id;

        foreach (var (rawName, firedName) in Pairs)
        {
            var rawId = ContentRegistry.RequireBlock(rawName).Id;
            var firedId = ContentRegistry.RequireBlock(firedName).Id;

            var craft = ContentRegistry.RecipeByName(rawName)!;
            Assert.Equal(rawId, craft.ResultId);
            Assert.Equal(1, craft.ResultCount);
            var pressed = Assert.Single(craft.Ingredients);
            Assert.False(pressed.IsTag);
            Assert.Equal(mudball, pressed.Id);
            Assert.Equal(3, pressed.Count);
            Assert.Same(craft, ContentRegistry.RecipeForResult(rawId));

            var uncraft = ContentRegistry.RecipeByName($"mudball_from_{rawName}")!;
            Assert.Equal(mudball, uncraft.ResultId);
            Assert.Equal(3, uncraft.ResultCount);
            var squashed = Assert.Single(uncraft.Ingredients);
            Assert.False(squashed.IsTag);
            Assert.Equal(rawId, squashed.Id);
            Assert.Equal(1, squashed.Count);

            // Firing is one way: nothing crafts a fired mold and nothing takes one apart.
            Assert.DoesNotContain(ContentRegistry.CraftingRecipes,
                r => r.ResultId == firedId || r.Ingredients.Any(i => i.Accepts(firedId)));
        }
    }

    [Fact]
    public void UncraftThenCraftRoundTripsThroughTheInventory()
    {
        var inventory = new Inventory();
        var mudball = ContentRegistry.RequireItem("mudball").Id;
        var moldId = ContentRegistry.RequireBlock("mold_pickaxe").Id;
        Assert.Equal(Inventory.StartingStock, inventory.CountFor(moldId));
        Assert.Equal(Inventory.StartingStock, inventory.CountFor(ContentRegistry.RequireBlock("mold_axe").Id));

        var craft = ContentRegistry.RecipeByName("mold_pickaxe")!;
        var uncraft = ContentRegistry.RecipeByName("mudball_from_mold_pickaxe")!;

        // A fresh inventory holds the molds but no mudballs, so the uncrafts into mudballs are
        // affordable and the craft back from them is not.
        var fresh = inventory.Craftable();
        Assert.Contains(uncraft, fresh);
        Assert.Contains(ContentRegistry.RecipeByName("mudball_from_mold_axe")!, fresh);
        Assert.DoesNotContain(craft, fresh);
        Assert.DoesNotContain(ContentRegistry.RecipeByName("mold_axe")!, fresh);

        Assert.True(inventory.TryCraft(uncraft));
        Assert.Equal(3, inventory.CountFor(mudball));
        Assert.Equal(Inventory.StartingStock - 1, inventory.CountFor(moldId));
        Assert.Contains(craft, inventory.Craftable());

        Assert.True(inventory.TryCraft(craft));
        Assert.Equal(0, inventory.CountFor(mudball));
        Assert.Equal(Inventory.StartingStock, inventory.CountFor(moldId));
    }

    [Theory]
    [MemberData(nameof(MoldPairs))]
    public void RawMoldsFireOnACampfire(string rawName, string firedName)
    {
        var rawId = ContentRegistry.RequireBlock(rawName).Id;
        var firedId = ContentRegistry.RequireBlock(firedName).Id;

        var recipe = ContentRegistry.CookRecipeForInput(rawId)!;
        Assert.Equal(firedId, recipe.ResultId);
        Assert.Equal(1, recipe.ResultCount);
        // One firing has to fit inside a single burn, or the fire dies holding the mold.
        Assert.InRange(recipe.CookSeconds, 0.01f, CampfireManager.BurnSeconds);
        Assert.Null(ContentRegistry.CookRecipeForInput(firedId));

        var manager = LitPile(out var fires, out var drops);
        Assert.True(fires.TryPlaceFood(0, 0, 0, rawId));
        fires.Update(recipe.CookSeconds, manager, drops);
        Assert.Equal([firedId], fires.CollectFinished(0, 0, 0));
        Assert.Empty(drops.Drops);
        Assert.Null(fires.CookingAt(0, 0, 0));
    }
}
