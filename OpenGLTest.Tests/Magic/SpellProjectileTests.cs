using System.Numerics;
using VoxelEngine.Content.Spells;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Slimes;
using VoxelEngine.Magic;
using VoxelEngine.Player;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>A spell bolt's flight: straight, as long as its range factor says, stopped by walls, fluids and mobs.</summary>
public sealed class SpellProjectileTests
{
    public SpellProjectileTests() => TestContent.EnsureLoaded();

    private static SpellStats Stats(float range = 1f) => new(SpellVerdict.Valid, 0f, 0f, 0f, 1f, range);

    private static SpellProjectile Bolt(Vector3 eye, Vector3 direction, float range = 1f) =>
        new(new PlayerContext(Inventory.Empty()), SpellPartRegistry.Require(SpellPartKind.Element, "fire"), Stats(range),
            eye, direction);

    private static SpellFlight Fly(SpellProjectile bolt, ChunkManager chunks, Mob[]? mobs = null)
    {
        var flight = SpellFlight.Flying;
        for (var i = 0; i < 1200 && flight == SpellFlight.Flying; i++)
            flight = bolt.Update(1f / 60f, chunks, mobs);
        return flight;
    }

    [Fact]
    public void ALargerRangeFactorFliesFartherBeforeItIsSpent()
    {
        var chunks = MobTestWorld.Floor(ContentRegistry.RequireBlock("stone").Id);
        var eye = new Vector3(1f, 3.5f, 8.5f);
        var near = Bolt(eye, Vector3.UnitX);
        var far = Bolt(eye, Vector3.UnitX, range: 2f);

        Assert.Equal(SpellFlight.Spent, Fly(near, chunks));
        Assert.Equal(SpellFlight.Spent, Fly(far, chunks));
        Assert.True(far.Traveled > near.Traveled);
        Assert.True(Vector3.Distance(far.Position, far.Origin) > Vector3.Distance(near.Position, near.Origin));
        // It flies straight: no gravity bends it down onto the floor
        Assert.Equal(near.Origin.Y, near.Position.Y, 3);
    }

    [Fact]
    public void ABoltStopsAtAWallWithTheWallAsItsImpactCell()
    {
        var (chunks, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(5, 0, 0, ContentRegistry.RequireBlock("stone").Id, 0);
        var bolt = Bolt(new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX);

        Assert.Equal(SpellFlight.HitCell, Fly(bolt, chunks));
        var cell = bolt.ImpactCell;
        Assert.True(BlockType.IsSolid(chunks.GetBlockGlobal((int)cell.X, (int)cell.Y, (int)cell.Z)));
        // The face it landed on looks back toward where the bolt came from
        Assert.True(Vector3.Dot(bolt.ImpactNormal, bolt.Origin - (cell + new Vector3(0.5f))) > 0f);
    }

    [Fact]
    public void ABoltStopsAtTheFirstFluidCell()
    {
        var chunks = MobTestWorld.Pool(3);
        var bolt = Bolt(new Vector3(8.5f, 10f, 8.5f), -Vector3.UnitY);

        Assert.Equal(SpellFlight.HitCell, Fly(bolt, chunks));
        var cell = bolt.ImpactCell;
        Assert.True(BlockType.IsFluid(chunks.GetBlockGlobal((int)cell.X, (int)cell.Y, (int)cell.Z)));
        // The topmost water cell, above the floor, facing up toward the caster
        Assert.True(BlockType.IsFluid(chunks.GetBlockGlobal((int)cell.X, (int)cell.Y - 1, (int)cell.Z)));
        Assert.False(BlockType.IsFluid(chunks.GetBlockGlobal((int)cell.X, (int)cell.Y + 1, (int)cell.Z)));
        Assert.Equal(Vector3.UnitY, bolt.ImpactNormal);
    }

    [Fact]
    public void ABoltHitsAMobInItsPath()
    {
        var (chunks, _) = MeshTestWorld.WalledArea();
        var slime = new Slime(EntityRegistry.Require("slime"), new Vector3(4f, 0.5f, 0.5f));
        var bolt = Bolt(new Vector3(0.5f, 0.5f, 0.5f), Vector3.UnitX);

        Assert.Equal(SpellFlight.HitMob, Fly(bolt, chunks, [slime]));
        Assert.Same(slime, bolt.HitMob);
    }
}
