using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Interaction;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Chunks.Meshing;

/// <summary>
/// The permanent chop damage of natural trunk cells: how much of the face it eats, the mesh it draws, that
/// it is kept (and saved) for natural wood only, and what clears it (<see cref="WoodDamage"/>).
/// </summary>
[Collection("Worldgen seed")]
public sealed class ChoppedWoodTests : IDisposable
{
    private const float Dt = 1f / 60f;
    private static readonly byte Natural = WoodState.Encode(WoodState.FullCube, WoodState.ParentRoot, false);

    private World? _world;

    public ChoppedWoodTests() => TestContent.EnsureLoaded();

    public void Dispose()
    {
        _world?.SaveAndDispose();
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Fact]
    public void TheSlitDeepensWithTheDamageAndIsAtItsDeepestOnlyAtTheBreak()
    {
        Assert.Equal(0, WoodDamage.BiteDepthPx(0f, 24f));
        Assert.Equal(0, WoodDamage.BiteDepthPx(5f, 0f));

        var previous = 0;
        for (var damage = 0.05f; damage < 24f; damage += 0.25f)
        {
            var depth = WoodDamage.BiteDepthPx(damage, 24f);
            Assert.True(depth >= previous);
            Assert.InRange(depth, 1, WoodDamage.MaxDepthPx);
            previous = depth;
        }

        // Any damage already shows, and the deepest slit is only for the damage that breaks the block
        Assert.True(WoodDamage.BiteDepthPx(0.01f, 24f) >= 1);
        Assert.True(WoodDamage.BiteDepthPx(23.9f, 24f) <= WoodDamage.MaxDepthPx);
        Assert.Equal(WoodDamage.MaxDepthPx, WoodDamage.BiteDepthPx(24f, 24f));
    }

    [Fact]
    public void TheStoreKeepsTheFirstSideFaceAndTheLatestTotalAndForgetsOnClear()
    {
        var damage = new WoodDamage();
        var version = damage.Version;
        Assert.True(damage.Set(1, 2, 3, 2f, face: WoodDamage.NoFace));
        Assert.True(damage.Version != version);

        // A side face fills in the missing one; a later face does not move it
        Assert.True(damage.Set(1, 2, 3, 3f, face: 2));
        Assert.False(damage.Set(1, 2, 3, 3f, face: 2));
        Assert.True(damage.Set(1, 2, 3, 4f, face: 0));
        Assert.True(damage.TryGet(1, 2, 3, out var entry));
        Assert.Equal((4f, 2), (entry.Damage, entry.Face));

        Assert.True(damage.Clear(1, 2, 3));
        Assert.False(damage.TryGet(1, 2, 3, out _));
        Assert.False(damage.Clear(1, 2, 3));
        Assert.True(damage.Set(1, 2, 3, 1f, 1));
        Assert.True(damage.Set(1, 2, 3, 0f, 1));
        Assert.True(damage.IsEmpty);
    }

