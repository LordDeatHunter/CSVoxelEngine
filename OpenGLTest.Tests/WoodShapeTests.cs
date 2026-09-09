using System.Numerics;
using Silk.NET.Input;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class WoodShapeTests
{
    public WoodShapeTests() => TestContent.EnsureLoaded();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    private static ChunkManager SingleChunk()
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        return manager;
    }

    private static byte[] Neighbours(byte fill = 0, byte? negZ = null, byte? posZ = null,
        byte? negX = null, byte? posX = null, byte? posY = null, byte? negY = null) =>
        [negZ ?? fill, posZ ?? fill, negX ?? fill, posX ?? fill, posY ?? fill, negY ?? fill];

    [Fact]
    public void VerticalStackConnectsOnSameOrAdjacentLevels()
    {
        var log = Block("log");
        var branch = Block("branch");
        var limb = Block("limb");
        var twig = Block("twig");

        Assert.True(WoodShape.ConnectsVertical(log, log));
        Assert.True(WoodShape.ConnectsVertical(branch, branch));
        Assert.True(WoodShape.ConnectsVertical(branch, log));
        Assert.True(WoodShape.ConnectsVertical(limb, branch));
        Assert.False(WoodShape.ConnectsVertical(limb, log));
        Assert.False(WoodShape.ConnectsVertical(twig, log));
        Assert.False(WoodShape.ConnectsVertical(branch, Block("leaves")));
        Assert.False(WoodShape.ConnectsVertical(branch, Block("stone")));
    }

    [Fact]
    public void HorizontalConnectsOnlyOnExactTaperStep()
    {
        var log = Block("log");
        var branch = Block("branch");
        var limb = Block("limb");
        var sprig = Block("sprig");

        Assert.True(WoodShape.ConnectsHorizontal(branch, log));
        Assert.True(WoodShape.ConnectsHorizontal(limb, branch));
        Assert.False(WoodShape.ConnectsHorizontal(branch, branch));
        Assert.False(WoodShape.ConnectsHorizontal(log, log));
        Assert.False(WoodShape.ConnectsHorizontal(sprig, branch));
        Assert.False(WoodShape.ConnectsHorizontal(branch, Block("leaves")));
    }

    [Fact]
    public void IsolatedLimbIsJustItsNode()
    {
        var limb = Block("limb");
        var boxes = WoodShape.GetBoxes(limb, Neighbours());
        var single = Assert.Single(boxes);
        Assert.Equal(new Vector3(0.1875f, 0.1875f, 0.1875f), single.Min);
        Assert.Equal(new Vector3(0.8125f, 0.8125f, 0.8125f), single.Max);
    }

    [Fact]
    public void HorizontalLimbKeepsNodeHeightAndGainsArmBoxes()
    {
        var log = Block("log");
        var branch = Block("branch");
        // Limb (level 2) joined to its branch parent (level 1) on -X: no vertical tubes,
        // so the core stays node-high and the arm reaches the -X face.
        var limb = Block("limb");
        var boxes = WoodShape.GetBoxes(limb, Neighbours(negX: branch, posX: log));
        Assert.Equal(2, boxes.Length);

        var core = boxes[0];
        Assert.Equal(new Vector3(0.1875f, 0.1875f, 0.1875f), core.Min);
        Assert.Equal(new Vector3(0.8125f, 0.8125f, 0.8125f), core.Max);

        var arm = boxes[1];
        Assert.Equal(new Vector3(0f, 0.1875f, 0.1875f), arm.Min);
        Assert.Equal(new Vector3(0.1875f, 0.8125f, 0.8125f), arm.Max);
    }

    [Fact]
    public void SameLevelSideTouchGainsNoArm()
    {
        var limb = Block("limb");
        var boxes = WoodShape.GetBoxes(limb, Neighbours(negX: limb));
        Assert.Single(boxes);
    }

    [Fact]
    public void VerticalRunSpansFullHeight()
    {
        var branch = Block("branch");
        var limb = Block("limb");
        var boxes = WoodShape.GetBoxes(branch, Neighbours(posY: limb, negY: Block("log")));
        var single = Assert.Single(boxes);
        Assert.Equal(new Vector3(0.125f, 0f, 0.125f), single.Min);
        Assert.Equal(new Vector3(0.875f, 1f, 0.875f), single.Max);
    }

    [Fact]
    public void FootGrowsDownOntoGroundButNotOntoForeignWood()
    {
        var branch = Block("branch");
        var sprig = Block("sprig");

        var onGround = WoodShape.GetBoxes(branch, Neighbours(negY: Block("grass")));
        Assert.Equal(0f, Assert.Single(onGround).Min.Y);

        var onForeignWood = WoodShape.GetBoxes(branch, Neighbours(negY: sprig));
        Assert.Equal(0.125f, Assert.Single(onForeignWood).Min.Y);
    }

    [Fact]
    public void LogKeepsItsFullCubeHitbox()
    {
        var log = Block("log");
        var boxes = WoodShape.GetBoxes(log, Neighbours(posX: Block("branch")));
        var single = Assert.Single(boxes);
        Assert.Equal(Vector3.Zero, single.Min);
        Assert.Equal(Vector3.One, single.Max);
    }

    [Fact]
    public void BodyMissingTheNodeDoesNotCollide()
    {
        var manager = SingleChunk();
        manager.Chunks[Vector3.Zero].SetBlockRaw(5, 1, 2, Block("twig"), 0);
        // Isolated twig node: [5.375..5.625] x [1.375..1.625] x [2.375..2.625].
        // The body overlaps the cell but stops short of the node in Z.
        Assert.False(WoodShape.OverlapsShape(Block("twig"), manager.GetBlockGlobal, 5, 1, 2,
            new Vector3(4.9f, 1f, 1.9f), new Vector3(5.5f, 2f, 2.3f)));
        Assert.True(WoodShape.OverlapsShape(Block("twig"), manager.GetBlockGlobal, 5, 1, 2,
            new Vector3(5f, 1f, 2f), new Vector3(5.5f, 2f, 2.7f)));
    }

    [Fact]
    public void BodyHittingOnlyASideArmCollides()
    {
        var manager = SingleChunk();
        var branch = Block("branch");
        manager.Chunks[Vector3.Zero].SetBlockRaw(4, 1, 2, Block("log"), 0);
        manager.Chunks[Vector3.Zero].SetBlockRaw(5, 1, 2, branch, 0);
        // The -X arm spans x [5.0..5.125] while the core starts at 5.125.
        Assert.True(WoodShape.OverlapsShape(branch, manager.GetBlockGlobal, 5, 1, 2,
            new Vector3(4.7f, 1.2f, 2.4f), new Vector3(5.05f, 1.8f, 2.6f)));
        Assert.False(WoodShape.OverlapsShape(branch, manager.GetBlockGlobal, 5, 1, 2,
            new Vector3(4.7f, 1.2f, 2.4f), new Vector3(4.95f, 1.8f, 2.6f)));
    }

    [Fact]
    public void SolidCubeCollisionStaysFullCell()
    {
        var manager = SingleChunk();
        var stone = Block("stone");
        manager.Chunks[Vector3.Zero].SetBlockRaw(5, 1, 2, stone, 0);
        Assert.True(WoodShape.OverlapsShape(stone, manager.GetBlockGlobal, 5, 1, 2,
            new Vector3(5.9f, 1.9f, 2.9f), new Vector3(6.5f, 2.5f, 3.5f)));
        Assert.False(WoodShape.OverlapsShape(stone, manager.GetBlockGlobal, 5, 1, 2,
            new Vector3(6.1f, 1.9f, 2.9f), new Vector3(6.5f, 2.5f, 3.5f)));
    }

    [Fact]
    public void PlayerWalksPastLimbCornerButIsStoppedByNode()
    {
        var manager = SingleChunk();
        var stone = Block("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            manager.Chunks[Vector3.Zero].SetBlockRaw(x, 0, z, stone, 0);
        manager.Chunks[Vector3.Zero].SetBlockRaw(5, 1, 2, Block("twig"), 0);
        var keyboard = new ScriptedKeyboard();
        keyboard.Press([Key.W]);

        // Lane through the node: the walk ends against it.
        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(3f, 2.62f, 2.5f), yaw: 0f, pitch: 0f);
        for (var i = 0; i < 60; i++) camera.Update(1f / 60f, keyboard, manager);
        Assert.True(camera.Position.X > 4.5f);
        Assert.True(camera.Position.X < 5.3f);

        // Lane through the cell corner missing the node: the walk passes through.
        camera.TestTeleport(new Vector3(3f, 2.62f, 2f), yaw: 0f, pitch: 0f);
        for (var i = 0; i < 60; i++) camera.Update(1f / 60f, keyboard, manager);
        Assert.True(camera.Position.X > 6f);
    }
}
