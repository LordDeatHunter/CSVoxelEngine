using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Simulation;
using VoxelEngine.WorldGen.Trees;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>A felled limb comes to rest on its real underside, whatever its thickness.</summary>
public sealed class FellBodyTests
{
    public FellBodyTests() => TestContent.EnsureLoaded();

    private static float RestHeightOfCenter(int thickness)
    {
        var log = ContentRegistry.RequireBlock("oak_log").Id;
        var blocks = new List<TopplingTree.FellBlock>();
        for (var i = 0; i < 4; i++)
            blocks.Add(new TopplingTree.FellBlock(new Vector3(4 + i, 20, 4), log,
                WoodState.Encode(thickness, i == 0 ? WoodState.ParentRoot : 2, false)));
        var centers = blocks.Select(b => b.Cell + new Vector3(0.5f)).ToList();
        var body = new FellBody(centers, centers.Select(_ => 1f).ToList(), FellContacts.Build(blocks),
            new Vector3(4.5f, 20f, 4.5f), Vector3.UnitZ, hinged: false, isSolid: (_, y, _) => y < 10);

        for (var i = 0; i < 60 * 8 && !body.AtRest; i++)
            body.Advance(1f / 60f);
        Assert.True(body.AtRest);
        return body.PointNow(1).Y;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(15)]
    public void ALimbSettlesWithItsOwnUndersideOnTheGround(int thickness)
    {
        var half = (thickness + 1) / 32f;
        // The block's center sits half its diameter above the ground (top face y = 10), within the contact skin
        Assert.Equal(10f + half, RestHeightOfCenter(thickness), 0.08f);
    }
}