    // Signed volume of the closed surface the mesh draws for the cell at (8, 8, 8), by the divergence theorem
    private static float Volume(ChunkMeshData mesh)
    {
        var sum = 0f;
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var a = mesh.Vertices[mesh.Indices[i]].Position - new Vector3(8.5f, 8.5f, 8.5f);
            var b = mesh.Vertices[mesh.Indices[i + 1]].Position - new Vector3(8.5f, 8.5f, 8.5f);
            var c = mesh.Vertices[mesh.Indices[i + 2]].Position - new Vector3(8.5f, 8.5f, 8.5f);
            sum += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
        }
        return sum;
    }

    private static (ChunkManager Manager, Chunk Center) LogInAir(byte state)
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, ContentRegistry.RequireBlock("oak_log").Id, state);
        return (manager, center);
    }

    private static float Hardness() => Mining.HardnessOf(ContentRegistry.RequireBlock("oak_log").Id, Natural);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AChoppedFaceIsACubeWithAClosedFullWidthSlitThatOnlyDeepensAndNeverSeesThrough(int face)
    {
        var (manager, center) = LogInAir(Natural);
        var whole = Volume(center.GenerateMeshData(manager)!);
        Assert.NotEqual(0f, whole);

        var previous = 1f;
        var firstTaken = 0f;
        foreach (var depth in new[] { 1, 2, 4, 8, 12, WoodDamage.MaxDepthPx })
        {
            // Damage that cuts exactly this deep
            var damage = Hardness() * (depth - 0.5f) / WoodDamage.MaxDepthPx;
            manager.ChopDamage.Set(8, 8, 8, damage, face);
            var ratio = Volume(center.GenerateMeshData(manager)!) / whole;

            // A closed surface that has lost a box: less volume as the slit deepens, never nothing, and the
            // volume taken out is in proportion to the depth (the slit is as wide and as tall at every depth)
            Assert.True(ratio < previous, $"depth {depth} did not remove more than the cut before");
            Assert.True(ratio > 0.5f);
            if (depth == 1) firstTaken = 1f - ratio;
            Assert.InRange(1f - ratio, firstTaken * depth - 0.005f, firstTaken * depth + 0.005f);
            previous = ratio;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AChoppedThinCellLosesMoreWoodAsItDeepensAndStaysClosed(int face)
    {
        var thin = WoodState.Encode(5, WoodState.ParentRoot, false);
        var (manager, center) = LogInAir(thin);
        var whole = Volume(center.GenerateMeshData(manager)!);
        Assert.True(whole > 0f);

        var hardness = Mining.HardnessOf(ContentRegistry.RequireBlock("oak_log").Id, thin);
        var previous = 1f;
        foreach (var depth in new[] { 1, 4, 8, 12, WoodDamage.MaxDepthPx })
        {
            manager.ChopDamage.Set(8, 8, 8, hardness * (depth - 0.5f) / WoodDamage.MaxDepthPx, face);
            var ratio = Volume(center.GenerateMeshData(manager)!) / whole;
            // A closed surface that keeps a core of the node: less wood with every depth, never none
            Assert.True(ratio < previous, $"depth {depth} did not remove more than the cut before");
            Assert.True(ratio > 0.2f);
            previous = ratio;
        }
    }

    [Fact]
    public void OnlyNaturalWoodWithASideBiteIsDrawnEaten()
    {
        var (manager, center) = LogInAir(WoodState.Detached);
        var plain = center.GenerateMeshData(manager)!.Vertices.Count;
        manager.ChopDamage.Set(8, 8, 8, Hardness() * 0.5f, 1);
        Assert.Equal(plain, center.GenerateMeshData(manager)!.Vertices.Count);

        // A natural cell hit only on its top or bottom has damage but no bite to draw
        var (topManager, topCenter) = LogInAir(Natural);
        var whole = topCenter.GenerateMeshData(topManager)!.Vertices.Count;
        topManager.ChopDamage.Set(8, 8, 8, Hardness() * 0.5f, WoodDamage.NoFace);
        Assert.Equal(whole, topCenter.GenerateMeshData(topManager)!.Vertices.Count);

        topManager.ChopDamage.Set(8, 8, 8, Hardness() * 0.5f, 1);
        Assert.True(topCenter.GenerateMeshData(topManager)!.Vertices.Count > whole);
    }

    [Fact]
    public void BreakingTheCellForgetsItsDamage()
    {
        var (manager, center) = LogInAir(Natural);
        manager.ChopDamage.Set(8, 8, 8, 3f, 1);
        Assert.True(center.RemoveBlockAt(new Vector3(8f, 8f, 8f), manager));
        Assert.False(manager.ChopDamage.TryGet(8, 8, 8, out _));
    }

    [Fact]
    public void TheSaveReturnsTheSameDamageAndAnythingElseReadsAsNone()
    {
        var damage = new WoodDamage();
        damage.Set(-5, 70, 12, 2.5f, 3);
        damage.Set(100, -4, -90, 7f, WoodDamage.NoFace);

        var parsed = WoodDamageCodec.TryParse(WoodDamageCodec.ToBytes(damage.Export()));
        Assert.NotNull(parsed);
        var restored = new WoodDamage();
        restored.Import(parsed);
        Assert.Equal(damage.Export().OrderBy(e => e.Key), restored.Export().OrderBy(e => e.Key));

        Assert.Null(WoodDamageCodec.TryParse(null));
        Assert.Null(WoodDamageCodec.TryParse([1, 2, 3]));
        var bytes = WoodDamageCodec.ToBytes(damage.Export());
        Assert.Null(WoodDamageCodec.TryParse(bytes[..(bytes.Length - 4)]));
    }

    // A storeless survival world with a log two blocks in front of the eye (up in open air, facing -Z)
    private (World World, Vector3 Cell) WorldWithLog(byte state)
    {
        WorldSeed.Apply(WorldSeed.Default);
        _world = new World(null!, null!, null!, null!, null!, null!, null, null);
        var eye = new Vector3(0.5f, 300.5f, 0.5f);
        _world.Camera.TestTeleport(eye, yaw: -90f, pitch: 0f);
        var cell = new Vector3(0f, 300f, -3f);
        var manager = _world.GetChunkManager();
        var chunkPos = VoxelCoords.ChunkPosOf(cell);
        // Nothing generates up there, so the chunk holding the log is ours
        var chunk = new Chunk(null!, chunkPos, generateBlockData: false) { IsBlockDataReady = true };
        manager.Chunks[chunkPos] = chunk;
        chunk.SetBlockRaw(0, (int)cell.Y - (int)chunkPos.Y * Chunk.Size, (int)cell.Z - (int)chunkPos.Z * Chunk.Size,
            ContentRegistry.RequireBlock("oak_log").Id, state);

        // Logs need an axe to be hurt at all
        var axe = ContentRegistry.RequireItem("primitive_axe");
        Assert.True(_world.Inventory.Add(axe.Id));
        _world.Inventory.Select(0);
        return (_world, cell);
    }

    private static void Chop(World world, int frames, bool holding)
    {
        for (var i = 0; i < frames; i++)
        {
            world.LeftClick.UpdateSelection();
            world.LeftClick.UpdateBlockBreaking(Dt, holding);
        }
    }

    [Fact]
    public void ANaturalTrunkKeepsItsDamageAcrossReleasingAndChoppingAgain()
    {
        var (world, cell) = WorldWithLog(Natural);
        var damage = world.GetChunkManager().ChopDamage;

        Chop(world, 40, holding: true);
        Assert.True(damage.TryGet((int)cell.X, (int)cell.Y, (int)cell.Z, out var first), "the hits left no damage");
        Assert.True(first.Damage > 0f);
        Assert.NotEqual(WoodDamage.NoFace, first.Face);

        // Letting go does not heal it, and starting again carries on from it
        Chop(world, 3, holding: false);
        Assert.True(damage.TryGet((int)cell.X, (int)cell.Y, (int)cell.Z, out var kept));
        Assert.Equal(first.Damage, kept.Damage);

        Chop(world, 1, holding: true);
        Assert.True(world.LeftClick.BreakProgress >= first.Damage / Hardness() - 1e-4f);
        Chop(world, 40, holding: true);
        damage.TryGet((int)cell.X, (int)cell.Y, (int)cell.Z, out var more);
        Assert.True(more.Damage > first.Damage);
    }

    [Fact]
    public void APlacedLogKeepsNoDamageAndStartsOverEachTime()
    {
        var (world, cell) = WorldWithLog(WoodState.Detached);
        var damage = world.GetChunkManager().ChopDamage;

        world.LeftClick.UpdateSelection();
        Assert.True(world.LeftClick.HasSelectedBlock, "no block under the crosshair");
        Assert.False(world.Camera.IsCreative, "creative breaks at once");
        Chop(world, 40, holding: true);
        Assert.True(world.LeftClick.BreakProgress > 0f);
        Assert.True(damage.IsEmpty);

        Chop(world, 3, holding: false);
        Assert.Equal(0f, world.LeftClick.BreakProgress);
    }
}
