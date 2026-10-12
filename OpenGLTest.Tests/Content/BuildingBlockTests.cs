using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The construction pieces (slabs and the other partial building blocks) asserted through the
/// baked tables: a block the body collides with by its boxes is drawn exactly as far as those
/// boxes reach, on every facing it can take, so what the player stands on is what they see, and a
/// floor slab is stood on at its top rather than walling its cell off.
/// </summary>
public sealed class BuildingBlockTests
{
    public BuildingBlockTests() => TestContent.EnsureLoaded();

    private static (Vector3 Min, Vector3 Max) Bounds(BakedModel model)
    {
        var positions = model.Quads.SelectMany(q => q.Positions).ToArray();
        return (positions.Aggregate(Vector3.Min), positions.Aggregate(Vector3.Max));
    }

    private static (Vector3 Min, Vector3 Max) Bounds(BlockHitbox[] boxes) =>
        (boxes.Select(b => b.Min).Aggregate(Vector3.Min), boxes.Select(b => b.Max).Aggregate(Vector3.Max));

    [Fact]
    public void EveryShapeCollidingModelIsDrawnExactlyToItsBoxes()
    {
        var checkedAny = false;
        foreach (var type in BlockType.Types.Values)
        {
            if (!type.CollidesByHitbox || type.Renderer != BlockRenderer.Model) continue;
            checkedAny = true;
            var models = type.RotatedModels ?? [type.BakedModel!];
            var boxes = type.RotatedHitboxes ?? [type.Hitboxes];
            Assert.Equal(models.Length, boxes.Length);
            for (var facing = 0; facing < models.Length; facing++)
            {
                var (drawnMin, drawnMax) = Bounds(models[facing]);
                var (boxMin, boxMax) = Bounds(boxes[facing]);
                Assert.True(Vector3.Distance(drawnMin, boxMin) < 1e-4f && Vector3.Distance(drawnMax, boxMax) < 1e-4f,
                    $"{type.Name} facing {facing}: drawn {drawnMin}..{drawnMax}, collides with {boxMin}..{boxMax}");
            }
        }
        Assert.True(checkedAny);
    }

    [Fact]
    public void AFloorSlabIsStoodOnAtItsTopNotWalledOff()
    {
        // Every block whose one box is a floor-standing slab spanning its cell (a slab, a mold)
        var slabs = BlockType.Types.Values
            .Where(t => t.Renderer == BlockRenderer.Model && t.CollidesByHitbox && t.Hitboxes.Length == 1
                        && t.Hitboxes[0].Min == Vector3.Zero && t.Hitboxes[0].Max is { X: 1f, Z: 1f, Y: < 1f })
            .ToArray();
        Assert.NotEmpty(slabs);

        foreach (var slab in slabs)
        {
            var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
            var origin = center.Position * Chunk.Size;
            center.SetBlockRaw(8, 4, 8, slab.Id, 0);
            var top = origin.Y + 4 + slab.Hitboxes[0].Max.Y;
            var column = new Vector3(origin.X + 8.5f, 0f, origin.Z + 8.5f);

            // Standing on the slab's top is clear, sinking into it is not
            Assert.False(PlayerPhysics.BodyCollides(column + new Vector3(0f, top + 0.01f + Camera.EyeHeight, 0f), manager),
                $"a body standing on {slab.Name} collides with it");
            Assert.True(PlayerPhysics.BodyCollides(column + new Vector3(0f, top - 0.1f + Camera.EyeHeight, 0f), manager),
                $"a body sunk into {slab.Name} does not collide with it");
        }
    }
}
