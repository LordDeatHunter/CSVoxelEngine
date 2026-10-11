using System;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Torch flames: the <c>flame</c> block field (parsed by <see cref="ContentRegistry.ParseFlame"/>,
/// turned with a facing block), the flame points the mesher records per chunk
/// (<see cref="ChunkMeshData.FlamePoints"/>), and the tight, small particles the flame instance of
/// <see cref="FireParticleSystem"/> spawns.
/// </summary>
public sealed class TorchFlameTests
{
    public TorchFlameTests() => TestContent.EnsureLoaded();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void EveryTorchsTopFaceWearsTheCharredTopTile()
    {
        var top = TextureAtlas.TileOf("torch_top");
        foreach (var name in new[] { "torch", "wall_torch" })
        {
            var type = ContentRegistry.RequireBlock(name);
            var up = type.BakedModel!.Quads.Where(q => q.Face == 4).ToArray();
            Assert.NotEmpty(up);
            // The whole tile, so the top never shows the side crop's char band and wood rows
            Assert.All(up, q => Assert.Equal(top, q.Tile));
            Assert.All(up, q => Assert.Equal(0f, q.Uvs.Min(u => MathF.Min(u.X, u.Y))));
            Assert.All(up, q => Assert.Equal(1f, q.Uvs.Max(u => MathF.Max(u.X, u.Y))));
        }
    }

    [Fact]
    public void EveryFlameBurnsJustOverTheTopOfItsOwnModelOnEveryFacing()
    {
        var flaming = BlockType.Types.Values.Where(t => t.FlamePoints != null).ToList();
        Assert.NotEmpty(flaming);
        foreach (var type in flaming)
        {
            var facings = type.HasHorizontalFacing ? 4 : 1;
            for (var facing = 0; facing < facings; facing++)
            {
                var model = type.RotatedModels?[facing] ?? type.BakedModel!;
                var top = model.Quads.SelectMany(q => q.Positions).MaxBy(p => p.Y);
                var flame = type.FlamePointAt((byte)facing)!.Value;
                // Over the head, never inside the stick, and within a pixel and a half of it
                Assert.True(flame.Y >= top.Y, $"{type.Name} facing {facing}: the flame is below its model's top");
                Assert.True(Vector3.Distance(flame, top) < 1.5f / 16f + 1f / 16f,
                    $"{type.Name} facing {facing}: the flame {flame} is off its head {top}");
            }
        }
    }

    [Fact]
    public void AFacingBlocksFlameTurnsWithItAndAPlainBlockKeepsOnePoint()
    {
        var definition = new BlockDefinition { Flame = [8f, 12f, 3f], Facing = "horizontal" };
        var points = ContentRegistry.ParseFlame(definition, BlockRenderer.Model, "test")!;
        Assert.Equal(4, points.Length);
        for (var facing = 0; facing < 4; facing++)
            Assert.Equal(HorizontalFacing.RotatePoint(points[0], facing, 1f), points[facing]);
        // North-authored: the point nearer the front (-Z) moves to the +Z side facing south
        Assert.True(points[1].Z > 0.5f && points[0].Z < 0.5f);

        var plain = ContentRegistry.ParseFlame(new BlockDefinition { Flame = [8f, 10f, 8f] },
            BlockRenderer.Model, "test")!;
        Assert.Single(plain);
        Assert.Null(ContentRegistry.ParseFlame(new BlockDefinition(), BlockRenderer.Model, "test"));
    }

    [Theory]
    [InlineData(BlockRenderer.Pebbles, new float[] { 8f, 8f, 8f })]
    [InlineData(BlockRenderer.Model, new float[] { 8f, 8f })]
    [InlineData(BlockRenderer.Model, new float[] { 8f, 17f, 8f })]
    [InlineData(BlockRenderer.Model, new float[] { -1f, 8f, 8f })]
    public void AFlameIsRefusedOffAModelOrOutsideTheCell(BlockRenderer renderer, float[] flame)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ContentRegistry.ParseFlame(new BlockDefinition { Flame = flame }, renderer, "blocks/x.json"));
        Assert.Contains("blocks/x.json", ex.Message);
    }

    [Fact]
    public void TheMesherRecordsEveryTorchFlameTurnedToItsFacingAndNothingElse()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var stone = Block("stone");
        var torch = Block("torch");
        var wallTorch = BlockType.ById[torch]!.WallBlockId;
        center.SetBlockRaw(4, 4, 4, stone, 0);
        center.SetBlockRaw(4, 5, 4, torch, 0);
        center.SetBlockRaw(9, 6, 9, stone, 0);
        // Facing west: it hangs on the stone to its east
        center.SetBlockRaw(8, 6, 9, wallTorch, 2);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        var flames = mesh.FlamePoints!;
        Assert.Equal(2, flames.Length);
        Assert.Contains(new Vector3(4, 5, 4) + BlockType.ById[torch]!.FlamePointAt(0)!.Value, flames);
        Assert.Contains(new Vector3(8, 6, 9) + BlockType.ById[wallTorch]!.FlamePointAt(2)!.Value, flames);

        var (bareManager, bare) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        bare.SetBlockRaw(4, 4, 4, stone, 0);
        Assert.Null(bare.GenerateMeshData(bareManager)!.FlamePoints);
    }

    [Fact]
    public void ATorchFlameSpawnsTightOnItsPointAndSmallerThanACampfiresParticles()
    {
        const float jitter = 0.06f;
        const float scale = 0.45f;
        var torch = new FireParticleSystem(30f, 10f, 128, Vector3.Zero, jitter, scale);
        var campfire = new FireParticleSystem(30f, 10f, 128, Vector3.Zero);
        var point = new Vector3(3.5f, 10.66f, -2.25f);
        torch.Update(0.05f, [point], point, new Random(5));
        campfire.Update(0.05f, [point], point, new Random(5));

        Assert.NotEmpty(torch.Particles);
        // Spawned this frame and moved by one short step, so they sit within the scatter of the point
        foreach (var particle in torch.Particles)
        {
            Assert.True(MathF.Abs(particle.Position.X - point.X) <= jitter * 0.5f + 0.05f);
            Assert.True(MathF.Abs(particle.Position.Z - point.Z) <= jitter * 0.5f + 0.05f);
        }
        // The same draws spawn the same particles in both, so each torch particle is its campfire
        // twin shrunk
        Assert.Equal(campfire.Particles.Count, torch.Particles.Count);
        for (var i = 0; i < torch.Particles.Count; i++)
            Assert.True(torch.Particles[i].Size < campfire.Particles[i].Size);
    }
}
