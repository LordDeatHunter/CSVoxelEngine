using System.Collections.Generic;
using System.Linq;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Rooted plants keep their roots: a swaying block whose model is not a full cube (tall grass,
/// a shrub, a flower) carries a per-vertex sway weight that eases from nothing at the model's
/// own base to full at its own top, while a swaying full cube (canopy leaves) keeps the uniform
/// full sway it always had. The weight rides the vertex alpha of a swaying vertex
/// (<see cref="Vertex.SwayWeight"/>).
/// </summary>
public sealed class PlantSwayTests
{
    public PlantSwayTests() => TestContent.EnsureLoaded();

    private static IEnumerable<BlockType> SwayingBlocks(bool rooted) =>
        BlockType.Types.Values.Where(t => t.Sway && t.Renderer == BlockRenderer.Model
                                          && t.BakedModel is { } m && m.IsFullCube != rooted);

    private static List<Vertex> MeshSingle(BlockType type, bool greedy)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        center.SetBlockRaw(8, 8, 8, type.Id, 0);
        var mesh = center.GenerateMeshData(manager, greedy);
        Assert.NotNull(mesh);
        var vertices = mesh.Vertices.Concat(mesh.TranslucentVertices).ToList();
        Assert.NotEmpty(vertices);
        return vertices;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARootedPlantSwaysFromNothingAtItsBaseToFullAtItsTip(bool greedy)
    {
        var plants = SwayingBlocks(rooted: true).ToList();
        Assert.NotEmpty(plants);
        foreach (var plant in plants)
        {
            Assert.True(plant.SwaysFromRoot, $"{plant.Name} is a swaying sub-cell model but not rooted");
            var vertices = MeshSingle(plant, greedy);
            Assert.All(vertices, v => Assert.True(v.IsSwaying, $"{plant.Name} vertex at {v.Position} lost its sway"));

            var baseY = vertices.Min(v => v.Position.Y);
            var tipY = vertices.Max(v => v.Position.Y);
            Assert.True(tipY > baseY, $"{plant.Name} has no height to sway over");
            Assert.All(vertices.Where(v => v.Position.Y == baseY),
                v => Assert.Equal(0f, v.SwayWeight));
            Assert.All(vertices.Where(v => v.Position.Y == tipY),
                v => Assert.Equal(1f, v.SwayWeight));

            // Never less sway higher up the plant
            var byHeight = vertices.OrderBy(v => v.Position.Y).ToList();
            for (var i = 1; i < byHeight.Count; i++)
                Assert.True(byHeight[i].SwayWeight >= byHeight[i - 1].SwayWeight,
                    $"{plant.Name}: weight drops between y {byHeight[i - 1].Position.Y} and {byHeight[i].Position.Y}");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanopyCubesKeepTheirUniformFullSway(bool greedy)
    {
        var canopy = SwayingBlocks(rooted: false).ToList();
        Assert.NotEmpty(canopy);
        foreach (var leaves in canopy)
        {
            Assert.False(leaves.SwaysFromRoot, $"{leaves.Name} is a full cube and must not be rooted");
            Assert.All(MeshSingle(leaves, greedy), v =>
            {
                Assert.True(v.IsSwaying, $"{leaves.Name} vertex at {v.Position} lost its sway");
                Assert.Equal(1f, v.SwayWeight);
            });
        }
    }

    [Fact]
    public void ABlockThatDoesNotSwayCarriesNoWeight()
    {
        var stone = ContentRegistry.RequireBlock("stone");
        Assert.All(MeshSingle(stone, greedy: false), v => Assert.Equal(0f, v.SwayWeight));
    }

    [Fact]
    public void TheLowerHalfOfAPlantBarelyMoves()
    {
        foreach (var plant in SwayingBlocks(rooted: true))
        {
            var mid = (plant.SwayBaseY + plant.SwayTopY) * 0.5f;
            // Eased, not linear: below half sway at half height, and rising all the way up
            Assert.True(plant.SwayWeightAt(mid) < 0.5f, $"{plant.Name} sways linearly");
            var previous = -1f;
            for (var i = 0; i <= 32; i++)
            {
                var w = plant.SwayWeightAt(plant.SwayBaseY + (plant.SwayTopY - plant.SwayBaseY) * i / 32f);
                Assert.True(w >= previous, $"{plant.Name}: weight drops at step {i}");
                previous = w;
            }
        }
    }

    [Fact]
    public void ASwayWeightNeverWearsTheFluidOrLodFoliageAlpha()
    {
        var previous = -1;
        for (var i = 0; i <= 10000; i++)
        {
            var a = Vertex.EncodeSwayWeight(i / 10000f);
            Assert.NotEqual(254, a);
            Assert.NotEqual(253, a);
            Assert.True(a >= previous, $"encoding drops at weight {i / 10000f}");
            previous = a;
        }

        Assert.Equal(0, Vertex.EncodeSwayWeight(0f));
        Assert.Equal(255, Vertex.EncodeSwayWeight(1f));
    }
}
