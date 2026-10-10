using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The torch is a placeable light source that needs the block it stands on: its light spreads
/// from its own cell, and it pops with its loot when that block goes.
/// </summary>
[Collection("Worldgen seed")]
public sealed class TorchTests : IDisposable
{
    private const int FloorY = 4;

    private readonly byte _torch;
    private readonly byte _stone;
    private SupportTestWorld? _world;

    public TorchTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
        _torch = ContentRegistry.RequireBlock("torch").Id;
        _stone = ContentRegistry.RequireBlock("stone").Id;
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

    private static Vector3 WorldCentre(Chunk chunk, int x, int y, int z) =>
        chunk.Position * Chunk.Size + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);

    // Block light at three cells along the floor row, nearest the torch's column first
    private (float Near, float Mid, float Far) FloorRowLight(bool withTorch)
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            center.SetBlockRaw(x, FloorY, z, _stone, 0);
        if (withTorch)
            center.SetBlockRaw(2, FloorY + 1, 8, _torch, 0);

        var mesh = center.GenerateMeshData(manager);
        Assert.NotNull(mesh);
        center.PublishLight(mesh!.Light);

        return (EntityLight.Sample(manager, WorldCentre(center, 3, FloorY + 1, 8)).Block,
            EntityLight.Sample(manager, WorldCentre(center, 8, FloorY + 1, 8)).Block,
            EntityLight.Sample(manager, WorldCentre(center, 14, FloorY + 1, 8)).Block);
    }

    [Fact]
    public void ATorchLightsTheCellsAroundItAndTheLightFallsOff()
    {
        var dark = FloorRowLight(withTorch: false);
        Assert.Equal(0f, dark.Near);

        var lit = FloorRowLight(withTorch: true);
        Assert.True(lit.Near > 0f, "no block light beside the torch");
        Assert.True(lit.Near > lit.Mid && lit.Mid >= lit.Far, "the torch light does not fall off with distance");
    }

    [Fact]
    public void BreakingTheBlockUnderATorchPopsIt()
    {
        _world = new SupportTestWorld();
        var (x, z) = (4, 4);
        var ground = _world.Top;
        _world.Load(x, ground, z);

        Assert.True(_world.Place(_torch, new Vector3(x, ground, z), Vector3.UnitY));
        Assert.Equal(_torch, _world.IdAt(x, ground + 1, z));

        // Mining a neighbouring ground block leaves it standing
        Assert.True(_world.Break(x + 1, ground, z));
        Assert.Equal(_torch, _world.IdAt(x, ground + 1, z));

        // The block it stands on takes it down
        Assert.True(_world.Break(x, ground, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ground + 1, z));
    }

    private static readonly Vector3[] SideNormals = [-Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitX];

    [Fact]
    public void TheHitFacePicksTheFloorOrTheWallTorchAndItsFacing()
    {
        var wall = BlockType.ById[_torch]!.WallBlockId;
        Assert.NotEqual(0, wall);

        // On top: the floor torch, standing on the block that was hit
        Assert.True(Placement.TryMount(_torch, 0, Vector3.UnitY, fillsHitCell: false, out var id, out var state));
        Assert.Equal(_torch, id);
        var hit = new Vector3(5, 7, 9);
        Assert.True(BlockSupport.TryGetSupportCell(id, state, hit + Vector3.UnitY, out var support));
        Assert.Equal(hit, support);

        // On a side: the wall torch, hanging on the block that was hit and facing away from it
        foreach (var normal in SideNormals)
        {
            Assert.True(Placement.TryMount(_torch, 0, normal, fillsHitCell: false, out id, out state));
            Assert.Equal(wall, id);
            Assert.Equal(normal, Chunk.CubeFaces[state & 3].normal);
            Assert.True(BlockSupport.TryGetSupportCell(id, state, hit + normal, out support));
            Assert.Equal(hit, support);
        }

        // Under a ceiling there is nothing to hang from
        Assert.False(Placement.TryMount(_torch, 0, -Vector3.UnitY, fillsHitCell: false, out _, out _));

        // Taking over the hit cell itself (a snow drift) stands on the floor whatever the face
        Assert.True(Placement.TryMount(_torch, 0, Vector3.UnitX, fillsHitCell: true, out id, out _));
        Assert.Equal(_torch, id);

        // A block without a wall variant places as itself with its own state on every face
        foreach (var normal in SideNormals)
        {
            Assert.True(Placement.TryMount(_stone, 3, normal, fillsHitCell: false, out id, out state));
            Assert.Equal((_stone, (byte)3), (id, state));
        }
    }

    [Fact]
    public void TurningTheNorthFrontByAFacingPointsItAlongThatFacing()
    {
        // The facing a wall torch is given is its normal's face index, and its model and hitbox are
        // authored with the front on north: the turn by that facing must land the front on it
        for (var facing = 0; facing < 4; facing++)
        {
            var front = HorizontalFacing.RotateDirection(Chunk.CubeFaces[0].normal, facing);
            Assert.Equal(Chunk.CubeFaces[facing].normal, front);
            Assert.Equal(facing, HorizontalFacing.FacingOf(front));
        }
    }

    [Fact]
    public void AWallTorchLeansAwayFromItsWallOnEveryFacing()
    {
        var wall = BlockType.ById[BlockType.ById[_torch]!.WallBlockId]!;
        Assert.NotNull(wall.RotatedModels);
        Assert.NotNull(wall.RotatedHitboxes);
        for (var facing = 0; facing < 4; facing++)
        {
            var front = Chunk.CubeFaces[facing].normal;

            // The selection box stays in the cell and reaches the face the torch hangs on
            foreach (var box in wall.RotatedHitboxes![facing])
            {
                Assert.True(Vector3.Min(box.Min, Vector3.Zero) == Vector3.Zero && Vector3.Max(box.Max, Vector3.One) == Vector3.One,
                    $"facing {facing}: box {box} leaves its cell");
                // How far the box reaches from the cell centre toward the wall (behind the front)
                var reach = Vector3.Dot(-front, box.Center - new Vector3(0.5f)) + Vector3.Dot(Vector3.Abs(front), box.Size * 0.5f);
                Assert.True(MathF.Abs(reach - 0.5f) < 1e-4f, $"facing {facing}: box {box} does not reach the wall");
            }

            // The flame end sits further out from the wall than the foot
            var positions = wall.RotatedModels![facing].Quads.SelectMany(q => q.Positions).ToArray();
            var top = positions.Max(p => p.Y);
            var bottom = positions.Min(p => p.Y);
            var topOut = positions.Where(p => p.Y > top - 0.05f).Average(p => Vector3.Dot(p, front));
            var footOut = positions.Where(p => p.Y < bottom + 0.05f).Average(p => Vector3.Dot(p, front));
            Assert.True(topOut > footOut, $"facing {facing}: the torch does not lean away from its wall");
        }
    }

    [Fact]
    public void AWallBlockDropsAndIsPickedAsItsFloorBlock()
    {
        var pairs = 0;
        foreach (var type in BlockType.Types.Values)
        {
            if (type.WallBlockId == 0) continue;
            pairs++;
            var floorLoot = new List<byte>();
            var wallLoot = new List<byte>();
            DropManager.RollLoot(type.Id, new LootContext(0, 0, 0), floorLoot);
            DropManager.RollLoot(type.WallBlockId, new LootContext(0, 0, 0), wallLoot);
            Assert.Equal(floorLoot, wallLoot);
            Assert.Equal(type.Id, BlockType.PickId(type.WallBlockId));
            Assert.Equal(type.Id, BlockType.PickId(type.Id));
        }
        Assert.True(pairs > 0);
    }

    [Fact]
    public void TheRegistryRefusesAFacingOrSupportItCannotHonour()
    {
        const string path = "data/blocks/x.json";
        (bool, bool) Parse(BlockDefinition d, BlockRenderer renderer = BlockRenderer.Model, bool fluid = false,
            bool falls = false, int stackMax = 0, bool needsSupport = true) =>
            ContentRegistry.ParseFacingAndSupport(d, renderer, fluid, falls, stackMax, needsSupport, path);

        var wallTorch = new BlockDefinition { Facing = "horizontal", Support = "attached" };
        Assert.Equal((true, true), Parse(wallTorch));

        // Attached support names the face behind the front, so it needs a facing
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            Parse(new BlockDefinition { Support = "attached" })).Message);
        // Support means nothing on a block that never pops
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            Parse(wallTorch, needsSupport: false)).Message);
        // The facing lives in the state byte, which a fluid, a stack or a falling cube already owns
        var facing = new BlockDefinition { Facing = "horizontal" };
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => Parse(facing, fluid: true)).Message);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => Parse(facing, falls: true)).Message);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() => Parse(facing, stackMax: 4)).Message);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            Parse(facing, renderer: BlockRenderer.Multiblock)).Message);
        Assert.Contains(path, Assert.Throws<InvalidOperationException>(() =>
            Parse(new BlockDefinition { Facing = "up" })).Message);

        // Neither field written: a plain block, below-supported if it needs support at all
        Assert.Equal((false, false), Parse(new BlockDefinition()));
    }

    [Fact]
    public void AWallTorchPopsWithItsWallAndAFloorTorchWithItsGround()
    {
        _world = new SupportTestWorld();
        var wallId = BlockType.ById[_torch]!.WallBlockId;
        var ground = _world.Top;
        var (x, z) = (6, 6);
        _world.Load(x, ground, z);

        // A one-block wall on the plateau, a torch on its east side and one on the ground west of it
        var wall = (X: x, Y: ground + 1, Z: z);
        _world.Set(wall.X, wall.Y, wall.Z, _stone);
        Assert.True(_world.Place(_torch, new Vector3(wall.X, wall.Y, wall.Z), Vector3.UnitX));
        Assert.Equal(wallId, _world.IdAt(x + 1, wall.Y, z));
        Assert.Equal(Vector3.UnitX, Chunk.CubeFaces[_world.StateAt(x + 1, wall.Y, z) & 3].normal);
        Assert.True(_world.Place(_torch, new Vector3(x - 1, ground, z), Vector3.UnitY));
        Assert.Equal(_torch, _world.IdAt(x - 1, wall.Y, z));

        // Under a ceiling: refused, and nothing is spent
        var held = _world.Player.Inventory.CountFor(_torch);
        _world.Player.Inventory.Add(_torch);
        Assert.False(_world.World.TryPlaceSelectedBlockAt(_world.Player.Context, new Vector3(wall.X, wall.Y, wall.Z), -Vector3.UnitY));
        Assert.Equal(held + 1, _world.Player.Inventory.CountFor(_torch));

        // The ground under the wall torch is not what holds it up
        Assert.True(_world.Break(x + 1, ground, z));
        Assert.Equal(wallId, _world.IdAt(x + 1, wall.Y, z));

        // The wall takes its torch down, and the torch on the ground beside it stays
        Assert.True(_world.Break(wall.X, wall.Y, wall.Z));
        Assert.Equal(BlockType.Air, _world.IdAt(x + 1, wall.Y, z));
        Assert.Equal(_torch, _world.IdAt(x - 1, wall.Y, z));

        // ...until its own ground goes
        Assert.True(_world.Break(x - 1, ground, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x - 1, wall.Y, z));
    }

    [Fact]
    public void ABlastPopsTheTorchesItsCraterHeldUpAndNoOthers()
    {
        var wallTorch = BlockType.ById[_torch]!.WallBlockId;
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var origin = center.Position * Chunk.Size;
        void Set(int x, int y, int z, byte id, byte state = 0) => center.SetBlockRaw(x, y, z, id, state);
        byte At(int x, int y, int z) => manager.GetBlockGlobal((int)origin.X + x, (int)origin.Y + y, (int)origin.Z + z);

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            Set(x, FloorY, z, _stone);

        // The blast centre sits on (7, FloorY + 1, 9). Every torch below stands outside its radius,
        // while some of them hang on, or stand on, a block inside it; no cell sits within a few
        // tenths of a block of the radius, so the primed cube's first nudge decides nothing.
        // A wall torch on the east face of a pillar inside the blast, its own floor outside it
        Set(10, FloorY + 1, 8, _stone);
        Set(11, FloorY + 1, 8, wallTorch, 3);
        // A floor torch on a block inside the blast
        Set(7, FloorY + 4, 10, _stone);
        Set(7, FloorY + 5, 10, _torch);
        // A floor torch on ground outside the blast
        Set(11, FloorY + 1, 10, _torch);
        // A wall torch on a wall outside the blast, over a cell the blast takes (facing west, so
        // its wall is the block east of it)
        Set(11, FloorY + 3, 11, _stone);
        Set(10, FloorY + 3, 11, wallTorch, 2);
        Set(10, FloorY + 2, 11, _stone);

        var tnt = new TntManager();
        tnt.Spawn(origin + new Vector3(7, FloorY + 1, 9), ContentRegistry.RequireBlock("tnt").Id, 0f);
        tnt.Update(1e-4f, manager, new LodManager(null!), new DropManager(), new FluidManager(), new FallingBlockManager());

        Assert.Equal(BlockType.Air, At(10, FloorY + 1, 8));
        Assert.Equal(_stone, At(11, FloorY, 8));
        Assert.Equal(BlockType.Air, At(11, FloorY + 1, 8));

        Assert.Equal(BlockType.Air, At(7, FloorY + 4, 10));
        Assert.Equal(BlockType.Air, At(7, FloorY + 5, 10));

        Assert.Equal(_torch, At(11, FloorY + 1, 10));

        Assert.Equal(BlockType.Air, At(10, FloorY + 2, 11));
        Assert.Equal(wallTorch, At(10, FloorY + 3, 11));
    }

    [Fact]
    public void FellingATrunkPopsTheTorchOnTheWoodThatFalls()
    {
        _world = new SupportTestWorld();
        var wallTorch = BlockType.ById[_torch]!.WallBlockId;
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var ground = _world.Top;
        var (x, z) = (4, 10);
        _world.Load(x, ground + 2, z);

        // A three-cell trunk on the plateau: the root, then two cells each parented downward
        const int down = 5;
        _world.Set(x, ground + 1, z, log, WoodState.Encode(WoodState.FullCube, WoodState.ParentRoot, false));
        _world.Set(x, ground + 2, z, log, WoodState.Encode(WoodState.FullCube, down, false));
        _world.Set(x, ground + 3, z, log, WoodState.Encode(WoodState.FullCube, down, false));
        // A torch on the top cell's east side, and one on the root's
        Assert.True(_world.Place(_torch, new Vector3(x, ground + 3, z), Vector3.UnitX));
        Assert.True(_world.Place(_torch, new Vector3(x, ground + 1, z), Vector3.UnitX));
        Assert.Equal(wallTorch, _world.IdAt(x + 1, ground + 3, z));
        Assert.Equal(wallTorch, _world.IdAt(x + 1, ground + 1, z));

        // Cutting the middle fells the top cell, and the torch it held goes with it
        Assert.True(_world.Break(x, ground + 2, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x, ground + 3, z));
        Assert.Equal(BlockType.Air, _world.IdAt(x + 1, ground + 3, z));
        // The root stands, and so does its torch
        Assert.Equal(log, _world.IdAt(x, ground + 1, z));
        Assert.Equal(wallTorch, _world.IdAt(x + 1, ground + 1, z));
    }

    [Fact]
    public void AWallTorchNeedsASolidWallToHangOn()
    {
        _world = new SupportTestWorld();
        var ground = _world.Top;
        var (x, z) = (8, 4);
        _world.Load(x, ground, z);

        // The side of a non-solid block is no wall: a torch aimed at a floor torch's side is refused
        Assert.True(_world.Place(_torch, new Vector3(x, ground, z), Vector3.UnitY));
        Assert.False(_world.Place(_torch, new Vector3(x, ground + 1, z), Vector3.UnitX));
        Assert.Equal(BlockType.Air, _world.IdAt(x + 1, ground + 1, z));
    }
}
