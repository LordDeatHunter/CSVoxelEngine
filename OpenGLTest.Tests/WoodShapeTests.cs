using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class WoodShapeTests
{
    public WoodShapeTests() => TestContent.EnsureLoaded();

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

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
}
