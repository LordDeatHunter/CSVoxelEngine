using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Magic;
using VoxelEngine.Particles;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>
/// A casting client's cosmetic preview bolt (<see cref="SpellBoltPreview"/>): it flies the real bolt's line
/// and lands where that one would, but never edits a cell or hurts a mob. Bare chunk fixtures stand in for
/// the client's streamed chunks, since the preview reads nothing else of a world.
/// </summary>
public sealed class SpellBoltPreviewTests
{
    public SpellBoltPreviewTests() => TestContent.EnsureLoaded();

    private static readonly SpellPart Fire = SpellPartRegistry.Require(SpellPartKind.Element, "fire");

    private static SpellStats Stats() =>
        new SpellRecipe([Fire, SpellPartRegistry.Require(SpellPartKind.Shape, "projectile")]).Compile();

    // Flies every preview bolt until none is left and returns where they landed
    private static List<Vector3> Fly(SpellBoltPreview preview, ChunkManager chunks, Mob[]? mobs = null)
    {
        var debris = new BlockDebrisSystem();
        var landings = new List<Vector3>();
        for (var i = 0; i < 1200 && preview.Bolts.Count > 0; i++)
            preview.Update(1f / 60f, chunks, mobs, debris, landings);
        Assert.Empty(preview.Bolts);
        return landings;
    }

    [Fact]
    public void APreviewBoltLandsAtTheFirstBlockInItsLineAndEditsNothing()
    {
        var (chunks, center) = MeshTestWorld.WalledArea();
        var tnt = ContentRegistry.RequireBlock("tnt").Id;
        center.SetBlockRaw(5, 0, 0, tnt, 0);
        var preview = new SpellBoltPreview();
        preview.Launch(new PlayerContext(Inventory.Empty()), Fire, Stats(), new Vector3(0.5f, 0.5f, 0.5f),
            Vector3.UnitX, SpellSparks.ColorOf(Fire));

        var landing = Assert.Single(Fly(preview, chunks));

        // It stopped against the TNT cell, which a real fire bolt would have primed
        Assert.InRange(landing.X, 4f, 5f);
        Assert.Equal(tnt, chunks.GetBlockGlobal(5, 0, 0));
    }

    [Fact]
    public void APreviewBoltIntoOpenAirIsSpentWithNoLanding()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var preview = new SpellBoltPreview();
        preview.Launch(new PlayerContext(Inventory.Empty()), Fire, Stats(), new Vector3(1f, 3.5f, 8.5f),
            Vector3.UnitX, SpellSparks.ColorOf(Fire));

        Assert.Empty(Fly(preview, chunks));
    }

    [Fact]
    public void APreviewBoltStopsAtAMobWithoutHurtingIt()
    {
        var (chunks, _) = MeshTestWorld.WalledArea();
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(4f, 0.5f, 0.5f));
        var health = slime.Health;
        var preview = new SpellBoltPreview();
        preview.Launch(new PlayerContext(Inventory.Empty()), Fire, Stats(), new Vector3(0.5f, 0.5f, 0.5f),
            Vector3.UnitX, SpellSparks.ColorOf(Fire));

        var landing = Assert.Single(Fly(preview, chunks, [slime]));

        Assert.True(Vector3.Distance(landing, slime.Position) < 1.5f);
        Assert.Equal(health, slime.Health);
        Assert.False(slime.Effects.IsActive(StatusEffectRegistry.Require(Fire.Effect!)));
    }
}
