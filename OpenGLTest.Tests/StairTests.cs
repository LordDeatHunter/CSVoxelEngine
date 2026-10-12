using System;
using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Stairs take their facing from the placing player's look (<c>facingFromLook</c>): the four look
/// directions give the four facings whatever face was hit, the step rises the way the placer was
/// looking, and a server replaying a placement faces it by that player's own look.
/// </summary>
[Collection("Worldgen seed")]
public sealed class StairTests : IDisposable
{
    private SupportTestWorld? _world;

    public StairTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        _world?.Dispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static BlockType[] LookFaced() =>
        BlockType.Types.Values.Where(t => t.FacingFromLook).ToArray();

    private static readonly Vector3[] HitNormals =
        [Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitX];

    [Fact]
    public void TheFourLookDirectionsFaceTheFourFacings()
    {
        var stairs = LookFaced();
        Assert.NotEmpty(stairs);
        for (var facing = 0; facing < 4; facing++)
        {
            var flat = Chunk.CubeFaces[facing].normal;
            // Looking down at the floor, or a little up, still faces the way the player is turned
            foreach (var look in new[] { flat, Vector3.Normalize(flat - Vector3.UnitY * 1.5f), Vector3.Normalize(flat + Vector3.UnitY * 0.3f) })
            {
                Assert.Equal(facing, Placement.LookFacing(look));
                foreach (var stair in stairs)
                foreach (var normal in HitNormals)
                {
                    Assert.True(Placement.TryMount(stair.Id, 0, normal, fillsHitCell: false, look, out var id, out var state));
                    Assert.Equal((stair.Id, facing), (id, state & 3));
                }
            }
        }

        // A block that does not face its look keeps the state it was given on every face
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.True(Placement.TryMount(stone, 0, Vector3.UnitY, false, Vector3.UnitX, out _, out var stoneState));
        Assert.Equal(0, stoneState);
    }

    [Fact]
    public void AStairRisesTheWayItsPlacerLooks()
    {
        // A stair: a look-faced block the body climbs, a box of it standing above the half line
        foreach (var stair in LookFaced().Where(t => t.CollidesByHitbox && t.Hitboxes.Any(b => b.Min.Y >= 0.5f - 1e-4f)))
        for (var facing = 0; facing < 4; facing++)
        {
            var look = Chunk.CubeFaces[facing].normal;
            Assert.True(Placement.TryMount(stair.Id, 0, Vector3.UnitY, false, look, out _, out var state));
            var boxes = stair.RotatedHitboxes![state & 3];

            // The body stands on a floor-wide lower half, and the step above it lies ahead
            Assert.Contains(boxes, b => b.Min == Vector3.Zero && b.Max is { X: 1f, Z: 1f } && b.Max.Y <= 0.5f);
            var steps = boxes.Where(b => b.Min.Y >= 0.5f - 1e-4f).ToArray();
            Assert.NotEmpty(steps);
            foreach (var step in steps)
                Assert.True(Vector3.Dot(step.Center - new Vector3(0.5f), look) > 0f,
                    $"{stair.Name} facing {facing}: the step {step} is not ahead of the look {look}");

            // And the model's top lies over that step
            var top = stair.RotatedModels![state & 3].Quads.Where(q => q.Face == 4 && q.Positions.All(p => p.Y > 1f - 1e-4f));
            Assert.All(top, q => Assert.True(Vector3.Dot(q.Positions.Aggregate(Vector3.Add) / 4f - new Vector3(0.5f), look) > 0f));
        }
    }

    [Fact]
    public void TheRegistryRefusesALookFacingItCannotHonour()
    {
        const string path = "data/blocks/x.json";
        var look = new BlockDefinition { FacingFromLook = true };
        Assert.True(ContentRegistry.ParseFacingFromLook(look, horizontalFacing: true, supportAttached: false, path));
        Assert.False(ContentRegistry.ParseFacingFromLook(new BlockDefinition(), true, false, path));
        // No facing to turn, or a facing that already names a wall
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            ContentRegistry.ParseFacingFromLook(look, horizontalFacing: false, supportAttached: false, path)).Message);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            ContentRegistry.ParseFacingFromLook(look, horizontalFacing: true, supportAttached: true, path)).Message);
    }

    [Fact]
    public void APlacementFacesTheStairByThePlacingPlayersOwnLook()
    {
        _world = new SupportTestWorld();
        var stair = LookFaced().First().Id;
        var ground = _world.Top;
        _world.Load(4, ground, 4);

        for (var facing = 0; facing < 4; facing++)
        {
            var (x, z) = (2 + 2 * facing, 4);
            _world.Player.Context.ViewDirection = Vector3.Normalize(Chunk.CubeFaces[facing].normal - Vector3.UnitY);
            Assert.True(_world.Place(stair, new Vector3(x, ground, z), Vector3.UnitY));
            Assert.Equal(stair, _world.IdAt(x, ground + 1, z));
            Assert.Equal(facing, _world.StateAt(x, ground + 1, z) & 3);
        }
    }
}
